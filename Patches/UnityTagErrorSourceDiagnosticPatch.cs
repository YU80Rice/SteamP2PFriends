using HarmonyLib;
using SteamP2PFriends.Shared;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.19 D-Vis-14 诊断 patch（Unity Tag 错误源头追踪）。
    ///
    /// 目标：定位 "Tag: Undefined is not defined" 错误的源头。
    ///
    /// 客机日志证据（第十二次-场景X）：
    ///   [UnityBridge] FIRST Error sid=n/a t=111.039s msg=Tag: Undefined is not defined. (L320)
    ///
    /// 既有基础设施：
    ///   - UnityLogBridgePatch 已订阅 Application.logMessageReceivedThreaded
    ///   - DiagnosticContext.ThrottledLogBridge 捕获首条 + 聚合重复
    ///   - 但既有实现未对 Tag 错误捕获完整调用栈
    ///
    /// D-Vis-14 方案：
    ///   方案 A（推荐）：增强 UnityLogBridgePatch，对 Tag 错误日志捕获完整调用栈
    ///   - 在 UnityLogBridgePatch.OnUnityLog 中检测 "Tag:" + "is not defined" 关键字
    ///   - 输出完整 stackTrace（不聚合）
    ///
    /// 诊断目标：
    ///   - 捕获 "Tag: Undefined is not defined" 错误的完整调用栈
    ///   - 定位是哪个 GameObject 的 Tag 设置触发
    ///   - 验证是否与 Player 初始化相关（Player prefab Tag 未定义？）
    ///
    /// 节流：无（错误日志频率低）
    ///
    /// 严格禁止：
    ///   - 修改 Unity 日志系统
    ///   - 修改 GameObject.tag 设置
    /// </summary>
    public static class UnityTagErrorSourceDiagnosticPatch
    {
        public static bool DVis14_Registered { get; private set; }
        public static bool DVis14_TagErrorCaptured { get; private set; }
        public static string LastTagErrorStack { get; private set; }

        public static bool AllRegistrationsSucceeded => DVis14_Registered;

        public static bool RegisterManual(Harmony harmony)
        {
            // D-Vis-14 不 patch 任何方法，仅注册回调
            // UnityLogBridgePatch 已订阅 logMessageReceivedThreaded，本 patch 通过注册 Tag 错误检测回调
            DVis14_Registered = RegisterTagErrorDetector();
            RoleLogger.Info("[Shared]",
                $"[D-Vis] UnityTagErrorSourceDiagnosticPatch 汇总: D-Vis-14={DVis14_Registered}");
            return AllRegistrationsSucceeded;
        }

        private static bool RegisterTagErrorDetector()
        {
            const string Label = "D-Vis-14 Unity Tag Error Detector";
            try
            {
                // 注册 Tag 错误检测器（与 UnityLogBridgePatch 并列订阅）
                UnityEngine.Application.logMessageReceivedThreaded += OnUnityLogForTagError;
                RoleLogger.Info("[Shared]", $"[D-Vis-14] OK {Label} 已登记 (logMessageReceivedThreaded 回调)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[D-Vis-14] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Plugin.Shutdown 时反订阅，避免重复注册。
        /// </summary>
        public static void Shutdown()
        {
            try
            {
                UnityEngine.Application.logMessageReceivedThreaded -= OnUnityLogForTagError;
            }
            catch
            {
                // 忽略反订阅异常
            }
        }

        // ====================== Hooks ======================

        private static void OnUnityLogForTagError(string condition, string stackTrace, UnityEngine.LogType type)
        {
            try
            {
                if (!ShouldLogDVis()) return;

                // 仅 Error/Exception/Assert 级别
                if (type != UnityEngine.LogType.Error && type != UnityEngine.LogType.Exception
                    && type != UnityEngine.LogType.Assert) return;

                // 检测 Tag 错误关键字
                if (string.IsNullOrEmpty(condition)) return;
                if (!condition.Contains("Tag:") || !condition.Contains("is not defined")) return;

                DVis14_TagErrorCaptured = true;
                LastTagErrorStack = stackTrace;

                // 输出完整调用栈（不聚合）
                RoleLogger.Error("[Shared]",
                    $"[D-Vis-14] Unity Tag 错误捕获 condition=\"{condition}\"");
                if (!string.IsNullOrEmpty(stackTrace))
                {
                    RoleLogger.Error("[Shared]", $"[D-Vis-14] 完整调用栈:\n{stackTrace}");
                }
                else
                {
                    RoleLogger.Warn("[Shared]", "[D-Vis-14] stackTrace 为空（Unity 可能未提供）");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Shared]", $"[D-Vis-14] Tag 错误检测异常（不阻断）: {ex.Message}");
            }
        }

        // ====================== Helpers ======================

        private static bool ShouldLogDVis()
        {
            try
            {
                return SteamP2PFriendsPlugin.VerboseLog != null
                    && SteamP2PFriendsPlugin.VerboseLog.Value
                    && SteamP2PFriendsPlugin.RouteDiagnostics != null
                    && SteamP2PFriendsPlugin.RouteDiagnostics.Value;
            }
            catch
            {
                return false;
            }
        }
    }
}
