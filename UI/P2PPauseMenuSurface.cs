using SDG.Unturned;
using SteamP2PFriends.Shared;
using System;
using System.Reflection;
using UnityEngine;

namespace SteamP2PFriends.UI
{
    // =====================================================================
    //   只允许反射 typeof(PlayerPauseUI) 的私有静态字段 "container"。
    //   字段缺失/GetValue 异常/空值/非 ISleekElement/PlayerPauseUI.active=false
    //   都返回 false。失败意味着"不显示"，绝不是"改挂 PlayerUI.container"。
    //   禁止字段名模糊扫描、遍历所有 UI 单例或 fallback 回 PlayerUI.container。
    // =====================================================================
    internal static class P2PPauseMenuSurface
    {
        private static FieldInfo _containerField;
        private static bool _fieldResolved;
        private static float _nextFailureLogAt;

        // 测试 hook
        internal static bool _testBypassThreadAssert;
        internal static bool? _testActiveOverride;                    // 覆盖 PlayerPauseUI.active
        internal static Func<ISleekElement> _testContainerProvider;   // 覆盖反射（返回 parent 或 null）

        /// <summary>
        /// 蓝图 §3.1：仅在 PlayerPauseUI.active 且反射到 container 为 ISleekElement 时返回 true。
        /// 主线程 + CanTouchClientUi 双前置。失败一律 false（UI 不创建，审批内核继续运行）。
        /// </summary>
        internal static bool TryGetActivePauseContainer(out ISleekElement parent)
        {
            if (!_testBypassThreadAssert)
            {
                ThreadUtil.assertIsGameThread();
            }
            parent = null;

            if (!P2PClientUiEnvironment.CanTouchClientUi()) return false;

            bool active = _testActiveOverride ?? PlayerPauseUI.active;
            if (!active) return false;

            // 测试路径：直接注入 parent（绕过反射，便于单测）
            if (_testContainerProvider != null)
            {
                parent = _testContainerProvider();
                return parent != null;
            }

            // 生产路径：精确反射私有静态字段 container（不模糊扫描、不遍历）
            try
            {
                if (!_fieldResolved)
                {
                    _fieldResolved = true;
                    _containerField = typeof(PlayerPauseUI).GetField(
                        "container",
                        BindingFlags.NonPublic | BindingFlags.Static);
                }
                parent = _containerField == null
                    ? null
                    : _containerField.GetValue(null) as ISleekElement;
                if (parent != null) return true;
            }
            catch (Exception ex)
            {
                LogUnavailableThrottled(ex.GetType().Name);
                return false;
            }

            LogUnavailableThrottled("container unavailable");
            return false;
        }

        private static void LogUnavailableThrottled(string reason)
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextFailureLogAt) return;
            _nextFailureLogAt = now + 10f;
            RoleLogger.Warn("[Host]", "[P2P-PauseUI] pause container unavailable; UI denied: " + reason);
        }
    }
}
