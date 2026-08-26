namespace NGPN.Gameplay
{
    /// <summary>
    /// 전역 강제 타겟(탱커 외침 등) 적용 시, 공격/스킬/CTS/애니를 "안전하게 중단"하고 추격으로 전환하기 위한 인터페이스
    /// </summary>
    public interface IForceRetargetHandler
    {
        /// <summary>리타겟 직전/직후 공격 상태를 안전하게 정리</summary>
        void ForceStopForRetarget_Server();
    }
}
