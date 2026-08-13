using SteamP2PFriends.Shared;
using Steamworks;
using SteamP2PFriends.Patches;
using SteamP2PFriends.Host;
using SDG.Unturned;
using SteamP2PFriends.UI;
using System.Reflection;

namespace SteamP2PFriends.WhitelistTests
{
    internal static class Stage7_8Tests
    {
        internal static bool Test_R1_IndividualSteamIdRoutesP2P()
        {
            ulong raw = new CSteamID(new AccountID_t(123456U), EUniverse.k_EUniversePublic,
                EAccountType.k_EAccountTypeIndividual).m_SteamID;
            return UnifiedJoinAddressClassifier.Classify(raw.ToString(), out ulong parsed) ==
                   UnifiedJoinAddressKind.SteamP2P && parsed == raw;
        }

        internal static bool Test_R2_GameServerCodeRemainsVanilla()
        {
            ulong raw = new CSteamID(new AccountID_t(123U), 304930U,
                EUniverse.k_EUniversePublic, EAccountType.k_EAccountTypeGameServer).m_SteamID;
            return UnifiedJoinAddressClassifier.Classify(raw.ToString(), out ulong parsed) ==
                   UnifiedJoinAddressKind.Vanilla && parsed == 0UL;
        }

        internal static bool Test_R3_IpDnsAndUrlRemainVanilla()
        {
            string[] values = { "127.0.0.1", "192.168.1.5:27015", "example.com", "api.example.com/server" };
            foreach (string value in values)
            {
                if (UnifiedJoinAddressClassifier.Classify(value, out ulong parsed) != UnifiedJoinAddressKind.Vanilla || parsed != 0UL)
                    return false;
            }
            return true;
        }

        internal static bool Test_R4_WhitespaceIsTrimmed()
        {
            ulong raw = new CSteamID(new AccountID_t(456789U), EUniverse.k_EUniversePublic,
                EAccountType.k_EAccountTypeIndividual).m_SteamID;
            return UnifiedJoinAddressClassifier.Classify("  " + raw + "  ", out ulong parsed) ==
                   UnifiedJoinAddressKind.SteamP2P && parsed == raw;
        }

        internal static bool Test_R5_InvalidNumericRemainsVanilla()
        {
            return UnifiedJoinAddressClassifier.Classify("12345678901234567", out ulong parsed) ==
                   UnifiedJoinAddressKind.Vanilla && parsed == 0UL;
        }

