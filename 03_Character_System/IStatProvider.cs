namespace NGPN.Gameplay
{
    /// <summary>
    /// (서버 권위) 공격/피격/표시(UI)에서 참조하는 캐릭터 스탯 공급자.
    /// 공격/스킬 로직은 이 인터페이스만 의존하면, 구체 클래스(캐릭터/AI)에 덜 결합된다.
    /// </summary>
    public interface IStatProvider
    {
        /// <summary>현재 ATK (레벨→SO 테이블→실수값)</summary>
        float CurrATK { get; }

        /// <summary>현재 DEF (레벨→SO 테이블→실수값)</summary>
        float CurrDEF { get; }

        /// <summary>현재 MaxHP (레벨→SO 테이블→실수값)</summary>
        float CurrMaxHP { get; }

        /// <summary>현재 치명타 확률(0~1)</summary>
        float CurrCritChance { get; }

        /// <summary>현재 걷기 속도(레벨 무관, SO 상수)</summary>
        float CurrWalkSpeed { get; }

        /// <summary>현재 달리기 속도(레벨 무관, SO 상수)</summary>
        float CurrRunSpeed { get; }

        /// <summary>현재 ATK/DEF/HP/CRT 레벨(1~3)</summary>
        int AtkLv { get; }
        int DefLv { get; }
        int HpLv  { get; }
        int CrtLv { get; }
    }
}
