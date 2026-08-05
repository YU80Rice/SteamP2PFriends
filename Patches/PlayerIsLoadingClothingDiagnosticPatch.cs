using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.19 D-Vis-12 诊断 patch（客机模型可见性差异诊断 - Player.isLoadingClothing 生命周期）。
    ///
    /// 审计强制修正 3：D-Vis-12 补 PlayerClothing.cs:1375 清理点 patch，共 3 patch 点。
    ///
    /// 3 patch 点（U3-SDK 源码验证）：
    ///   Patch 1: Player.InitializePlayer（Player.cs:1572）
    ///            - L1570-1572: if (channel.IsLocalPlayer) { isLoadingInventory=true; isLoadingLife=true; isLoadingClothing=true; }
    ///            - Postfix 验证设置成功（仅本地 Player）
    ///
    ///   Patch 2: PlayerClothing.ReceiveClothingState（PlayerClothing.cs:1375）
    ///            - L1371: player.animator.NotifyClothingIsVisible();
    ///            - L1373-1376: if (channel.IsLocalPlayer) { Player.isLoadingClothing=false; }
    ///            - Postfix 验证清理成功（仅本地 Player）
    ///
    ///   Patch 3: Player.OnDestroy（Player.cs:1780）
    ///            - L1776-1780: if (channel != null && channel.IsLocalPlayer) { ... isLoadingClothing=false; }
    ///            - Postfix 验证清理成功（仅本地 Player）
    ///
    /// 关键诊断目标：
    ///   - 验证客机端 ReceiveClothingState 是否触发
    ///     若不触发 -> isLoadingClothing 保持 true（根因之一）
    ///   - 验证 OnDestroy 是否过早触发（客机 Player 被销毁？）
    ///
    /// 关联 D-Vis-11：
    ///   - isLoadingClothing=true 持续 -> LoadingUI.Update 中 Player.isLoading=true 持续 -> isBlocked=true 持续
    ///
    /// 严格禁止：
    ///   - 修改原方法参数或返回值
    ///   - 修改 vanilla isLoadingClothing 字段
    /// </summary>
    public static class PlayerIsLoadingClothingDiagnosticPatch
    {
        public static bool DVis12_InitPlayer_Registered { get; private set; }
        public static bool DVis12_ReceiveClothing_Registered { get; private set; }
        public static bool DVis12_OnDestroy_Registered { get; private set; }

        public static bool AllRegistrationsSucceeded =>
            DVis12_InitPlayer_Registered && DVis12_ReceiveClothing_Registered && DVis12_OnDestroy_Registered;

        public static bool RegisterManual(Harmony harmony)
        {
            DVis12_InitPlayer_Registered = RegisterDVis12_InitializePlayer(harmony);
            DVis12_ReceiveClothing_Registered = RegisterDVis12_ReceiveClothingState(harmony);
            DVis12_OnDestroy_Registered = RegisterDVis12_OnDestroy(harmony);

            RoleLogger.Info("[Shared]",
                $"[D-Vis] PlayerIsLoadingClothingDiagnosticPatch 汇总: " +
                $"InitPlayer={DVis12_InitPlayer_Registered} " +
                $"ReceiveClothing={DVis12_ReceiveClothing_Registered} " +
                $"OnDestroy={DVis12_OnDestroy_Registered}");

            return AllRegistrationsSucceeded;
        }

        // ---------- Patch 1: Player.InitializePlayer ----------
        private static bool RegisterDVis12_InitializePlayer(Harmony harmony)
        {
            const string Label = "D-Vis-12 Player.InitializePlayer";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(Player), "InitializePlayer");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[D-Vis-12] !!! {Label} 反射失败");
                    return false;
                }
                MethodInfo postfix = typeof(Hooks).GetMethod(nameof(Hooks.InitializePlayerPostfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, postfix: new HarmonyMethod(postfix));
                RoleLogger.Info("[Shared]", $"[D-Vis-12] OK {Label} 已登记 (Postfix)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[D-Vis-12] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        // ---------- Patch 2: PlayerClothing.ReceiveClothingState ----------
        private static bool RegisterDVis12_ReceiveClothingState(Harmony harmony)
        {
            const string Label = "D-Vis-12 PlayerClothing.ReceiveClothingState";
            try
            {
                // ReceiveClothingState 已在 D-Vis-10 / InitialStateReceiveDiagnosticPatch 登记
                // 本 patch 追加 Postfix（与既有 Prefix 共存，Harmony 支持多 Postfix）
                MethodInfo original = AccessTools.Method(typeof(PlayerClothing), "ReceiveClothingState");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[D-Vis-12] !!! {Label} 反射失败");
                    return false;
                }
                MethodInfo postfix = typeof(Hooks).GetMethod(nameof(Hooks.ReceiveClothingStatePostfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, postfix: new HarmonyMethod(postfix));
                RoleLogger.Info("[Shared]", $"[D-Vis-12] OK {Label} 已登记 (Postfix, 与既有 Prefix 共存)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[D-Vis-12] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        // ---------- Patch 3: Player.OnDestroy ----------
        private static bool RegisterDVis12_OnDestroy(Harmony harmony)
        {
            const string Label = "D-Vis-12 Player.OnDestroy";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(Player), "OnDestroy");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[D-Vis-12] !!! {Label} 反射失败");
                    return false;
                }
                MethodInfo postfix = typeof(Hooks).GetMethod(nameof(Hooks.OnDestroyPostfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, postfix: new HarmonyMethod(postfix));
                RoleLogger.Info("[Shared]", $"[D-Vis-12] OK {Label} 已登记 (Postfix)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[D-Vis-12] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        // ====================== Hooks ======================

        private static class Hooks
        {
            // Patch 1: Player.InitializePlayer Postfix
            internal static void InitializePlayerPostfix(Player __instance)
            {
                try
                {
                    if (!ShouldLogDVis()) return;

                    SteamPlayer sp = __instance?.channel?.owner;
                    ulong steamId = sp?.playerID?.steamID.m_SteamID ?? 0UL;
                    bool isLocalPlayer = __instance?.channel?.IsLocalPlayer ?? false;

                    // Player.isLoadingClothing 是 static 字段（Player.cs:221）
                    bool isLoadingClothing = Player.isLoadingClothing;

                    RoleLogger.Info("[Shared]",
                        $"[D-Vis-12] Player.InitializePlayer Postfix steamId={DiagnosticMaskUtil.MaskSteamId(steamId)} " +
                        $"isLocalPlayer={isLocalPlayer} Player.isLoadingClothing(static)={isLoadingClothing} " +
                        $"(预期: true 仅当 isLocalPlayer=true)");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Shared]", $"[D-Vis-12] InitializePlayer 异常（不阻断）: {ex.Message}");
                }
            }

            // Patch 2: PlayerClothing.ReceiveClothingState Postfix
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

                    // Player.isLoadingClothing 是 static 字段
                    bool isLoadingClothing = Player.isLoadingClothing;

                    RoleLogger.Info("[Shared]",
                        $"[D-Vis-12] PlayerClothing.ReceiveClothingState Postfix steamId={DiagnosticMaskUtil.MaskSteamId(steamId)} " +
                        $"isLocalPlayer={isLocalPlayer} Player.isLoadingClothing(static)={isLoadingClothing} " +
                        $"(预期: false 仅当 isLocalPlayer=true)");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Shared]", $"[D-Vis-12] ReceiveClothingState Postfix 异常（不阻断）: {ex.Message}");
                }
            }

            // Patch 3: Player.OnDestroy Postfix
            internal static void OnDestroyPostfix(Player __instance)
            {
                try
                {
                    if (!ShouldLogDVis()) return;

                    SteamPlayer sp = __instance?.channel?.owner;
                    ulong steamId = sp?.playerID?.steamID.m_SteamID ?? 0UL;
                    bool isLocalPlayer = __instance?.channel?.IsLocalPlayer ?? false;

                    bool isLoadingClothing = Player.isLoadingClothing;

                    RoleLogger.Info("[Shared]",
                        $"[D-Vis-12] Player.OnDestroy Postfix steamId={DiagnosticMaskUtil.MaskSteamId(steamId)} " +
                        $"isLocalPlayer={isLocalPlayer} Player.isLoadingClothing(static)={isLoadingClothing} " +
                        $"(预期: false 仅当 isLocalPlayer=true)");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Shared]", $"[D-Vis-12] OnDestroy 异常（不阻断）: {ex.Message}");
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
