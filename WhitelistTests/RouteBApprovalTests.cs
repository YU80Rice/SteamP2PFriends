using SteamP2PFriends.Host;
using SteamP2PFriends.Patches;
using SteamP2PFriends.WhitelistTests.Fakes;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SteamP2PFriends.WhitelistTests
{
    /// <summary>Route B state-machine regression tests. These tests never start Unturned or Steam.</summary>
    internal static class RouteBApprovalTests
    {
        private static readonly CSteamID HostId = new CSteamID(76561199030780228UL);
        private static readonly CSteamID Guest1 = new CSteamID(76561199721762479UL);
        private static readonly CSteamID Guest2 = new CSteamID(76561199721762480UL);

        internal static bool Test_B1_HandshakePermitIsScopedAndRejectable()
        {
            var runtime = NewRuntime();
            var whitelist = new FakeApprovalWhitelistProxy { ContainsResult = false };
            using (P2PApprovalManager.InstallTestDependencies(runtime, whitelist, signal: (_, __) => { }))
            {
                if (!P2PApprovalManager.CanPermitHandshakeForTest(Guest1))
                    return Fail("unknown P2P guest should pass only the whitelist gate", "permit=false");
                if (P2PApprovalManager.CanPermitHandshakeForTest(HostId))
                    return Fail("host identity must not use Route B bypass", "permit=true");
                if (P2PApprovalManager.RegisterConnectedForTest(Guest1) != P2PApprovalRegistrationResult.PendingQuarantine)
                    return Fail("precondition pending registration", "unexpected result");
                if (!P2PApprovalManager.RejectPlayer(Guest1, out _))
                    return Fail("reject precondition", "false");
                bool permitAfterReject = P2PApprovalManager.CanPermitHandshakeForTest(Guest1);
                if (!permitAfterReject) Fail("rejected guest should be allowed to reapply", "permit=false");
                return permitAfterReject;
            }
        }

        internal static bool Test_B2_NewWorldEntryBecomesPendingQuarantine()
        {
            var runtime = NewRuntime();
            var whitelist = new FakeApprovalWhitelistProxy { ContainsResult = false };
            using (P2PApprovalManager.InstallTestDependencies(runtime, whitelist))
            {
                P2PApprovalRegistrationResult result = P2PApprovalManager.RegisterConnectedForTest(Guest1);
                if (result != P2PApprovalRegistrationResult.PendingQuarantine)
                    return Fail("unknown guest should become pending after world entry", result.ToString());
                if (!P2PApprovalManager.TryGetPending(Guest1, out P2PPendingApproval pending))
                    return Fail("pending entry missing", "false");
                if (pending.Deadline != runtime.RealtimeValue + P2PApprovalManager.ApprovalLifetimeSeconds)
                    return Fail("deadline must start at world entry", pending.Deadline.ToString());
                return P2PApprovalManager.PendingCount == 1 && P2PApprovalManager.IsPending(Guest1);
            }
        }

        internal static bool Test_B3_TrustedVisitorSkipsQuarantine()
        {
            var runtime = NewRuntime();
            var whitelist = new FakeApprovalWhitelistProxy { ContainsResult = true };
            using (P2PApprovalManager.InstallTestDependencies(runtime, whitelist))
            {
                P2PApprovalRegistrationResult result = P2PApprovalManager.RegisterConnectedForTest(Guest1);
                return result == P2PApprovalRegistrationResult.Trusted &&
                       P2PApprovalManager.PendingCount == 0 && !P2PApprovalManager.IsPending(Guest1);
            }
        }

        internal static bool Test_B4_ConcurrentGuestsAreDeduplicatedAndBounded()
        {
            var runtime = NewRuntime();
            var whitelist = new FakeApprovalWhitelistProxy { ContainsResult = false };
            using (P2PApprovalManager.InstallTestDependencies(runtime, whitelist))
            {
                Parallel.For(0, 32, index =>
                {
                    P2PApprovalManager.RegisterConnectedForTest(new CSteamID(76561198000010000UL + (ulong)index));
                });
                P2PApprovalManager.RegisterConnectedForTest(Guest1);
                P2PApprovalManager.RegisterConnectedForTest(Guest1);
                return P2PApprovalManager.PendingCount == P2PApprovalManager.MaxPendingEntries &&
                       P2PApprovalManager.SnapshotPending().Count == P2PApprovalManager.MaxPendingEntries;
            }
        }

        internal static bool Test_B5_ApprovePersistsThenReleases()
        {
            var runtime = NewRuntime();
            var whitelist = new FakeApprovalWhitelistProxy { ContainsResult = false, TryAddResult = true };
            var signals = new List<string>();
            using (P2PApprovalManager.InstallTestDependencies(runtime, whitelist,
                signal: (id, enabled) => signals.Add(id + ":" + enabled), chat: (_, __) => { }))
            {
                if (P2PApprovalManager.RegisterConnectedForTest(Guest1) != P2PApprovalRegistrationResult.PendingQuarantine)
                    return Fail("precondition pending", "unexpected result");
                if (!P2PApprovalManager.ApprovePlayer(Guest1, out string feedback))
                    return Fail("approve should succeed", feedback);
                return whitelist.TryAddCallCount == 1 && whitelist.LastTryAddTarget == Guest1 &&
                       whitelist.LastTryAddTag == P2PApprovalManager.ApprovedTag &&
                       P2PApprovalManager.PendingCount == 0 && signals.Count == 1 && signals[0] == Guest1.m_SteamID + ":False";
            }
        }

        internal static bool Test_B6_PersistenceFailureRetainsQuarantine()
        {
            var runtime = NewRuntime();
            var whitelist = new FakeApprovalWhitelistProxy { ContainsResult = false, TryAddResult = false, TryAddFeedback = "save failed" };
            using (P2PApprovalManager.InstallTestDependencies(runtime, whitelist, signal: (_, __) => { }))
            {
                P2PApprovalManager.RegisterConnectedForTest(Guest1);
                if (P2PApprovalManager.ApprovePlayer(Guest1, out string feedback))
                    return Fail("approval must fail when whitelist persistence fails", "true");
                return feedback == "save failed" && P2PApprovalManager.IsPending(Guest1);
            }
        }

        internal static bool Test_B7_DisconnectAndRejectCleanState()
        {
            var runtime = NewRuntime();
            var whitelist = new FakeApprovalWhitelistProxy { ContainsResult = false };
            int kicks = 0;
            using (P2PApprovalManager.InstallTestDependencies(runtime, whitelist,
                signal: (_, __) => { }, kick: (_, __) => kicks++))
            {
                P2PApprovalManager.RegisterConnectedForTest(Guest1);
                P2PApprovalManager.ForgetDisconnected(Guest1);
                if (P2PApprovalManager.PendingCount != 0)
                    return Fail("disconnect must remove pending state", P2PApprovalManager.PendingCount.ToString());
                P2PApprovalManager.RegisterConnectedForTest(Guest2);
                if (!P2PApprovalManager.RejectPlayer(Guest2, out _))
                    return Fail("reject must remove pending state", "false");
                bool permitAfterReject = P2PApprovalManager.CanPermitHandshakeForTest(Guest2);
                if (!(kicks == 1 && !P2PApprovalManager.IsPending(Guest2) && permitAfterReject))
                    Fail("rejected guest should be able to reconnect", "kicks=" + kicks + " permit=" + permitAfterReject);
                return kicks == 1 && !P2PApprovalManager.IsPending(Guest2) && permitAfterReject;
            }
        }

        internal static bool Test_B8_TimeoutKicksOnceAndPKeyLayoutIsStable()
        {
            var runtime = NewRuntime();
            var whitelist = new FakeApprovalWhitelistProxy { ContainsResult = false };
            int kicks = 0;
            using (P2PApprovalManager.InstallTestDependencies(runtime, whitelist,
                signal: (_, __) => { }, kick: (_, __) => kicks++))
            {
                P2PApprovalManager.RegisterConnectedForTest(Guest1);
                runtime.AdvanceTime(P2PApprovalManager.ApprovalLifetimeSeconds);
                P2PApprovalManager.Tick();
                P2PApprovalManager.Tick();
                return kicks == 1 && P2PApprovalManager.PendingCount == 0 &&
                       Patch_PlayerDashboardPlayersUI.PendingActionWidthForTest == 132 &&
                       Patch_PlayerDashboardPlayersUI.AllowButtonOffsetForTest == -132 &&
                       Patch_PlayerDashboardPlayersUI.RejectButtonOffsetForTest == -64;
            }
        }

        internal static bool Test_B9_RevokeRemovesWhitelistAndKicks()
        {
            var runtime = NewRuntime();
            var whitelist = new FakeApprovalWhitelistProxy
            {
                ContainsResult = true,
                TryRemoveResult = true
            };
            int kicks = 0;
            using (P2PApprovalManager.InstallTestDependencies(runtime, whitelist,
                signal: (_, __) => { }, kick: (_, __) => kicks++))
            {
                if (!P2PApprovalManager.RevokePlayer(Guest1, out string feedback))
                    return Fail("revoke should succeed", feedback);
                return whitelist.TryRemoveCallCount == 1 && whitelist.LastTryRemoveTarget == Guest1 &&
                       kicks == 1 && P2PApprovalManager.CanPermitHandshakeForTest(Guest1) &&
                       Patch_PlayerDashboardPlayersUI.SingleActionWidthForTest == 64 &&
                       Patch_PlayerDashboardPlayersUI.LocalCopyActionTextForTest == "复制ID" &&
                       Patch_PlayerDashboardPlayersUI.RevokeActionTextForTest == "撤销允许";
            }
        }

        internal static bool Test_B10_RevokePersistenceFailureDoesNotKick()
        {
            var runtime = NewRuntime();
            var whitelist = new FakeApprovalWhitelistProxy
            {
                ContainsResult = true,
                TryRemoveResult = false,
                TryRemoveFeedback = "save failed"
            };
            int kicks = 0;
            using (P2PApprovalManager.InstallTestDependencies(runtime, whitelist,
                signal: (_, __) => { }, kick: (_, __) => kicks++))
            {
                bool result = P2PApprovalManager.RevokePlayer(Guest1, out string feedback);
                return !result && feedback == "save failed" && whitelist.TryRemoveCallCount == 1 && kicks == 0;
            }
        }

        internal static bool Test_B11_PendingActionAndCommandGatesAreAuthoritative()
        {
            bool actionBlocked = P2PQuarantineActionGatePatch.ShouldBlockForTest(true, true, false, true);
            bool approvedAllowed = !P2PQuarantineActionGatePatch.ShouldBlockForTest(true, true, false, false);
            bool pendingAdminCommandBlocked = P2PListenHostCommandPermissionPatch.ShouldBlock(
                true, true, false, true, true, true, "/god");
            bool approvedAdminCommandAllowed = !P2PListenHostCommandPermissionPatch.ShouldBlock(
                true, true, false, false, true, true, "/god");
            int discovered = 0;
            Type contextRef = typeof(SDG.Unturned.ServerInvocationContext).MakeByRefType();
            var requiredContext = new[]
            {
                ResolveU3Method("SDG.Unturned.BarricadeDrop", "ReceiveSalvageRequest", contextRef),
                ResolveU3Method("SDG.Unturned.StructureDrop", "ReceiveSalvageRequest", contextRef),
                ResolveU3Method("SDG.Unturned.ResourceManager", "ReceiveForageRequest", contextRef, typeof(byte), typeof(byte), typeof(ushort)),
                ResolveU3Method("SDG.Unturned.InteractableFarm", "ReceiveHarvestRequest", contextRef),
                ResolveU3Method("SDG.Unturned.InteractableDoor", "ReceiveToggleRequest", contextRef, typeof(bool)),
                ResolveU3Method("SDG.Unturned.ItemManager", "ReceiveTakeItemRequest", contextRef, typeof(byte), typeof(byte), typeof(uint), typeof(byte), typeof(byte), typeof(byte), typeof(byte)),
                ResolveU3Method("SDG.Unturned.VehicleManager", "ReceiveEnterVehicleRequest", contextRef, typeof(uint), typeof(byte[]), typeof(byte[]), typeof(byte))
            };
            var requiredOwner = new[]
            {
                ResolveU3Method("SDG.Unturned.PlayerInventory", "ReceiveDragItem", typeof(byte), typeof(byte), typeof(byte), typeof(byte), typeof(byte), typeof(byte), typeof(byte)),
                ResolveU3Method("SDG.Unturned.PlayerInventory", "ReceiveSwapItem", typeof(byte), typeof(byte), typeof(byte), typeof(byte), typeof(byte), typeof(byte), typeof(byte), typeof(byte)),
                ResolveU3Method("SDG.Unturned.PlayerInventory", "ReceiveDropItem", typeof(byte), typeof(byte), typeof(byte)),
                ResolveU3Method("SDG.Unturned.PlayerEquipment", "ReceiveEquipRequest", typeof(byte), typeof(byte), typeof(byte)),
                ResolveU3Method("SDG.Unturned.PlayerEquipment", "ReceiveToggleVisionRequest")
            };
            Type[] assemblyTypes;
            try { assemblyTypes = typeof(SDG.Unturned.Player).Assembly.GetTypes(); }
            catch (System.Reflection.ReflectionTypeLoadException ex) { assemblyTypes = ex.Types; }
            var typesToCheck = new HashSet<Type>(assemblyTypes);
            foreach (Type type in typesToCheck)
            {
                if (type == null) continue;
                foreach (System.Reflection.MethodInfo method in type.GetMethods(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.DeclaredOnly))
                {
                    if (P2PQuarantineActionGatePatch.IsBlockedContextTarget(method))
                        discovered++;
                }
            }
            bool exactContextCoverage = Array.TrueForAll(requiredContext,
                method => method != null && P2PQuarantineActionGatePatch.IsBlockedContextTarget(method));
            bool storageMetadataCoverage = CecilHasSteamCallMethod(
                "SDG.Unturned.InteractableStorage", "ReceiveInteractRequest",
                (int)SDG.Unturned.ESteamCallValidation.SERVERSIDE,
                "SDG.Unturned.ServerInvocationContext&", "System.Boolean");
            bool exactOwnerCoverage = Array.TrueForAll(requiredOwner,
                method => method != null && P2PQuarantineActionGatePatch.IsBlockedOwnerTarget(method));
            System.Reflection.MethodInfo input = null;
            foreach (System.Reflection.MethodInfo candidate in typeof(SDG.Unturned.PlayerInput).GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (candidate.Name != "ReceiveInputs") continue;
                System.Reflection.ParameterInfo[] parameters = candidate.GetParameters();
                if (parameters.Length > 0 && parameters[0].ParameterType ==
                    typeof(SDG.Unturned.ServerInvocationContext).MakeByRefType())
                {
                    input = candidate;
                    break;
                }
            }
            bool inputConsumed = !P2PQuarantineActionGatePatch.IsBlockedContextTarget(input) &&
                !P2PQuarantineActionGatePatch.IsBlockedOwnerTarget(input);
            bool coverage = discovered >= P2PQuarantineActionGatePatch.MinimumExpectedContextTargetCount &&
                exactContextCoverage && storageMetadataCoverage && exactOwnerCoverage && inputConsumed;
            if (!coverage) Fail("generated gameplay RPC coverage incomplete",
                "discovered=" + discovered + " exactContext=" + exactContextCoverage +
                " storageMetadata=" + storageMetadataCoverage + " exactOwner=" + exactOwnerCoverage +
                " inputConsumed=" + inputConsumed);
            return actionBlocked && approvedAllowed && pendingAdminCommandBlocked &&
                   approvedAdminCommandAllowed && coverage;
        }

        private static System.Reflection.MethodInfo ResolveU3Method(string typeName, string methodName,
            params Type[] parameterTypes)
        {
            try
            {
                Type type = typeof(SDG.Unturned.Player).Assembly.GetType(typeName, false);
                return type?.GetMethod(methodName,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance,
                    null, parameterTypes, null);
            }
            catch (TypeLoadException ex)
            {
                Console.WriteLine("    U3 metadata load failed: " + typeName + "." + methodName + " (" + ex.Message + ")");
                return null;
            }
        }

        private static bool CecilHasSteamCallMethod(string typeName, string methodName,
            int expectedValidation, params string[] parameterTypeNames)
        {
            using (Mono.Cecil.ModuleDefinition module = Mono.Cecil.ModuleDefinition.ReadModule(
                typeof(SDG.Unturned.Player).Assembly.Location,
                new Mono.Cecil.ReaderParameters { ReadingMode = Mono.Cecil.ReadingMode.Deferred }))
            {
                Mono.Cecil.TypeDefinition type = module.GetType(typeName);
                if (type == null) return false;
                foreach (Mono.Cecil.MethodDefinition method in type.Methods)
                {
                    if (method.Name != methodName || method.Parameters.Count != parameterTypeNames.Length) continue;
                    bool parametersMatch = true;
                    for (int index = 0; index < parameterTypeNames.Length; index++)
                        parametersMatch &= method.Parameters[index].ParameterType.FullName == parameterTypeNames[index];
                    if (!parametersMatch) continue;
                    foreach (Mono.Cecil.CustomAttribute attribute in method.CustomAttributes)
                    {
                        if (attribute.AttributeType.FullName == "SDG.Unturned.SteamCall" &&
                            attribute.ConstructorArguments.Count == 1 &&
                            Convert.ToInt32(attribute.ConstructorArguments[0].Value) == expectedValidation)
                            return true;
                    }
                }
                return false;
            }
        }

        internal static bool Test_B12_InputSanitizerPreservesNetworkProgress()
        {
            var packet = new SDG.Unturned.WalkingPlayerInputPacket
            {
                clientSimulationFrameNumber = 42,
                recov = 7,
                keys = UInt16.MaxValue,
                primaryAttack = SDG.Unturned.EAttackInputFlags.Start,
                secondaryAttack = SDG.Unturned.EAttackInputFlags.Stop,
                yaw = 90f,
                pitch = 15f,
                analog = 0x22,
                clientPosition = new UnityEngine.Vector3(9f, 8f, 7f),
                clientsideInputs = new List<SDG.Unturned.PlayerInputPacket.ClientRaycast>(),
                serversideInputs = new Queue<SDG.Unturned.InputInfo>()
            };
            packet.clientsideInputs.Add(default);
            packet.serversideInputs.Enqueue(default);
            var authoritative = new UnityEngine.Vector3(1f, 2f, 3f);

            P2PQuarantineClientInputPatch.NeutralizePacket(packet, authoritative);

            return packet.clientSimulationFrameNumber == 42 && packet.recov == 7 &&
                   packet.yaw == 90f && packet.pitch == 15f && packet.keys == 0 &&
                   packet.primaryAttack == SDG.Unturned.EAttackInputFlags.None &&
                   packet.secondaryAttack == SDG.Unturned.EAttackInputFlags.None &&
                   packet.analog == 0x11 && packet.clientPosition == authoritative &&
                   packet.clientsideInputs.Count == 0 && packet.serversideInputs.Count == 0;
        }

        private static FakeApprovalRuntimeContext NewRuntime()
        {
            return new FakeApprovalRuntimeContext
            {
                IsActiveP2PHostValue = true,
                LocalUserValue = HostId,
                RealtimeValue = 1000f
            };
        }

        private static bool Fail(string message, string detail)
        {
            Console.WriteLine("    FAIL: " + message + (string.IsNullOrEmpty(detail) ? string.Empty : " (" + detail + ")"));
            return false;
        }
    }
}
