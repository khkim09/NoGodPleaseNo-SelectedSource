using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using FishNet;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Object;
using FishNet.Object.Synchronizing;

namespace NGPN.Gameplay
{
    /// <summary>
    /// 공용 재화를 서버 권위로 관리하는 매니저.
    /// - 서버만 증액/차감 수행
    /// - 모든 플레이어에게 익명 지출 알림 브로드캐스트
    /// - 유료(옵션)로 지출 로그 공개
    /// </summary>
    public class SharedEconomy : NetworkBehaviour
    {
        [Header("Config")]
        /// <summary>지출 로그 열람 비용(예시)</summary>
        [SerializeField]
        private int revealLogBaseCost = 30;

        [Header("Audio")]
        /// <summary>공용 재화 사용(차감) 시 재생할 SFX</summary>
        [SerializeField] private AudioClip spendSfx;
        /// <summary>재화 사용 SFX 볼륨</summary>
        [SerializeField, Range(0f, 1f)] private float spendSfxVolume = 1f;
        /// <summary>피치 랜덤 범위 (조금씩 다르게 들리도록)</summary>
        [SerializeField] private Vector2 spendSfxPitchJitter = new(0.98f, 1.02f);

        /// <summary>재화 SFX를 재생할 오디오 소스</summary>
        private AudioSource _audioSrc;

        /// <summary>싱글턴 인스턴스</summary>
        public static SharedEconomy Instance { get; private set; }

        private readonly SyncVar<int> _sharedCurrency = new();

        /// <summary>현재 공용 재화</summary>
        public int SharedCurrency => _sharedCurrency.Value;

        /// <summary>지출 로그 열람 비용</summary>
        public int RevealLogsBaseCost => revealLogBaseCost;

        /// <summary>지출 사유</summary>
        public enum SpendReason : byte
        {
            AutoRepairAfterWave = 1,
            AltarUpgrade = 2,
            ShopPurchase = 3,
            RevealLogs = 4
        }

        [Serializable]
        public struct SpendLog
        {
            public double serverTs;
            public int amount;
            public SpendReason reason;
            public int waveIndex;
            public int? spenderConnId; // 서버 저장은 익명(ConnId만 저장)

            // 기존 원문
            public string place;
            public string detail;

            // 로컬라이징 키 + 인자
            public string placeKey;
            public string[] placeArgs;
            public string detailKey;
            public string[] detailArgs;
        }

        /// <summary>클라로 내려갈 때 사용할 뷰</summary>
        [Serializable]
        public struct SpendLogView
        {
            public double serverTs;
            public int amount;
            public SpendReason reason;
            public int waveIndex;
            public string spenderName; // 공개 시점에만 닉네임 해석

            // 기존 원문
            public string place;
            public string detail;

            // 로컬라이징 키 + 인자
            public string placeKey;
            public string[] placeArgs;
            public string detailKey;
            public string[] detailArgs;
        }

        /// <summary>UI 구독 이벤트 - 클라 전용 (유료 재화 사용 내역 로그)</summary>
        public static event Action<SpendLogView[]> LogsRevealedClient;

        private readonly List<SpendLog> _serverSpendLogs = new();

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;

            _audioSrc = GetComponent<AudioSource>();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            _sharedCurrency.Value = 0;
            // _sharedCurrency.Value = 10000;
        }

        #region 공유 재화 현재 보유량

        /// <summary>새 게임 세션 시작 시 공유 재화량 초기화</summary>
        [Server]
        public void ResetForNewRun_Server()
        {
            _sharedCurrency.Value = 500;
        }

        /// <summary>서버 - 보상 가산</summary>
        [Server]
        public void AddReward_Server(int amount, string note = null)
        {
            if (amount <= 0) return;
            _sharedCurrency.Value += amount;
            if (!string.IsNullOrEmpty(note))
                Debug.Log($"[SharedEconomy] +{amount} ({note}) → {SharedCurrency}");
        }

