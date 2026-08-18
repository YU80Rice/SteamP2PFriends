using SDG.Unturned;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SteamP2PFriends.Shared
{
    /// <summary>
    ///
    ///   - 原 RemoteSteamId/LocalJoinSessionId 为全局单值，多客机交错时互相覆盖。
    ///   - 改为按 remoteSteamId 建立 session 字典，每个 session 独立 sid + 首次见时间。
    ///   - 客机端：BeginClientJoin 仍写本地 session（客机只跟一个远端通信）。
    ///   - 主机端：EnsureHostJoinSession 按 remoteSteamId 查/建 session，不再覆盖全局。
    ///   - RemoveClient/dismiss 时调用 RemoveSession(steamId) 清理。
    ///   - 客机 addPlayer 不进入 host session 初始化（加 isServer 门控）。
    ///
    /// 双机对齐 key：remoteSteamId + connection handle + UTC 时间窗口。
    /// </summary>
    internal static class DiagnosticContext
    {
        public class SessionInfo
        {
            public string JoinSessionId;
            public ulong RemoteSteamId;
            public float FirstSeen;
            public int Attempt;
        }

        /// <summary>
        /// 主机端按 remoteSteamId 索引的 session 字典。
        /// 客机端只跟一个远端，仍用 _clientSession。
        /// </summary>
        private static readonly Dictionary<ulong, SessionInfo> _hostSessions =
            new Dictionary<ulong, SessionInfo>();

        /// <summary>客机端本地 session（单一远端）</summary>
        private static SessionInfo _clientSession;

        private static readonly object _idLock = new object();

        /// <summary>当前活跃 session（用于 FormatPrefix 默认输出）</summary>
        public static string LocalJoinSessionId
        {
            get
            {
                lock (_idLock)
                {
                    if (_clientSession != null) return _clientSession.JoinSessionId;
                    if (_hostSessions.Count > 0)
                    {
                        foreach (var kv in _hostSessions) return kv.Value.JoinSessionId;
                    }
                    return "n/a";
                }
            }
        }

        public static ulong RemoteSteamId
        {
            get
            {
                lock (_idLock)
                {
                    if (_clientSession != null) return _clientSession.RemoteSteamId;
                    if (_hostSessions.Count > 0)
                    {
                        foreach (var kv in _hostSessions) return kv.Value.RemoteSteamId;
                    }
                    return 0;
                }
            }
        }

        /// <summary>
        /// 客机发起连接时生成新 session。
        /// </summary>
        public static void BeginClientJoin(ulong remoteSteamId)
        {
            lock (_idLock)
            {
                _clientSession = new SessionInfo
                {
                    JoinSessionId = GenerateId(),
                    RemoteSteamId = remoteSteamId,
                    FirstSeen = Time.realtimeSinceStartup,
                    Attempt = 1
                };
            }
        }

        /// <summary>
        /// 主机首次 addPlayer 时按 remoteSteamId 查/建 session。
        /// </summary>
        public static void EnsureHostJoinSession(ulong remoteSteamId)
        {
            if (!Provider.isServer) return;

            lock (_idLock)
            {
                if (_hostSessions.TryGetValue(remoteSteamId, out SessionInfo existing))
                {
                    existing.Attempt++;
                    return;
                }
                _hostSessions[remoteSteamId] = new SessionInfo
                {
                    JoinSessionId = GenerateId(),
                    RemoteSteamId = remoteSteamId,
                    FirstSeen = Time.realtimeSinceStartup,
                    Attempt = 1
                };
            }
        }

        /// <summary>
        /// 按 remoteSteamId 查询 session（用于日志输出精确 sid）。
        /// </summary>
        public static SessionInfo GetSession(ulong remoteSteamId)
        {
            lock (_idLock)
            {
                if (_clientSession != null && _clientSession.RemoteSteamId == remoteSteamId)
                    return _clientSession;
                if (_hostSessions.TryGetValue(remoteSteamId, out SessionInfo s))
                    return s;
                return null;
            }
        }

        /// <summary>
        /// 按 remoteSteamId 移除 session（RemoveClient/dismiss 时调用）。
        /// </summary>
        public static void RemoveSession(ulong remoteSteamId)
        {
            lock (_idLock)
            {
                if (_clientSession != null && _clientSession.RemoteSteamId == remoteSteamId)
                {
                    _clientSession = null;
                }
                _hostSessions.Remove(remoteSteamId);
            }
        }

        public static void Clear()
        {
            lock (_idLock)
            {
                _clientSession = null;
                _hostSessions.Clear();
            }
        }

        private static string GenerateId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        /// <summary>
        /// 统一格式化诊断日志前缀。
        /// 优先使用按 remoteSteamId 查询的 session；找不到时回退到活跃 session 或 "n/a"。
        /// </summary>
        public static string FormatPrefix(string tag)
        {
            return $"[Diag] sid={LocalJoinSessionId} t={Time.realtimeSinceStartup:F3}s " +
                   $"utc={DateTime.UtcNow:O} remote={RemoteSteamId} {tag}";
        }

        /// <summary>
        /// 按 remoteSteamId 格式化诊断日志前缀（精确 session）。
        /// </summary>
        public static string FormatPrefixFor(ulong remoteSteamId, string tag)
        {
            SessionInfo s = GetSession(remoteSteamId);
            string sid = s?.JoinSessionId ?? "n/a";
            return $"[Diag] sid={sid} t={Time.realtimeSinceStartup:F3}s " +
                   $"utc={DateTime.UtcNow:O} remote={remoteSteamId} {tag}";
        }

        /// <summary>
        /// D-11 限流器：按 异常类型+首栈帧 聚合，首条完整输出，重复项每 5 秒输出累计次数。
        ///   - 重复分支也调用 TryFlush（时间驱动 flush，不再因 return 跳过）。
        ///   - 保留首样本 entry（count=1 也保留），仅 count>1 的 entry 在 flush 时输出 AGGREGATE。
        ///   - flush 后清空 count>1 的 entry，count=1 的保留（避免重复 FIRST）。
        ///   - 重入保护改用 ThreadStatic（线程安全）。
        /// </summary>
        public static class ThrottledLogBridge
        {
            private static readonly object _bridgeLock = new object();

            [ThreadStatic]
            private static int _reentryGuard;

            private static readonly Dictionary<string, AggregatedEntry> _entries =
                new Dictionary<string, AggregatedEntry>();

            private const float FlushIntervalSeconds = 5f;
            private static float _lastFlushTime;

            public static void HandleLog(string logString, string stackTrace, LogType type)
            {
                if (_reentryGuard != 0) return;
                _reentryGuard = 1;
                try
                {
                    string key = BuildKey(logString, stackTrace, type);
                    if (key == null) return;

                    bool isFirst = false;
                    AggregatedEntry entry;
                    lock (_bridgeLock)
                    {
                        if (_entries.TryGetValue(key, out entry))
                        {
                            entry.Count++;
                            entry.LastSeen = Time.realtimeSinceStartup;
                        }
                        else
                        {
                            entry = new AggregatedEntry
                            {
                                Count = 1,
                                FirstSeen = Time.realtimeSinceStartup,
                                LastSeen = Time.realtimeSinceStartup,
                                FirstSample = logString,
                                FirstStackTrace = stackTrace,
                                LogType = type
                            };
                            _entries[key] = entry;
                            isFirst = true;
                        }
                    }

                    if (isFirst)
                    {
                        RoleLogger.Error("[Diag]",
                            $"[UnityBridge] FIRST {type} sid={LocalJoinSessionId} t={Time.realtimeSinceStartup:F3}s " +
                            $"msg={logString}");
                        if (!string.IsNullOrEmpty(stackTrace))
                        {
                            RoleLogger.Error("[Diag]", $"[UnityBridge] stack:\n{stackTrace}");
                        }
                    }

                    TryFlush();
                }
                finally
                {
                    _reentryGuard = 0;
                }
            }

            private static void TryFlush()
            {
                float now = Time.realtimeSinceStartup;
                bool shouldFlush;
                lock (_bridgeLock)
                {
                    shouldFlush = now - _lastFlushTime >= FlushIntervalSeconds && _entries.Count > 0;
                    if (shouldFlush) _lastFlushTime = now;
                }

                if (!shouldFlush) return;

                // 然后立即修改同一 entry 的 Count/FirstSeen，导致输出阶段看到的也是被改写后的值）
                List<FlushSnapshot> snapshot;
                lock (_bridgeLock)
                {
                    snapshot = new List<FlushSnapshot>(_entries.Count);
                    foreach (var kv in _entries)
                    {
                        if (kv.Value.Count > 1)
                        {
                            // 先创建不可变快照（保留原 Count/FirstSeen/LastSeen/FirstSample/LogType）
                            snapshot.Add(new FlushSnapshot
                            {
                                Key = kv.Key,
                                Count = kv.Value.Count,
                                FirstSeen = kv.Value.FirstSeen,
                                LastSeen = kv.Value.LastSeen,
                                FirstSample = kv.Value.FirstSample,
                                LogType = kv.Value.LogType
                            });
                            // 重置原 entry（保留首样本，避免后续相同异常再次作为 FIRST 输出）
                            kv.Value.Count = 1;
                            kv.Value.FirstSeen = now;
                        }
                    }
                }

                if (snapshot.Count == 0) return;

                foreach (var s in snapshot)
                {
                    RoleLogger.Warn("[Diag]",
                        $"[UnityBridge] AGGREGATE {s.LogType} count={s.Count} " +
                        $"firstSeen={s.FirstSeen:F3}s lastSeen={s.LastSeen:F3}s " +
                        $"msg={s.FirstSample}");
                }
            }

            private static string BuildKey(string logString, string stackTrace, LogType type)
            {
                if (string.IsNullOrEmpty(logString)) return null;

                string firstFrame = "";
                if (!string.IsNullOrEmpty(stackTrace))
                {
                    string[] lines = stackTrace.Split('\n');
                    if (lines.Length > 0) firstFrame = lines[0].Trim();
                }

                string msgKey = logString.Length > 120 ? logString.Substring(0, 120) : logString;
                return $"{type}|{msgKey}|{firstFrame}";
            }

            private class AggregatedEntry
            {
                public int Count;
                public float FirstSeen;
                public float LastSeen;
                public string FirstSample;
                public string FirstStackTrace;
                public LogType LogType;
            }

            /// <summary>
            /// 在重置原 entry 前创建新对象，输出阶段看到的是原始 Count/FirstSeen/LastSeen。
            /// </summary>
            private class FlushSnapshot
            {
                public string Key;
                public int Count;
                public float FirstSeen;
                public float LastSeen;
                public string FirstSample;
                public LogType LogType;
            }
        }
    }
}
