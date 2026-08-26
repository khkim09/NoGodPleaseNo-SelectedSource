using System;
using System.Threading;
using UnityEngine;
using FishNet.Object;
using Cysharp.Threading.Tasks;
using NGPN.Combat;
using System.Collections.Generic;
using NGPN.Core;

namespace NGPN.Gameplay
{
    /// <summary>탱커 궁극기(외침): 10초간 모든 몬스터 타겟을 본인으로 강제</summary>
    [DisallowMultipleComponent]
    public class TankerUltimateShout : NetworkBehaviour, IUltimateAbility, IDeathCleanable, IRespawnable
    {
        [Header("Settings")]
        /// <summary>궁극기 지속 시간(초)</summary>
        [SerializeField] private float durationSec = 10f;

        [Header("Refs")]
        /// <summary>궁극기 차징 컴포넌트 참조</summary>
        [SerializeField] private UltimateCharge ultimateCharge;
        /// <summary>탱커 패널티 컴포넌트 참조(궁극기 중 무효화)</summary>
        [SerializeField] private TankerPenalty tankerPenalty;
        /// <summary>체력(죽으면 즉시 궁극기 종료 처리용)</summary>
        [SerializeField] private CharacterHealth health;

        [Header("Anim/Attack Ref")]
        [SerializeField] private TankerAttack tankerAttack;

        [Header("Taunt SubTarget Query (Structures)")]
        [SerializeField] private float structureSensorRadius = 2.2f;
        [SerializeField] private float structureDownRayLength = 15f;
        [SerializeField] private LayerMask structureRayMask = ~0;

        // OverlapSphereNonAlloc 버퍼(alloc 방지)
        private readonly Collider[] _structureHits = new Collider[64];

        /// <summary>현재 궁극기 활성 여부(서버 전용)</summary>
        private bool _active;
        /// <summary>궁극기 지속 작업 취소용 CTS</summary>
        private CancellationTokenSource _activeCts;

        // Multi-tanker safe taunt session
        // - 탱커마다 taunt 세션 id를 발급하고, 그 세션에서 taunt된 몬스터만 추적/해제한다.
        private static uint s_nextTauntId = 1;
        private uint _tauntId;
        private readonly List<MonsterDetector> _tauntedDetectors = new(64);
        private readonly List<TrainingDummy> _tauntedDummies = new(16);

        private void Awake()
        {
            if (!ultimateCharge) ultimateCharge = GetComponent<UltimateCharge>();
            if (!tankerPenalty) tankerPenalty = GetComponent<TankerPenalty>();
            if (!health) health = GetComponent<CharacterHealth>();
        }

        public override void OnStopServer()
        {
            // 서버에서 오브젝트가 내려갈 때 전역 타운트가 남지 않게 정리
            if (_active) Deactivate_Server();
            else
            {
                // _active=false인데 리스트/세션이 남아있는 예외 케이스 방어
                for (int i = 0; i < _tauntedDetectors.Count; i++)
                {
                    MonsterDetector d = _tauntedDetectors[i];
                    if (d == null) continue;
                    d.ClearTauntIfMatches_Server(base.NetworkObject, _tauntId, resetTarget: true);
                }
                _tauntedDetectors.Clear();

                for (int i = 0; i < _tauntedDummies.Count; i++)
                {
                    TrainingDummy dum = _tauntedDummies[i];
                    if (dum == null) continue;
                    dum.ClearTauntVisualIfMatches_Server(base.NetworkObject, _tauntId);
                }
                _tauntedDummies.Clear();

                _tauntId = 0;
            }

            base.OnStopServer();
        }

        #region Subtarget Finding