        /// <summary>차감 후 로그 기록(기본: wave=-1, place/detail 없음).</summary>
        [Server]
        public bool TrySpend_Server(int amount, SpendReason reason, NetworkConnection spender)
        {
            return TrySpend_Server(amount, reason, spender, -1,
                null, null,
                null, null,
                null, null);
        }

        /// <summary>차감 후 로그 기록(웨이브 인덱스 지정).</summary>
        [Server]
        public bool TrySpend_Server(int amount, SpendReason reason, NetworkConnection spender, int waveIndex)
        {
            return TrySpend_Server(amount, reason, spender, waveIndex,
                null, null,
                null, null,
                null, null);
        }

        /// <summary>차감 후 로그 기록(사용 장소/상세까지 전체 컨텍스트 기록).</summary>
        [Server]
        public bool TrySpend_Server(int amount, SpendReason reason, NetworkConnection spender, int waveIndex,
            string place, string detail)
        {
            return TrySpend_Server(amount, reason, spender, waveIndex,
                null, null,
                null, null,
                place, detail);
        }

        /// <summary>차감 후 로그 기록(키 기반 - 로컬라이징 원활화)</summary>
        [Server]
        public bool TrySpend_Server(
            int amount, SpendReason reason, NetworkConnection spender,
            int waveIndex,
            string placeKey, string[] placeArgs,
            string detailKey, string[] detailArgs,
            string placeFallback = null, string detailFallback = null)
        {
            if (amount <= 0) return false;
            if (_sharedCurrency.Value < amount) return false;

            _sharedCurrency.Value -= amount;

            // SFX 재생
            if (_audioSrc != null && spendSfx != null)
            {
                float pitch = UnityEngine.Random.Range(spendSfxPitchJitter.x, spendSfxPitchJitter.y);
                PlaySpendSfx_ObserversRpc(spendSfxVolume, pitch);
            }

            _serverSpendLogs.Add(new SpendLog
            {
                serverTs = TimeManager != null ? TimeManager.ServerUptime : Time.timeAsDouble,
                amount = amount,
                reason = reason,
                waveIndex = waveIndex,
                spenderConnId = spender?.ClientId,

                // 키/인자
                placeKey = placeKey,
                placeArgs = placeArgs,
                detailKey = detailKey,
                detailArgs = detailArgs,

                // fallback
                place = placeFallback,
                detail = detailFallback
            });
            return true;
        }

        #endregion

        #region 지출 로그 열람

        /// <summary>현재 보유 재화로 주어진 금액을 지불할 수 있는지</summary>
        public bool CanAfford(int amount)
        {
            return SharedCurrency >= Mathf.Max(0, amount);
        }

        /// <summary>클라이언트→서버: 지출 로그 스냅샷을 요청(요청자에게만 공개). 소유권 불필요.</summary>
        [ServerRpc(RequireOwnership = false)]
        public void RequestRevealLogs_ServerRpc(NetworkConnection requester)
        {
            RevealAllLogsSnapshot_Server(requester);
        }

        /// <summary>호스트 본인에게 보내는 TargetRpc가 로컬에서도 실행되도록</summary>
        /// <param name="conn"></param>
        /// <param name="logs"></param>
        [TargetRpc(RunLocally = true)]
        private void SendRevealedLogsTargetRpc(NetworkConnection conn, SpendLogView[] logs)
        {
            // UI 모듈이 이 이벤트를 구독해서 표시(제단 UI에서 사용)
            LogsRevealedClient?.Invoke(logs);
        }