        internal static bool Test_R6_PatchTargetsAndSignaturesAreExact()
        {
            MethodBase routeTarget = MenuPlayConnectP2PRoutePatch.TargetMethod();
            MethodInfo routePrefix = typeof(MenuPlayConnectP2PRoutePatch).GetMethod(
                "Prefix", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            MethodBase indicatorTarget = MenuPlayConnectP2PIndicatorPatch.TargetMethod();
            MethodInfo indicatorPostfix = typeof(MenuPlayConnectP2PIndicatorPatch).GetMethod(
                "Postfix", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            return routeTarget != null && routeTarget.Name == "onClickedConnectButton" &&
                   routePrefix != null && routePrefix.ReturnType == typeof(bool) &&
                   routePrefix.GetParameters().Length == 0 &&
                   indicatorTarget != null && indicatorTarget.Name == "RefreshServerCodeInfo" &&
                   indicatorPostfix != null && indicatorPostfix.ReturnType == typeof(void) &&
                   indicatorPostfix.GetParameters().Length == 0;
        }

        internal static bool Test_R7_KeepDeathRulesOverrideBothPvpAndPve()
        {
            var config = new ModeConfigData(EGameMode.NORMAL);
            config.Players.Lose_Weapons_PvP = true;
            config.Players.Lose_Weapons_PvE = true;
            config.Players.Lose_Clothes_PvP = true;
            config.Players.Lose_Clothes_PvE = true;
            config.Players.Lose_Items_PvP = 1f;
            config.Players.Lose_Items_PvE = 1f;
            config.Players.Lose_Skills_PvP = 0.5f;
            config.Players.Lose_Skills_PvE = 0.5f;
            config.Players.Lose_Skill_Levels_PvP = 3;
            config.Players.Lose_Skill_Levels_PvE = 3;
            config.Players.Lose_Experience_PvP = 0.5f;
            config.Players.Lose_Experience_PvE = 0.5f;

            new P2PRoomRules(false, true, true, true).ApplyTo(config);
            PlayersConfigData p = config.Players;
            return !p.Lose_Weapons_PvP && !p.Lose_Weapons_PvE &&
                   !p.Lose_Clothes_PvP && !p.Lose_Clothes_PvE &&
                   p.Lose_Items_PvP == 0f && p.Lose_Items_PvE == 0f &&
                   p.Lose_Skills_PvP == 1f && p.Lose_Skills_PvE == 1f &&
                   p.Lose_Skill_Levels_PvP == 0 && p.Lose_Skill_Levels_PvE == 0 &&
                   p.Lose_Experience_PvP == 1f && p.Lose_Experience_PvE == 1f;
        }

        internal static bool Test_R8_DisabledKeepRulesPreserveModeDefaults()
        {
            var config = new ModeConfigData(EGameMode.HARD);
            float itemsPvp = config.Players.Lose_Items_PvP;
            float skillsPve = config.Players.Lose_Skills_PvE;
            float xpPvp = config.Players.Lose_Experience_PvP;
            bool weaponsPve = config.Players.Lose_Weapons_PvE;

            new P2PRoomRules(true, false, false, false).ApplyTo(config);
            return config.Players.Lose_Items_PvP == itemsPvp &&
                   config.Players.Lose_Skills_PvE == skillsPve &&
                   config.Players.Lose_Experience_PvP == xpPvp &&
                   config.Players.Lose_Weapons_PvE == weaponsPve;
        }

        internal static bool Test_R9_CopyIdActionsPreserveRowAndExplainNativeJoin()
        {
            return P2PPlayerListApprovalDecorator.LocalCopyActionTextForTest == "复制ID" &&
                   P2PPlayerListApprovalDecorator.LocalCopyUsesVanillaRowWidthForTest &&
                   P2PNativeMenuUI.RoomCopyButtonTextForTest == "复制房主 SteamID" &&
                   P2PNativeMenuUI.RoomCopyUsageForTest.Contains("直连");
        }

        internal static bool Test_R10_SessionAdminPolicySeparatesHostAndRemote()
        {
            return P2PSessionAdminPolicy.Decide(true, true) == EP2PSessionAdminAction.Grant &&
                   P2PSessionAdminPolicy.Decide(false, true) == EP2PSessionAdminAction.Preserve &&
                   P2PSessionAdminPolicy.Decide(true, false) == EP2PSessionAdminAction.Grant &&
                   P2PSessionAdminPolicy.Decide(false, false) == EP2PSessionAdminAction.Revoke;
        }

        internal static bool Test_R11_ProductionUsesSnapshotRestoredSessionAdminProjection()
        {
            MethodInfo oldPersistentGrant = typeof(HostManager).GetMethod(
                "GrantAdminToPlayer", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo sessionPolicy = typeof(HostManager).GetMethod(
                "ApplySessionAdminPolicy", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo transientSetter = typeof(HostManager).GetMethod(
                "SetTransientAdminState", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo findAdminIndex = typeof(HostManager).GetMethod(
                "FindAdminIndex", BindingFlags.Static | BindingFlags.NonPublic);

            return oldPersistentGrant == null && sessionPolicy != null &&
                   transientSetter != null && findAdminIndex != null;
        }

        internal static bool Test_R12_ListenHostRemoteCommandPermissionMatrix()
        {
            return !P2PListenHostCommandPermissionPatch.ShouldBlock(false, true, false, false, false, "/day") &&
                   !P2PListenHostCommandPermissionPatch.ShouldBlock(true, false, false, false, false, "/day") &&
                   !P2PListenHostCommandPermissionPatch.ShouldBlock(true, true, true, false, true, "/day") &&
                   !P2PListenHostCommandPermissionPatch.ShouldBlock(true, true, false, false, false, "hello") &&
                   P2PListenHostCommandPermissionPatch.ShouldBlock(true, true, false, false, false, "/day") &&
                   P2PListenHostCommandPermissionPatch.ShouldBlock(true, true, false, true, false, "@night") &&
                   !P2PListenHostCommandPermissionPatch.ShouldBlock(true, true, false, true, true, "/day");
        }

        internal static bool Test_R13_ListenHostCommandPatchTargetIsExact()
        {
            MethodInfo target = P2PListenHostCommandPermissionPatch.TargetMethod();
            MethodInfo prefix = typeof(P2PListenHostCommandPermissionPatch).GetMethod(
                "Prefix", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (target == null || prefix == null || target.Name != "process") return false;

            ParameterInfo[] targetParameters = target.GetParameters();
            ParameterInfo[] prefixParameters = prefix.GetParameters();
            return target.ReturnType == typeof(bool) && targetParameters.Length == 3 &&
                   targetParameters[0].ParameterType == typeof(SteamPlayer) &&
                   targetParameters[1].ParameterType == typeof(string) &&
                   targetParameters[2].ParameterType == typeof(bool) &&
                   prefix.ReturnType == typeof(bool) && prefixParameters.Length == 3;
        }

        internal static bool Test_R14_DirectIpListenSocketRedirectTargetIsExact()
        {
            MethodInfo target = SteamUserP2PRedirectPatch.ResolveCreateListenSocketIPTargetForTest();
            MethodInfo prefix = typeof(SteamUserP2PRedirectPatch).GetMethod(
                nameof(SteamUserP2PRedirectPatch.CreateListenSocketIP_Prefix),
                BindingFlags.Static | BindingFlags.Public);
            if (target == null || prefix == null) return false;

            ParameterInfo[] targetParameters = target.GetParameters();
            ParameterInfo[] prefixParameters = prefix.GetParameters();
            return target.ReturnType == typeof(HSteamListenSocket) && targetParameters.Length == 3 &&
                   targetParameters[0].ParameterType == typeof(SteamNetworkingIPAddr).MakeByRefType() &&
                   targetParameters[1].ParameterType == typeof(int) &&
                   targetParameters[2].ParameterType == typeof(SteamNetworkingConfigValue_t[]) &&
                   prefix.ReturnType == typeof(bool) && prefixParameters.Length == 4;
        }

        internal static bool Test_R15_DirectIpEndpointUsesSinglePort()
        {
            bool ok = UnifiedJoinAddressClassifier.TryBuildDirectIpEndpoint(
                "26.196.34.90", 27016, out Unturned.SystemEx.IPv4Address address,
                out ushort queryPort, out ushort connectionPort);
            return ok && address.ToString() == "26.196.34.90" &&
                   queryPort == 27016 && connectionPort == 27016;
        }

        internal static bool Test_R16_DirectIpInlinePortOverridesField()
        {
            bool ok = UnifiedJoinAddressClassifier.TryBuildDirectIpEndpoint(
                "192.168.1.25:28015", 27015, out _, out ushort queryPort,
                out ushort connectionPort);
            return ok && queryPort == 28015 && connectionPort == 28015;
        }

        internal static bool Test_R17_DirectIpRejectsZeroPortAndNonIpHosts()
        {
            return !UnifiedJoinAddressClassifier.TryBuildDirectIpEndpoint(
                       "26.196.34.90", 0, out _, out _, out _) &&
                   !UnifiedJoinAddressClassifier.TryBuildDirectIpEndpoint(
                       "example.com", 27015, out _, out _, out _) &&
                   !UnifiedJoinAddressClassifier.TryBuildDirectIpEndpoint(
                       "76561199030780228", 27015, out _, out _, out _);
        }

        internal static bool Test_R18_DirectIpRoutePatchStillHasExactAbi()
        {
            MethodBase target = MenuPlayConnectP2PRoutePatch.TargetMethod();
            MethodInfo prefix = typeof(MenuPlayConnectP2PRoutePatch).GetMethod(
                "Prefix", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            return target != null && target.Name == "onClickedConnectButton" &&
                   prefix != null && prefix.ReturnType == typeof(bool) &&
                   prefix.GetParameters().Length == 0;
        }

        internal static bool Test_R19_RoomPersistenceContractExists()
        {
            string[] fields =
            {
                "LastRoomMode", "LastRoomCheats", "LastRoomPvp", "LastRoomKeepInventory",
                "LastRoomKeepSkills", "LastRoomKeepExperience"
            };
            foreach (string name in fields)
            {
                if (typeof(SteamP2PFriendsPlugin).GetField(name,
                    BindingFlags.Static | BindingFlags.Public) == null) return false;
            }

            MethodInfo persist = typeof(P2PNativeMenuUI).GetMethod("PersistLastRoomSettings",
                BindingFlags.Static | BindingFlags.NonPublic);
            return persist != null && persist.ReturnType == typeof(void) &&
                   persist.GetParameters().Length == 5;
        }

        internal static bool Test_R20_InvalidPersistedModeFailsClosedToEasy()
        {
            return P2PNativeMenuUI.NormalizePersistedModeForTest(EGameMode.EASY) == EGameMode.EASY &&
                   P2PNativeMenuUI.NormalizePersistedModeForTest(EGameMode.NORMAL) == EGameMode.NORMAL &&
                   P2PNativeMenuUI.NormalizePersistedModeForTest(EGameMode.HARD) == EGameMode.HARD &&
                   P2PNativeMenuUI.NormalizePersistedModeForTest((EGameMode)255) == EGameMode.EASY;
        }

        // ===== Stage 9-2: SakuraFRP single-port Direct-IP =====

        internal static bool Test_R21_SinglePortLanEndpoint()
        {
            bool ok = UnifiedJoinAddressClassifier.TryBuildDirectIpEndpoint(
                "26.196.34.90", 27016, out _, out ushort queryPort, out ushort connectionPort);
            return ok && queryPort == 27016 && connectionPort == 27016;
        }

        internal static bool Test_R22_SinglePortInlineOverride()
        {
            bool ok = UnifiedJoinAddressClassifier.TryBuildDirectIpEndpoint(
                "192.168.1.25:31567", 27015, out _, out ushort queryPort,
                out ushort connectionPort);
            return ok && queryPort == 31567 && connectionPort == 31567;
        }

        internal static bool Test_R23_SinglePortMaxPortValid()
        {
            bool maxOk = UnifiedJoinAddressClassifier.TryBuildDirectIpEndpoint(
                "26.196.34.90", 65535, out _, out ushort maxQuery,
                out ushort maxConnection);
            bool zeroRejected = !UnifiedJoinAddressClassifier.TryBuildDirectIpEndpoint(
                "26.196.34.90", 0, out _, out _, out _);
            return maxOk && maxQuery == 65535 && maxConnection == 65535 && zeroRejected;
        }

        internal static bool Test_R24_SinglePortParametersPredicate()
        {
            bool ok = UnifiedJoinAddressClassifier.TryBuildDirectIpEndpoint(
                "26.196.34.90", 27016, out Unturned.SystemEx.IPv4Address address,
                out ushort queryPort, out ushort connectionPort);
            if (!ok) return false;

            ServerConnectParameters single = new ServerConnectParameters(address, queryPort, connectionPort, "");
            bool singleOk = UnifiedJoinAddressClassifier.IsSinglePortDirectIpParameters(single);

            ServerConnectParameters queryPlusOne = new ServerConnectParameters(
                address, (ushort)(queryPort - 1), connectionPort, "");
            bool dualOk = !UnifiedJoinAddressClassifier.IsSinglePortDirectIpParameters(queryPlusOne);

            ServerConnectParameters zeroConnection = new ServerConnectParameters(address, queryPort, 0, "");
            bool zeroOk = !UnifiedJoinAddressClassifier.IsSinglePortDirectIpParameters(zeroConnection);

            ServerConnectParameters nullParams = null;
            bool nullOk = !UnifiedJoinAddressClassifier.IsSinglePortDirectIpParameters(nullParams);

            return singleOk && dualOk && zeroOk && nullOk;
        }

        internal static bool Test_R25_QueryPortProjectionMatrix()
        {
            // originalResult=false -> never fabricate success.
            if (DirectIpSinglePortQueryPortPatch.ProjectForTest(
                    false, 27015, true, 27016, out _) != false) return false;

            // non-single-port -> unchanged.
            if (DirectIpSinglePortQueryPortPatch.ProjectForTest(
                    true, 27015, false, 27015, out ushort unchanged) != true) return false;
            if (unchanged != 27015) return false;

            // single-port -> project to R (sharedPort).
            if (DirectIpSinglePortQueryPortPatch.ProjectForTest(
                    true, 31566, true, 31567, out ushort projected) != true) return false;
            if (projected != 31567) return false;

            // single-port with sharedPort == 0 -> unchanged (guard).
            if (DirectIpSinglePortQueryPortPatch.ProjectForTest(
                    true, 27015, true, 0, out ushort zeroShared) != true) return false;
            return zeroShared == 27015;
        }

        internal static bool Test_R26_QueryPortPatchAbiExact()
        {
            MethodBase original = DirectIpSinglePortQueryPortPatch.TargetMethod();
            if (original == null || original.Name != "TryGetQueryPort") return false;
            if (original.DeclaringType != typeof(SDG.NetTransport.SteamNetworkingSockets.ClientTransport_SteamNetworkingSockets))
                return false;

            ParameterInfo[] originalParams = original.GetParameters();
            if (originalParams.Length != 1 ||
                originalParams[0].ParameterType != typeof(ushort).MakeByRefType())
                return false;

            MethodInfo postfix = typeof(DirectIpSinglePortQueryPortPatch).GetMethod(
                nameof(DirectIpSinglePortQueryPortPatch.Postfix),
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (postfix == null || postfix.ReturnType != typeof(void)) return false;

            ParameterInfo[] postfixParams = postfix.GetParameters();
            if (postfixParams.Length != 2 ||
                postfixParams[0].ParameterType != typeof(bool).MakeByRefType() ||
                postfixParams[1].ParameterType != typeof(ushort).MakeByRefType())
                return false;

            MethodInfo project = typeof(DirectIpSinglePortQueryPortPatch).GetMethod(
                nameof(DirectIpSinglePortQueryPortPatch.ProjectForTest),
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return project != null && project.ReturnType == typeof(bool) &&
                   project.GetParameters().Length == 5;
        }

        internal static bool Test_R27_VanillaAndSteamIdRoutesUnaffected()
        {
            // Individual SteamID still routes to P2P.
            ulong raw = new CSteamID(new AccountID_t(123456U), EUniverse.k_EUniversePublic,
                EAccountType.k_EAccountTypeIndividual).m_SteamID;
            bool steamP2P = UnifiedJoinAddressClassifier.Classify(raw.ToString(), out ulong parsed) ==
                            UnifiedJoinAddressKind.SteamP2P && parsed == raw;

            // Game server code stays vanilla.
            ulong gsRaw = new CSteamID(new AccountID_t(123U), 304930U,
                EUniverse.k_EUniversePublic, EAccountType.k_EAccountTypeGameServer).m_SteamID;
            bool gameServer = UnifiedJoinAddressClassifier.Classify(gsRaw.ToString(), out _) ==
                              UnifiedJoinAddressKind.Vanilla;

            // DNS / URL stay vanilla.
            bool dns = UnifiedJoinAddressClassifier.Classify("example.com", out _) ==
                       UnifiedJoinAddressKind.Vanilla;
            bool url = UnifiedJoinAddressClassifier.Classify("http://127.0.0.1:27015", out _) ==
                       UnifiedJoinAddressKind.Vanilla;

            // Non-numeric / non-SteamID text stays vanilla.
            bool nonNumeric = UnifiedJoinAddressClassifier.Classify("server-name", out _) ==
                              UnifiedJoinAddressKind.Vanilla;

            return steamP2P && gameServer && dns && url && nonNumeric;
        }
    }
}
