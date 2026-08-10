using System;

namespace SteamP2PFriends.WhitelistTests
{
    /// <summary>
    /// Stage 7-2-2 纯单元测试入口。
    /// 蓝图 §3：测试入口仅返回 0（全过）或非 0（失败）。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.WriteLine("=== SteamP2PFriends.WhitelistTests (Stage 7-2-2) ===");

            int total = 0;
            int passed = 0;
            int failed = 0;

            RunTest("1. Bootstrap_Success", WhitelistServiceTests.Test_Bootstrap_Success, ref total, ref passed, ref failed);
            RunTest("2a. Bootstrap_SaveFailure_NoDisconnect", WhitelistServiceTests.Test_Bootstrap_SaveFailure_NoDisconnect, ref total, ref passed, ref failed);
            RunTest("2b. Bootstrap_LoadFailure_NoDisconnect", WhitelistServiceTests.Test_Bootstrap_LoadFailure_NoDisconnect, ref total, ref passed, ref failed);
            RunTest("2c. Bootstrap_ContainsFailure_NoDisconnect", WhitelistServiceTests.Test_Bootstrap_ContainsFailure_NoDisconnect, ref total, ref passed, ref failed);
            RunTest("3a. Add_SaveFailure_GatewayOnce", WhitelistServiceTests.Test_Add_SaveFailure_GatewayOnce, ref total, ref passed, ref failed);
            RunTest("3b. Add_LoadFailure_GatewayOnce", WhitelistServiceTests.Test_Add_LoadFailure_GatewayOnce, ref total, ref passed, ref failed);
            RunTest("3c. Add_ContainsFailure_GatewayOnce", WhitelistServiceTests.Test_Add_ContainsFailure_GatewayOnce, ref total, ref passed, ref failed);
            RunTest("3d. Add_SnapshotFailure_GatewayOnce", WhitelistServiceTests.Test_Add_SnapshotFailure_GatewayOnce, ref total, ref passed, ref failed);
            RunTest("4a. Remove_SaveFailure_GatewayOnce", WhitelistServiceTests.Test_Remove_SaveFailure_GatewayOnce, ref total, ref passed, ref failed);
            RunTest("4b. Remove_NoOp_NoSave_NoDisconnect", WhitelistServiceTests.Test_Remove_NoOp_NoSave_NoDisconnect, ref total, ref passed, ref failed);
            RunTest("4c. Remove_SnapshotFailure_GatewayOnce", WhitelistServiceTests.Test_Remove_SnapshotFailure_GatewayOnce, ref total, ref passed, ref failed);
            RunTest("5a. Add_Self_Rejected", WhitelistServiceTests.Test_Add_Self_Rejected, ref total, ref passed, ref failed);
            RunTest("5b. Remove_Self_Rejected", WhitelistServiceTests.Test_Remove_Self_Rejected, ref total, ref passed, ref failed);
            RunTest("5c. Add_InvalidLocalUser_Rejected", WhitelistServiceTests.Test_Add_InvalidLocalUser_Rejected, ref total, ref passed, ref failed);
            RunTest("5d. Remove_InvalidLocalUser_Rejected", WhitelistServiceTests.Test_Remove_InvalidLocalUser_Rejected, ref total, ref passed, ref failed);
            RunTest("6. Add_JudgeId_Equals_LocalUser", WhitelistServiceTests.Test_Add_JudgeId_Equals_LocalUser, ref total, ref passed, ref failed);
            RunTest("7. PersistenceFault_Blocks_Second_Mutate_And_Reset_Restores", WhitelistServiceTests.Test_PersistenceFault_Blocks_Second_Mutate_And_Reset_Restores, ref total, ref passed, ref failed);

            Console.WriteLine();
            Console.WriteLine("--- Stage 7-3 v2 JoinApprovalService ---");

