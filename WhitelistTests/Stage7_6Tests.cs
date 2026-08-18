using HarmonyLib;
using SteamP2PFriends.Host;
using SteamP2PFriends.Client;
using SteamP2PFriends.Patches;
using SteamP2PFriends.UI;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace SteamP2PFriends.WhitelistTests
{
    internal static class Stage7_6Tests
    {
        private const ulong Id1 = 76561199721762479UL;
        private const ulong Id2 = 76561199721762480UL;
        private const ulong Id3 = 76561199721762481UL;
        private const ulong Id4 = 76561199721762482UL;
        private const ulong Id5 = 76561199721762483UL;

        internal static bool Test_Q1_ReservationCapAndDedup()
        {
            Setup();
            try
            {
                object t1 = new object();
                if (!P2PQuarantineAdmissionService.TryReserveForTest(Id1, t1, 0f)) return Fail("reserve 1", "false");
                if (!P2PQuarantineAdmissionService.TryReserveForTest(Id1, t1, 1f)) return Fail("same token dedup", "false");
                if (P2PQuarantineAdmissionService.TryReserveForTest(Id1, new object(), 1f)) return Fail("different token duplicate", "true");
                if (!P2PQuarantineAdmissionService.TryReserveForTest(Id2, new object(), 0f)) return Fail("reserve 2", "false");
                if (!P2PQuarantineAdmissionService.TryReserveForTest(Id3, new object(), 0f)) return Fail("reserve 3", "false");
                if (!P2PQuarantineAdmissionService.TryReserveForTest(Id4, new object(), 0f)) return Fail("reserve 4", "false");
                if (P2PQuarantineAdmissionService.TryReserveForTest(Id5, new object(), 0f)) return Fail("fifth must fail closed", "true");
                return P2PQuarantineAdmissionService.EntryCountForTest == 4;
            }
            finally { Cleanup(); }
        }

        internal static bool Test_Q2_ReservedExpiresPendingDoesNot()
        {
            Setup();
            try
            {
                object pendingToken = new object();
                P2PQuarantineAdmissionService.TryReserveForTest(Id1, pendingToken, 0f);
                P2PQuarantineAdmissionService.BindPendingForTest(Id1, pendingToken);
                P2PQuarantineAdmissionService.TryReserveForTest(Id2, new object(), 0f);
                if (!P2PQuarantineAdmissionService.TryReserveForTest(Id3, new object(), 16f))
                    return Fail("reserve after purge", "false");
                if (P2PQuarantineAdmissionService.GetPhaseForTest(Id1) != QuarantinePhase.Pending)
                    return Fail("pending must survive reservation TTL", P2PQuarantineAdmissionService.GetPhaseForTest(Id1).ToString());
                if (P2PQuarantineAdmissionService.GetPhaseForTest(Id2) != QuarantinePhase.None)
                    return Fail("unbound reservation must expire", P2PQuarantineAdmissionService.GetPhaseForTest(Id2).ToString());
                return true;
            }
            finally { Cleanup(); }
        }

        internal static bool Test_Q3_ActiveDeadlineStartsOnPromotion()
        {
            Setup();
            try
            {
                object token = new object();
                P2PQuarantineAdmissionService.TryReserveForTest(Id1, token, 2f);
                P2PQuarantineAdmissionService.BindPendingForTest(Id1, token);
                if (!P2PQuarantineAdmissionService.PromoteForTest(Id1, token, 100f))
                    return Fail("promote", "false");
                if (P2PQuarantineAdmissionService.GetDeadlineForTest(Id1) != 130f)
                    return Fail("deadline must be promotion+30", P2PQuarantineAdmissionService.GetDeadlineForTest(Id1).ToString());
                return P2PQuarantineAdmissionService.GetPhaseForTest(Id1) == QuarantinePhase.Active;
            }
            finally { Cleanup(); }
        }

        internal static bool Test_Q4_TimeoutKicksOnceAndCleans()
        {
            Setup();
            float now = 0f;
            int kicks = 0;
            P2PQuarantineAdmissionService._testTimeProvider = () => now;
            P2PQuarantineAdmissionService._testKickCallback = (id, reason) => kicks++;
            try
            {
                object token = new object();
                P2PQuarantineAdmissionService.TryReserveForTest(Id1, token, 0f);
                P2PQuarantineAdmissionService.PromoteForTest(Id1, token, 0f);
                now = 29.9f;
                P2PQuarantineAdmissionService.Tick();
                if (kicks != 0) return Fail("early kick", kicks.ToString());
                now = 30f;
                P2PQuarantineAdmissionService.Tick();
                P2PQuarantineAdmissionService.Tick();
                if (kicks != 1) return Fail("kick exactly once", kicks.ToString());
                return P2PQuarantineAdmissionService.EntryCountForTest == 0;
            }
            finally { Cleanup(); }
        }

        internal static bool Test_Q5_ReleaseRequiresWhitelistPostcondition()
        {
            Setup();
            bool whitelisted = false;
            P2PQuarantineAdmissionService._testWhitelistContains = id => whitelisted;
            try
            {
                object token = new object();
                P2PQuarantineAdmissionService.TryReserveForTest(Id1, token, 0f);
                P2PQuarantineAdmissionService.PromoteForTest(Id1, token, 0f);
                if (P2PQuarantineAdmissionService.ReleaseAfterPersistentApproval(new CSteamID(Id1), out _))
                    return Fail("release before whitelist", "true");
                if (!P2PQuarantineAdmissionService.IsActive(new CSteamID(Id1)))
                    return Fail("failed release must retain isolation", "inactive");
                whitelisted = true;
                if (!P2PQuarantineAdmissionService.ReleaseAfterPersistentApproval(new CSteamID(Id1), out string failure))
                    return Fail("release after whitelist", failure);
                return !P2PQuarantineAdmissionService.IsKnown(new CSteamID(Id1));
            }
            finally { Cleanup(); }
        }

        internal static bool Test_Q6_SignalBitCompatibility()
        {
            return P2PQuarantineAdmissionService.IsSignalBitCompatible() &&
                   P2PQuarantineAdmissionService.QuarantineSignalMask == 0x80000000u;
        }

        internal static bool Test_Q7_ManualHarmonyRegistration()
        {
            // CoreCLR test runner cannot detour these Unity/Mono internal handlers (Harmony IL compile error).
            // Validate exact targets/signatures here; the in-game startup gate verifies real ownership/activation.
            MethodBase readyTarget = P2PQuarantineReadyToConnectScopePatch.GetTargetMethod();
            MethodInfo readyPrefix = AccessTools.Method(
                typeof(P2PQuarantineReadyToConnectScopePatch), nameof(P2PQuarantineReadyToConnectScopePatch.Prefix));
            MethodInfo readyFinalizer = AccessTools.Method(
                typeof(P2PQuarantineReadyToConnectScopePatch), nameof(P2PQuarantineReadyToConnectScopePatch.Finalizer));
            Type invokeType = AccessTools.TypeByName(P2PQuarantineServerInvokeGatePatch.TargetTypeName);
            Type readerType = AccessTools.TypeByName("SDG.NetPak.NetPakReader");
            MethodInfo invokeTarget = invokeType == null || readerType == null ? null :
                AccessTools.Method(invokeType, "ReadMessage",
                    new[] { typeof(SDG.NetTransport.ITransportConnection), readerType });
            MethodInfo invokePrefix = AccessTools.Method(
                typeof(P2PQuarantineServerInvokeGatePatch), nameof(P2PQuarantineServerInvokeGatePatch.Prefix));
            MethodInfo normalFactory = AccessTools.Method(typeof(SDG.Unturned.PlayerDashboardInformationUI),
                "OnCreatePlayerEntry", new[] { typeof(SDG.Unturned.SteamPlayer) });
            MethodInfo groupedFactory = AccessTools.Method(typeof(SDG.Unturned.PlayerDashboardInformationUI),
                "OnCreatePlayerEntryWithGrouping", new[] { typeof(SDG.Unturned.SteamPlayer) });
            MethodInfo decorator = AccessTools.Method(typeof(P2PPlayerListApprovalDecorator),
                nameof(P2PPlayerListApprovalDecorator.Postfix));
            return readyTarget != null && readyPrefix != null && readyFinalizer != null &&
                   invokeTarget != null && invokePrefix != null &&
                   normalFactory != null && groupedFactory != null && decorator != null;
        }

        internal static bool Test_Q8_ChatCountdownEveryFiveSeconds()
        {
            List<string> messages = new List<string>();
            P2PQuarantineClientView._testChatSink = text => messages.Add(text);
            P2PQuarantineClientView.ResetCountdownForTest();
            try
            {
                P2PQuarantineClientView.UpdateCountdownAnnouncementsForTest(true, 30);
                P2PQuarantineClientView.UpdateCountdownAnnouncementsForTest(true, 26);
                if (messages.Count != 0) return Fail("no early countdown message", messages.Count.ToString());
                P2PQuarantineClientView.UpdateCountdownAnnouncementsForTest(true, 25);
                P2PQuarantineClientView.UpdateCountdownAnnouncementsForTest(true, 24);
                P2PQuarantineClientView.UpdateCountdownAnnouncementsForTest(true, 20);
                P2PQuarantineClientView.UpdateCountdownAnnouncementsForTest(true, 15);
                P2PQuarantineClientView.UpdateCountdownAnnouncementsForTest(true, 10);
                P2PQuarantineClientView.UpdateCountdownAnnouncementsForTest(true, 5);
                if (messages.Count != 5) return Fail("exactly five milestone messages", messages.Count.ToString());
                if (!messages[0].Contains("25") || !messages[4].Contains("5"))
                    return Fail("milestone text", string.Join(" | ", messages));
                P2PQuarantineClientView.UpdateCountdownAnnouncementsForTest(false, 0);
                return messages.Count == 6 && messages[5].Contains("审核状态已结束");
            }
            finally
            {
                P2PQuarantineClientView._testChatSink = null;
                P2PQuarantineClientView.ResetCountdownForTest();
            }
        }

        internal static bool Test_Q9_WhitelistPatchUsesIndexedArgumentBinding()
        {
            MethodInfo target = AccessTools.Method(typeof(SDG.Unturned.SteamWhitelist),
                nameof(SDG.Unturned.SteamWhitelist.checkWhitelisted), new[] { typeof(CSteamID) });
            MethodInfo postfix = AccessTools.Method(typeof(P2PQuarantineWhitelistPermitPatch), "Postfix");
            if (target == null || postfix == null) return Fail("whitelist target/postfix metadata", "missing");

            ParameterInfo[] targetParameters = target.GetParameters();
            ParameterInfo[] patchParameters = postfix.GetParameters();
            if (targetParameters.Length != 1 || targetParameters[0].ParameterType != typeof(CSteamID))
                return Fail("target ABI", target.ToString());
            if (patchParameters.Length != 2 || patchParameters[0].Name != "__0" ||
                patchParameters[0].ParameterType != typeof(CSteamID))
                return Fail("postfix must bind argument by index", postfix.ToString());
            return patchParameters[1].Name == "__result" &&
                   patchParameters[1].ParameterType == typeof(bool).MakeByRefType();
        }

        internal static bool Test_Q10_ServerTargetedCountdownMilestones()
        {
            Setup();
            float now = 0f;
            List<string> messages = new List<string>();
            P2PQuarantineAdmissionService._testTimeProvider = () => now;
            P2PQuarantineAdmissionService._testChatCallback = (id, text) => messages.Add(text);
            try
            {
                object token = new object();
                if (!P2PQuarantineAdmissionService.TryReserveForTest(Id1, token, 0f)) return false;
                if (!P2PQuarantineAdmissionService.PromoteForTest(Id1, token, 0f)) return false;

                now = 4.9f;
                P2PQuarantineAdmissionService.Tick();
                if (messages.Count != 0) return Fail("countdown before 25s milestone", messages.Count.ToString());

                int[] times = { 5, 10, 15, 20, 25 };
                int[] expected = { 25, 20, 15, 10, 5 };
                for (int i = 0; i < times.Length; i++)
                {
                    now = times[i];
                    P2PQuarantineAdmissionService.Tick();
                    P2PQuarantineAdmissionService.Tick();
                    if (messages.Count != i + 1 || !messages[i].Contains(expected[i].ToString()))
                        return Fail("targeted countdown milestone", string.Join(" | ", messages));
                }
                return true;
            }
            finally { Cleanup(); }
        }

        internal static bool Test_Q11_ApprovalButtonIsInsideRow()
        {
            return P2PPlayerListApprovalDecorator.ActionPositionOffsetForTest == -70;
        }

        internal static bool Test_Q12_PlayerRowPreservesVanillaHeight()
        {
            return Math.Abs(P2PPlayerListApprovalDecorator.WrapperHeightScaleForTest) < 0.001f;
        }

        internal static bool Test_Q13_ApproveTransitionsToClickableRevoke()
        {
            return P2PPlayerListApprovalDecorator.IsActionClickableAfterSuccessForTest(true) &&
                   !P2PPlayerListApprovalDecorator.IsActionClickableAfterSuccessForTest(false);
        }

        private static void Setup()
        {
            P2PQuarantineAdmissionService._testBypassThreadAssert = true;
            P2PQuarantineAdmissionService._testActiveHost = true;
            P2PQuarantineAdmissionService._testWhitelistContains = id => false;
            P2PQuarantineAdmissionService._testTimeProvider = () => 0f;
            P2PQuarantineAdmissionService._testKickCallback = null;
            P2PQuarantineAdmissionService._testChatCallback = (id, text) => { };
            P2PQuarantineAdmissionService._testSignalCallback = (id, enabled) => { };
            P2PQuarantineAdmissionService._testLocalUserSteamId = 76561199030780228UL;
            P2PQuarantineAdmissionService.ResetForSession();
        }

        private static void Cleanup()
        {
            P2PQuarantineAdmissionService.ResetForSession();
            P2PQuarantineAdmissionService._testBypassThreadAssert = false;
            P2PQuarantineAdmissionService._testActiveHost = null;
            P2PQuarantineAdmissionService._testWhitelistContains = null;
            P2PQuarantineAdmissionService._testTimeProvider = null;
            P2PQuarantineAdmissionService._testKickCallback = null;
            P2PQuarantineAdmissionService._testChatCallback = null;
            P2PQuarantineAdmissionService._testSignalCallback = null;
            P2PQuarantineAdmissionService._testLocalUserSteamId = null;
        }

        private static bool Fail(string message, string detail)
        {
            Console.WriteLine("    FAIL: " + message + (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")"));
            return false;
        }
    }
}
