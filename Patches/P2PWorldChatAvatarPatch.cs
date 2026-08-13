using HarmonyLib;
using SDG.Unturned;
using Steamworks;
using System;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// Converts a plugin-private icon marker into the vanilla SteamPlayer avatar path immediately
    /// before either chat UI version renders the entry. Network speaker remains Nil, preserving
    /// system-announcement delivery even when the subject player is locally muted.
    /// </summary>
    [HarmonyPatch]
    internal static class P2PWorldChatAvatarPatch
    {
        private const string Prefix = "sp2pf-avatar:";

        internal static string BuildAvatarMarker(CSteamID steamId)
        {
            return steamId.IsValid() ? Prefix + steamId.m_SteamID : string.Empty;
        }

        internal static bool TryParseAvatarMarker(string value, out CSteamID steamId)
        {
            steamId = CSteamID.Nil;
            if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal))
                return false;
            if (!ulong.TryParse(value.Substring(Prefix.Length), out ulong raw)) return false;
            CSteamID parsed = new CSteamID(raw);
            if (!parsed.IsValid()) return false;
            steamId = parsed;
            return true;
        }

        internal static ReceivedChatMessage ProjectAvatar(ReceivedChatMessage message)
        {
            if (!TryParseAvatarMarker(message.iconURL, out CSteamID steamId)) return message;
            message.iconURL = string.Empty;
            try
            {
                if (!OptionsSettings.ShouldAnonymizeMultiplayerDetails)
                    message.speaker = PlayerTool.getSteamPlayer(steamId);
            }
            catch { message.speaker = null; }
            return message;
        }

        internal static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.PropertySetter(typeof(SleekChatEntryV1), "representingChatMessage");
            yield return AccessTools.PropertySetter(typeof(SleekChatEntryV2), "representingChatMessage");
        }

        [HarmonyPrefix]
        internal static void PrefixProject(ref ReceivedChatMessage value)
        {
            value = ProjectAvatar(value);
        }
    }
}