            RunTest("A1. Record_FirstRejection_Registers", JoinApprovalServiceTests.Test_Record_FirstRejection_Registers, ref total, ref passed, ref failed);
            RunTest("A2. Record_InvalidConditions_NotRegistered", JoinApprovalServiceTests.Test_Record_InvalidConditions_NotRegistered, ref total, ref passed, ref failed);
            RunTest("A3. Record_DedupAndAttemptCount", JoinApprovalServiceTests.Test_Record_DedupAndAttemptCount, ref total, ref passed, ref failed);
            RunTest("A4. Record_RateLimit_Cap_Expiry", JoinApprovalServiceTests.Test_Record_RateLimit_Cap_Expiry, ref total, ref passed, ref failed);
            RunTest("A5. RejectForSession_BlocksUntilReset", JoinApprovalServiceTests.Test_RejectForSession_BlocksUntilReset, ref total, ref passed, ref failed);
            RunTest("A6. Approve_Success_RemovesFromPending", JoinApprovalServiceTests.Test_Approve_Success_RemovesFromPending, ref total, ref passed, ref failed);
            RunTest("A7. Approve_Failure_DoesNotRemove", JoinApprovalServiceTests.Test_Approve_Failure_DoesNotRemove, ref total, ref passed, ref failed);
            RunTest("A8. Record_ContainsException_DoesNotThrow", JoinApprovalServiceTests.Test_Record_ContainsException_DoesNotThrow, ref total, ref passed, ref failed);

            Console.WriteLine();
            Console.WriteLine("--- Stage 7-3 v3 JoinApprovalService (CaptureQueue/Epoch/CrossThread) ---");

            RunTest("A9. v3_CaptureQueue_Dedup_Cap_Order", JoinApprovalServiceTests.Test_v3_CaptureQueue_Dedup_Cap_Order, ref total, ref passed, ref failed);
            RunTest("A10. v3_CrossThread_Prefix_Enqueue_MainThread_Drain", JoinApprovalServiceTests.Test_v3_CrossThread_Prefix_Enqueue_MainThread_Drain, ref total, ref passed, ref failed);
            RunTest("A11. v3_Reset_Discards_Old_Epoch", JoinApprovalServiceTests.Test_v3_Reset_Discards_Old_Epoch, ref total, ref passed, ref failed);
            RunTest("A12. v3_ResetAfterSession_Increments_Epoch_And_Clears", JoinApprovalServiceTests.Test_v3_ResetAfterSession_Increments_Epoch_And_Clears, ref total, ref passed, ref failed);

            Console.WriteLine();
            Console.WriteLine("--- Stage 7-3 v4 Approval Drain Decoupling / Main Thread Contract ---");

            RunTest("A13. v4_BusinessDrainSurvivesHudUnavailable", JoinApprovalServiceTests.Test_v4_A13_BusinessDrainSurvivesHudUnavailable, ref total, ref passed, ref failed);
            RunTest("A14. v4_MainThreadContract_ThrowsWhenNotBypassed", JoinApprovalServiceTests.Test_v4_A14_MainThreadContract_ThrowsWhenNotBypassed, ref total, ref passed, ref failed);

            Console.WriteLine();
            Console.WriteLine("--- Stage 7-4 ESC Approval Menu UI Lifecycle / Pause Surface ---");

