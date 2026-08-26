using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using Cysharp.Threading.Tasks;

namespace NGPN.Gameplay
{
    /// <summary>
    /// 마을 파괴율 계산 및 자동 수리(웨이브 종료 후) 수행 매니저.
    /// - 서버 전용 계산
    /// - 수리 비용은 누적 손실 HP * 단가
    /// - 공유 재화에서 우선 차감 후 수리
    /// </summary>
    public class TownDamageManager : NetworkBehaviour
    {
        [Header("Tags to Exclude From Damage Ratio")]
        [SerializeField] private string goddessTag = "Goddess"; // 여신상
        [SerializeField] private string altarTag = "Altar"; // 제단(상호작용 오브젝트)

        public string GoddessTag => goddessTag;
        public string AltarTag => altarTag;

        [Header("Batching")]
        /// <summary>수리 시, 한 프레임에 복구할 빌딩 수(부하 완화용)</summary>
        [SerializeField] private int restoreBatchSize = 12;

        /// <summary>씬 내 빌딩 캐시</summary>
        private List<BuildingBase> _buildings;

        // network damage ratio

        /// <summary>
        /// 서버가 브로드캐스트하는 마을 피해율(0~1).
        /// - 클라 UI는 이 값을 구독하거나 읽어서 슬라이더에 반영.
        /// </summary>
        private readonly SyncVar<float> _netDamageRatio = new();

        /// <summary>클라/서버 공용: 현재 네트워크로 공유되는 피해율(0~1)</summary>
        public float NetDamageRatio => _netDamageRatio.Value;

        // === 클라 HUD용 캐시 & 이벤트 ===
        /// <summary>마지막으로 브로드캐스트된 마을 파괴율(0~1)</summary>
        public static float LastKnownDamageRatio { get; private set; } = 0f;

        /// <summary>클라 전용: 마을 파괴율 변경 이벤트(UIManager에서 구독)</summary>
        public static event Action<float> TownDamageRatioChanged;

        public override void OnStartServer()
        {
            base.OnStartServer();
            CacheBuildings();

            // 서버 시작 시 한 번 0으로 브로드캐스트 (초기 UI 안정화)
            _netDamageRatio.Value = 0f;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            // 클라에서 SyncVar 변경 감지
            _netDamageRatio.OnChange += OnNetDamageRatioChanged;
        }

        public override void OnStopClient()
        {
            _netDamageRatio.OnChange -= OnNetDamageRatioChanged;
            base.OnStopClient();
        }

        private void OnNetDamageRatioChanged(float prev, float next, bool asServer)
        {
            if (asServer) return;
            LastKnownDamageRatio = next;
            TownDamageRatioChanged?.Invoke(next);
        }

        /// <summary>
        /// 서버: 씬 내 BuildingBase를 캐시.
        /// </summary>
        [Server]
        private void CacheBuildings()
        {
            _buildings = FindObjectsByType<BuildingBase>(FindObjectsSortMode.None).ToList();
        }

        /// <summary>현재 마을 파괴율(0~1)</summary>
        /// <param name="damageRatio"></param>
        [Server]
        public void ComputeDamageRatio_Server(out float damageRatio)
        {
            if (_buildings == null || _buildings.Count == 0)
                CacheBuildings();

            double sumMax = 0;
            double sumMissing = 0;

            foreach (BuildingBase b in _buildings)
            {
                GameObject go = b.gameObject;

                // 파괴율 계산에서 여신상/제단은 제외
                if (HasTagInHierarchy(go, goddessTag) || HasTagInHierarchy(go, altarTag))
                    continue;

                float max = b.GetMaxHP();
                float cur = b.GetCurrentHP();
                sumMax += max;
                sumMissing += Mathf.Clamp(max - cur, 0f, max);
            }
            damageRatio = (sumMax <= 0.01) ? 0f : (float)(sumMissing / sumMax);
        }

        /// <summary>
        /// 서버: 현재 마을 파괴율을 다시 계산한 뒤, 모든 관찰자에게 브로드캐스트.
        /// (건물에 데미지가 들어갈 때마다 호출해 주면 HUD가 실시간으로 갱신됨)
        /// </summary>
        [Server]
        public void RecomputeAndBroadcastTownDamage_Server()
        {
            ComputeDamageRatio_Server(out float ratio);
            _netDamageRatio.Value = Mathf.Clamp01(ratio);

            // ObserversRpc_UpdateTownDamage(ratio);
        }

        /// <summary>
        /// 서버: 마을 피해율을 0으로 강제 브로드캐스트.
        /// - 웨이브 종료 → 준비 단계 진입 시점에 UI를 즉시 0으로 만들 때 사용.
        /// </summary>
        [Server]
        public void BroadcastTownDamageZero_Server()
        {
            _netDamageRatio.Value = 0f;
        }

        /// <summary>웨이브 종료: 비용 없이 마을/제단 모두 복구(여신상만 제외)</summary>
        [Server]
        public async UniTask<int> FreeRestoreAllExceptGoddess_Server()
        {
            if (_buildings == null || _buildings.Count == 0)
                CacheBuildings();

            int restored = 0;
            int processed = 0;

            foreach (BuildingBase b in _buildings)
            {
                GameObject go = b.gameObject;
                if (HasTagInHierarchy(go, goddessTag)) continue; // 여신상은 복구 금지

                float max = b.GetMaxHP();
                float cur = b.GetCurrentHP();
                if (cur + 0.01f < max)
                {
                    b.RestoreFull_Server(max);
                    restored++;
                }

                processed++;
                if (processed % Mathf.Max(1, restoreBatchSize) == 0)
                    await UniTask.Yield();
            }

            return restored;
        }

        #region Helper
        /// <summary>계층 전체에서 특정 Tag 보유 여부 확인</summary>
        public static bool HasTagInHierarchy(GameObject root, string tag)
        {
            if (!root || string.IsNullOrWhiteSpace(tag)) return false;
            if (root.CompareTag(tag)) return true;
            foreach (Transform t in root.transform)
                if (t && t.gameObject.CompareTag(tag))
                    return true;
            return false;
        }
        #endregion
    }
}
//
// 1008
// 1) 마을 파괴율 계산 매니저
// 웨이브 종료 시점 - 마을 파괴율 산정, 자동 수리를 위해 필요한 총 수리 비용 계산
// 공유 재화에서 우선 차감 수리 진행
// 파괴율 - 씬 내 모든 BuildingBase의 (HP 손실 총합) / (전체 최대 HP합계)
