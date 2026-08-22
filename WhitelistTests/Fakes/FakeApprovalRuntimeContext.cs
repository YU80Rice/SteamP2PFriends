using Steamworks;

namespace SteamP2PFriends.WhitelistTests.Fakes
{
    /// <summary>
    /// Route B 单元测试 Fake：P2PApprovalManager.IApprovalRuntimeContext。
    /// 蓝图 v2 §3.2：测试可行性，不启动 Unturned/Steam/Unity。
    /// </summary>
    internal sealed class FakeApprovalRuntimeContext : SteamP2PFriends.Host.IApprovalRuntimeContext
    {
        public bool IsActiveP2PHostValue = true;
        public CSteamID LocalUserValue = new CSteamID(76561199030780228UL);
        public float RealtimeValue = 1000f;

        public bool IsActiveP2PHost => IsActiveP2PHostValue;
        public CSteamID LocalUser => LocalUserValue;
        public float RealtimeSinceStartup => RealtimeValue;

        public void AdvanceTime(float seconds)
        {
            RealtimeValue += seconds;
        }
    }
}