            RunTest("U13. v4_HostSessionUI_U3DS_NoCreate", UiLifecycleTests.Test_v4_U13_HostSessionUI_U3DS_NoCreate, ref total, ref passed, ref failed);
            RunTest("U14. v4_HostSessionUI_NonHost_NoCreate", UiLifecycleTests.Test_v4_U14_HostSessionUI_NonHost_NoCreate, ref total, ref passed, ref failed);
            RunTest("U15. v4_HostSessionUI_NullParent_NoCreate", UiLifecycleTests.Test_v4_U15_HostSessionUI_NullParent_NoCreate, ref total, ref passed, ref failed);
            RunTest("U16. v4_HostSessionUI_ResetAfterSession_Destroys", UiLifecycleTests.Test_v4_U16_HostSessionUI_ResetAfterSession_Destroys, ref total, ref passed, ref failed);
            RunTest("U19. v4_HostSessionUI_PauseInactive_NoCreate", UiLifecycleTests.Test_v4_U19_HostSessionUI_PauseInactive_NoCreate, ref total, ref passed, ref failed);
            RunTest("U20. v4_PauseSurface_Inactive_ReturnsFalse", UiLifecycleTests.Test_v4_U20_PauseSurface_Inactive_ReturnsFalse, ref total, ref passed, ref failed);
            RunTest("U21. v4_PauseSurface_NullContainer_ReturnsFalse", UiLifecycleTests.Test_v4_U21_PauseSurface_NullContainer_ReturnsFalse, ref total, ref passed, ref failed);
            RunTest("U22. v4_PauseSurface_U3DS_ReturnsFalse", UiLifecycleTests.Test_v4_U22_PauseSurface_U3DS_ReturnsFalse, ref total, ref passed, ref failed);
            RunTest("U17. v3_NativeMenuUI_U3DS_NoCreate", UiLifecycleTests.Test_v3_U17_NativeMenuUI_U3DS_NoCreate, ref total, ref passed, ref failed);
            RunTest("U18. v3_CanTouchClientUi_Override_Scope", UiLifecycleTests.Test_v3_U18_CanTouchClientUi_Override_Scope, ref total, ref passed, ref failed);

            Console.WriteLine();
            Console.WriteLine("--- Stage 7-4 v3 LeftResponsive Layout Geometry ---");

            RunTest("L1. v3_1920x1080_NormalGeometry", ApprovalPanelLayoutTests.Test_v3_L1_1920x1080_NormalGeometry, ref total, ref passed, ref failed);
            RunTest("L2. v3_2560x1440_MaxWidthClamp", ApprovalPanelLayoutTests.Test_v3_L2_2560x1440_MaxWidthClamp, ref total, ref passed, ref failed);
            RunTest("L3. v3_1280x720_NormalMinWidth", ApprovalPanelLayoutTests.Test_v3_L3_1280x720_NormalMinWidth, ref total, ref passed, ref failed);
            RunTest("L4. v3_CompactMode", ApprovalPanelLayoutTests.Test_v3_L4_CompactMode, ref total, ref passed, ref failed);
            RunTest("L5. v3_NarrowWidth_FailClosed", ApprovalPanelLayoutTests.Test_v3_L5_NarrowWidth_FailClosed, ref total, ref passed, ref failed);
            RunTest("L6. v3_NarrowHeight_FailClosed", ApprovalPanelLayoutTests.Test_v3_L6_NarrowHeight_FailClosed, ref total, ref passed, ref failed);
            RunTest("L7. v3_NoOverlapInvariant", ApprovalPanelLayoutTests.Test_v3_L7_NoOverlapInvariant, ref total, ref passed, ref failed);

            Console.WriteLine();
            Console.WriteLine("--- Stage 7-4 v4 ScrollIntegrity (content range / render hash / two-line) ---");

            RunTest("L8. v4_RenderPlanAndContentHeight", ApprovalPanelLayoutTests.Test_v4_L8_RenderPlanAndContentHeight, ref total, ref passed, ref failed);
            RunTest("L9. v4_EmptyContentHeight", ApprovalPanelLayoutTests.Test_v4_L9_EmptyContentHeight, ref total, ref passed, ref failed);
            RunTest("L10. v4_Compact16Scrollable", ApprovalPanelLayoutTests.Test_v4_L10_Compact16Scrollable, ref total, ref passed, ref failed);
            RunTest("L12. v4_TwoLinePlanNoSingleLine", ApprovalPanelLayoutTests.Test_v4_L12_TwoLinePlanNoSingleLine, ref total, ref passed, ref failed);

            Console.WriteLine();
            Console.WriteLine("--- Stage 7-4 v5 ExactRenderSnapshot (per-item snapshot equality) ---");

