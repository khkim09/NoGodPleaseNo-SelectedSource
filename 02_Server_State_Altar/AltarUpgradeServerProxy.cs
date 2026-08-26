using UnityEngine;
using FishNet.Object;
using FishNet.Connection;
using NGPN.Gameplay.UI;

namespace NGPN.Gameplay
{
    public interface IStatLevelBackend : IStatProvider
    {
        /// <summary>서버에서 실제 레벨을 +1 적용. 성공 시 true.</summary>
        bool TryIncreaseLevel_Server(StatKind kind);

        JobStatsDefinition JobStatsDef { get; }
    }

    public class AltarUpgradeServerProxy : NetworkBehaviour
    {
        [SerializeField] private IStatLevelBackend backend; // 같은 오브젝트에 구현되어야 함
        public JobStatsDefinition Job => backend?.JobStatsDef;

        private DefenseGameManager G => DefenseGameManager.Instance;
        private SharedEconomy E => SharedEconomy.Instance;

        private void Awake()
        {
            if (backend == null) backend = GetComponent<IStatLevelBackend>();
        }

        /// <summary>서버에서 공용 지출 로그 스냅샷 생성 → 요청자(Owner)에게만 내려줌</summary>
        [ServerRpc]
        public void RequestRevealSpentLogs()
        {
            // Owner: 이 NetworkBehaviour를 소유한 클라의 NetworkConnection
            SharedEconomy.Instance?.RevealAllLogsSnapshot_Server(Owner);
        }

        [ServerRpc]
        public void RequestUpgrade(StatKind kind, NetworkConnection conn = null)
        {
            if (backend == null || E == null || G == null) return;

            // wave 사이 준비시간에만 업그레이드 허용
            if (!G.CanUseAltars) return;

            int curr = kind switch
            {
                StatKind.ATK => backend.AtkLv,
                StatKind.DEF => backend.DefLv,
                StatKind.MaxHP => backend.HpLv,
                _ => backend.CrtLv
            };

            if (curr >= 3) return; // MAX

            int next = curr + 1;
            int cost = kind switch
            {
                StatKind.ATK => backend.JobStatsDef.GetAtkCost(next),
                StatKind.DEF => backend.JobStatsDef.GetDefCost(next),
                StatKind.MaxHP => backend.JobStatsDef.GetHpCost(next),
                _ => backend.JobStatsDef.GetCrtCost(next)
            };

            string statName = kind.ToString();
            int waveIndex = G.CurrentWave;

            // 구매 기록을 '키 기반'으로 저장.
            // - placeKey 없음: UI에서 ReasonToPlace로 현재 Locale에 맞게 장소를 만듭니다.
            // - detailKey 사용: "{stat} Lv{from} > Lv{to}"
            if (!E.TrySpend_Server(
                    cost,
                    SharedEconomy.SpendReason.AltarUpgrade,
                    conn,
                    waveIndex,
                    placeKey: null, placeArgs: null,
                    detailKey: "UI.Common:ingame.altar.logs.detail.upgrade",
                    detailArgs: new[] { statName, curr.ToString(), next.ToString() },
                    // 폴백(테이블 미완 시 임시 노출용)
                    placeFallback: null,
                    detailFallback: $"{statName} Lv{curr} > Lv{next}"
                ))
                return;

            // 실제 레벨업 적용
            if (!backend.TryIncreaseLevel_Server(kind))
                // 실패 시 롤백(간단히: 환불)
                E.AddReward_Server(cost, "Refund (Upgrade Failed)");
        }
    }
}
