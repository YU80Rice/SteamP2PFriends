using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    ///
    ///
    /// 审计要求（§9.1 修正 4 + §5.3）：
    ///   - 追踪客机端 PlayerLifecycleDesc.ready 状态变化
    ///   - 记录就绪时机、bitmask 变化、LocalPlayerCreated 事件因果关系
    ///   - 若 ready=True 但事件未触发：确认 vanilla 事件触发条件有隐藏依赖
    ///
    /// U3-SDK 源码核验结果：
    ///   - vanilla Player.cs 无 `LocalPlayerCreated` 事件
    ///   - 仅有 `onPlayerCreated`（PlayerCreated 委托，Player.cs:192，由 PlayerQuests.InitializePlayer L2620 触发）
    ///   - PlayerLifecycleDesc 在 U3-SDK 源码中未找到（可能 internal 或未导出）
    ///
    /// 替代方案（本 patch 实现）：
    ///   1. Postfix Player.InitializePlayer，记录关键状态：
    ///      - Player.isLoadingClothing / isLoadingInventory / isLoadingLife static 字段
    ///      - Player.LocalPlayer != null
    ///      - GameplayReadyTracker.GetMask(player)（插件自己的 bitmask）
    ///      - channel.IsLocalPlayer
    ///   2. 订阅 Player.onPlayerCreated 事件，记录触发时机与 Player 关键状态
    ///   3. Postfix PlayerQuests.InitializePlayer（最后调用的组件），验证 onPlayerCreated 触发
    ///
    /// 诊断目标：
    ///   - 定位 bitmask=0xFF 但 LocalPlayerCreated 信号未触发的"隐藏条件"
    ///   - 验证 onPlayerCreated 事件是否在客机端正常触发
    ///   - 验证 Player.isLoadingClothing 生命周期与 onPlayerCreated 的因果关系
    ///
    /// 严格禁止：
    ///   - 修改原方法参数或返回值
    ///   - 修改 vanilla 事件订阅
    ///   - 在事件回调中抛异常
    /// </summary>
    public static class PlayerLifecycleReadyDiagnosticPatch
    {
        public static bool DVis17_InitPlayer_Registered { get; private set; }
        public static bool DVis17_QuestsInit_Registered { get; private set; }
        public static bool DVis17_OnPlayerCreated_Subscribed { get; private set; }

        public static bool AllRegistrationsSucceeded =>
            DVis17_InitPlayer_Registered && DVis17_QuestsInit_Registered && DVis17_OnPlayerCreated_Subscribed;

        public static bool RegisterManual(Harmony harmony)
        {
            DVis17_InitPlayer_Registered = RegisterDVis17_InitializePlayer(harmony);
            DVis17_QuestsInit_Registered = RegisterDVis17_QuestsInitializePlayer(harmony);
            DVis17_OnPlayerCreated_Subscribed = SubscribeOnPlayerCreated();

            RoleLogger.Info("[Shared]",
                $"[D-Vis] PlayerLifecycleReadyDiagnosticPatch 汇总: " +
                $"InitPlayer={DVis17_InitPlayer_Registered} " +
                $"QuestsInit={DVis17_QuestsInit_Registered} " +
                $"OnPlayerCreated={DVis17_OnPlayerCreated_Subscribed}");

            return AllRegistrationsSucceeded;
        }

        private static bool RegisterDVis17_InitializePlayer(Harmony harmony)
        {
            const string Label = "D-Vis-17 Player.InitializePlayer Ready State";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(Player), "InitializePlayer");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[D-Vis-17] !!! {Label} 反射失败");
                    return false;
                }
                MethodInfo postfix = typeof(Hooks).GetMethod(nameof(Hooks.InitializePlayerReadyPostfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, postfix: new HarmonyMethod(postfix));

                // 验证与 D-Vis-12/D-Vis-13 共存
                HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
                int postfixCount = info?.Postfixes?.Count ?? 0;
                RoleLogger.Info("[Shared]",
                    $"[D-Vis-17] OK {Label} 已登记 (Postfix)。当前 InitializePlayer postfixes={postfixCount} " +
                    $"(含 D-Vis-12/D-Vis-13/D-Vis-17)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[D-Vis-17] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        private static bool RegisterDVis17_QuestsInitializePlayer(Harmony harmony)
        {
            const string Label = "D-Vis-17 PlayerQuests.InitializePlayer";
            try
            {
                // PlayerQuests.InitializePlayer 是 onPlayerCreated 的触发点（PlayerQuests.cs:2620）
                MethodInfo original = AccessTools.Method(typeof(PlayerQuests), "InitializePlayer");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[D-Vis-17] !!! {Label} 反射失败");
                    return false;
                }
                MethodInfo postfix = typeof(Hooks).GetMethod(nameof(Hooks.QuestsInitializePlayerPostfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, postfix: new HarmonyMethod(postfix));

                RoleLogger.Info("[Shared]", $"[D-Vis-17] OK {Label} 已登记 (Postfix)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[D-Vis-17] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        private static bool SubscribeOnPlayerCreated()
        {
            try
            {
                // Player.onPlayerCreated 是 public static PlayerCreated 委托
                // PlayerCreated 的签名：void(Player player)
                Player.onPlayerCreated += OnPlayerCreatedHandler;
                RoleLogger.Info("[Shared]", "[D-Vis-17] OK Player.onPlayerCreated 已订阅");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[D-Vis-17] !!! Player.onPlayerCreated 订阅异常: {ex.Message}");
                return false;
            }
        }

        // ====================== Hooks ======================

        private static class Hooks
        {
            /// <summary>
            /// Postfix Player.InitializePlayer。记录关键就绪状态。
            /// </summary>
            internal static void InitializePlayerReadyPostfix(Player __instance)
            {
                try
                {
                    if (!ShouldLogDVis()) return;

                    ulong steamId = __instance?.channel?.owner?.playerID?.steamID.m_SteamID ?? 0UL;
                    bool isLocalPlayer = __instance?.channel?.IsLocalPlayer ?? false;
                    bool isServer = Provider.isServer;

                    // 关键状态字段
                    bool isLoadingClothing = Player.isLoadingClothing;
                    bool isLoadingInventory = Player.isLoadingInventory;
                    bool isLoadingLife = Player.isLoadingLife;
                    bool localPlayerExists = !ReferenceEquals(Player.LocalPlayer, null);
                    int bitmask = SteamP2PFriends.Client.GameplayReadyTracker.GetMask(__instance);
                    bool localComponentsInit = (bitmask == 0xFF);

                    RoleLogger.Info("[Shared]",
                        $"[D-Vis-17] InitializePlayer Postfix steamId={DiagnosticMaskUtil.MaskSteamId(steamId)} " +
                        $"isLocalPlayer={isLocalPlayer} isServer={isServer} " +
                        $"loading(static): cloth={isLoadingClothing} inv={isLoadingInventory} life={isLoadingLife} | " +
                        $"LocalPlayer_exists={localPlayerExists} bitmask=0x{bitmask:X2} " +
                        $"localComponentsInit={localComponentsInit}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Shared]", $"[D-Vis-17] InitializePlayer Postfix 异常（不阻断）: {ex.Message}");
                }
            }

            /// <summary>
            /// Postfix PlayerQuests.InitializePlayer。PlayerQuests 是最后一个调用 InitializePlayer 的组件，
            /// 且 PlayerQuests.InitializePlayer L2620 触发 onPlayerCreated 事件。
            /// 本 Postfix 在事件触发后执行，可验证事件是否真的触发了。
            /// </summary>
            internal static void QuestsInitializePlayerPostfix(PlayerQuests __instance)
            {
                try
                {
                    if (!ShouldLogDVis()) return;

                    Player player = __instance?.player;
                    if (ReferenceEquals(player, null)) return;

                    ulong steamId = player.channel?.owner?.playerID?.steamID.m_SteamID ?? 0UL;
                    bool isLocalPlayer = player.channel?.IsLocalPlayer ?? false;
                    bool isLoadingClothing = Player.isLoadingClothing;
                    int bitmask = SteamP2PFriends.Client.GameplayReadyTracker.GetMask(player);

                    RoleLogger.Info("[Shared]",
                        $"[D-Vis-17] PlayerQuests.InitializePlayer Postfix steamId={DiagnosticMaskUtil.MaskSteamId(steamId)} " +
                        $"isLocalPlayer={isLocalPlayer} isLoadingClothing(static)={isLoadingClothing} " +
                        $"bitmask=0x{bitmask:X2} (onPlayerCreated 事件应已触发)");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Shared]", $"[D-Vis-17] PlayerQuests Postfix 异常（不阻断）: {ex.Message}");
                }
            }
        }

        // ====================== 事件回调 ======================

        private static void OnPlayerCreatedHandler(Player player)
        {
            try
            {
                if (!ShouldLogDVis()) return;
                if (ReferenceEquals(player, null)) return;

                ulong steamId = player.channel?.owner?.playerID?.steamID.m_SteamID ?? 0UL;
                bool isLocalPlayer = player.channel?.IsLocalPlayer ?? false;
                bool isLoadingClothing = Player.isLoadingClothing;
                bool isLoadingInventory = Player.isLoadingInventory;
                bool isLoadingLife = Player.isLoadingLife;
                int bitmask = SteamP2PFriends.Client.GameplayReadyTracker.GetMask(player);

                RoleLogger.Info("[Shared]",
                    $"[D-Vis-17] >>>> onPlayerCreated EVENT FIRED steamId={DiagnosticMaskUtil.MaskSteamId(steamId)} " +
                    $"isLocalPlayer={isLocalPlayer} " +
                    $"loading(static): cloth={isLoadingClothing} inv={isLoadingInventory} life={isLoadingLife} | " +
                    $"bitmask=0x{bitmask:X2} (此时若 bitmask=0xFF 且 isLoadingClothing=False，LocalPlayerCreated 应可触发)");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Shared]", $"[D-Vis-17] onPlayerCreated 回调异常（不阻断）: {ex.Message}");
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

        /// <summary>
        /// </summary>
        public static void Shutdown()
        {
            try
            {
                Player.onPlayerCreated -= OnPlayerCreatedHandler;
                RoleLogger.Info("[Shared]", "[D-Vis-17] Player.onPlayerCreated 已反订阅");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Shared]", $"[D-Vis-17] Shutdown 反订阅异常（不阻断）: {ex.Message}");
            }
        }
    }
}