            RunTest("L13. v5_PendingSnapshotEquals", ApprovalPanelLayoutTests.Test_v5_L13_PendingSnapshotEquals, ref total, ref passed, ref failed);
            RunTest("L14. v5_WhitelistSnapshotEquals", ApprovalPanelLayoutTests.Test_v5_L14_WhitelistSnapshotEquals, ref total, ref passed, ref failed);
            RunTest("L15. v5_EmptySnapshotStability", ApprovalPanelLayoutTests.Test_v5_L15_EmptySnapshotStability, ref total, ref passed, ref failed);
            RunTest("L16. v5_SnapshotCapAndNameIrrelevant", ApprovalPanelLayoutTests.Test_v5_L16_SnapshotCapAndNameIrrelevant, ref total, ref passed, ref failed);

            Console.WriteLine();
            Console.WriteLine("--- Stage 7-4 v6 AtomicSnapshotCommit (fault injection) ---");

            RunTest("L17. v6_PendingSetContentHeightThrows", AtomicSnapshotTests.Test_v6_L17_PendingSetContentHeightThrows, ref total, ref passed, ref failed);
            RunTest("L18. v6_WhitelistThrowsPendingIsolated", AtomicSnapshotTests.Test_v6_L18_WhitelistThrowsPendingIsolated, ref total, ref passed, ref failed);
            RunTest("L19. v6_BuildRowThrowsAtomic", AtomicSnapshotTests.Test_v6_L19_BuildRowThrowsAtomic, ref total, ref passed, ref failed);
            RunTest("L20. v6_InvalidationScope", AtomicSnapshotTests.Test_v6_L20_InvalidationScope, ref total, ref passed, ref failed);
            RunTest("L21. v6_RecoverThenSuccessfulRebuild", AtomicSnapshotTests.Test_v6_L21_RecoverThenSuccessfulRebuild, ref total, ref passed, ref failed);
            RunTest("L22. v6_TabSwitchImmediateRefresh", AtomicSnapshotTests.Test_v6_L22_TabSwitchImmediateRefresh, ref total, ref passed, ref failed);
            RunTest("L23. v6_EmptyListContentHeight", AtomicSnapshotTests.Test_v6_L23_EmptyListContentHeight, ref total, ref passed, ref failed);
            RunTest("L24. v7_SurfaceNullProbe", AtomicSnapshotTests.Test_v7_L24_SurfaceNullProbe, ref total, ref passed, ref failed);
            RunTest("L25. v7_WhitelistEmptyListContentHeight", AtomicSnapshotTests.Test_v7_L25_WhitelistEmptyListContentHeight, ref total, ref passed, ref failed);
            RunTest("L26. v7_WhitelistSurfaceNullProbeCompact", AtomicSnapshotTests.Test_v7_L26_WhitelistSurfaceNullProbeCompact, ref total, ref passed, ref failed);
            RunTest("L27. v7_InvalidScrollCenterIsNotRestored", AtomicSnapshotTests.Test_v7_L27_InvalidScrollCenterIsNotRestored, ref total, ref passed, ref failed);
            RunTest("L28. v7_ValidScrollCenterIsRestored", AtomicSnapshotTests.Test_v7_L28_ValidScrollCenterIsRestored, ref total, ref passed, ref failed);

            Console.WriteLine();
            Console.WriteLine("--- Stage 7-4 SteamPersonaDisplay / Authorization Key ---");

            RunTest("P1. v4_Normalize_Empty_Fallback", SteamPersonaDisplayTests.Test_v4_P1_Normalize_Empty_Fallback, ref total, ref passed, ref failed);
            RunTest("P2. v4_Normalize_ControlChars_Stripped", SteamPersonaDisplayTests.Test_v4_P2_Normalize_ControlChars_Stripped, ref total, ref passed, ref failed);
            RunTest("P3. v4_Normalize_Truncates_32", SteamPersonaDisplayTests.Test_v4_P3_Normalize_Truncates_32, ref total, ref passed, ref failed);
            RunTest("P4. v4_Normalize_Valid_Preserved", SteamPersonaDisplayTests.Test_v4_P4_Normalize_Valid_Preserved, ref total, ref passed, ref failed);
            RunTest("P5. v4_FormatPlayer_KeepsSteamId_AndFallback", SteamPersonaDisplayTests.Test_v4_P5_FormatPlayer_KeepsSteamId_AndFallback, ref total, ref passed, ref failed);
            RunTest("P6. v4_GetRemoteDisplayName_InvalidId_Fallback", SteamPersonaDisplayTests.Test_v4_P6_GetRemoteDisplayName_InvalidId_Fallback, ref total, ref passed, ref failed);

