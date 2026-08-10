using SDG.Unturned;
using SteamP2PFriends.Shared;
using SteamP2PFriends.UI;
using System;

namespace SteamP2PFriends.WhitelistTests
{
    /// <summary>
    /// Stage 7-4 §4 门 1/2/3 UI 生命周期测试（ESC 暂停菜单 parent）。
    /// 蓝图 EscApprovalMenu [指令 A/B/C/E]：
    ///   - U13: P2PHostSessionUI CanTouchClientUi=false 时 Destroy 不创建（U3DS 隔离）
    ///   - U14: P2PHostSessionUI hostActive=false 时 Destroy 不创建（非房主）
    ///   - U15: P2PHostSessionUI pause surface 返回 null parent 时 Destroy 不创建
    ///   - U16: P2PHostSessionUI ResetAfterSession 调用 Destroy
    ///   - U19: P2PHostSessionUI pauseActive=false（ESC 未开）时 Destroy 不创建
    ///   - U20: P2PPauseMenuSurface active=false -> false（不读 reflection parent）
    ///   - U21: P2PPauseMenuSurface active=true + container=null -> false
    ///   - U22: P2PPauseMenuSurface CanTouchClientUi=false -> false
    ///   - U17/U18: P2PNativeMenuUI / CanTouchClientUi 既有用例保留
    /// </summary>
    internal static class UiLifecycleTests
    {
        // 测试 hook 配置助手：统一设置 P2PHostSessionUI + P2PPauseMenuSurface 测试状态。
        // pauseActive 由 P2PPauseMenuSurface._testActiveOverride 独占（P2PHostSessionUI 不直接引用 PlayerPauseUI）。
        private static void ConfigurePauseSurface(bool? hostActive, bool? pauseActive, Func<ISleekElement> containerProvider)
        {
            P2PHostSessionUI._testBypassThreadAssert = true;
            P2PHostSessionUI._testHostActiveOverride = hostActive;
            P2PPauseMenuSurface._testBypassThreadAssert = true;
            P2PPauseMenuSurface._testActiveOverride = pauseActive;
            P2PPauseMenuSurface._testContainerProvider = containerProvider;
        }

        private static void ClearPauseSurface()
        {
            P2PHostSessionUI._testHostActiveOverride = null;
            P2PHostSessionUI._testBypassThreadAssert = false;
            P2PPauseMenuSurface._testActiveOverride = null;
            P2PPauseMenuSurface._testContainerProvider = null;
            P2PPauseMenuSurface._testBypassThreadAssert = false;
        }

        // ===== U13. P2PHostSessionUI U3DS 隔离：CanTouchClientUi=false 不创建 =====
        internal static bool Test_v4_U13_HostSessionUI_U3DS_NoCreate()
        {
            using (P2PClientUiEnvironment.OverrideForTest(false))
            {
                ConfigurePauseSurface(true, true, () => null); // 不应被访问
                try
                {
                    P2PHostSessionUI.Destroy();
                    P2PHostSessionUI.Tick();
                    if (P2PHostSessionUI.IsCreatedForTest)
                        return Fail("P2PHostSessionUI should NOT create when CanTouchClientUi=false", "created=true");
                    if (P2PHostSessionUI.BoundParentForTest != null)
                        return Fail("BoundParent should remain null", "boundParent != null");
                }
                finally { ClearPauseSurface(); }
            }
            return true;
        }

        // ===== U14. P2PHostSessionUI 非房主：hostActive=false 不创建 =====
        internal static bool Test_v4_U14_HostSessionUI_NonHost_NoCreate()
        {
            using (P2PClientUiEnvironment.OverrideForTest(true))
            {
                ConfigurePauseSurface(false, true, () => null); // 非房主
                try
                {
                    P2PHostSessionUI.Destroy();
                    P2PHostSessionUI.Tick();
                    if (P2PHostSessionUI.IsCreatedForTest)
                        return Fail("P2PHostSessionUI should NOT create when hostActive=false", "created=true");
                }
                finally { ClearPauseSurface(); }
            }
            return true;
        }

