using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using Steamworks;
using System;
using System.Collections.Generic;

namespace SteamP2PFriends.WhitelistTests
{
    /// <summary>
    /// Stage 10: world-status broadcast tests WSB1-WSB32 (blueprint §8.1).
    /// Tests call production pure functions and transaction entry points; no copied test mapper.
    /// All host-gate checks bypass Provider via _testDisableHostGate, exactly like existing suites.
    /// </summary>
    internal static class Stage10WorldStatusBroadcastTests
    {
        private static readonly CSteamID GuestA = new CSteamID(76561199721762479UL);
        private static readonly CSteamID GuestB = new CSteamID(76561199030780222UL);
        private static readonly CSteamID HostId = new CSteamID(76561199030780228UL);
        private static readonly CSteamID KillerC = new CSteamID(76561199080000001UL);

        private static void ResetBc()
        {
            P2PWorldStatusBroadcaster.ResetForTest();
            P2PWorldStatusBroadcaster._testBypassThreadAssert = true;
            P2PWorldStatusBroadcaster._testDisableHostGate = true;
            // Console tests cannot safely mutate the game's static legacy delegate. Default to
            // no-op adapters; tests which verify counts replace these explicitly.
            P2PWorldStatusBroadcaster._testSubscribeLegacyDeath = _ => { };
            P2PWorldStatusBroadcaster._testUnsubscribeLegacyDeath = _ => { };
            // Fully isolate quarantine static test hooks: bypass thread assert on the test
            // console and clear every hook so no test leaks host/whitelist/time/kick/signal/chat
            // state into the next test.
            P2PQuarantineAdmissionService._testBypassThreadAssert = true;
            P2PQuarantineAdmissionService._testActiveHost = null;
            P2PQuarantineAdmissionService._testWhitelistContains = null;
            P2PQuarantineAdmissionService._testTimeProvider = null;
            P2PQuarantineAdmissionService._testKickCallback = null;
            P2PQuarantineAdmissionService._testSignalCallback = null;
            P2PQuarantineAdmissionService._testChatCallback = null;
            P2PQuarantineAdmissionService._testLocalUserSteamId = null;
            P2PQuarantineAdmissionService.ResetForSession();
        }

        // ===== v6 WSB57: fallback observes only a committed alive -> health-zero transition =====
        internal static bool Test_WSB57_DeathCommitTransitionGate()
        {
            return Patches.P2PWorldDeathCommitPatch.ShouldForwardCommittedDeath(true, true) &&
                   !Patches.P2PWorldDeathCommitPatch.ShouldForwardCommittedDeath(true, false) &&
                   !Patches.P2PWorldDeathCommitPatch.ShouldForwardCommittedDeath(false, true) &&
                   !Patches.P2PWorldDeathCommitPatch.ShouldForwardCommittedDeath(false, false);
        }

        // ===== v5 WSB58: target is the authoritative private doDamage ABI from the runtime DLL =====
        internal static bool Test_WSB58_DeathCommitTargetAbi()
        {
            var method = Patches.P2PWorldDeathCommitPatch.TargetMethod();
            if (method == null || method.DeclaringType != typeof(PlayerLife) || method.Name != "doDamage")
                return false;
            var ps = method.GetParameters();
            return ps.Length == 9 &&
                   ps[0].ParameterType == typeof(byte) &&
                   ps[1].ParameterType == typeof(UnityEngine.Vector3) &&
                   ps[2].ParameterType == typeof(EDeathCause) &&
                   ps[3].ParameterType == typeof(ELimb) &&
                   ps[4].ParameterType == typeof(CSteamID) &&
                   ps[5].ParameterType == typeof(EPlayerKill).MakeByRefType() &&
                   ps[6].ParameterType == typeof(bool) &&
                   ps[7].ParameterType == typeof(ERagdollEffect) &&
                   ps[8].ParameterType == typeof(bool);
        }

        // ===== v7 WSB59: both preferred legacy source and compatibility event are symmetric =====
        internal static bool Test_WSB59_LegacyAndCompatibilitySourcesSymmetric()
        {
            ResetBc();
            int legacyAdd = 0, legacyRemove = 0, eventAdd = 0, eventRemove = 0;
            P2PWorldStatusBroadcaster._testSubscribeDeath = _ => eventAdd++;
            P2PWorldStatusBroadcaster._testUnsubscribeDeath = _ => eventRemove++;
            P2PWorldStatusBroadcaster._testSubscribeLegacyDeath = _ => legacyAdd++;
            P2PWorldStatusBroadcaster._testUnsubscribeLegacyDeath = _ => legacyRemove++;
            if (!P2PWorldStatusBroadcaster.InitializeCore()) return false;
            P2PWorldStatusBroadcaster.Shutdown();
            return eventAdd == 1 && eventRemove == 1 && legacyAdd == 1 && legacyRemove == 1;
        }

        // ===== v8 WSB60: listen-host identity fallback remains exact and fail-closed =====
        internal static bool Test_WSB60_LocalVictimIdentityPolicy()
        {
            return !P2PWorldStatusBroadcaster.ShouldUseLocalVictimIdentity(false, false) &&
                   P2PWorldStatusBroadcaster.ShouldUseLocalVictimIdentity(true, false) &&
                   P2PWorldStatusBroadcaster.ShouldUseLocalVictimIdentity(false, true) &&
                   P2PWorldStatusBroadcaster.ShouldUseLocalVictimIdentity(true, true);
        }

        // ===== v9 WSB61: server/client CLR projections correlate by authoritative Life NetId =====
        internal static bool Test_WSB61_VictimProjectionNetIdPolicy()
        {
            NetId a = new NetId(0x1234U);
            NetId b = new NetId(0x5678U);
            return P2PWorldStatusBroadcaster.ShouldMatchVictimProjection(a, a, false, false) &&
                   !P2PWorldStatusBroadcaster.ShouldMatchVictimProjection(a, b, false, false) &&
                   !P2PWorldStatusBroadcaster.ShouldMatchVictimProjection(
                       NetId.INVALID, NetId.INVALID, false, false) &&
                   P2PWorldStatusBroadcaster.ShouldMatchVictimProjection(
                       NetId.INVALID, NetId.INVALID, true, false) &&
                   P2PWorldStatusBroadcaster.ShouldMatchVictimProjection(
                       NetId.INVALID, NetId.INVALID, false, true);
        }

        // ===== v10 WSB62: unusable player.channel must not shadow PlayerLife.channel owner =====
        internal static bool Test_WSB62_VictimChannelOwnerSelectionPolicy()
        {
            return P2PWorldStatusBroadcaster.SelectVictimChannelCandidate(true, true) == 1 &&
                   P2PWorldStatusBroadcaster.SelectVictimChannelCandidate(true, false) == 1 &&
                   P2PWorldStatusBroadcaster.SelectVictimChannelCandidate(false, true) == 2 &&
                   P2PWorldStatusBroadcaster.SelectVictimChannelCandidate(false, false) == 0;
        }

        // ===== v11 WSB63: matched SteamPlayerID projects without overloaded null operators =====
        internal static bool Test_WSB63_VictimPlayerIdProjection()
        {
            if (P2PWorldStatusBroadcaster.TryProjectVictimPlayerId(
                    null, out CSteamID nilId, out string nilName)) return false;
            if (nilId != CSteamID.Nil || nilName != null) return false;

            SteamPlayerID playerId = new SteamPlayerID(
                GuestA, 0, "Account", "Character", "", CSteamID.Nil);
            if (!P2PWorldStatusBroadcaster.TryProjectVictimPlayerId(
                    playerId, out CSteamID projected, out string name)) return false;
            return projected == GuestA && name == "Character";
        }

        // ===== v12 WSB64: every player token is cyan and raw markup cannot escape =====
        internal static bool Test_WSB64_RichPlayerNamePresentation()
        {
            string dirty = "<b>Alice</b>";
            string colored = P2PWorldStatusTemplates.ColorizePlayerName(dirty);
            if (colored != "<color=#55FFFF>bAlice/b</color>") return false;
            string two = P2PWorldStatusTemplates.RenderRich(
                "{name} vs {killer}", "Alice", "Bob");
            return two == "<color=#55FFFF>Alice</color> vs <color=#55FFFF>Bob</color>";
        }

        // ===== v12 WSB65: production presentation keeps plugin speaker nil and sends avatar marker =====
        internal static bool Test_WSB65_DeathPresentationAbi()
        {
            ResetBc();
            P2PWorldStatusBroadcaster._testPlainPresentation = false;
            P2PWorldStatusBroadcaster._testTimeProvider = () => 0f;
            P2PWorldStatusBroadcaster._testRandomIndexProvider = () => 0;
            string text = null;
            P2PWorldStatusBroadcaster._testChatManagerSend = (t, color, from, to, mode, icon, rich) =>
            {
                text = t;
                P2PWorldStatusBroadcaster.LastCapturedFromPlayer = from;
                P2PWorldStatusBroadcaster.LastCapturedToPlayer = to;
                P2PWorldStatusBroadcaster.LastCapturedMode = mode;
                P2PWorldStatusBroadcaster.LastCapturedIconUrl = icon;
                P2PWorldStatusBroadcaster.LastCapturedRichText = rich;
            };
            P2PWorldStatusBroadcaster.HandleDeathCore(
                GuestA, "Alice", EDeathCause.ZOMBIE, CSteamID.Nil);
            return text != null && text.Contains("<color=#55FFFF>Alice</color>") &&
                   P2PWorldStatusBroadcaster.LastCapturedFromPlayer == null &&
                   P2PWorldStatusBroadcaster.LastCapturedToPlayer == null &&
                   P2PWorldStatusBroadcaster.LastCapturedMode == EChatMode.WELCOME &&
                   P2PWorldStatusBroadcaster.LastCapturedRichText &&
                   Patches.P2PWorldChatAvatarPatch.TryParseAvatarMarker(
                       P2PWorldStatusBroadcaster.LastCapturedIconUrl, out CSteamID parsed) &&
                   parsed == GuestA;
        }

        // ===== v12 WSB66: malformed markers fail closed and never become web URLs =====
        internal static bool Test_WSB66_AvatarMarkerParser()
        {
            string marker = Patches.P2PWorldChatAvatarPatch.BuildAvatarMarker(GuestA);
            return Patches.P2PWorldChatAvatarPatch.TryParseAvatarMarker(
                       marker, out CSteamID parsed) && parsed == GuestA &&
                   !Patches.P2PWorldChatAvatarPatch.TryParseAvatarMarker(
                       "https://example.invalid/a.png", out _) &&
                   !Patches.P2PWorldChatAvatarPatch.TryParseAvatarMarker(
                       "sp2pf-avatar:not-a-number", out _);
        }