            Console.WriteLine();
            Console.WriteLine("--- Stage 7-3 v3 Harmony Metadata Self-Check ---");

            RunTest("H1. v3_Harmony_Metadata_SelfCheck", HarmonyMetadataTests.Test_v3_Harmony_Metadata_SelfCheck, ref total, ref passed, ref failed);
            RunTest("H2. v4_RealHarmonyGetPatchInfo_VerifiesOwnerAndMethod", HarmonyMetadataTests.Test_v4_H2_RealHarmonyGetPatchInfo_VerifiesOwnerAndMethod, ref total, ref passed, ref failed);
            RunTest("H3. v5_PendingIdentityConstructorPatchActivates", HarmonyMetadataTests.Test_v5_H3_PendingIdentityConstructorPatchActivates, ref total, ref passed, ref failed);
            RunTest("H4. v5_GroupProbeComplexSignaturesActivate", HarmonyMetadataTests.Test_v5_H4_GroupProbeComplexSignaturesActivate, ref total, ref passed, ref failed);
            RunTest("H6. v6_RejectPendingIdentityPrefixActivates", HarmonyMetadataTests.Test_v6_H6_RejectPendingIdentityPrefixActivates, ref total, ref passed, ref failed);
            RunTest("H7. Alpha_AuthorityProbe_16ExactTargetsResolve", HarmonyMetadataTests.Test_Alpha_H7_AuthorityProbe16ExactTargetsResolve, ref total, ref passed, ref failed);
            RunTest("H8. Alpha_ItemAuthorityGate_3ExactHooksResolve", HarmonyMetadataTests.Test_Alpha_H8_ItemAuthorityGateExactHooksResolve, ref total, ref passed, ref failed);

            Console.WriteLine();
            Console.WriteLine("--- Alpha-1 Authoritative Natural Item Generation Gate ---");
            RunTest("G1. FirstCommitBlocksSecondProducer", AuthorityGenerationGateTests.Test_G1_FirstCommitBlocksSecondProducer, ref total, ref passed, ref failed);
            RunTest("G2. AbortAllowsRetry", AuthorityGenerationGateTests.Test_G2_AbortAllowsRetry, ref total, ref passed, ref failed);
            RunTest("G3. ResetInvalidatesOldEpoch", AuthorityGenerationGateTests.Test_G3_ResetInvalidatesOldEpoch, ref total, ref passed, ref failed);
            RunTest("G4. PreparingRejectsReentry", AuthorityGenerationGateTests.Test_G4_PreparingRejectsReentry, ref total, ref passed, ref failed);

            Console.WriteLine();
            Console.WriteLine("--- Stage 7-5 IdentityWaitUX (name display + wait controller) ---");

