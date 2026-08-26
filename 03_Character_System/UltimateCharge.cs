using System;
using System.Threading;
using UnityEngine;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using Cysharp.Threading.Tasks;
using NGPN.Combat;

namespace NGPN.Gameplay
{
    /// <summary>서버 권위로 궁극기 포인트를 누적/동기화하고, Ready/사용중 상태를 관리</summary>
    [DisallowMultipleComponent]
    public class UltimateCharge : NetworkBehaviour, IDeathCleanable, IRespawnable
    {
        [Header("Refs")]
        [SerializeField] private CharacterActor actor;

        [Header("Input")]
        /// <summary>궁극기 입력 키(Q)</summary>
        [SerializeField] private KeyCode ultimateKey = KeyCode.Q;

        [Header("Ability")]
        /// <summary>(선택) 서버에서 궁극기 실행을 담당하는 컴포넌트(미지정 시 자동 탐색)</summary>
        [SerializeField] private MonoBehaviour ultimateAbilityBehaviour;

        /// <summary>현재 누적 궁극기 포인트(0~cost). 서버 권위 SyncVar</summary>
        private readonly SyncVar<float> _points = new();

        /// <summary>궁극기 사용중 여부(사용이 완전히 끝날 때까지 차징 금지). 서버 권위 SyncVar</summary>
        private readonly SyncVar<bool> _inUse = new();

        /// <summary>클라(UI)에서 게이지 갱신에 쓰는 이벤트</summary>
        public event Action<float, float, bool, bool> ClientUltimateChanged;

        /// <summary>현재 포인트(서버/클라 읽기)</summary>
        public float Points => _points.Value;

        /// <summary>궁극기 사용중 여부</summary>
        public bool InUse => _inUse.Value;

        /// <summary>각 직업 바쁜 여부</summary>
        private IJobBusyProvider _jobBusy;

        private float _usablePollAcc;

        /// <summary>100% 충전(Ready) 여부</summary>
        public bool IsReady => Points >= GetCostSafe() && !_inUse.Value;

        [SerializeField] private InteractionLockHub _lockHub;
        private CancellationTokenSource _useCts;

        public event Action<bool> ClientUltimateUsableChanged;
        private bool _lastUsable;

        private void Awake()
        {
            if (!actor) actor = GetComponent<CharacterActor>();
            if (!_lockHub) _lockHub = GetComponent<InteractionLockHub>();
            _jobBusy = GetComponent<IJobBusyProvider>(); // 없으면 null
        }

        private void OnDestroy()
        {
            _useCts?.Cancel();
            _useCts?.Dispose();
            _useCts = null;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            _points.OnChange += OnPointsChanged_Client;
            _inUse.OnChange += OnInUseChanged_Client;

            // 초기 1회
            RaiseClientChanged();
        }

        public override void OnStopClient()
        {
            base.OnStopClient();
            _points.OnChange -= OnPointsChanged_Client;
            _inUse.OnChange -= OnInUseChanged_Client;
        }

        private void OnPointsChanged_Client(float prev, float next, bool asServer)
        {
            if (asServer) return;
            RaiseClientChanged();
        }

        private void OnInUseChanged_Client(bool prev, bool next, bool asServer)
        {
            if (asServer) return;
            RaiseClientChanged();
        }

        /// <summary>클라(UI)에 현재 궁극기 상태를 브로드캐스트</summary>
        private void RaiseClientChanged()
        {
            float cost = GetCostSafe();
            bool ready = _points.Value >= cost;
            ClientUltimateChanged?.Invoke(_points.Value, cost, ready, _inUse.Value);

            bool usable = ready && !_inUse.Value && !IsUltimateInputBlocked();
            if (usable != _lastUsable)
            {
                _lastUsable = usable;
                ClientUltimateUsableChanged?.Invoke(usable);
            }
        }