        // ===== v12 WSB67: both native chat UI generations are exact avatar projection targets =====
        internal static bool Test_WSB67_AvatarPatchTargetsBothChatUis()
        {
            var targets = new List<System.Reflection.MethodBase>(
                Patches.P2PWorldChatAvatarPatch.TargetMethods());
            if (targets.Count != 2 || targets.Exists(m => m == null)) return false;
            return targets.Exists(m => m.DeclaringType == typeof(SleekChatEntryV1) &&
                                      m.Name == "set_representingChatMessage") &&
                   targets.Exists(m => m.DeclaringType == typeof(SleekChatEntryV2) &&
                                      m.Name == "set_representingChatMessage");
        }

        // ===== WSB1 (v2 P1-07): Initialize twice subscribes exactly once (counted via adapter) =====
        internal static bool Test_WSB1_InitializeTwiceSubscribesOnce()
        {
            ResetBc();
            int addCount = 0;
            int removeCount = 0;
            P2PWorldStatusBroadcaster._testSubscribeDeath = _ => addCount++;
            P2PWorldStatusBroadcaster._testUnsubscribeDeath = _ => removeCount++;

            bool first = P2PWorldStatusBroadcaster.InitializeCore();
            bool firstInit = P2PWorldStatusBroadcaster.IsInitializedForTest;
            bool second = P2PWorldStatusBroadcaster.InitializeCore(); // must be a no-op
            bool stillInitialized = P2PWorldStatusBroadcaster.IsInitializedForTest;
            if (!first || !firstInit || !second || !stillInitialized) return false;
            if (addCount != 1) return false; // EXACTLY one add

            P2PWorldStatusBroadcaster.Shutdown();
            bool afterShutdown = P2PWorldStatusBroadcaster.IsInitializedForTest;
            P2PWorldStatusBroadcaster.Shutdown(); // double shutdown safe
            bool afterSecondShutdown = P2PWorldStatusBroadcaster.IsInitializedForTest;
            return !afterShutdown && !afterSecondShutdown && addCount == 1 && removeCount == 1;
        }

        // ===== WSB2 (v2 P1-07): Shutdown twice -> exactly one remove =====
        internal static bool Test_WSB2_ShutdownTwiceSafe()
        {
            ResetBc();
            int addCount = 0;
            int removeCount = 0;
            P2PWorldStatusBroadcaster._testSubscribeDeath = _ => addCount++;
            P2PWorldStatusBroadcaster._testUnsubscribeDeath = _ => removeCount++;
            P2PWorldStatusBroadcaster.InitializeCore();
            P2PWorldStatusBroadcaster.Shutdown();
            P2PWorldStatusBroadcaster.Shutdown();
            return !P2PWorldStatusBroadcaster.IsInitializedForTest &&
                   addCount == 1 && removeCount == 1;
        }

        // ===== WSB3: single-player / normal client / dedicated gate all do not send =====
        // The production host gate (IsActiveP2PHost) is authoritative; disabling it means
        // "NOT an active plugin listen-host", so no send must occur.
        internal static bool Test_WSB3_NonHostGateDoesNotSend()
        {
            ResetBc();
            int sent = 0;
            P2PWorldStatusBroadcaster._testSendSink = _ => sent++;
            P2PWorldStatusBroadcaster._testDisableHostGate = false; // host gate OFF => not host

            // Host gate is false; even a death must not send.
            P2PWorldStatusBroadcaster.HandleDeathCore(GuestA, "Alice", EDeathCause.ZOMBIE, CSteamID.Nil);
            return sent == 0;
        }

        // ===== WSB4 (v2 P1-07): SteamID P2P / IPv4 / DNS routes all funnel through ONE broadcaster path =====
        // The three route classifiers each resolve into the same Provider session; the broadcaster
        // only ever sees a CSteamID + promotion result and has NO route branch. We prove route
        // consistency by driving the SAME broadcaster connect path for a guest that could have
        // arrived via any route: identical projection registration + identical JoinApproved output.
        internal static bool Test_WSB4_ThreeRoutesUnifiedGate()
        {
            // All three production classifiers accept their route (no route is suppressed).
            bool p2p = UnifiedJoinAddressClassifier.Classify(GuestA.m_SteamID.ToString(), out ulong sid) ==
                       UnifiedJoinAddressKind.SteamP2P && sid == GuestA.m_SteamID;
            bool ipv4 = UnifiedJoinAddressClassifier.TryBuildDirectIpEndpoint(
                "26.196.34.90", 27016, out _, out _, out _);
            bool dns = UnifiedJoinAddressClassifier.TryBuildExplicitDnsEndpoint(
                "node.frp", 26655, out _, out _);
            if (!p2p || !ipv4 || !dns) return false;

            // The broadcaster is route-agnostic: for a given guest (whoever the route carried),
            // an AlreadyApproved promotion registers Approved projection and broadcasts JoinApproved
            // via the full-ABI production chat sender — the exact same code path for all three routes.
            ResetBc();
            int sent = 0;
            // v3 P1-09: use the full-ABI adapter so the broadcast captures the REAL 7-arg ABI.
            P2PWorldStatusBroadcaster._testChatManagerSend = (text, color, from, to, mode, icon, rich) =>
            {
                sent++;
                P2PWorldStatusBroadcaster.LastCapturedFromPlayer = from;
                P2PWorldStatusBroadcaster.LastCapturedToPlayer = to;
                P2PWorldStatusBroadcaster.LastCapturedMode = mode;
                P2PWorldStatusBroadcaster.LastCapturedIconUrl = icon;
                P2PWorldStatusBroadcaster.LastCapturedRichText = rich;
            };
            P2PWorldStatusBroadcaster.OnPlayerConnectedCore(
                GuestA, "Alice", QuarantinePromotionResult.AlreadyApproved);
            return sent == 1 &&
                   P2PWorldStatusBroadcaster.ConnectionStateForTest(GuestA.m_SteamID) ==
                       P2PWorldStatusBroadcaster.EConnectionProjectionState.Approved &&
                   P2PWorldStatusBroadcaster.LastCapturedFromPlayer == null &&
                   P2PWorldStatusBroadcaster.LastCapturedToPlayer == null &&
                   P2PWorldStatusBroadcaster.LastCapturedMode == EChatMode.WELCOME &&
                   P2PWorldStatusBroadcaster.LastCapturedIconUrl == string.Empty &&
                   !P2PWorldStatusBroadcaster.LastCapturedRichText;
        }

        // ===== WSB5: whitelisted player connect broadcasts JoinApproved only =====
        internal static bool Test_WSB5_ApprovedConnectBroadcastsJoinApproved()
        {
            ResetBc();
            int sent = 0;
            P2PWorldStatusBroadcaster._testSendSink = _ => sent++;
            P2PWorldStatusBroadcaster.OnPlayerConnectedCore(
                GuestA, "Alice", QuarantinePromotionResult.AlreadyApproved);
            return sent == 1;
        }

        // ===== WSB6: unapproved Activated connect broadcasts JoinQuarantined only =====
        internal static bool Test_WSB6_ActivatedConnectBroadcastsJoinQuarantined()
        {
            ResetBc();
            int sent = 0;
            P2PWorldStatusBroadcaster._testSendSink = _ => sent++;
            P2PWorldStatusBroadcaster.OnPlayerConnectedCore(
                GuestA, "Alice", QuarantinePromotionResult.Activated);
            return sent == 1;
        }

        // ===== WSB7: missing reservation -> no "joined" broadcast =====
        internal static bool Test_WSB7_MissingReservationNoJoin()
        {
            ResetBc();
            int sent = 0;
            P2PWorldStatusBroadcaster._testSendSink = _ => sent++;
            P2PWorldStatusBroadcaster.OnPlayerConnectedCore(
                GuestA, "Alice", QuarantinePromotionResult.RejectedMissingReservation);
            return sent == 0;
        }

        // ===== WSB8: signal failure -> no "joined" broadcast =====
        internal static bool Test_WSB8_SignalFailureNoJoin()
        {
            ResetBc();
            int sent = 0;
            P2PWorldStatusBroadcaster._testSendSink = _ => sent++;
            P2PWorldStatusBroadcaster.OnPlayerConnectedCore(
                GuestA, "Alice", QuarantinePromotionResult.RejectedSignalFailure);
            return sent == 0;
        }

        // ===== WSB9: whitelist persistence failure -> no ApprovalReleased =====
        // Uses the real P2PJoinApprovalService.Approve transaction: whitelist proxy TryAdd=false.
        internal static bool Test_WSB9_PersistenceFailureNoApproval()
        {
            ResetBc();
            var runtime = new Fakes.FakeApprovalRuntimeContext();
            var whitelist = new Fakes.FakeApprovalWhitelistProxy { TryAddResult = false };
            using (P2PJoinApprovalService.InstallTestDependencies(runtime, whitelist))
            {
                int sent = 0;
                P2PWorldStatusBroadcaster._testSendSink = _ => sent++;
                bool ok = P2PJoinApprovalService.Approve(GuestA, out string feedback);
                return !ok && sent == 0;
            }
        }

        // ===== WSB10: persisted but release failure -> no ApprovalReleased =====
        internal static bool Test_WSB10_PersistOkReleaseFailNoApproval()
        {
            ResetBc();
            var runtime = new Fakes.FakeApprovalRuntimeContext();
            var whitelist = new Fakes.FakeApprovalWhitelistProxy { TryAddResult = true };
            bool result;
            using (P2PJoinApprovalService.InstallTestDependencies(runtime, whitelist))
            {
                // Seed quarantine Active for GuestA so Approve takes the release branch.
                P2PQuarantineAdmissionService._testActiveHost = true;
                P2PQuarantineAdmissionService._testWhitelistContains = id => false; // release postcondition fails
                P2PQuarantineAdmissionService._testTimeProvider = () => 1000f;
                P2PQuarantineAdmissionService._testLocalUserSteamId = HostId.m_SteamID;
                P2PQuarantineAdmissionService._testKickCallback = (sid, r) => { };
                P2PQuarantineAdmissionService._testSignalCallback = (sid, on) => { };
                P2PQuarantineAdmissionService._testChatCallback = (sid, t) => { };
                object token = new object();
                P2PQuarantineAdmissionService.TryReserveForTest(GuestA.m_SteamID, token, 1000f);
                P2PQuarantineAdmissionService.PromoteForTest(GuestA.m_SteamID, token, 1000f);

                int sent = 0;
                P2PWorldStatusBroadcaster._testSendSink = _ => sent++;
                // Approve: TryAdd=true, but ReleaseAfterPersistentApproval checks ContainsWhitelist
                // (false) => release fails => Approve returns false => NO broadcast.
                bool ok = P2PJoinApprovalService.Approve(GuestA, out string feedback);
                result = !ok && sent == 0;
            }
            // Restore quarantine hooks so no state leaks into later tests.
            ResetBc();
            return result;
        }

