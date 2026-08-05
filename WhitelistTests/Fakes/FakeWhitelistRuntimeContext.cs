using SteamP2PFriends.Host;
using Steamworks;

namespace SteamP2PFriends.WhitelistTests.Fakes
{
    /// <summary>
    /// Stage 7-2-2 单元测试 fake：IWhitelistRuntimeContext。
    /// 蓝图 §3：不启动 Unturned、不触碰 Provider/Steam API/Unity/文件系统。
    /// AssertGameThread 为 no-op；IsActiveP2PHost / LocalUser / SetWhitelisted 由测试控制。
    /// </summary>
    internal sealed class FakeWhitelistRuntimeContext : IWhitelistRuntimeContext
    {
        public int AssertGameThreadCount;
        public int SetWhitelistedCount;
        public bool LastWhitelistedValue;
        public bool IsActiveP2PHostValue = true;
        public CSteamID LocalUserValue;

        public void AssertGameThread()
        {
            AssertGameThreadCount++;
            // no-op：不调 ThreadUtil
        }

        public bool IsActiveP2PHost => IsActiveP2PHostValue;

        public CSteamID LocalUser => LocalUserValue;

        public void SetWhitelisted(bool value)
        {
            SetWhitelistedCount++;
            LastWhitelistedValue = value;
            // 不触碰 Provider.isWhitelisted
        }
    }
}
