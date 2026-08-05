using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Reflection;
using UnityEngine;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.19 D-Vis-11 诊断 patch（LoadingUI.Update - 加载阻塞追踪）。
    ///
    /// 审计强制修正 2：D-Vis-11 改为 Patch LoadingUI.Update。
    ///
    /// U3-SDK 源码（LoadingUI.cs）：
    ///   L115: private static int lastLoading;
    ///   L119: public static bool isBlocked => Time.frameCount <= lastLoading;（只读属性，无 setter）
    ///   L923-928: Update() 设置 lastLoading 的唯一位置
    ///     private void Update()
    ///     {
    ///         if (!Dedicator.IsDedicatedServer && (Assets.isLoading || Provider.isLoading
    ///             || Level.isLoading || Player.isLoading || Level.isExiting))
    ///         {
    ///             lastLoading = Time.frameCount + 1;
    ///         }
    ///         // ...
    ///     }
    ///
    /// 关键设计：isBlocked 是 derived property，无 setter。Patch LoadingUI.Update Postfix 记录
    ///          5 个 isLoading 标志位状态 + isBlocked 状态变化。
    ///
    /// 诊断目标：
    ///   - 捕获 isBlocked=true 的时机与触发标志位（5 个中哪个）
    ///   - 捕获 isBlocked=false 的时机与持续帧数
    ///   - 定位客机端 isBlocked 反复变化（22 次 True / 47 次 False）的根因
    ///   - 关键验证：是否 Player.isLoading 反复触发（与 D-Vis-12 isLoadingClothing 关联）
    ///
    /// 严格禁止：
    ///   - 修改原方法参数或返回值
    ///   - 修改 lastLoading 字段
    ///   - 修改 isBlocked 属性
    /// </summary>
    public static class LoadingUIUpdateDiagnosticPatch
    {
        public static bool DVis11_Registered { get; private set; }

        public static bool AllRegistrationsSucceeded => DVis11_Registered;

        // 状态变化追踪：仅在 isBlocked 状态变化时记录
        private static bool? _lastIsBlocked = null;
        private static int _lastBlockedFrameCount = -1;
        private static int _blockedStartFrame = -1;

        public static bool RegisterManual(Harmony harmony)
        {
            DVis11_Registered = RegisterDVis11(harmony);
            RoleLogger.Info("[Shared]",
                $"[D-Vis] LoadingUIUpdateDiagnosticPatch 汇总: D-Vis-11={DVis11_Registered}");
            return AllRegistrationsSucceeded;
        }

        private static bool RegisterDVis11(Harmony harmony)
        {
            const string Label = "D-Vis-11 LoadingUI.Update";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(LoadingUI), "Update");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[D-Vis-11] !!! {Label} 反射失败");
                    return false;
                }
                MethodInfo postfix = typeof(Hooks).GetMethod(nameof(Hooks.UpdatePostfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, postfix: new HarmonyMethod(postfix));
                RoleLogger.Info("[Shared]", $"[D-Vis-11] OK {Label} 已登记 (Postfix)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[D-Vis-11] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        // ====================== Hooks ======================

        private static class Hooks
        {
            // LoadingUI.Update Postfix
            // 记录 5 个 isLoading 标志位 + isBlocked 状态变化
            internal static void UpdatePostfix()
            {
                try
                {
                    if (!ShouldLogDVis()) return;

                    // 5 个 isLoading 标志位（LoadingUI.cs:925）
                    bool assetsLoading = Assets.isLoading;
                    bool providerLoading = Provider.isLoading;
                    bool levelLoading = Level.isLoading;
                    bool playerLoading = Player.isLoading;
                    bool levelExiting = Level.isExiting;

                    // isBlocked 是 derived property
                    bool currentBlocked = LoadingUI.isBlocked;
                    int frameCount = Time.frameCount;

                    // 状态变化检测
                    bool stateChanged = !_lastIsBlocked.HasValue || _lastIsBlocked.Value != currentBlocked;

                    if (!stateChanged)
                    {
                        // 即使状态未变，若当前是 blocked 状态，每 30 帧输出一次心跳
                        if (currentBlocked && frameCount - _lastBlockedFrameCount >= 30)
                        {
                            LogBlockedHeartbeat(currentBlocked, frameCount, assetsLoading, providerLoading,
                                levelLoading, playerLoading, levelExiting);
                            _lastBlockedFrameCount = frameCount;
                        }
                        return;
                    }

                    // 状态变化：记录完整 5 标志位
                    _lastIsBlocked = currentBlocked;
                    _lastBlockedFrameCount = frameCount;

                    if (currentBlocked)
                    {
                        // 切换为 blocked
                        _blockedStartFrame = frameCount;
                        string triggerFlags = BuildTriggerFlags(assetsLoading, providerLoading,
                            levelLoading, playerLoading, levelExiting);

                        RoleLogger.Info("[Shared]",
                            $"[D-Vis-11] LoadingUI.Update Postfix isBlocked=False->True frameCount={frameCount} " +
                            $"triggers=[{triggerFlags}] " +
                            $"Player.isLoadingClothing={Player.isLoadingClothing} " +
                            $"(5 flags: Assets={assetsLoading} Provider={providerLoading} " +
                            $"Level={levelLoading} Player={playerLoading} Exiting={levelExiting})");
                    }
                    else
                    {
                        // 切换为 unblocked
                        int blockedDuration = _blockedStartFrame >= 0 ? frameCount - _blockedStartFrame : -1;
                        RoleLogger.Info("[Shared]",
                            $"[D-Vis-11] LoadingUI.Update Postfix isBlocked=True->False frameCount={frameCount} " +
                            $"blockedFrames={blockedDuration} " +
                            $"(all 5 flags false now: Assets={assetsLoading} Provider={providerLoading} " +
                            $"Level={levelLoading} Player={playerLoading} Exiting={levelExiting})");
                        _blockedStartFrame = -1;
                    }
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Shared]", $"[D-Vis-11] Update Postfix 异常（不阻断）: {ex.Message}");
                }
            }
        }

        // ====================== Helpers ======================

        private static string BuildTriggerFlags(bool assets, bool provider, bool level, bool player, bool exiting)
        {
            var sb = new System.Text.StringBuilder();
            if (assets) sb.Append("Assets,");
            if (provider) sb.Append("Provider,");
            if (level) sb.Append("Level,");
            if (player) sb.Append("Player,");
            if (exiting) sb.Append("Exiting,");
            if (sb.Length == 0) sb.Append("(none)");
            return sb.ToString().TrimEnd(',');
        }

        private static void LogBlockedHeartbeat(bool blocked, int frameCount,
            bool assets, bool provider, bool level, bool player, bool exiting)
        {
            string triggerFlags = BuildTriggerFlags(assets, provider, level, player, exiting);
            RoleLogger.Info("[Shared]",
                $"[D-Vis-11] LoadingUI.Update heartbeat isBlocked={blocked} frameCount={frameCount} " +
                $"triggers=[{triggerFlags}] Player.isLoadingClothing={Player.isLoadingClothing}");
        }

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
