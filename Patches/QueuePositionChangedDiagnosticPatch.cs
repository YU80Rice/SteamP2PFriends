using HarmonyLib;
using SDG.NetPak;
using SDG.Unturned;
using SteamP2PFriends.Client;
using SteamP2PFriends.Shared;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.3 P0-B：ClientMessageHandler_QueuePositionChanged.ReadMessage Postfix。
    ///
    /// 设计目标：
    ///   - QueuePositionChanged 消息到达时，触发 NativeLoadingGateDumper.Dump。
    ///   - 调用 P2PJoinManager.NotifyQueuePositionChanged 记录状态。
    ///   - 纯观察，不修改 vanilla 状态。
    /// </summary>
    public static class QueuePositionChangedDiagnosticPatch
    {
        public static void RegisterManual(Harmony harmony)
        {
            // ClientMessageHandler_QueuePositionChanged 是 internal static class
            System.Type handlerType = AccessTools.TypeByName("SDG.Unturned.ClientMessageHandler_QueuePositionChanged");
            if (handlerType == null)
            {
                RoleLogger.Error("[Shared]", "[P0-B] ClientMessageHandler_QueuePositionChanged 反射失败");
                return;
            }

            MethodInfo original = AccessTools.Method(handlerType, "ReadMessage",
                new System.Type[] { typeof(NetPakReader) });
            if (original == null)
            {
                RoleLogger.Error("[Shared]", "[P0-B] ClientMessageHandler_QueuePositionChanged.ReadMessage 反射失败");
                return;
            }

            MethodInfo postfix = typeof(QueuePositionChangedDiagnosticPatch).GetMethod(
                nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic);

            harmony.Patch(original, postfix: new HarmonyMethod(postfix));
            RoleLogger.Info("[Shared]", "[P0-B] ClientMessageHandler_QueuePositionChanged.ReadMessage 已登记 (Postfix only-read)");
        }

        private static void Postfix()
        {
            try
            {
                byte pos = Provider.queuePosition;
                P2PJoinManager.NotifyQueuePositionChanged(pos);
                // Dump 已在 P2PJoinManager.NotifyQueuePositionChanged 内部触发
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Shared]", $"[P0-B] QueuePositionChanged Postfix 异常（不阻断）: {ex.Message}");
            }
        }
    }
}