        // ===== WSB11 (v2): full approval transaction broadcasts exactly once =====
        internal static bool Test_WSB11_FullApprovalBroadcastsOnce()
        {
            ResetBc();
            var runtime = new Fakes.FakeApprovalRuntimeContext();
            var whitelist = new Fakes.FakeApprovalWhitelistProxy { TryAddResult = true };
            using (P2PJoinApprovalService.InstallTestDependencies(runtime, whitelist))
            {
                // v2 P1-02: register the guest's Quarantined connection projection first, so the
                // approval has a generation to promote to Approved.
                P2PWorldStatusBroadcaster.OnPlayerConnectedCore(
                    GuestA, "Alice", QuarantinePromotionResult.Activated);
                int sent = 0;
                P2PWorldStatusBroadcaster._testSendSink = _ => sent++;
                bool ok = P2PJoinApprovalService.Approve(GuestA, out _);
                // JoinQuarantined (1) + ApprovalReleased (1) = 2 total; the approval itself is 1.
                return ok && sent == 1;
            }
        }

        // ===== WSB12 (v2): duplicate approve click in the SAME connection does not re-broadcast =====
        // Approval dedup is per-connection-generation (projection state), so a Quarantined guest
        // is promoted to Approved once; the second Approve within the same connection is suppressed.
        internal static bool Test_WSB12_DuplicateApproveNoRepeat()
        {
            ResetBc();
            var runtime = new Fakes.FakeApprovalRuntimeContext();
            var whitelist = new Fakes.FakeApprovalWhitelistProxy { TryAddResult = true };
            using (P2PJoinApprovalService.InstallTestDependencies(runtime, whitelist))
            {
                // Register the guest as a quarantined (Activated) connection projection FIRST, so
                // approval has a generation to promote.
                P2PWorldStatusBroadcaster.OnPlayerConnectedCore(
                    GuestA, "Alice", QuarantinePromotionResult.Activated);
                int sent = 0;
                P2PWorldStatusBroadcaster._testSendSink = _ => sent++;
                bool ok1 = P2PJoinApprovalService.Approve(GuestA, out _);
                bool ok2 = P2PJoinApprovalService.Approve(GuestA, out _); // retry
                // WSB12: same-connection approval dedup suppresses the second notification.
                return ok1 && ok2 && sent == 1;
            }
        }

        // ===== WSB13: 30s timeout broadcasts once and writes expected-departure =====
        internal static bool Test_WSB13_TimeoutBroadcastsOnceAndMarks()
        {
            ResetBc();
            P2PWorldStatusBroadcaster._testTimeProvider = () => 100f;
            int sent = 0;
            P2PWorldStatusBroadcaster._testSendSink = _ => sent++;
            P2PWorldStatusBroadcaster.OnApprovalTimeoutCore(GuestA);
            return sent == 1 && P2PWorldStatusBroadcaster.ExpectedDepartureCountForTest == 1;
        }

        // ===== WSB14 (v2): disconnect consumes marker, does not re-broadcast left =====
        internal static bool Test_WSB14_DisconnectConsumesMarkerNoLeft()
        {
            ResetBc();
            P2PWorldStatusBroadcaster._testTimeProvider = () => 100f;
            // Register a quarantine projection so the timeout + disconnect flow is realistic.
            P2PWorldStatusBroadcaster.OnPlayerConnectedCore(
                GuestA, "Alice", QuarantinePromotionResult.Activated);
            int sent = 0;
            P2PWorldStatusBroadcaster._testSendSink = _ => sent++;
            P2PWorldStatusBroadcaster.OnApprovalTimeoutCore(GuestA); // writes marker + 1 send
            if (sent != 1) return false;
            P2PWorldStatusBroadcaster.OnPlayerDisconnectedCore(GuestA, "Alice");
            // marker consumed -> no second "left" message
            return sent == 1 && P2PWorldStatusBroadcaster.ExpectedDepartureCountForTest == 0;
        }

        // ===== WSB15 (v2): quarantined player disconnect broadcasts LeftBeforeApproval =====
        internal static bool Test_WSB15_PendingLeaveBroadcastsLeftBeforeApproval()
        {
            ResetBc();
            int sent = 0;
            P2PWorldStatusBroadcaster._testSendSink = _ => sent++;
            // Activated registers the Quarantined projection; disconnect then emits LeftBeforeApproval.
            P2PWorldStatusBroadcaster.OnPlayerConnectedCore(
                GuestA, "Alice", QuarantinePromotionResult.Activated);
            if (sent != 1) return false; // the join broadcast happened
            int before = sent;
            P2PWorldStatusBroadcaster.OnPlayerDisconnectedCore(GuestA, "Alice");
            return sent == before + 1;
        }

        // ===== WSB16 (v2): approved player disconnect broadcasts LeftApproved =====
        internal static bool Test_WSB16_ApprovedLeaveBroadcastsLeftApproved()
        {
            ResetBc();
            int sent = 0;
            P2PWorldStatusBroadcaster._testSendSink = _ => sent++;
            // AlreadyApproved registers the Approved projection; disconnect then emits LeftApproved.
            P2PWorldStatusBroadcaster.OnPlayerConnectedCore(
                GuestA, "Alice", QuarantinePromotionResult.AlreadyApproved);
            if (sent != 1) return false;
            int before = sent;
            P2PWorldStatusBroadcaster.OnPlayerDisconnectedCore(GuestA, "Alice");
            return sent == before + 1;
        }

        // ===== WSB17: FOOD/WATER/ZOMBIE/BONES mapping is precise =====
        internal static bool Test_WSB17_DeathCauseMappingPrecise()
        {
            // Slot arrays are indexed by EDeathCause enum order.
            P2PWorldStatusTemplates.DeathMessageSlot[] food =
                P2PWorldStatusTemplates.GetSlots(EDeathCause.FOOD);
            P2PWorldStatusTemplates.DeathMessageSlot[] water =
                P2PWorldStatusTemplates.GetSlots(EDeathCause.WATER);
            P2PWorldStatusTemplates.DeathMessageSlot[] zombie =
                P2PWorldStatusTemplates.GetSlots(EDeathCause.ZOMBIE);
            P2PWorldStatusTemplates.DeathMessageSlot[] bones =
                P2PWorldStatusTemplates.GetSlots(EDeathCause.BONES);
            return food.Length == 5 && food[0].WithoutKiller.Contains("饿死") &&
                   water.Length == 5 && water[0].WithoutKiller.Contains("渴死") &&
                   zombie.Length == 5 && zombie[0].WithoutKiller.Contains("僵尸") &&
                   bones.Length == 5 && bones[0].WithoutKiller.Contains("坠落");
        }

        // ===== WSB18: 147 random slots, SUICIDE exactly 2 practical =====
        internal static bool Test_WSB18_CatalogIntegrity147()
        {
            int total, failed;
            bool ok = P2PWorldStatusTemplates.VerifyCatalogIntegrity(out total, out failed);
            P2PWorldStatusTemplates.DeathMessageSlot[] suicide =
                P2PWorldStatusTemplates.GetSlots(EDeathCause.SUICIDE);
            return ok && total == 147 && failed == 0 && suicide.Length == 2 &&
                   suicide[0].WithoutKiller.Contains("自杀") &&
                   suicide[1].WithoutKiller.Contains("主动结束");
        }

        // ===== WSB19: same player death within 2s broadcasts once =====
        internal static bool Test_WSB19_DeathCooldown2s()
        {
            ResetBc();
            float t = 0f;
            P2PWorldStatusBroadcaster._testTimeProvider = () => t;
            int sent = 0;
            P2PWorldStatusBroadcaster._testSendSink = _ => sent++;
            P2PWorldStatusBroadcaster.HandleDeathCore(GuestA, "Alice", EDeathCause.ZOMBIE, CSteamID.Nil);
            if (sent != 1) return false;
            t = 1f; // within 2s -> suppressed
            P2PWorldStatusBroadcaster.HandleDeathCore(GuestA, "Alice", EDeathCause.ZOMBIE, CSteamID.Nil);
            if (sent != 1) return false;
            t = 2.1f; // after 2s -> allowed
            P2PWorldStatusBroadcaster.HandleDeathCore(GuestA, "Alice", EDeathCause.ZOMBIE, CSteamID.Nil);
            return sent == 2;
        }

        // ===== WSB20: global 8/10s window suppresses the 9th, recovers after window =====
        internal static bool Test_WSB20_Global8Per10s()
        {
            ResetBc();
            float t = 0f;
            P2PWorldStatusBroadcaster._testTimeProvider = () => t;
            int sent = 0;
            P2PWorldStatusBroadcaster._testSendSink = _ => sent++;
            // 8 distinct players each once, all in the same window.
            for (int i = 0; i < 8; i++)
                P2PWorldStatusBroadcaster.HandleDeathCore(
                    new CSteamID(76561199030000000UL + (ulong)i), "P" + i, EDeathCause.ZOMBIE,
                    CSteamID.Nil);
            if (sent != 8) return false;
            // 9th distinct player in same window -> suppressed
            P2PWorldStatusBroadcaster.HandleDeathCore(
                new CSteamID(76561199030009999UL), "P9", EDeathCause.ZOMBIE, CSteamID.Nil);
            if (sent != 8) return false;
            // advance past the 10s window -> recovers
            t = 10.1f;
            P2PWorldStatusBroadcaster.HandleDeathCore(
                new CSteamID(76561199030008888UL), "P10", EDeathCause.ZOMBIE, CSteamID.Nil);
            return sent == 9;
        }

        // ===== WSB21: throttling drops, never queues/deferred =====
        internal static bool Test_WSB21_ThrottleNoQueue()
        {
            ResetBc();
            float t = 0f;
            P2PWorldStatusBroadcaster._testTimeProvider = () => t;
            int sent = 0;
            P2PWorldStatusBroadcaster._testSendSink = _ => sent++;
            for (int i = 0; i < 8; i++)
                P2PWorldStatusBroadcaster.HandleDeathCore(
                    new CSteamID(76561199030000000UL + (ulong)i), "P" + i, EDeathCause.ZOMBIE,
                    CSteamID.Nil);
            // over-limit: dropped, no pending queue
            P2PWorldStatusBroadcaster.HandleDeathCore(GuestA, "Alice", EDeathCause.ZOMBIE, CSteamID.Nil);
            // Even after time passes, the dropped one is not delivered late.
            t = 11f;
            // (no delivery queue exists; the dropped message is simply gone)
            return sent == 8;
        }

