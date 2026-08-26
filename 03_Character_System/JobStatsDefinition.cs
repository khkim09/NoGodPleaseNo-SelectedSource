using UnityEngine;
using NGPN.Core;

namespace NGPN.Gameplay
{
    /// <summary>
    /// 직업별 기본/이동/쿨타임 + 1/2/3단계 스탯을 담는 SO
    /// - baseStats는 레거시 호환 및 초기값 용(권장: level1과 동일값)
    /// </summary>
    [CreateAssetMenu(fileName = "JobStatsDefinition", menuName = "Game/Jobs/JobStatsDefinition")]
    public class JobStatsDefinition : ScriptableObject
    {
        public JobType jobType;

        [Header("Movement (레벨 무관)")] public float walkSpeed = 5f;
        public float runSpeed = 10f;

        /// <summary>
        /// 제단 업그레이드 단계별 스텟
        /// </summary>
        [Header("Per-Level Stats (size = 3, 1~3 레벨)")]
        [Tooltip("ATK[0]=Lv1, ATK[1]=Lv2, ATK[2]=Lv3")] public float[] ATK = new float[3];
        [Tooltip("DEF[0]=Lv1, DEF[1]=Lv2, DEF[2]=Lv3")] public float[] DEF = new float[3];
        [Tooltip("MaxHP[0]=Lv1, MaxHP[1]=Lv2, MaxHP[2]=Lv3")] public float[] MaxHP = new float[3];
        [Tooltip("CRT(확률 0~1) [0]=Lv1, [1]=Lv2, [2]=Lv3")] public float[] CRT = new float[3];

        [Header("Voodoo Aura (Specific)")]
        [Tooltip("오라 고정 수치 [0]=Lv1, [1]=Lv2, [2]=Lv3")] public float[] AuraFixedValue = new float[3];

        [Header("Per-Level Costs (Lv1~Lv3)")]
        [Tooltip("ATK 업그레이드 비용")] public int[] ATK_COST = new int[3];
        [Tooltip("DEF 업그레이드 비용")] public int[] DEF_COST = new int[3];
        [Tooltip("HP 업그레이드 비용")] public int[] HP_COST = new int[3];
        [Tooltip("CRT 업그레이드 비용")] public int[] CRT_COST = new int[3];

        [Header("Ultimate (Charge)")]
        /// <summary>궁극기 100%에 필요한 총 누적 포인트(딜/힐/시간수급이 이 값에 도달하면 Ready)</summary>
        public float ultimateCost = 1500f;

        /// <summary>가만히 있어도 초당 획득하는 궁극기 포인트(= 패시브 수급)</summary>
        public float ultimatePassivePointsPerSec = 1.2f;

        /// <summary>가한 피해 1당 궁극기 포인트로 환산되는 비율</summary>
        public float ultimateFromDamageMultiplier = 1.7f;

        /// <summary>가한 회복량 1당 궁극기 포인트로 환산되는 비율</summary>
        public float ultimateFromHealMultiplier = 2.0f;

        [Header("Preview (Client-only)")]
        [Tooltip("제단 UI 좌측 미리보기에 쓸 '비네트워크' 프리팹")]
        public GameObject previewPrefab;

        [Tooltip("카메라 X 위치 고정값 (null이면 회전 계산값 사용)")]
        public float previewCamFixedX = 0f;

        [Tooltip("콜라이더 중심(허리) Y 값(미터). 캐릭터 기준 좌표계에서 값만 입력")]
        public float previewCenterY = 1.1f;

        [Tooltip("기본 카메라 거리(정면)")] public float previewCamDistance = 3.0f;
        [Tooltip("정면 기준 좌우 회전각(도)")] public float previewYawDeg = 10f;
        [Tooltip("카메라 orthographic size")] public float orthoSize = 1.3f;

        /// <summary>클램프된 인덱스(0~2) 반환</summary>
        private static int Idx(int lv)
        {
            return Mathf.Clamp(lv - 1, 0, 2);
        }

        public float GetATK(int lv)
        {
            return ATK[Idx(lv)];
        }

        public float GetDEF(int lv)
        {
            return DEF[Idx(lv)];
        }

        public float GetMaxHP(int lv)
        {
            return MaxHP[Idx(lv)];
        }

        public float GetCritChance(int lv)
        {
            return CRT[Idx(lv)];
        }

        public float GetAuraFixedValue(int lv)
        {
            return AuraFixedValue[Idx(lv)];
        }

        public int GetAtkCost(int nextLv)
        {
            return ATK_COST[Mathf.Clamp(nextLv - 1, 0, 2)];
        }

        public int GetDefCost(int nextLv)
        {
            return DEF_COST[Mathf.Clamp(nextLv - 1, 0, 2)];
        }

        public int GetHpCost(int nextLv)
        {
            return HP_COST[Mathf.Clamp(nextLv - 1, 0, 2)];
        }

        public int GetCrtCost(int nextLv)
        {
            return CRT_COST[Mathf.Clamp(nextLv - 1, 0, 2)];
        }
    }
}
