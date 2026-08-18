using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using SteamP2PFriends.Shared.Enums;
using System.Collections.Generic;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// Player 初始化隔离补丁。
    ///
    /// v2 审计 §4.4 评估：
    ///   - E-1 状态表（Constructed -> Initializing -> Ready -> Failed）合理
    ///   - E-2 Update/FixedUpdate 短路必须加 P2P 模式门控 + IsLocalPlayer 排除
    ///   - E-3 Prefix/Postfix/Finalizer 三件套是标准做法
    ///   - E-4 调用 vanilla dismiss 是正确路径
    ///
    /// 手动注册以保证目标和所有者可验证；ExtractPlayer 缓存 PropertyInfo，避免每帧反射。
    /// </summary>
    public static class PlayerUpdateGuardPatch
    {
        public const bool Enabled = true;

        private static bool _e3Registered;

        public static void RegisterManual(Harmony harmony)
        {
            // Player.InitializePlayer 状态机。
            RegisterInitializePlayerStatePatch(harmony);

            // E-2：PlayerMovement.Update 短路
            RegisterUpdateGuard<PlayerMovement>(harmony, "Update");

            // E-2：PlayerLook.Update 短路（updateAim 是 Update 内部调用）
            RegisterUpdateGuard<PlayerLook>(harmony, "Update");

            // E-2：PlayerInput.FixedUpdate 短路
            RegisterUpdateGuard<PlayerInput>(harmony, "FixedUpdate");

            // E-2：PlayerStance.Update 短路
            RegisterUpdateGuard<PlayerStance>(harmony, "Update");

            RoleLogger.Info("[Shared]",
                "[P0-E] PlayerUpdateGuardPatch Enabled=true " +
                "(E-2 Update/FixedUpdate 护栏: 4 组件, " +
                "E-3 InitializePlayer 状态机: Prefix/Postfix/Finalizer via RegisterManual, " +
                "P2P 门控 + IsLocalPlayer 排除)");
        }

        private static void RegisterInitializePlayerStatePatch(Harmony harmony)
        {
            if (_e3Registered) return;
            _e3Registered = true;

            MethodInfo original = AccessTools.Method(typeof(Player), "InitializePlayer");
            if (original == null)
            {
                RoleLogger.Warn("[Shared]", "[P0-E] 找不到 Player.InitializePlayer");
                return;
            }

            MethodInfo prefix = AccessTools.Method(
                typeof(InitializePlayerStatePatch), nameof(InitializePlayerStatePatch.Prefix));
            MethodInfo postfix = AccessTools.Method(
                typeof(InitializePlayerStatePatch), nameof(InitializePlayerStatePatch.Postfix));
            MethodInfo finalizer = AccessTools.Method(
                typeof(InitializePlayerStatePatch), nameof(InitializePlayerStatePatch.Finalizer));

            harmony.Patch(original,
                prefix: new HarmonyMethod(prefix),
                postfix: new HarmonyMethod(postfix),
                finalizer: new HarmonyMethod(finalizer));

            RoleLogger.Info("[Shared]", "[P0-E] InitializePlayerStatePatch registered manually");
        }

        private static void RegisterUpdateGuard<T>(Harmony harmony, string methodName)
        {
            MethodInfo original = AccessTools.Method(typeof(T), methodName);
            if (original == null)
            {
                RoleLogger.Warn("[Shared]", $"[P0-E] 找不到 {typeof(T).Name}.{methodName}");
                return;
            }

            MethodInfo prefix = AccessTools.Method(typeof(UpdateGuardCommon<T>), nameof(UpdateGuardCommon<T>.Prefix));
            harmony.Patch(original, prefix: new HarmonyMethod(prefix));
            RoleLogger.Info("[Shared]", $"[P0-E] Update guard registered: {typeof(T).Name}.{methodName}");
        }
    }

    /// <summary>
    /// E-2 通用 Update/FixedUpdate 短路 Prefix（泛型版本，按组件类型缓存 PropertyInfo）。
    /// 二次审计 Medium-3 修复：缓存 PropertyInfo，避免每帧反射。
    /// </summary>
    public static class UpdateGuardCommon<T>
    {
        private static PropertyInfo _playerProperty;
        private static bool _propertyResolved;

        public static bool Prefix(object __instance)
        {
            // 门控 1：仅 P2P 主机模式启用
            if (!HostManager.IsP2PHostMode) return true;

            // 提取 player 字段（缓存 PropertyInfo）
            Player player = ExtractPlayer(__instance);
            if (ReferenceEquals(player, null)) return true;

            // 门控 2/3/4：委托给 ShouldShortCircuitUpdate
            // ShouldShortCircuitUpdate 内部已排除 IsLocalPlayer
            bool shouldShortCircuit = PlayerInitializationTracker.ShouldShortCircuitUpdate(player);
            return !shouldShortCircuit;
        }

        private static Player ExtractPlayer(object instance)
        {
            if (ReferenceEquals(instance, null)) return null;
            if (!_propertyResolved)
            {
                _propertyResolved = true;
                try
                {
                    _playerProperty = typeof(T).GetProperty("player");
                }
                catch
                {
                    _playerProperty = null;
                }
            }
            if (_playerProperty == null) return null;
            try
            {
                return _playerProperty.GetValue(instance, null) as Player;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// E-3 Player.InitializePlayer 状态机（RegisterManual，无 [HarmonyPatch] 特性）。
    ///   - Prefix 改为返回 bool：非 P2P 主机返回 true；首次调用 __state=true 返回 true；
    ///     真正重复调用 __state=false 返回 false（真正阻止原方法）。
    ///   - Postfix 仅在 __state && __runOriginal 时 MarkReady。
    ///   - Finalizer 仅在真实原方法异常时 MarkFailed。
    ///   - 状态机唯一所有者（PlayerInitializeDiagnosticPatch 已恢复为纯观察）。
    /// </summary>
    public static class InitializePlayerStatePatch
    {
        public static bool Prefix(Player __instance, ref bool __state)
        {
            // 非 P2P 主机放行（不参与状态机）
            if (!HostManager.IsP2PHostMode)
            {
                __state = false;
                return true;
            }

            // Medium-4 修复：状态表无记录时强制 MarkConstructed 兜底
            EPlayerInitState currentState = PlayerInitializationTracker.GetState(__instance);
            if (currentState == EPlayerInitState.Unknown)
            {
                RoleLogger.Warn("[Host]",
                    $"[P0-E] Player {__instance.GetInstanceID()} 状态表无记录，" +
                    "兜底 MarkConstructed（SteamPlayer.ctor Postfix 可能未执行）");
                PlayerInitializationTracker.MarkConstructed(__instance);
            }

            bool allow = PlayerInitializationTracker.TryMarkInitializing(__instance);
            __state = allow;
            if (!allow)
            {
                RoleLogger.Warn("[Host]",
                    $"[P0-E] Player.InitializePlayer Prefix 阻止重复初始化: " +
                    $"instanceId={__instance.GetInstanceID()}");
                return false; // 真正阻止原方法执行
            }
            return true;
        }

        public static void Postfix(Player __instance, bool __state, bool __runOriginal)
        {
            if (!HostManager.IsP2PHostMode) return;
            // 仅在 Prefix 允许（__state=true）且原方法真实执行（__runOriginal=true）时 MarkReady
            if (!__state || !__runOriginal) return;

            PlayerInitializationTracker.MarkReady(__instance);
            RoleLogger.Info("[Host]",
                $"[P0-E] Player.InitializePlayer Ready: " +
                $"steamId={__instance.channel?.owner?.playerID?.steamID} " +
                $"instanceId={__instance.GetInstanceID()}");
        }

        public static System.Exception Finalizer(Player __instance, bool __state, System.Exception __exception)
        {
            // 仅在 Prefix 允许（__state=true）且原方法真实抛异常时 MarkFailed
            if (__state && __exception != null)
            {
                if (HostManager.IsP2PHostMode)
                {
                    PlayerInitializationTracker.MarkFailed(__instance);
                    RoleLogger.Error("[Host]",
                        $"[P0-E] Player.InitializePlayer Failed: " +
                        $"steamId={__instance.channel?.owner?.playerID?.steamID} " +
                        $"instanceId={__instance.GetInstanceID()} " +
                        $"exception={__exception.GetType().Name}: {__exception.Message}");
                }
            }
            return __exception;
        }
    }
}
