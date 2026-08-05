using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Collections.Generic;
using UnityEngine;

namespace SteamP2PFriends.Host
{
    /// <summary>
    /// v0.2.3 P0-E：Player 初始化状态跟踪表。
    ///
    /// 审计员要求：
    ///   - 建立按 Player 实例记录的 initialization state：
    ///     Constructed -> Initializing -> Ready -> Failed
    ///   - 独立状态表，避免向 vanilla 类型写字段。
    ///   - 确认 InitializePlayer 对同一实例只调用一次；重复调用必须记录并阻止。
    ///
    /// 状态转换：
    ///   (无记录) -> Constructed（SteamPlayer 构造完成时）
    ///   Constructed -> Initializing（Player.InitializePlayer Prefix 时）
    ///   Initializing -> Ready（Player.InitializePlayer Postfix 时，无异常）
    ///   Initializing -> Failed（Player.InitializePlayer Finalizer 时，有异常）
    ///   Failed/Ready -> (移除)（SteamPlayer 从 clients 移除时）
    ///
    /// 该表仅用于：
    ///   1. Update/FixedUpdate 护栏（E-2）：未 Ready 的远端实例短路
    ///   2. 重复 Initialize 检测（E-6）
    ///   3. 状态查询（诊断日志）
    /// </summary>
    public enum EPlayerInitState
    {
        /// <summary>尚未记录（新实例首次访问时视为该状态）</summary>
        Unknown,
        /// <summary>SteamPlayer 构造完成，尚未开始 InitializePlayer</summary>
        Constructed,
        /// <summary>Player.InitializePlayer 正在执行</summary>
        Initializing,
        /// <summary>Player.InitializePlayer 成功完成</summary>
        Ready,
        /// <summary>Player.InitializePlayer 抛异常或未完成</summary>
        Failed
    }

    public static class PlayerInitializationTracker
    {
        private static readonly Dictionary<int, EPlayerInitState> _states =
            new Dictionary<int, EPlayerInitState>();

        private static readonly Dictionary<int, bool> _warningLogged =
            new Dictionary<int, bool>();

        private static readonly object _lock = new object();

        /// <summary>
        /// 标记 Player 所在 GameObject 的 instanceId 为 Constructed。
        /// 由 SteamPlayer 构造 Postfix 调用（与 P0-C 同一处）。
        /// </summary>
        public static void MarkConstructed(Player player)
        {
            if (ReferenceEquals(player, null)) return;
            int instanceId = player.GetInstanceID();
            lock (_lock)
            {
                if (_states.TryGetValue(instanceId, out EPlayerInitState existing))
                {
                    // 已存在记录（可能是同 instanceId 复用）
                    if (existing != EPlayerInitState.Constructed)
                    {
                        RoleLogger.Warn("[Host]",
                            $"[P0-E] Player {instanceId} 重复 MarkConstructed: existing={existing} " +
                            $"(可能是 GameObject 复用或重复构造)");
                    }
                    return;
                }
                _states[instanceId] = EPlayerInitState.Constructed;
            }
        }

        /// <summary>
        /// 标记 Player 为 Initializing。
        /// 由 Player.InitializePlayer Prefix 调用。
        /// 返回 true 表示允许继续，false 表示重复调用必须阻止（E-6）。
        /// </summary>
        public static bool TryMarkInitializing(Player player)
        {
            if (ReferenceEquals(player, null)) return true;
            int instanceId = player.GetInstanceID();
            lock (_lock)
            {
                if (_states.TryGetValue(instanceId, out EPlayerInitState existing))
                {
                    if (existing == EPlayerInitState.Initializing)
                    {
                        RoleLogger.Error("[Host]",
                            $"[P0-E] Player {instanceId} 重复 InitializePlayer 调用（正在初始化中）！已阻止。");
                        return false;
                    }
                    if (existing == EPlayerInitState.Ready)
                    {
                        RoleLogger.Warn("[Host]",
                            $"[P0-E] Player {instanceId} 已 Ready，再次调用 InitializePlayer（可能是 vanilla 重初始化）。");
                    }
                    if (existing == EPlayerInitState.Failed)
                    {
                        RoleLogger.Warn("[Host]",
                            $"[P0-E] Player {instanceId} 之前 Failed，重试 InitializePlayer。");
                    }
                }
                _states[instanceId] = EPlayerInitState.Initializing;
                return true;
            }
        }

        /// <summary>
        /// 标记 Player 为 Ready（InitializePlayer 无异常完成）。
        /// 由 Player.InitializePlayer Postfix 调用。
        /// </summary>
        public static void MarkReady(Player player)
        {
            if (ReferenceEquals(player, null)) return;
            int instanceId = player.GetInstanceID();
            lock (_lock)
            {
                _states[instanceId] = EPlayerInitState.Ready;
                _warningLogged[instanceId] = false; // 重置警告标志
            }
        }

