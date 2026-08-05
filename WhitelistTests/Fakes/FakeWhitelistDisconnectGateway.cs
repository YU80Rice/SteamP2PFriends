using SteamP2PFriends.Host;

namespace SteamP2PFriends.WhitelistTests.Fakes
{
    /// <summary>
    /// Stage 7-2-2 单元测试 fake：IWhitelistDisconnectGateway。
    /// 蓝图 §3：不启动 Unturned、不触碰 Provider.disconnect()。
    /// 仅记录调用次数，不执行真实断开。
    /// </summary>
    internal sealed class FakeWhitelistDisconnectGateway : IWhitelistDisconnectGateway
    {
        public int DisconnectCallCount;

        public void DisconnectCurrentP2PHost()
        {
            DisconnectCallCount++;
            // 不调 Provider.disconnect()
        }
    }
}
