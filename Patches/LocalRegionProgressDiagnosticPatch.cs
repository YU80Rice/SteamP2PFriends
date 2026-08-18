using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Client;
using SteamP2PFriends.Shared;
using System.Reflection;
using UnityEngine;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    ///
    /// 设计目标：
    ///   - 记录本地 PlayerInput.FixedUpdate 与 PlayerMovement.simulate 是否持续运行。
    ///   - 低频输出：movement.region_x / region_y / transform.position / Level.isLoadingArea。
    ///   - 若 Player 三项都已 false 而 Level.isLoadingArea 独自保持 true，
    ///     此诊断将直接定位区域更新链断点。
    ///
    /// 覆盖方法：
    ///   - PlayerMovement.simulate (3 个重载) - Prefix 计数 + 低频日志
    ///   - PlayerInput.FixedUpdate - Prefix 计数 + 低频日志
    ///   - PlayerMovement.onRegionUpdated (事件订阅，非 patch) - 通过 simulate Postfix 检查 region 变化
    /// </summary>
    public static class LocalRegionProgressDiagnosticPatch
    {
        private static int _fixedUpdateCount;
        private static int _simulateCount;
        private static float _lastReportTime;
        private const float ReportIntervalSeconds = 2f;

        private static byte _lastRegionX = 255;
        private static byte _lastRegionY = 255;

        //   - _areaPendingStartTime：首次进入"Area pending"状态的时间（0f 表示未进入）
        //   - _areaPendingUpgraded：是否已升级为 breakpoint warning
        //   - AreaPendingGraceSeconds：带宽限期（15s），超过才升级 warning
        //   - 升级条件：Area pending >= 15s + LoadingUI.isBlocked=true + Level.isLoadingArea=true 共同成立
        //   - 房主自连基线测试中 region=(255,255) 在 ~10s 后自行推进，证明短暂 Area pending 是正常加载阶段
        private static float _areaPendingStartTime;
        private static bool _areaPendingUpgraded;
        private const float AreaPendingGraceSeconds = 15f;

        public static void RegisterManual(Harmony harmony)
        {
            // PlayerInput.FixedUpdate (private)
            MethodInfo fixedUpdate = AccessTools.Method(typeof(PlayerInput), "FixedUpdate");
            if (fixedUpdate == null)
            {
                RoleLogger.Error("[Shared]", "[P0-D] PlayerInput.FixedUpdate 反射失败");
            }
            else
            {
                MethodInfo prefix = typeof(LocalRegionProgressDiagnosticPatch).GetMethod(
                    nameof(FixedUpdatePrefix), BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(fixedUpdate, prefix: new HarmonyMethod(prefix));
                RoleLogger.Info("[Shared]", "[P0-D] PlayerInput.FixedUpdate 已登记 (Prefix only-read)");
            }

            // PlayerMovement.simulate 3 个重载
            // 重载 1: simulate() - 0 args
            MethodInfo sim1 = AccessTools.Method(typeof(PlayerMovement), "simulate", new System.Type[0]);
            if (sim1 != null)
            {
                MethodInfo prefix = typeof(LocalRegionProgressDiagnosticPatch).GetMethod(
                    nameof(SimulatePrefix), BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(sim1, prefix: new HarmonyMethod(prefix));
                RoleLogger.Info("[Shared]", "[P0-D] PlayerMovement.simulate() (0 args) 已登记");
            }
            else
            {
                RoleLogger.Warn("[Shared]", "[P0-D] PlayerMovement.simulate() (0 args) 反射失败（可能仅 dedicated server 用）");
            }

            // 重载 2: simulate(uint, int, bool, bool, Vector3, Quaternion, float, float, float, float, float) - 11 args (driving)
            MethodInfo sim2 = AccessTools.Method(typeof(PlayerMovement), "simulate",
                new System.Type[] {
                    typeof(uint), typeof(int), typeof(bool), typeof(bool),
                    typeof(Vector3), typeof(Quaternion),
                    typeof(float), typeof(float), typeof(float), typeof(float), typeof(float)
                });
            if (sim2 != null)
            {
                MethodInfo prefix = typeof(LocalRegionProgressDiagnosticPatch).GetMethod(
                    nameof(SimulatePrefix), BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(sim2, prefix: new HarmonyMethod(prefix));
                RoleLogger.Info("[Shared]", "[P0-D] PlayerMovement.simulate(11 args, driving) 已登记");
            }

            // 重载 3: simulate(uint, int, int, int, float, float, bool, bool, float) - 9 args (walking)
            MethodInfo sim3 = AccessTools.Method(typeof(PlayerMovement), "simulate",
                new System.Type[] {
                    typeof(uint), typeof(int), typeof(int), typeof(int),
                    typeof(float), typeof(float),
                    typeof(bool), typeof(bool), typeof(float)
                });
            if (sim3 != null)
            {
                MethodInfo prefix = typeof(LocalRegionProgressDiagnosticPatch).GetMethod(
                    nameof(SimulatePrefix), BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(sim3, prefix: new HarmonyMethod(prefix));
                RoleLogger.Info("[Shared]", "[P0-D] PlayerMovement.simulate(9 args, walking) 已登记");
            }
            else
            {
                RoleLogger.Warn("[Shared]", "[P0-D] PlayerMovement.simulate(9 args, walking) 反射失败");
            }
        }

        private static void FixedUpdatePrefix(PlayerInput __instance)
        {
            _fixedUpdateCount++;

            // 仅本地 Player 的 FixedUpdate 才记录
            Player player = null;
            try
            {
                player = __instance?.player;
            }
            catch
            {
                return;
            }
            if (ReferenceEquals(player, null)) return;
            if (!ReferenceEquals(player, Player.LocalPlayer)) return;

            TryReport(__instance.player, "PlayerInput.FixedUpdate");
        }

        private static void SimulatePrefix(PlayerMovement __instance)
        {
            _simulateCount++;

            Player player = null;
            try
            {
                player = __instance?.player;
            }
            catch
            {
                return;
            }
            if (ReferenceEquals(player, null)) return;
            if (!ReferenceEquals(player, Player.LocalPlayer)) return;

            TryReport(__instance.player, "PlayerMovement.simulate");
        }

        private static void TryReport(Player localPlayer, string trigger)
        {
            float now = Time.realtimeSinceStartup;
            if (now - _lastReportTime < ReportIntervalSeconds) return;
            _lastReportTime = now;

            try
            {
                byte regionX = 255, regionY = 255;
                Vector3 pos = Vector3.zero;
                try
                {
                    if (localPlayer.movement != null)
                    {
                        regionX = localPlayer.movement.region_x;
                        regionY = localPlayer.movement.region_y;
                    }
                    if (localPlayer.transform != null)
                    {
                        pos = localPlayer.transform.position;
                    }
                }
                catch { /* ignore */ }

                bool regionChanged = (regionX != _lastRegionX || regionY != _lastRegionY);
                if (regionChanged)
                {
                    _lastRegionX = regionX;
                    _lastRegionY = regionY;
                }

                RoleLogger.Info("[Client]",
                    $"[P0-D] RegionProgress trigger={trigger} t={now:F2}s frame={Time.frameCount} " +
                    $"fixedUpdateCount={_fixedUpdateCount} simulateCount={_simulateCount} " +
                    $"region=({regionX},{regionY}) pos=({pos.x:F2},{pos.y:F2},{pos.z:F2}) " +
                    $"regionChanged={regionChanged} " +
                    $"Level.isLoadingArea={Level.isLoadingArea} " +
                    $"Player.isLoadingInventory={Player.isLoadingInventory} " +
                    $"Player.isLoadingLife={Player.isLoadingLife} " +
                    $"Player.isLoadingClothing={Player.isLoadingClothing} " +
                    $"Level.isLoadingLighting={Level.isLoadingLighting} " +
                    $"Level.isLoadingVehicles={Level.isLoadingVehicles} " +
                    $"Level.isLoadingBarricades={Level.isLoadingBarricades} " +
                    $"Level.isLoadingStructures={Level.isLoadingStructures}");

                // 若 Player 三项都已 false 而 Level.isLoadingArea 独自保持 true，
                //   - 短暂 Area pending 只记 Info，不立即升级为 breakpoint warning。
                //   - 升级条件：Area pending 持续 >= 15s + LoadingUI.isBlocked=true + Level.isLoadingArea=true
                //     共同成立（审计要求三条件 AND）。
                //   - 房主自连基线测试中 region=(255,255) 在 ~10s 后自行推进，证明短暂 Area pending 是正常加载阶段。
                if (!Player.isLoadingInventory && !Player.isLoadingLife && !Player.isLoadingClothing &&
                    !Level.isLoadingLighting && !Level.isLoadingVehicles &&
                    !Level.isLoadingBarricades && !Level.isLoadingStructures &&
                    Level.isLoadingArea)
                {
                    // 进入带宽限期：记录首次进入时间，输出 Info（非 warning）
                    if (_areaPendingStartTime == 0f)
                    {
                        _areaPendingStartTime = now;
                        RoleLogger.Info("[Client]",
                            $"[P0-D] Area pending: Player 三项=已清零 + Level 三项=已清零，" +
                            $"但 Level.isLoadingArea=True。region=({regionX},{regionY})。 " +
                            $"进入带宽限期（15s 内自行消退视为正常加载阶段）。");
                    }
                    else
                    {
                        float pendingElapsed = now - _areaPendingStartTime;
                        bool loadingUiBlocked = false;
                        try { loadingUiBlocked = LoadingUI.isBlocked; } catch { }

                        if (pendingElapsed >= AreaPendingGraceSeconds
                            && loadingUiBlocked
                            && !_areaPendingUpgraded)
                        {
                            _areaPendingUpgraded = true;
                            RoleLogger.Warn("[Client]",
                                $"[P0-D] !!! 区域更新链断点确认（带宽限期 {AreaPendingGraceSeconds:F0}s 已超 + LoadingUI.isBlocked=true）!!! " +
                                $"Player 三项=已清零 + Level 三项=已清零，但 Level.isLoadingArea 仍为 True。 " +
                                $"region=({regionX},{regionY}) pendingElapsed={pendingElapsed:F1}s loadingUiBlocked={loadingUiBlocked}。 " +
                                $"下一步：检查 PlayerMovement.updateRegionAndBound 是否被调用，" +
                                $"LevelObjects.onRegionUpdated 是否被订阅/触发。");
                        }
                        else if (!_areaPendingUpgraded)
                        {
                            RoleLogger.Info("[Client]",
                                $"[P0-D] Area pending 持续中 pendingElapsed={pendingElapsed:F1}s/{AreaPendingGraceSeconds:F0}s " +
                                $"region=({regionX},{regionY}) loadingUiBlocked={loadingUiBlocked}");
                        }
                    }
                }
                else
                {
                    // isLoadingArea 已清零或 Player/Level 三项仍有 true：重置带宽限期状态
                    if (_areaPendingStartTime != 0f)
                    {
                        float totalPending = now - _areaPendingStartTime;
                        RoleLogger.Info("[Client]",
                            $"[P0-D] Area pending 已消退 totalPending={totalPending:F1}s " +
                            $"(曾升级为 breakpoint warning: {_areaPendingUpgraded})");
                        _areaPendingStartTime = 0f;
                        _areaPendingUpgraded = false;
                    }
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Client]", $"[P0-D] TryReport 异常（不阻断）: {ex.Message}");
            }
        }
    }
}