        /// <summary>궁극기 비용 가져오기</summary>
        private float GetCostSafe()
        {
            JobStatsDefinition def = actor ? actor.JobStatsDef : null;
            return def != null ? Mathf.Max(1f, def.ultimateCost) : 1000f;
        }

        /// <summary>자동 수급 포인트 가져오기</summary>
        private float GetPassivePerSecSafe()
        {
            JobStatsDefinition def = actor ? actor.JobStatsDef : null;
            return def != null ? Mathf.Max(0f, def.ultimatePassivePointsPerSec) : 0f;
        }

        /// <summary>가한 피해량 환산 수급 포인트 가져오기</summary>
        private float GetDmgMulSafe()
        {
            JobStatsDefinition def = actor ? actor.JobStatsDef : null;
            return def != null ? Mathf.Max(0f, def.ultimateFromDamageMultiplier) : 0f;
        }

        /// <summary>힐량 환산 수급 포인트 가져오기</summary>
        private float GetHealMulSafe()
        {
            JobStatsDefinition def = actor ? actor.JobStatsDef : null;
            return def != null ? Mathf.Max(0f, def.ultimateFromHealMultiplier) : 0f;
        }

        private void Update()
        {
            // 1) 서버: 패시브 누적
            if (IsServerInitialized) TickPassive_Server();

            // 2) 오너 클라: Q 입력
            if (IsOwner && IsClientInitialized)
            {
                TickOwnerInput_Client();
                TickUsablePoll_Client();
            }
        }

        /// <summary>서버에서 패시브 포인트 누적을 처리</summary>
        private void TickPassive_Server()
        {
            // 웨이브 조건/궁 사용 중 조건을 중앙에서 통제
            if (!CanGainUltimatePoints_Server()) return;

            float cost = GetCostSafe();
            if (_points.Value >= cost) return; // 100% 고정

            float passive = GetPassivePerSecSafe();
            if (passive <= 0f) return;

            _points.Value = Mathf.Min(cost, _points.Value + passive * Time.deltaTime);
        }

        /// <summary>로컬 오너 입력(Q)을 처리하고 서버에 사용 요청</summary>
        private void TickOwnerInput_Client()
        {
            if (!Input.GetKeyDown(ultimateKey)) return;
            if (IsUltimateInputBlocked()) return;

            // Ready가 아니면 서버 호출해도 결국 실패하니, 로컬에서 1차 컷
            float cost = GetCostSafe();
            if (_inUse.Value) return; // 사용 중이면 재사용 금지
            if (_points.Value < cost) return; // 미충전

            TryUseUltimate_ServerRpc();
        }

        private void TickUsablePoll_Client()
        {
            // 매 프레임 해도 되는데, UI/성능 생각해서 0.05초(20Hz) 정도 추천
            _usablePollAcc += Time.unscaledDeltaTime;
            if (_usablePollAcc < 0.05f) return;
            _usablePollAcc = 0f;

            bool usable = CanUseUltimateNow_Client();

            if (usable != _lastUsable)
            {
                _lastUsable = usable;
                ClientUltimateUsableChanged?.Invoke(usable);
            }
        }

        /// <summary>ESC/상호작용/UI 등 “좌클/우클 정상 상황이 아닐 때” 궁극기 입력을 블락</summary>
        private bool IsUltimateInputBlocked()
        {
            if (TryGetComponent(out CharacterHealth ch) && !ch.isAlive) return true;

            // 공격/상호작용이 잠긴 상황은 전부 블락
            if (_lockHub != null && (_lockHub.AttackLocked || _lockHub.InteractionLocked))
                return true;

            if (_jobBusy != null && _jobBusy.IsJobBusy)
                return true;

            return false;
        }

