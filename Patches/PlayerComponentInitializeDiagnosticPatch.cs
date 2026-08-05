using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Collections.Generic;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.2 P0-D D-3b：各组件 InitializePlayer Prefix+Finalizer 诊断 patch。
    ///
    /// v0.2.3.2 P0-3 修复（Codex 第三次审计）：
    ///   - 补齐 PlayerVoice / PlayerWorkzone 组件（vanilla Player.InitializePlayer 内部调用 15 个子组件）。
    ///   - 补齐 Player.InitializePlayerStart（private，Player.cs:1542，InitializePlayer 第一个调用）。
    ///   - SteamID 提取改为 cast __instance 为 PlayerCaller 后访问公开 player 属性
    ///     （原反射查 "player" 字段失败，真实字段名是 PlayerCaller._player）。
    /// </summary>
    public static class PlayerComponentInitializeDiagnosticPatch
    {
        public static void RegisterManual(Harmony harmony)
        {
            try
            {
                // 15 个子组件 InitializePlayer，签名都是 internal void InitializePlayer()
                System.Type[] componentTypes = new System.Type[]
                {
                    typeof(PlayerClothing),
                    typeof(PlayerInventory),
                    typeof(PlayerLife),
                    typeof(PlayerSkills),
                    typeof(PlayerCrafting),
                    typeof(PlayerStance),
                    typeof(PlayerMovement),
                    typeof(PlayerLook),
                    typeof(PlayerInteract),
                    typeof(PlayerAnimator),
                    typeof(PlayerEquipment),
                    typeof(PlayerInput),
                    typeof(PlayerVoice),
                    typeof(PlayerWorkzone),
                    typeof(PlayerQuests),
                };

                foreach (System.Type t in componentTypes)
                {
                    RegisterOne(harmony, t);
                }

                // Player.InitializePlayerStart (private, Player.cs:1542)
                RegisterPlayerStart(harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[ManualPatch] PlayerComponentInitializeDiagnosticPatch.RegisterManual 失败: {ex}");
            }
        }

        private static void RegisterOne(Harmony harmony, System.Type targetType)
        {
            try
            {
                System.Reflection.MethodInfo original = AccessTools.Method(targetType, "InitializePlayer", new System.Type[0]);
                if (original == null)
                {
                    RoleLogger.Warn("[Shared]",
                        $"[ManualPatch] !!! {targetType.Name}.InitializePlayer: method not found");
                    return;
                }

                System.Reflection.MethodInfo prefix = AccessTools.Method(
                    typeof(PlayerComponentInitializeDiagnosticPatch), "Component_InitializePlayer_Prefix");
                System.Reflection.MethodInfo finalizer = AccessTools.Method(
                    typeof(PlayerComponentInitializeDiagnosticPatch), "Component_InitializePlayer_Finalizer");

                harmony.Patch(original,
                    prefix: new HarmonyMethod(prefix),
                    finalizer: new HarmonyMethod(finalizer));

                HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
                RoleLogger.Info("[Shared]",
                    $"[ManualPatch] OK {targetType.Name}.InitializePlayer 已登记 " +
                    $"(prefixes={info?.Prefixes?.Count ?? 0}, finalizers={info?.Finalizers?.Count ?? 0})");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[ManualPatch] !!! {targetType.Name}.InitializePlayer 注册异常: {ex}");
            }
        }

        private static void RegisterPlayerStart(Harmony harmony)
        {
            try
            {
                // private void InitializePlayerStart() — AccessTools 默认包含非公共成员
                System.Reflection.MethodInfo original = AccessTools.Method(typeof(Player), "InitializePlayerStart", new System.Type[0]);
                if (original == null)
                {
                    RoleLogger.Warn("[Shared]", "[ManualPatch] !!! Player.InitializePlayerStart: method not found");
                    return;
                }

                System.Reflection.MethodInfo prefix = AccessTools.Method(
                    typeof(PlayerComponentInitializeDiagnosticPatch), "PlayerStart_Prefix");
                System.Reflection.MethodInfo finalizer = AccessTools.Method(
                    typeof(PlayerComponentInitializeDiagnosticPatch), "PlayerStart_Finalizer");

                harmony.Patch(original,
                    prefix: new HarmonyMethod(prefix),
                    finalizer: new HarmonyMethod(finalizer));

                HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
                RoleLogger.Info("[Shared]",
                    $"[ManualPatch] OK Player.InitializePlayerStart 已登记 " +
                    $"(prefixes={info?.Prefixes?.Count ?? 0}, finalizers={info?.Finalizers?.Count ?? 0})");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[ManualPatch] !!! Player.InitializePlayerStart 注册异常: {ex}");
            }
        }

        // ===== 子组件 InitializePlayer =====
        public static void Component_InitializePlayer_Prefix(object __instance)
        {
            try
            {
                string componentName = __instance?.GetType().Name ?? "null";
                ulong steamId = ExtractSteamIdFromComponent(__instance);

                RoleLogger.Info(RoleLogger.ResolveDynamicRole(),
                    $"{DiagnosticContext.FormatPrefixFor(steamId, $"{componentName}.InitializePlayer ENTER")}");
            }
            catch { }
        }

        public static void Component_InitializePlayer_Finalizer(object __instance, System.Exception __exception)
        {
            try
            {
                string componentName = __instance?.GetType().Name ?? "null";
                ulong steamId = ExtractSteamIdFromComponent(__instance);

                if (__exception != null)
                {
                    RoleLogger.Error(RoleLogger.ResolveDynamicRole(),
                        $"{DiagnosticContext.FormatPrefixFor(steamId, $"{componentName}.InitializePlayer THREW")} " +
                        $"exceptionType={__exception.GetType().Name} message={__exception.Message}");
                    RoleLogger.Error(RoleLogger.ResolveDynamicRole(),
                        $"[Diag] {componentName}.InitializePlayer stack:\n{__exception.StackTrace}");
                }
                else
                {
                    RoleLogger.Info(RoleLogger.ResolveDynamicRole(),
                        $"{DiagnosticContext.FormatPrefixFor(steamId, $"{componentName}.InitializePlayer OK")}");
                }
            }
            catch { }
        }

        // ===== Player.InitializePlayerStart =====
        public static void PlayerStart_Prefix(Player __instance)
        {
            try
            {
                ulong steamId = ExtractSteamIdFromPlayer(__instance);
                RoleLogger.Info(RoleLogger.ResolveDynamicRole(),
                    $"{DiagnosticContext.FormatPrefixFor(steamId, "Player.InitializePlayerStart ENTER")}");
            }
            catch { }
        }

        public static void PlayerStart_Finalizer(Player __instance, System.Exception __exception)
        {
            try
            {
                ulong steamId = ExtractSteamIdFromPlayer(__instance);
                if (__exception != null)
                {
                    RoleLogger.Error(RoleLogger.ResolveDynamicRole(),
                        $"{DiagnosticContext.FormatPrefixFor(steamId, "Player.InitializePlayerStart THREW")} " +
                        $"exceptionType={__exception.GetType().Name} message={__exception.Message}");
                    RoleLogger.Error(RoleLogger.ResolveDynamicRole(),
                        $"[Diag] Player.InitializePlayerStart stack:\n{__exception.StackTrace}");
                }
                else
                {
                    RoleLogger.Info(RoleLogger.ResolveDynamicRole(),
                        $"{DiagnosticContext.FormatPrefixFor(steamId, "Player.InitializePlayerStart OK")}");
                }
            }
            catch { }
        }

        // ===== 辅助方法 =====
        /// <summary>
        /// P0-3 修复：cast __instance 为 PlayerCaller 后访问公开 player 属性。
        /// 真实字段是 protected Player _player（PlayerCaller.cs:9），公开属性是 player => _player（:10）。
        /// 旧实现反射查 "player" 字段会返回 null（因为 player 是属性不是字段）。
        /// </summary>
        private static ulong ExtractSteamIdFromComponent(object component)
        {
            if (ReferenceEquals(component, null)) return 0;
            try
            {
                if (!(component is PlayerCaller caller)) return 0;
                Player player = caller.player;
                return ExtractSteamIdFromPlayer(player);
            }
            catch
            {
                return 0;
            }
        }

        private static ulong ExtractSteamIdFromPlayer(Player player)
        {
            if (ReferenceEquals(player, null)) return 0;
            if (ReferenceEquals(player.channel?.owner?.playerID, null)) return 0;
            return player.channel.owner.playerID.steamID.m_SteamID;
        }
    }
}
