namespace NGPN.Combat
{
    /// <summary>궁극기 실제 실행 + 사용 가능 여부까지 책임지는 인터페이스</summary>
    public interface IUltimateAbility
    {
        /// <summary>서버에서 궁극기를 실행하고, 사용중 상태를 유지할 시간을(초) 반환한다. 0 이하면 즉시 종료.</summary>
        float ExecuteUltimate_Server();
    }
}