        /// <summary>가한 피해량을 궁극기 포인트로 환산해 누적</summary>
        [Server]
        public void AddDamage_Server(float damageAmount)
        {
            if (!CanGainUltimatePoints_Server()) return;
            if (TryGetComponent(out CharacterHealth ch) && !ch.isAlive) return;

            float cost = GetCostSafe();
            if (_points.Value >= cost) return;
            if (damageAmount <= 0f) return;

            float pts = damageAmount * GetDmgMulSafe();
            if (pts <= 0f) return;

            _points.Value = Mathf.Min(cost, _points.Value + pts);
        }

        /// <summary>힐량을 궁극기 포인트로 환산해 누적</summary>
        [Server]
        public void AddHeal_Server(float healAmount)
        {
            if (!CanGainUltimatePoints_Server()) return;
            if (TryGetComponent(out CharacterHealth ch) && !ch.isAlive) return;

            float cost = GetCostSafe();
            if (_points.Value >= cost) return;
            if (healAmount <= 0f) return;

            float pts = healAmount * GetHealMulSafe();
            if (pts <= 0f) return;

            _points.Value = Mathf.Min(cost, _points.Value + pts);
        }

        /// <summary>궁극기 시전 시작 - 포인트를 0으로 리셋하고 사용중 상태로 전환</summary>
        [Server]
        public void BeginUltimateUse_Server(bool lockChargingUntilEnd = true)
        {
            _points.Value = 0f;
            _inUse.Value = true;
        }

        /// <summary>궁극기 시전 종료: 사용중 상태를 해제, 다시 차징 가능하게 전환</summary>
        [Server]
        public void EndUltimateUse_Server()
        {
            _inUse.Value = false;
        }

        /// <summary>오너가 서버에 궁극기 사용을 요청</summary>
        [ServerRpc(RequireOwnership = true)]
        private void TryUseUltimate_ServerRpc()
        {
            TryUseUltimate_Server();
        }

        /// <summary>서버에서 궁극기 사용 가능 여부를 검사하고 실행/종료를 관리</summary>
        [Server]
        private void TryUseUltimate_Server()
        {
            if (_inUse.Value) return;

            if (_lockHub != null && (_lockHub.AttackLocked || _lockHub.InteractionLocked))
                return;

            if (_jobBusy == null)
                _jobBusy = GetComponent<IJobBusyProvider>();
            if (_jobBusy != null && _jobBusy.IsJobBusy)
                return;

            float cost = GetCostSafe();
            if (_points.Value < cost) return;

            // 궁극기 컴포
            IUltimateAbility ability = null;

            if (ultimateAbilityBehaviour != null)
                ability = ultimateAbilityBehaviour as IUltimateAbility;
            if (ability == null)
                ability = GetComponent<IUltimateAbility>();

            if (ability == null)
            {
                Debug.LogError("등록된 궁극기 없음");
                return;
            }

            // 궁극기 시작
            BeginUltimateUse_Server();

            float duration = Mathf.Max(0f, ability.ExecuteUltimate_Server());

            _useCts?.Cancel();
            _useCts?.Dispose();
            _useCts = new CancellationTokenSource();
            AutoEndAfter_Server(duration, _useCts.Token).Forget();
        }

        /// <summary>서버에서 duration 후 자동으로 궁극기 사용을 종료</summary>
        private async UniTaskVoid AutoEndAfter_Server(float durationSec, CancellationToken ct)
        {
            if (durationSec <= 0f)
            {
                EndUltimateUse_Server();
                return;
            }

            try
            {
                await UniTask.Delay(TimeSpan.FromSeconds(durationSec), cancellationToken: ct);
            }
            catch
            {
                // cancelled
                return;
            }

            if (!ct.IsCancellationRequested)
                EndUltimateUse_Server();
        }

        /// <summary>현재 씬이 '웨이브 시스템을 사용하는 게임씬'인지 여부(웨이브 매니저가 유효하면 게임씬으로 간주)</summary>
        private bool IsWaveGameplayScene_Server()
        {
            DefenseGameManager dgm = DefenseGameManager.Instance;
            return dgm != null && dgm.waveManager != null;
        }

