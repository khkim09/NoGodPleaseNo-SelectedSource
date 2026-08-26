using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.AI;
using FishNet;
using FishNet.Object;
using NGPN.Core;
using NGPN.Combat;
using System;

/// <summary>
/// 타겟 탐지/선정
/// </summary>
namespace NGPN.Gameplay
{
    public class MonsterDetector : NetworkBehaviour, IDeathCleanable
    {
        [Header("Detection")]
        public float detectionRange;
        [SerializeField] LayerMask damageMask; // 공격 가능한 대상만 감지
        [SerializeField] float loseMultiplier = 5f; // 현재 타겟이 멀어지면 타겟을 재설정할 때 사용할 계수

        private bool isCaptured = false;
        private bool isLaunched = false;

        private bool _paused;
        public bool Paused => _paused;

        // 내부 버퍼
        readonly Collider[] _hits = new Collider[64];

        // 여신상의 위치 정보
        private Transform GoddessStatuePos;

        public Transform PrimaryTarget => CurrentTarget != null ? CurrentTarget : GoddessStatuePos;

        [Header("Blacklist")]
        [SerializeField] private float blacklistDuration = 3f;
        private readonly Dictionary<Transform, float> _blacklist = new();
        private static readonly List<Transform> _expiredBlacklistEntries = new();

        [Header("Taunt")]
        [SerializeField] private GameObject tauntEffectObject;
        private bool _tauntVisualOn;

        // Per-monster taunt state
        /// <summary>이 몬스터를 taunt한 탱커</summary>
        private NetworkObject _tauntOwnerNo;
        /// <summary>TankerUltimate가 발급한 taunt 세션 id</summary>
        private uint _tauntId;
        /// <summary>만료 시각</summary>
        private float _tauntUntil;

        [Header("Taunt Path Check")]
        [Tooltip("NavMesh.SamplePosition이 '아래 바닥'을 잡아 PathComplete가 뜨는 오판 방지용. 샘플 포인트 y와 실제 타겟 y 차이가 이 값보다 크면 PathOpen=false 처리.")]
        [SerializeField] private float tauntMaxVerticalDelta = 1.25f;

        [Header("Test Settings")]
        [SerializeField, ReadOnly] private Transform debugCT;

        public Transform CurrentTarget { get; private set; } // 현재 설정된 타겟
        public IDamageable CurrentTargetDamageable { get; private set; } // 현재 설정된 타겟의 IDamageable 스크립트(캐싱용)

        /// <summary>Sub Target - Main 도달 불가 시 가장 가까운 구조물</summary>
        private Transform _subTarget;
        private IDamageable _subTargetDamageable;

        /// <summary>Main Target까지 경로 Open 여부</summary>
        private bool _isPathToMainOpen;
        /// <summary>경로 상태</summary>
        public bool IsPathToMainOpen => _isPathToMainOpen;
        /// <summary>현재 SubTarget</summary>
        public Transform SubTarget => _subTarget;

        [Header("Gizmo")]
        [SerializeField] bool debugGizmos = true;
        [SerializeField] Color gizmoFill = new(0f, 1f, 1f, 0.08f);
        [SerializeField] Color gizmoWire = new(0f, 0.8f, 1f, 0.95f);

        #region Taunt API

        /// <summary>
        /// 실제 행동 대상
        /// - PathOpen = true: MainTarget
        /// - PathOpen = false: SubTarget (있으면) or MainTarget (없으면)
        /// </summary>
        public Transform GetActionTarget()
        {
            if (!IsTauntActive_Server())
                return PrimaryTarget;

            // Taunt 활성
            if (_isPathToMainOpen)
                return PrimaryTarget; // 경로 열림 → Main

            // 경로 닫힘 → Sub 우선, 없으면 Main
            return _subTarget != null ? _subTarget : PrimaryTarget;
        }

        public IDamageable GetActionTargetDamageable()
        {
            if (!IsTauntActive_Server())
                return CurrentTargetDamageable;

            if (_isPathToMainOpen)
                return CurrentTargetDamageable;

            return _subTargetDamageable != null ? _subTargetDamageable : CurrentTargetDamageable;
        }

