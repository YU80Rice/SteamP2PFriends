using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Client;

namespace SteamP2PFriends.Patches
{
    /// <summary>Client-side prediction suppression only; server InvokeMethod gate remains authoritative.</summary>
    [HarmonyPatch(typeof(PlayerInput), "FixedUpdate")]
    internal static class P2PQuarantineClientInputPatch
    {
        [HarmonyPrefix]
        internal static bool Prefix(PlayerInput __instance)
        {
            if (__instance == null || __instance.player == null ||
                __instance.player.channel == null || !__instance.player.channel.IsLocalPlayer)
                return true;
            return !P2PQuarantineClientView.IsLocalPlayerQuarantined;
        }
    }
}
