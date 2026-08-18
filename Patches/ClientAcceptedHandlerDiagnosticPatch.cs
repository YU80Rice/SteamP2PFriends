using HarmonyLib;
using SDG.NetPak;
using SDG.Unturned;
using SteamP2PFriends.Client;
using SteamP2PFriends.Shared;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    ///
    /// 审计员要求：
    ///   - 标记 ServerAccepted（不推进到 GameplayReady）。
    ///
    /// ClientMessageHandler_Accepted.ReadMessage 是客机收到 Accepted 消息的入口。
    /// vanilla 在此方法中调用 Provider.onClientConnected.Invoke()，但审计员明确：
    ///   "Provider.onClientConnected 只推进到 ServerAccepted，不代表 Gameplay 完成。"
    ///
    /// 实现说明：
    ///   - ClientMessageHandler_Accepted 是 internal static partial class，
    ///     编译时无法用 typeof() 访问。
    ///   - 本类不使用 [HarmonyPatch] 特性，改由 Plugin.ApplyManualWrapperPatches()
    ///     在运行时通过反射手动登记。
    ///   - Postfix 方法签名与 ReadMessage 一致：
    ///       internal static void ReadMessage(NetPakReader reader)
    /// </summary>
    public static class ClientAcceptedHandlerDiagnosticPatch
    {
        /// <summary>
        /// Postfix for ClientMessageHandler_Accepted.ReadMessage
        /// 注意：本 patch 是客机侧，主机不会触发。
        /// </summary>
        public static void ReadMessage_Postfix(NetPakReader reader)
        {
            try
            {
                RoleLogger.Info("[Client]",
                    $"{DiagnosticContext.FormatPrefix("ClientMessageHandler_Accepted.ReadMessage EXIT")} " +
                    $"server={Provider.server.m_SteamID} isClient={Provider.isClient} isConnected={Provider.isConnected}");

                NativeLoadingGateDumper.StartPostAcceptedTracking();
                NativeLoadingGateDumper.Dump("ClientMessageHandler_Accepted.ReadMessage-Postfix");

                // 推进 P2PJoinManager 状态到 ServerAccepted（仅诊断，不推进 GameplayReady）
                P2PJoinManager.NotifyServerAccepted();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Client]", $"[Diag] Accepted.Postfix 异常（不阻断）: {ex.Message}");
            }
        }

        /// <summary>
        /// 由 Plugin.ApplyManualWrapperPatches() 调用，通过反射手动登记 patch。
        /// </summary>
        public static void RegisterManual(Harmony harmony)
        {
            try
            {
                System.Type handlerType = AccessTools.TypeByName("SDG.Unturned.ClientMessageHandler_Accepted");
                if (handlerType == null)
                {
                    RoleLogger.Error("[Shared]", "[ManualPatch] !!! ClientMessageHandler_Accepted type not found");
                    return;
                }

                MethodInfo original = AccessTools.Method(handlerType, "ReadMessage",
                    new System.Type[] { typeof(NetPakReader) });
                if (original == null)
                {
                    RoleLogger.Warn("[Shared]",
                        "[ManualPatch] !!! ClientMessageHandler_Accepted.ReadMessage: method not found");
                    return;
                }

                MethodInfo postfix = AccessTools.Method(
                    typeof(ClientAcceptedHandlerDiagnosticPatch), nameof(ReadMessage_Postfix));
                if (postfix == null)
                {
                    RoleLogger.Error("[Shared]", "[ManualPatch] !!! ReadMessage_Postfix method not found");
                    return;
                }

                harmony.Patch(original, postfix: new HarmonyMethod(postfix));

                HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
                int postfixCount = info?.Postfixes?.Count ?? 0;
                RoleLogger.Info("[Shared]",
                    $"[ManualPatch] OK ClientMessageHandler_Accepted.ReadMessage 已登记 " +
                    $"(postfixes={postfixCount})");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[ManualPatch] ClientMessageHandler_Accepted 注册失败: {ex}");
            }
        }
    }
}
