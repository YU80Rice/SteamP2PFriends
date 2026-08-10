using Steamworks;
using System;

namespace SteamP2PFriends.WhitelistTests.Fakes
{
    /// <summary>
    /// Stage 7-3 v2 单元测试 Fake：P2PJoinApprovalService.IApprovalWhitelistProxy。
    /// 蓝图 v2 §3.2 + §6.7：可控制 Contains 返回值、TryAdd 成功/失败、记录调用次数。
    /// 蓝图 v2 §6.8：Contains 可抛异常模拟 capture patch 故障路径。
    /// </summary>
    internal sealed class FakeApprovalWhitelistProxy : SteamP2PFriends.Host.IApprovalWhitelistProxy
    {
        public bool ContainsResult = false;
        public bool TryAddResult = true;
        public string TryAddFeedback = "添加成功";
        public Exception ThrowOnContains;

        public int ContainsCallCount;
        public int TryAddCallCount;
        public CSteamID LastTryAddTarget;
        public string LastTryAddTag;

        public bool Contains(CSteamID target)
        {
            ContainsCallCount++;
            if (ThrowOnContains != null) throw ThrowOnContains;
            return ContainsResult;
        }

        public bool TryAdd(CSteamID target, string tag, out string feedback)
        {
            TryAddCallCount++;
            LastTryAddTarget = target;
            LastTryAddTag = tag;
            feedback = TryAddFeedback;
            return TryAddResult;
        }
    }
}