        [Server]
        public bool IsTauntForcedTarget_Server(Transform t)
        {
            if (t == null) return false;
            if (!IsTauntActive_Server()) return false;
            return _tauntOwnerNo != null && _tauntOwnerNo.transform == t;
        }

        [Server]
        private bool IsTauntActive_Server()
        {
            if (_tauntOwnerNo == null) return false;
            if (InstanceFinder.TimeManager == null) return false;
            if (InstanceFinder.TimeManager.ServerUptime >= _tauntUntil) return false;
            return _tauntOwnerNo.IsSpawned;
        }

        /// <summary>taunt 시각 효과 on/off</summary>
        [Server]
        private void SetTauntVisual_Server(bool enabled)
        {
            if (tauntEffectObject == null) return;
            if (_tauntVisualOn == enabled) return;
            _tauntVisualOn = enabled;
            RpcSetTauntVisual(enabled);
        }

        [ObserversRpc(BufferLast = true)]
        private void RpcSetTauntVisual(bool enabled)
        {
            if (tauntEffectObject == null) return;
            tauntEffectObject.SetActive(enabled);
        }

        /// <summary>이미 필드에 스폰된 몬스터에 taunt 적용 (session + tauntId)</summary>
        [Server]
        public void ApplyTaunt_Server(NetworkObject ownerNo, uint tauntId, float durationSec, bool interruptAttack)
        {
            if (ownerNo == null) return;
            if (InstanceFinder.TimeManager == null) return;

            _tauntOwnerNo = ownerNo;
            _tauntId = tauntId;
            _tauntUntil = InstanceFinder.TimeManager.ServerUptime + Mathf.Max(0.05f, durationSec);

            // 즉시 리타겟
            ForceRetargetNow_Server(ownerNo.transform, interruptAttack);
            SetTauntVisual_Server(true);
        }

        /// <summary>owner/tauntId가 일치할 때만 taunt 해제.</summary>
        [Server]
        public void ClearTauntIfMatches_Server(NetworkObject ownerNo, uint tauntId, bool resetTarget)
        {
            if (_tauntOwnerNo == null) return;
            if (ownerNo == null) return;
            if (_tauntOwnerNo != ownerNo) return;
            if (_tauntId != tauntId) return;

            ClearTauntInternal_Server(resetTarget);
        }

        [Server]
        private void ClearTauntInternal_Server(bool resetTarget)
        {
            _tauntOwnerNo = null;
            _tauntId = 0;
            _tauntUntil = 0f;

            // 경로/SubTarget 초기화
            _isPathToMainOpen = false;
            _subTarget = null;
            _subTargetDamageable = null;

            SetTauntVisual_Server(false);

            if (resetTarget)
                SetServerTarget(null);
        }

        /// <summary>taunt owner 가져오기</summary>
        [Server]
        public bool TryGetTauntOwner_Server(out Transform owner)
        {
            owner = null;
            if (!IsTauntActive_Server()) return false;
            if (_tauntOwnerNo == null || !_tauntOwnerNo.IsSpawned) return false;
            owner = _tauntOwnerNo.transform;
            return owner != null;
        }

        /// <summary>탱커 Ult 쪽 센서에서 SubTarget(Structures) 가져오기</summary>
        [Server]
        private bool TryGetTauntOwnerSensorStructure_Server(Transform tauntOwner, out Transform buildingTarget)
        {
            buildingTarget = null;
            if (tauntOwner == null) return false;

            TankerUltimateShout shout = tauntOwner.GetComponentInParent<TankerUltimateShout>();
            if (shout == null || !shout.IsServerInitialized) return false;

            return shout.TryGetBestTauntStructure_Server(out buildingTarget);
        }