        /// <summary>서버 - 지출 로그를 유료로 '요청자에게만' 공개(구매 시점까지 스냅샷).</summary>
        [Server]
        public void RevealAllLogsSnapshot_Server(NetworkConnection requester)
        {
            // 1) 결제
            int waveIndex = DefenseGameManager.Instance ? DefenseGameManager.Instance.CurrentWave : -1;

            // 키만 기록
            string placeKey = "UI.Common:ingame.altar.logs.place.view_logs";
            string detailKey = "UI.Common:ingame.altar.logs.detail.view_logs";
            // string place = LocalizationSettings.StringDatabase
            //     .GetLocalizedString("UI.Common", "ingame.altar.logs.place.view_logs");
            // string detail = LocalizationSettings.StringDatabase
            //     .GetLocalizedString("UI.Common", "ingame.altar.logs.detail.view_logs");
            int cost = revealLogBaseCost;

            if (!TrySpend_Server(cost, SpendReason.RevealLogs, requester, waveIndex, placeKey, null, detailKey, null))
                return;

            // 2) 스냅샷 타임스탬프(구매 시점 고정)
            double snapTs = TimeManager != null ? TimeManager.ServerUptime : Time.timeAsDouble;

            // 3) 구매 시점 이전(<=) 로그만 복사
            SpendLog[] slice = _serverSpendLogs.Count == 0
                ? Array.Empty<SpendLog>()
                : _serverSpendLogs.Where(l => l.serverTs <= snapTs).ToArray();

            // 4) 닉네임 해석 포함한 뷰 생성
            SpendLogView[] views = new SpendLogView[slice.Length];
            for (int i = 0; i < slice.Length; i++)
            {
                SpendLog s = slice[i];
                views[i] = new SpendLogView
                {
                    serverTs = s.serverTs,
                    amount = s.amount,
                    reason = s.reason,
                    waveIndex = s.waveIndex,
                    spenderName = ResolvePlayerName(s.spenderConnId),
                    place = s.place,
                    detail = s.detail,
                    placeKey = s.placeKey,
                    placeArgs = s.placeArgs,
                    detailKey = s.detailKey,
                    detailArgs = s.detailArgs
                };
            }

            // 5) 요청자에게만 전송
            if (requester != null)
            {
                SendRevealedLogsTargetRpc(requester, views);
            }
            else
            {
                // 서버 전용 모드 - 에디터 테스트용
#if UNITY_EDITOR
                Debug.Log("[SharedEconomy] Server-only mode: invoking UI event directly.");
                LogsRevealedClient?.Invoke(views);
#endif
            }
        }

        /// <summary>
        /// ConnId → 닉네임 매핑.
        /// 우선순위: PlayerRegistry → 해당 커넥션의 캐릭터(IHasDisplayName/CharacterActor) → "Conn-###"
        /// </summary>
        [Server]
        private string ResolvePlayerName(int? connIdNullable)
        {
            if (connIdNullable is not int connId) return "?";

            // 1) PlayerRegistry 디렉토리
            if (PlayerRegistry.Instance != null &&
                PlayerRegistry.Instance.TryGetPlayerName(connId, out string regName) &&
                !string.IsNullOrWhiteSpace(regName))
                return regName;

            // 2) 커넥션의 FirstObject에서 탐색
            NetworkManager nm = InstanceFinder.NetworkManager;
            if (nm != null && nm.ServerManager != null && nm.ServerManager.Clients != null &&
                nm.ServerManager.Clients.TryGetValue(connId, out NetworkConnection conn) &&
                conn != null)
            {
                NetworkObject no = conn.FirstObject;
                if (no != null)
                {
                    IHasDisplayName disp = no.GetComponentInChildren<IHasDisplayName>();
                    if (disp != null && !string.IsNullOrWhiteSpace(disp.DisplayName))
                        return disp.DisplayName;

                    CharacterActor ca = no.GetComponentInChildren<CharacterActor>();
                    if (ca != null && !string.IsNullOrWhiteSpace(ca.DisplayName))
                        return ca.DisplayName;
                }
            }

            // 3) 실패 시
            return $"Conn-{connId}";
        }

        #endregion

        #region Audio RPC
        /// <summary>모든 클라이언트에서 공용 재화 사용 SFX 재생</summary>
        [ObserversRpc(BufferLast = false, RunLocally = true)]
        private void PlaySpendSfx_ObserversRpc(float volume, float pitch)
        {
            if (_audioSrc == null || spendSfx == null) return;

            _audioSrc.pitch = pitch;
            _audioSrc.PlayOneShot(spendSfx, volume);
        }
        #endregion
    }
}
