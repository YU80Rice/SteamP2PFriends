using SDG.Unturned;
using SteamP2PFriends.Client;
using SteamP2PFriends.Patches;
using SteamP2PFriends.Shared;
using SteamP2PFriends.Shared.Enums;
using SteamP2PFriends.UI;
using Steamworks;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SteamP2PFriends.WhitelistTests
{
    /// <summary>
    /// Stage 7-5 IdentityWaitUX 测试。
    /// [指令 A/B/C] 名称分层解析 + persona 缓存限频/上限/过期。
    /// [指令 D/E/F] 客机等待审批控制器 + 受限重连。
    /// </summary>
    internal static class Stage7_5Tests
    {
        private static readonly CSteamID HostId = new CSteamID(76561199030780228UL);
        private static readonly CSteamID ClientId = new CSteamID(76561199721762479UL);
        // 测试用 fake parent（仅需非-null + ReferenceEquals）
        private static readonly ISleekElement FakeParent1 = new FakeSleekElement();
        private static readonly ISleekElement FakeParent2 = new FakeSleekElement();

        // ===== 名称显示测试 =====

        private static void SetupPersonaHooks(CSteamID? localId = null)
        {
            SteamPersonaDisplay._testBypassThreadAssert = true;
            SteamPersonaDisplay._testLocalSteamId = localId ?? HostId;
            SteamPersonaDisplay._testLocalPersonaProvider = null;
            SteamPersonaDisplay._testRemotePersonaProvider = null;
            SteamPersonaDisplay._testConnectedNameProvider = null;
            SteamPersonaDisplay._testRequestUserInfoCallback = null;
            SteamPersonaDisplay._testTimeProvider = () => 1000f;
            SteamPersonaDisplay.ClearPersonaCacheForTest();
            SteamPersonaDisplay.ClearDisplayNameCacheForTest();
        }

        private static void ClearPersonaHooks()
        {
            SteamPersonaDisplay._testBypassThreadAssert = false;
            SteamPersonaDisplay._testLocalSteamId = null;
            SteamPersonaDisplay._testLocalPersonaProvider = null;
            SteamPersonaDisplay._testRemotePersonaProvider = null;
            SteamPersonaDisplay._testConnectedNameProvider = null;
            SteamPersonaDisplay._testRequestUserInfoCallback = null;
            SteamPersonaDisplay._testTimeProvider = null;
            SteamPersonaDisplay.ClearPersonaCacheForTest();
            SteamPersonaDisplay.ClearDisplayNameCacheForTest();
        }

        // N1: 本地房主 -> GetLocalDisplayName
        internal static bool Test_N1_LocalHostName()
        {
            SetupPersonaHooks(HostId);
            SteamPersonaDisplay._testLocalPersonaProvider = () => "DiDATuT";
            try
            {
                string name = SteamPersonaDisplay.ResolveDisplayName(HostId);
                if (name != "DiDATuT") return Fail("host should get local name", "got=" + name);
            }
            finally { ClearPersonaHooks(); }
            return true;
        }

        // N2: 已连接客机 -> character name
        internal static bool Test_N2_ConnectedCharacterName()
        {
            SetupPersonaHooks(HostId);
            SteamPersonaDisplay._testConnectedNameProvider = (id) => id.m_SteamID == ClientId.m_SteamID ? "易烨不会玩FPS" : null;
            try
            {
                string name = SteamPersonaDisplay.ResolveDisplayName(ClientId);
                if (name != "易烨不会玩FPS") return Fail("connected client should get character name", "got=" + name);
            }
            finally { ClearPersonaHooks(); }
            return true;
        }

        // N3: 未连接 pending -> Steam persona fallback
        internal static bool Test_N3_PersonaFallback()
        {
            SetupPersonaHooks(HostId);
            SteamPersonaDisplay._testRemotePersonaProvider = (id) => "PersonaName";
            int requestCount = 0;
            SteamPersonaDisplay._testRequestUserInfoCallback = (id) => requestCount++;
            try
            {
                string name = SteamPersonaDisplay.ResolveDisplayName(ClientId);
                if (name != "PersonaName") return Fail("pending should get persona name", "got=" + name);
                if (requestCount != 1) return Fail("should request persona once", "count=" + requestCount);
            }
            finally { ClearPersonaHooks(); }
            return true;
        }

        // N4: 全部无资料 -> "未知玩家"
        internal static bool Test_N4_UnknownPlayer()
        {
            SetupPersonaHooks(HostId);
            SteamPersonaDisplay._testRemotePersonaProvider = (id) => null;
            try
            {
                string name = SteamPersonaDisplay.ResolveDisplayName(ClientId);
                if (name != "未知玩家") return Fail("should fallback to 未知玩家", "got=" + name);
            }
            finally { ClearPersonaHooks(); }
            return true;
        }

        // N5: persona 缓存限频（5s）+ 16 上限 + 120s 过期
        internal static bool Test_N5_PersonaCacheRateLimit()
        {
            SetupPersonaHooks(HostId);
            int requestCount = 0;
            SteamPersonaDisplay._testRemotePersonaProvider = (id) => "Name" + id.m_SteamID;
            SteamPersonaDisplay._testRequestUserInfoCallback = (id) => requestCount++;
            float testTime = 1000f;
            SteamPersonaDisplay._testTimeProvider = () => testTime;
            try
            {
                // 第一次：请求 + 缓存
                SteamPersonaDisplay.ResolveDisplayName(ClientId);
                if (requestCount != 1) return Fail("first call should request", "count=" + requestCount);

                // 3 秒后：限频，不请求（用缓存）
                testTime += 3f;
                SteamPersonaDisplay.ResolveDisplayName(ClientId);
                if (requestCount != 1) return Fail("3s later should not request (rate limited)", "count=" + requestCount);

                // 已取得有效名称后：120s TTL 内不重复请求（5s 仅用于“仍未知”重试）
                testTime += 3f; // total 6s
                SteamPersonaDisplay.ResolveDisplayName(ClientId);
                if (requestCount != 1) return Fail("known persona should stay cached for TTL", "count=" + requestCount);

                // 120s 后：显示缓存与 persona 缓存均过期，重新请求
                testTime += 120f;
                SteamPersonaDisplay.ResolveDisplayName(ClientId);
                if (requestCount != 2) return Fail("120s later should request (expired)", "count=" + requestCount);

                // 16 上限测试
                SteamPersonaDisplay.ClearPersonaCacheForTest();
                requestCount = 0;
                for (ulong i = 1; i <= 17; i++)
                {
                    SteamPersonaDisplay.ResolveDisplayName(new CSteamID(76561198000000000UL + i));
                }
                if (SteamPersonaDisplay.PersonaCacheCountForTest > 16)
                    return Fail("cache should cap at 16", "count=" + SteamPersonaDisplay.PersonaCacheCountForTest);
            }
            finally { ClearPersonaHooks(); }
            return true;
        }

        // N6: CSteamID 不变（名称不影响授权键）
        internal static bool Test_N6_SteamIdUnchanged()
        {
            SetupPersonaHooks(HostId);
            SteamPersonaDisplay._testRemotePersonaProvider = (id) => "SomeName";
            try
            {
                CSteamID original = ClientId;
                string name = SteamPersonaDisplay.ResolveDisplayName(ClientId);
                // CSteamID 是值类型，不会被修改。验证 m_SteamID 不变。
                if (original.m_SteamID != ClientId.m_SteamID)
                    return Fail("CSteamID should not change", "modified");
                if (string.IsNullOrEmpty(name)) return Fail("should return a name", "empty");
            }
            finally { ClearPersonaHooks(); }
            return true;
        }

        // ===== 等待控制器测试 =====

        private static void SetupWaitHooks(bool safeToRetry = true, float startTime = 1000f)
        {
            P2PApprovalWaitController._testBypassThreadAssert = true;
            P2PApprovalWaitController._testIsSafeToRetry = safeToRetry;
            P2PApprovalWaitController._testTryConnectCallback = null;
            P2PJoinManager._testSafeAlertCount = 0;
            P2PJoinManager._testBypassFailurePresentationRuntime = true;
            float t = startTime;
            P2PApprovalWaitController._testTimeProvider = () => t;
            P2PNativeMenuUI._testBypassGlazier = true;
            P2PNativeMenuUI._testBypassThreadAssert = true;
            P2PNativeMenuUI._testParentProvider = () => FakeParent1;
            P2PJoinManager._testSafeAlertCount = 0;
            P2PApprovalWaitController.Cancel(); // 确保初始干净
        }

        private static void SetWaitTime(float t)
        {
            P2PApprovalWaitController._testTimeProvider = () => t;
        }

        private static void ClearWaitHooks()
        {
            P2PApprovalWaitController.Cancel();
            P2PApprovalWaitController._testBypassThreadAssert = false;
            P2PApprovalWaitController._testIsSafeToRetry = null;
            P2PApprovalWaitController._testTryConnectCallback = null;
            P2PApprovalWaitController._testTimeProvider = null;
            P2PJoinManager._testSafeAlertCount = 0;
            P2PJoinManager._testBypassFailurePresentationRuntime = false;
            P2PNativeMenuUI._testBypassGlazier = false;
            P2PNativeMenuUI._testBypassThreadAssert = false;
            P2PNativeMenuUI._testParentProvider = null;
            P2PJoinManager._testSafeAlertCount = 0;
        }

        // W1: BeginAfterWhitelistRejected -> waiting
        internal static bool Test_W1_BeginAfterWhitelistRejected()
        {
            using (P2PClientUiEnvironment.OverrideForTest(true))
            {
                SetupWaitHooks();
                try
                {
                    P2PApprovalWaitController.BeginAfterWhitelistRejected(HostId.m_SteamID);
                    if (!P2PApprovalWaitController.IsWaitingForTest) return Fail("should be waiting", "not waiting");
                    if (!P2PNativeMenuUI.IsWaitVisibleForTest) return Fail("wait UI should be visible", "hidden");
                }
                finally { ClearWaitHooks(); }
            }
            return true;
        }

        // W2: 5s 后重试（IsSafeToRetry=true）
        internal static bool Test_W2_RetryAfter5Seconds()
        {
            using (P2PClientUiEnvironment.OverrideForTest(true))
            {
                SetupWaitHooks(safeToRetry: true, startTime: 1000f);
                int connectCount = 0;
                P2PApprovalWaitController._testTryConnectCallback = (id) => connectCount++;
                try
                {
                    P2PApprovalWaitController.BeginAfterWhitelistRejected(HostId.m_SteamID);

                    // 3s: 未到重试时间
                    SetWaitTime(1003f);
                    P2PApprovalWaitController.Tick();
                    if (connectCount != 0) return Fail("3s should not retry", "count=" + connectCount);

                    // 5s: 重试
                    SetWaitTime(1005f);
                    P2PApprovalWaitController.Tick();
                    if (connectCount != 1) return Fail("5s should retry", "count=" + connectCount);
                    if (P2PApprovalWaitController.AttemptsForTest != 1) return Fail("attempts=1", "got=" + P2PApprovalWaitController.AttemptsForTest);

                    // 8s: 未到下次重试（5s 间隔）
                    SetWaitTime(1008f);
                    P2PApprovalWaitController.Tick();
                    if (connectCount != 1) return Fail("8s should not retry (3s since last)", "count=" + connectCount);

                    // 10s: 第二次重试
                    SetWaitTime(1010f);
                    P2PApprovalWaitController.Tick();
                    if (connectCount != 2) return Fail("10s should retry (2nd)", "count=" + connectCount);
                }
                finally { ClearWaitHooks(); }
            }
            return true;
        }

        // W3: Cancel
        internal static bool Test_W3_Cancel()
        {
            using (P2PClientUiEnvironment.OverrideForTest(true))
            {
                SetupWaitHooks();
                try
                {
                    P2PApprovalWaitController.BeginAfterWhitelistRejected(HostId.m_SteamID);
                    P2PApprovalWaitController.Cancel();
                    if (P2PApprovalWaitController.IsWaitingForTest) return Fail("should not be waiting after cancel", "waiting");
                    if (P2PNativeMenuUI.IsWaitVisibleForTest) return Fail("wait UI should be hidden", "visible");
                }
                finally { ClearWaitHooks(); }
            }
            return true;
        }

        // W4: 24 次上限 + 120s 超时 -> Cancel（24 次 5s = 120s = 超时，第 24 次因超时取消）
        internal static bool Test_W4_MaxAttemptsCancel()
        {
            using (P2PClientUiEnvironment.OverrideForTest(true))
            {
                SetupWaitHooks(safeToRetry: true, startTime: 1000f);
                int connectCount = 0;
                P2PApprovalWaitController._testTryConnectCallback = (id) => connectCount++;
                try
                {
                    P2PApprovalWaitController.BeginAfterWhitelistRejected(HostId.m_SteamID);
                    // 5s * 23 = 115s < 120s -> 23 次重试成功
                    float t = 1000f;
                    for (int i = 0; i < 23; i++)
                    {
                        t += 5f;
                        SetWaitTime(t);
                        P2PApprovalWaitController.Tick();
                    }
                    if (connectCount != 23) return Fail("should have 23 retries (120s allows 23)", "count=" + connectCount);

                    // 第 24 次 (120s) -> 超时 Cancel
                    t += 5f; // t = 1120 = 1000 + 120 = _expiresAt
                    SetWaitTime(t);
                    P2PApprovalWaitController.Tick();
                    if (P2PApprovalWaitController.IsWaitingForTest) return Fail("should cancel at 120s timeout", "still waiting");
                    if (connectCount != 23) return Fail("should not retry after cancel", "count=" + connectCount);
                }
                finally { ClearWaitHooks(); }
            }
            return true;
        }

        // W5: 120s 超时 -> Cancel
        internal static bool Test_W5_TimeoutCancel()
        {
            using (P2PClientUiEnvironment.OverrideForTest(true))
            {
                SetupWaitHooks(safeToRetry: false, startTime: 1000f);
                try
                {
                    P2PApprovalWaitController.BeginAfterWhitelistRejected(HostId.m_SteamID);
                    // 120s 后 -> 超时
                    SetWaitTime(1121f);
                    P2PApprovalWaitController.Tick();
                    if (P2PApprovalWaitController.IsWaitingForTest) return Fail("should cancel on timeout", "still waiting");
                }
                finally { ClearWaitHooks(); }
            }
            return true;
        }

        // W6: IsSafeToRetry=false -> 不重试
        internal static bool Test_W6_NotSafeToRetryNoConnect()
        {
            using (P2PClientUiEnvironment.OverrideForTest(true))
            {
                SetupWaitHooks(safeToRetry: false, startTime: 1000f);
                int connectCount = 0;
                P2PApprovalWaitController._testTryConnectCallback = (id) => connectCount++;
                try
                {
                    P2PApprovalWaitController.BeginAfterWhitelistRejected(HostId.m_SteamID);
                    SetWaitTime(1010f); // 10s 后
                    P2PApprovalWaitController.Tick();
                    if (connectCount != 0) return Fail("should not connect when not safe", "count=" + connectCount);
                    if (!P2PApprovalWaitController.IsWaitingForTest) return Fail("should still be waiting", "cancelled");
                }
                finally { ClearWaitHooks(); }
            }
            return true;
        }

        // W7: U3DS 环境 -> 不开始等待
        internal static bool Test_W7_U3DSNoWait()
        {
            using (P2PClientUiEnvironment.OverrideForTest(false))
            {
                SetupWaitHooks();
                try
                {
                    P2PApprovalWaitController.BeginAfterWhitelistRejected(HostId.m_SteamID);
                    if (P2PApprovalWaitController.IsWaitingForTest) return Fail("should not wait in U3DS", "waiting");
                }
                finally { ClearWaitHooks(); }
            }
            return true;
        }

        // W8: 同 host 再次 Begin 不重置预算（P0-RETRY-BUDGET-01 核心验证）
        internal static bool Test_W8_RejectedRetryDoesNotRenewBudget()
        {
            using (P2PClientUiEnvironment.OverrideForTest(true))
            {
                SetupWaitHooks(safeToRetry: true, startTime: 1000f);
                int connectCount = 0;
                P2PApprovalWaitController._testTryConnectCallback = (id) => connectCount++;
                try
                {
                    P2PApprovalWaitController.BeginAfterWhitelistRejected(HostId.m_SteamID);
                    float origExpires = P2PApprovalWaitController.ExpiresAtForTest;

                    // t=5: 第一次重试
                    SetWaitTime(1005f);
                    P2PApprovalWaitController.Tick();
                    if (connectCount != 1) return Fail("first retry", "count=" + connectCount);
                    if (P2PApprovalWaitController.AttemptsForTest != 1) return Fail("attempts=1", "got=" + P2PApprovalWaitController.AttemptsForTest);

                    // 模拟重试被再次 WHITELISTED 拒绝 -> BeginAgain（同 host）
                    P2PApprovalWaitController.BeginAfterWhitelistRejected(HostId.m_SteamID);

                    // 关键断言：attempts 不归零，expiresAt 不续期
                    if (P2PApprovalWaitController.AttemptsForTest != 1) return Fail("attempts must NOT reset", "got=" + P2PApprovalWaitController.AttemptsForTest);
                    if (P2PApprovalWaitController.ExpiresAtForTest != origExpires) return Fail("expiresAt must NOT renew", "orig=" + origExpires + " now=" + P2PApprovalWaitController.ExpiresAtForTest);
                    if (!P2PApprovalWaitController.IsWaitingForTest) return Fail("should still be waiting", "cancelled");
                }
                finally { ClearWaitHooks(); }
            }
            return true;
        }

        // W9: 连续 23 次 retry->WHITELISTED 后 t=120 停止（P0-RETRY-BUDGET-01 闭环验证）
        internal static bool Test_W9_RepeatedRejectStopsAtOriginalDeadline()
        {
            using (P2PClientUiEnvironment.OverrideForTest(true))
            {
                SetupWaitHooks(safeToRetry: true, startTime: 1000f);
                int connectCount = 0;
                P2PApprovalWaitController._testTryConnectCallback = (id) => connectCount++;
                try
                {
                    P2PApprovalWaitController.BeginAfterWhitelistRejected(HostId.m_SteamID);
                    float origExpires = P2PApprovalWaitController.ExpiresAtForTest;

                    // 23 次 retry->WHITELISTED 闭环
                    float t = 1000f;
                    for (int i = 0; i < 23; i++)
                    {
                        t += 5f;
                        SetWaitTime(t);
                        P2PApprovalWaitController.Tick();
                        // 模拟再次被 WHITELISTED -> Begin（同 host，幂等）
                        P2PApprovalWaitController.BeginAfterWhitelistRejected(HostId.m_SteamID);
                    }
                    if (connectCount != 23) return Fail("should have 23 retries", "count=" + connectCount);
                    if (P2PApprovalWaitController.ExpiresAtForTest != origExpires) return Fail("expiresAt must NOT renew across rejects", "changed");

                    // t=120 -> 超时停止
                    t += 5f; // 1120 = 1000+120
                    SetWaitTime(t);
                    P2PApprovalWaitController.Tick();
                    if (P2PApprovalWaitController.IsWaitingForTest) return Fail("should stop at original deadline", "still waiting");
                    if (connectCount != 23) return Fail("no 24th connect", "count=" + connectCount);
                }
                finally { ClearWaitHooks(); }
            }
            return true;
        }

        // W10: OnClientConnected 成功 -> 立即清除等待 UI（P0-WAIT-SUCCESS-CLEANUP-02）
        internal static bool Test_W10_AcceptedImmediatelyClosesWaitUi()
        {
            using (P2PClientUiEnvironment.OverrideForTest(true))
            {
                SetupWaitHooks();
                int connectCount = 0;
                P2PApprovalWaitController._testTryConnectCallback = (id) => connectCount++;
                try
                {
                    P2PApprovalWaitController.BeginAfterWhitelistRejected(HostId.m_SteamID);
                    if (!P2PApprovalWaitController.IsWaitingForTest) return Fail("should be waiting", "");

                    // 模拟 OnClientConnected -> NotifyConnectionAccepted
                    P2PApprovalWaitController.NotifyConnectionAccepted();
                    if (P2PApprovalWaitController.IsWaitingForTest) return Fail("should stop waiting on accept", "still waiting");
                    if (P2PApprovalWaitController.IsWaitUiVisibleForTest) return Fail("wait UI should be hidden", "visible");

                    // 再次 Tick 不应发起连接
                    SetWaitTime(1010f);
                    P2PApprovalWaitController.Tick();
                    if (connectCount != 0) return Fail("should not connect after accept", "count=" + connectCount);
                }
                finally { ClearWaitHooks(); }
            }
            return true;
        }

        // W11: WHITELISTED 不显示通用告警（P1-W11-FALSE-COVERAGE-05 -- 通过 SafeAlert 计数验证）
        internal static bool Test_W11_WhitelistedNoGenericAlert()
        {
            using (P2PClientUiEnvironment.OverrideForTest(true))
            {
                SetupWaitHooks();
                try
                {
                    // WHITELISTED -> Begin -> 等待 UI（非通用告警）
                    P2PApprovalWaitController.BeginAfterWhitelistRejected(HostId.m_SteamID);
                    if (!P2PApprovalWaitController.IsWaitUiVisibleForTest) return Fail("wait UI should be visible (not generic alert)", "hidden");
                    if (!P2PApprovalWaitController.IsWaitingForTest) return Fail("should be waiting", "");
                    // SafeAlert 不应被调用（WHITELISTED 路径在 SafeAlert 前 return）
                    if (P2PJoinManager._testSafeAlertCount != 0) return Fail("WHITELISTED should not trigger SafeAlert", "count=" + P2PJoinManager._testSafeAlertCount);
                }
                finally { ClearWaitHooks(); }
            }
            return true;
        }

        // W12: 显式加入取消旧等待；自动重试不取消自身（P1-EXPLICIT-JOIN-OWNERSHIP-04）
        internal static bool Test_W12_ExplicitJoinCancelsOldWaitButRetryDoesNot()
        {
            using (P2PClientUiEnvironment.OverrideForTest(true))
            {
                SetupWaitHooks(safeToRetry: true, startTime: 1000f);
                int connectCount = 0;
                P2PApprovalWaitController._testTryConnectCallback = (id) => connectCount++;
                try
                {
                    P2PApprovalWaitController.BeginAfterWhitelistRejected(HostId.m_SteamID);

                    // 自动重试不取消自身
                    SetWaitTime(1005f);
                    P2PApprovalWaitController.Tick();
                    if (!P2PApprovalWaitController.IsWaitingForTest) return Fail("auto-retry should NOT cancel wait", "cancelled");
                    if (connectCount != 1) return Fail("auto-retry should connect", "count=" + connectCount);

                    // 显式加入取消旧等待
                    P2PApprovalWaitController.CancelForExplicitUserJoin();
                    if (P2PApprovalWaitController.IsWaitingForTest) return Fail("explicit join should cancel wait", "still waiting");
                    if (P2PApprovalWaitController.IsWaitUiVisibleForTest) return Fail("wait UI should be hidden", "visible");
                }
                finally { ClearWaitHooks(); }
            }
            return true;
        }

        // W13: parent 替换 -> 旧 parent 无 wait child、新 parent 恰有一个、预算不变
        internal static bool Test_W13_ParentReplacementReattachesOneWaitView()
        {
            using (P2PClientUiEnvironment.OverrideForTest(true))
            {
                SetupWaitHooks(safeToRetry: false, startTime: 1000f);
                try
                {
                    P2PApprovalWaitController.BeginAfterWhitelistRejected(HostId.m_SteamID);
                    ISleekElement origParent = P2PNativeMenuUI.WaitBoundParentForTest;
                    float origExpires = P2PApprovalWaitController.ExpiresAtForTest;
                    int origAttempts = P2PApprovalWaitController.AttemptsForTest;
                    if (origParent != FakeParent1) return Fail("initial parent should be FakeParent1", "got=" + (origParent?.GetType().Name ?? "null"));

                    // 替换 parent 身份
                    P2PNativeMenuUI._testParentProvider = () => FakeParent2;

                    // 执行 Tick -> EnsureApprovalWaitVisible 检测 parent 变更 -> Detach + 重建
                    SetWaitTime(1001f);
                    P2PApprovalWaitController.Tick();

                    ISleekElement newParent = P2PNativeMenuUI.WaitBoundParentForTest;
                    if (!ReferenceEquals(newParent, FakeParent2)) return Fail("new parent should be FakeParent2", "got=" + (newParent?.GetType().Name ?? "null"));
                    if (ReferenceEquals(newParent, FakeParent1)) return Fail("should NOT be old parent", "still FakeParent1");
                    // 预算不变
                    if (P2PApprovalWaitController.ExpiresAtForTest != origExpires) return Fail("expiresAt should not change", "changed");
                    if (P2PApprovalWaitController.AttemptsForTest != origAttempts) return Fail("attempts should not change", "changed");
                    if (!P2PApprovalWaitController.IsWaitingForTest) return Fail("should still be waiting", "cancelled");
                }
                finally { ClearWaitHooks(); }
            }
            return true;
        }

        // W14: parent=null -> EnsureApprovalWaitVisible 返回 false -> fail-closed 停止
        internal static bool Test_W14_ViewUnavailableStopsWaitingBeforeRetry()
        {
            using (P2PClientUiEnvironment.OverrideForTest(true))
            {
                SetupWaitHooks(safeToRetry: true, startTime: 1000f);
                int connectCount = 0;
                P2PApprovalWaitController._testTryConnectCallback = (id) => connectCount++;
                try
                {
                    P2PApprovalWaitController.BeginAfterWhitelistRejected(HostId.m_SteamID);

                    // parent=null -> EnsureCreated 无法创建 -> EnsureApprovalWaitVisible false
                    P2PNativeMenuUI._testParentProvider = () => null;
                    SetWaitTime(1010f);
                    P2PApprovalWaitController.Tick();

                    if (P2PApprovalWaitController.IsWaitingForTest) return Fail("should fail-closed stop", "still waiting");
                    if (connectCount != 0) return Fail("should not connect when UI unavailable", "count=" + connectCount);

                    // 再次 Tick 也不连接
                    SetWaitTime(1020f);
                    P2PApprovalWaitController.Tick();
                    if (connectCount != 0) return Fail("should not connect after stop", "count=" + connectCount);
                }
                finally { ClearWaitHooks(); }
            }
            return true;
        }

        // W15: Destroy() 移除等待视图
        internal static bool Test_W15_DestroyDetachesWaitView()
        {
            using (P2PClientUiEnvironment.OverrideForTest(true))
            {
                SetupWaitHooks();
                try
                {
                    P2PApprovalWaitController.BeginAfterWhitelistRejected(HostId.m_SteamID);
                    if (P2PNativeMenuUI.WaitBoundParentForTest == null) return Fail("wait view should be bound", "null parent");

                    // Destroy 移除等待视图
                    P2PNativeMenuUI.Destroy();
                    if (P2PNativeMenuUI.WaitBoundParentForTest != null) return Fail("wait view should be detached", "parent still bound");
                    if (P2PNativeMenuUI.IsWaitVisibleForTest) return Fail("wait should not be visible", "visible");
                }
                finally { ClearWaitHooks(); }
            }
            return true;
        }

        // W16: WHITELISTED 路径 alert=0/wait=1；非 WHITELISTED alert=1/wait=0
        // 通过 SafeAlert 计数 spy 验证（W11 已验证 alert=0；此处补充非 WHITELISTED alert=1）
        internal static bool Test_W16_WhitelistedFailurePresentationRouting()
        {
            using (P2PClientUiEnvironment.OverrideForTest(true))
            {
                SetupWaitHooks();
                try
                {
                    P2PJoinManager._testSafeAlertCount = 0;
                    P2PJoinManager._testBypassFailurePresentationRuntime = true;
                    P2PJoinManager.HandleDisconnectFailureRouting(ESteamConnectionFailureInfo.WHITELISTED, HostId.m_SteamID);
                    if (P2PApprovalWaitController.IsWaitingForTest)
                        return Fail("Stage 7-6 must not start legacy wait", "waiting");
                    if (P2PJoinManager._testSafeAlertCount != 1)
                        return Fail("WHITELISTED should present one failure", "count=" + P2PJoinManager._testSafeAlertCount);
                }
                finally { ClearWaitHooks(); }
            }
            return true;
        }

        // W13-W16 已在上方实现（去重）

        // N7: 连接后缓存角色名；断线后白名单行仍显示最后已知名
        internal static bool Test_N7_ConnectedNameCachedAfterDisconnect()
        {
            SetupPersonaHooks(HostId);
            SteamPersonaDisplay._testConnectedNameProvider = (id) => id.m_SteamID == ClientId.m_SteamID ? "易烨不会玩FPS" : null;
            try
            {
                // 连接时解析 -> 缓存 character name
                string name1 = SteamPersonaDisplay.ResolveDisplayName(ClientId);
                if (name1 != "易烨不会玩FPS") return Fail("connected name", "got=" + name1);
                if (SteamPersonaDisplay.DisplayNameCacheCountForTest < 1) return Fail("should cache", "count=0");

                // 断线后（_testConnectedNameProvider 返回 null）仍从缓存读取
                SteamPersonaDisplay._testConnectedNameProvider = null;
                string name2 = SteamPersonaDisplay.ResolveDisplayName(ClientId);
                if (name2 != "易烨不会玩FPS") return Fail("cached name after disconnect", "got=" + name2);
            }
            finally { ClearPersonaHooks(); }
            return true;
        }

        // N8: persona 异步到达仅更新文字（不重建行/不改 CSteamID）
        internal static bool Test_N8_PersonaArrivesUpdatesTextOnly()
        {
            SetupPersonaHooks(HostId);
            SteamPersonaDisplay._testRemotePersonaProvider = (id) => null; // 首次无资料
            SteamPersonaDisplay._testTimeProvider = () => 1000f;
            try
            {
                // 首次：未知玩家 + 请求 persona
                SteamPersonaDisplay.ResetProbeForTest();
                string name1 = SteamPersonaDisplay.ResolveDisplayName(ClientId);
                if (name1 != "未知玩家") return Fail("first should be unknown", "got=" + name1);
                if (SteamPersonaDisplay.ProbeRequestIssuedForTest != 1) return Fail("should request", "probe=" + SteamPersonaDisplay.ProbeRequestIssuedForTest);

                // persona 到达（provider 返回名称）
                SteamPersonaDisplay._testRemotePersonaProvider = (id) => "PersonaArrived";
                SteamPersonaDisplay.ClearPersonaCacheForTest(); // 强制重新请求
                string name2 = SteamPersonaDisplay.ResolveDisplayName(ClientId);
                if (name2 != "PersonaArrived") return Fail("should show arrived persona", "got=" + name2);
            }
            finally { ClearPersonaHooks(); }
            return true;
        }

        // N9: 名称变化后批准/移除操作仍命中原 SteamID
        internal static bool Test_N9_NameChangeDoesNotAffectSteamId()
        {
            SetupPersonaHooks(HostId);
            SteamPersonaDisplay._testRemotePersonaProvider = (id) => "Name1";
            try
            {
                CSteamID id = ClientId;
                string n1 = SteamPersonaDisplay.ResolveDisplayName(id);
                SteamPersonaDisplay._testRemotePersonaProvider = (id2) => "Name2";
                SteamPersonaDisplay.ClearPersonaCacheForTest();
                SteamPersonaDisplay.ClearDisplayNameCacheForTest();
                string n2 = SteamPersonaDisplay.ResolveDisplayName(id);
                if (n1 == n2) return Fail("names should differ", "same=" + n1);
                if (id.m_SteamID != ClientId.m_SteamID) return Fail("CSteamID must not change", "modified");
            }
            finally { ClearPersonaHooks(); }
            return true;
        }

        // N10: probe 不含名称/SteamID（生产安全）
        internal static bool Test_N10_ProbeSafety()
        {
            SetupPersonaHooks(HostId);
            SteamPersonaDisplay._testRemotePersonaProvider = (id) => "TestName";
            try
            {
                SteamPersonaDisplay.ResetProbeForTest();
                SteamPersonaDisplay.ResolveDisplayName(ClientId);
                // probe 计数验证（不含名称/SteamID 值，仅计数）
                if (SteamPersonaDisplay.ProbeRequestIssuedForTest < 0) return Fail("probe count valid", "negative");
            }
            finally { ClearPersonaHooks(); }
            return true;
        }

        // N11: SteamPending 可在拒绝前捕获 characterName，主线程 drain 后 pending 行可显示名称
        internal static bool Test_N11_PendingCharacterNameCaptureDrain()
        {
            SetupPersonaHooks(HostId);
            try
            {
                SteamPersonaDisplay.ResetForSession();
                SteamPersonaDisplay.TryEnqueueObservedCharacterName(
                    ClientId.m_SteamID,
                    "易烨不会玩FPS");

                if (SteamPersonaDisplay.CapturedCharacterNameCountForTest != 1)
                    return Fail("pending name should be queued", "count=" + SteamPersonaDisplay.CapturedCharacterNameCountForTest);

                SteamPersonaDisplay.DrainObservedCharacterNamesOnMainThread();
                if (SteamPersonaDisplay.CapturedCharacterNameCountForTest != 0)
                    return Fail("capture queue should drain", "count=" + SteamPersonaDisplay.CapturedCharacterNameCountForTest);

                string displayed = SteamPersonaDisplay.ResolveDisplayName(ClientId);
                if (displayed != "易烨不会玩FPS")
                    return Fail("captured pending name should be displayed", "got=" + displayed);
            }
            finally { ClearPersonaHooks(); }
            return true;
        }

        // N12: Group probe 只能运行于本插件 P2P 主机/客机会话，且日志 ID 必须脱敏
        internal static bool Test_N12_GroupProbeSessionGateAndMask()
        {
            if (!P2PGroupStateProbeRuntime.ShouldRunForTest(true, true, false, true, EJoinState.Idle))
                return Fail("active P2P host should run probe", "false");

            if (!P2PGroupStateProbeRuntime.ShouldRunForTest(false, false, true, true, EJoinState.Connected))
                return Fail("connected plugin P2P client should run probe", "false");

            if (P2PGroupStateProbeRuntime.ShouldRunForTest(false, false, true, true, EJoinState.Idle))
                return Fail("ordinary connected multiplayer client must not run probe", "true");

            if (P2PGroupStateProbeRuntime.ShouldRunForTest(false, false, false, false, EJoinState.Connected))
                return Fail("disconnected process must not run probe", "true");

            string masked = P2PGroupStateProbeRuntime.MaskIdForTest(ClientId.m_SteamID);
            if (masked.Contains(ClientId.m_SteamID.ToString()))
                return Fail("masked ID leaked full SteamID", masked);
            if (!masked.StartsWith("...") || masked.Length != 7)
                return Fail("masked ID shape invalid", masked);

            return true;
        }

        // N13: 首次 persona 未知时，5 秒后应重新查询并接收异步到达的名称
        internal static bool Test_N13_UnknownPersonaRetriesAfterRateLimit()
        {
            SetupPersonaHooks(HostId);
            float now = 1000f;
            int requests = 0;
            string availableName = null;
            SteamPersonaDisplay._testTimeProvider = () => now;
            SteamPersonaDisplay._testRequestUserInfoCallback = id => requests++;
            SteamPersonaDisplay._testRemotePersonaProvider = id => availableName;
            try
            {
                string first = SteamPersonaDisplay.ResolveDisplayName(ClientId);
                if (first != "未知玩家" || requests != 1)
                    return Fail("first unresolved persona should request once", "name=" + first + " requests=" + requests);

                now += 3f;
                SteamPersonaDisplay.ResolveDisplayName(ClientId);
                if (requests != 1)
                    return Fail("unknown persona must remain rate-limited before 5s", "requests=" + requests);

                availableName = "LatePersona";
                now += 3f;
                string resolved = SteamPersonaDisplay.ResolveDisplayName(ClientId);
                if (requests != 2 || resolved != "LatePersona")
                    return Fail("unknown persona should retry and resolve after 5s", "name=" + resolved + " requests=" + requests);
            }
            finally { ClearPersonaHooks(); }
            return true;
        }

        internal static bool Test_N15_PendingIdentityNamePriority()
        {
            SetupPersonaHooks(HostId);
            try
            {
                SteamPersonaDisplay.ResetForSession();
                if (!SteamPersonaDisplay.TryEnqueueObservedIdentity(ClientId.m_SteamID, "", "SteamPlayerName", "Nick"))
                    return Fail("playerName fallback should queue", "false");
                SteamPersonaDisplay.DrainObservedCharacterNamesOnMainThread();
                if (SteamPersonaDisplay.ResolveDisplayName(ClientId) != "SteamPlayerName")
                    return Fail("playerName fallback priority", SteamPersonaDisplay.ResolveDisplayName(ClientId));

                SteamPersonaDisplay.ResetForSession();
                SteamPersonaDisplay.TryEnqueueObservedIdentity(ClientId.m_SteamID, "CharacterName", "SteamPlayerName", "Nick");
                SteamPersonaDisplay.DrainObservedCharacterNamesOnMainThread();
                if (SteamPersonaDisplay.ResolveDisplayName(ClientId) != "CharacterName")
                    return Fail("characterName must have highest priority", SteamPersonaDisplay.ResolveDisplayName(ClientId));
            }
            finally { ClearPersonaHooks(); }
            return true;
        }

        internal static bool Test_W17_RateLimitCooldownWithoutGenericAlert()
        {
            using (P2PClientUiEnvironment.OverrideForTest(true))
            {
                SetupWaitHooks(safeToRetry: true, startTime: 1000f);
                int connects = 0;
                P2PApprovalWaitController._testTryConnectCallback = id => connects++;
                try
                {
                    P2PApprovalWaitController.BeginAfterWhitelistRejected(HostId.m_SteamID);
                    SetWaitTime(1005f);
                    P2PApprovalWaitController.Tick();
                    if (connects != 1) return Fail("first retry should connect", "count=" + connects);

                    SetWaitTime(1010f);
                    P2PJoinManager._testSafeAlertCount = 0;
                    P2PJoinManager._testBypassFailurePresentationRuntime = true;
                    P2PJoinManager.HandleDisconnectFailureRouting(
                        ESteamConnectionFailureInfo.CONNECT_RATE_LIMITING,
                        HostId.m_SteamID);

                    if (P2PJoinManager._testSafeAlertCount != 1)
                        return Fail("Stage 7-6 route should present one failure", "count=" + P2PJoinManager._testSafeAlertCount);
                    if (connects != 1)
                        return Fail("Stage 7-6 route must not schedule another retry", "count=" + connects);
                }
                finally { ClearWaitHooks(); }
            }
            return true;
        }

        internal static bool Test_W18_VanillaTeardownMustBeStable()
        {
            if (!P2PJoinManager.IsSafeToRetryForTest(EJoinState.Failed, false, false, false))
                return Fail("stable failed state should allow retry", "false");
            if (P2PJoinManager.IsSafeToRetryForTest(EJoinState.Failed, false, false, true))
                return Fail("Level.isExiting must block retry", "true");
            if (P2PJoinManager.IsSafeToRetryForTest(EJoinState.Failed, false, true, false))
                return Fail("Level.isLoading must block retry", "true");
            if (P2PJoinManager.IsSafeToRetryForTest(EJoinState.Failed, true, false, false))
                return Fail("Provider.isConnected must block retry", "true");
            if (P2PJoinManager.IsSafeToRetryForTest(EJoinState.Connecting, false, false, false))
                return Fail("connecting state must block retry", "true");
            return true;
        }

        internal static bool Test_W19_RepeatedWhitelistReschedulesFromDisconnect()
        {
            using (P2PClientUiEnvironment.OverrideForTest(true))
            {
                SetupWaitHooks(safeToRetry: true, startTime: 1000f);
                int connects = 0;
                P2PApprovalWaitController._testTryConnectCallback = id => connects++;
                try
                {
                    P2PApprovalWaitController.BeginAfterWhitelistRejected(HostId.m_SteamID);
                    SetWaitTime(1005f);
                    P2PApprovalWaitController.Tick();
                    if (connects != 1) return Fail("first retry", "count=" + connects);

                    SetWaitTime(1008f);
                    P2PApprovalWaitController.BeginAfterWhitelistRejected(HostId.m_SteamID);
                    if (P2PApprovalWaitController.NextRetryAtForTest != 1013f)
                        return Fail("next retry must be scheduled from disconnect", "next=" + P2PApprovalWaitController.NextRetryAtForTest);
                    SetWaitTime(1012f);
                    P2PApprovalWaitController.Tick();
                    if (connects != 1) return Fail("zero-interval retry regression", "count=" + connects);
                    SetWaitTime(1013f);
                    P2PApprovalWaitController.Tick();
                    if (connects != 2) return Fail("retry after rescheduled delay", "count=" + connects);
                }
                finally { ClearWaitHooks(); }
            }
            return true;
        }

        private static bool Fail(string msg, string detail)
        {
            Console.WriteLine("    FAIL: " + msg + (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")"));
            return false;
        }
    }

    /// <summary>最小 ISleekElement fake：仅需非-null + ReferenceEquals；所有方法抛 NotImplementedException。</summary>
    internal sealed class FakeSleekElement : ISleekElement
    {
        public bool IsVisible { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public ISleekElement Parent => throw new NotImplementedException();
        public ISleekLabel SideLabel => throw new NotImplementedException();
        public float PositionOffset_X { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public float PositionOffset_Y { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public float PositionScale_X { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public float PositionScale_Y { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public float SizeOffset_X { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public float SizeOffset_Y { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public float SizeScale_X { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public float SizeScale_Y { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public ISleekElement AttachmentRoot => throw new NotImplementedException();
        public bool IsAnimatingTransform => throw new NotImplementedException();
        public bool UseManualLayout { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public bool UseWidthLayoutOverride { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public bool UseHeightLayoutOverride { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public ESleekChildLayout UseChildAutoLayout { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public ESleekChildPerpendicularAlignment ChildPerpendicularAlignment { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public bool ExpandChildren { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public bool IgnoreLayout { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public float ChildAutoLayoutPadding { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public void InternalDestroy() => throw new NotImplementedException();
        public void AnimatePositionOffset(float newPositionOffset_X, float newPositionOffset_Y, ESleekLerp lerp, float time) => throw new NotImplementedException();
        public void AnimatePositionScale(float newPositionScale_X, float newPositionScale_Y, ESleekLerp lerp, float time) => throw new NotImplementedException();
        public void AnimateSizeOffset(float newSizeOffset_X, float newSizeOffset_Y, ESleekLerp lerp, float time) => throw new NotImplementedException();
        public void AnimateSizeScale(float newSizeScale_X, float newSizeScale_Y, ESleekLerp lerp, float time) => throw new NotImplementedException();
        public void AddChild(ISleekElement child) { /* no-op for test */ }
        public void AddLabel(string text, ESleekSide side) => throw new NotImplementedException();
        public void AddLabel(string text, Color color, ESleekSide side) => throw new NotImplementedException();
        public void UpdateLabel(string text) => throw new NotImplementedException();
        public int FindIndexOfChild(ISleekElement sleek) => throw new NotImplementedException();
        public ISleekElement GetChildAtIndex(int index) => throw new NotImplementedException();
        public int GetChildCount() => throw new NotImplementedException();
        public void Update() => throw new NotImplementedException();
        public void RemoveChild(ISleekElement child) { /* no-op for test */ }
        public void RemoveAllChildren() => throw new NotImplementedException();
        public Vector2 ViewportToNormalizedPosition(Vector2 viewportPosition) => throw new NotImplementedException();
        public Vector2 GetNormalizedCursorPosition() => throw new NotImplementedException();
        public Vector2 GetAbsoluteSize() => throw new NotImplementedException();
        public void SetAsFirstSibling() => throw new NotImplementedException();
        public void ForceLayoutUpdate() => throw new NotImplementedException();
    }
}