        /// <summary>
        /// Taunt 대상(탱커)이 NavMesh로 도달 불가일 때,
        /// 탱커의 수직 아래에서 Building을 찾아 임시 공격 타겟으로 반환한다.
        /// Building을 못 찾으면 false 반환.
        /// </summary>
        [Server]
        public bool TryGetTauntBuildingTarget_Server(
            Transform tauntOwner,
            NavMeshAgent agent,
            out Transform buildingTarget)
        {
            buildingTarget = null;

            if (tauntOwner == null || agent == null) return false;

            // 탱커의 수직 아래로 Raycast - Building 탐지
            Vector3 rayOrigin = tauntOwner.position + Vector3.up * 0.25f;
            float rayLen = 15f; // 충분한 길이로 아래 탐색

            RaycastHit[] hits = Physics.RaycastAll(
                rayOrigin,
                Vector3.down,
                rayLen,
                damageMask,
                QueryTriggerInteraction.Collide);

            if (hits == null || hits.Length == 0)
                return false;

            // 가까운 것부터 처리 (발 아래 표면 우선)
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            for (int i = 0; i < hits.Length; i++)
            {
                Collider col = hits[i].collider;
                if (col == null) continue;

                // 탱커 자신의 콜라이더는 스킵
                if (col.transform.root == tauntOwner.root)
                    continue;

                IDamageable d = col.GetComponentInParent<IDamageable>();
                if (d == null || !d.isAlive) continue;

                Team team = d.GetTeam();

                // Building(Structures)인 경우만 선택
                if (team != Team.Structures) continue;

                Transform t = d.GetTransform();
                if (t == null) continue;

                // 이 건물까지 NavMesh로 도달 가능한지 검사
                Vector3 targetPos = t.position;
                targetPos.y = agent.transform.position.y;

                if (!NavMesh.SamplePosition(targetPos, out NavMeshHit targetHit, 6f, agent.areaMask))
                    continue;

                // 실제 경로 검증
                NavMeshPath path = new NavMeshPath();
                if (!NavMesh.CalculatePath(agent.transform.position, targetHit.position, agent.areaMask, path))
                    continue;

                if (path.status != NavMeshPathStatus.PathComplete)
                    continue;

                // 도달 가능한 건물 발견
                buildingTarget = t;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 경로 상태 및 SubTarget 갱신 (매 Tick)
        /// </summary>
        [Server]
        private void UpdatePathAndSubTarget_Server(Transform mainTarget)
        {
            if (mainTarget == null)
            {
                _isPathToMainOpen = false;
                _subTarget = null;
                _subTargetDamageable = null;
                return;
            }

            NavMeshAgent agent = GetComponent<NavMeshAgent>();
            if (agent == null || !agent.isOnNavMesh)
            {
                _isPathToMainOpen = false;
                _subTarget = null;
                _subTargetDamageable = null;
                return;
            }

            // === Step 1: Main까지 경로 Check (오판 방지 포함) ===
            Vector3 rawMainPos = mainTarget.position; // (중요) y를 agent.y로 덮어쓰면 오판이 발생함

            if (!NavMesh.SamplePosition(rawMainPos, out NavMeshHit mainHit, 6f, agent.areaMask))
            {
                _isPathToMainOpen = false;
            }
            else
            {
                // (핵심) SamplePosition이 아래 바닥을 잡아도 성공하는 케이스 방지
                float dy = Mathf.Abs(mainHit.position.y - rawMainPos.y);
                if (dy > tauntMaxVerticalDelta)
                {
                    _isPathToMainOpen = false;
                }
                else
                {
                    NavMeshPath path = new NavMeshPath();
                    bool calculated = NavMesh.CalculatePath(agent.transform.position, mainHit.position, agent.areaMask, path);
                    _isPathToMainOpen = calculated && (path.status == NavMeshPathStatus.PathComplete);
                }
            }

            // === Step 2: Path Open → SubTarget 초기화 ===
            if (_isPathToMainOpen)
            {
                _subTarget = null;
                _subTargetDamageable = null;
                return;
            }

            // === Step 3: Path Closed → SubTarget 갱신 ===
            // 우선순위:
            // 1) 탱커 궁극기 센서(Trigger Sphere + 발 아래 Ray)에서 제공하는 구조물
            // 2) 기존 Detector의 발 아래 Raycast + NavMesh 검증

            Transform building = null;

            if (!TryGetTauntOwnerSensorStructure_Server(mainTarget, out building))
            {
                // 센서가 못 잡으면 기존 방식 fallback
                TryGetTauntBuildingTarget_Server(mainTarget, agent, out building);
            }

            if (building != null)
            {
                _subTarget = building;
                _subTargetDamageable = building.GetComponentInParent<IDamageable>();
            }
            else
            {
                _subTarget = null;
                _subTargetDamageable = null;
            }

        }

        #endregion

        /// <summary>탐지 범위 초기화</summary>
        [Server]
        public void Initialize(float detectRange) => detectionRange = detectRange;

        /// <summary>여신상 위치 고정</summary>
        public void InjectGoddessStatuePos(Transform t) => GoddessStatuePos = t;

        public override void OnStartServer()
        {
            base.OnStartServer();
            InstanceFinder.TimeManager.OnTick += ServerTick_OnTick;
        }

        public override void OnStopServer()
        {
            if (InstanceFinder.TimeManager != null)
                InstanceFinder.TimeManager.OnTick -= ServerTick_OnTick;

            // 풀 복귀/디스폰 시 taunt/타겟 잔존 방지
            if (_tauntOwnerNo != null)
                ClearTauntInternal_Server(resetTarget: true);
            else
                SetTauntVisual_Server(false);

            base.OnStopServer();
        }

        private void OnDisable()
        {
            // 클라, 서버 무관하게 pool 진입 시 즉시 taunt 해제
            if (tauntEffectObject != null)
                tauntEffectObject.SetActive(false);

            // 풀링으로 GameObject만 비활성화되는 케이스 방어(디스폰 훅이 누락될 수 있음)
            if (!IsServerInitialized) return;

            if (_tauntOwnerNo != null)
                ClearTauntInternal_Server(resetTarget: true);
            else
                SetTauntVisual_Server(false);

            // 다음 스폰에서 정상적으로 재탐지하도록 타겟도 비움
            SetServerTarget(null);
        }

        private void ServerTick_OnTick()
        {
            if (!IsServerInitialized || isLaunched || isCaptured) return;
            ServerTick();
        }

        [Server]
        private void ServerTick()
        {
            TryLateBindGoddess();
            CleanupBlacklist();

            if (_paused)
            {
                if (CurrentTarget != null) SetServerTarget(null);
                SetTauntVisual_Server(false);
                return;
            }

            // taunt는 "궁극기 사용 시점에 이미 스폰된 몬스터"에만 ApplyTaunt_Server가 호출되며,
            // 그 몬스터만 duration 동안 탱커를 강제 타겟한다.
            if (IsTauntActive_Server())
            {
                Transform ownerT = _tauntOwnerNo != null ? _tauntOwnerNo.transform : null;
                IDamageable ownerDmg = ownerT != null ? ownerT.GetComponentInParent<IDamageable>() : null;

                // 탱커가 죽었거나(=isAlive false) 스폰이 내려간 경우 즉시 해제
                if (ownerT == null || ownerDmg == null || !ownerDmg.isAlive)
                {
                    ClearTauntInternal_Server(resetTarget: true);
                }
                else
                {
                    SetServerTarget(ownerT); // Main = Tanker (불변)

                    // 경로 상태 및 SubTarget 갱신
                    UpdatePathAndSubTarget_Server(ownerT);

                    SetTauntVisual_Server(true);
                    return;
                }
            }
            else
            {
                // 시간이 만료됐으면 즉시 해제(타겟/이펙트 리셋)
                if (_tauntOwnerNo != null)
                    ClearTauntInternal_Server(resetTarget: true);
            }

            // 현재 타겟 유효성 검사(죽음/이탈)
            if (CurrentTarget)
            {
                if (!IsTargetValid(CurrentTargetDamageable)
                    || OutOfRange(CurrentTarget.position, detectionRange * loseMultiplier))
                    SetServerTarget(null);
            }

            Transform best = Server_FindBestTarget();
            if (best != null)
            {
                SetServerTarget(best);
            }
            else
            {
                if (CurrentTarget == null && GoddessStatuePos != null)
                    SetServerTarget(GoddessStatuePos);
                else
                    SetServerTarget(null);
            }
        }

        /// <summary>블랙리스트 만료 처리</summary>
        [Server]
        private void CleanupBlacklist()
        {
            if (_blacklist.Count == 0) return;

            _expiredBlacklistEntries.Clear();
            float now = TimeManager.ServerUptime;

            foreach (KeyValuePair<Transform, float> kvp in _blacklist)
            {
                if (now >= kvp.Value) _expiredBlacklistEntries.Add(kvp.Key);
            }

            for (int i = 0; i < _expiredBlacklistEntries.Count; i++)
            {
                _blacklist.Remove(_expiredBlacklistEntries[i]);
            }
            _expiredBlacklistEntries.Clear();
        }

        [Server]
        private bool OutOfRange(Vector3 pos, float range)
        {
            return (pos - transform.position).sqrMagnitude > (range * range);
        }

        [Server]
        private void TryLateBindGoddess()
        {
            if (GoddessStatuePos != null) return;
            GameObject statue = GameObject.FindWithTag("Goddess");
            if (statue != null) GoddessStatuePos = statue.transform;
        }

        /// <summary>스폰 직후 ‘최소 여신상’을 타겟으로 프라임</summary>
        [Server]
        public void PrimeInitialTarget_Server()
        {
            TryLateBindGoddess();
            if (CurrentTarget == null && GoddessStatuePos != null)
                SetServerTarget(GoddessStatuePos);
        }

        [Server]
        public void AddToBlacklist(Transform t)
        {
            if (t == null) return;
            _blacklist[t] = TimeManager.ServerUptime + blacklistDuration;
        }

        /// <summary>
        /// 타겟 탐지 (우선 순위 : 플레이어 > 탱커 > 여신상 > 건물)
        /// </summary>
        [Server]
        private Transform Server_FindBestTarget()
        {
            Vector3 selfPos = transform.position;
            int count = Physics.OverlapSphereNonAlloc(selfPos, detectionRange, _hits, damageMask, QueryTriggerInteraction.Collide);

            Transform bestPlayer = null; float bestPlayerSqr = float.PositiveInfinity;
            Transform bestLowPrioPlayer = null; float bestLowPrioSqr = float.PositiveInfinity;
            Transform bestGoddess = null; float bestGoddessSqr = float.PositiveInfinity;
            Transform bestBuilding = null; float bestBuildingSqr = float.PositiveInfinity;

            for (int i = 0; i < count; i++)
            {
                Collider col = _hits[i];
                if (col == null) continue;

                // 자기 자신 제외
                if (col.transform.root == transform.root) continue;

                // Damage 입는 대상인지?
                IDamageable detectedObj = col.GetComponentInParent<IDamageable>();
                if (detectedObj == null) continue;

                // 해당 대상이 죽은 상태라면 스킵
                if (!IsTargetValid(detectedObj)) continue;

                // target까지의 거리 측정
                Transform t = detectedObj.GetTransform();
                if (t == null) continue;

                // 도달 불가 판정 - 블랙리스트 스킵
                if (_blacklist.ContainsKey(t)) continue;

                float d2 = (t.position - selfPos).sqrMagnitude;

                switch (detectedObj.GetTeam())
                {
                    case Team.Players: // 플레이어 우선 탐지
                        // 탱커의 리버스 어그로
                        bool isLow = t.CompareTag("LowPriorityTarget") || t.root.CompareTag("LowPriorityTarget");

                        if (isLow)
                        {
                            if (d2 < bestLowPrioSqr) { bestLowPrioSqr = d2; bestLowPrioPlayer = t; }
                        }
                        else
                        {
                            if (d2 < bestPlayerSqr) { bestPlayerSqr = d2; bestPlayer = t; }
                        }
                        break;

                    case Team.GoddessStatue:
                        if (d2 < bestGoddessSqr) { bestGoddessSqr = d2; bestGoddess = t; }
                        break;

                    case Team.Structures:
                        if (d2 < bestBuildingSqr) { bestBuildingSqr = d2; bestBuilding = t; }
                        break;
                }
            }

            // 우선순위 결정: 플레이어 > 탱커 > 여신상 > 건물
            Transform bestTarget = bestPlayer ?? bestLowPrioPlayer ?? bestGoddess ?? bestBuilding;
            return bestTarget;
        }

        /// <summary>서버에서 타겟을 확정하고(또는 해제) 타겟 변경 이벤트 콜백</summary>
        [Server]
        private void SetServerTarget(Transform t)
        {
            if (t == null)
            {
                CurrentTarget = null;
                CurrentTargetDamageable = null;
                debugCT = null;
                return;
            }

            // 같은 타겟인 경우
            if (CurrentTarget == t) return;

            IDamageable d = t.GetComponentInParent<IDamageable>();
            if (d == null || !d.isAlive)
            {
                CurrentTarget = null;
                CurrentTargetDamageable = null;
                debugCT = null;
                return;
            }

            CurrentTarget = t;
            CurrentTargetDamageable = d;
            debugCT = t;
        }

        /// <summary>타겟 해제(공개 API)</summary>
        [Server]
        public void ClearTargetServer() => SetServerTarget(null);

        /// <summary>탐지 일시정지/해제(공개 API)</summary>
        /// <param name="pause"></param>
        [Server]
        public void PauseDetection(bool pause)
        {
            _paused = pause;
            if (pause) SetServerTarget(null); // 일시정지 진입 시, 현재 타겟을 즉시 비움
        }

        // === 타겟 생사 여부 검사 ===
        private bool IsTargetValid(IDamageable d) => d != null && d.isAlive;

        // 해적에 의한 장전당함 여부 적용
        [Server]
        public void ApplyCaptured(bool captured)
        {
            isCaptured = captured;
            if (captured) SetServerTarget(null);
        }

        // 발사 여부 적용
        [Server]
        public void ApplyLaunched(bool launched) => isLaunched = launched;

        #region ForcedTarget Helpers

        /// <summary>
        /// 강제 리타겟(외침 발동 즉시 호출용)
        /// - interruptAttack=true면 공격/스킬/CTS를 안전하게 중단
        /// </summary>
        [Server]
        public void ForceRetargetNow_Server(Transform target, bool interruptAttack)
        {
            if (target == null) return;

            if (interruptAttack)
            {
                // 공격/스킬 컴포넌트 안전 중단
                IForceRetargetHandler[] handlers = GetComponents<IForceRetargetHandler>();
                for (int i = 0; i < handlers.Length; i++)
                    handlers[i]?.ForceStopForRetarget_Server();

                // 이동도 즉시 추격 전환하도록 정리
                if (TryGetComponent(out MonsterMovement mv))
                    mv.Stop();
            }

            SetServerTarget(target);
        }

        #endregion

        #region IDeathCleanable

        public void CleanUpOnDeath_Server()
        {
            if (!IsServerInitialized) return;

            if (_tauntOwnerNo != null)
                ClearTauntInternal_Server(resetTarget: true);
            else
                SetTauntVisual_Server(false);

            // 사망 시 타겟도 비워서 다음 스폰/풀링 복귀 시 정상 탐지로 시작
            SetServerTarget(null);

            _blacklist.Clear();

            isCaptured = false;
            isLaunched = false;
        }

        #endregion

        // === Gizmos ===
        void OnDrawGizmos()
        {
            if (!debugGizmos) return;
            DrawDetectorGizmos();
        }

        void DrawDetectorGizmos()
        {
            Vector3 pos = transform.position;

            Gizmos.color = gizmoFill;
            Gizmos.DrawSphere(pos, detectionRange);

#if UNITY_EDITOR
            UnityEditor.Handles.color = gizmoWire;
            // XZ 평면의 원(디스크) – 위에서 보면 반경이 정확히 보임
            UnityEditor.Handles.DrawWireDisc(pos, Vector3.up, detectionRange);
#endif
        }
    }
}
