using SDG.Unturned;
using SteamP2PFriends.Shared;
using UnityEngine;

namespace SteamP2PFriends.Client
{
    /// <summary>
    /// v0.2.3.3 P1-C：记录真实断线发起方（Codex 第四次审计外部审计报告）。
    ///
    /// 设计目标：
    ///   - 在插件所有 Provider.disconnect() / Provider.RequestDisconnect() 调用点记录 reason 和调用栈。
    ///   - Patch vanilla Provider.RequestDisconnect(string) 只读记录 reason。
    ///   - 下一次能明确区分：
    ///     * 点击排队取消按钮
    ///     * 插件主动断开
    ///     * 服务器踢出
    ///     * Steam transport 远端关闭
    ///
    /// 注意：仅记录，不阻断、不修改任何 vanilla 状态。
    /// </summary>
    internal static class DisconnectTracer
    {
        /// <summary>
        /// 客机端插件主动调用 Provider.disconnect() 的入口。
        /// 在调用 Provider.disconnect() 前调用此方法。
        /// </summary>
        internal static void TraceClientInitiated(string reason)
        {
            RoleLogger.Info("[Client]",
                $"[DisconnectTrace] 插件主动断开（client-initiated）: " +
                $"t={Time.realtimeSinceStartup:F2}s reason={reason} " +
                $"state={P2PJoinManager.State} isConnected={Provider.isConnected} " +
                $"stack=\n{GetShortStack()}");
        }

        /// <summary>
        /// vanilla Provider.RequestDisconnect(string) Postfix。
        /// 由 DisconnectTracerPatch 登记，记录 vanilla 调用方传入的 reason。
        /// </summary>
        internal static void OnVanillaRequestDisconnect(string reason)
        {
            RoleLogger.Info("[Client]",
                $"[DisconnectTrace] vanilla Provider.RequestDisconnect 被调用: " +
                $"t={Time.realtimeSinceStartup:F2}s reason={reason ?? "(null)"} " +
                $"state={P2PJoinManager.State} isConnected={Provider.isConnected} " +
                $"stack=\n{GetShortStack()}");
        }

        private static string GetShortStack()
        {
            try
            {
                System.Diagnostics.StackTrace st = new System.Diagnostics.StackTrace(2, false);
                return st.ToString();
            }
            catch
            {
                return "(stack-trace-failed)";
            }
        }
    }
}