        /// <summary>
        /// (업계식) Taunt 중 SubTarget을 제공:
        /// 1) Ult 센서(trigger) 안에 들어온 Structures 중 탱커와 가장 가까운 것
        /// 2) 실패 시 탱커 발 아래 RaycastAll로 Structures 탐색(자기 자신 콜라이더 스킵)
        /// </summary>
        [Server]
        public bool TryGetBestTauntStructure_Server(out Transform structure)
        {
            structure = null;
            if (!IsServerInitialized) return false;

            // 1) 탱커 주변 구조물 후보 수집 (OverlapSphereNonAlloc)
            int count = Physics.OverlapSphereNonAlloc(
                transform.position,
                structureSensorRadius,
                _structureHits,
                structureRayMask,
                QueryTriggerInteraction.Collide);

            float best = float.MaxValue;
            Transform bestT = null;

            for (int i = 0; i < count; i++)
            {
                Collider col = _structureHits[i];
                if (col == null) continue;

                // 자기 자신 스킵
                if (col.transform.root == transform.root) continue;

                IDamageable dmg = col.GetComponentInParent<IDamageable>();
                if (dmg == null || !dmg.isAlive) continue;
                if (dmg.GetTeam() != Team.Structures) continue;

                Transform t = dmg.GetTransform();
                if (t == null) continue;

                float d = (t.position - transform.position).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    bestT = t;
                }
            }

            if (bestT != null)
            {
                structure = bestT;
                return true;
            }

            // 2) 못 찾으면 탱커 발 아래 Raycast fallback (기존 유지)
            Vector3 rayOrigin = transform.position + Vector3.up * 0.25f;

            RaycastHit[] hits = Physics.RaycastAll(
                rayOrigin,
                Vector3.down,
                structureDownRayLength,
                structureRayMask,
                QueryTriggerInteraction.Collide);

            if (hits == null || hits.Length == 0)
                return false;

            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            for (int i = 0; i < hits.Length; i++)
            {
                Collider col = hits[i].collider;
                if (col == null) continue;

                // 자기 자신 스킵
                if (col.transform.root == transform.root) continue;

                IDamageable dmg = col.GetComponentInParent<IDamageable>();
                if (dmg == null || !dmg.isAlive) continue;
                if (dmg.GetTeam() != Team.Structures) continue;

                Transform t = dmg.GetTransform();
                if (t == null) continue;

                structure = t;
                return true;
            }

            return false;
        }

        #endregion

        /// <summary>궁극기 실행 (외침)</summary>
        public float ExecuteUltimate_Server()
        {
            if (!IsServerInitialized) return 0f;
            if (_active) return 0f;
            if (health && !health.isAlive) return 0f;
            if (ultimateCharge == null) return 0f;

            Activate_Server();
            return durationSec;
        }

        /// <summary>탱커 궁극기 실행(외침 시작)</summary>
        [Server]
        private void Activate_Server()
        {
            if (_active) return;

            // 새 taunt 세션 시작(동시 탱커 궁극기 중첩 대비)
            _tauntId = ++s_nextTauntId;
            if (_tauntId == 0) _tauntId = ++s_nextTauntId; // wrap 방어
            _tauntedDetectors.Clear();

            // 1) 궁극기 애니메이션 시작
            if (tankerAttack != null)
                tankerAttack.BeginUltimateAnimation_Server();

            // 2) 10초 동안 탱커 패널티 무효화
            if (tankerPenalty != null)
                tankerPenalty.SetSuppressedByUltimate_Server(true);

            // 3) "궁극기 사용 시점"에 이미 필드에 스폰된 몬스터만 taunt 적용
            MonsterDetector[] detectors = FindObjectsByType<MonsterDetector>(FindObjectsSortMode.None);
            for (int i = 0; i < detectors.Length; i++)
            {
                MonsterDetector d = detectors[i];
                if (d == null || !d.IsServerInitialized) continue;

                d.ApplyTaunt_Server(base.NetworkObject, _tauntId, durationSec, interruptAttack: true);
                _tauntedDetectors.Add(d);
            }

            // 3-1) Dummy(훈련 허수아비)에도 taunt visual 적용
            TrainingDummy[] dummies = FindObjectsByType<TrainingDummy>(FindObjectsSortMode.None);
            _tauntedDummies.Clear();
            for (int i = 0; i < dummies.Length; i++)
            {
                TrainingDummy dum = dummies[i];
                if (dum == null || !dum.IsServerInitialized) continue;

                dum.ApplyTauntVisual_Server(base.NetworkObject, _tauntId, durationSec);
                _tauntedDummies.Add(dum);
            }

            _active = true;

            // 4) 10초 지속(중간 사망 시 즉시 해제)
            _activeCts?.Cancel();
            _activeCts?.Dispose();
            _activeCts = new CancellationTokenSource();
            RunDuration_Server(_activeCts.Token).Forget();
        }

