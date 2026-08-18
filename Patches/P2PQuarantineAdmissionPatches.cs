using HarmonyLib;
using SDG.NetTransport;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using Steamworks;
using System;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    internal static class P2PQuarantineReadyToConnectScope
    {
        [ThreadStatic] private static ITransportConnection _transport;
        [ThreadStatic] private static int _depth;

        internal static void Enter(ITransportConnection transport)
        {
            if (_depth++ == 0) _transport = transport;
        }

        internal static void Exit()
        {
            if (_depth <= 0)
            {
                _depth = 0;
                _transport = null;
                return;
            }
            if (--_depth == 0) _transport = null;
        }

        internal static bool TryGetTransport(out ITransportConnection transport)
        {
            transport = _depth == 1 ? _transport : null;
            return transport != null;
        }

        internal static int DepthForTest => _depth;
    }

    internal static class P2PQuarantineReadyToConnectScopePatch
    {
        internal const string TargetTypeName = "SDG.Unturned.ServerMessageHandler_ReadyToConnect";
        internal static bool RegistrationValid { get; private set; }

        internal static MethodInfo GetTargetMethod()
        {
            Type targetType = AccessTools.TypeByName(TargetTypeName);
            Type readerType = AccessTools.TypeByName("SDG.NetPak.NetPakReader");
            return targetType == null || readerType == null ? null :
                AccessTools.Method(targetType, "ReadMessage", new[] { typeof(ITransportConnection), readerType });
        }

        internal static void RegisterManual(Harmony harmony)
        {
            RegistrationValid = false;
            MethodInfo original = GetTargetMethod();
            MethodInfo prefix = AccessTools.Method(typeof(P2PQuarantineReadyToConnectScopePatch), nameof(Prefix));
            MethodInfo finalizer = AccessTools.Method(typeof(P2PQuarantineReadyToConnectScopePatch), nameof(Finalizer));
            if (original == null || prefix == null || finalizer == null)
            {
                RoleLogger.Error("[Shared]", "[P2P-Quarantine] ReadyToConnect scope target unresolved");
                return;
            }

            if (!HasOwnedPatch(original, prefix, true) || !HasOwnedPatch(original, finalizer, false, true))
                harmony.Patch(original, prefix: new HarmonyMethod(prefix), finalizer: new HarmonyMethod(finalizer));

            RegistrationValid = HasOwnedPatch(original, prefix, true) && HasOwnedPatch(original, finalizer, false, true);
            if (!RegistrationValid)
                RoleLogger.Error("[Shared]", "[P2P-Quarantine] ReadyToConnect scope registration failed");
        }

        internal static void Prefix(ITransportConnection transportConnection)
        {
            P2PQuarantineReadyToConnectScope.Enter(transportConnection);
        }

        internal static void Finalizer()
        {
            P2PQuarantineReadyToConnectScope.Exit();
        }

        private static bool HasOwnedPatch(MethodBase original, MethodInfo expected,
            bool prefix, bool finalizer = false)
        {
            HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
            if (info == null) return false;
            System.Collections.Generic.IEnumerable<Patch> patches = finalizer
                ? info.Finalizers
                : (prefix ? info.Prefixes : info.Postfixes);
            foreach (Patch patch in patches)
            {
                if (patch.owner == SteamP2PFriendsPlugin.HARMONY_ID && patch.PatchMethod == expected)
                    return true;
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(SteamWhitelist), nameof(SteamWhitelist.checkWhitelisted))]
    internal static class P2PQuarantineWhitelistPermitPatch
    {
        [HarmonyPostfix]
        internal static void Postfix(CSteamID __0, ref bool __result)
        {
            if (__result) return;
            try
            {
                ThreadUtil.assertIsGameThread();
                if (!P2PQuarantineReadyToConnectScope.TryGetTransport(out ITransportConnection transport))
                    return;
                // Bind by argument index rather than the game's parameter name. Unturned currently
                // calls this parameter "steamID"; relying on a source-level name makes Harmony fail
                // during startup whenever the SDK/game metadata differs.
                __result = P2PQuarantineAdmissionService.TryReserve(__0, transport);
            }
            catch (Exception ex)
            {
                __result = false;
                RoleLogger.Error("[Host]",
                    "[P2P-Quarantine] permit reservation failed closed: " + ex.GetType().Name);
            }
        }
    }

    internal static class P2PQuarantineServerInvokeGatePatch
    {
        internal const string TargetTypeName = "SDG.Unturned.ServerMessageHandler_InvokeMethod";
        internal static bool RegistrationValid { get; private set; }

        internal static void RegisterManual(Harmony harmony)
        {
            RegistrationValid = false;
            Type targetType = AccessTools.TypeByName(TargetTypeName);
            Type readerType = AccessTools.TypeByName("SDG.NetPak.NetPakReader");
            MethodInfo original = targetType == null || readerType == null ? null :
                AccessTools.Method(targetType, "ReadMessage", new[] { typeof(ITransportConnection), readerType });
            MethodInfo prefix = AccessTools.Method(typeof(P2PQuarantineServerInvokeGatePatch), nameof(Prefix));
            if (original == null || prefix == null)
            {
                RoleLogger.Error("[Shared]", "[P2P-Quarantine] InvokeMethod gate target unresolved");
                return;
            }

            harmony.Patch(original, prefix: new HarmonyMethod(prefix));
            HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
            if (info != null)
            {
                foreach (Patch patch in info.Prefixes)
                {
                    if (patch.owner == SteamP2PFriendsPlugin.HARMONY_ID && patch.PatchMethod == prefix)
                    {
                        RegistrationValid = true;
                        break;
                    }
                }
            }
            if (!RegistrationValid)
                RoleLogger.Error("[Shared]", "[P2P-Quarantine] InvokeMethod gate registration failed");
        }

        internal static bool Prefix(ITransportConnection transportConnection)
        {
            ThreadUtil.assertIsGameThread();
            SteamPlayer caller = Provider.findPlayer(transportConnection);
            if (ReferenceEquals(caller, null) || ReferenceEquals(caller.playerID, null)) return true;
            return !P2PQuarantineAdmissionService.IsActive(caller.playerID.steamID);
        }
    }

    [HarmonyPatch]
    internal static class P2PQuarantineDamageGuardPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(PlayerLife), nameof(PlayerLife.askDamage), new Type[]
            {
                typeof(byte), typeof(UnityEngine.Vector3), typeof(EDeathCause), typeof(ELimb),
                typeof(CSteamID), typeof(EPlayerKill).MakeByRefType(), typeof(bool),
                typeof(ERagdollEffect), typeof(bool), typeof(bool)
            });
        }

        [HarmonyPrefix]
        internal static bool Prefix(PlayerLife __instance, ref EPlayerKill kill)
        {
            kill = EPlayerKill.NONE;
            if (!Provider.isServer || __instance == null || __instance.player == null ||
                __instance.player.channel == null || __instance.player.channel.owner == null)
                return true;

            CSteamID target = __instance.player.channel.owner.playerID.steamID;
            return !P2PQuarantineAdmissionService.IsActive(target);
        }
    }
}