            RunTest("N1. LocalHostName", Stage7_5Tests.Test_N1_LocalHostName, ref total, ref passed, ref failed);
            RunTest("N2. ConnectedCharacterName", Stage7_5Tests.Test_N2_ConnectedCharacterName, ref total, ref passed, ref failed);
            RunTest("N3. PersonaFallback", Stage7_5Tests.Test_N3_PersonaFallback, ref total, ref passed, ref failed);
            RunTest("N4. UnknownPlayer", Stage7_5Tests.Test_N4_UnknownPlayer, ref total, ref passed, ref failed);
            RunTest("N5. PersonaCacheRateLimit", Stage7_5Tests.Test_N5_PersonaCacheRateLimit, ref total, ref passed, ref failed);
            RunTest("N6. SteamIdUnchanged", Stage7_5Tests.Test_N6_SteamIdUnchanged, ref total, ref passed, ref failed);
            RunTest("N7. ConnectedNameCachedAfterDisconnect", Stage7_5Tests.Test_N7_ConnectedNameCachedAfterDisconnect, ref total, ref passed, ref failed);
            RunTest("N8. PersonaArrivesUpdatesTextOnly", Stage7_5Tests.Test_N8_PersonaArrivesUpdatesTextOnly, ref total, ref passed, ref failed);
            RunTest("N9. NameChangeDoesNotAffectSteamId", Stage7_5Tests.Test_N9_NameChangeDoesNotAffectSteamId, ref total, ref passed, ref failed);
            RunTest("N10. ProbeSafety", Stage7_5Tests.Test_N10_ProbeSafety, ref total, ref passed, ref failed);
            RunTest("N11. PendingCharacterNameCaptureDrain", Stage7_5Tests.Test_N11_PendingCharacterNameCaptureDrain, ref total, ref passed, ref failed);
            RunTest("N12. GroupProbeSessionGateAndMask", Stage7_5Tests.Test_N12_GroupProbeSessionGateAndMask, ref total, ref passed, ref failed);
            RunTest("N13. UnknownPersonaRetriesAfterRateLimit", Stage7_5Tests.Test_N13_UnknownPersonaRetriesAfterRateLimit, ref total, ref passed, ref failed);
            RunTest("N15. PendingIdentityNamePriority", Stage7_5Tests.Test_N15_PendingIdentityNamePriority, ref total, ref passed, ref failed);
            RunTest("W1. BeginAfterWhitelistRejected", Stage7_5Tests.Test_W1_BeginAfterWhitelistRejected, ref total, ref passed, ref failed);
            RunTest("W2. RetryAfter5Seconds", Stage7_5Tests.Test_W2_RetryAfter5Seconds, ref total, ref passed, ref failed);
            RunTest("W3. Cancel", Stage7_5Tests.Test_W3_Cancel, ref total, ref passed, ref failed);
            RunTest("W4. MaxAttemptsCancel", Stage7_5Tests.Test_W4_MaxAttemptsCancel, ref total, ref passed, ref failed);
            RunTest("W5. TimeoutCancel", Stage7_5Tests.Test_W5_TimeoutCancel, ref total, ref passed, ref failed);
            RunTest("W6. NotSafeToRetryNoConnect", Stage7_5Tests.Test_W6_NotSafeToRetryNoConnect, ref total, ref passed, ref failed);
            RunTest("W7. U3DSNoWait", Stage7_5Tests.Test_W7_U3DSNoWait, ref total, ref passed, ref failed);
            RunTest("W8. RejectedRetryDoesNotRenewBudget", Stage7_5Tests.Test_W8_RejectedRetryDoesNotRenewBudget, ref total, ref passed, ref failed);
            RunTest("W9. RepeatedRejectStopsAtOriginalDeadline", Stage7_5Tests.Test_W9_RepeatedRejectStopsAtOriginalDeadline, ref total, ref passed, ref failed);
            RunTest("W10. AcceptedImmediatelyClosesWaitUi", Stage7_5Tests.Test_W10_AcceptedImmediatelyClosesWaitUi, ref total, ref passed, ref failed);
            RunTest("W11. WhitelistedNoGenericAlert", Stage7_5Tests.Test_W11_WhitelistedNoGenericAlert, ref total, ref passed, ref failed);
            RunTest("W12. ExplicitJoinCancelsOldWaitButRetryDoesNot", Stage7_5Tests.Test_W12_ExplicitJoinCancelsOldWaitButRetryDoesNot, ref total, ref passed, ref failed);
            RunTest("W13. ParentReplacementReattachesOneWaitView", Stage7_5Tests.Test_W13_ParentReplacementReattachesOneWaitView, ref total, ref passed, ref failed);
            RunTest("W14. ViewUnavailableStopsWaitingBeforeRetry", Stage7_5Tests.Test_W14_ViewUnavailableStopsWaitingBeforeRetry, ref total, ref passed, ref failed);
            RunTest("W15. DestroyDetachesWaitView", Stage7_5Tests.Test_W15_DestroyDetachesWaitView, ref total, ref passed, ref failed);
            RunTest("W16. Stage76LegacyRetryDisabled", Stage7_5Tests.Test_W16_WhitelistedFailurePresentationRouting, ref total, ref passed, ref failed);
            RunTest("W17. Stage76RateLimitRouteNoRetry", Stage7_5Tests.Test_W17_RateLimitCooldownWithoutGenericAlert, ref total, ref passed, ref failed);
            RunTest("W18. VanillaTeardownMustBeStable", Stage7_5Tests.Test_W18_VanillaTeardownMustBeStable, ref total, ref passed, ref failed);
            RunTest("W19. RepeatedWhitelistReschedulesFromDisconnect", Stage7_5Tests.Test_W19_RepeatedWhitelistReschedulesFromDisconnect, ref total, ref passed, ref failed);