        /// <summary>
        /// 标记 Player 为 Failed（InitializePlayer 抛异常）。
        /// 由 Player.InitializePlayer Finalizer 调用。
        /// </summary>
        public static void MarkFailed(Player player)
        {
            if (ReferenceEquals(player, null)) return;
            int instanceId = player.GetInstanceID();
            lock (_lock)
            {
                _states[instanceId] = EPlayerInitState.Failed;
            }
        }

        /// <summary>
        /// 查询 Player 当前状态。
        /// 未记录返回 Unknown。
        /// </summary>
        public static EPlayerInitState GetState(Player player)
        {
            if (ReferenceEquals(player, null)) return EPlayerInitState.Unknown;
            int instanceId = player.GetInstanceID();
            lock (_lock)
            {
                if (_states.TryGetValue(instanceId, out EPlayerInitState state))
                {
                    return state;
                }
                return EPlayerInitState.Unknown;
            }
        }

        /// <summary>
        /// Update/FixedUpdate 护栏（E-2）：
        /// 若 Player 状态为 Constructed/Initializing/Failed，则应短路（return）。
        /// 仅对远端实例生效（isLocalPlayer=false），本地 Player 不护栏。
        /// 按实例只记录一次警告。
        ///
        /// 二次审计 Medium-4 修复：
        ///   - 若状态表无记录（Unknown）且不是本地 Player，在 P2P 模式下防御性短路。
        ///   - 防止 SteamPlayer.ctor Postfix 异常导致状态表缺失时 NRE 洪泛。
        ///   - 若 IsLocalPlayer=true（本地房主 Player），仍放行（本地 Player 不护栏）。
        /// </summary>
        /// <returns>true 表示应短路（return），false 表示放行</returns>
        public static bool ShouldShortCircuitUpdate(Player player)
        {
            if (ReferenceEquals(player, null)) return false;

            // 本地 Player 不护栏
            bool isLocalPlayer = player.channel?.IsLocalPlayer ?? false;
            if (isLocalPlayer) return false;

            int instanceId = player.GetInstanceID();
            EPlayerInitState state;
            lock (_lock)
            {
                if (!_states.TryGetValue(instanceId, out state))
                {
                    // 二次审计 Medium-4 修复：
                    // 状态表无记录且非本地 Player -> 防御性短路
                    // 此场景出现在 SteamPlayer.ctor Postfix 异常未执行 MarkConstructed 时
                    // 宁可多短路几帧也不要让 vanilla Update NRE 洪泛
                    // Player.InitializePlayer Prefix 会兜底 MarkConstructed + TryMarkInitializing
                    if (!_warningLogged.TryGetValue(instanceId, out bool logged) || !logged)
                    {
                        _warningLogged[instanceId] = true;
                        RoleLogger.Warn("[Host]",
                            $"[P0-E] Update/FixedUpdate 防御性护栏: instance={instanceId} state=Unknown " +
                            $"(状态表无记录, 已短路, 等待 InitializePlayer Prefix 兜底)");
                    }
                    return true;
                }

                if (state == EPlayerInitState.Ready)
                {
                    // 已就绪，放行
                    return false;
                }

                // Constructed/Initializing/Failed 状态下短路
                if (!_warningLogged.TryGetValue(instanceId, out bool logged2) || !logged2)
                {
                    _warningLogged[instanceId] = true;
                    RoleLogger.Warn("[Host]",
                        $"[P0-E] Update/FixedUpdate 护栏触发: instance={instanceId} state={state} " +
                        $"(已短路，防止未初始化 Player 产生 NRE 洪泛)");
                }
                return true;
            }
        }

        /// <summary>
        /// 移除 Player 记录（SteamPlayer 从 clients 移除时调用）。
        /// </summary>
        public static void Remove(Player player)
        {
            if (ReferenceEquals(player, null)) return;
            int instanceId = player.GetInstanceID();
            lock (_lock)
            {
                _states.Remove(instanceId);
                _warningLogged.Remove(instanceId);
            }
        }

        /// <summary>
        /// 诊断：输出当前状态表快照。
        /// </summary>
        public static void LogSnapshot()
        {
            lock (_lock)
            {
                RoleLogger.Info("[Host]",
                    $"[P0-E] PlayerInitializationTracker snapshot: total={_states.Count}");
                foreach (var kv in _states)
                {
                    RoleLogger.Info("[Host]",
                        $"[P0-E] instance={kv.Key} state={kv.Value}");
                }
            }
        }
    }
}
