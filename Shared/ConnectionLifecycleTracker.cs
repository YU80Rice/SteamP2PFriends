using System.Collections.Generic;
using SteamP2PFriends.Shared;
using Steamworks;
using UnityEngine;

namespace SteamP2PFriends.Shared
{
    /// <summary>
    ///
    ///   - 对每个 handle 建立只读跟踪。
    ///   - 进入 Connecting/FindingRoute 后按 0s/1s/5s/10s/20s/25s 节拍输出
    ///     GetConnectionInfo、RealTimeStatus 和 relay/auth readiness。
    ///   - 进入 Connected/None/终态后立即清理 tracker，避免陈旧 handle 和日志泄漏。
    ///
    /// 严格禁止：
    ///   - 修改 ICE/SDR/认证配置
    ///   - 重复 Accept
    ///   - 完整落盘候选地址
    /// </summary>
    public static class ConnectionLifecycleTracker
    {
        private class TrackerEntry
        {
            public HSteamNetConnection Handle;
            public string Role;
            public string TransportLabel;
            public float EnterConnectingTime;
            public float EnterFindingRouteTime;
            public bool InConnectingOrFindingRoute;
            public int NextSnapshotIndex;
        }

        private static readonly object _lock = new object();
        private static readonly Dictionary<int, TrackerEntry> _entries = new Dictionary<int, TrackerEntry>();
        // 0s/1s/5s/10s/20s/25s 节拍（相对进入 FindingRoute 时刻）
        private static readonly float[] _findingRouteOffsets = new float[] { 0f, 1f, 5f, 10f, 20f, 25f };
        // Connecting 阶段也输出一次 0s 快照（追踪连接发起）
        private const float ConnectingInitialSnapshot = 0f;

