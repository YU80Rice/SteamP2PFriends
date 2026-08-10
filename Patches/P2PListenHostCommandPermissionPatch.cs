using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using System;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// Vanilla ChatManager permits every player to execute slash commands whenever the process is
    /// not dedicated. Listen-host is non-dedicated, so enforce the missing remote-player permission gate.
    /// </summary>
    internal static class P2PListenHostCommandPermissionPatch
    {
        private const int BlockLogLimit = 32;
        private static int _blockedLogCount;

        internal static bool RegistrationValid { get; private set; }

        internal static MethodInfo TargetMethod()
        {
            return AccessTools.Method(typeof(ChatManager), nameof(ChatManager.process),
                new[] { typeof(SteamPlayer), typeof(string), typeof(bool) });
        }

        internal static void RegisterManual(Harmony harmony)
        {
            RegistrationValid = false;
            MethodInfo original = TargetMethod();
            MethodInfo prefix = AccessTools.Method(typeof(P2PListenHostCommandPermissionPatch), nameof(Prefix));
            if (harmony == null || original == null || prefix == null)
            {
                RoleLogger.Error("[Shared]", "[P2P-CommandGate] target or Prefix unresolved");
                return;
            }

            HarmonyLib.Patches existing = Harmony.GetPatchInfo(original);
            bool alreadyPatched = false;
            if (existing != null)
            {
                foreach (Patch patch in existing.Prefixes)
                {
                    if (patch.owner == SteamP2PFriendsPlugin.HARMONY_ID && patch.PatchMethod == prefix)
                    {
                        alreadyPatched = true;
                        break;
                    }
                }
            }

            if (!alreadyPatched)
                harmony.Patch(original, prefix: new HarmonyMethod(prefix));

            existing = Harmony.GetPatchInfo(original);
            if (existing != null)
            {
                foreach (Patch patch in existing.Prefixes)
                {
                    if (patch.owner == SteamP2PFriendsPlugin.HARMONY_ID && patch.PatchMethod == prefix)
                    {
                        RegistrationValid = true;
                        break;
                    }
                }
            }

            if (RegistrationValid)
                RoleLogger.Info("[Shared]", "[P2P-CommandGate] OK ChatManager.process remote permission gate registered");
            else
                RoleLogger.Error("[Shared]", "[P2P-CommandGate] registration verification failed");
        }

        internal static void ResetForSession()
        {
            _blockedLogCount = 0;
        }

        internal static bool ShouldBlock(bool isP2PHostMode, bool isServer, bool isLocalHost,
            bool hasCheats, bool isAdmin, string command)
        {
            if (!isP2PHostMode || !isServer || isLocalHost || String.IsNullOrEmpty(command)) return false;
            char first = command[0];
            if (first != '/' && first != '@') return false;
            return !hasCheats || !isAdmin;
        }

        internal static bool Prefix(SteamPlayer player, string cmd, ref bool __result)
        {
            ThreadUtil.assertIsGameThread();
            if (ReferenceEquals(player, null) || ReferenceEquals(player.playerID, null)) return true;

            bool isLocalHost = player.playerID.steamID == Provider.user;
            if (!ShouldBlock(HostManager.IsP2PHostMode, Provider.isServer, isLocalHost,
                    Provider.hasCheats, player.isAdmin, cmd))
                return true;

            __result = false;
            if (_blockedLogCount++ < BlockLogLimit)
            {
                RoleLogger.Warn("[Host]",
                    "[P2P-CommandGate] blocked remote command channel=" + (byte)player.channel +
                    " cheats=" + Provider.hasCheats + " admin=" + player.isAdmin +
                    " token=" + GetSafeCommandToken(cmd));
            }
            return false;
        }

        private static string GetSafeCommandToken(string command)
        {
            if (String.IsNullOrEmpty(command)) return "empty";
            int end = command.IndexOf(' ');
            string token = end > 0 ? command.Substring(0, end) : command;
            if (token.Length > 32) token = token.Substring(0, 32);
            char[] chars = token.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (Char.IsControl(chars[i])) chars[i] = '?';
            }
            return new string(chars);
        }
    }
}
