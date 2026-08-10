using HarmonyLib;
using SDG.NetTransport;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using SteamP2PFriends.Shared.Enums;
using Steamworks;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// LAN 测试模式重复 Steam ID 绕过 3 合 1（对齐原版 HarmonyPatches.cs:125-187）。
    ///
    /// 三个 patch 配合实现"同账号双开"（仅 HostMode == LAN 时启用）：
    ///   1. LanTestReadyToConnectPatch - 动态 TargetMethod 定位 ServerMessageHandler_ReadyToConnect.ReadMessage
    ///      Prefix 调 BeginLanJoinDuplicateCheckBypass(depth++)，Finalizer 调 EndLanJoinDuplicateCheckBypass(depth--)
    ///   2. LanTestPendingPlayerPatch - Provider.findPendingPlayerBySteamId Postfix，bypass 时 __result = null
    ///   3. LanTestSteamPlayerPatch - PlayerTool.getSteamPlayer(CSteamID) Postfix，bypass 时 __result = null
    ///
    /// 启用条件：HostMode == EHostMode.LAN（P2P 模式不启用，不同 Steam 账号本来就不会撞 ID）
    /// </summary>
    public static class LanTestDuplicateBypassPatch
    {
        /// <summary>
        /// 动态定位 ServerMessageHandler_ReadyToConnect.ReadMessage（vanilla internal 类，需反射）。
        /// </summary>
        [HarmonyPatch]
        public static class LanTestReadyToConnectPatch
        {
            public static MethodBase TargetMethod()
            {
                MethodInfo method = ReflectionUtil.FindStaticMethod(
                    "SDG.Unturned.ServerMessageHandler_ReadyToConnect",
                    "ReadMessage");

                if (method == null)
                {
                    RoleLogger.Warn("[Shared]", "[LAN-Test] ReadyToConnect.ReadMessage 未找到；LAN duplicate Steam bypass 仅 pending/player 生效");
                }
                else
                {
                    RoleLogger.InfoVerbose("[Shared]", "[LAN-Test] ReadyToConnect patch target resolved");
                }

                return method;
            }

            [HarmonyPrefix]
            public static void Prefix(ITransportConnection transportConnection)
            {
                P2PQuarantineReadyToConnectScope.Enter(transportConnection);
                if (HostManager.HostMode == EHostMode.LAN)
                {
                    HostManager.BeginLanJoinDuplicateCheckBypass();
                    RoleLogger.InfoVerbose("[Host]", $"[LAN-Test] ReadyToConnect from {transportConnection} (bypass on)");
                }
            }

            [HarmonyFinalizer]
            public static void Finalizer()
            {
                try
                {
                    HostManager.EndLanJoinDuplicateCheckBypass();
                }
                finally
                {
                    P2PQuarantineReadyToConnectScope.Exit();
                }
            }
        }

        [HarmonyPatch(typeof(Provider), "findPendingPlayerBySteamId")]
        [HarmonyPostfix]
        public static void LanTestPendingPlayer_Postfix(ref SteamPending __result)
        {
            if (HostManager.ShouldBypassDuplicateSteamCheck() && __result != null)
            {
                __result = null;
            }
        }

        [HarmonyPatch(typeof(PlayerTool), "getSteamPlayer", new[] { typeof(CSteamID) })]
        [HarmonyPostfix]
        public static void LanTestSteamPlayer_Postfix(ref SteamPlayer __result)
        {
            if (HostManager.ShouldBypassDuplicateSteamCheck() && __result != null)
            {
                __result = null;
            }
        }
    }
}
