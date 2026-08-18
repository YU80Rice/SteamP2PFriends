using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Client;
using SteamP2PFriends.Shared;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    ///
    ///   - 增加 Prefix：在 workshop timeout 触发、ClientTransport teardown 之前
    ///     读取客户端 connection handle 的状态（state、endReason、endDebug、flags 等）。
    ///   - 保留 Postfix：记录 vanilla 调用方传入的 reason。
    ///   - 不修改 RequestDisconnect 行为，纯观察。
    /// </summary>
    public static class DisconnectTracerPatch
    {
        public static void RegisterManual(Harmony harmony)
        {
            MethodInfo original = AccessTools.Method(typeof(Provider), "RequestDisconnect",
                new System.Type[] { typeof(string) });
            if (original == null)
            {
                RoleLogger.Error("[Shared]", "[P1-C] Provider.RequestDisconnect(string) 反射失败");
                return;
            }

            MethodInfo prefix = typeof(DisconnectTracerPatch).GetMethod(nameof(Prefix),
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo postfix = typeof(DisconnectTracerPatch).GetMethod(nameof(Postfix),
                BindingFlags.Static | BindingFlags.NonPublic);

            harmony.Patch(original,
                prefix: new HarmonyMethod(prefix),
                postfix: new HarmonyMethod(postfix));
            RoleLogger.Info("[Shared]", "[P1-C] Provider.RequestDisconnect(string) 已登记 (Prefix+Postfix read-only)");
        }

        /// <summary>
        /// vanilla 调用栈可能在 RequestDisconnect 后立即触发 ClientTransport teardown，
        /// 一旦 teardown 完成即 handle 失效。Prefix 是唯一可靠时机。
        /// </summary>
        private static void Prefix(string reason)
        {
            try
            {
                // 反射读取 Provider._clientTransport 或 serverTransport
                // 但 vanilla Provider._clientTransport 是 internal，这里只通过 SNS API 直接抓取
                // 由于 vanilla RequestDisconnect 多由客机端触发，我们记录当前已知的所有 handle 状态
                // 由 lifecycle tracker 维护的 handle 列表来兜底
                RoleLogger.Info("[Shared]",
                    $"[P1-C] Provider.RequestDisconnect Prefix reason=\"{reason}\" " +
                    $"isConnected={Provider.isConnected} isServer={Provider.isServer} " +
                    $"connectionFailureInfo={Provider.connectionFailureInfo}({(int)Provider.connectionFailureInfo})");

                // 客机端：通过 ClientTransport_SteamNetworkingSockets 反射读取 connection handle
                // 实际上 lifecycle tracker 已经在维护 handle 列表，这里只触发一次额外快照
                ConnectionLifecycleTrackerTrackerNoteForClientTransport();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Shared]", $"[P1-C] RequestDisconnect Prefix 异常（不阻断）: {ex.Message}");
            }
        }

        /// <summary>
        /// 客机端：在 RequestDisconnect Prefix 时输出当前 lifecycle tracker 中所有活跃 handle 的快照。
        /// 这能让日志直接显示：在 vanilla 触发断开时，SNS 连接处于什么状态。
        /// </summary>
        private static void ConnectionLifecycleTrackerTrackerNoteForClientTransport()
        {
            try
            {
                // 由 lifecycle tracker 暴露的 SnapshotAll 方法负责输出
                ConnectionLifecycleTracker.SnapshotAllForced("[Client]", "RequestDisconnect-Prefix");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Shared]", $"[P1-C] TrackerNoteForClientTransport 异常（不阻断）: {ex.Message}");
            }
        }

        private static void Postfix(string reason)
        {
            try
            {
                DisconnectTracer.OnVanillaRequestDisconnect(reason);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Shared]", $"[P1-C] RequestDisconnect Postfix 异常（不阻断）: {ex.Message}");
            }
        }
    }
}
