using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using Steamworks;

namespace SteamP2PFriends.WhitelistTests
{
    internal static class HarmonyCompatibilityAuditTests
    {
        private const string OwnTestOwner = SteamP2PFriendsPlugin.HARMONY_ID;
        private const string ForeignTestOwner = "example.foreign.compatibility.test";

        public static void ObserverTarget()
        {
        }

        public static void ObserverPrefix()
        {
        }

        public static void ObserverPostfix()
        {
        }

        public static IEnumerable<CodeInstruction> NoOpTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            return instructions;
        }

        internal static bool Test_ObserverPatch_IsRecordedWithoutBlocking()
        {
            MethodInfo target = typeof(HarmonyCompatibilityAuditTests).GetMethod(
                nameof(ObserverTarget), BindingFlags.Public | BindingFlags.Static);
            MethodInfo ownPrefix = typeof(HarmonyCompatibilityAuditTests).GetMethod(
                nameof(ObserverPrefix), BindingFlags.Public | BindingFlags.Static);
            MethodInfo foreignPostfix = typeof(HarmonyCompatibilityAuditTests).GetMethod(
                nameof(ObserverPostfix), BindingFlags.Public | BindingFlags.Static);
            if (target == null || ownPrefix == null || foreignPostfix == null) return false;

            var own = new Harmony(OwnTestOwner);
            var foreign = new Harmony(ForeignTestOwner);
            try
            {
                own.Patch(target, prefix: new HarmonyMethod(ownPrefix));
                foreign.Patch(target, postfix: new HarmonyMethod(foreignPostfix));

                HarmonyCompatibilityAudit.Reset();
                return HarmonyCompatibilityAudit.Inspect(target, "test-observer") &&
                       HarmonyCompatibilityAudit.CountOwned(Harmony.GetPatchInfo(target).Prefixes) == 1;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { own.Unpatch(target, HarmonyPatchType.All, OwnTestOwner); } catch { }
                try { foreign.Unpatch(target, HarmonyPatchType.All, ForeignTestOwner); } catch { }
                HarmonyCompatibilityAudit.Reset();
            }
        }

        internal static bool Test_ForeignTranspiler_OnOwnTranspiledTarget_Blocks()
        {
            MethodInfo target = typeof(HarmonyCompatibilityAuditTests).GetMethod(
                nameof(ObserverTarget), BindingFlags.Public | BindingFlags.Static);
            MethodInfo transpiler = typeof(HarmonyCompatibilityAuditTests).GetMethod(
                nameof(NoOpTranspiler), BindingFlags.Public | BindingFlags.Static);
            if (target == null || transpiler == null) return false;

            var own = new Harmony(OwnTestOwner);
            var foreign = new Harmony(ForeignTestOwner);
            try
            {
                own.Patch(target, transpiler: new HarmonyMethod(transpiler));
                foreign.Patch(target, transpiler: new HarmonyMethod(transpiler));

                HarmonyCompatibilityAudit.Reset();
                return !HarmonyCompatibilityAudit.Inspect(target, "test-transpiler");
            }
            catch
            {
                return false;
            }
            finally
            {
                try { own.Unpatch(target, HarmonyPatchType.All, OwnTestOwner); } catch { }
                try { foreign.Unpatch(target, HarmonyPatchType.All, ForeignTestOwner); } catch { }
                HarmonyCompatibilityAudit.Reset();
            }
        }

        internal static bool Test_P2PTransportTargets_RemainExclusive()
        {
            MethodInfo reject = AccessTools.Method(typeof(Provider), "reject",
                new[] { typeof(CSteamID), typeof(ESteamRejection) });
            MethodInfo closeConnection = AccessTools.Method(
                typeof(SteamGameServerNetworkingSockets), "CloseConnection");
            MethodInfo loopback = AccessTools.Method(typeof(ClientMethodHandle), "SendAndLoopback");

            return HarmonyCompatibilityAudit.IsExclusiveTransportTarget(reject) &&
                   HarmonyCompatibilityAudit.IsExclusiveTransportTarget(closeConnection) &&
                   HarmonyCompatibilityAudit.IsExclusiveTransportTarget(loopback);
        }
    }
}
