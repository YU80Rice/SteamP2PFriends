using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.UI;
using Steamworks;
using System;
using System.Collections.Generic;

namespace SteamP2PFriends.WhitelistTests
{
    /// <summary>
    /// Stage 7-4 v3 [指令 B/C/D] ApprovalPanelLayout 几何/响应式测试。
    /// 蓝图 §4 门 1/2：蓝框几何 + 多分辨率不遮挡中央原版菜单。
    ///   - L1: 1920x1080 Normal，宽~278 高~902，panelRight <= menuLeft - 20
    ///   - L2: 2560x1440 Normal，宽<=360（MaxWidth 截断）
    ///   - L3: 1280x720 Normal，宽 220（NormalMinWidth 下限）
    ///   - L4: 648x1080 Compact（可用宽 200，<220 -> Compact）
    ///   - L5: 500x800 fail-closed（可用宽 126 < 160）
    ///   - L6: 1920x300 fail-closed（高不足）
    ///   - L7: panelRight <= menuLeft - 20 不变量（所有通过用例）
    /// </summary>
    internal static class ApprovalPanelLayoutTests
    {
        private const float LeftInset = ApprovalPanelLayout.LeftInset;
        private const float NativeMenuHalfWidth = ApprovalPanelLayout.NativeMenuHalfWidth;
        private const float NativeMenuGap = ApprovalPanelLayout.NativeMenuGap;

        private static float PanelRight(ApprovalPanelLayout l) { return LeftInset + l.Width; }
        private static float MenuLeft(float vw) { return vw * 0.5f - NativeMenuHalfWidth; }

        internal static bool Test_v3_L1_1920x1080_NormalGeometry()
        {
            if (!ApprovalPanelLayout.TryCreate(1920f, 1080f, out ApprovalPanelLayout l))
                return Fail("1920x1080 should create", "false");
            if (l.Mode != EApprovalLayoutMode.Normal) return Fail("mode should be Normal", l.Mode.ToString());
            // 宽 ~ 1920*0.145 = 278.4，在 [220,360] -> 278
            if (l.Width < 270f || l.Width > 290f) return Fail("width ~278", "w=" + l.Width);
            // 高 ~ 1080*0.835 = 901.8，maxHeight = 1080-22-90 = 968 -> 902
            if (l.Height < 890f || l.Height > 910f) return Fail("height ~902", "h=" + l.Height);
            if (PanelRight(l) > MenuLeft(1920f) - NativeMenuGap) return Fail("panelRight must <= menuLeft-20", "pr=" + PanelRight(l));
            return true;
        }

        internal static bool Test_v3_L2_2560x1440_MaxWidthClamp()
        {
            if (!ApprovalPanelLayout.TryCreate(2560f, 1440f, out ApprovalPanelLayout l))
                return Fail("2560x1440 should create", "false");
            if (l.Mode != EApprovalLayoutMode.Normal) return Fail("mode should be Normal", l.Mode.ToString());
            // 2560*0.145 = 371.2 -> clamp to MaxWidth 360
            if (l.Width > 360f) return Fail("width must clamp to MaxWidth 360", "w=" + l.Width);
            if (PanelRight(l) > MenuLeft(2560f) - NativeMenuGap) return Fail("panelRight must <= menuLeft-20", "pr=" + PanelRight(l));
            return true;
        }

        internal static bool Test_v3_L3_1280x720_NormalMinWidth()
        {
            if (!ApprovalPanelLayout.TryCreate(1280f, 720f, out ApprovalPanelLayout l))
                return Fail("1280x720 should create", "false");
            if (l.Mode != EApprovalLayoutMode.Normal) return Fail("mode should be Normal", l.Mode.ToString());
            // 1280*0.145 = 185.6 -> clamp to NormalMinWidth 220
            if (l.Width < 219f || l.Width > 221f) return Fail("width should be NormalMinWidth 220", "w=" + l.Width);
            if (PanelRight(l) > MenuLeft(1280f) - NativeMenuGap) return Fail("panelRight must <= menuLeft-20", "pr=" + PanelRight(l));
            return true;
        }

