using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using Steamworks;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// Route B handshake patch. It permits only an otherwise unwhitelisted guest to continue
    /// through the native ReadyToConnect pipeline; all non-whitelist native checks are preserved.
    /// World-entry quarantine is intentionally deferred to Provider.onServerConnected.
    /// </summary>
    [HarmonyPatch(typeof(SteamWhitelist), nameof(SteamWhitelist.checkWhitelisted))]
    internal static class Patch_ServerConnectValidation
    {
        [HarmonyPostfix]
        internal static void Postfix(CSteamID __0, ref bool __result)
        {
            if (__result || !P2PApprovalManager.CanPermitHandshake(__0)) return;
            __result = true;
            RoleLogger.Info("[Host]", "[P2P-Approval] Route B handshake permit: steamId=" + __0.m_SteamID);
        }
    }
}