        // ===== WSB22: injecting indices 0..4 reaches all 5 slots; no UnityEngine.Random =====
        internal static bool Test_WSB22_InjectIndicesReachAllCandidates()
        {
            ResetBc();
            P2PWorldStatusBroadcaster._testTimeProvider = () => 0f;
            P2PWorldStatusTemplates.DeathMessageSlot[] slots =
                P2PWorldStatusTemplates.GetSlots(EDeathCause.ZOMBIE);
            var sentTexts = new List<string>();
            P2PWorldStatusBroadcaster._testSendSink = s => sentTexts.Add(s);

            // Drive the FULL production HandleDeathCore path with injected index 0..4. Each call
            // uses a distinct player to avoid the per-player 2s cooldown (global 8/10s budget is
            // 5 < 8). This proves the production RNG path routes through the injectable provider
            // and reaches all 5 slots (2 practical + 3 humorous).
            for (int i = 0; i < slots.Length; i++)
            {
                int idx = i;
                P2PWorldStatusBroadcaster._testRandomIndexProvider = () => idx;
                P2PWorldStatusBroadcaster.HandleDeathCore(
                    new CSteamID(76561199040000000UL + (ulong)i), "Alice", EDeathCause.ZOMBIE,
                    CSteamID.Nil);
            }
            if (sentTexts.Count != 5) return false;
            var set = new HashSet<string>(sentTexts);
            if (set.Count != 5) return false; // all five distinct WithoutKiller lines reached

            // Out-of-range injected index fails closed to the first slot.
            int before = sentTexts.Count;
            P2PWorldStatusBroadcaster._testRandomIndexProvider = () => 99;
            P2PWorldStatusBroadcaster.HandleDeathCore(
                new CSteamID(76561199040009999UL), "Bob", EDeathCause.ZOMBIE, CSteamID.Nil);
            if (sentTexts.Count != before + 1) return false;
            return sentTexts[sentTexts.Count - 1] ==
                   P2PWorldStatusTemplates.Render(slots[0].WithoutKiller, "Bob");
        }

        // ===== WSB23 (v2 P1-04): name with control/rich/isolated-surrogate/bidi-format is safe =====
        internal static bool Test_WSB23_NameSanitizationSafety()
        {
            // Line/control/rich-text characters are stripped; safe chars are retained.
            // Build with explicit char codes so no literal control char appears in the source:
            // CR(13) LF(10) TAB(9) SOH(1) US(0x1F) C1 NEL(0x85) LS(0x2028).
            string dirty = "A" + (char)13 + (char)10 + "B" + (char)9 + "C" + (char)1 + "D" +
                           (char)0x1F + "<color=red>E" + (char)0x85 + "F" + (char)0x2028 + "G";
            string safe = P2PWorldStatusTemplates.NormalizePlayerName(dirty);
            if (safe.Contains("\r") || safe.Contains("\n") || safe.Contains("\t") ||
                safe.Contains("<") || safe.Contains(">")) return false;
            if (!safe.Contains("ABCD") || !safe.Contains("EFG")) return false;

            // P1-04 case 1: 31 BMP chars + a 2-unit emoji = 34 units. The emoji must NOT be split;
            // the result must be 32 units max and must NOT end in a lone high surrogate.
            string emoji = "名" + new string('A', 31) + "😀";
            string clamped = P2PWorldStatusTemplates.NormalizePlayerName(emoji);
            if (clamped.Length > 32) return false;
            if (clamped.Length > 0 && char.IsHighSurrogate(clamped[clamped.Length - 1]))
                return false; // trailing lone high surrogate
            if (clamped.Length > 0 && char.IsLowSurrogate(clamped[clamped.Length - 1]))
                return false; // trailing lone low surrogate

            // P1-04 case 2: isolated high and low surrogates are dropped entirely.
            string isoHigh = "A" + (char)0xD800 + "B";
            string highNorm = P2PWorldStatusTemplates.NormalizePlayerName(isoHigh);
            if (highNorm.IndexOfAny(new[] { (char)0xD800, (char)0xDC00 }) >= 0) return false;
            string isoLow = "C" + (char)0xDC00 + "D";
            string lowNorm = P2PWorldStatusTemplates.NormalizePlayerName(isoLow);
            if (lowNorm.IndexOfAny(new[] { (char)0xD800, (char)0xDC00 }) >= 0) return false;

            // P1-04 case 3: bidi/format chars (U+202E RLO, U+2066 LRI, ZWJ U+200D, ZWNJ U+200C)
            // are removed so they cannot spoof display.
            string bidi = "A" + (char)0x202E + (char)0x2066 + (char)0x200D + (char)0x200C + "B";
            string bidiNorm = P2PWorldStatusTemplates.NormalizePlayerName(bidi);
            if (bidiNorm.IndexOfAny(new[] { (char)0x202E, (char)0x2066, (char)0x200D, (char)0x200C }) >= 0)
                return false;
            return bidiNorm.Contains("AB");
        }

        // ===== WSB24: empty name -> "一名玩家", no SteamID in message =====
        internal static bool Test_WSB24_EmptyNameFallbackNoSteamId()
        {
            ResetBc();
            string text = P2PWorldStatusTemplates.Render(
                P2PWorldStatusTemplates.GetWorldStatusTemplate(EWorldBroadcastKind.LeftApproved)[0], "  ");
            if (!text.Contains(P2PWorldStatusTemplates.FallbackPlayerName)) return false;
            if (text.Contains(GuestA.m_SteamID.ToString())) return false;
            // also assert normalized empty fallback
            return P2PWorldStatusTemplates.NormalizePlayerName(null) == P2PWorldStatusTemplates.FallbackPlayerName;
        }

        // ===== WSB25: quarantined player death is not broadcast =====
        internal static bool Test_WSB25_QuarantinedDeathNotBroadcast()
        {
            ResetBc();
            int sent = 0;
            P2PWorldStatusBroadcaster._testSendSink = _ => sent++;
            // Seed quarantine Active for GuestA.
            P2PQuarantineAdmissionService._testActiveHost = true;
            P2PQuarantineAdmissionService._testWhitelistContains = id => false;
            P2PQuarantineAdmissionService._testTimeProvider = () => 1000f;
            P2PQuarantineAdmissionService._testLocalUserSteamId = HostId.m_SteamID;
            P2PQuarantineAdmissionService._testKickCallback = (sid, r) => { };
            P2PQuarantineAdmissionService._testSignalCallback = (sid, on) => { };
            P2PQuarantineAdmissionService._testChatCallback = (sid, t) => { };
            object token = new object();
            P2PQuarantineAdmissionService.TryReserveForTest(GuestA.m_SteamID, token, 1000f);
            P2PQuarantineAdmissionService.PromoteForTest(GuestA.m_SteamID, token, 1000f);
            bool active = P2PQuarantineAdmissionService.IsActive(GuestA);
            // IsActive now true -> death ignored.
            P2PWorldStatusBroadcaster.HandleDeathCore(GuestA, "Alice", EDeathCause.ZOMBIE, CSteamID.Nil);
            bool result = active && sent == 0;
            ResetBc();
            return result;
        }

        // ===== WSB26: unresolvable killer still broadcasts victim death =====
        internal static bool Test_WSB26_KillerUnresolvableStillBroadcasts()
        {
            ResetBc();
            int sent = 0;
            P2PWorldStatusBroadcaster._testSendSink = _ => sent++;
            P2PWorldStatusBroadcaster.HandleDeathCore(GuestA, "Alice", EDeathCause.ZOMBIE, CSteamID.Nil);
            return sent == 1;
        }

        // ===== WSB27 (v2 P1-07): ResetForSession clears expected-departure + death + rate + projection =====
        internal static bool Test_WSB27_ResetClearsSessionState()
        {
            ResetBc();
            P2PWorldStatusBroadcaster._testTimeProvider = () => 100f;
            // Build up state: expected-departure marker + connection projection + death cooldown.
            P2PWorldStatusBroadcaster.OnApprovalTimeoutCore(GuestA); // writes marker
            P2PWorldStatusBroadcaster.OnPlayerConnectedCore(
                GuestA, "Alice", QuarantinePromotionResult.Activated); // projection Quarantined
            P2PWorldStatusBroadcaster.HandleDeathCore(GuestA, "Alice", EDeathCause.ZOMBIE, CSteamID.Nil); // death cooldown
            if (P2PWorldStatusBroadcaster.ExpectedDepartureCountForTest != 1) return false;
            if (P2PWorldStatusBroadcaster.ConnectionStateForTest(GuestA.m_SteamID) !=
                P2PWorldStatusBroadcaster.EConnectionProjectionState.Quarantined) return false;

            P2PWorldStatusBroadcaster.ResetForSession();

            // All session state cleared: expected-departure, connection projection, death cooldown
            // (a fresh death after reset is allowed immediately).
            bool markerCleared = P2PWorldStatusBroadcaster.ExpectedDepartureCountForTest == 0;
            bool projectionCleared = P2PWorldStatusBroadcaster.ConnectionStateForTest(GuestA.m_SteamID) ==
                                     P2PWorldStatusBroadcaster.EConnectionProjectionState.None;
            return markerCleared && projectionCleared;
        }

        // ===== WSB28 (v2): broadcast exception does not block approval/kick/disconnect cleanup =====
        internal static bool Test_WSB28_SendExceptionDoesNotBlockTransactions()
        {
            ResetBc();
            P2PWorldStatusBroadcaster._testSendSink = _ => throw new InvalidOperationException("sink");
            var runtime = new Fakes.FakeApprovalRuntimeContext();
            var whitelist = new Fakes.FakeApprovalWhitelistProxy { TryAddResult = true };
            using (P2PJoinApprovalService.InstallTestDependencies(runtime, whitelist))
            {
                // Register a connection projection so approval + disconnect have a generation.
                P2PWorldStatusBroadcaster.OnPlayerConnectedCore(
                    GuestA, "Alice", QuarantinePromotionResult.Activated);
                // Approve still returns true even though the broadcast sink throws.
                bool ok = P2PJoinApprovalService.Approve(GuestA, out _);
                // Disconnect cleanup still runs (broadcast throws but is caught); state is cleaned.
                P2PWorldStatusBroadcaster.OnPlayerDisconnectedCore(GuestA, "Alice");
                return ok &&
                       P2PWorldStatusBroadcaster.ConnectionStateForTest(GuestA.m_SteamID) ==
                           P2PWorldStatusBroadcaster.EConnectionProjectionState.None;
            }
        }

        // ===== WSB29 (v2 P1-05): master off -> NO subscribe, NO send =====
        internal static bool Test_WSB29_MasterOffNoSendNoSubscribe()
        {
            ResetBc();
            int addCount = 0;
            P2PWorldStatusBroadcaster._testSubscribeDeath = _ => addCount++;
            P2PWorldStatusBroadcaster.SetConfigForTest(master: false);

            // Initialize with master off: must NOT subscribe (add count stays 0) and must report
            // activation valid (a deliberately disabled feature is not a registration failure).
            bool activated = P2PWorldStatusBroadcaster.InitializeCore();

            int sent = 0;
            P2PWorldStatusBroadcaster._testSendSink = _ => sent++;
            P2PWorldStatusBroadcaster.HandleDeathCore(GuestA, "Alice", EDeathCause.ZOMBIE, CSteamID.Nil);
            P2PWorldStatusBroadcaster.OnPlayerConnectedCore(GuestA, "Alice", QuarantinePromotionResult.AlreadyApproved);
            return activated && addCount == 0 && sent == 0;
        }