        internal static bool Test_v3_L4_CompactMode()
        {
            // 648x1080: safeRight = 324-120 = 204; maximumWidth = 204-4 = 200; 200 < 220 -> Compact
            if (!ApprovalPanelLayout.TryCreate(648f, 1080f, out ApprovalPanelLayout l))
                return Fail("648x1080 should create (Compact)", "false");
            if (l.Mode != EApprovalLayoutMode.Compact) return Fail("mode should be Compact", l.Mode.ToString());
            if (l.Width < 160f) return Fail("Compact width must >= AbsoluteMinWidth 160", "w=" + l.Width);
            if (l.Width >= 220f) return Fail("Compact width must < 220", "w=" + l.Width);
            if (PanelRight(l) > MenuLeft(648f) - NativeMenuGap) return Fail("panelRight must <= menuLeft-20 (Compact)", "pr=" + PanelRight(l));
            return true;
        }

        internal static bool Test_v3_L5_NarrowWidth_FailClosed()
        {
            // 500x800: maximumWidth = 250-120-4 = 126 < 160 -> fail-closed
            if (ApprovalPanelLayout.TryCreate(500f, 800f, out ApprovalPanelLayout l))
                return Fail("500x800 should fail-closed (width < 160)", "created w=" + l.Width);
            return true;
        }

        internal static bool Test_v3_L6_NarrowHeight_FailClosed()
        {
            // 1920x300: maximumHeight = 300-22-90 = 188 < 280 -> fail-closed
            if (ApprovalPanelLayout.TryCreate(1920f, 300f, out ApprovalPanelLayout l))
                return Fail("1920x300 should fail-closed (height < 280)", "created h=" + l.Height);
            return true;
        }

        internal static bool Test_v3_L7_NoOverlapInvariant()
        {
            // 多分辨率遍历：所有通过用例必须 panelRight <= menuLeft - 20
            float[] widths = { 800f, 1024f, 1280f, 1366f, 1600f, 1920f, 2048f, 2560f, 648f };
            float[] heights = { 600f, 720f, 768f, 900f, 1024f, 1080f, 1440f };
            foreach (float vw in widths)
            {
                foreach (float vh in heights)
                {
                    if (!ApprovalPanelLayout.TryCreate(vw, vh, out ApprovalPanelLayout l)) continue;
                    float panelRight = PanelRight(l);
                    float menuLeft = MenuLeft(vw);
                    if (panelRight > menuLeft - NativeMenuGap)
                        return Fail("overlap at " + vw + "x" + vh, "panelRight=" + panelRight + " menuLeft-20=" + (menuLeft - NativeMenuGap));
                    if (l.Width < ApprovalPanelLayout.AbsoluteMinWidth)
                        return Fail("width below absolute min at " + vw + "x" + vh, "w=" + l.Width);
                }
            }
            return true;
        }

        private static bool Fail(string msg, string detail)
        {
            Console.WriteLine("    FAIL: " + msg + (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")"));
            return false;
        }

        // =====================================================================
        // v4 ScrollIntegrity [P0-A/B/C/D] L8-L12
        // =====================================================================

        // L8: Normal/Compact render plan 行高 + Name/SteamID 两行 + 16 项 ContentHeight
        internal static bool Test_v4_L8_RenderPlanAndContentHeight()
        {
            var normal = ApprovalListRenderPlan.Create(EApprovalLayoutMode.Normal);
            if (normal.RowHeight != 52f) return Fail("Normal RowHeight=52", normal.RowHeight.ToString());
            if (normal.NameHeight != 20f) return Fail("Normal NameHeight=20", normal.NameHeight.ToString());
            if (normal.SteamIdHeight != 18f) return Fail("Normal SteamIdHeight=18", normal.SteamIdHeight.ToString());
            if (normal.ButtonHeight != 24f) return Fail("Normal ButtonHeight=24", normal.ButtonHeight.ToString());

            var compact = ApprovalListRenderPlan.Create(EApprovalLayoutMode.Compact);
            if (compact.RowHeight != 76f) return Fail("Compact RowHeight=76", compact.RowHeight.ToString());
            if (compact.NameHeight != 20f) return Fail("Compact NameHeight=20", compact.NameHeight.ToString());
            if (compact.SteamIdHeight != 18f) return Fail("Compact SteamIdHeight=18", compact.SteamIdHeight.ToString());
            if (compact.ButtonHeight != 30f) return Fail("Compact ButtonHeight=30", compact.ButtonHeight.ToString());

            // 16 项内容高度 = 16 × RowHeight
            if (P2PHostSessionUI.ComputeContentHeight(16, normal) != 16f * 52f)
                return Fail("Normal 16 content=832", P2PHostSessionUI.ComputeContentHeight(16, normal).ToString());
            if (P2PHostSessionUI.ComputeContentHeight(16, compact) != 16f * 76f)
                return Fail("Compact 16 content=1216", P2PHostSessionUI.ComputeContentHeight(16, compact).ToString());
            return true;
        }