            Console.WriteLine();
            Console.WriteLine("--- Stage 7-6 Quarantine Admission / U Player List ---");
            RunTest("Q1. ReservationCapAndDedup", Stage7_6Tests.Test_Q1_ReservationCapAndDedup, ref total, ref passed, ref failed);
            RunTest("Q2. ReservedExpiresPendingDoesNot", Stage7_6Tests.Test_Q2_ReservedExpiresPendingDoesNot, ref total, ref passed, ref failed);
            RunTest("Q3. ActiveDeadlineStartsOnPromotion", Stage7_6Tests.Test_Q3_ActiveDeadlineStartsOnPromotion, ref total, ref passed, ref failed);
            RunTest("Q4. TimeoutKicksOnceAndCleans", Stage7_6Tests.Test_Q4_TimeoutKicksOnceAndCleans, ref total, ref passed, ref failed);
            RunTest("Q5. ReleaseRequiresWhitelistPostcondition", Stage7_6Tests.Test_Q5_ReleaseRequiresWhitelistPostcondition, ref total, ref passed, ref failed);
            RunTest("Q6. SignalBitCompatibility", Stage7_6Tests.Test_Q6_SignalBitCompatibility, ref total, ref passed, ref failed);
            RunTest("Q7. ManualHarmonyRegistration", Stage7_6Tests.Test_Q7_ManualHarmonyRegistration, ref total, ref passed, ref failed);
            RunTest("Q8. ChatCountdownEveryFiveSeconds", Stage7_6Tests.Test_Q8_ChatCountdownEveryFiveSeconds, ref total, ref passed, ref failed);
            RunTest("Q9. WhitelistPatchUsesIndexedArgumentBinding", Stage7_6Tests.Test_Q9_WhitelistPatchUsesIndexedArgumentBinding, ref total, ref passed, ref failed);
            RunTest("Q10. ServerTargetedCountdownMilestones", Stage7_6Tests.Test_Q10_ServerTargetedCountdownMilestones, ref total, ref passed, ref failed);
            RunTest("Q11. ApprovalButtonIsInsideRow", Stage7_6Tests.Test_Q11_ApprovalButtonIsInsideRow, ref total, ref passed, ref failed);
            RunTest("Q12. PlayerRowPreservesVanillaHeight", Stage7_6Tests.Test_Q12_PlayerRowPreservesVanillaHeight, ref total, ref passed, ref failed);
            RunTest("Q13. ApproveTransitionsToClickableRevoke", Stage7_6Tests.Test_Q13_ApproveTransitionsToClickableRevoke, ref total, ref passed, ref failed);