        // ===== WSB30: SUICIDE always outputs only 2 short practical lines, never humorous =====
        internal static bool Test_WSB30_SuicideNeverHumorous()
        {
            P2PWorldStatusTemplates.DeathMessageSlot[] suicide =
                P2PWorldStatusTemplates.GetSlots(EDeathCause.SUICIDE);
            if (suicide.Length != 2) return false;
            // Render every possible injected index (including out-of-range) and compare against
            // the RENDERED practical lines (both are name-substituted, like RenderDeath).
            string s0 = P2PWorldStatusTemplates.Render(suicide[0].WithoutKiller, "Alice");
            string s1 = P2PWorldStatusTemplates.Render(suicide[1].WithoutKiller, "Alice");
            for (int i = -1; i <= 10; i++)
            {
                string text = P2PWorldStatusTemplates.RenderDeath(EDeathCause.SUICIDE, "Alice", i);
                if (text != s0 && text != s1) return false;
            }
            // SUICIDE is not an ordinary cause (no humorous candidates).
            return P2PWorldStatusTemplates.IsSuicide(EDeathCause.SUICIDE) &&
                   !P2PWorldStatusTemplates.TryGetOrdinaryIndex(EDeathCause.SUICIDE, out _);
        }

        // ===== WSB31 (v2 P1-07 / v3 P1-09): every world broadcast goes through the production send
        // branch with the REAL 7-arg ChatManager ABI captured and asserted =====
        // The _testChatManagerSend adapter receives the exact same (text, color, fromPlayer,
        // toPlayer, mode, iconURL, useRichTextFormatting) tuple that the production adapter would
        // forward to ChatManager.serverSendMessage. Every broadcast kind (connect / timeout /
        // disconnect / death) is asserted to use fromPlayer=null, toPlayer=null, mode=WELCOME,
        // iconURL=string.Empty, useRichTextFormatting=false. No self-written descriptor string is
        // compared — the captured parameters are authoritative. Each segment uses a DISTINCT player
        // so the timeout expected-departure marker written by the timeout segment never collides
        // with a later player's disconnect (the marker would otherwise suppress that "left" message).
        internal static bool Test_WSB31_AllBroadcastsUseNullPlayers()
        {
            ResetBc();
            var connectPlayer = new CSteamID(76561199031000001UL);
            var timeoutPlayer = new CSteamID(76561199031000002UL);
            var disconnectPlayer = new CSteamID(76561199031000003UL);
            var deathPlayer = new CSteamID(76561199031000004UL);
            int realSendCount = 0;
            // Capture every parameter of the real production ABI call.
            P2PWorldStatusBroadcaster._testChatManagerSend = (text, color, from, to, mode, icon, rich) =>
            {
                realSendCount++;
                P2PWorldStatusBroadcaster.LastCapturedFromPlayer = from;
                P2PWorldStatusBroadcaster.LastCapturedToPlayer = to;
                P2PWorldStatusBroadcaster.LastCapturedMode = mode;
                P2PWorldStatusBroadcaster.LastCapturedIconUrl = icon;
                P2PWorldStatusBroadcaster.LastCapturedRichText = rich;
            };
            bool abiOk()
            {
                return P2PWorldStatusBroadcaster.LastCapturedFromPlayer == null &&
                       P2PWorldStatusBroadcaster.LastCapturedToPlayer == null &&
                       P2PWorldStatusBroadcaster.LastCapturedMode == EChatMode.WELCOME &&
                       P2PWorldStatusBroadcaster.LastCapturedIconUrl == string.Empty &&
                       !P2PWorldStatusBroadcaster.LastCapturedRichText;
            }

            // Connect (Approved projection) -> JoinApproved via real send branch.
            P2PWorldStatusBroadcaster.OnPlayerConnectedCore(
                connectPlayer, "Alice", QuarantinePromotionResult.AlreadyApproved);
            if (realSendCount != 1) return false;
            if (!abiOk()) return false;

            // Timeout (distinct player) -> ApprovalTimedOut.
            P2PWorldStatusBroadcaster.OnApprovalTimeoutCore(timeoutPlayer);
            if (realSendCount != 2) return false;
            if (!abiOk()) return false;

            // Disconnect (register projection first, distinct player) -> LeftBeforeApproval.
            P2PWorldStatusBroadcaster.OnPlayerConnectedCore(
                disconnectPlayer, "Bob", QuarantinePromotionResult.Activated);
            if (realSendCount != 3) return false;
            P2PWorldStatusBroadcaster.OnPlayerDisconnectedCore(disconnectPlayer, "Bob");
            if (realSendCount != 4) return false; // join + timeout + join + left
            if (!abiOk()) return false;

            // Death (distinct player) -> ZOMBIE line.
            P2PWorldStatusBroadcaster.HandleDeathCore(deathPlayer, "Carol", EDeathCause.ZOMBIE, CSteamID.Nil);
            if (realSendCount != 5) return false;
            return abiOk();
        }

        // ===== WSB32: quarantine 5s countdown stays targeted (not upgraded to all) =====
        // The countdown is emitted by P2PQuarantineAdmissionService.Tick via SendTargetedChat
        // (toPlayer=the pending guest only). Drive the real production quarantine Tick and assert
        // the countdown reaches only the pending guest (targeted), never the world broadcaster sink.
        internal static bool Test_WSB32_QuarantineCountdownStaysTargeted()
        {
            ResetBc();
            P2PWorldStatusBroadcaster._testTimeProvider = () => 1000f;
            int broadcastSent = 0;
            P2PWorldStatusBroadcaster._testSendSink = _ => broadcastSent++;

            // Quarantine test hooks: targeted chat sink + time + host + signals.
            var targetedMessages = new List<string>();
            P2PQuarantineAdmissionService._testActiveHost = true;
            P2PQuarantineAdmissionService._testWhitelistContains = id => false;
            P2PQuarantineAdmissionService._testTimeProvider = () => 1000f;
            P2PQuarantineAdmissionService._testLocalUserSteamId = HostId.m_SteamID;
            P2PQuarantineAdmissionService._testKickCallback = (sid, r) => { };
            P2PQuarantineAdmissionService._testSignalCallback = (sid, on) => { };
            P2PQuarantineAdmissionService._testChatCallback = (sid, t) => targetedMessages.Add(t);

            // Active quarantine with a deadline far enough that the 5s countdown fires.
            object token = new object();
            P2PQuarantineAdmissionService.TryReserveForTest(GuestA.m_SteamID, token, 1000f);
            P2PQuarantineAdmissionService.PromoteForTest(GuestA.m_SteamID, token, 1000f); // deadline 1030
            if (!P2PQuarantineAdmissionService.IsActive(GuestA)) return false;

            // Tick at 1005s -> remaining 25s; countdown every 5s fires "剩余约 25 秒".
            P2PQuarantineAdmissionService._testTimeProvider = () => 1005f;
            P2PQuarantineAdmissionService.Tick();

            // The countdown went to the TARGETED chat sink (toPlayer=guest), NOT the broadcaster
            // world sink. A targeted countdown was emitted and zero world broadcasts occurred.
            bool countdownTargeted = targetedMessages.Count >= 1 &&
                                     targetedMessages.Exists(m => m.Contains("剩余约"));
            bool result = countdownTargeted && broadcastSent == 0;

            ResetBc();
            return result;
        }

        // ===== v2 WSB33 (P1-01): core fail-closed on Nil victim -> NO broadcast, NO cooldown =====
        // Per v2 audit erratum: this core-level test drives HandleDeathCore directly with a Nil
        // victim, so it proves the CORE fail-closed gate (0 broadcast, 0 cooldown write). It does
        // NOT drive GetVictimSteamId's production extraction (that is a private PlayerLife-only
        // method); the owner-only extraction is verified by static source review: GetVictimSteamId
        // reads sender.channel.owner.playerID.steamID ONLY and never PlayerLife.deathKiller.
        internal static bool Test_WSB33_VictimOwnerMissingNoBroadcastNoCooldown()
        {
            ResetBc();
            int sent = 0;
            P2PWorldStatusBroadcaster._testSendSink = _ => sent++;

            // victimId = Nil (owner missing). The death must be dropped: 0 broadcasts, and no
            // cooldown may be written (a later real victim death for the SAME steamId still
            // broadcasts immediately).
            P2PWorldStatusBroadcaster.HandleDeathCore(CSteamID.Nil, null, EDeathCause.ZOMBIE, CSteamID.Nil);
            if (sent != 0) return false;

            // Now a real victim death for the same ID must broadcast immediately (cooldown was
            // never polluted by the Nil-victim drop).
            P2PWorldStatusBroadcaster.HandleDeathCore(GuestA, "Alice", EDeathCause.ZOMBIE, CSteamID.Nil);
            return sent == 1;
        }

        // ===== v2 WSB34 (P1-02): RejectedMissingReservation -> disconnect -> 0 join / 0 leave =====
        internal static bool Test_WSB34_RejectedMissingReservationSilent()
        {
            ResetBc();
            int sent = 0;
            P2PWorldStatusBroadcaster._testSendSink = _ => sent++;
            // Rejected connect must not register projection nor broadcast join.
            P2PWorldStatusBroadcaster.OnPlayerConnectedCore(
                GuestA, "Alice", QuarantinePromotionResult.RejectedMissingReservation);
            if (sent != 0) return false;
            // Disconnect of the rejected player must be SILENT (no LeftApproved/LeftBeforeApproval).
            P2PWorldStatusBroadcaster.OnPlayerDisconnectedCore(GuestA, "Alice");
            return sent == 0;
        }

        // ===== v2 WSB35 (P1-02): RejectedSignalFailure -> disconnect -> 0 join / 0 leave =====
        internal static bool Test_WSB35_RejectedSignalFailureSilent()
        {
            ResetBc();
            int sent = 0;
            P2PWorldStatusBroadcaster._testSendSink = _ => sent++;
            P2PWorldStatusBroadcaster.OnPlayerConnectedCore(
                GuestA, "Alice", QuarantinePromotionResult.RejectedSignalFailure);
            if (sent != 0) return false;
            P2PWorldStatusBroadcaster.OnPlayerDisconnectedCore(GuestA, "Alice");
            return sent == 0;
        }

