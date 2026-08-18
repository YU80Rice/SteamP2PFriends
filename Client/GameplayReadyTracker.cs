using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Collections.Generic;

namespace SteamP2PFriends.Client
{
    /// <summary>
    ///   - 8 位信号语义为 LocalComponentsInitialized（不单独宣称 GameplayReady）。
    ///   - 完成时回调 P2PJoinManager.NotifyLocalComponentsInitialized，仅记录信号。
    ///   - P2PJoinManager 仅记录 AcceptedAndLocalComponentsInitialized 阶段，不命名真实 GameplayReady。
    ///   - 真实 GameplayReady 由原生 LoadingUI / loading flag 决定，不由插件宣告。
    ///
    /// 跟踪的 8 个关键组件（Player.InitializePlayer 调用顺序，Player.cs:1625-1633）：
    ///   bit 0: PlayerClothing.InitializePlayer
    ///   bit 1: PlayerInventory.InitializePlayer
    ///   bit 2: PlayerLife.InitializePlayer
    ///   bit 3: PlayerStance.InitializePlayer
    ///   bit 5: PlayerLook.InitializePlayer
    ///   bit 6: PlayerInteract.InitializePlayer
    ///   bit 7: PlayerInput.InitializePlayer
    ///
    /// 完成条件：所有 8 位都置 1（mask=0xFF）= LocalComponentsInitialized。
    /// 注意：mask=0xFF 仅证明本地 8 个组件 InitializePlayer 已返回，不能单独证明 GameplayReady。
    /// </summary>
    public static class GameplayReadyTracker
    {
        private const int RequiredMask = 0xFF; // 8 位全 1 = LocalComponentsInitialized
        private const int ComponentCount = 8;

        private static readonly Dictionary<int, int> _bitmasks = new Dictionary<int, int>();
        private static readonly Dictionary<int, bool> _readyLogged = new Dictionary<int, bool>();
        private static readonly object _lock = new object();

        /// <summary>
        /// 标记某个组件的 InitializePlayer 已完成。
        /// componentIndex 范围 [0, 7]，对应上述 8 个组件。
        /// </summary>
        public static void MarkComponentReady(Player player, int componentIndex)
        {
            if (ReferenceEquals(player, null)) return;
            if (componentIndex < 0 || componentIndex >= ComponentCount) return;

            int instanceId = player.GetInstanceID();
            bool becameReady = false;
            int newMask = 0;
            lock (_lock)
            {
                int current = _bitmasks.TryGetValue(instanceId, out int existing) ? existing : 0;
                newMask = current | (1 << componentIndex);
                _bitmasks[instanceId] = newMask;

                if (newMask == RequiredMask)
                {
                    bool alreadyLogged = _readyLogged.TryGetValue(instanceId, out bool logged) && logged;
                    if (!alreadyLogged)
                    {
                        _readyLogged[instanceId] = true;
                        becameReady = true;
                    }
                }
            }

            if (becameReady)
            {
                RoleLogger.Info("[Client]",
                    $"[P1-G] LocalComponentsInitialized bitmask 完成: instanceId={instanceId} " +
                    $"steamId={player.channel?.owner?.playerID?.steamID} mask=0x{newMask:X2}/{RequiredMask:X2} " +
                    $"(仅证明 8 组件 InitializePlayer 已返回，不等于 GameplayReady)");
                P2PJoinManager.NotifyLocalComponentsInitialized();
            }
        }

        /// <summary>
        /// 查询 Player 是否已 LocalComponentsInitialized（所有 8 位都置 1）。
        /// </summary>
        public static bool IsLocalComponentsInitialized(Player player)
        {
            if (ReferenceEquals(player, null)) return false;
            int instanceId = player.GetInstanceID();
            lock (_lock)
            {
                if (!_bitmasks.TryGetValue(instanceId, out int mask))
                {
                    return false;
                }
                return mask == RequiredMask;
            }
        }

        /// <summary>
        /// 获取当前 bitmask（诊断用）。
        /// </summary>
        public static int GetMask(Player player)
        {
            if (ReferenceEquals(player, null)) return 0;
            int instanceId = player.GetInstanceID();
            lock (_lock)
            {
                if (!_bitmasks.TryGetValue(instanceId, out int mask))
                {
                    return 0;
                }
                return mask;
            }
        }

        /// <summary>
        /// 清除 Player 记录（Player.dismiss 或 SteamPlayer 移除时调用）。
        /// </summary>
        public static void Remove(Player player)
        {
            if (ReferenceEquals(player, null)) return;
            int instanceId = player.GetInstanceID();
            lock (_lock)
            {
                _bitmasks.Remove(instanceId);
                _readyLogged.Remove(instanceId);
            }
        }

        /// <summary>
        /// 诊断：输出当前所有 Player 的 bitmask 快照。
        /// </summary>
        public static void LogSnapshot()
        {
            lock (_lock)
            {
                RoleLogger.Info("[Client]",
                    $"[P1-G] GameplayReadyTracker snapshot: total={_bitmasks.Count}");
                foreach (var kv in _bitmasks)
                {
                    RoleLogger.Info("[Client]",
                        $"[P1-G] instance={kv.Key} mask=0x{kv.Value:X2}/{RequiredMask:X2} " +
                        $"localComponentsInit={(kv.Value == RequiredMask ? "YES" : "NO")}");
                }
            }
        }
    }
}
