using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Client;
using SteamP2PFriends.Shared;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.3 第四次审计 P0-A 修复（Codex 外部审计）：纯观察，删除所有 PlayerInitializationTracker 调用。
    /// v0.2.3.3 P0-A：删除 serverBoundsHistory 引用（该字段为服务器端专属，客机永远为 null）。
    /// 状态机所有权归 InitializePlayerStatePatch（PlayerUpdateGuardPatch.cs）独占。
    /// 本 patch 仅输出 ENTER/RETURNED/THREW 诊断日志 + __state 透传。
    /// v0.2.3.3 P1-A：所有 [Host] 改为 RoleLogger.ResolveDynamicRole()。
    /// v0.2.3.3 P0-B：本地 Player.InitializePlayer Postfix 触发 NativeLoadingGateDumper.Dump。
    /// </summary>
    [HarmonyPatch(typeof(Player), "InitializePlayer")]
    public static class PlayerInitializeDiagnosticPatch
    {
        /// <summary>
        /// 纯观察 Prefix：void 签名，不拦截原方法，不写 tracker。
        /// </summary>
        [HarmonyPrefix]
        public static void Prefix(Player __instance, ref bool __state)
        {
            try
            {
                ulong steamId = 0;
                uint netId = 0;
                string playerName = "n/a";
                if (!ReferenceEquals(__instance.channel?.owner, null) &&
                    !ReferenceEquals(__instance.channel.owner.playerID, null))
                {
                    steamId = __instance.channel.owner.playerID.steamID.m_SteamID;
                    playerName = __instance.channel.owner.playerID.playerName ?? "n/a";
                    try { netId = __instance.channel.owner.GetNetId().id; } catch { }
                }

                bool isLocalPlayer = __instance.channel?.IsLocalPlayer ?? false;

                RoleLogger.Info(RoleLogger.ResolveDynamicRole(),
                    $"{DiagnosticContext.FormatPrefix("Player.InitializePlayer ENTER")} " +
                    $"steamId={steamId} name=\"{playerName}\" netId={netId} " +
                    $"isLocalPlayer={isLocalPlayer} isServer={Provider.isServer}");

                __state = true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn(RoleLogger.ResolveDynamicRole(),
                    $"[Diag] Player.InitializePlayer Prefix 异常（不阻断）: {ex.Message}");
                __state = false;
            }
        }

        /// <summary>
        /// 纯观察 Postfix：仅记录 RETURNED 日志，不写 tracker。
        /// v0.2.3.3 P0-A：删除 serverBoundsHistory 引用。
        /// v0.2.3.3 P0-B：本地 Player.InitializePlayer Postfix 触发 NativeLoadingGateDumper.Dump。
        /// </summary>
        [HarmonyPostfix]
        public static void Postfix(Player __instance, bool __state)
        {
            if (!__state)
            {
                return;
            }

            try
            {
                ulong steamId = 0;
                if (!ReferenceEquals(__instance.channel?.owner, null) &&
                    !ReferenceEquals(__instance.channel.owner.playerID, null))
                {
                    steamId = __instance.channel.owner.playerID.steamID.m_SteamID;
                }

                bool isLocalPlayer = __instance.channel?.IsLocalPlayer ?? false;

                RoleLogger.Info(RoleLogger.ResolveDynamicRole(),
                    $"{DiagnosticContext.FormatPrefix("Player.InitializePlayer RETURNED")} " +
                    $"steamId={steamId} isLocalPlayer={isLocalPlayer} isServer={Provider.isServer}");

                // v0.2.3.3 P0-B：本地 Player.InitializePlayer Postfix 触发加载门快照
                if (isLocalPlayer && !Provider.isServer)
                {
                    NativeLoadingGateDumper.Dump("Player.InitializePlayer-Postfix(localPlayer)");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn(RoleLogger.ResolveDynamicRole(),
                    $"[Diag] Player.InitializePlayer Postfix 异常（不阻断）: {ex.Message}");
            }
        }

        /// <summary>
        /// Finalizer：void 签名，不吞异常。仅记录日志，不写 tracker。
        /// </summary>
        [HarmonyFinalizer]
        public static void Finalizer(Player __instance, System.Exception __exception)
        {
            try
            {
                ulong steamId = 0;
                if (!ReferenceEquals(__instance?.channel?.owner, null) &&
                    !ReferenceEquals(__instance.channel.owner.playerID, null))
                {
                    steamId = __instance.channel.owner.playerID.steamID.m_SteamID;
                }

                if (__exception != null)
                {
                    RoleLogger.Error(RoleLogger.ResolveDynamicRole(),
                        $"{DiagnosticContext.FormatPrefix("Player.InitializePlayer THREW")} " +
                        $"steamId={steamId} exceptionType={__exception.GetType().Name} " +
                        $"message={__exception.Message}");
                    RoleLogger.Error(RoleLogger.ResolveDynamicRole(),
                        $"[Diag] Player.InitializePlayer stack:\n{__exception.StackTrace}");
                }
                else
                {
                    RoleLogger.Info(RoleLogger.ResolveDynamicRole(),
                        $"{DiagnosticContext.FormatPrefix("Player.InitializePlayer OK (no exception)}")} " +
                        $"steamId={steamId}");
                }
            }
            catch
            {
                // Finalizer 内部异常不得影响 原异常传播
            }
            // void 签名，保留原异常
        }
    }
}
