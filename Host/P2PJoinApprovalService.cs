using SDG.Unturned;
using SteamP2PFriends.Shared;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SteamP2PFriends.Host
{
    // =====================================================================
    // Stage 7-3 v3 待审批服务（Codex 接管蓝图 v3 §3.2）
    // 进程内唯一 internal static class，只维护当前 P2P 会话的待审批集合。
    // 不写第二份持久化名单（蓝图 §5）。
    // =====================================================================
    // v3 重构（P0-REJECT-CAPTURE-AFFINITY-03 修复）：
    //   - Prefix 只调 TryEnqueueRejectedTransportId（不访问 Unity/Provider/whitelist）
    //   - Update 主线程 drain 调 RecordWhitelistRejectedOnMainThread
    //   - volatile 会话 epoch；旧 epoch 一律丢弃
    //   - 队列独立有界上限 32（与 16 可见 pending 分开）
    //   - ThreadUtil.assertIsGameThread() 显式主线程断言
    // =====================================================================

    internal interface IApprovalRuntimeContext
    {
        bool IsActiveP2PHost { get; }
        CSteamID LocalUser { get; }
        float RealtimeSinceStartup { get; }
    }

    internal interface IApprovalWhitelistProxy
    {
        bool Contains(CSteamID target);
        bool TryAdd(CSteamID target, string tag, out string feedback);
    }

    internal readonly struct PendingJoinRequest
    {
        public readonly CSteamID SteamId;
        public readonly float FirstSeenRealtime;
        public readonly float LastSeenRealtime;
        public readonly int AttemptCount;

        public PendingJoinRequest(CSteamID steamId, float now)
        {
            SteamId = steamId;
            FirstSeenRealtime = now;
            LastSeenRealtime = now;
            AttemptCount = 1;
        }

        private PendingJoinRequest(CSteamID steamId, float firstSeen, float lastSeen, int attemptCount)
        {
            SteamId = steamId;
            FirstSeenRealtime = firstSeen;
            LastSeenRealtime = lastSeen;
            AttemptCount = attemptCount;
        }

        public PendingJoinRequest WithNewAttempt(float now)
        {
            return new PendingJoinRequest(SteamId, FirstSeenRealtime, now, AttemptCount + 1);
        }
    }

    internal readonly struct CapturedReject
    {
        public readonly ulong SteamId;
        public readonly int Epoch;

        public CapturedReject(ulong steamId, int epoch)
        {
            SteamId = steamId;
            Epoch = epoch;
        }
    }

    internal sealed class ApprovalRuntimeContext : IApprovalRuntimeContext
    {
        public bool IsActiveP2PHost =>
            HostManager.IsP2PHostMode && Provider.isServer && Provider.isWhitelisted;

        public CSteamID LocalUser => Provider.user;

        public float RealtimeSinceStartup => Time.realtimeSinceStartup;
    }

    internal sealed class ApprovalWhitelistProxy : IApprovalWhitelistProxy
    {
        public bool Contains(CSteamID target)
        {
            if (target == CSteamID.Nil) return false;
            IReadOnlyList<SteamWhitelistID> snapshot = P2PWhitelistService.SnapshotForUi();
            for (int i = 0; i < snapshot.Count; i++)
            {
                if (snapshot[i].steamID.m_SteamID == target.m_SteamID) return true;
            }
            return false;
        }

        public bool TryAdd(CSteamID target, string tag, out string feedback)
        {
            return P2PWhitelistService.TryAdd(target, tag, out feedback);
        }
    }

    internal static class P2PJoinApprovalService
    {
        private static readonly object Sync = new object();
        private static IApprovalRuntimeContext _runtime = new ApprovalRuntimeContext();
        private static IApprovalWhitelistProxy _whitelist = new ApprovalWhitelistProxy();

        private static readonly List<PendingJoinRequest> _pending = new List<PendingJoinRequest>();
        private static readonly HashSet<ulong> _sessionSuppressed = new HashSet<ulong>();

        // v3 P0-REJECT-CAPTURE-AFFINITY-03：Prefix -> 队列 -> 主线程 drain
        private static readonly Queue<CapturedReject> _captureQueue = new Queue<CapturedReject>();
        private static readonly HashSet<ulong> _queuedSteamIds = new HashSet<ulong>();
        private static int _sessionEpoch;

        // v3 测试 hook：bypass ThreadUtil.assertIsGameThread（测试控制台无 gameThread）
        internal static bool _testBypassThreadAssert;

        // v4 [指令 C]：主线程业务契约。RejectForSession/GetPendingRequests/PendingCount/Approve/
        //   Drain/Reset/Record 全部须经此门。测试域经 _testBypassThreadAssert bypass
        //   （InternalsVisibleTo("SteamP2PFriends.WhitelistTests")）。
        private static void AssertBusinessMainThread()
        {
            if (!_testBypassThreadAssert)
            {
                ThreadUtil.assertIsGameThread();
            }
        }

        internal const int MaxPendingEntries = 16;
        internal const int MaxCapturedEntries = 32;
        internal const float MinUpdateIntervalSeconds = 5f;
        internal const float EntryExpirySeconds = 120f;
        private const string ApprovedTag = "APPROVED";

        // ===== v3 Prefix 入口（线程安全，不访问 Unity/Provider/whitelist）=====

        /// <summary>
        /// 蓝图 v3 §3.2：从 Provider.reject Prefix 调用。
        /// 仅投递到队列，不执行业务逻辑。永不抛给原版 reject。
        /// </summary>
        internal static void TryEnqueueRejectedTransportId(ulong steamId)
        {
            if (steamId == 0UL) return;
            int epoch = Volatile.Read(ref _sessionEpoch);
            lock (Sync)
            {
                if (_queuedSteamIds.Contains(steamId)) return;
                if (_captureQueue.Count >= MaxCapturedEntries) return;
                _captureQueue.Enqueue(new CapturedReject(steamId, epoch));
                _queuedSteamIds.Add(steamId);
            }
        }

        /// <summary>
        /// 蓝图 v3 §3.2：仅由 Plugin.Update 主线程调用。
        /// drain 队列，对当前 epoch 的条目调 RecordWhitelistRejectedOnMainThread。
        /// 旧 epoch 一律丢弃。
        /// </summary>
        internal static void DrainCapturedRejectsOnMainThread()
        {
            AssertBusinessMainThread();

            int currentEpoch = Volatile.Read(ref _sessionEpoch);
            while (true)
            {
                CapturedReject captured;
                lock (Sync)
                {
                    if (_captureQueue.Count == 0) return;
                    captured = _captureQueue.Dequeue();
                    _queuedSteamIds.Remove(captured.SteamId);
                }
                if (captured.Epoch != currentEpoch)
                {
                    // 旧 epoch - 丢弃
                    continue;
                }
                RecordWhitelistRejectedOnMainThread(new CSteamID(captured.SteamId));
            }
        }

        // ===== 生命周期（蓝图 v3 §4.7）=====

        internal static void ResetForSession()
        {
            AssertBusinessMainThread();
            Interlocked.Increment(ref _sessionEpoch);
            lock (Sync)
            {
                _pending.Clear();
                _sessionSuppressed.Clear();
                _captureQueue.Clear();
                _queuedSteamIds.Clear();
            }
            RoleLogger.Info("[Host]", "[P2P-Approval] ResetForSession: epoch incremented, all queues cleared");
        }

        internal static void ResetAfterSession()
        {
            AssertBusinessMainThread();
            Interlocked.Increment(ref _sessionEpoch);
            lock (Sync)
            {
                _pending.Clear();
                _sessionSuppressed.Clear();
                _captureQueue.Clear();
                _queuedSteamIds.Clear();
            }
            RoleLogger.Info("[Host]", "[P2P-Approval] ResetAfterSession: epoch incremented, all queues cleared");
        }

        // ===== 主线程业务入口（原 RecordWhitelistRejected，重构为 private）=====

        /// <summary>
        /// 蓝图 v3 §3.2：登记 WHITELISTED 拒绝的 SteamID。
        /// 仅由 DrainCapturedRejectsOnMainThread 调用（主线程）。
        /// Contains/Snapshot、Time、Provider 状态和 pending 业务校验只能在此方法内发生。
        /// </summary>
        private static void RecordWhitelistRejectedOnMainThread(CSteamID steamId)
        {
            AssertBusinessMainThread();

            if (!_runtime.IsActiveP2PHost)
            {
                return;
            }

            if (steamId == CSteamID.Nil || !steamId.IsValid())
            {
                return;
            }

            CSteamID localUser = _runtime.LocalUser;
            if (steamId.m_SteamID == localUser.m_SteamID)
            {
                return;
            }

            try
            {
                if (_whitelist.Contains(steamId))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]", $"[P2P-Approval] Contains check failed: {ex.Message}");
            }

            float now = _runtime.RealtimeSinceStartup;

            lock (Sync)
            {
                if (_sessionSuppressed.Contains(steamId.m_SteamID))
                {
                    return;
                }

                PurgeExpiredUnsafe(now);

                bool found = false;
                for (int i = 0; i < _pending.Count; i++)
                {
                    if (_pending[i].SteamId.m_SteamID == steamId.m_SteamID)
                    {
                        found = true;
                        if (now - _pending[i].LastSeenRealtime < MinUpdateIntervalSeconds)
                        {
                            return;
                        }
                        _pending[i] = _pending[i].WithNewAttempt(now);
                        RoleLogger.Info("[Host]",
                            $"[P2P-Approval] Pending updated: steamId={steamId.m_SteamID}" +
                            $" attempts={_pending[i].AttemptCount}");
                        break;
                    }
                }

                if (found) return;

                if (_pending.Count >= MaxPendingEntries)
                {
                    int oldestIdx = 0;
                    float oldestFirstSeen = _pending[0].FirstSeenRealtime;
                    for (int i = 1; i < _pending.Count; i++)
                    {
                        if (_pending[i].FirstSeenRealtime < oldestFirstSeen)
                        {
                            oldestFirstSeen = _pending[i].FirstSeenRealtime;
                            oldestIdx = i;
                        }
                    }
                    _pending.RemoveAt(oldestIdx);
                }

                _pending.Add(new PendingJoinRequest(steamId, now));
                RoleLogger.Info("[Host]",
                    $"[P2P-Approval] Pending added: steamId={steamId.m_SteamID}");
            }
        }

        // ===== 公开 API（仅主线程调用）=====

        internal static bool Approve(CSteamID steamId, out string feedback)
        {
            AssertBusinessMainThread();

            feedback = "";

            if (!_runtime.IsActiveP2PHost)
            {
                feedback = "当前不是活动 P2P 房主";
                RoleLogger.Warn("[Host]", "[P2P-Approval] Approve rejected: " + feedback);
                return false;
            }

            if (steamId == CSteamID.Nil || !steamId.IsValid())
            {
                feedback = "SteamID 无效";
                RoleLogger.Warn("[Host]", "[P2P-Approval] Approve rejected: " + feedback);
                return false;
            }

            CSteamID localUser = _runtime.LocalUser;
            if (steamId.m_SteamID == localUser.m_SteamID)
            {
                feedback = "不能批准房主自身";
                RoleLogger.Warn("[Host]", "[P2P-Approval] Approve rejected: " + feedback);
                return false;
            }

            bool ok = _whitelist.TryAdd(steamId, ApprovedTag, out feedback);
            if (ok)
            {
                if (P2PQuarantineAdmissionService.IsKnown(steamId))
                {
                    if (!P2PQuarantineAdmissionService.ReleaseAfterPersistentApproval(
                        steamId, out string releaseFailure))
                    {
                        feedback = "白名单已写入，但解除隔离失败：" + releaseFailure;
                        RoleLogger.Error("[Host]",
                            "[P2P-Approval] persisted approval but quarantine release failed: " +
                            releaseFailure);
                        return false;
                    }
                }
                lock (Sync)
                {
                    for (int i = 0; i < _pending.Count; i++)
                    {
                        if (_pending[i].SteamId.m_SteamID == steamId.m_SteamID)
                        {
                            _pending.RemoveAt(i);
                            break;
                        }
                    }
                    _sessionSuppressed.Remove(steamId.m_SteamID);
                }
                RoleLogger.Info("[Host]",
                    $"[P2P-Approval] Approve success: steamId={steamId.m_SteamID}");

                // Stage 10 指令 B: broadcast ONLY after the full transaction committed
                // (TryAdd persisted + ReleaseAfterPersistentApproval success + pending/suppressed
                // cleanup). Any earlier failure returns false above and never broadcasts. A
                // broadcaster exception must not block or change the approval outcome.
                try { P2PWorldStatusBroadcaster.OnPlayerApproved(steamId); }
                catch (Exception bcEx) { RoleLogger.Warn("[Host]", $"[WorldBroadcast] approval notify failed: {bcEx.GetType().Name}"); }

                return true;
            }

            RoleLogger.Warn("[Host]",
                $"[P2P-Approval] Approve failed: steamId={steamId.m_SteamID} feedback={feedback}");
            return false;
        }

        internal static void RejectForSession(CSteamID steamId)
        {
            // v4 [指令 C]：主线程契约断言
            AssertBusinessMainThread();
            if (steamId == CSteamID.Nil || !steamId.IsValid()) return;

            lock (Sync)
            {
                for (int i = 0; i < _pending.Count; i++)
                {
                    if (_pending[i].SteamId.m_SteamID == steamId.m_SteamID)
                    {
                        _pending.RemoveAt(i);
                        break;
                    }
                }
                _sessionSuppressed.Add(steamId.m_SteamID);
            }
            RoleLogger.Info("[Host]",
                $"[P2P-Approval] RejectForSession: steamId={steamId.m_SteamID}");
        }

        internal static IReadOnlyList<PendingJoinRequest> GetPendingRequests()
        {
            // v4 [指令 C]：主线程契约断言
            AssertBusinessMainThread();
            float now = _runtime.RealtimeSinceStartup;
            lock (Sync)
            {
                PurgeExpiredUnsafe(now);
                return _pending.ToArray();
            }
        }

        internal static bool IsSessionSuppressed(CSteamID steamId)
        {
            if (steamId == CSteamID.Nil) return false;
            lock (Sync)
            {
                return _sessionSuppressed.Contains(steamId.m_SteamID);
            }
        }

        internal static int PendingCount
        {
            get
            {
                // v4 [指令 C]：主线程契约断言
                AssertBusinessMainThread();
                float now = _runtime.RealtimeSinceStartup;
                lock (Sync)
                {
                    PurgeExpiredUnsafe(now);
                    return _pending.Count;
                }
            }
        }

        // v3 测试辅助：暴露当前 epoch
        internal static int CurrentEpochForTest => Volatile.Read(ref _sessionEpoch);

        // v3 测试辅助：暴露队列深度
        internal static int CaptureQueueDepthForTest
        {
            get
            {
                lock (Sync) { return _captureQueue.Count; }
            }
        }

        // ===== 私有 =====

        private static void PurgeExpiredUnsafe(float now)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (now - _pending[i].LastSeenRealtime > EntryExpirySeconds)
                {
                    _pending.RemoveAt(i);
                }
            }
        }

        // ===== 测试 hook =====

        internal static IDisposable InstallTestDependencies(
            IApprovalRuntimeContext runtime,
            IApprovalWhitelistProxy whitelist)
        {
            return new TestDependencyScope(runtime, whitelist);
        }

        private sealed class TestDependencyScope : IDisposable
        {
            private readonly IApprovalRuntimeContext _prevRuntime;
            private readonly IApprovalWhitelistProxy _prevWhitelist;
            private readonly bool _prevBypassAssert;
            private readonly int _prevEpoch;
            private bool _disposed;

            internal TestDependencyScope(
                IApprovalRuntimeContext runtime,
                IApprovalWhitelistProxy whitelist)
            {
                _prevRuntime = _runtime;
                _prevWhitelist = _whitelist;
                _prevBypassAssert = _testBypassThreadAssert;
                _prevEpoch = Volatile.Read(ref _sessionEpoch);
                _runtime = runtime;
                _whitelist = whitelist;
                _testBypassThreadAssert = true;
                lock (Sync)
                {
                    _pending.Clear();
                    _sessionSuppressed.Clear();
                    _captureQueue.Clear();
                    _queuedSteamIds.Clear();
                }
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _runtime = _prevRuntime ?? new ApprovalRuntimeContext();
                _whitelist = _prevWhitelist ?? new ApprovalWhitelistProxy();
                _testBypassThreadAssert = _prevBypassAssert;
                Volatile.Write(ref _sessionEpoch, _prevEpoch);
                lock (Sync)
                {
                    _pending.Clear();
                    _sessionSuppressed.Clear();
                    _captureQueue.Clear();
                    _queuedSteamIds.Clear();
                }
            }
        }
    }
}
