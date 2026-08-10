using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using Steamworks;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SteamP2PFriends.UI
{
    // =====================================================================
    // Stage 7-4 v3 LeftResponsive + v4 ScrollIntegrity + v5 ExactRenderSnapshot + v6 AtomicSnapshotCommit
    //   v6 [P1-A/B/C/D]：重建事务完整 try 边界（Clear+SetContentHeight+Build+Capture+Restore 全在 try 内）；
    //     失败 best-effort 清半建行 + 收敛 scroll 内容高度 + 失效对应表快照（不重抛，下帧重试）；
    //     双表隔离（pending 失败只失效 pending）；IApprovalRebuildSurface 抽象便于故障注入测试。
    //   审批内核（P2PJoinApprovalService）独立运行；CSteamID 唯一授权键。
    // =====================================================================

    internal enum EApprovalLayoutMode { Normal, Compact }

    internal struct ApprovalPanelLayout
    {
        internal const float LeftInset = 4f;
        internal const float TopInset = 22f;
        internal const float WidthRatio = 0.145f;
        internal const float HeightRatio = 0.835f;
        internal const float NativeMenuHalfWidth = 100f;
        internal const float NativeMenuGap = 20f;
        internal const float BottomSafeArea = 90f;
        internal const float NormalMinWidth = 220f;
        internal const float AbsoluteMinWidth = 160f;
        internal const float MaxWidth = 360f;
        internal const float MinHeight = 280f;

        internal float Width;
        internal float Height;
        internal EApprovalLayoutMode Mode;

        internal static bool TryCreate(float viewportWidth, float viewportHeight, out ApprovalPanelLayout layout)
        {
            layout = default(ApprovalPanelLayout);
            if (viewportWidth <= 0f || viewportHeight <= 0f) return false;
            float safeRight = viewportWidth * 0.5f - NativeMenuHalfWidth - NativeMenuGap;
            float maximumWidth = safeRight - LeftInset;
            if (maximumWidth < AbsoluteMinWidth) return false;
            float proportionalWidth = viewportWidth * WidthRatio;
            float normalWidth = Math.Max(NormalMinWidth, Math.Min(proportionalWidth, MaxWidth));
            layout.Width = Math.Min(normalWidth, maximumWidth);
            layout.Mode = layout.Width >= NormalMinWidth ? EApprovalLayoutMode.Normal : EApprovalLayoutMode.Compact;
            float proportionalHeight = viewportHeight * HeightRatio;
            float maximumHeight = viewportHeight - TopInset - BottomSafeArea;
            if (maximumHeight < MinHeight) return false;
            layout.Height = Math.Min(proportionalHeight, maximumHeight);
            return true;
        }

        internal static bool TryCreateFromScreen(out ApprovalPanelLayout layout)
        {
            return TryCreate(Screen.width, Screen.height, out layout);
        }
    }

    internal readonly struct ApprovalListRenderPlan
    {
        internal readonly EApprovalLayoutMode Mode;
        internal readonly float RowHeight;
        internal readonly float NameHeight;
        internal readonly float SteamIdHeight;
        internal readonly float ButtonHeight;

        private ApprovalListRenderPlan(EApprovalLayoutMode mode, float rowHeight, float nameHeight, float steamIdHeight, float buttonHeight)
        {
            Mode = mode; RowHeight = rowHeight; NameHeight = nameHeight; SteamIdHeight = steamIdHeight; ButtonHeight = buttonHeight;
        }

        internal static ApprovalListRenderPlan Create(EApprovalLayoutMode mode)
        {
            return mode == EApprovalLayoutMode.Compact
                ? new ApprovalListRenderPlan(mode, 76f, 20f, 18f, 30f)
                : new ApprovalListRenderPlan(mode, 52f, 20f, 18f, 24f);
        }
    }

    internal enum EApprovalTab { Pending, Whitelist }

    // v6 [P1-E]：最小 scroll 表面抽象，便于测试项目注入故障（ContentSizeOffset setter 抛异常）
    internal interface IApprovalRebuildSurface
    {
        Vector2 ContentSizeOffset { get; set; }
        Vector2 NormalizedStateCenter { get; set; }
        void AddChild(ISleekElement child);
        void RemoveChild(ISleekElement child);
    }

    internal static class P2PHostSessionUI
    {
        private const float EmptyContentHeight = 24f;
        private const float ScrollReservedHeight = 106f;

        private static ISleekElement _boundParent;
        private static ISleekBox _rootBox;
        private static bool _created;

        private static ISleekLabel _hostLabel;
        private static ISleekLabel _pendingCountLabel;
        private static ISleekLabel _statusLabel;
        private static float _statusUntil;

        private static ISleekScrollView _requestScroll;
        private static ISleekScrollView _whitelistScroll;
        private static EApprovalTab _activeTab = EApprovalTab.Pending;

        private static readonly List<ApprovalRow> _approvalRows = new List<ApprovalRow>();
        private static readonly List<WhitelistRow> _whitelistRows = new List<WhitelistRow>();

        // v5 [P1-A/B/C]：精确渲染快照（逐项比较，无 hash 碰撞）
        private static readonly List<PendingRenderEntry> _renderedPending = new List<PendingRenderEntry>(16);
        private static readonly List<WhitelistRenderEntry> _renderedWhitelist = new List<WhitelistRenderEntry>(16);
        private static bool _hasPendingSnapshot;
        private static bool _hasWhitelistSnapshot;
        private static EApprovalLayoutMode _pendingSnapshotMode;
        private static EApprovalLayoutMode _whitelistSnapshotMode;

        private static bool _announcementDone;
        private static float _nextRefreshAt;
        private static ApprovalPanelLayout _lastLayout;

        private const float RefreshIntervalSeconds = 1f;
        private const float StatusDurationSeconds = 2.5f;

        // 测试 hook
        internal static bool _testBypassThreadAssert;
        internal static bool? _testHostActiveOverride;
        internal static IApprovalRebuildSurface _testRequestSurfaceOverride;
        internal static IApprovalRebuildSurface _testWhitelistSurfaceOverride;
        internal static bool _testRowBuildThrows;
        internal static bool _testBypassGlazier; // 测试：跳过 Glazier.CreateLabel/Button，便于测试完整重建事务

        // v7 probe（Beta-2 ScrollContent 诊断）：仅记录计数/模式/可见性/content offset，不含 persona/SteamID
        private static int _renderProbeEpoch;
        private static EApprovalTab? _lastProbeTab;
        private static int _lastProbeShown = Int32.MinValue;
        private static int _lastProbeRows = Int32.MinValue;
        private static Vector2 _lastProbeContent;
        private static string _lastProbePhaseForTest;
        private static EApprovalLayoutMode? _lastProbeModeForTest;
        internal static ApprovalPanelLayout LastLayoutForTest => _lastLayout;
        internal static bool IsCreatedForTest => _created;
        internal static ISleekElement BoundParentForTest => _boundParent;
        internal static EApprovalTab ActiveTabForTest => _activeTab;
        internal static string LastProbePhaseForTest => _lastProbePhaseForTest;
        internal static EApprovalLayoutMode? LastProbeModeForTest => _lastProbeModeForTest;

        // ===== v7 probe（Beta-2 ScrollContent 诊断）=====

        private static void LogRenderProbe(string phase, EApprovalTab tab, EApprovalLayoutMode mode, int sourceCount, int shown, bool snapshotEqual, int rowCount, IApprovalRebuildSurface surface)
        {
            _lastProbePhaseForTest = phase;
            _lastProbeModeForTest = mode;
            Vector2 content = Vector2.zero;
            string surfaceState = "ok";
            try { if (surface == null) surfaceState = "null"; else content = surface.ContentSizeOffset; }
            catch (Exception ex) { surfaceState = "content:" + ex.GetType().Name; }

            bool requestVisible = _requestScroll != null && _requestScroll.IsVisible;
            bool whitelistVisible = _whitelistScroll != null && _whitelistScroll.IsVisible;
            // skip 限频：相同 skip 状态不重复记录（surface-null 不被限频吞掉）
            if (phase == "skip" && _lastProbeTab == tab && _lastProbeShown == shown &&
                _lastProbeRows == rowCount && _lastProbeContent.x == content.x && _lastProbeContent.y == content.y) return;

            _lastProbeTab = tab; _lastProbeShown = shown; _lastProbeRows = rowCount; _lastProbeContent = content;
            // 避免 Vector2.ToString() ECall（SharedInternalsModule）；用 x/y 字段拼接
            RoleLogger.Info("[Host]", "[P2P-PauseUI-Probe] phase=" + phase + " epoch=" + _renderProbeEpoch + " tab=" + tab +
                " mode=" + mode + " source=" + sourceCount + " shown=" + shown + " equal=" + snapshotEqual + " rows=" + rowCount +
                " content=(" + content.x + "," + content.y + ")" +
                " root=" + (_rootBox != null) + " requestVisible=" + requestVisible + " whitelistVisible=" + whitelistVisible + " surface=" + surfaceState);
        }

        // v7 测试初始化器（仅 internal，仅测试项目调用，显式 Reset）
        internal static void TestInitializeCreated(ApprovalPanelLayout layout, IApprovalRebuildSurface requestSurface, IApprovalRebuildSurface whitelistSurface)
        {
            _testBypassThreadAssert = true;
            _testBypassGlazier = true;
            _testRowBuildThrows = false;
            _created = true;
            _lastLayout = layout;
            _testRequestSurfaceOverride = requestSurface;
            _testWhitelistSurfaceOverride = whitelistSurface;
            _activeTab = EApprovalTab.Pending;
            _approvalRows.Clear();
            _whitelistRows.Clear();
            InvalidateRenderSnapshots();
            _nextRefreshAt = float.MaxValue; // 未来值，证明普通 Tick 此刻不会刷新
        }

        internal static void TestReset()
        {
            _testBypassGlazier = false;
            _testRowBuildThrows = false;
            _testRequestSurfaceOverride = null;
            _testWhitelistSurfaceOverride = null;
            _testBypassThreadAssert = false;
            _created = false;
            _activeTab = EApprovalTab.Pending;
            _lastLayout = default(ApprovalPanelLayout);
            _nextRefreshAt = 0f;
            _renderProbeEpoch = 0;
            _lastProbeTab = null;
            _lastProbeShown = Int32.MinValue;
            _lastProbeRows = Int32.MinValue;
            _lastProbeContent = Vector2.zero;
            _lastProbePhaseForTest = null;
            _lastProbeModeForTest = null;
            InvalidateRenderSnapshots();
        }

        // v7 [ActivateTab]：标签切换即时刷新，不依赖下一秒 Tick
        internal static void ActivateTab(EApprovalTab next)
        {
            if (!_testBypassThreadAssert) ThreadUtil.assertIsGameThread();
            _activeTab = next;
            if (_requestScroll != null) _requestScroll.IsVisible = next == EApprovalTab.Pending;
            if (_whitelistScroll != null) _whitelistScroll.IsVisible = next == EApprovalTab.Whitelist;
            _nextRefreshAt = 0f; // 下一帧仍保留定期刷新机会；当前点击立即刷新。
            if (!_created) return;
            try
            {
                RefreshHeaderAndPendingCount();
                RefreshActiveScrollView(_lastLayout);
            }
            catch (Exception ex)
            {
                // 点击 UI 不能让 Unity 事件链抛出；快照未提交时下一帧会因 _nextRefreshAt=0 重试。
                RoleLogger.Warn("[Host]", "[P2P-PauseUI] ActivateTab refresh 异常: " + ex.GetType().Name);
            }
        }

        // ===== 纯逻辑：内容高度公式 =====

        internal static float ComputeContentHeight(int shownCount, ApprovalListRenderPlan plan)
        {
            return shownCount <= 0 ? EmptyContentHeight : Math.Max(EmptyContentHeight, shownCount * plan.RowHeight);
        }

        internal static float ComputeScrollViewportHeight(ApprovalPanelLayout layout)
        {
            return layout.Height - ScrollReservedHeight;
        }

        // ===== v5 精确渲染快照 =====

        internal static bool PendingSnapshotEquals(IReadOnlyList<PendingJoinRequest> pending, int shown, EApprovalLayoutMode mode)
        {
            if (!_hasPendingSnapshot || _pendingSnapshotMode != mode || _renderedPending.Count != shown) return false;
            for (int i = 0; i < shown; i++)
            {
                if (_renderedPending[i].SteamId != pending[i].SteamId.m_SteamID || _renderedPending[i].AttemptCount != pending[i].AttemptCount) return false;
            }
            return true;
        }

        internal static void CapturePendingSnapshot(IReadOnlyList<PendingJoinRequest> pending, int shown, EApprovalLayoutMode mode)
        {
            _renderedPending.Clear();
            int n = pending == null ? 0 : Math.Min(Math.Min(shown, pending.Count), 16);
            for (int i = 0; i < n; i++)
                _renderedPending.Add(new PendingRenderEntry { SteamId = pending[i].SteamId.m_SteamID, AttemptCount = pending[i].AttemptCount });
            _pendingSnapshotMode = mode;
            _hasPendingSnapshot = true;
        }

        internal static bool WhitelistSnapshotEquals(IReadOnlyList<SteamWhitelistID> list, int shown, EApprovalLayoutMode mode)
        {
            if (!_hasWhitelistSnapshot || _whitelistSnapshotMode != mode || _renderedWhitelist.Count != shown) return false;
            for (int i = 0; i < shown; i++)
            {
                if (_renderedWhitelist[i].SteamId != list[i].steamID.m_SteamID || !String.Equals(_renderedWhitelist[i].Tag, list[i].tag, StringComparison.Ordinal)) return false;
            }
            return true;
        }

        internal static void CaptureWhitelistSnapshot(IReadOnlyList<SteamWhitelistID> list, int shown, EApprovalLayoutMode mode)
        {
            _renderedWhitelist.Clear();
            int n = list == null ? 0 : Math.Min(Math.Min(shown, list.Count), 16);
            for (int i = 0; i < n; i++)
                _renderedWhitelist.Add(new WhitelistRenderEntry { SteamId = list[i].steamID.m_SteamID, Tag = list[i].tag });
            _whitelistSnapshotMode = mode;
            _hasWhitelistSnapshot = true;
        }

        // v6 [P1-B/C]：单表失效
        internal static void InvalidatePendingRenderSnapshot()
        {
            _renderedPending.Clear();
            _hasPendingSnapshot = false;
            _pendingSnapshotMode = default(EApprovalLayoutMode);
        }

        internal static void InvalidateWhitelistRenderSnapshot()
        {
            _renderedWhitelist.Clear();
            _hasWhitelistSnapshot = false;
            _whitelistSnapshotMode = default(EApprovalLayoutMode);
        }

        internal static void InvalidateRenderSnapshots()
        {
            InvalidatePendingRenderSnapshot();
            InvalidateWhitelistRenderSnapshot();
        }

        internal static int RenderedPendingCountForTest => _renderedPending.Count;
        internal static int RenderedWhitelistCountForTest => _renderedWhitelist.Count;
        internal static bool HasPendingSnapshotForTest => _hasPendingSnapshot;
        internal static bool HasWhitelistSnapshotForTest => _hasWhitelistSnapshot;
        internal static int ApprovalRowCountForTest => _approvalRows.Count;
        internal static int WhitelistRowCountForTest => _whitelistRows.Count;

        // ===== 会话起始公告 =====

        internal static void TickSessionStartAnnouncement()
        {
            if (!_testBypassThreadAssert) ThreadUtil.assertIsGameThread();
            if (_announcementDone) return;
            if (!Level.isLoaded || !Provider.isServer) return;
            AnnounceHostSteamId();
            _announcementDone = true;
        }

        // ===== Tick =====

        internal static void Tick()
        {
            if (!_testBypassThreadAssert) ThreadUtil.assertIsGameThread();
            if (!P2PClientUiEnvironment.CanTouchClientUi()) { Destroy(); return; }
            bool hostActive = _testHostActiveOverride ?? (HostManager.IsP2PHostMode && Provider.isServer);
            if (!hostActive) { Destroy(); return; }
            ISleekElement parent;
            if (!P2PPauseMenuSurface.TryGetActivePauseContainer(out parent)) { Destroy(); return; }
            ApprovalPanelLayout layout;
            if (!ApprovalPanelLayout.TryCreateFromScreen(out layout)) { Destroy(); return; }
            EnsureCreated(parent, layout);
            if (!_created) return;
            try { RefreshIfNeeded(layout); }
            catch (Exception ex) { RoleLogger.Warn("[Host]", "[P2P-PauseUI] Tick 异常: " + ex.Message); }
        }

        private static void EnsureCreated(ISleekElement parent, ApprovalPanelLayout layout)
        {
            if (!_testBypassThreadAssert) ThreadUtil.assertIsGameThread();
            if (parent == null) { Destroy(); return; }
            if (_created && !ReferenceEquals(_boundParent, parent)) Destroy();
            if (!_created)
            {
                _boundParent = parent;
                try
                {
                    BuildPanel(_boundParent, layout);
                    _lastLayout = layout;
                    InvalidateRenderSnapshots();
                    _created = true;
                    RoleLogger.Info("[Host]", "[P2P-PauseUI] P2PHostSessionUI 已创建 (v6 atomic) w=" + (int)layout.Width + " h=" + (int)layout.Height + " mode=" + layout.Mode);
                }
                catch (Exception ex)
                {
                    RoleLogger.Warn("[Host]", "[P2P-PauseUI] build failed; UI removed: " + ex.GetType().Name);
                    Destroy();
                    return;
                }
            }
            ApplyRootLayout(_rootBox, layout);
            ApplyScrollLayout(_requestScroll, layout);
            ApplyScrollLayout(_whitelistScroll, layout);
            if (layout.Mode != _lastLayout.Mode)
            {
                ClearApprovalRows(RequestSurface);
                ClearWhitelistRows(WhitelistSurface);
                InvalidateRenderSnapshots();
                _nextRefreshAt = 0f;
            }
            _lastLayout = layout;
        }

        private static void RefreshIfNeeded(ApprovalPanelLayout layout)
        {
            float now = Time.unscaledTime;
            if (now >= _nextRefreshAt)
            {
                _nextRefreshAt = now + RefreshIntervalSeconds;
                RefreshHeaderAndPendingCount();
                RefreshActiveScrollView(layout);
            }
            // Stage 7-5 [指令 C]：persona 到达后仅更新 NameLabel.Text，不重建/重绑按钮/不改 CSteamID
            RefreshVisiblePersonaText();
            if (_statusLabel != null)
            {
                bool shouldShow = now < _statusUntil;
                if (_statusLabel.IsVisible != shouldShow) _statusLabel.IsVisible = shouldShow;
            }
        }

        /// <summary>Beta-5 P1：逐行 try/catch；仅文本更新，不 Clear/Build/Capture snapshot；不改 row.SteamId。</summary>
        private static void RefreshVisiblePersonaText()
        {
            if (!_testBypassThreadAssert) ThreadUtil.assertIsGameThread();
            for (int i = 0; i < _approvalRows.Count; i++)
            {
                try
                {
                    if (_approvalRows[i].NameLabel != null)
                        _approvalRows[i].NameLabel.Text = "玩家：" + SteamPersonaDisplay.ResolveDisplayName(_approvalRows[i].SteamId);
                }
                catch (Exception ex) { RoleLogger.Warn("[Host]", "[P2P-PauseUI] persona refresh row " + i + " 异常: " + ex.GetType().Name); }
            }
            for (int i = 0; i < _whitelistRows.Count; i++)
            {
                try
                {
                    if (_whitelistRows[i].NameLabel != null)
                        _whitelistRows[i].NameLabel.Text = "玩家：" + SteamPersonaDisplay.ResolveDisplayName(_whitelistRows[i].SteamId);
                }
                catch (Exception ex) { RoleLogger.Warn("[Host]", "[P2P-PauseUI] persona refresh wl-row " + i + " 异常: " + ex.GetType().Name); }
            }
        }

        // ===== 表面解析（生产用 adapter，测试用 override）=====

        private static IApprovalRebuildSurface RequestSurface
            => _testRequestSurfaceOverride ?? (_requestScroll != null ? new SleekScrollSurface(_requestScroll) : null);
        private static IApprovalRebuildSurface WhitelistSurface
            => _testWhitelistSurfaceOverride ?? (_whitelistScroll != null ? new SleekScrollSurface(_whitelistScroll) : null);

        // ===== 布局应用 =====

        private static void ApplyRootLayout(ISleekBox root, ApprovalPanelLayout layout)
        {
            if (root == null) return;
            root.PositionScale_X = 0f; root.PositionScale_Y = 0f;
            root.PositionOffset_X = (int)ApprovalPanelLayout.LeftInset;
            root.PositionOffset_Y = (int)ApprovalPanelLayout.TopInset;
            root.SizeScale_X = 0f; root.SizeScale_Y = 0f;
            root.SizeOffset_X = (int)layout.Width;
            root.SizeOffset_Y = (int)layout.Height;
        }

        private static void ApplyScrollLayout(ISleekScrollView scroll, ApprovalPanelLayout layout)
        {
            if (scroll == null) return;
            scroll.PositionOffset_X = 8;
            scroll.PositionOffset_Y = 98;
            scroll.SizeScale_X = 1f; scroll.SizeScale_Y = 1f;
            scroll.SizeOffset_X = -16;
            scroll.SizeOffset_Y = -ScrollReservedHeight;
            scroll.ScaleContentToWidth = true;
        }

        // ===== v6 [P1-A] scroll 操作（全部在事务 try 内调用）=====

        private static void SetContentHeight(IApprovalRebuildSurface scroll, int shownCount, ApprovalListRenderPlan plan)
        {
            if (scroll == null) return;
            scroll.ContentSizeOffset = new Vector2(0f, ComputeContentHeight(shownCount, plan));
        }

        private static Vector2? CaptureScrollCenter(IApprovalRebuildSurface scroll)
        {
            if (scroll == null) return null;
            try
            {
                Vector2 center = scroll.NormalizedStateCenter;
                // 新建 ScrollView 的 content 高度为 0 时，uGUI 会产生 NaN/Infinity。
                // 绝不能将该值写回，否则整个 content RectTransform 会失去有效坐标而被裁剪。
                return IsFinite(center) ? center : (Vector2?)null;
            }
            catch { return null; }
        }

        private static void RestoreScrollCenter(IApprovalRebuildSurface scroll, Vector2? center)
        {
            if (scroll == null || !center.HasValue || !IsFinite(center.Value)) return;
            try { scroll.NormalizedStateCenter = center.Value; } catch { }
        }

        private static bool IsFinite(Vector2 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                   !float.IsNaN(value.y) && !float.IsInfinity(value.y);
        }

        // v6 [P1-D]：catch 后 best-effort 收敛 scroll 内容高度（异常吞掉，保留 snapshot-invalid）
        private static void BestEffortResetScrollContent(IApprovalRebuildSurface scroll)
        {
            if (scroll == null) return;
            try { scroll.ContentSizeOffset = Vector2.zero; } catch { /* parent 已失效；snapshot 仍无效 */ }
        }

        private static void TryRemoveChild(ISleekElement parent, ISleekElement child)
        {
            if (parent == null || child == null) return;
            try { parent.RemoveChild(child); } catch (Exception ex) { RoleLogger.Warn("[Host]", "[P2P-PauseUI] RemoveChild 异常: " + ex.Message); }
        }

        private static void TryRemoveChild(IApprovalRebuildSurface parent, ISleekElement child)
        {
            if (parent == null || child == null) return;
            try { parent.RemoveChild(child); } catch { /* fail-soft */ }
        }

        // ===== 面板构建 =====

        private static void BuildPanel(ISleekElement parent, ApprovalPanelLayout layout)
        {
            _rootBox = Glazier.Get().CreateBox();
            ApplyRootLayout(_rootBox, layout);
            parent.AddChild(_rootBox);

            _hostLabel = Glazier.Get().CreateLabel();
            _hostLabel.PositionOffset_X = 6; _hostLabel.PositionOffset_Y = 4;
            _hostLabel.SizeScale_X = 1f; _hostLabel.SizeOffset_X = -70; _hostLabel.SizeOffset_Y = 28;
            _hostLabel.FontSize = ESleekFontSize.Small; _hostLabel.Text = "房主：...";
            _rootBox.AddChild(_hostLabel);

            _pendingCountLabel = Glazier.Get().CreateLabel();
            _pendingCountLabel.PositionOffset_X = 6; _pendingCountLabel.PositionOffset_Y = 32;
            _pendingCountLabel.SizeScale_X = 1f; _pendingCountLabel.SizeOffset_X = -70; _pendingCountLabel.SizeOffset_Y = 20;
            _pendingCountLabel.FontSize = ESleekFontSize.Small; _pendingCountLabel.Text = "待审批：0";
            _rootBox.AddChild(_pendingCountLabel);

            ISleekButton copyBtn = Glazier.Get().CreateButton();
            copyBtn.PositionScale_X = 1f; copyBtn.PositionOffset_X = -64; copyBtn.PositionOffset_Y = 8;
            copyBtn.SizeOffset_X = 58; copyBtn.SizeOffset_Y = 40;
            copyBtn.FontSize = ESleekFontSize.Small; copyBtn.Text = "复制ID";
            copyBtn.OnClicked += OnClickedCopyHostSteamId;
            _rootBox.AddChild(copyBtn);

            ISleekButton tabPending = Glazier.Get().CreateButton();
            tabPending.PositionOffset_X = 6; tabPending.PositionOffset_Y = 68;
            tabPending.SizeScale_X = 0.5f; tabPending.SizeOffset_X = -8; tabPending.SizeOffset_Y = 26;
            tabPending.FontSize = ESleekFontSize.Small; tabPending.Text = "待审批";
            tabPending.OnClicked += OnClickedTabPending;
            _rootBox.AddChild(tabPending);

            ISleekButton tabWhitelist = Glazier.Get().CreateButton();
            tabWhitelist.PositionScale_X = 0.5f; tabWhitelist.PositionOffset_X = 2; tabWhitelist.PositionOffset_Y = 68;
            tabWhitelist.SizeScale_X = 0.5f; tabWhitelist.SizeOffset_X = -8; tabWhitelist.SizeOffset_Y = 26;
            tabWhitelist.FontSize = ESleekFontSize.Small; tabWhitelist.Text = "已允许名单";
            tabWhitelist.OnClicked += OnClickedTabWhitelist;
            _rootBox.AddChild(tabWhitelist);

            _requestScroll = Glazier.Get().CreateScrollView();
            ApplyScrollLayout(_requestScroll, layout);
            _requestScroll.IsVisible = true;
            _rootBox.AddChild(_requestScroll);

            _whitelistScroll = Glazier.Get().CreateScrollView();
            ApplyScrollLayout(_whitelistScroll, layout);
            _whitelistScroll.IsVisible = false;
            _rootBox.AddChild(_whitelistScroll);

            _statusLabel = Glazier.Get().CreateLabel();
            _statusLabel.PositionScale_Y = 1f; _statusLabel.PositionOffset_X = 6; _statusLabel.PositionOffset_Y = -22;
            _statusLabel.SizeScale_X = 1f; _statusLabel.SizeOffset_X = -12; _statusLabel.SizeOffset_Y = 18;
            _statusLabel.FontSize = ESleekFontSize.Small; _statusLabel.TextAlignment = TextAnchor.MiddleLeft;
            _statusLabel.Text = ""; _statusLabel.IsVisible = false;
            _rootBox.AddChild(_statusLabel);
        }

        // ===== 事件处理 =====

        private static void OnClickedCopyHostSteamId(ISleekElement button)
        {
            if (!_testBypassThreadAssert) ThreadUtil.assertIsGameThread();
            try
            {
                CSteamID myId = SteamUser.GetSteamID();
                if (myId == CSteamID.Nil || !myId.IsValid()) { ShowStatus("SteamID 不可用"); return; }
                GUIUtility.systemCopyBuffer = myId.m_SteamID.ToString();
                ShowStatus("已复制");
                RoleLogger.Info("[Host]", "[P2P-PauseUI] 已复制房主 SteamID: " + myId.m_SteamID);
            }
            catch (Exception ex) { RoleLogger.Warn("[Host]", "[P2P-PauseUI] CopyHostSteamId 异常: " + ex.Message); }
        }

        private static void OnClickedTabPending(ISleekElement button)
        {
            ActivateTab(EApprovalTab.Pending);
        }

        private static void OnClickedTabWhitelist(ISleekElement button)
        {
            ActivateTab(EApprovalTab.Whitelist);
        }

        // v6 [P1-C]：Approve 同时失效两表（批准后 pending 变 + 白名单变）；Reject 仅 pending；Remove 仅 whitelist
        private static void OnClickedApprove(ISleekElement button)
        {
            if (!_testBypassThreadAssert) ThreadUtil.assertIsGameThread();
            for (int i = 0; i < _approvalRows.Count; i++)
            {
                if (ReferenceEquals(_approvalRows[i].ApproveButton, button))
                {
                    CSteamID target = _approvalRows[i].SteamId;
                    string feedback;
                    bool ok = P2PJoinApprovalService.Approve(target, out feedback);
                    if (ok) { ShowStatus("已批准 " + target.m_SteamID); RoleLogger.Info("[Host]", "[P2P-PauseUI] Approve ok: " + target.m_SteamID); }
                    else { ShowStatus("批准失败: " + feedback); RoleLogger.Warn("[Host]", "[P2P-PauseUI] Approve 失败: " + target.m_SteamID + " feedback=" + feedback); }
                    InvalidatePendingRenderSnapshot();
                    InvalidateWhitelistRenderSnapshot();
                    _nextRefreshAt = 0f;
                    break;
                }
            }
        }

        private static void OnClickedReject(ISleekElement button)
        {
            if (!_testBypassThreadAssert) ThreadUtil.assertIsGameThread();
            for (int i = 0; i < _approvalRows.Count; i++)
            {
                if (ReferenceEquals(_approvalRows[i].RejectButton, button))
                {
                    CSteamID target = _approvalRows[i].SteamId;
                    P2PJoinApprovalService.RejectForSession(target);
                    ShowStatus("已拒绝 " + target.m_SteamID);
                    RoleLogger.Info("[Host]", "[P2P-PauseUI] RejectForSession: " + target.m_SteamID);
                    InvalidatePendingRenderSnapshot();
                    _nextRefreshAt = 0f;
                    break;
                }
            }
        }

        private static void OnClickedRemove(ISleekElement button)
        {
            if (!_testBypassThreadAssert) ThreadUtil.assertIsGameThread();
            for (int i = 0; i < _whitelistRows.Count; i++)
            {
                if (ReferenceEquals(_whitelistRows[i].RemoveButton, button))
                {
                    CSteamID target = _whitelistRows[i].SteamId;
                    string feedback;
                    bool ok = P2PWhitelistService.TryRemove(target, out feedback);
                    if (ok) { ShowStatus("已移除 " + target.m_SteamID); RoleLogger.Info("[Host]", "[P2P-PauseUI] Remove ok: " + target.m_SteamID); }
                    else { ShowStatus("移除失败: " + feedback); RoleLogger.Warn("[Host]", "[P2P-PauseUI] Remove 失败: " + target.m_SteamID + " feedback=" + feedback); }
                    InvalidateWhitelistRenderSnapshot();
                    _nextRefreshAt = 0f;
                    break;
                }
            }
        }

        // ===== 刷新 =====

        private static void RefreshHeaderAndPendingCount()
        {
            if (_hostLabel != null) { try { _hostLabel.Text = "房主：" + SteamPersonaDisplay.GetLocalDisplayName(); } catch { _hostLabel.Text = "房主：..."; } }
            if (_pendingCountLabel != null)
            {
                try
                {
                    int count = P2PJoinApprovalService.PendingCount;
                    _pendingCountLabel.Text = "待审批：" + count + " / 已允许：" + P2PWhitelistService.SnapshotForUi().Count;
                }
                catch { }
            }
        }

        private static void RefreshActiveScrollView(ApprovalPanelLayout layout)
        {
            if (_activeTab == EApprovalTab.Pending) RefreshApprovalPanel(layout);
            else RefreshWhitelistPanel(layout);
        }

        // v6 [P1-A/B/D]：完整事务 try（Clear+SetContentHeight+Build+Capture+Restore 全在 try 内）；
        //   失败 best-effort 清半建行 + 收敛 scroll + 失效 pending 快照（不重抛，下帧重试）
        internal static void RefreshApprovalPanel(ApprovalPanelLayout layout)
        {
            if (!_testBypassThreadAssert) ThreadUtil.assertIsGameThread();

            // v7：先读 service snapshot，再判定 surface（消除 surface-null 盲区）
            IReadOnlyList<PendingJoinRequest> pending = P2PJoinApprovalService.GetPendingRequests();
            IReadOnlyList<PendingJoinRequest> safe = pending ?? Array.Empty<PendingJoinRequest>();
            int shown = Math.Min(safe.Count, 16);
            IApprovalRebuildSurface surface = RequestSurface;
            if (surface == null)
            {
                LogRenderProbe("surface-null", EApprovalTab.Pending, layout.Mode, safe.Count, shown, false, _approvalRows.Count, null);
                return;
            }

            ApprovalListRenderPlan plan = ApprovalListRenderPlan.Create(layout.Mode);
            bool equals = PendingSnapshotEquals(safe, shown, layout.Mode);
            if (equals)
            {
                LogRenderProbe("skip", EApprovalTab.Pending, layout.Mode, safe.Count, shown, true, _approvalRows.Count, surface);
                return;
            }

            ++_renderProbeEpoch;
            LogRenderProbe("before-build", EApprovalTab.Pending, layout.Mode, safe.Count, shown, false, _approvalRows.Count, surface);
            Vector2? center = CaptureScrollCenter(surface);
            try
            {
                ClearApprovalRows(surface);
                SetContentHeight(surface, shown, plan);
                if (shown == 0) AddEmptyLabel(surface, "暂无待审批请求", true);
                else for (int i = 0; i < shown; i++) BuildApprovalRow(surface, safe[i], i, plan);
                CapturePendingSnapshot(safe, shown, layout.Mode);
                LogRenderProbe("built", EApprovalTab.Pending, layout.Mode, safe.Count, shown, false, _approvalRows.Count, surface);
                RestoreScrollCenter(surface, center);
            }
            catch (Exception ex)
            {
                LogRenderProbe("abort:" + ex.GetType().Name, EApprovalTab.Pending, layout.Mode, safe.Count, shown, false, _approvalRows.Count, surface);
                ClearApprovalRows(surface);
                BestEffortResetScrollContent(surface);
                InvalidatePendingRenderSnapshot();
            }
        }

        internal static void RefreshWhitelistPanel(ApprovalPanelLayout layout)
        {
            if (!_testBypassThreadAssert) ThreadUtil.assertIsGameThread();

            IReadOnlyList<SteamWhitelistID> list = P2PWhitelistService.SnapshotForUi();
            IReadOnlyList<SteamWhitelistID> safe = list ?? Array.Empty<SteamWhitelistID>();
            int shown = Math.Min(safe.Count, 16);
            IApprovalRebuildSurface surface = WhitelistSurface;
            if (surface == null)
            {
                LogRenderProbe("surface-null", EApprovalTab.Whitelist, layout.Mode, safe.Count, shown, false, _whitelistRows.Count, null);
                return;
            }

            ApprovalListRenderPlan plan = ApprovalListRenderPlan.Create(layout.Mode);
            bool equals = WhitelistSnapshotEquals(safe, shown, layout.Mode);
            if (equals)
            {
                LogRenderProbe("skip", EApprovalTab.Whitelist, layout.Mode, safe.Count, shown, true, _whitelistRows.Count, surface);
                return;
            }

            ++_renderProbeEpoch;
            LogRenderProbe("before-build", EApprovalTab.Whitelist, layout.Mode, safe.Count, shown, false, _whitelistRows.Count, surface);
            Vector2? center = CaptureScrollCenter(surface);
            try
            {
                ClearWhitelistRows(surface);
                SetContentHeight(surface, shown, plan);
                if (shown == 0) AddEmptyLabel(surface, "白名单为空", false);
                else for (int i = 0; i < shown; i++) BuildWhitelistRow(surface, safe[i], i, plan);
                CaptureWhitelistSnapshot(safe, shown, layout.Mode);
                LogRenderProbe("built", EApprovalTab.Whitelist, layout.Mode, safe.Count, shown, false, _whitelistRows.Count, surface);
                RestoreScrollCenter(surface, center);
            }
            catch (Exception ex)
            {
                LogRenderProbe("abort:" + ex.GetType().Name, EApprovalTab.Whitelist, layout.Mode, safe.Count, shown, false, _whitelistRows.Count, surface);
                ClearWhitelistRows(surface);
                BestEffortResetScrollContent(surface);
                InvalidateWhitelistRenderSnapshot();
            }
        }

        private static void AddEmptyLabel(IApprovalRebuildSurface scroll, string text, bool isRequest)
        {
            if (_testRowBuildThrows) throw new InvalidOperationException("test: AddEmptyLabel throws");
            if (_testBypassGlazier)
            {
                if (isRequest) _approvalRows.Add(new ApprovalRow());
                else _whitelistRows.Add(new WhitelistRow());
                return;
            }
            ISleekLabel empty = Glazier.Get().CreateLabel();
            empty.PositionOffset_X = 2; empty.PositionOffset_Y = 2;
            empty.SizeScale_X = 1f; empty.SizeOffset_X = -4; empty.SizeOffset_Y = EmptyContentHeight;
            empty.FontSize = ESleekFontSize.Small; empty.Text = text;
            scroll.AddChild(empty);
            if (isRequest) _approvalRows.Add(new ApprovalRow { NameLabel = empty });
            else _whitelistRows.Add(new WhitelistRow { NameLabel = empty });
        }

        private static void BuildApprovalRow(IApprovalRebuildSurface parent, PendingJoinRequest req, int index, ApprovalListRenderPlan plan)
        {
            if (_testRowBuildThrows) throw new InvalidOperationException("test: BuildApprovalRow throws");
            CSteamID steamId = req.SteamId;
            if (_testBypassGlazier)
            {
                _approvalRows.Add(new ApprovalRow { SteamId = steamId });
                return;
            }
            float y = index * plan.RowHeight;
            string persona = SteamPersonaDisplay.ResolveDisplayName(steamId);

            ISleekLabel nameLabel = Glazier.Get().CreateLabel();
            nameLabel.PositionOffset_X = 2; nameLabel.PositionOffset_Y = y;
            nameLabel.SizeScale_X = 1f; nameLabel.SizeOffset_X = plan.Mode == EApprovalLayoutMode.Normal ? -108 : -4;
            nameLabel.SizeOffset_Y = plan.NameHeight;
            nameLabel.FontSize = ESleekFontSize.Small; nameLabel.Text = "玩家：" + persona;
            parent.AddChild(nameLabel);

            ISleekLabel steamIdLabel = Glazier.Get().CreateLabel();
            steamIdLabel.PositionOffset_X = 2; steamIdLabel.PositionOffset_Y = y + plan.NameHeight;
            steamIdLabel.SizeScale_X = 1f; steamIdLabel.SizeOffset_X = plan.Mode == EApprovalLayoutMode.Normal ? -108 : -4;
            steamIdLabel.SizeOffset_Y = plan.SteamIdHeight;
            steamIdLabel.FontSize = ESleekFontSize.Small; steamIdLabel.Text = "SteamID：" + steamId.m_SteamID;
            parent.AddChild(steamIdLabel);

            ISleekButton approveBtn = Glazier.Get().CreateButton();
            ISleekButton rejectBtn = Glazier.Get().CreateButton();
            if (plan.Mode == EApprovalLayoutMode.Normal)
            {
                float btnY = y + (plan.RowHeight - plan.ButtonHeight) * 0.5f;
                approveBtn.PositionScale_X = 1f; approveBtn.PositionOffset_X = -104; approveBtn.PositionOffset_Y = btnY;
                approveBtn.SizeOffset_X = 48; approveBtn.SizeOffset_Y = plan.ButtonHeight;
                rejectBtn.PositionScale_X = 1f; rejectBtn.PositionOffset_X = -52; rejectBtn.PositionOffset_Y = btnY;
                rejectBtn.SizeOffset_X = 48; rejectBtn.SizeOffset_Y = plan.ButtonHeight;
            }
            else
            {
                float btnY = y + plan.NameHeight + plan.SteamIdHeight + 4f;
                approveBtn.PositionOffset_X = 2; approveBtn.PositionOffset_Y = btnY;
                approveBtn.SizeScale_X = 0.5f; approveBtn.SizeOffset_X = -4; approveBtn.SizeOffset_Y = plan.ButtonHeight;
                rejectBtn.PositionScale_X = 0.5f; rejectBtn.PositionOffset_X = 2; rejectBtn.PositionOffset_Y = btnY;
                rejectBtn.SizeScale_X = 0.5f; rejectBtn.SizeOffset_X = -4; rejectBtn.SizeOffset_Y = plan.ButtonHeight;
            }
            approveBtn.FontSize = ESleekFontSize.Small; approveBtn.Text = "允许"; approveBtn.OnClicked += OnClickedApprove;
            parent.AddChild(approveBtn);
            rejectBtn.FontSize = ESleekFontSize.Small; rejectBtn.Text = "拒绝"; rejectBtn.OnClicked += OnClickedReject;
            parent.AddChild(rejectBtn);

            _approvalRows.Add(new ApprovalRow { SteamId = steamId, NameLabel = nameLabel, SteamIdLabel = steamIdLabel, ApproveButton = approveBtn, RejectButton = rejectBtn });
        }

        private static void BuildWhitelistRow(IApprovalRebuildSurface parent, SteamWhitelistID entry, int index, ApprovalListRenderPlan plan)
        {
            if (_testRowBuildThrows) throw new InvalidOperationException("test: BuildWhitelistRow throws");
            CSteamID steamId = entry.steamID;
            if (_testBypassGlazier)
            {
                _whitelistRows.Add(new WhitelistRow { SteamId = steamId });
                return;
            }
            float y = index * plan.RowHeight;
            string persona = SteamPersonaDisplay.ResolveDisplayName(steamId);

            ISleekLabel nameLabel = Glazier.Get().CreateLabel();
            nameLabel.PositionOffset_X = 2; nameLabel.PositionOffset_Y = y;
            nameLabel.SizeScale_X = 1f; nameLabel.SizeOffset_X = plan.Mode == EApprovalLayoutMode.Normal ? -56 : -4;
            nameLabel.SizeOffset_Y = plan.NameHeight;
            nameLabel.FontSize = ESleekFontSize.Small; nameLabel.Text = "玩家：" + persona + " [" + entry.tag + "]";
            parent.AddChild(nameLabel);

            ISleekLabel steamIdLabel = Glazier.Get().CreateLabel();
            steamIdLabel.PositionOffset_X = 2; steamIdLabel.PositionOffset_Y = y + plan.NameHeight;
            steamIdLabel.SizeScale_X = 1f; steamIdLabel.SizeOffset_X = plan.Mode == EApprovalLayoutMode.Normal ? -56 : -4;
            steamIdLabel.SizeOffset_Y = plan.SteamIdHeight;
            steamIdLabel.FontSize = ESleekFontSize.Small; steamIdLabel.Text = "SteamID：" + steamId.m_SteamID;
            parent.AddChild(steamIdLabel);

            ISleekButton removeBtn = Glazier.Get().CreateButton();
            if (plan.Mode == EApprovalLayoutMode.Normal)
            {
                float btnY = y + (plan.RowHeight - plan.ButtonHeight) * 0.5f;
                removeBtn.PositionScale_X = 1f; removeBtn.PositionOffset_X = -52; removeBtn.PositionOffset_Y = btnY;
                removeBtn.SizeOffset_X = 48; removeBtn.SizeOffset_Y = plan.ButtonHeight;
            }
            else
            {
                float btnY = y + plan.NameHeight + plan.SteamIdHeight + 4f;
                removeBtn.PositionOffset_X = 2; removeBtn.PositionOffset_Y = btnY;
                removeBtn.SizeScale_X = 1f; removeBtn.SizeOffset_X = -4; removeBtn.SizeOffset_Y = plan.ButtonHeight;
            }
            removeBtn.FontSize = ESleekFontSize.Small; removeBtn.Text = "移除"; removeBtn.OnClicked += OnClickedRemove;
            parent.AddChild(removeBtn);

            _whitelistRows.Add(new WhitelistRow { SteamId = steamId, NameLabel = nameLabel, SteamIdLabel = steamIdLabel, RemoveButton = removeBtn });
        }

        private static void ClearApprovalRows(IApprovalRebuildSurface scroll)
        {
            if (scroll != null)
            {
                foreach (ApprovalRow row in _approvalRows)
                {
                    if (row.NameLabel != null) TryRemoveChild(scroll, row.NameLabel);
                    if (row.SteamIdLabel != null) TryRemoveChild(scroll, row.SteamIdLabel);
                    if (row.ApproveButton != null) TryRemoveChild(scroll, row.ApproveButton);
                    if (row.RejectButton != null) TryRemoveChild(scroll, row.RejectButton);
                }
            }
            _approvalRows.Clear();
        }

        private static void ClearWhitelistRows(IApprovalRebuildSurface scroll)
        {
            if (scroll != null)
            {
                foreach (WhitelistRow row in _whitelistRows)
                {
                    if (row.NameLabel != null) TryRemoveChild(scroll, row.NameLabel);
                    if (row.SteamIdLabel != null) TryRemoveChild(scroll, row.SteamIdLabel);
                    if (row.RemoveButton != null) TryRemoveChild(scroll, row.RemoveButton);
                }
            }
            _whitelistRows.Clear();
        }

        // ===== 公告 =====

        private static void AnnounceHostSteamId()
        {
            try
            {
                if (!Provider.isServer) return;
                CSteamID myId = SteamUser.GetSteamID();
                if (myId == CSteamID.Nil || !myId.IsValid()) { RoleLogger.Warn("[Host]", "[P2P-SessionUI] 公告跳过：SteamID 不可用"); return; }
                string text = "[P2P] 房主 SteamID: " + myId.m_SteamID + "（客机可复制后通过好友/外部渠道发送）";
                ChatManager.serverSendMessage(text, Color.white, null, null, EChatMode.SAY, null, false);
                RoleLogger.Info("[Host]", "[P2P-SessionUI] 已公告房主 SteamID: " + myId.m_SteamID);
            }
            catch (Exception ex) { RoleLogger.Warn("[Host]", "[P2P-SessionUI] AnnounceHostSteamId 异常（不阻断）: " + ex.Message); }
        }

        private static void ShowStatus(string msg)
        {
            if (_statusLabel == null) return;
            _statusLabel.Text = msg ?? "";
            _statusUntil = Time.unscaledTime + StatusDurationSeconds;
            _statusLabel.IsVisible = true;
        }

        // ===== Destroy / reset =====

        internal static void Destroy()
        {
            if (!_testBypassThreadAssert) ThreadUtil.assertIsGameThread();
            ClearApprovalRows(RequestSurface);
            ClearWhitelistRows(WhitelistSurface);
            if (_boundParent != null) TryRemoveChild(_boundParent, _rootBox);
            _rootBox = null; _hostLabel = null; _pendingCountLabel = null; _statusLabel = null;
            _requestScroll = null; _whitelistScroll = null; _boundParent = null;
            _activeTab = EApprovalTab.Pending; _statusUntil = 0f; _nextRefreshAt = 0f;
            _lastLayout = default(ApprovalPanelLayout);
            InvalidateRenderSnapshots();
            _created = false;
        }

        internal static void ResetForSession()
        {
            if (!_testBypassThreadAssert) ThreadUtil.assertIsGameThread();
            try
            {
                ClearApprovalRows(RequestSurface);
                ClearWhitelistRows(WhitelistSurface);
                _activeTab = EApprovalTab.Pending;
                if (_requestScroll != null) _requestScroll.IsVisible = true;
                if (_whitelistScroll != null) _whitelistScroll.IsVisible = false;
                _announcementDone = false; _statusUntil = 0f;
                if (_statusLabel != null) { _statusLabel.Text = ""; _statusLabel.IsVisible = false; }
                if (_pendingCountLabel != null) _pendingCountLabel.Text = "待审批：0";
                InvalidateRenderSnapshots();
                _nextRefreshAt = 0f;
            }
            catch (Exception ex) { RoleLogger.Warn("[Host]", "[P2P-PauseUI] ResetForSession 异常: " + ex.Message); }
        }

        internal static void ResetAfterSession() { Destroy(); }

        // ===== 内部结构 =====

        private struct ApprovalRow
        {
            public CSteamID SteamId;
            public ISleekLabel NameLabel;
            public ISleekLabel SteamIdLabel;
            public ISleekButton ApproveButton;
            public ISleekButton RejectButton;
        }

        private struct WhitelistRow
        {
            public CSteamID SteamId;
            public ISleekLabel NameLabel;
            public ISleekLabel SteamIdLabel;
            public ISleekButton RemoveButton;
        }

        private struct PendingRenderEntry { public ulong SteamId; public int AttemptCount; }
        private struct WhitelistRenderEntry { public ulong SteamId; public string Tag; }

        // v6 [P1-E]：ISleekScrollView -> IApprovalRebuildSurface adapter
        private readonly struct SleekScrollSurface : IApprovalRebuildSurface
        {
            private readonly ISleekScrollView _scroll;
            internal SleekScrollSurface(ISleekScrollView scroll) { _scroll = scroll; }
            public Vector2 ContentSizeOffset
            {
                get { return _scroll.ContentSizeOffset; }
                set { _scroll.ContentSizeOffset = value; }
            }
            public Vector2 NormalizedStateCenter
            {
                get { return _scroll.NormalizedStateCenter; }
                set { _scroll.NormalizedStateCenter = value; }
            }
            public void AddChild(ISleekElement child) { _scroll.AddChild(child); }
            public void RemoveChild(ISleekElement child) { _scroll.RemoveChild(child); }
        }
    }
}
