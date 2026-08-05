using HarmonyLib;
using SDG.NetTransport;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using Steamworks;
using UnityEngine;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.1 P0-D D-12 + P0-E E-1：SteamPlayer 构造完成 Postfix 诊断 patch。
    ///
    /// v0.2.3.1 P0-1 修复（审计要求）：
    ///   - SteamPlayer 只有一个大型有参构造器，无无参构造器。
    ///   - 原 [HarmonyPatch(typeof(SteamPlayer), MethodType.Constructor)] 未指定 argumentTypes，
    ///     Harmony 默认查找无参构造器，目标会解析为 null。
    ///   - 精确指定完整参数类型数组。
    ///
    /// v0.2.3.1 P0-4 修复（审计要求）：
    ///   - 纯诊断构建：本 patch 仅观察记录，不修改 IsLocalServerHost。
    ///   - P0-C IsLocalServerHost 修正移到 v0.2.3-p0c-ab 第二构建。
    ///   - E-1 MarkConstructed 保留（仅状态表登记，不修改行为）。
    /// </summary>
    [HarmonyPatch(typeof(SteamPlayer), MethodType.Constructor, new System.Type[] {
        typeof(ITransportConnection), typeof(NetId), typeof(SteamPlayerID), typeof(Transform),
        typeof(bool), typeof(bool), typeof(int), typeof(byte), typeof(byte), typeof(byte),
        typeof(Color), typeof(Color), typeof(Color), typeof(Color), typeof(bool),
        typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int),
        typeof(int[]), typeof(string[]), typeof(string[]),
        typeof(EPlayerSkillset), typeof(string), typeof(CSteamID), typeof(EClientPlatform)
    })]
    public static class SteamPlayerConstructorDiagnosticPatch
    {
        [HarmonyPostfix]
        public static void Postfix(SteamPlayer __instance, ITransportConnection transportConnection, NetId netId, SteamPlayerID newPlayerID)
        {
            try
            {
                ulong steamId = newPlayerID.steamID.m_SteamID;
                string transportType = transportConnection?.GetType().Name ?? "null";
                bool vanillaIsLocalServerHost = __instance.IsLocalServerHost;
                bool isLoopback = transportType == "TransportConnection_Loopback" ||
                                  transportType == "LoopbackTransportConnection";
                bool isLocalPlayerId = newPlayerID.steamID == Provider.client;

                RoleLogger.Info("[Host]",
                    $"{DiagnosticContext.FormatPrefix("SteamPlayer.ctor EXIT")} " +
                    $"steamId={steamId} name=\"{newPlayerID.playerName}\" netId={netId.id} " +
                    $"transport={transportType} isLoopback={isLoopback} " +
                    $"vanillaIsLocalServerHost={vanillaIsLocalServerHost} " +
                    $"isLocalPlayerId={isLocalPlayerId} " +
                    $"expected_IsLocalServerHost={(isLoopback && isLocalPlayerId)}");

                // E-1：登记到初始化状态表（仅状态表登记，不修改 vanilla 行为）
                if (__instance.player != null)
                {
                    PlayerInitializationTracker.MarkConstructed(__instance.player);
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Host]", $"[Diag] SteamPlayer.ctor Postfix 异常（不阻断）: {ex.Message}");
            }
        }
    }
}