        public static void OnConnectionStateChanged(string role, string transportLabel,
            HSteamNetConnection handle, ESteamNetworkingConnectionState oldState,
            ESteamNetworkingConnectionState newState, ulong remoteSteamId)
        {
            try
            {
                int key = (int)handle.m_HSteamNetConnection;
                lock (_lock)
                {
                    // 进入 Connecting：登记新 tracker
                    if (newState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting)
                    {
                        if (!_entries.ContainsKey(key))
                        {
                            var entry = new TrackerEntry
                            {
                                Handle = handle,
                                Role = role,
                                TransportLabel = transportLabel,
                                EnterConnectingTime = Time.realtimeSinceStartup,
                                InConnectingOrFindingRoute = true,
                                NextSnapshotIndex = 0
                            };
                            _entries[key] = entry;
                            RoleLogger.Info(role,
                                $"[Diag] [D-Life] Tracker CREATE handle={key} remote={remoteSteamId} " +
                                $"role={role} label={transportLabel} phase=Connecting " +
                                $"t={entry.EnterConnectingTime:F2}s");
                            // Connecting 0s 快照
                            SnsDiagnosticUtil.SnapshotLiveState(role, transportLabel, handle, "Connecting+0s");
                            SnsDiagnosticUtil.SnapshotRelayAuthReadiness(role, "Connecting+0s");
                        }
                    }
                    // 进入 FindingRoute：更新 entry
                    else if (newState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_FindingRoute)
                    {
                        if (_entries.TryGetValue(key, out var entry))
                        {
                            entry.EnterFindingRouteTime = Time.realtimeSinceStartup;
                            entry.NextSnapshotIndex = 0;
                            RoleLogger.Info(role,
                                $"[Diag] [D-Life] Tracker ENTER FindingRoute handle={key} remote={remoteSteamId} " +
                                $"t={entry.EnterFindingRouteTime:F2}s");
                            // FindingRoute 0s 快照
                            SnsDiagnosticUtil.SnapshotLiveState(role, transportLabel, handle, "FindingRoute+0s");
                            SnsDiagnosticUtil.SnapshotRelayAuthReadiness(role, "FindingRoute+0s");
                            // 推进到下一个节拍
                            entry.NextSnapshotIndex = 1;
                        }
                        else
                        {
                            // 未登记但已 FindingRoute（可能 Connecting 阶段被错过）：补登记
                            var lateEntry = new TrackerEntry
                            {
                                Handle = handle,
                                Role = role,
                                TransportLabel = transportLabel,
                                EnterConnectingTime = Time.realtimeSinceStartup,
                                EnterFindingRouteTime = Time.realtimeSinceStartup,
                                InConnectingOrFindingRoute = true,
                                NextSnapshotIndex = 1
                            };
                            _entries[key] = lateEntry;
                            RoleLogger.Info(role,
                                $"[Diag] [D-Life] Tracker CREATE-LATE (FindingRoute missed Connecting) handle={key} remote={remoteSteamId}");
                            SnsDiagnosticUtil.SnapshotLiveState(role, transportLabel, handle, "FindingRoute+0s(late)");
                            SnsDiagnosticUtil.SnapshotRelayAuthReadiness(role, "FindingRoute+0s(late)");
                        }
                    }
                    // 进入终态或 Connected：清理 tracker
                    else if (newState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected
                        || newState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer
                        || newState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally
                        || newState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_None
                        || newState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_FinWait
                        || newState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Linger)
                    {
                        if (_entries.TryGetValue(key, out var entry))
                        {
                            float now = Time.realtimeSinceStartup;
                            float connectingDur = now - entry.EnterConnectingTime;
                            float findingRouteDur = entry.EnterFindingRouteTime > 0f ? now - entry.EnterFindingRouteTime : -1f;
                            RoleLogger.Info(role,
                                $"[Diag] [D-Life] Tracker CLOSE handle={key} newState={newState} " +
                                $"connectingDur={connectingDur:F2}s findingRouteDur={findingRouteDur:F2}s");
                            _entries.Remove(key);
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn(role, $"[Diag] [D-Life] OnConnectionStateChanged 异常（不阻断）: {ex.Message}");
            }
        }

        /// <summary>
        /// Plugin.Update 每帧调用：检查是否到了下一个快照节拍。
        /// </summary>
        public static void Tick()
        {
            try
            {
                float now = Time.realtimeSinceStartup;
                List<int> toRemove = null;
                lock (_lock)
                {
                    foreach (var kv in _entries)
                    {
                        var entry = kv.Value;
                        if (!entry.InConnectingOrFindingRoute) continue;
                        if (entry.EnterFindingRouteTime <= 0f) continue; // 还在 Connecting，未进 FindingRoute
                        if (entry.NextSnapshotIndex >= _findingRouteOffsets.Length) continue;
                        float nextOffset = _findingRouteOffsets[entry.NextSnapshotIndex];
                        if (now - entry.EnterFindingRouteTime >= nextOffset)
                        {
                            try
                            {
                                string phase = $"FindingRoute+{nextOffset:F0}s";
                                SnsDiagnosticUtil.SnapshotLiveState(entry.Role, entry.TransportLabel, entry.Handle, phase);
                                // 仅在 5s/10s/20s/25s 节拍同时输出 relay readiness（减少噪声）
                                if (nextOffset >= 5f)
                                {
                                    SnsDiagnosticUtil.SnapshotRelayAuthReadiness(entry.Role, phase);
                                }
                            }
                            catch (System.Exception ex)
                            {
                                RoleLogger.Warn(entry.Role, $"[Diag] [D-Life] Tick snapshot 异常（不阻断）: {ex.Message}");
                            }
                            entry.NextSnapshotIndex++;
                        }
                    }
                }
                if (toRemove != null)
                {
                    lock (_lock)
                    {
                        foreach (int key in toRemove)
                        {
                            _entries.Remove(key);
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Shared]", $"[Diag] [D-Life] Tick 异常（不阻断）: {ex.Message}");
            }
        }

        /// <summary>清除所有 tracker（应用退出时调用）。</summary>
        public static void Shutdown()
        {
            lock (_lock)
            {
                _entries.Clear();
            }
        }

        /// <summary>
        /// 强制对当前所有活跃 handle 输出一次 live snapshot。
        /// 用于在 vanilla teardown 前捕获 SNS 连接状态。
        /// </summary>
        public static void SnapshotAllForced(string role, string occasion)
        {
            try
            {
                lock (_lock)
                {
                    if (_entries.Count == 0)
                    {
                        RoleLogger.Info(role,
                            $"[Diag] [D-Life] SnapshotAllForced {occasion}: no active tracker entries");
                        return;
                    }
                    foreach (var kv in _entries)
                    {
                        var entry = kv.Value;
                        try
                        {
                            SnsDiagnosticUtil.SnapshotLiveState(entry.Role, entry.TransportLabel, entry.Handle,
                                $"Forced({occasion})");
                        }
                        catch (System.Exception ex)
                        {
                            RoleLogger.Warn(role, $"[Diag] [D-Life] SnapshotAllForced 单项异常（不阻断）: {ex.Message}");
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn(role, $"[Diag] [D-Life] SnapshotAllForced 异常（不阻断）: {ex.Message}");
            }
        }
    }
}