        // L9: 空列表 ContentHeight == EmptyContentHeight，不沿用大列表范围
        internal static bool Test_v4_L9_EmptyContentHeight()
        {
            var plan = ApprovalListRenderPlan.Create(EApprovalLayoutMode.Normal);
            float empty = P2PHostSessionUI.ComputeContentHeight(0, plan);
            // EmptyContentHeight=24（私有常量；通过公式验证）
            if (empty != 24f) return Fail("empty content should be 24", empty.ToString());
            // 空列表不应等于 16 项高度
            if (empty == P2PHostSessionUI.ComputeContentHeight(16, plan))
                return Fail("empty must not equal 16-item height", "same");
            // 负数 shown 也按空处理
            if (P2PHostSessionUI.ComputeContentHeight(-1, plan) != 24f)
                return Fail("negative shown should be empty", "");
            return true;
        }

        // L10: 16 项 Compact contentHeight > scroll viewport（存在可滚动范围）
        internal static bool Test_v4_L10_Compact16Scrollable()
        {
            if (!ApprovalPanelLayout.TryCreate(648f, 1080f, out ApprovalPanelLayout layout))
                return Fail("648x1080 should create (Compact)", "");
            if (layout.Mode != EApprovalLayoutMode.Compact) return Fail("mode should be Compact", layout.Mode.ToString());
            var plan = ApprovalListRenderPlan.Create(layout.Mode);
            float contentHeight = P2PHostSessionUI.ComputeContentHeight(16, plan);
            float viewport = P2PHostSessionUI.ComputeScrollViewportHeight(layout);
            if (contentHeight <= viewport)
                return Fail("16 Compact content must > viewport (scrollable)", "content=" + contentHeight + " viewport=" + viewport);
            return true;
        }

        // L11: (removed in v5 - int hash 可碰撞，已改为精确逐项快照比较；见 L13-L16)
        // L13: pending 精确快照 - 模式/顺序/SteamID/AttemptCount/shownCount 任一变化 -> false；同序列 -> true
        internal static bool Test_v5_L13_PendingSnapshotEquals()
        {
            P2PHostSessionUI.InvalidateRenderSnapshots();
            CSteamID id1 = new CSteamID(76561199721762479UL);
            CSteamID id2 = new CSteamID(76561198000000001UL);

            var p1 = new List<PendingJoinRequest> { new PendingJoinRequest(id1, 1000f), new PendingJoinRequest(id2, 1001f) };
            var p2 = new List<PendingJoinRequest> { new PendingJoinRequest(id1, 1000f), new PendingJoinRequest(id2, 1001f) };

            // 无快照 -> false
            if (P2PHostSessionUI.PendingSnapshotEquals(p1, 2, EApprovalLayoutMode.Normal))
                return Fail("no snapshot should be false", "true before capture");

            P2PHostSessionUI.CapturePendingSnapshot(p1, 2, EApprovalLayoutMode.Normal);
            if (!P2PHostSessionUI.PendingSnapshotEquals(p2, 2, EApprovalLayoutMode.Normal))
                return Fail("same sequence should be true", "false");

            // AttemptCount 改变 -> false
            var p3 = new List<PendingJoinRequest> { p1[0].WithNewAttempt(1002f), p1[1] };
            if (P2PHostSessionUI.PendingSnapshotEquals(p3, 2, EApprovalLayoutMode.Normal))
                return Fail("AttemptCount change should be false", "true");

            // 顺序改变 -> false
            var p4 = new List<PendingJoinRequest> { p1[1], p1[0] };
            if (P2PHostSessionUI.PendingSnapshotEquals(p4, 2, EApprovalLayoutMode.Normal))
                return Fail("order change should be false", "true");

            // SteamID 改变 -> false
            var p5 = new List<PendingJoinRequest> { new PendingJoinRequest(new CSteamID(99999999999999999UL), 1000f), p1[1] };
            if (P2PHostSessionUI.PendingSnapshotEquals(p5, 2, EApprovalLayoutMode.Normal))
                return Fail("SteamID change should be false", "true");

            // mode 改变 -> false
            if (P2PHostSessionUI.PendingSnapshotEquals(p2, 2, EApprovalLayoutMode.Compact))
                return Fail("mode change should be false", "true");

            // shownCount 改变 -> false
            if (P2PHostSessionUI.PendingSnapshotEquals(p2, 1, EApprovalLayoutMode.Normal))
                return Fail("shownCount change should be false", "true");
            return true;
        }