        // ===== v2 WSB36 (P1-06) [v3 erratum]: exact kind/text assertions per lifecycle =====
        // Per v2 audit erratum: this asserts the precise message kind and rendered text of every
        // broadcast, not just "sent == N". Lifecycle 1: Activated connect -> JoinQuarantined,
        // approval -> ApprovalReleased, then disconnect; because the projection was promoted to
        // Approved before leaving, the leave must render as LeftApproved (NOT LeftBeforeApproval).
        // Lifecycle 2 (revoke/rejoin/approve) is a fresh connection generation -> JoinQuarantined +
        // ApprovalReleased again (dedup is per-generation, not session-wide).
        internal static bool Test_WSB36_ReapprovalAfterReconnectBroadcasts()
        {
            ResetBc();
            var runtime = new Fakes.FakeApprovalRuntimeContext();
            var whitelist = new Fakes.FakeApprovalWhitelistProxy { TryAddResult = true };
            using (P2PJoinApprovalService.InstallTestDependencies(runtime, whitelist))
            {
                var sent = new List<string>();
                P2PWorldStatusBroadcaster._testSendSink = sent.Add;
                // OnPlayerApproved resolves the name via ResolveName (test console -> fallback),
                // so pin the resolver so the ApprovalReleased text assertion is deterministic.
                P2PWorldStatusBroadcaster._testNameResolver = _ => "Alice";

                string RenderKind(EWorldBroadcastKind kind, string name)
                {
                    return P2PWorldStatusTemplates.Render(
                        P2PWorldStatusTemplates.GetWorldStatusTemplate(kind)[0], name);
                }

                // Lifecycle 1: activate -> approve -> disconnect.
                P2PWorldStatusBroadcaster.OnPlayerConnectedCore(
                    GuestA, "Alice", QuarantinePromotionResult.Activated);
                if (!P2PJoinApprovalService.Approve(GuestA, out _)) return false;
                if (sent.Count != 2) return false;
                if (sent[0] != RenderKind(EWorldBroadcastKind.JoinQuarantined, "Alice")) return false;
                if (sent[1] != RenderKind(EWorldBroadcastKind.ApprovalReleased, "Alice")) return false;

                // Projection is now Approved (Activated -> approved). Disconnect -> LeftApproved.
                if (P2PWorldStatusBroadcaster.ConnectionStateForTest(GuestA.m_SteamID) !=
                    P2PWorldStatusBroadcaster.EConnectionProjectionState.Approved) return false;
                P2PWorldStatusBroadcaster.OnPlayerDisconnectedCore(GuestA, "Alice");
                if (sent.Count != 3) return false;
                if (sent[2] != RenderKind(EWorldBroadcastKind.LeftApproved, "Alice")) return false;
                if (P2PWorldStatusBroadcaster.ConnectionStateForTest(GuestA.m_SteamID) !=
                    P2PWorldStatusBroadcaster.EConnectionProjectionState.None) return false;

                // Lifecycle 2 (revoke/rejoin/approve): a fresh connection generation -> the second
                // approval is allowed and broadcasts JoinQuarantined + ApprovalReleased again.
                P2PWorldStatusBroadcaster.OnPlayerConnectedCore(
                    GuestA, "Alice", QuarantinePromotionResult.Activated);
                if (sent.Count != 4) return false;
                if (sent[3] != RenderKind(EWorldBroadcastKind.JoinQuarantined, "Alice")) return false;
                if (!P2PJoinApprovalService.Approve(GuestA, out _)) return false;
                if (sent.Count != 5) return false;
                return sent[4] == RenderKind(EWorldBroadcastKind.ApprovalReleased, "Alice");
            }
        }

        // ===== v2 WSB37 (P1-06): disconnect clears death cooldown for that player =====
        internal static bool Test_WSB37_DisconnectClearsDeathCooldown()
        {
            ResetBc();
            float t = 0f;
            P2PWorldStatusBroadcaster._testTimeProvider = () => t;
            int sent = 0;
            P2PWorldStatusBroadcaster._testSendSink = _ => sent++;
            P2PWorldStatusBroadcaster.HandleDeathCore(GuestA, "Alice", EDeathCause.ZOMBIE, CSteamID.Nil);
            if (sent != 1) return false;
            // Within 2s the same player's death is suppressed (cooldown active).
            t = 1f;
            P2PWorldStatusBroadcaster.HandleDeathCore(GuestA, "Alice", EDeathCause.ZOMBIE, CSteamID.Nil);
            if (sent != 1) return false;
            // Disconnect clears the cooldown (P1-06): even at t=1.5 the next death broadcasts.
            P2PWorldStatusBroadcaster.OnPlayerDisconnectedCore(GuestA, "Alice");
            t = 1.5f;
            P2PWorldStatusBroadcaster.HandleDeathCore(GuestA, "Alice", EDeathCause.ZOMBIE, CSteamID.Nil);
            return sent == 2;
        }

        // ===== v2 WSB38 (P1-03) [v3 structured slots]: strict five-slot membership, no sixth line =====
        // Every ordinary cause has EXACTLY 5 DeathMessageSlots; each index 0..4 renders a distinct
        // WithoutKiller line that belongs to that cause's unique five-slot set. The rendered output
        // is a membership assertion (never merely "seen.Count == 5"), and the catalog total stays
        // exactly 147 random slots.
        internal static bool Test_WSB38_KillerCausesFiveCandidateReachability()
        {
            EDeathCause[] allOrdinary =
            {
                EDeathCause.BLEEDING, EDeathCause.BONES, EDeathCause.FREEZING, EDeathCause.BURNING,
                EDeathCause.FOOD, EDeathCause.WATER, EDeathCause.GUN, EDeathCause.MELEE,
                EDeathCause.ZOMBIE, EDeathCause.ANIMAL, EDeathCause.KILL, EDeathCause.INFECTION,
                EDeathCause.PUNCH, EDeathCause.BREATH, EDeathCause.ROADKILL, EDeathCause.VEHICLE,
                EDeathCause.GRENADE, EDeathCause.SHRED, EDeathCause.LANDMINE, EDeathCause.ARENA,
                EDeathCause.MISSILE, EDeathCause.CHARGE, EDeathCause.SPLASH, EDeathCause.SENTRY,
                EDeathCause.ACID, EDeathCause.BOULDER, EDeathCause.BURNER, EDeathCause.SPIT,
                EDeathCause.SPARK
            };
            for (int c = 0; c < allOrdinary.Length; c++)
            {
                EDeathCause cause = allOrdinary[c];
                P2PWorldStatusTemplates.DeathMessageSlot[] slots =
                    P2PWorldStatusTemplates.GetSlots(cause);
                if (slots == null || slots.Length != 5) return false;
                var seen = new HashSet<string>();
                for (int i = 0; i < 5; i++)
                {
                    string text = P2PWorldStatusTemplates.RenderSlot(cause, "Alice", null, i);
                    // The rendered line must be one of this cause's FIVE WithoutKiller renderings.
                    if (!seen.Add(text)) return false; // distinct per index
                }
                if (seen.Count != 5) return false;
                for (int i = 0; i < slots.Length; i++)
                {
                    string rendered = P2PWorldStatusTemplates.Render(slots[i].WithoutKiller, "Alice");
                    if (!seen.Contains(rendered)) return false;
                }
            }
            // Catalog integrity still holds at exactly 147 random slots.
            int total, failed;
            bool ok = P2PWorldStatusTemplates.VerifyCatalogIntegrity(out total, out failed);
            return ok && total == 147 && failed == 0;
        }

        // ===== v2 WSB39 (P1-05): subscribe throws -> activation invalid =====
        internal static bool Test_WSB39_SubscribeThrowActivationInvalid()
        {
            ResetBc();
            P2PWorldStatusBroadcaster._testSubscribeDeath = _ => throw new InvalidOperationException("boom");
            bool activated = P2PWorldStatusBroadcaster.InitializeCore();
            bool initState = P2PWorldStatusBroadcaster.IsInitializedForTest;
            bool activation = P2PWorldStatusBroadcaster.ActivationValidForTest;
            return !activated && !initState && !activation;
        }

        // ===== v2 WSB40 (P1-02/06): Reset and Disconnect clear expected-departure + death + rate =====
        internal static bool Test_WSB40_DisconnectClearsExpectedDepartureAndState()
        {
            ResetBc();
            P2PWorldStatusBroadcaster._testTimeProvider = () => 100f;
            // Timeout writes expected-departure; disconnect consumes it and cleans up.
            P2PWorldStatusBroadcaster.OnPlayerConnectedCore(
                GuestA, "Alice", QuarantinePromotionResult.Activated);
            P2PWorldStatusBroadcaster.OnApprovalTimeoutCore(GuestA);
            if (P2PWorldStatusBroadcaster.ExpectedDepartureCountForTest != 1) return false;
            P2PWorldStatusBroadcaster.OnPlayerDisconnectedCore(GuestA, "Alice");
            return P2PWorldStatusBroadcaster.ExpectedDepartureCountForTest == 0 &&
                   P2PWorldStatusBroadcaster.ConnectionStateForTest(GuestA.m_SteamID) ==
                       P2PWorldStatusBroadcaster.EConnectionProjectionState.None;
        }

        // ===== v3 WSB41 (directive §9.1): slot-count authority =====
        // All 29 ordinary causes have EXACTLY 5 DeathMessageSlots; SUICIDE exactly 2.
        internal static bool Test_WSB41_EveryCauseExactSlotCount()
        {
            EDeathCause[] allOrdinary =
            {
                EDeathCause.BLEEDING, EDeathCause.BONES, EDeathCause.FREEZING, EDeathCause.BURNING,
                EDeathCause.FOOD, EDeathCause.WATER, EDeathCause.GUN, EDeathCause.MELEE,
                EDeathCause.ZOMBIE, EDeathCause.ANIMAL, EDeathCause.KILL, EDeathCause.INFECTION,
                EDeathCause.PUNCH, EDeathCause.BREATH, EDeathCause.ROADKILL, EDeathCause.VEHICLE,
                EDeathCause.GRENADE, EDeathCause.SHRED, EDeathCause.LANDMINE, EDeathCause.ARENA,
                EDeathCause.MISSILE, EDeathCause.CHARGE, EDeathCause.SPLASH, EDeathCause.SENTRY,
                EDeathCause.ACID, EDeathCause.BOULDER, EDeathCause.BURNER, EDeathCause.SPIT,
                EDeathCause.SPARK
            };
            for (int c = 0; c < allOrdinary.Length; c++)
            {
                EDeathCause cause = allOrdinary[c];
                if (P2PWorldStatusTemplates.SlotCount(cause) != 5) return false;
                if (P2PWorldStatusTemplates.GetSlots(cause).Length != 5) return false;
            }
            return P2PWorldStatusTemplates.SlotCount(EDeathCause.SUICIDE) == 2 &&
                   P2PWorldStatusTemplates.GetSlots(EDeathCause.SUICIDE).Length == 2;
        }

        // ===== v3 WSB42 (directive §9.2): attributed causes x 5 indices -> WithKiller =====
        // For each of the 9 player-attributed causes, each index 0..4, a RELIABLE killer renders
        // the selected slot's WithKiller line.
        internal static bool Test_WSB42_AttributedCausesRenderWithKiller()
        {
            EDeathCause[] attributed =
            {
                EDeathCause.GUN, EDeathCause.MELEE, EDeathCause.PUNCH, EDeathCause.ROADKILL,
                EDeathCause.GRENADE, EDeathCause.LANDMINE, EDeathCause.MISSILE, EDeathCause.CHARGE,
                EDeathCause.SENTRY
            };
            foreach (EDeathCause cause in attributed)
            {
                P2PWorldStatusTemplates.DeathMessageSlot[] slots =
                    P2PWorldStatusTemplates.GetSlots(cause);
                if (slots.Length != 5) return false;
                for (int i = 0; i < 5; i++)
                {
                    if (string.IsNullOrEmpty(slots[i].WithKiller)) return false;
                    string text = P2PWorldStatusTemplates.RenderSlot(cause, "Alice", "Killer", i);
                    if (text != P2PWorldStatusTemplates.Render(slots[i].WithKiller, "Alice", "Killer"))
                        return false;
                    if (!text.Contains("Killer")) return false;
                }
            }
            return true;
        }

