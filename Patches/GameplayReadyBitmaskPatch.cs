using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Client;
using SteamP2PFriends.Shared;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.3 第四次审计 P0-A 修复（Codex 外部审计）：
    ///   - 8 位信号语义为 LocalComponentsInitialized（不单独宣称 GameplayReady）。
    ///   - Postfix 三重门控：!Provider.isServer && ReferenceEquals(player, Player.LocalPlayer) && player.channel?.IsLocalPlayer == true。
    ///   - 完成回调 P2PJoinManager.NotifyLocalComponentsInitialized 仅记录信号，不推进 GameplayReady。
    ///   - v0.2.3.3 P0-A：删除 serverBoundsHistory 引用（该字段为服务器端专属，客机永远为 null）。
    ///
    /// 跟踪 8 个组件（Player.InitializePlayer 调用顺序，Player.cs:1625-1633）：
    ///   bit 0: PlayerClothing
    ///   bit 1: PlayerInventory
    ///   bit 2: PlayerLife
    ///   bit 3: PlayerStance
    ///   bit 4: PlayerMovement（P0-J 修正后不抛 NRE）
    ///   bit 5: PlayerLook
    ///   bit 6: PlayerInteract
    ///   bit 7: PlayerInput
    ///
    /// 二次审计 Medium-2 修复：
    ///   - 缓存 PropertyInfo（避免每次 Postfix 反射）。
    ///   - null 兜底：若 __instance.player 为 null，尝试 Player.LocalPlayer 作为 fallback。
    /// </summary>
    public static class GameplayReadyBitmaskPatch
    {
        public const bool Enabled = true;

        public static void RegisterManual(Harmony harmony)
        {
            RegisterComponentPostfix<PlayerClothing>(harmony, 0);
            RegisterComponentPostfix<PlayerInventory>(harmony, 1);
            RegisterComponentPostfix<PlayerLife>(harmony, 2);
            RegisterComponentPostfix<PlayerStance>(harmony, 3);
            RegisterComponentPostfix<PlayerMovement>(harmony, 4);
            RegisterComponentPostfix<PlayerLook>(harmony, 5);
            RegisterComponentPostfix<PlayerInteract>(harmony, 6);
            RegisterComponentPostfix<PlayerInput>(harmony, 7);

            RoleLogger.Info("[Shared]",
                "[P1-G] GameplayReadyBitmaskPatch registered (8 组件 InitializePlayer Postfix, " +
                "LocalComponentsInitialized 信号 + 三重门控: !isServer && ==LocalPlayer && IsLocalPlayer)");
        }

        private static void RegisterComponentPostfix<T>(Harmony harmony, int bitIndex)
        {
            MethodInfo original = AccessTools.Method(typeof(T), "InitializePlayer");
            if (original == null)
            {
                RoleLogger.Warn("[Shared]", $"[P1-G] 找不到 {typeof(T).Name}.InitializePlayer");
                return;
            }

            MethodInfo specificPostfix = BitmaskPostfixCache<T>.PostfixMethod;
            if (specificPostfix == null)
            {
                RoleLogger.Error("[Shared]", $"[P1-G] {typeof(T).Name} Postfix 反射失败");
                return;
            }

            harmony.Patch(original, postfix: new HarmonyMethod(specificPostfix));
            RoleLogger.Info("[Shared]",
                $"[P1-G] Bitmask hook registered: {typeof(T).Name}.InitializePlayer (bit {bitIndex})");
        }
    }

    /// <summary>
    /// 泛型缓存容器：每个 T 类型缓存 PropertyInfo 与 Postfix MethodInfo。
    /// 二次审计 Medium-2/Medium-3 修复：避免每次 Postfix 反射。
    /// </summary>
    public static class BitmaskPostfixCache<T>
    {
        public static readonly PropertyInfo PlayerProperty;
        public static readonly MethodInfo PostfixMethod;
        public static readonly int BitIndex;

        static BitmaskPostfixCache()
        {
            try
            {
                PlayerProperty = typeof(T).GetProperty("player");
            }
            catch
            {
                PlayerProperty = null;
            }

            PostfixMethod = typeof(BitmaskPostfixCache<T>).GetMethod(
                nameof(Postfix), BindingFlags.Static | BindingFlags.Public);
            BitIndex = ResolveBitIndex();
        }

        public static void Postfix(object __instance)
        {
            // 第四次审计 P0-2 修复：三重门控，仅客机本地 Player 推进 bitmask
            // 房主侧不参与（房主 Player.IsLocalPlayer=true 但 Provider.isServer=true 会被门控拦截）
            if (Provider.isServer) return;

            Player player = null;

            // 优先从 __instance.player 读取（缓存 PropertyInfo）
            if (PlayerProperty != null && !ReferenceEquals(__instance, null))
            {
                try
                {
                    player = PlayerProperty.GetValue(__instance, null) as Player;
                }
                catch
                {
                    // 早期阶段 player 可能未赋值，fallback 到 Player.LocalPlayer
                }
            }

            // null 兜底：fallback 到 Player.LocalPlayer
            if (ReferenceEquals(player, null))
            {
                try
                {
                    player = Player.LocalPlayer;
                }
                catch
                {
                    // 极早期 Player.LocalPlayer 访问可能异常
                }
            }

            if (ReferenceEquals(player, null))
            {
                RoleLogger.Error("[Client]",
                    $"[P1-G] {typeof(T).Name}.InitializePlayer Postfix: player 仍为 null " +
                    "(PropertyInfo + Player.LocalPlayer 均失败)，bitmask 位 {BitIndex} 未标记。");
                return;
            }

            // 三重门控 2/3：必须是本地 Player
            if (!ReferenceEquals(player, Player.LocalPlayer)) return;
            if (player.channel?.IsLocalPlayer != true) return;

            GameplayReadyTracker.MarkComponentReady(player, BitIndex);
        }

        private static int ResolveBitIndex()
        {
            if (typeof(T) == typeof(PlayerClothing)) return 0;
            if (typeof(T) == typeof(PlayerInventory)) return 1;
            if (typeof(T) == typeof(PlayerLife)) return 2;
            if (typeof(T) == typeof(PlayerStance)) return 3;
            if (typeof(T) == typeof(PlayerMovement)) return 4;
            if (typeof(T) == typeof(PlayerLook)) return 5;
            if (typeof(T) == typeof(PlayerInteract)) return 6;
            if (typeof(T) == typeof(PlayerInput)) return 7;
            return -1;
        }
    }
}