        // L14: whitelist 精确快照 - tag/顺序/SteamID/mode 改变 -> false；StringComparison.Ordinal 大小写差异 -> false
        internal static bool Test_v5_L14_WhitelistSnapshotEquals()
        {
            P2PHostSessionUI.InvalidateRenderSnapshots();
            CSteamID id1 = new CSteamID(76561199721762479UL);
            CSteamID judge = new CSteamID(76561199030780228UL);

            var w1 = new List<SteamWhitelistID> { new SteamWhitelistID(id1, "APPROVED", judge) };
            var w2 = new List<SteamWhitelistID> { new SteamWhitelistID(id1, "APPROVED", judge) };

            P2PHostSessionUI.CaptureWhitelistSnapshot(w1, 1, EApprovalLayoutMode.Normal);
            if (!P2PHostSessionUI.WhitelistSnapshotEquals(w2, 1, EApprovalLayoutMode.Normal))
                return Fail("same whitelist should be true", "false");

            // tag 改变 -> false
            var w3 = new List<SteamWhitelistID> { new SteamWhitelistID(id1, "OTHER", judge) };
            if (P2PHostSessionUI.WhitelistSnapshotEquals(w3, 1, EApprovalLayoutMode.Normal))
                return Fail("tag change should be false", "true");

            // tag 大小写差异（Ordinal）-> false
            var w4 = new List<SteamWhitelistID> { new SteamWhitelistID(id1, "approved", judge) };
            if (P2PHostSessionUI.WhitelistSnapshotEquals(w4, 1, EApprovalLayoutMode.Normal))
                return Fail("tag case difference (Ordinal) should be false", "true");

            // SteamID 改变 -> false
            var w5 = new List<SteamWhitelistID> { new SteamWhitelistID(new CSteamID(88888888888888888UL), "APPROVED", judge) };
            if (P2PHostSessionUI.WhitelistSnapshotEquals(w5, 1, EApprovalLayoutMode.Normal))
                return Fail("SteamID change should be false", "true");

            // mode 改变 -> false
            if (P2PHostSessionUI.WhitelistSnapshotEquals(w2, 1, EApprovalLayoutMode.Compact))
                return Fail("mode change should be false", "true");
            return true;
        }