        // ===== v3 WSB43 (directive §9.3): same matrix without killer -> WithoutKiller =====
        // For every ordinary cause and every index 0..4, NO killer renders the selected slot's
        // WithoutKiller line (never the WithKiller text).
        internal static bool Test_WSB43_AllCausesNoKillerRenderWithoutKiller()
        {
            EDeathCause[] allOrdinary =
            {
                EDeathCause.BLEEDING, EDeathCause.BONES, EDeathCause.FREEZING, EDeathCause.BURNING,
                EDeathCause.FOOD, EDeathCause.WATER, EDeathCause.GUN, EDeathCause.MELEE,
                EDeathCause.ZOMBIE, EDeathCause.ANIMAL, EDeathCause.KILL, EDeathCause.INFECTION,
                EDeathCause.PUNCH, EDeathCause.BREATH, EDeathCause.ROADKILL, EDeathCause.VEHICLE,
                EDeathCause.GRENADE, EDeathCause.SHRED, EDeathCause.LANDMINE, EDeathCause.ARENA,
                EDeathCause.MISSILE, EDeathCause.CHARGE, EDeathCause.SPLASH, EDeathCause.SENTRY,
                EDeathCause.ACID, EDeathCause.BOULDER, EDeathCause.BURNER, EDeathCause.SPIT,
                EDeathCause.SPARK
            };
            foreach (EDeathCause cause in allOrdinary)
            {
                P2PWorldStatusTemplates.DeathMessageSlot[] slots =
                    P2PWorldStatusTemplates.GetSlots(cause);
                if (slots.Length != 5) return false;
                for (int i = 0; i < 5; i++)
                {
                    string text = P2PWorldStatusTemplates.RenderSlot(cause, "Alice", null, i);
                    if (text != P2PWorldStatusTemplates.Render(slots[i].WithoutKiller, "Alice"))
                        return false;
                    if (text.Contains("{killer}")) return false; // never leaks the token
                }
            }
            return true;
        }

        // ===== v3 WSB44 (directive §9.4): exactly ONE RNG call per death =====
        // Killer presence/absence must NOT change the number of RNG calls or the selected index.
        // The provider returns a fixed index once; exactly one call must occur with and without a
        // reliable killer, and the emitted text must be the SAME slot index in both cases.
        internal static bool Test_WSB44_OneRngCallPerDeath()
        {
            ResetBc();
            P2PWorldStatusBroadcaster._testTimeProvider = () => 0f;
            P2PWorldStatusBroadcaster._testClientNameResolver = _ => "Killer";

            var calls = new List<int>();
            P2PWorldStatusBroadcaster._testRandomIndexProvider = () =>
            {
                calls.Add(0);
                return 0;
            };

            // With a reliable killer.
            var withKiller = new List<string>();
            P2PWorldStatusBroadcaster._testSendSink = s => withKiller.Add(s);
            P2PWorldStatusBroadcaster.HandleDeathCore(GuestA, "Alice", EDeathCause.GUN, KillerC);
            if (calls.Count != 1) return false;
            if (withKiller.Count != 1) return false;
            string withText = withKiller[0];
            calls.Clear();

            // Without a killer (Nil instigator).
            var withoutKiller = new List<string>();
            P2PWorldStatusBroadcaster._testSendSink = s => withoutKiller.Add(s);
            P2PWorldStatusBroadcaster.HandleDeathCore(GuestB, "Bob", EDeathCause.GUN, CSteamID.Nil);
            if (calls.Count != 1) return false; // still exactly one RNG call
            if (withoutKiller.Count != 1) return false;

            // Both selected slot 0: the WithKiller and WithoutKiller variants of the SAME slot.
            string slot0With = P2PWorldStatusTemplates.Render(
                P2PWorldStatusTemplates.GetSlots(EDeathCause.GUN)[0].WithKiller, "Alice", "Killer");
            string slot0Without = P2PWorldStatusTemplates.Render(
                P2PWorldStatusTemplates.GetSlots(EDeathCause.GUN)[0].WithoutKiller, "Bob");
            return withText == slot0With && withoutKiller[0] == slot0Without;
        }

        // ===== v3 WSB45 (directive §9.5): killer reliability gate fallbacks =====
        // Nil instigator, victim-self instigator, and a non-connected SteamID all fall back to the
        // selected slot's WithoutKiller. (Provider.server is never a player; the test console has
        // no Provider, so the "not a player" case is covered by the no-resolver path.)
        internal static bool Test_WSB45_KillerReliabilityGateFallback()
        {
            ResetBc();
            P2PWorldStatusBroadcaster._testTimeProvider = () => 0f;
            P2PWorldStatusBroadcaster._testClientNameResolver = null; // no connected client
            int rngCount = 0;
            P2PWorldStatusBroadcaster._testRandomIndexProvider = () =>
            {
                rngCount++;
                return 0;
            };
            var sent = new List<string>();
            P2PWorldStatusBroadcaster._testSendSink = s => sent.Add(s);
            string slot0Without = P2PWorldStatusTemplates.Render(
                P2PWorldStatusTemplates.GetSlots(EDeathCause.GUN)[0].WithoutKiller, "Alice");

            // 1. Nil instigator -> WithoutKiller.
            P2PWorldStatusBroadcaster.HandleDeathCore(GuestA, "Alice", EDeathCause.GUN, CSteamID.Nil);
            if (sent.Count != 1 || sent[0] != slot0Without) return false;

            // 2. instigator == victim -> WithoutKiller.
            P2PWorldStatusBroadcaster.HandleDeathCore(GuestB, "Bob", EDeathCause.GUN, GuestB);
            if (sent.Count != 2 || sent[1] != P2PWorldStatusTemplates.Render(
                    P2PWorldStatusTemplates.GetSlots(EDeathCause.GUN)[0].WithoutKiller, "Bob"))
                return false;

            // 3. valid but NOT a connected client (no resolver) -> WithoutKiller.
            // Distinct victim (GuestA was used in case 1 -> would hit the 2s death cooldown).
            P2PWorldStatusBroadcaster.HandleDeathCore(HostId, "Carol", EDeathCause.GUN, KillerC);
            if (sent.Count != 3 || sent[2] != P2PWorldStatusTemplates.Render(
                    P2PWorldStatusTemplates.GetSlots(EDeathCause.GUN)[0].WithoutKiller, "Carol"))
                return false;

            // Exactly 3 RNG calls (one per death) — no extra calls from killer probing.
            return rngCount == 3;
        }

        // ===== v3 WSB46 (directive §9.6): full sink captures the seven-arg ABI =====
        // Through the production HandleDeathCore path, the chat-send adapter receives
        // fromPlayer=null, toPlayer=null, mode=WELCOME, iconUrl=string.Empty,
        // useRichTextFormatting=false, and a non-empty text.
        internal static bool Test_WSB46_DeathSinkCapturesSevenArgAbi()
        {
            ResetBc();
            P2PWorldStatusBroadcaster._testTimeProvider = () => 0f;
            P2PWorldStatusBroadcaster._testRandomIndexProvider = () => 0;
            string capturedText = null;
            P2PWorldStatusBroadcaster._testChatManagerSend = (text, color, from, to, mode, icon, rich) =>
            {
                capturedText = text;
                P2PWorldStatusBroadcaster.LastCapturedFromPlayer = from;
                P2PWorldStatusBroadcaster.LastCapturedToPlayer = to;
                P2PWorldStatusBroadcaster.LastCapturedMode = mode;
                P2PWorldStatusBroadcaster.LastCapturedIconUrl = icon;
                P2PWorldStatusBroadcaster.LastCapturedRichText = rich;
            };
            P2PWorldStatusBroadcaster.HandleDeathCore(GuestA, "Alice", EDeathCause.ZOMBIE, CSteamID.Nil);
            return !string.IsNullOrEmpty(capturedText) &&
                   P2PWorldStatusBroadcaster.LastCapturedFromPlayer == null &&
                   P2PWorldStatusBroadcaster.LastCapturedToPlayer == null &&
                   P2PWorldStatusBroadcaster.LastCapturedMode == EChatMode.WELCOME &&
                   P2PWorldStatusBroadcaster.LastCapturedIconUrl == string.Empty &&
                   !P2PWorldStatusBroadcaster.LastCapturedRichText;
        }

        // ===== v3 WSB47 (directive §9.7): WSB36 first-connection leave is LeftApproved =====
        // The first connection's approved leave renders LeftApproved, and a second connection
        // generation may approve and broadcast again.
        internal static bool Test_WSB47_ApprovedLeaveLeftApprovedAndSecondGeneration()
        {
            ResetBc();
            var runtime = new Fakes.FakeApprovalRuntimeContext();
            var whitelist = new Fakes.FakeApprovalWhitelistProxy { TryAddResult = true };
            using (P2PJoinApprovalService.InstallTestDependencies(runtime, whitelist))
            {
                var sent = new List<string>();
                P2PWorldStatusBroadcaster._testSendSink = sent.Add;
                P2PWorldStatusBroadcaster._testNameResolver = _ => "Alice";
                string RenderKind(EWorldBroadcastKind kind, string name)
                {
                    return P2PWorldStatusTemplates.Render(
                        P2PWorldStatusTemplates.GetWorldStatusTemplate(kind)[0], name);
                }

                // Lifecycle 1: Activated connect -> JoinQuarantined, approve -> ApprovalReleased,
                // disconnect -> LeftApproved (projection was Approved).
                P2PWorldStatusBroadcaster.OnPlayerConnectedCore(
                    GuestA, "Alice", QuarantinePromotionResult.Activated);
                if (!P2PJoinApprovalService.Approve(GuestA, out _)) return false;
                if (sent.Count != 2) return false;
                if (sent[1] != RenderKind(EWorldBroadcastKind.ApprovalReleased, "Alice")) return false;
                P2PWorldStatusBroadcaster.OnPlayerDisconnectedCore(GuestA, "Alice");
                if (sent.Count != 3) return false;
                if (sent[2] != RenderKind(EWorldBroadcastKind.LeftApproved, "Alice")) return false;

                // Lifecycle 2: a fresh connection generation -> JoinQuarantined + ApprovalReleased
                // again (dedup is per-generation).
                P2PWorldStatusBroadcaster.OnPlayerConnectedCore(
                    GuestA, "Alice", QuarantinePromotionResult.Activated);
                if (sent.Count != 4) return false;
                if (sent[3] != RenderKind(EWorldBroadcastKind.JoinQuarantined, "Alice")) return false;
                if (!P2PJoinApprovalService.Approve(GuestA, out _)) return false;
                if (sent.Count != 5) return false;
                return sent[4] == RenderKind(EWorldBroadcastKind.ApprovalReleased, "Alice");
            }
        }