        // ===== U15. P2PHostSessionUI pause surface 返回 null parent 不创建 =====
        internal static bool Test_v4_U15_HostSessionUI_NullParent_NoCreate()
        {
            using (P2PClientUiEnvironment.OverrideForTest(true))
            {
                ConfigurePauseSurface(true, true, () => null); // active=true 但 container=null
                try
                {
                    P2PHostSessionUI.Destroy();
                    P2PHostSessionUI.Tick();
                    if (P2PHostSessionUI.IsCreatedForTest)
                        return Fail("P2PHostSessionUI should NOT create when pause parent=null", "created=true");
                    if (P2PHostSessionUI.BoundParentForTest != null)
                        return Fail("BoundParent should remain null", "boundParent != null");
                }
                finally { ClearPauseSurface(); }
            }
            return true;
        }

        // ===== U16. P2PHostSessionUI ResetAfterSession 调用 Destroy =====
        internal static bool Test_v4_U16_HostSessionUI_ResetAfterSession_Destroys()
        {
            using (P2PClientUiEnvironment.OverrideForTest(true))
            {
                ConfigurePauseSurface(true, true, () => null);
                try
                {
                    P2PHostSessionUI.Destroy();
                    P2PHostSessionUI.ResetAfterSession();
                    if (P2PHostSessionUI.IsCreatedForTest)
                        return Fail("ResetAfterSession should leave _created=false", "created=true");
                    if (P2PHostSessionUI.BoundParentForTest != null)
                        return Fail("ResetAfterSession should clear _boundParent", "boundParent != null");
                }
                finally { ClearPauseSurface(); }
            }
            return true;
        }

        // ===== U19. P2PHostSessionUI pauseActive=false（ESC 未开）不创建 =====
        internal static bool Test_v4_U19_HostSessionUI_PauseInactive_NoCreate()
        {
            using (P2PClientUiEnvironment.OverrideForTest(true))
            {
                // 房主活动但 ESC 未开 -> pauseActive=false
                ConfigurePauseSurface(true, false, () => null);
                try
                {
                    P2PHostSessionUI.Destroy();
                    P2PHostSessionUI.Tick();
                    if (P2PHostSessionUI.IsCreatedForTest)
                        return Fail("P2PHostSessionUI should NOT create when pauseActive=false (ESC closed)", "created=true");
                    if (P2PHostSessionUI.BoundParentForTest != null)
                        return Fail("BoundParent should remain null when ESC closed", "boundParent != null");
                }
                finally { ClearPauseSurface(); }
            }
            return true;
        }

        // ===== U20. P2PPauseMenuSurface active=false -> false（不读 reflection parent）=====
        internal static bool Test_v4_U20_PauseSurface_Inactive_ReturnsFalse()
        {
            using (P2PClientUiEnvironment.OverrideForTest(true))
            {
                P2PPauseMenuSurface._testBypassThreadAssert = true;
                P2PPauseMenuSurface._testActiveOverride = false; // ESC 未开
                P2PPauseMenuSurface._testContainerProvider = () => null; // 不应被访问
                try
                {
                    ISleekElement parent;
                    bool ok = P2PPauseMenuSurface.TryGetActivePauseContainer(out parent);
                    if (ok) return Fail("active=false should return false", "ok=true");
                    if (parent != null) return Fail("parent should be null when inactive", "parent != null");
                }
                finally
                {
                    P2PPauseMenuSurface._testActiveOverride = null;
                    P2PPauseMenuSurface._testContainerProvider = null;
                    P2PPauseMenuSurface._testBypassThreadAssert = false;
                }
            }
            return true;
        }