        // L15: 先 16 项、后空列表、再同一空列表：16->0 重建（false），0->0 不重建（true）；EmptyContentHeight=24 持续
        internal static bool Test_v5_L15_EmptySnapshotStability()
        {
            P2PHostSessionUI.InvalidateRenderSnapshots();

            // 16 项 -> capture
            var p16 = new List<PendingJoinRequest>();
            for (ulong i = 1; i <= 16; i++) p16.Add(new PendingJoinRequest(new CSteamID(76561198000000000UL + i), 1000f));
            P2PHostSessionUI.CapturePendingSnapshot(p16, 16, EApprovalLayoutMode.Normal);
            if (P2PHostSessionUI.RenderedPendingCountForTest != 16)
                return Fail("16-item capture count=16", P2PHostSessionUI.RenderedPendingCountForTest.ToString());

            // 空列表 -> 16 != 0 -> false（重建）
            var p0 = new List<PendingJoinRequest>();
            if (P2PHostSessionUI.PendingSnapshotEquals(p0, 0, EApprovalLayoutMode.Normal))
                return Fail("16->0 should be false (rebuild)", "true");

            // capture 空列表
            P2PHostSessionUI.CapturePendingSnapshot(p0, 0, EApprovalLayoutMode.Normal);
            if (P2PHostSessionUI.RenderedPendingCountForTest != 0)
                return Fail("empty capture count=0", P2PHostSessionUI.RenderedPendingCountForTest.ToString());

            // 同一空列表 -> 0 == 0 -> true（不重建）
            if (!P2PHostSessionUI.PendingSnapshotEquals(p0, 0, EApprovalLayoutMode.Normal))
                return Fail("same empty should be true (no rebuild)", "false");

            // EmptyContentHeight 持续保留
            var plan = ApprovalListRenderPlan.Create(EApprovalLayoutMode.Normal);
            if (P2PHostSessionUI.ComputeContentHeight(0, plan) != 24f)
                return Fail("empty content=24", "");
            return true;
        }

        // L16: 快照项目数永远 <=16；名称不参与比较/授权
        internal static bool Test_v5_L16_SnapshotCapAndNameIrrelevant()
        {
            P2PHostSessionUI.InvalidateRenderSnapshots();

            // 20 项传入 -> capture 内部 cap 至 16
            var p20 = new List<PendingJoinRequest>();
            for (ulong i = 1; i <= 20; i++) p20.Add(new PendingJoinRequest(new CSteamID(76561198000000000UL + i), 1000f));
            P2PHostSessionUI.CapturePendingSnapshot(p20, 20, EApprovalLayoutMode.Normal);
            if (P2PHostSessionUI.RenderedPendingCountForTest > 16)
                return Fail("snapshot must cap at 16", P2PHostSessionUI.RenderedPendingCountForTest.ToString());

            // 名称不参与快照（PendingRenderEntry 仅 SteamId/AttemptCount；PendingJoinRequest 无名称字段）
            P2PHostSessionUI.InvalidateRenderSnapshots();
            CSteamID id1 = new CSteamID(76561199721762479UL);
            var q1 = new List<PendingJoinRequest> { new PendingJoinRequest(id1, 1000f) };
            var q2 = new List<PendingJoinRequest> { new PendingJoinRequest(id1, 1000f) };
            P2PHostSessionUI.CapturePendingSnapshot(q1, 1, EApprovalLayoutMode.Normal);
            if (!P2PHostSessionUI.PendingSnapshotEquals(q2, 1, EApprovalLayoutMode.Normal))
                return Fail("same SteamID/AttemptCount should be true (name irrelevant)", "false");
            return true;
        }

        // L12: 800x600(Normal) / 648x1080(Compact) 计划均拆 Name/SteamID 两行，非单行拼接
        internal static bool Test_v4_L12_TwoLinePlanNoSingleLine()
        {
            float[][] cases = new float[][] { new float[] { 800f, 600f }, new float[] { 648f, 1080f } };
            foreach (float[] c in cases)
            {
                float vw = c[0], vh = c[1];
                if (!ApprovalPanelLayout.TryCreate(vw, vh, out ApprovalPanelLayout layout))
                    return Fail(vw + "x" + vh + " should create", "");
                var plan = ApprovalListRenderPlan.Create(layout.Mode);
                if (plan.NameHeight <= 0f) return Fail("NameHeight must > 0 (two-line)", vw + " mode=" + layout.Mode);
                if (plan.SteamIdHeight <= 0f) return Fail("SteamIdHeight must > 0 (two-line)", vw + " mode=" + layout.Mode);
                if (plan.RowHeight < plan.NameHeight + plan.SteamIdHeight)
                    return Fail("RowHeight must fit both lines", vw + " rh=" + plan.RowHeight + " need>=" + (plan.NameHeight + plan.SteamIdHeight));
            }
            return true;
        }
    }
}