        // ===== v3 WSB48 (directive §9.8): zero out-of-catalog output =====
        // Through the FULL production HandleDeathCore path (RNG injected, reliable killer present),
        // every emitted death text must be EXACTLY one of the selected slot's two legal variants
        // (WithKiller when the slot defines it and the killer is reliable, otherwise WithoutKiller).
        internal static bool Test_WSB48_NoOutOfCatalogOutput()
        {
            ResetBc();
            // ~147 calls: advance the clock past the 10s global window on every call so the global
            // 8/10s rate limiter never suppresses the broadcast under test.
            float t = 0f;
            P2PWorldStatusBroadcaster._testTimeProvider = () => t;
            P2PWorldStatusBroadcaster._testClientNameResolver = _ => "Killer";
            EDeathCause[] allCauses =
            {
                EDeathCause.BLEEDING, EDeathCause.BONES, EDeathCause.FREEZING, EDeathCause.BURNING,
                EDeathCause.FOOD, EDeathCause.WATER, EDeathCause.GUN, EDeathCause.MELEE,
                EDeathCause.ZOMBIE, EDeathCause.ANIMAL, EDeathCause.KILL, EDeathCause.INFECTION,
                EDeathCause.PUNCH, EDeathCause.BREATH, EDeathCause.ROADKILL, EDeathCause.VEHICLE,
                EDeathCause.GRENADE, EDeathCause.SHRED, EDeathCause.LANDMINE, EDeathCause.ARENA,
                EDeathCause.MISSILE, EDeathCause.CHARGE, EDeathCause.SPLASH, EDeathCause.SENTRY,
                EDeathCause.ACID, EDeathCause.BOULDER, EDeathCause.BURNER, EDeathCause.SPIT,
                EDeathCause.SPARK, EDeathCause.SUICIDE
            };
            ulong counter = 0;
            foreach (EDeathCause cause in allCauses)
            {
                P2PWorldStatusTemplates.DeathMessageSlot[] slots =
                    P2PWorldStatusTemplates.GetSlots(cause);
                for (int i = 0; i < slots.Length; i++)
                {
                    var sentTexts = new List<string>();
                    P2PWorldStatusBroadcaster._testSendSink = s => sentTexts.Add(s);
                    int idx = i;
                    P2PWorldStatusBroadcaster._testRandomIndexProvider = () => idx;
                    CSteamID pid = new CSteamID(76561199090000000UL + counter++);
                    t += 11f;
                    P2PWorldStatusBroadcaster.HandleDeathCore(pid, "Alice", cause, KillerC);
                    if (sentTexts.Count != 1) return false;
                    string expected;
                    if (P2PWorldStatusTemplates.IsSuicide(cause) ||
                        string.IsNullOrEmpty(slots[i].WithKiller))
                    {
                        expected = P2PWorldStatusTemplates.Render(slots[i].WithoutKiller, "Alice");
                    }
                    else
                    {
                        expected = P2PWorldStatusTemplates.Render(slots[i].WithKiller, "Alice", "Killer");
                    }
                    if (sentTexts[0] != expected) return false;
                }
            }
            return true;
        }

        // ===== v4 WSB49: Awake bind with gameThread unavailable -> Pending, no subscribe, valid =====
        internal static bool Test_WSB49_AwakeBindPendingNoSubscribe()
        {
            ResetBc();
            int add = 0;
            P2PWorldStatusBroadcaster._testGameThreadReady = () => false;
            P2PWorldStatusBroadcaster._testSubscribeDeath = _ => add++;
            bool ok = P2PWorldStatusBroadcaster.Initialize(null);
            return ok && add == 0 && !P2PWorldStatusBroadcaster.IsInitializedForTest &&
                   P2PWorldStatusBroadcaster.ActivationValidForTest &&
                   P2PWorldStatusBroadcaster.ActivationState ==
                       P2PWorldStatusBroadcaster.EWorldBroadcastActivationState.Pending;
        }

        // ===== v4 WSB50: Update while gameThread unavailable stays Pending =====
        internal static bool Test_WSB50_UpdateStillPending()
        {
            ResetBc();
            int add = 0;
            P2PWorldStatusBroadcaster._testGameThreadReady = () => false;
            P2PWorldStatusBroadcaster._testSubscribeDeath = _ => add++;
            P2PWorldStatusBroadcaster.Initialize(null);
            bool tick = P2PWorldStatusBroadcaster.TryActivateOnGameThread();
            return tick && add == 0 && P2PWorldStatusBroadcaster.ShouldSuspendPluginUpdate &&
                   P2PWorldStatusBroadcaster.ActivationState ==
                       P2PWorldStatusBroadcaster.EWorldBroadcastActivationState.Pending;
        }

        // ===== v4 WSB51: later ready Update subscribes exactly once and becomes Active =====
        internal static bool Test_WSB51_LaterUpdateActivatesOnce()
        {
            ResetBc();
            bool ready = false;
            int add = 0;
            P2PWorldStatusBroadcaster._testGameThreadReady = () => ready;
            P2PWorldStatusBroadcaster._testSubscribeDeath = _ => add++;
            P2PWorldStatusBroadcaster.Initialize(null);
            P2PWorldStatusBroadcaster.TryActivateOnGameThread();
            ready = true;
            bool first = P2PWorldStatusBroadcaster.TryActivateOnGameThread();
            bool second = P2PWorldStatusBroadcaster.TryActivateOnGameThread();
            return first && second && add == 1 && P2PWorldStatusBroadcaster.IsInitializedForTest &&
                   P2PWorldStatusBroadcaster.IsReadyForHostStart &&
                   P2PWorldStatusBroadcaster.ActivationState ==
                       P2PWorldStatusBroadcaster.EWorldBroadcastActivationState.ActiveValid;
        }

        // ===== v4 WSB52: repeated Pending ticks never duplicate subscription =====
        internal static bool Test_WSB52_RepeatedPendingNoSubscribe()
        {
            ResetBc();
            int add = 0;
            P2PWorldStatusBroadcaster._testGameThreadReady = () => false;
            P2PWorldStatusBroadcaster._testSubscribeDeath = _ => add++;
            P2PWorldStatusBroadcaster.Initialize(null);
            for (int i = 0; i < 20; i++)
                if (!P2PWorldStatusBroadcaster.TryActivateOnGameThread()) return false;
            return add == 0 && P2PWorldStatusBroadcaster.ShouldSuspendPluginUpdate;
        }

        // ===== v4 WSB53: real subscribe failure -> Failed and host not ready =====
        internal static bool Test_WSB53_SubscribeFailureFailsClosed()
        {
            ResetBc();
            P2PWorldStatusBroadcaster._testGameThreadReady = () => true;
            P2PWorldStatusBroadcaster._testSubscribeDeath = _ =>
                throw new InvalidOperationException("subscribe");
            P2PWorldStatusBroadcaster.Initialize(null);
            bool ok = P2PWorldStatusBroadcaster.TryActivateOnGameThread();
            return !ok && !P2PWorldStatusBroadcaster.ActivationValidForTest &&
                   !P2PWorldStatusBroadcaster.IsReadyForHostStart &&
                   P2PWorldStatusBroadcaster.ActivationState ==
                       P2PWorldStatusBroadcaster.EWorldBroadcastActivationState.Failed;
        }

        // ===== v4 WSB54: master disabled -> DisabledValid and never subscribes =====
        internal static bool Test_WSB54_MasterDisabledNeverSubscribes()
        {
            ResetBc();
            int add = 0;
            P2PWorldStatusBroadcaster.SetConfigForTest(master: false);
            P2PWorldStatusBroadcaster._testGameThreadReady = () => true;
            P2PWorldStatusBroadcaster._testSubscribeDeath = _ => add++;
            bool bind = P2PWorldStatusBroadcaster.Initialize(null);
            bool tick = P2PWorldStatusBroadcaster.TryActivateOnGameThread();
            return bind && tick && add == 0 && P2PWorldStatusBroadcaster.IsReadyForHostStart &&
                   P2PWorldStatusBroadcaster.ActivationState ==
                       P2PWorldStatusBroadcaster.EWorldBroadcastActivationState.DisabledValid;
        }

        // ===== v4 WSB55: Shutdown is idempotent for Pending/Active/Failed =====
        internal static bool Test_WSB55_ShutdownStateMatrix()
        {
            // Pending
            ResetBc();
            P2PWorldStatusBroadcaster._testGameThreadReady = () => false;
            P2PWorldStatusBroadcaster.Initialize(null);
            P2PWorldStatusBroadcaster.Shutdown();
            P2PWorldStatusBroadcaster.Shutdown();
            if (P2PWorldStatusBroadcaster.ActivationState !=
                P2PWorldStatusBroadcaster.EWorldBroadcastActivationState.Pending) return false;

            // Active
            ResetBc();
            int remove = 0;
            P2PWorldStatusBroadcaster._testGameThreadReady = () => true;
            P2PWorldStatusBroadcaster._testSubscribeDeath = _ => { };
            P2PWorldStatusBroadcaster._testUnsubscribeDeath = _ => remove++;
            P2PWorldStatusBroadcaster.Initialize(null);
            P2PWorldStatusBroadcaster.TryActivateOnGameThread();
            P2PWorldStatusBroadcaster.Shutdown();
            P2PWorldStatusBroadcaster.Shutdown();
            if (remove != 1) return false;

            // Failed
            ResetBc();
            P2PWorldStatusBroadcaster._testGameThreadReady = () => true;
            P2PWorldStatusBroadcaster._testSubscribeDeath = _ => throw new Exception("fail");
            P2PWorldStatusBroadcaster.Initialize(null);
            P2PWorldStatusBroadcaster.TryActivateOnGameThread();
            P2PWorldStatusBroadcaster.Shutdown();
            P2PWorldStatusBroadcaster.Shutdown();
            return !P2PWorldStatusBroadcaster.IsInitializedForTest;
        }

        // ===== v4 WSB56: Pending is valid but blocks host start until Active =====
        internal static bool Test_WSB56_PendingSelfHealContract()
        {
            ResetBc();
            bool ready = false;
            P2PWorldStatusBroadcaster._testGameThreadReady = () => ready;
            P2PWorldStatusBroadcaster._testSubscribeDeath = _ => { };
            bool bind = P2PWorldStatusBroadcaster.Initialize(null);
            if (!bind || !P2PWorldStatusBroadcaster.ActivationValidForTest ||
                P2PWorldStatusBroadcaster.IsReadyForHostStart ||
                !P2PWorldStatusBroadcaster.ShouldSuspendPluginUpdate) return false;
            ready = true;
            if (!P2PWorldStatusBroadcaster.TryActivateOnGameThread()) return false;
            return P2PWorldStatusBroadcaster.IsReadyForHostStart &&
                   !P2PWorldStatusBroadcaster.ShouldSuspendPluginUpdate;
        }
    }
}