        // ===== U21. P2PPauseMenuSurface active=true + container=null -> false =====
        internal static bool Test_v4_U21_PauseSurface_NullContainer_ReturnsFalse()
        {
            using (P2PClientUiEnvironment.OverrideForTest(true))
            {
                P2PPauseMenuSurface._testBypassThreadAssert = true;
                P2PPauseMenuSurface._testActiveOverride = true; // ESC 开
                P2PPauseMenuSurface._testContainerProvider = () => null; // container 字段为 null
                try
                {
                    ISleekElement parent;
                    bool ok = P2PPauseMenuSurface.TryGetActivePauseContainer(out parent);
                    if (ok) return Fail("container=null should return false", "ok=true");
                    if (parent != null) return Fail("parent should be null", "parent != null");
                }
                finally
                {
                    P2PPauseMenuSurface._testActiveOverride = null;
                    P2PPauseMenuSurface._testContainerProvider = null;
                    P2PPauseMenuSurface._testBypassThreadAssert = false;
                }
            }
            return true;
        }

        // ===== U22. P2PPauseMenuSurface CanTouchClientUi=false -> false（U3DS）=====
        internal static bool Test_v4_U22_PauseSurface_U3DS_ReturnsFalse()
        {
            using (P2PClientUiEnvironment.OverrideForTest(false))
            {
                P2PPauseMenuSurface._testBypassThreadAssert = true;
                P2PPauseMenuSurface._testActiveOverride = true;
                P2PPauseMenuSurface._testContainerProvider = () => null; // 不应被访问
                try
                {
                    ISleekElement parent;
                    bool ok = P2PPauseMenuSurface.TryGetActivePauseContainer(out parent);
                    if (ok) return Fail("CanTouchClientUi=false should return false", "ok=true");
                    if (parent != null) return Fail("parent should be null in U3DS", "parent != null");
                }
                finally
                {
                    P2PPauseMenuSurface._testActiveOverride = null;
                    P2PPauseMenuSurface._testContainerProvider = null;
                    P2PPauseMenuSurface._testBypassThreadAssert = false;
                }
            }
            return true;
        }

        // ===== U17. P2PNativeMenuUI U3DS 隔离：CanTouchClientUi=false 不创建（保留）=====
        internal static bool Test_v3_U17_NativeMenuUI_U3DS_NoCreate()
        {
            using (P2PClientUiEnvironment.OverrideForTest(false))
            {
                P2PNativeMenuUI._testBypassThreadAssert = true;
                P2PNativeMenuUI._testParentProvider = () => null;
                try
                {
                    P2PNativeMenuUI.Destroy();
                    P2PNativeMenuUI.EnsureCreated();
                    if (P2PNativeMenuUI.IsCreatedForTest)
                        return Fail("P2PNativeMenuUI should NOT create when CanTouchClientUi=false", "created=true");
                }
                finally
                {
                    P2PNativeMenuUI._testParentProvider = null;
                    P2PNativeMenuUI._testBypassThreadAssert = false;
                }
            }
            return true;
        }

        // ===== U18. P2PClientUiEnvironment.OverrideForTest 行为验证（保留）=====
        internal static bool Test_v3_U18_CanTouchClientUi_Override_Scope()
        {
            using (P2PClientUiEnvironment.OverrideForTest(true))
            {
                if (!P2PClientUiEnvironment.CanTouchClientUi())
                    return Fail("OverrideForTest(true) should make CanTouchClientUi=true", "returned false");
            }
            using (P2PClientUiEnvironment.OverrideForTest(false))
            {
                if (P2PClientUiEnvironment.CanTouchClientUi())
                    return Fail("OverrideForTest(false) should make CanTouchClientUi=false", "returned true");
            }
            using (P2PClientUiEnvironment.OverrideForTest(true))
            {
                using (P2PClientUiEnvironment.OverrideForTest(false))
                {
                    if (P2PClientUiEnvironment.CanTouchClientUi())
                        return Fail("nested OverrideForTest(false) should take precedence", "returned true");
                }
                if (!P2PClientUiEnvironment.CanTouchClientUi())
                    return Fail("outer OverrideForTest(true) should be restored after inner Dispose", "returned false");
            }
            return true;
        }

        // ===== 辅助 =====
        private static bool Fail(string msg, string detail)
        {
            Console.WriteLine("    FAIL: " + msg + (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")"));
            return false;
        }
    }
}
