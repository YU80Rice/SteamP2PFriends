using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System;
using System.Diagnostics;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.19 D-Vis-13 诊断 patch（Player.InitializePlayer 阶段标记 - 初始化耗时追踪）。
    ///
    /// 目标：追踪 Player.InitializePlayer 各阶段耗时，定位"卡在哪一步"。
    ///
    /// U3-SDK 源码：
    ///   - 文件：Player.cs
    ///   - 方法：internal void InitializePlayer(...)
    ///   - 关键阶段（基于 L1570-1572 上下文）：
    ///     阶段 1：channel.IsLocalPlayer 分支入口
    ///     阶段 2：设置 isLoadingInventory/Life/Clothing = true（L1570-1572）
    ///     阶段 3：look.onPerspectiveUpdated += onPerspectiveUpdated（L1574）
    ///     阶段 4：8 组件 InitializePlayer 调用
    ///     阶段 5：LocalComponentsInitialized 信号
    ///
    /// 诊断目标：
    ///   - 测量 InitializePlayer 总耗时
    ///   - 对比房主自连场景与双机场景的耗时差异
    ///   - 若 InitializePlayer 未完成 -> 定位是哪个阶段卡住
    ///
    /// 严格禁止：
    ///   - 修改原方法参数或返回值
    ///   - 在 InitializePlayer 内部插桩（避免影响初始化时序）
    /// </summary>
    public static class PlayerInitializePlayerStageDiagnosticPatch
    {
        public static bool DVis13_Registered { get; private set; }

        public static bool AllRegistrationsSucceeded => DVis13_Registered;

        public static bool RegisterManual(Harmony harmony)
        {
            DVis13_Registered = RegisterDVis13(harmony);
            RoleLogger.Info("[Shared]",
                $"[D-Vis] PlayerInitializePlayerStageDiagnosticPatch 汇总: D-Vis-13={DVis13_Registered}");
            return AllRegistrationsSucceeded;
        }

        private static bool RegisterDVis13(Harmony harmony)
        {
            const string Label = "D-Vis-13 Player.InitializePlayer";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(Player), "InitializePlayer");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[D-Vis-13] !!! {Label} 反射失败");
                    return false;
                }
                MethodInfo prefix = typeof(Hooks).GetMethod(nameof(Hooks.InitializePlayerPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo postfix = typeof(Hooks).GetMethod(nameof(Hooks.InitializePlayerPostfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(postfix));
                RoleLogger.Info("[Shared]", $"[D-Vis-13] OK {Label} 已登记 (Prefix+Postfix)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[D-Vis-13] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        // ====================== Hooks ======================

        private static class Hooks
        {
            // 用 Stopwatch 测量耗时（比 Time.realtimeSinceStartup 精度高）
            [ThreadStatic]
            private static Stopwatch _stopwatch;

            [ThreadStatic]
            private static ulong _pendingSteamId;

            [ThreadStatic]
            private static bool _pendingIsLocalPlayer;

            internal static void InitializePlayerPrefix(Player __instance)
            {
                try
                {
                    if (!ShouldLogDVis()) return;

                    SteamPlayer sp = __instance?.channel?.owner;
                    ulong steamId = sp?.playerID?.steamID.m_SteamID ?? 0UL;
                    bool isLocalPlayer = __instance?.channel?.IsLocalPlayer ?? false;

                    _pendingSteamId = steamId;
                    _pendingIsLocalPlayer = isLocalPlayer;
                    _stopwatch = Stopwatch.StartNew();

                    RoleLogger.Info("[Shared]",
                        $"[D-Vis-13] InitializePlayer Prefix steamId={DiagnosticMaskUtil.MaskSteamId(steamId)} " +
                        $"isLocalPlayer={isLocalPlayer} timestamp={_stopwatch.Elapsed.TotalMilliseconds:F3}ms");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Shared]", $"[D-Vis-13] Prefix 异常（不阻断）: {ex.Message}");
                }
            }

            internal static void InitializePlayerPostfix(Player __instance)
            {
                try
                {
                    if (!ShouldLogDVis()) return;

                    Stopwatch sw = _stopwatch;
                    if (sw == null) return;
                    sw.Stop();
                    double elapsedMs = sw.Elapsed.TotalMilliseconds;

                    ulong steamId = _pendingSteamId;
                    bool isLocalPlayer = _pendingIsLocalPlayer;

                    // 反射读取关键字段状态
                    bool isLoadingClothing = Player.isLoadingClothing;
                    bool isLocalComponentsInitialized = false;
                    try
                    {
                        // Player.channel.IsLocalPlayer 对应本地 Player 初始化完成
                        isLocalComponentsInitialized = __instance?.channel?.IsLocalPlayer ?? false;
                    }
                    catch { /* ignore */ }

                    RoleLogger.Info("[Shared]",
                        $"[D-Vis-13] InitializePlayer Postfix steamId={DiagnosticMaskUtil.MaskSteamId(steamId)} " +
                        $"isLocalPlayer={isLocalPlayer} elapsed={elapsedMs:F3}ms " +
                        $"isLoadingClothing(static)={isLoadingClothing} " +
                        $"(预期: isLocalPlayer=true 时 isLoadingClothing=true)");

                    // 重置 ThreadStatic 状态
                    _stopwatch = null;
                    _pendingSteamId = 0UL;
                    _pendingIsLocalPlayer = false;
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Shared]", $"[D-Vis-13] Postfix 异常（不阻断）: {ex.Message}");
                }
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