        /// <summary>일정 시간 후 궁극기 효과 종료 (사망 시 조기 종료)</summary>
        [Server]
        private async UniTaskVoid RunDuration_Server(CancellationToken ct)
        {
            float endAt = TimeManager.ServerUptime + durationSec;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (!_active) return;

                    // 죽으면 즉시 종료
                    if (health != null && !health.isAlive)
                        break;

                    if (TimeManager.ServerUptime >= endAt)
                        break;

                    await UniTask.Delay(TimeSpan.FromMilliseconds(100), cancellationToken: ct);
                }
            }
            catch
            {
                return;
            }

            Deactivate_Server();
        }

        /// <summary>궁극기 효과 종료(외침 해제, 패널티 복귀)</summary>
        [Server]
        private void Deactivate_Server()
        {
            if (!_active) return;
            _active = false;

            _activeCts?.Cancel();
            _activeCts?.Dispose();
            _activeCts = null;

            // 내가 taunt한 몬스터만 해제 (다른 탱커 궁극기와 겹칠 수 있음)
            for (int i = 0; i < _tauntedDetectors.Count; i++)
            {
                MonsterDetector d = _tauntedDetectors[i];
                if (d == null) continue;
                d.ClearTauntIfMatches_Server(base.NetworkObject, _tauntId, resetTarget: true);
            }
            _tauntedDetectors.Clear();

            // Dummy taunt visual 해제
            for (int i = 0; i < _tauntedDummies.Count; i++)
            {
                TrainingDummy dum = _tauntedDummies[i];
                if (dum == null) continue;
                dum.ClearTauntVisualIfMatches_Server(base.NetworkObject, _tauntId);
            }
            _tauntedDummies.Clear();

            _tauntId = 0;

            // 패널티 억제 해제
            if (tankerPenalty != null)
                tankerPenalty.SetSuppressedByUltimate_Server(false);

            // 궁극기 “사용중” 종료(재사용 가능 상태로)
            if (ultimateCharge != null)
                ultimateCharge.RequestEndUltimate_Server();
        }

        /// <summary>사망 시 즉시 궁극기 관련 작업/상태 정리</summary>
        public void CleanUpOnDeath_Server()
        {
            if (!IsServerInitialized) return;
            if (_active) Deactivate_Server();
            else
            {
                _activeCts?.Cancel();
                _activeCts?.Dispose();
                _activeCts = null;

                // 혹시 남아있을 수 있는 것들 방어적으로 제거(세션이 남아있는 경우만)
                for (int i = 0; i < _tauntedDetectors.Count; i++)
                {
                    MonsterDetector d = _tauntedDetectors[i];
                    if (d == null) continue;
                    d.ClearTauntIfMatches_Server(base.NetworkObject, _tauntId, resetTarget: true);
                }
                _tauntedDetectors.Clear();

                for (int i = 0; i < _tauntedDummies.Count; i++)
                {
                    TrainingDummy dum = _tauntedDummies[i];
                    if (dum == null) continue;
                    dum.ClearTauntVisualIfMatches_Server(base.NetworkObject, _tauntId);
                }
                _tauntedDummies.Clear();

                _tauntId = 0;

                if (tankerPenalty != null) tankerPenalty.SetSuppressedByUltimate_Server(false);
                if (ultimateCharge != null) ultimateCharge.RequestEndUltimate_Server();
            }
        }

        /// <summary>리스폰 후 상태 복구(패널티/강제타겟/CTS 정리)</summary>
        public void OnAfterRespawn_Server()
        {
            // 리스폰 후에는 무조건 “궁극기 비활성” 상태로 정리
            CleanUpOnDeath_Server();
        }
    }
}
