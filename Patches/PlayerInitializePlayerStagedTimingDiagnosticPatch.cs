using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.20 D-Vis-18 诊断 patch（InitializePlayer 分阶段计时）。
    ///
    /// 审计授权：外部审计报告-第十三次双机测试-v0.2.3.19-修订版验收 §9.2 授权实施。
    ///
    /// 审计要求（§9.1 修正 4 + §5.4）：
    ///   - 在 D-Vis-13 基础上扩展，用 Stopwatch 分段计时
    ///   - 分阶段：prefab 加载 / 组件注册 / Material 加载 / SteamChannel 注册 / 其他
    ///   - 定位客机 InitializePlayer 1718ms 中哪个子阶段耗时最长
    ///
    /// U3-SDK 源码核验（Player.cs:1614-1648）：
    ///   vanilla Player.InitializePlayer 调用顺序（14 个组件）：
    ///     1. InitializePlayerStart()
    ///     2. clothing.InitializePlayer()
    ///     3. inventory.InitializePlayer()
    ///     4. life.InitializePlayer()
    ///     5. skills.InitializePlayer()
    ///     6. crafting.InitializePlayer()
    ///     7. stance.InitializePlayer()
    ///     8. movement.InitializePlayer()
    ///     9. look.InitializePlayer()
    ///    10. interact.InitializePlayer()
    ///    11. animator.InitializePlayer()
    ///    12. equipment.InitializePlayer()
    ///    13. input.InitializePlayer()
    ///    14. voice.InitializePlayer()
    ///    15. workzone.InitializePlayer()（条件）
    ///    16. quests.InitializePlayer()（最后，触发 onPlayerCreated）
    ///    17. playerUI.InitializePlayer()（条件）
    ///
    /// 本 patch 采用"关键采样点"策略，只 Patch 4 个关键组件的 Postfix，记录时间戳：
    ///   - clothing（首个组件，阶段 1 结束）
    ///   - movement（中间组件，位置同步相关，阶段 2 结束）
    ///   - animator（材质/SMR 相关，阶段 3 结束）
    ///   - quests（末尾组件，阶段 4 结束，触发 onPlayerCreated）
    ///
    /// 阶段划分：
    ///   阶段 A：InitializePlayer Prefix -> clothing Postfix（含 InitializePlayerStart + clothing）
    ///   阶段 B：clothing Postfix -> movement Postfix（inventory + life + skills + crafting + stance + movement）
    ///   阶段 C：movement Postfix -> animator Postfix（look + interact + animator）
    ///   阶段 D：animator Postfix -> quests Postfix（equipment + input + voice + workzone + quests）
    ///   阶段 E：quests Postfix -> InitializePlayer Postfix（playerUI + 收尾）
    ///
    /// 与 D-Vis-13 的关系：
    ///   - D-Vis-13 记录 InitializePlayer 总耗时
    ///   - D-Vis-18 记录各阶段耗时
    ///   - 两者共存，D-Vis-18 Prefix 启动 Stopwatch，D-Vis-13 Postfix 仍正常记录总耗时
    ///
    /// 严格禁止：
    ///   - 修改原方法参数或返回值
    ///   - 在 InitializePlayer 内部插桩影响时序
    ///   - Stopwatch 异常影响原方法
    /// </summary>
    public static class PlayerInitializePlayerStagedTimingDiagnosticPatch
    {
        public static bool DVis18_Registered { get; private set; }

        public static bool AllRegistrationsSucceeded => DVis18_Registered;

        public static bool RegisterManual(Harmony harmony)
        {
            DVis18_Registered = RegisterDVis18(harmony);
            RoleLogger.Info("[Shared]",
                $"[D-Vis] PlayerInitializePlayerStagedTimingDiagnosticPatch 汇总: D-Vis-18={DVis18_Registered}");
            return AllRegistrationsSucceeded;
        }

        private static bool RegisterDVis18(Harmony harmony)
        {
            const string Label = "D-Vis-18 InitializePlayer Staged Timing";
            try
            {
                bool okA = RegisterOne(harmony, typeof(Player), "InitializePlayer",
                    nameof(Hooks.InitializePlayerPrefix), null);
                bool okB = RegisterOne(harmony, typeof(PlayerClothing), "InitializePlayer",
                    null, nameof(Hooks.ClothingPostfix));
                bool okC = RegisterOne(harmony, typeof(PlayerMovement), "InitializePlayer",
                    null, nameof(Hooks.MovementPostfix));
                bool okD = RegisterOne(harmony, typeof(PlayerAnimator), "InitializePlayer",
                    null, nameof(Hooks.AnimatorPostfix));
                bool okE = RegisterOne(harmony, typeof(PlayerQuests), "InitializePlayer",
                    null, nameof(Hooks.QuestsPostfix));
                bool okF = RegisterOne(harmony, typeof(Player), "InitializePlayer",
                    null, nameof(Hooks.InitializePlayerPostfix));

                bool allOk = okA && okB && okC && okD && okE && okF;
                RoleLogger.Info("[Shared]",
                    $"[D-Vis-18] {(allOk ? "OK" : "!!!")} {Label} 登记结果: " +
                    $"PlayerPrefix={okA} ClothingPost={okB} MovementPost={okC} " +
                    $"AnimatorPost={okD} QuestsPost={okE} PlayerPost={okF}");
                return allOk;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[D-Vis-18] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        private static bool RegisterOne(Harmony harmony, System.Type targetType, string methodName,
            string prefixName, string postfixName)
        {
            try
            {
                MethodInfo original = AccessTools.Method(targetType, "InitializePlayer");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[D-Vis-18] !!! {targetType.Name}.{methodName} 反射失败");
                    return false;
                }

                HarmonyMethod prefix = null, postfix = null;
                if (!string.IsNullOrEmpty(prefixName))
                {
                    MethodInfo p = typeof(Hooks).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic);
                    if (p != null) prefix = new HarmonyMethod(p);
                }
                if (!string.IsNullOrEmpty(postfixName))
                {
                    MethodInfo p = typeof(Hooks).GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic);
                    if (p != null) postfix = new HarmonyMethod(p);
                }

                harmony.Patch(original, prefix: prefix, postfix: postfix);
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[D-Vis-18] !!! {targetType.Name}.{methodName} 登记异常: {ex.Message}");
                return false;
            }
        }

        // ====================== Hooks ======================

        private static class Hooks
        {
            // ThreadStatic Stopwatch：与 D-Vis-13 独立的 Stopwatch（避免干扰 D-Vis-13 计时）
            [ThreadStatic]
            private static Stopwatch _stagedSw;

            [ThreadStatic]
            private static ulong _stagedSteamId;

            [ThreadStatic]
            private static bool _stagedIsLocalPlayer;

            // 各阶段时间戳（ms）
            [ThreadStatic]
            private static double _t1_clothing;   // clothing Postfix
            [ThreadStatic]
            private static double _t2_movement;   // movement Postfix
            [ThreadStatic]
            private static double _t3_animator;   // animator Postfix
            [ThreadStatic]
            private static double _t4_quests;     // quests Postfix
            [ThreadStatic]
            private static double _t5_end;        // InitializePlayer Postfix

            internal static void InitializePlayerPrefix(Player __instance)
            {
                try
                {
                    if (!ShouldLogDVis()) return;

                    _stagedSw = Stopwatch.StartNew();
                    _stagedSteamId = __instance?.channel?.owner?.playerID?.steamID.m_SteamID ?? 0UL;
                    _stagedIsLocalPlayer = __instance?.channel?.IsLocalPlayer ?? false;
                    _t1_clothing = _t2_movement = _t3_animator = _t4_quests = _t5_end = -1.0;
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Shared]", $"[D-Vis-18] Prefix 异常（不阻断）: {ex.Message}");
                }
            }

            internal static void ClothingPostfix(PlayerClothing __instance)
            {
                RecordStage(nameof(_t1_clothing), ref _t1_clothing);
            }

            internal static void MovementPostfix(PlayerMovement __instance)
            {
                RecordStage(nameof(_t2_movement), ref _t2_movement);
            }

            internal static void AnimatorPostfix(PlayerAnimator __instance)
            {
                RecordStage(nameof(_t3_animator), ref _t3_animator);
            }

            internal static void QuestsPostfix(PlayerQuests __instance)
            {
                RecordStage(nameof(_t4_quests), ref _t4_quests);
            }

            internal static void InitializePlayerPostfix(Player __instance)
            {
                try
                {
                    if (!ShouldLogDVis()) return;

                    Stopwatch sw = _stagedSw;
                    if (sw == null) return;
                    sw.Stop();
                    _t5_end = sw.Elapsed.TotalMilliseconds;

                    ulong steamId = _stagedSteamId;
                    bool isLocalPlayer = _stagedIsLocalPlayer;

                    // 计算各阶段耗时（若某阶段未触发则标记 -1）
                    double stageA = _t1_clothing >= 0 ? _t1_clothing : -1.0;
                    double stageB = (_t1_clothing >= 0 && _t2_movement >= 0) ? _t2_movement - _t1_clothing : -1.0;
                    double stageC = (_t2_movement >= 0 && _t3_animator >= 0) ? _t3_animator - _t2_movement : -1.0;
                    double stageD = (_t3_animator >= 0 && _t4_quests >= 0) ? _t4_quests - _t3_animator : -1.0;
                    double stageE = (_t4_quests >= 0 && _t5_end >= 0) ? _t5_end - _t4_quests : -1.0;

                    RoleLogger.Info("[Shared]",
                        $"[D-Vis-18] InitializePlayer STAGED steamId={DiagnosticMaskUtil.MaskSteamId(steamId)} " +
                        $"isLocalPlayer={isLocalPlayer} total={_t5_end:F3}ms | " +
                        $"A(clothing)={stageA:F3} B(movement)={stageB:F3} " +
                        $"C(animator)={stageC:F3} D(quests)={stageD:F3} E(tail)={stageE:F3}");

                    // 重置 ThreadStatic
                    _stagedSw = null;
                    _stagedSteamId = 0UL;
                    _stagedIsLocalPlayer = false;
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Shared]", $"[D-Vis-18] Postfix 异常（不阻断）: {ex.Message}");
                }
            }

            private static void RecordStage(string label, ref double field)
            {
                try
                {
                    if (!ShouldLogDVis()) return;
                    Stopwatch sw = _stagedSw;
                    if (sw == null) return;
                    field = sw.Elapsed.TotalMilliseconds;
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Shared]", $"[D-Vis-18] {label} 记录异常（不阻断）: {ex.Message}");
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
