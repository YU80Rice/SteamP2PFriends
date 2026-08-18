using HarmonyLib;
using SteamP2PFriends.Shared;
using UnityEngine;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    ///
    /// v2 审计要求：
    ///   - D-11 应列为阻断项。第四次测试前在客机端 Plugin.Awake 注册
    ///     Application.logMessageReceivedThreaded 钩子。
    ///   - 把 Unity 引擎层 NRE 转发到 BepInEx 日志，并标记 [Client-UnityBridge] 前缀。
    ///
    ///   1. 从 Application.logMessageReceived 升级为 Application.logMessageReceivedThreaded
    ///      （线程安全版本，可在任何线程触发，包括 Unity 主线程之外的 NRE）。
    ///   2. 提供静态 IsSubscribed 属性供 Plugin.VerifyCriticalPatches 验证。
    ///      订阅失败时 DiagnosticBuildValid=false（阻断）。
    ///   3. 限流逻辑由 DiagnosticContext.ThrottledLogBridge 实现（已线程安全）。
    ///   4. 前缀标签 [Client-UnityBridge] 由 RoleLogger 自动路由（[Diag] 也保留兼容）。
    /// </summary>
    public static class UnityLogBridgePatch
    {
        private static bool _subscribed;
        private static bool _subscribeFailed;

        /// <summary>
        /// 由 Plugin.Awake 调用，订阅 Application.logMessageReceivedThreaded。
        /// 幂等：重复调用安全。
        /// </summary>
        public static void Initialize()
        {
            if (_subscribed) return;
            try
            {
                Application.logMessageReceivedThreaded += OnUnityLog;
                _subscribed = true;
                RoleLogger.Info("[Shared]",
                    "[D-11] Unity logMessageReceivedThreaded bridge subscribed (thread-safe, blocking)");
            }
            catch (System.Exception ex)
            {
                _subscribeFailed = true;
                RoleLogger.Error("[Shared]",
                    $"[D-11] !!! logMessageReceivedThreaded 订阅失败: {ex}");
            }
        }

        public static void Shutdown()
        {
            if (!_subscribed) return;
            try
            {
                Application.logMessageReceivedThreaded -= OnUnityLog;
            }
            catch
            {
                // 忽略反订阅异常
            }
            _subscribed = false;
        }

        /// <summary>
        /// 订阅失败时返回 false，触发 DIAGNOSTIC BUILD INVALID。
        /// </summary>
        public static bool IsSubscribed => _subscribed;

        public static bool IsFailed => _subscribeFailed;

        private static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            // 仅桥接 Error/Exception/Assert，Warning/Log 不桥接（避免噪音）
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            DiagnosticContext.ThrottledLogBridge.HandleLog(condition, stackTrace, type);
        }
    }
}
