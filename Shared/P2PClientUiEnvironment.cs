using System;
using System.Reflection;
using SDG.Unturned;

namespace SteamP2PFriends.Shared
{
    // =====================================================================
    // 不得直接读取 Dedicator.IsDedicatedServer；使用反射探测，失败一律 fail-closed。
    // 单人 / LAN / U3DS / 普通客机环境不创建本模块 UI。
    // =====================================================================

    internal static class P2PClientUiEnvironment
    {
        private static bool? _testOverride;

        /// <summary>
        /// 蓝图 v3 §3.1：判断当前环境是否可触碰客户端 UI。
        /// fail-closed：探测失败/异常一律返回 false（UI denied）。
        /// </summary>
        internal static bool CanTouchClientUi()
        {
            if (_testOverride.HasValue) return _testOverride.Value;
            try
            {
                return !TryResolveDedicatedServerFailClosed();
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Shared]", "[P2P-UI] environment probe failed; UI denied: " + ex.GetType().Name);
                return false;
            }
        }

        /// <summary>
        /// 蓝图 v3 §2[指令 A] + §3.1：反射探测 Dedicator.IsDedicatedServer。
        /// 不得直接读取（编译期绑定可能触发静态 getter 副作用）。
        /// 返回 true 表示 dedicated 或未知（UI denied）；false 表示非 dedicated（UI allowed）。
        /// </summary>
        private static bool TryResolveDedicatedServerFailClosed()
        {
            try
            {
                PropertyInfo prop = typeof(Dedicator).GetProperty("IsDedicatedServer",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (prop == null)
                {
                    // 属性不存在 - fail-closed
                    return true;
                }
                object value = prop.GetValue(null, null);
                if (value is bool b)
                {
                    return b;
                }
                // 非 bool 值 - fail-closed
                return true;
            }
            catch
            {
                // 任何异常 - fail-closed
                return true;
            }
        }

        // ===== 测试 hook =====

        /// <summary>
        /// 测试用：覆盖 CanTouchClientUi 返回值。
        /// 用 using 包裹，Dispose 恢复原值。
        /// </summary>
        internal static IDisposable OverrideForTest(bool canTouch)
        {
            return new TestOverrideScope(canTouch);
        }

        private sealed class TestOverrideScope : IDisposable
        {
            private readonly bool? _prev;
            private bool _disposed;

            internal TestOverrideScope(bool canTouch)
            {
                _prev = _testOverride;
                _testOverride = canTouch;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _testOverride = _prev;
            }
        }
    }
}