        /// <summary>
        /// 서버 기준 '지금 궁극기 포인트를 수급(패시브/딜/힐)해도 되는지' 여부.
        /// - 궁극기 사용/효과 지속 중에는 어떤 방식으로도 충전 금지
        /// - 게임씬에서는 웨이브 진행 중일 때만 충전 허용
        /// - 로비/승리/패배 등 웨이브가 없는 씬에서는 항상 충전 허용
        /// </summary>
        private bool CanGainUltimatePoints_Server()
        {
            // 1) 궁극기 사용/효과 지속 중에는 무조건 차징 금지
            if (_inUse.Value) return false;

            // 2) 웨이브 게임씬이면 웨이브 진행 중일 때만 차징
            if (IsWaveGameplayScene_Server())
                return DefenseGameManager.IsWaveActive;

            // 3) 그 외 씬(로비/승리/패배 등): 항상 차징 허용
            return true;
        }

        public bool CanUseUltimateNow_Client()
        {
            if (!IsOwner || !IsClientInitialized) return false;

            float cost = GetCostSafe();
            bool ready = _points.Value >= cost && !_inUse.Value;

            if (!ready) return false; // 게이지 부족이면 usable false (하지만 UI 회색은 안 줄 거라 UI에서 처리)

            // Ready일 때만 락 체크 의미있음
            return !IsUltimateInputBlocked();
        }

        public bool CanUseNowClient
        {
            get
            {
                if (!IsClientInitialized) return false;
                if (!IsOwner) return false;
                return IsReady && !IsUltimateInputBlocked();
            }
        }

        /// <summary>사망 시 궁극기 관련 CTS/상태를 즉시 정리</summary>
        public void CleanUpOnDeath_Server()
        {
            _useCts?.Cancel();
            _useCts?.Dispose();
            _useCts = null;

            _inUse.Value = false;
        }

        /// <summary>리스폰 후 궁극기 상태를 기본값으로 복구</summary>
        public void OnAfterRespawn_Server()
        {
            // 강제 리셋 시에도 안전하도록 동일 처리
            CleanUpOnDeath_Server();
        }

        /// <summary>
        /// (서버) 궁극기 능력(Ability)에서 “조기 종료”를 요청할 때 사용.
        /// 상태 소유는 UltimateCharge가 하므로, 외부는 이 함수만 호출.
        /// </summary>
        [Server]
        public void RequestEndUltimate_Server()
        {
            // 이미 종료된 경우 안전하게 무시
            if (!_inUse.Value) return;

            // 기존 AutoEndAfter가 돌고 있을 수 있으니 CTS도 같이 정리
            _useCts?.Cancel();
            _useCts?.Dispose();
            _useCts = null;

            EndUltimateUse_Server();
        }

        /// <summary>
        /// (서버) 씬 진입/재시작 등 "완전 초기화"가 필요할 때 궁극기 상태를 기본값으로 리셋한다.
        /// - 포인트 0
        /// - InUse false
        /// - 진행 중 AutoEnd 타이머(CTS) 취소
        /// </summary>
        [Server]
        public void ResetForSceneEntry_Server()
        {
            _useCts?.Cancel();
            _useCts?.Dispose();
            _useCts = null;

            _points.Value = 0f;
            _inUse.Value = false;
        }

        /// <summary>
        /// (서버) 궁극기 게이지를 즉시 100%로 만든다.
        /// - InUse 중이면(효과 지속) 충전 정책에 따라 막을 수 있음
        /// </summary>
        [Server]
        public void ForceFullCharge_Server()
        {
            if (!IsServerInitialized) return;

            // 효과 지속 중에는 충전 금지 정책(네 CanGainUltimatePoints 정책과 일치)
            if (_inUse.Value) return;

            float cost = GetCostSafe();
            _points.Value = cost;
        }
    }
}
