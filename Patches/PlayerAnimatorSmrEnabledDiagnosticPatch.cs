using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Reflection;
using UnityEngine;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    ///
    /// 审计强制修正 1：D-Vis-10 改为 4 patch 目标，覆盖 isHiddenWaitingForClothing 完整生命周期。
    ///
    /// 4 patch 目标（U3-SDK 源码验证）：
    ///   Patch 1: PlayerAnimator.InitializePlayer（L1593）
    ///            - L1595 设置 isHiddenWaitingForClothing=true
    ///            - Postfix 验证设置成功
    ///
    ///   Patch 2: PlayerAnimator.NotifyClothingIsVisible（L641）
    ///            - L643 清除 isHiddenWaitingForClothing=false
    ///            - L651/656 设置 thirdRenderer_0/1.enabled=true
    ///            - Prefix 捕获即将被清除的 true 值
    ///            - Postfix 验证清除成功 + SMR 启用
    ///
    ///   Patch 3: PlayerAnimator.onLifeUpdated（L582）
    ///            - L621 守卫 !isHiddenWaitingForClothing 控制 L625/630 SMR.enabled=!isDead
    ///            - Prefix 捕获守卫状态（守卫失败=SMR 永不被设置）
    ///
    ///   Patch 4: PlayerClothing.ReceiveClothingState Postfix（L1318）
    ///            - L1371 调用 NotifyClothingIsVisible
    ///            - L1375 Player.isLoadingClothing=false（仅 IsLocalPlayer 分支）
    ///            - Postfix 验证 NotifyClothingIsVisible 被调用 + isHiddenWaitingForClothing=false
    ///
    ///   InitializePlayer 触发? -> 是 -> ReceiveClothingState 触发? -> 是 -> NotifyClothingIsVisible 触发?
    ///   -> 是 -> isHiddenWaitingForClothing=false? -> 是 -> SMR.enabled=true?
    ///
    /// 严格禁止：
    ///   - 修改原方法参数或返回值
    ///   - 修改 vanilla 渲染状态
    ///   - 修改 isHiddenWaitingForClothing / SMR.enabled
    /// </summary>
    public static class PlayerAnimatorSmrEnabledDiagnosticPatch
    {
        public static bool DVis10_InitPlayer_Registered { get; private set; }
        public static bool DVis10_NotifyClothing_Registered { get; private set; }
        public static bool DVis10_OnLifeUpdated_Registered { get; private set; }
        public static bool DVis10_ReceiveClothing_Registered { get; private set; }

        public static bool AllRegistrationsSucceeded =>
            DVis10_InitPlayer_Registered && DVis10_NotifyClothing_Registered
            && DVis10_OnLifeUpdated_Registered && DVis10_ReceiveClothing_Registered;

        // 反射缓存：PlayerAnimator.isHiddenWaitingForClothing（private bool，L152）
        private static FieldInfo _isHiddenWaitingForClothingField;

        // 反射缓存：PlayerAnimator.thirdRenderer_0/1（private SkinnedMeshRenderer，L55/56）
        private static FieldInfo _thirdRenderer0Field;
        private static FieldInfo _thirdRenderer1Field;

        public static bool RegisterManual(Harmony harmony)
        {
            CacheReflection();

            DVis10_InitPlayer_Registered = RegisterDVis10_InitializePlayer(harmony);
            DVis10_NotifyClothing_Registered = RegisterDVis10_NotifyClothingIsVisible(harmony);
            DVis10_OnLifeUpdated_Registered = RegisterDVis10_OnLifeUpdated(harmony);
            DVis10_ReceiveClothing_Registered = RegisterDVis10_ReceiveClothingState(harmony);

            RoleLogger.Info("[Shared]",
                $"[D-Vis] PlayerAnimatorSmrEnabledDiagnosticPatch 汇总: " +
                $"InitPlayer={DVis10_InitPlayer_Registered} " +
                $"NotifyClothing={DVis10_NotifyClothing_Registered} " +
                $"OnLifeUpdated={DVis10_OnLifeUpdated_Registered} " +
                $"ReceiveClothing={DVis10_ReceiveClothing_Registered}");

            return AllRegistrationsSucceeded;
        }

        private static void CacheReflection()
        {
            try
            {
                _isHiddenWaitingForClothingField = AccessTools.Field(typeof(PlayerAnimator), "isHiddenWaitingForClothing");
                _thirdRenderer0Field = AccessTools.Field(typeof(PlayerAnimator), "thirdRenderer_0");
                _thirdRenderer1Field = AccessTools.Field(typeof(PlayerAnimator), "thirdRenderer_1");

                if (_isHiddenWaitingForClothingField == null)
                    RoleLogger.Warn("[Shared]", "[D-Vis-10] isHiddenWaitingForClothing 反射失败");
                if (_thirdRenderer0Field == null)
                    RoleLogger.Warn("[Shared]", "[D-Vis-10] thirdRenderer_0 反射失败");
                if (_thirdRenderer1Field == null)
                    RoleLogger.Warn("[Shared]", "[D-Vis-10] thirdRenderer_1 反射失败");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[D-Vis-10] 反射缓存异常: {ex.Message}");
            }
        }

        // ---------- Patch 1: PlayerAnimator.InitializePlayer ----------
        private static bool RegisterDVis10_InitializePlayer(Harmony harmony)
        {
            const string Label = "D-Vis-10 PlayerAnimator.InitializePlayer";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(PlayerAnimator), "InitializePlayer");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[D-Vis-10] !!! {Label} 反射失败");
                    return false;
                }
                MethodInfo postfix = typeof(Hooks).GetMethod(nameof(Hooks.InitializePlayerPostfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, postfix: new HarmonyMethod(postfix));
                RoleLogger.Info("[Shared]", $"[D-Vis-10] OK {Label} 已登记 (Postfix)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[D-Vis-10] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        // ---------- Patch 2: PlayerAnimator.NotifyClothingIsVisible ----------
        private static bool RegisterDVis10_NotifyClothingIsVisible(Harmony harmony)
        {
            const string Label = "D-Vis-10 PlayerAnimator.NotifyClothingIsVisible";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(PlayerAnimator), "NotifyClothingIsVisible");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[D-Vis-10] !!! {Label} 反射失败");
                    return false;
                }
                MethodInfo prefix = typeof(Hooks).GetMethod(nameof(Hooks.NotifyClothingIsVisiblePrefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo postfix = typeof(Hooks).GetMethod(nameof(Hooks.NotifyClothingIsVisiblePostfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(postfix));
                RoleLogger.Info("[Shared]", $"[D-Vis-10] OK {Label} 已登记 (Prefix+Postfix)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[D-Vis-10] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        // ---------- Patch 3: PlayerAnimator.onLifeUpdated ----------
        private static bool RegisterDVis10_OnLifeUpdated(Harmony harmony)
        {
            const string Label = "D-Vis-10 PlayerAnimator.onLifeUpdated";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(PlayerAnimator), "onLifeUpdated");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[D-Vis-10] !!! {Label} 反射失败");
                    return false;
                }
                MethodInfo prefix = typeof(Hooks).GetMethod(nameof(Hooks.OnLifeUpdatedPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, prefix: new HarmonyMethod(prefix));
                RoleLogger.Info("[Shared]", $"[D-Vis-10] OK {Label} 已登记 (Prefix)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[D-Vis-10] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        // ---------- Patch 4: PlayerClothing.ReceiveClothingState ----------
        private static bool RegisterDVis10_ReceiveClothingState(Harmony harmony)
        {
            const string Label = "D-Vis-10 PlayerClothing.ReceiveClothingState";
            try
            {
                // PlayerClothing.ReceiveClothingState 已在 InitialStateReceiveDiagnosticPatch 登记
                MethodInfo original = AccessTools.Method(typeof(PlayerClothing), "ReceiveClothingState");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[D-Vis-10] !!! {Label} 反射失败");
                    return false;
                }
                MethodInfo postfix = typeof(Hooks).GetMethod(nameof(Hooks.ReceiveClothingStatePostfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, postfix: new HarmonyMethod(postfix));
                RoleLogger.Info("[Shared]", $"[D-Vis-10] OK {Label} 已登记 (Postfix, 与既有 Prefix 共存)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[D-Vis-10] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        // ====================== Hooks ======================

        private static class Hooks
        {
            // Patch 1: InitializePlayer Postfix
            // 验证 isHiddenWaitingForClothing 被设置为 true
            internal static void InitializePlayerPostfix(PlayerAnimator __instance)
            {
                try
                {
                    if (!ShouldLogDVis()) return;

                    Player player = __instance?.player;
                    if (player == null) return;
                    SteamPlayer sp = player.channel?.owner;
                    ulong steamId = sp?.playerID?.steamID.m_SteamID ?? 0UL;
                    bool isLocalPlayer = player.channel?.IsLocalPlayer ?? false;

                    bool? isHidden = GetIsHiddenWaitingForClothing(__instance);
                    string smrState = GetSmrState(__instance);

                    RoleLogger.Info("[Shared]",
                        $"[D-Vis-10] InitializePlayer Postfix steamId={DiagnosticMaskUtil.MaskSteamId(steamId)} " +
                        $"isLocalPlayer={isLocalPlayer} isHiddenWaitingForClothing={FormatBool(isHidden)} " +
                        $"smr={smrState}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Shared]", $"[D-Vis-10] InitializePlayer 异常（不阻断）: {ex.Message}");
                }
            }

            // Patch 2: NotifyClothingIsVisible Prefix + Postfix
            // Prefix 捕获即将被清除的 true 值；Postfix 验证清除 + SMR 启用
            internal static void NotifyClothingIsVisiblePrefix(PlayerAnimator __instance)
            {
                try
                {
                    if (!ShouldLogDVis()) return;

                    Player player = __instance?.player;
                    if (player == null) return;
                    SteamPlayer sp = player.channel?.owner;
                    ulong steamId = sp?.playerID?.steamID.m_SteamID ?? 0UL;
                    bool isLocalPlayer = player.channel?.IsLocalPlayer ?? false;

                    bool? isHiddenBefore = GetIsHiddenWaitingForClothing(__instance);
                    string smrBefore = GetSmrState(__instance);
                    bool isAlive = player.life?.IsAlive ?? false;

                    RoleLogger.Info("[Shared]",
                        $"[D-Vis-10] NotifyClothingIsVisible Prefix steamId={DiagnosticMaskUtil.MaskSteamId(steamId)} " +
                        $"isLocalPlayer={isLocalPlayer} isAlive={isAlive} " +
                        $"isHiddenWaitingForClothing(before)={FormatBool(isHiddenBefore)} " +
                        $"smr(before)={smrBefore}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Shared]", $"[D-Vis-10] NotifyClothingIsVisible Prefix 异常（不阻断）: {ex.Message}");
                }
            }

            internal static void NotifyClothingIsVisiblePostfix(PlayerAnimator __instance)
            {
                try
                {
                    if (!ShouldLogDVis()) return;

                    Player player = __instance?.player;
                    if (player == null) return;
                    SteamPlayer sp = player.channel?.owner;
                    ulong steamId = sp?.playerID?.steamID.m_SteamID ?? 0UL;

                    bool? isHiddenAfter = GetIsHiddenWaitingForClothing(__instance);
                    string smrAfter = GetSmrState(__instance);

                    // 捕获调用栈（定位是谁调用了 NotifyClothingIsVisible）
                    string stack = System.Environment.StackTrace;
                    string shortStack = ShortenStack(stack);

                    RoleLogger.Info("[Shared]",
                        $"[D-Vis-10] NotifyClothingIsVisible Postfix steamId={DiagnosticMaskUtil.MaskSteamId(steamId)} " +
                        $"isHiddenWaitingForClothing(after)={FormatBool(isHiddenAfter)} " +
                        $"smr(after)={smrAfter} stack={shortStack}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Shared]", $"[D-Vis-10] NotifyClothingIsVisible Postfix 异常（不阻断）: {ex.Message}");
                }
            }

            // Patch 3: onLifeUpdated Prefix
            // 捕获守卫 !isHiddenWaitingForClothing 是否通过
            internal static void OnLifeUpdatedPrefix(PlayerAnimator __instance, bool isDead)
            {
                try
                {
                    if (!ShouldLogDVis()) return;

                    Player player = __instance?.player;
                    if (player == null) return;
                    SteamPlayer sp = player.channel?.owner;
                    ulong steamId = sp?.playerID?.steamID.m_SteamID ?? 0UL;
                    bool isLocalPlayer = player.channel?.IsLocalPlayer ?? false;

                    bool? isHidden = GetIsHiddenWaitingForClothing(__instance);
                    string smrState = GetSmrState(__instance);

                    // 守卫判定：!isHiddenWaitingForClothing
                    // - isHidden=true -> 守卫失败 -> L625/630 不执行 -> SMR.enabled 永不被设置
                    // - isHidden=false -> 守卫通过 -> L625/630 执行 -> SMR.enabled=!isDead
                    bool guardPasses = !isHidden ?? true;
                    string guardStatus = isHidden == null ? "unknown" : (guardPasses ? "PASS" : "FAIL(blocked)");

                    RoleLogger.Info("[Shared]",
                        $"[D-Vis-10] onLifeUpdated Prefix steamId={DiagnosticMaskUtil.MaskSteamId(steamId)} " +
                        $"isLocalPlayer={isLocalPlayer} isDead={isDead} " +
                        $"isHiddenWaitingForClothing={FormatBool(isHidden)} guard={guardStatus} " +
                        $"smr={smrState}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Shared]", $"[D-Vis-10] onLifeUpdated 异常（不阻断）: {ex.Message}");
                }
            }

            // Patch 4: PlayerClothing.ReceiveClothingState Postfix
            // 验证 NotifyClothingIsVisible 被调用 + isHiddenWaitingForClothing=false
            internal static void ReceiveClothingStatePostfix(PlayerClothing __instance)
            {
                try
                {
                    if (!ShouldLogDVis()) return;

                    Player player = __instance?.player;
                    if (player == null) return;
                    SteamPlayer sp = player.channel?.owner;
                    ulong steamId = sp?.playerID?.steamID.m_SteamID ?? 0UL;
                    bool isLocalPlayer = player.channel?.IsLocalPlayer ?? false;

                    PlayerAnimator animator = player.animator;
                    bool? isHidden = animator != null ? GetIsHiddenWaitingForClothing(animator) : null;
                    string smrState = animator != null ? GetSmrState(animator) : "animator=null";

                    // 反射读取 Player.isLoadingClothing（关联 D-Vis-12）
                    bool isLoadingClothing = Player.isLoadingClothing;

                    // 服装字段摘要（验证数据已同步）
                    ushort shirtId = __instance.shirt;
                    ushort pantsId = __instance.pants;
                    ushort hatId = __instance.hat;

                    RoleLogger.Info("[Shared]",
                        $"[D-Vis-10] ReceiveClothingState Postfix steamId={DiagnosticMaskUtil.MaskSteamId(steamId)} " +
                        $"isLocalPlayer={isLocalPlayer} isHiddenWaitingForClothing={FormatBool(isHidden)} " +
                        $"smr={smrState} isLoadingClothing={isLoadingClothing} " +
                        $"shirt={shirtId} pants={pantsId} hat={hatId}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Shared]", $"[D-Vis-10] ReceiveClothingState Postfix 异常（不阻断）: {ex.Message}");
                }
            }
        }

        // ====================== Helpers ======================

        private static bool? GetIsHiddenWaitingForClothing(PlayerAnimator animator)
        {
            try
            {
                if (_isHiddenWaitingForClothingField == null || animator == null) return null;
                return (bool)_isHiddenWaitingForClothingField.GetValue(animator);
            }
            catch
            {
                return null;
            }
        }

        private static string GetSmrState(PlayerAnimator animator)
        {
            try
            {
                if (animator == null) return "animator=null";
                int enabled = 0;
                int total = 0;
                int matNull = 0;

                if (_thirdRenderer0Field != null)
                {
                    SkinnedMeshRenderer smr0 = _thirdRenderer0Field.GetValue(animator) as SkinnedMeshRenderer;
                    if (smr0 != null)
                    {
                        total++;
                        if (smr0.enabled) enabled++;
                        if (smr0.sharedMaterial == null) matNull++;
                    }
                }
                if (_thirdRenderer1Field != null)
                {
                    SkinnedMeshRenderer smr1 = _thirdRenderer1Field.GetValue(animator) as SkinnedMeshRenderer;
                    if (smr1 != null)
                    {
                        total++;
                        if (smr1.enabled) enabled++;
                        if (smr1.sharedMaterial == null) matNull++;
                    }
                }
                return $"(enabled={enabled},matNull={matNull},total={total})";
            }
            catch (System.Exception ex)
            {
                return $"err={ex.Message}";
            }
        }

        private static string FormatBool(bool? value)
        {
            return value.HasValue ? value.Value.ToString() : "null";
        }

        private static string ShortenStack(string stack)
        {
            if (string.IsNullOrEmpty(stack)) return "empty";
            // 取前 5 帧（足够定位调用方）
            string[] lines = stack.Split('\n');
            int take = System.Math.Min(5, lines.Length);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < take; i++)
            {
                string line = lines[i].Trim();
                if (line.Length > 120) line = line.Substring(0, 120) + "...";
                if (i > 0) sb.Append(" | ");
                sb.Append(line);
            }
            return sb.ToString();
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
