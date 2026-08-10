using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.UI;
using SteamP2PFriends.WhitelistTests.Fakes;
using Steamworks;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SteamP2PFriends.WhitelistTests
{
    /// <summary>
    /// v6 [P1-E] 故障注入测试：ISleekScrollView 重建事务的原子性。
    /// 蓝图 §3.2 L17-L20：SetContentHeight/BuildRow 抛异常后 snapshot invalid、
    ///   同输入下一周期不跳过、双表隔离、Approve/Reject/Remove 失效范围。
    /// </summary>
    internal static class AtomicSnapshotTests
    {
        private static readonly CSteamID HostId = new CSteamID(76561199030780228UL);
        private static readonly CSteamID ClientId = new CSteamID(76561199721762479UL);

        // v6 [P1-E] 最小 fake：仅 IApprovalRebuildSurface 4 成员；ContentSizeOffset setter 可配置抛异常
        internal sealed class FakeApprovalRebuildSurface : IApprovalRebuildSurface
        {
            internal bool ThrowOnContentSizeOffset;
            private Vector2 _contentSize;
            private Vector2 _normalizedStateCenter;
            public Vector2 ContentSizeOffset
            {
                get { return _contentSize; }
                set { if (ThrowOnContentSizeOffset) throw new InvalidOperationException("fake: ContentSizeOffset throws"); _contentSize = value; }
            }
            public Vector2 NormalizedStateCenter
            {
                get { return _normalizedStateCenter; }
                set { _normalizedStateCenter = value; NormalizedStateCenterSetCount++; }
            }
            internal int NormalizedStateCenterSetCount;
            public int AddChildCount;
            public int RemoveChildCount;
            public void AddChild(ISleekElement child) { AddChildCount++; }
            public void RemoveChild(ISleekElement child) { RemoveChildCount++; }
        }

        private static ApprovalPanelLayout DefaultLayout()
        {
            ApprovalPanelLayout.TryCreate(1920f, 1080f, out ApprovalPanelLayout layout);
            return layout;
        }

        // L17: pending SetContentHeight 抛异常 -> snapshot invalid；下一相同快照不跳过
        internal static bool Test_v6_L17_PendingSetContentHeightThrows()
        {
            var runtime = new FakeApprovalRuntimeContext { IsActiveP2PHostValue = true, LocalUserValue = HostId, RealtimeValue = 1000f };
            var wl = new FakeApprovalWhitelistProxy { ContainsResult = false };
            using (P2PJoinApprovalService.InstallTestDependencies(runtime, wl))
            {
                P2PJoinApprovalService.ResetForSession();
                P2PJoinApprovalService.TryEnqueueRejectedTransportId(ClientId.m_SteamID);
                P2PJoinApprovalService.DrainCapturedRejectsOnMainThread();

                ApprovalPanelLayout layout = DefaultLayout();
                P2PHostSessionUI._testBypassThreadAssert = true;
                P2PHostSessionUI.InvalidateRenderSnapshots();
                var fake = new FakeApprovalRebuildSurface { ThrowOnContentSizeOffset = true };
                P2PHostSessionUI._testRequestSurfaceOverride = fake;
                try
                {
                    P2PHostSessionUI.RefreshApprovalPanel(layout);
                    // 断言：snapshot invalid
                    if (P2PHostSessionUI.HasPendingSnapshotForTest)
                        return Fail("pending snapshot should be invalid after SetContentHeight throw", "hasSnapshot=true");
                    if (P2PHostSessionUI.RenderedPendingCountForTest != 0)
                        return Fail("rendered pending should be 0", "count=" + P2PHostSessionUI.RenderedPendingCountForTest);
                    // 下一相同快照不被跳过（PendingSnapshotEquals false）
                    var pending = P2PJoinApprovalService.GetPendingRequests();
                    int shown = Math.Min(pending.Count, 16);
                    if (P2PHostSessionUI.PendingSnapshotEquals(pending, shown, layout.Mode))
                        return Fail("next same snapshot should NOT be skipped", "equals=true");
                }
                finally
                {
                    P2PHostSessionUI._testRequestSurfaceOverride = null;
                    P2PHostSessionUI._testBypassThreadAssert = false;
                }
            }
            return true;
        }

        // L18: whitelist SetContentHeight 抛异常 -> whitelist snapshot invalid；pending 不被清空（双表隔离）
        internal static bool Test_v6_L18_WhitelistThrowsPendingIsolated()
        {
            var wlStore = new FakeWhitelistStore();
            var wlRuntime = new FakeWhitelistRuntimeContext { IsActiveP2PHostValue = true, LocalUserValue = HostId };
            var wlGateway = new FakeWhitelistDisconnectGateway();
            using (P2PWhitelistService.InstallTestDependencies(wlStore, wlRuntime, wlGateway))
            {
                ApprovalPanelLayout layout = DefaultLayout();
                P2PHostSessionUI._testBypassThreadAssert = true;
                P2PHostSessionUI.InvalidateRenderSnapshots();

                // 预置 pending snapshot 有效（直接 capture，不经 Glazier）
                var pendingList = new List<PendingJoinRequest> { new PendingJoinRequest(ClientId, 1000f) };
                P2PHostSessionUI.CapturePendingSnapshot(pendingList, 1, layout.Mode);
                if (!P2PHostSessionUI.HasPendingSnapshotForTest)
                    return Fail("setup: pending snapshot should be valid", "");

                var fake = new FakeApprovalRebuildSurface { ThrowOnContentSizeOffset = true };
                P2PHostSessionUI._testWhitelistSurfaceOverride = fake;
                try
                {
                    P2PHostSessionUI.RefreshWhitelistPanel(layout);
                    // whitelist snapshot invalid
                    if (P2PHostSessionUI.HasWhitelistSnapshotForTest)
                        return Fail("whitelist snapshot should be invalid after throw", "hasSnapshot=true");
                    // pending snapshot 仍有效（隔离）
                    if (!P2PHostSessionUI.HasPendingSnapshotForTest)
                        return Fail("pending snapshot should remain valid (isolation)", "pending cleared");
                }
                finally
                {
                    P2PHostSessionUI._testWhitelistSurfaceOverride = null;
                    P2PHostSessionUI._testBypassThreadAssert = false;
                    P2PHostSessionUI.InvalidateRenderSnapshots();
                }
            }
            return true;
        }

        // L19: BuildApprovalRow 抛异常 -> 无半建行、不提交 snapshot、下一次能重试
        internal static bool Test_v6_L19_BuildRowThrowsAtomic()
        {
            var runtime = new FakeApprovalRuntimeContext { IsActiveP2PHostValue = true, LocalUserValue = HostId, RealtimeValue = 1000f };
            var wl = new FakeApprovalWhitelistProxy { ContainsResult = false };
            using (P2PJoinApprovalService.InstallTestDependencies(runtime, wl))
            {
                P2PJoinApprovalService.ResetForSession();
                P2PJoinApprovalService.TryEnqueueRejectedTransportId(ClientId.m_SteamID);
                P2PJoinApprovalService.DrainCapturedRejectsOnMainThread();

                ApprovalPanelLayout layout = DefaultLayout();
                P2PHostSessionUI._testBypassThreadAssert = true;
                P2PHostSessionUI.InvalidateRenderSnapshots();
                var fake = new FakeApprovalRebuildSurface { ThrowOnContentSizeOffset = false }; // SetContentHeight 正常
                P2PHostSessionUI._testRequestSurfaceOverride = fake;
                P2PHostSessionUI._testRowBuildThrows = true; // BuildApprovalRow 抛异常
                try
                {
                    P2PHostSessionUI.RefreshApprovalPanel(layout);
                    // 无半建行
                    if (P2PHostSessionUI.ApprovalRowCountForTest != 0)
                        return Fail("no half-built rows should remain", "rows=" + P2PHostSessionUI.ApprovalRowCountForTest);
                    // snapshot 未提交（invalid）
                    if (P2PHostSessionUI.HasPendingSnapshotForTest)
                        return Fail("snapshot should not be committed after build throw", "hasSnapshot=true");
                    // 下一次不跳过
                    var pending = P2PJoinApprovalService.GetPendingRequests();
                    int shown = Math.Min(pending.Count, 16);
                    if (P2PHostSessionUI.PendingSnapshotEquals(pending, shown, layout.Mode))
                        return Fail("next same snapshot should NOT be skipped", "equals=true");
                }
                finally
                {
                    P2PHostSessionUI._testRowBuildThrows = false;
                    P2PHostSessionUI._testRequestSurfaceOverride = null;
                    P2PHostSessionUI._testBypassThreadAssert = false;
                }
            }
            return true;
        }

        // L20: 失效范围 - Approve 两表；Reject 仅 pending；Remove 仅 whitelist
        internal static bool Test_v6_L20_InvalidationScope()
        {
            P2PHostSessionUI._testBypassThreadAssert = true;
            P2PHostSessionUI.InvalidateRenderSnapshots();
            ApprovalPanelLayout layout = DefaultLayout();

            var pendingList = new List<PendingJoinRequest> { new PendingJoinRequest(ClientId, 1000f) };
            var wlList = new List<SteamWhitelistID> { new SteamWhitelistID(ClientId, "APPROVED", HostId) };

            try
            {
                // 两表有效
                P2PHostSessionUI.CapturePendingSnapshot(pendingList, 1, layout.Mode);
                P2PHostSessionUI.CaptureWhitelistSnapshot(wlList, 1, layout.Mode);
                if (!P2PHostSessionUI.HasPendingSnapshotForTest || !P2PHostSessionUI.HasWhitelistSnapshotForTest)
                    return Fail("setup: both snapshots valid", "");

                // Reject 仅失效 pending
                P2PHostSessionUI.InvalidatePendingRenderSnapshot();
                if (P2PHostSessionUI.HasPendingSnapshotForTest) return Fail("Reject: pending should be invalid", "");
                if (!P2PHostSessionUI.HasWhitelistSnapshotForTest) return Fail("Reject: whitelist should remain valid", "");

                // 重新 capture pending，Remove 仅失效 whitelist
                P2PHostSessionUI.CapturePendingSnapshot(pendingList, 1, layout.Mode);
                P2PHostSessionUI.InvalidateWhitelistRenderSnapshot();
                if (P2PHostSessionUI.HasWhitelistSnapshotForTest) return Fail("Remove: whitelist should be invalid", "");
                if (!P2PHostSessionUI.HasPendingSnapshotForTest) return Fail("Remove: pending should remain valid", "");

                // Approve 失效两表
                P2PHostSessionUI.CaptureWhitelistSnapshot(wlList, 1, layout.Mode);
                P2PHostSessionUI.InvalidateRenderSnapshots();
                if (P2PHostSessionUI.HasPendingSnapshotForTest) return Fail("Approve: pending should be invalid", "");
                if (P2PHostSessionUI.HasWhitelistSnapshotForTest) return Fail("Approve: whitelist should be invalid", "");
            }
            finally
            {
                P2PHostSessionUI.InvalidateRenderSnapshots();
                P2PHostSessionUI._testBypassThreadAssert = false;
            }
            return true;
        }

        // L21 (Codex 建议非阻断)：fake 从 ContentSizeOffset 抛异常恢复后，相同输入完成一次成功重建
        internal static bool Test_v6_L21_RecoverThenSuccessfulRebuild()
        {
            var runtime = new FakeApprovalRuntimeContext { IsActiveP2PHostValue = true, LocalUserValue = HostId, RealtimeValue = 1000f };
            var wl = new FakeApprovalWhitelistProxy { ContainsResult = false };
            using (P2PJoinApprovalService.InstallTestDependencies(runtime, wl))
            {
                P2PJoinApprovalService.ResetForSession();
                P2PJoinApprovalService.TryEnqueueRejectedTransportId(ClientId.m_SteamID);
                P2PJoinApprovalService.DrainCapturedRejectsOnMainThread();

                ApprovalPanelLayout layout = DefaultLayout();
                P2PHostSessionUI._testBypassThreadAssert = true;
                P2PHostSessionUI._testBypassGlazier = true;
                P2PHostSessionUI.InvalidateRenderSnapshots();
                var fake = new FakeApprovalRebuildSurface();
                P2PHostSessionUI._testRequestSurfaceOverride = fake;
                try
                {
                    // 1. 第一次：ContentSizeOffset 抛异常 -> snapshot invalid
                    fake.ThrowOnContentSizeOffset = true;
                    P2PHostSessionUI.RefreshApprovalPanel(layout);
                    if (P2PHostSessionUI.HasPendingSnapshotForTest)
                        return Fail("after throw: snapshot should be invalid", "hasSnapshot=true");

                    // 2. 恢复 + 成功重建
                    fake.ThrowOnContentSizeOffset = false;
                    P2PHostSessionUI.RefreshApprovalPanel(layout);
                    if (!P2PHostSessionUI.HasPendingSnapshotForTest)
                        return Fail("after recovery: snapshot should be committed", "hasSnapshot=false");
                    if (P2PHostSessionUI.RenderedPendingCountForTest != 1)
                        return Fail("rendered pending should be 1", "count=" + P2PHostSessionUI.RenderedPendingCountForTest);
                    if (P2PHostSessionUI.ApprovalRowCountForTest != 1)
                        return Fail("approval row count should be 1", "rows=" + P2PHostSessionUI.ApprovalRowCountForTest);
                }
                finally
                {
                    P2PHostSessionUI._testBypassGlazier = false;
                    P2PHostSessionUI._testRequestSurfaceOverride = null;
                    P2PHostSessionUI._testBypassThreadAssert = false;
                    P2PHostSessionUI.InvalidateRenderSnapshots();
                }
            }
            return true;
        }

        // L22: 标签切换必须在当前调用立即刷新实际列表（不是只写 _activeTab）。
        internal static bool Test_v6_L22_TabSwitchImmediateRefresh()
        {
            var approvalRuntime = new FakeApprovalRuntimeContext { IsActiveP2PHostValue = true, LocalUserValue = HostId, RealtimeValue = 1000f };
            var approvalWhitelist = new FakeApprovalWhitelistProxy { ContainsResult = false };
            var whitelistStore = new FakeWhitelistStore { ContainsResult = true };
            whitelistStore.InjectMember(HostId, "P2P_HOST", HostId);
            var whitelistRuntime = new FakeWhitelistRuntimeContext { IsActiveP2PHostValue = true, LocalUserValue = HostId };
            var whitelistGateway = new FakeWhitelistDisconnectGateway();
            using (P2PJoinApprovalService.InstallTestDependencies(approvalRuntime, approvalWhitelist))
            using (P2PWhitelistService.InstallTestDependencies(whitelistStore, whitelistRuntime, whitelistGateway))
            {
                P2PJoinApprovalService.ResetForSession();
                P2PJoinApprovalService.TryEnqueueRejectedTransportId(ClientId.m_SteamID);
                P2PJoinApprovalService.DrainCapturedRejectsOnMainThread();

                ApprovalPanelLayout layout = DefaultLayout();
                var request = new FakeApprovalRebuildSurface();
                var whitelist = new FakeApprovalRebuildSurface();
                P2PHostSessionUI.TestInitializeCreated(layout, request, whitelist);
                try
                {
                    P2PHostSessionUI.ActivateTab(EApprovalTab.Pending);
                    if (P2PHostSessionUI.ActiveTabForTest != EApprovalTab.Pending)
                        return Fail("ActivateTab(Pending) should set activeTab", P2PHostSessionUI.ActiveTabForTest.ToString());
                    if (!P2PHostSessionUI.HasPendingSnapshotForTest || P2PHostSessionUI.ApprovalRowCountForTest != 1)
                        return Fail("pending should rebuild during the click", "snapshot=" + P2PHostSessionUI.HasPendingSnapshotForTest + " rows=" + P2PHostSessionUI.ApprovalRowCountForTest);
                    if (request.ContentSizeOffset.y != ApprovalListRenderPlan.Create(layout.Mode).RowHeight)
                        return Fail("pending content height should rebuild during the click", "y=" + request.ContentSizeOffset.y);

                    P2PHostSessionUI.ActivateTab(EApprovalTab.Whitelist);
                    if (P2PHostSessionUI.ActiveTabForTest != EApprovalTab.Whitelist)
                        return Fail("ActivateTab(Whitelist) should set activeTab", P2PHostSessionUI.ActiveTabForTest.ToString());
                    if (!P2PHostSessionUI.HasWhitelistSnapshotForTest || P2PHostSessionUI.WhitelistRowCountForTest != 1)
                        return Fail("whitelist should rebuild during the click", "snapshot=" + P2PHostSessionUI.HasWhitelistSnapshotForTest + " rows=" + P2PHostSessionUI.WhitelistRowCountForTest);
                    if (whitelist.ContentSizeOffset.y != ApprovalListRenderPlan.Create(layout.Mode).RowHeight)
                        return Fail("whitelist content height should rebuild during the click", "y=" + whitelist.ContentSizeOffset.y);
                }
                finally { P2PHostSessionUI.TestReset(); }
            }
            return true;
        }

        // L23: 空表也须形成一项空态 row，并提交 content height 24
        internal static bool Test_v6_L23_EmptyListContentHeight()
        {
            var runtime = new FakeApprovalRuntimeContext { IsActiveP2PHostValue = true, LocalUserValue = HostId, RealtimeValue = 1000f };
            var wl = new FakeApprovalWhitelistProxy { ContainsResult = false };
            using (P2PJoinApprovalService.InstallTestDependencies(runtime, wl))
            {
                P2PJoinApprovalService.ResetForSession();
                // 不入队任何 pending -> shown=0

                ApprovalPanelLayout layout = DefaultLayout();
                P2PHostSessionUI._testBypassThreadAssert = true;
                P2PHostSessionUI._testBypassGlazier = true;
                P2PHostSessionUI.InvalidateRenderSnapshots();
                var fake = new FakeApprovalRebuildSurface();
                P2PHostSessionUI._testRequestSurfaceOverride = fake;
                try
                {
                    P2PHostSessionUI.RefreshApprovalPanel(layout);
                    // content height = EmptyContentHeight = 24
                    if (fake.ContentSizeOffset.y != 24f)
                        return Fail("empty list content height should be 24", "y=" + fake.ContentSizeOffset.y);
                    // 空态 row 已添加
                    if (P2PHostSessionUI.ApprovalRowCountForTest < 1)
                        return Fail("empty list should have at least 1 empty-state row", "rows=" + P2PHostSessionUI.ApprovalRowCountForTest);
                    // snapshot 已提交（0 项有效快照）
                    if (!P2PHostSessionUI.HasPendingSnapshotForTest)
                        return Fail("empty list snapshot should be committed", "hasSnapshot=false");
                    if (P2PHostSessionUI.RenderedPendingCountForTest != 0)
                        return Fail("rendered pending count should be 0 (empty)", "count=" + P2PHostSessionUI.RenderedPendingCountForTest);
                }
                finally
                {
                    P2PHostSessionUI._testBypassGlazier = false;
                    P2PHostSessionUI._testRequestSurfaceOverride = null;
                    P2PHostSessionUI._testBypassThreadAssert = false;
                    P2PHostSessionUI.InvalidateRenderSnapshots();
                }
            }
            return true;
        }

        // L24: surface=null 时必须留下可判定 probe，且不能提交 snapshot 或假装渲染完成。
        internal static bool Test_v7_L24_SurfaceNullProbe()
        {
            var runtime = new FakeApprovalRuntimeContext { IsActiveP2PHostValue = true, LocalUserValue = HostId, RealtimeValue = 1000f };
            var wl = new FakeApprovalWhitelistProxy { ContainsResult = false };
            using (P2PJoinApprovalService.InstallTestDependencies(runtime, wl))
            {
                P2PJoinApprovalService.ResetForSession();
                P2PJoinApprovalService.TryEnqueueRejectedTransportId(ClientId.m_SteamID);
                P2PJoinApprovalService.DrainCapturedRejectsOnMainThread();
                ApprovalPanelLayout layout = DefaultLayout();
                P2PHostSessionUI.TestInitializeCreated(layout, null, null);
                try
                {
                    P2PHostSessionUI.RefreshApprovalPanel(layout);
                    if (P2PHostSessionUI.HasPendingSnapshotForTest || P2PHostSessionUI.ApprovalRowCountForTest != 0)
                        return Fail("surface-null must not commit pending render", "snapshot=" + P2PHostSessionUI.HasPendingSnapshotForTest + " rows=" + P2PHostSessionUI.ApprovalRowCountForTest);
                    if (P2PHostSessionUI.LastProbePhaseForTest != "surface-null")
                        return Fail("surface-null must be observable", P2PHostSessionUI.LastProbePhaseForTest ?? "null");
                    if (P2PHostSessionUI.LastProbeModeForTest != layout.Mode)
                        return Fail("surface-null probe must include layout mode", P2PHostSessionUI.LastProbeModeForTest.ToString());
                }
                finally { P2PHostSessionUI.TestReset(); }
            }
            return true;
        }

        // L25: whitelist 空表与 pending 空表同样必须有空态 row、24px 内容高度和有效快照。
        internal static bool Test_v7_L25_WhitelistEmptyListContentHeight()
        {
            var store = new FakeWhitelistStore { ContainsResult = true };
            var runtime = new FakeWhitelistRuntimeContext { IsActiveP2PHostValue = true, LocalUserValue = HostId };
            var gateway = new FakeWhitelistDisconnectGateway();
            using (P2PWhitelistService.InstallTestDependencies(store, runtime, gateway))
            {
                ApprovalPanelLayout layout = DefaultLayout();
                var whitelist = new FakeApprovalRebuildSurface();
                P2PHostSessionUI.TestInitializeCreated(layout, null, whitelist);
                try
                {
                    P2PHostSessionUI.RefreshWhitelistPanel(layout);
                    if (whitelist.ContentSizeOffset.y != 24f)
                        return Fail("empty whitelist content height should be 24", "y=" + whitelist.ContentSizeOffset.y);
                    if (P2PHostSessionUI.WhitelistRowCountForTest != 1 || !P2PHostSessionUI.HasWhitelistSnapshotForTest)
                        return Fail("empty whitelist should commit one empty-state row", "rows=" + P2PHostSessionUI.WhitelistRowCountForTest + " snapshot=" + P2PHostSessionUI.HasWhitelistSnapshotForTest);
                }
                finally { P2PHostSessionUI.TestReset(); }
            }
            return true;
        }

        // L26: whitelist 也必须覆盖 surface-null；同时覆盖 Compact mode 的 probe 可观测性。
        internal static bool Test_v7_L26_WhitelistSurfaceNullProbeCompact()
        {
            var store = new FakeWhitelistStore { ContainsResult = true };
            store.InjectMember(HostId, "P2P_HOST", HostId);
            var runtime = new FakeWhitelistRuntimeContext { IsActiveP2PHostValue = true, LocalUserValue = HostId };
            var gateway = new FakeWhitelistDisconnectGateway();
            using (P2PWhitelistService.InstallTestDependencies(store, runtime, gateway))
            {
                if (!ApprovalPanelLayout.TryCreate(648f, 1080f, out ApprovalPanelLayout layout))
                    return Fail("compact layout setup failed", "");
                if (layout.Mode != EApprovalLayoutMode.Compact)
                    return Fail("compact layout expected", layout.Mode.ToString());

                P2PHostSessionUI.TestInitializeCreated(layout, null, null);
                try
                {
                    P2PHostSessionUI.RefreshWhitelistPanel(layout);
                    if (P2PHostSessionUI.HasWhitelistSnapshotForTest || P2PHostSessionUI.WhitelistRowCountForTest != 0)
                        return Fail("surface-null must not commit whitelist render", "snapshot=" + P2PHostSessionUI.HasWhitelistSnapshotForTest + " rows=" + P2PHostSessionUI.WhitelistRowCountForTest);
                    if (P2PHostSessionUI.LastProbePhaseForTest != "surface-null")
                        return Fail("whitelist surface-null must be observable", P2PHostSessionUI.LastProbePhaseForTest ?? "null");
                    if (P2PHostSessionUI.LastProbeModeForTest != EApprovalLayoutMode.Compact)
                        return Fail("whitelist surface-null must report Compact mode", P2PHostSessionUI.LastProbeModeForTest.ToString());
                }
                finally { P2PHostSessionUI.TestReset(); }
            }
            return true;
        }

        // L27: 新建 scroll 的 0 高 content 可产生 NaN/Infinity；不得将它写回 content transform。
        internal static bool Test_v7_L27_InvalidScrollCenterIsNotRestored()
        {
            var runtime = new FakeApprovalRuntimeContext { IsActiveP2PHostValue = true, LocalUserValue = HostId, RealtimeValue = 1000f };
            var wl = new FakeApprovalWhitelistProxy { ContainsResult = false };
            using (P2PJoinApprovalService.InstallTestDependencies(runtime, wl))
            {
                P2PJoinApprovalService.ResetForSession();
                P2PJoinApprovalService.TryEnqueueRejectedTransportId(ClientId.m_SteamID);
                P2PJoinApprovalService.DrainCapturedRejectsOnMainThread();
                ApprovalPanelLayout layout = DefaultLayout();
                var request = new FakeApprovalRebuildSurface { NormalizedStateCenter = new Vector2(float.NaN, float.PositiveInfinity) };
                request.NormalizedStateCenterSetCount = 0; // 忽略测试布置写入
                P2PHostSessionUI.TestInitializeCreated(layout, request, null);
                try
                {
                    P2PHostSessionUI.RefreshApprovalPanel(layout);
                    if (request.NormalizedStateCenterSetCount != 0)
                        return Fail("invalid center must not be restored", "setCount=" + request.NormalizedStateCenterSetCount);
                    if (!P2PHostSessionUI.HasPendingSnapshotForTest || request.ContentSizeOffset.y != 52f)
                        return Fail("invalid center must not prevent valid row rebuild", "snapshot=" + P2PHostSessionUI.HasPendingSnapshotForTest + " height=" + request.ContentSizeOffset.y);
                }
                finally { P2PHostSessionUI.TestReset(); }
            }
            return true;
        }

        // L28: 有效中心仍须恢复，避免 v6 的滚动位置保持能力退化。
        internal static bool Test_v7_L28_ValidScrollCenterIsRestored()
        {
            var runtime = new FakeApprovalRuntimeContext { IsActiveP2PHostValue = true, LocalUserValue = HostId, RealtimeValue = 1000f };
            var wl = new FakeApprovalWhitelistProxy { ContainsResult = false };
            using (P2PJoinApprovalService.InstallTestDependencies(runtime, wl))
            {
                P2PJoinApprovalService.ResetForSession();
                P2PJoinApprovalService.TryEnqueueRejectedTransportId(ClientId.m_SteamID);
                P2PJoinApprovalService.DrainCapturedRejectsOnMainThread();
                ApprovalPanelLayout layout = DefaultLayout();
                var request = new FakeApprovalRebuildSurface { NormalizedStateCenter = new Vector2(0.25f, 0.75f) };
                request.NormalizedStateCenterSetCount = 0;
                P2PHostSessionUI.TestInitializeCreated(layout, request, null);
                try
                {
                    P2PHostSessionUI.RefreshApprovalPanel(layout);
                    if (request.NormalizedStateCenterSetCount != 1)
                        return Fail("valid center should be restored exactly once", "setCount=" + request.NormalizedStateCenterSetCount);
                    Vector2 restored = request.NormalizedStateCenter;
                    if (restored.x != 0.25f || restored.y != 0.75f)
                        return Fail("valid center changed during restore", "x=" + restored.x + " y=" + restored.y);
                }
                finally { P2PHostSessionUI.TestReset(); }
            }
            return true;
        }

        private static bool Fail(string msg, string detail)
        {
            Console.WriteLine("    FAIL: " + msg + (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")"));
            return false;
        }
    }
}
