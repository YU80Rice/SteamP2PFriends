using HarmonyLib;
using SDG.NetTransport;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using Steamworks;
using UnityEngine;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    ///
    ///   - 真实签名 30 参数：Vector3 point + byte angle，非 Transform。
    ///   - 之前误用 SteamPlayer 构造器签名，PatchAll 必定失败。
    ///
    ///   - 使用 FormatPrefixFor 输出精确 sid。
    ///   - 仅 addPlayer 调用 EnsureHostJoinSession（accept Prefix 不再调用）。
    /// </summary>
    [HarmonyPatch(typeof(Provider), "addPlayer", new System.Type[] {
        typeof(ITransportConnection), typeof(NetId), typeof(SteamPlayerID),
        typeof(Vector3), typeof(byte),
        typeof(bool), typeof(bool), typeof(int),
        typeof(byte), typeof(byte), typeof(byte),
        typeof(Color), typeof(Color), typeof(Color), typeof(Color), typeof(bool),
        typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int),
        typeof(int[]), typeof(string[]), typeof(string[]),
        typeof(EPlayerSkillset), typeof(string), typeof(CSteamID), typeof(EClientPlatform)
    })]
    public static class ProviderAddPlayerDiagnosticPatch
    {
        [HarmonyPrefix]
        public static void Prefix(ITransportConnection transportConnection, NetId netId, SteamPlayerID playerID,
            Vector3 point, byte angle)
        {
            try
            {
                ulong remoteSteamId = playerID.steamID.m_SteamID;
                DiagnosticContext.EnsureHostJoinSession(remoteSteamId);

                string transportType = transportConnection?.GetType().Name ?? "null";

                bool duplicateSteamId = false;
                if (Provider.clients != null)
                {
                    foreach (SteamPlayer existing in Provider.clients)
                    {
                        if (ReferenceEquals(existing, null) || ReferenceEquals(existing.playerID, null)) continue;
                        if (existing.playerID.steamID.m_SteamID == remoteSteamId)
                        {
                            duplicateSteamId = true;
                            break;
                        }
                    }
                }

                RoleLogger.Info("[Host]",
                    $"{DiagnosticContext.FormatPrefixFor(remoteSteamId, "addPlayer ENTER")} " +
                    $"remoteSteamId={remoteSteamId} netId={netId.id} transport={transportType} " +
                    $"point=({point.x:F2},{point.y:F2},{point.z:F2}) angle={angle} " +
                    $"clients_before={Provider.clients?.Count ?? -1} duplicateSteamId={duplicateSteamId}");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Host]", $"[Diag] addPlayer Prefix 异常（不阻断）: {ex.Message}");
            }
        }

        [HarmonyPostfix]
        public static void Postfix(ITransportConnection transportConnection, NetId netId, SteamPlayerID playerID, SteamPlayer __result)
        {
            try
            {
                ulong remoteSteamId = playerID.steamID.m_SteamID;
                string transportType = transportConnection?.GetType().Name ?? "null";

                int modelInstanceId = -1;
                bool isLocalServerHost = false;
                if (!ReferenceEquals(__result, null))
                {
                    if (__result.model != null)
                    {
                        modelInstanceId = __result.model.GetInstanceID();
                    }
                    isLocalServerHost = __result.IsLocalServerHost;
                }

                RoleLogger.Info("[Host]",
                    $"{DiagnosticContext.FormatPrefixFor(remoteSteamId, "addPlayer RETURNED")} " +
                    $"remoteSteamId={remoteSteamId} netId={netId.id} transport={transportType} " +
                    $"clients_after={Provider.clients?.Count ?? -1} " +
                    $"result_null={ReferenceEquals(__result, null)} " +
                    $"modelInstanceId={modelInstanceId} IsLocalServerHost={isLocalServerHost}");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Host]", $"[Diag] addPlayer Postfix 异常（不阻断）: {ex.Message}");
            }
        }
    }
}