            Console.WriteLine();
            Console.WriteLine("--- Stage 7-8 Unified Vanilla Connect Routing ---");
            RunTest("R1. IndividualSteamIdRoutesP2P", Stage7_8Tests.Test_R1_IndividualSteamIdRoutesP2P, ref total, ref passed, ref failed);
            RunTest("R2. GameServerCodeRemainsVanilla", Stage7_8Tests.Test_R2_GameServerCodeRemainsVanilla, ref total, ref passed, ref failed);
            RunTest("R3. IpDnsAndUrlRemainVanilla", Stage7_8Tests.Test_R3_IpDnsAndUrlRemainVanilla, ref total, ref passed, ref failed);
            RunTest("R4. WhitespaceIsTrimmed", Stage7_8Tests.Test_R4_WhitespaceIsTrimmed, ref total, ref passed, ref failed);
            RunTest("R5. InvalidNumericRemainsVanilla", Stage7_8Tests.Test_R5_InvalidNumericRemainsVanilla, ref total, ref passed, ref failed);
            RunTest("R6. PatchTargetsAndSignaturesAreExact", Stage7_8Tests.Test_R6_PatchTargetsAndSignaturesAreExact, ref total, ref passed, ref failed);
            RunTest("R7. KeepDeathRulesOverrideBothPvpAndPve", Stage7_8Tests.Test_R7_KeepDeathRulesOverrideBothPvpAndPve, ref total, ref passed, ref failed);
            RunTest("R8. DisabledKeepRulesPreserveModeDefaults", Stage7_8Tests.Test_R8_DisabledKeepRulesPreserveModeDefaults, ref total, ref passed, ref failed);
            RunTest("R9. CopyIdActionsPreserveRowAndExplainNativeJoin", Stage7_8Tests.Test_R9_CopyIdActionsPreserveRowAndExplainNativeJoin, ref total, ref passed, ref failed);
            RunTest("R10. SessionAdminPolicySeparatesHostAndRemote", Stage7_8Tests.Test_R10_SessionAdminPolicySeparatesHostAndRemote, ref total, ref passed, ref failed);
            RunTest("R11. ProductionUsesSnapshotRestoredSessionAdminProjection", Stage7_8Tests.Test_R11_ProductionUsesSnapshotRestoredSessionAdminProjection, ref total, ref passed, ref failed);
            RunTest("R12. ListenHostRemoteCommandPermissionMatrix", Stage7_8Tests.Test_R12_ListenHostRemoteCommandPermissionMatrix, ref total, ref passed, ref failed);
            RunTest("R13. ListenHostCommandPatchTargetIsExact", Stage7_8Tests.Test_R13_ListenHostCommandPatchTargetIsExact, ref total, ref passed, ref failed);
            RunTest("R14. DirectIpListenSocketRedirectTargetIsExact", Stage7_8Tests.Test_R14_DirectIpListenSocketRedirectTargetIsExact, ref total, ref passed, ref failed);
            RunTest("R15. DirectIpEndpointUsesQueryPortPlusOne", Stage7_8Tests.Test_R15_DirectIpEndpointUsesQueryPortPlusOne, ref total, ref passed, ref failed);
            RunTest("R16. DirectIpInlinePortOverridesField", Stage7_8Tests.Test_R16_DirectIpInlinePortOverridesField, ref total, ref passed, ref failed);
            RunTest("R17. DirectIpRejectsUnsafePortsAndNonIpHosts", Stage7_8Tests.Test_R17_DirectIpRejectsUnsafePortsAndNonIpHosts, ref total, ref passed, ref failed);
            RunTest("R18. DirectIpRoutePatchStillHasExactAbi", Stage7_8Tests.Test_R18_DirectIpRoutePatchStillHasExactAbi, ref total, ref passed, ref failed);
            RunTest("R19. RoomPersistenceContractExists", Stage7_8Tests.Test_R19_RoomPersistenceContractExists, ref total, ref passed, ref failed);
            RunTest("R20. InvalidPersistedModeFailsClosedToEasy", Stage7_8Tests.Test_R20_InvalidPersistedModeFailsClosedToEasy, ref total, ref passed, ref failed);

            Console.WriteLine();
            Console.WriteLine($"=== Total: {total} / Passed: {passed} / Failed: {failed} ===");

            return failed == 0 ? 0 : 1;
        }

        private static void RunTest(string name, Func<bool> test, ref int total, ref int passed, ref int failed)
        {
            total++;
            Console.Write($"[{total,2}] {name,-60} ... ");
            try
            {
                bool ok = test();
                if (ok)
                {
                    passed++;
                    Console.WriteLine("PASS");
                }
                else
                {
                    failed++;
                    // 失败原因已在 test 内打印
                    Console.WriteLine("FAIL");
                }
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine("FAIL (exception)");
                Console.WriteLine("    " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
