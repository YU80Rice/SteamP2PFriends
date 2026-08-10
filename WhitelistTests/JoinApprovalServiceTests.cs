using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using SteamP2PFriends.UI;
using SteamP2PFriends.WhitelistTests.Fakes;
using Steamworks;
using System;
using System.Threading;

namespace SteamP2PFriends.WhitelistTests
{
    /// <summary>
    /// Stage 7-3 v3 纯单元测试：P2PJoinApprovalService。
    /// 蓝图 v3 §3.2 + §4[指令 D]：
    ///   - A1-A8：原 v2 八场景，统一改用 TryEnqueueRejectedTransportId + DrainCapturedRejectsOnMainThread
    ///   - A9-A14：v3 新增 epoch/queue/cross-thread/Reset-discards/UI-no-create/parent-rebuild/Harmony-metadata
    /// 不启动 Unturned、不触碰 Provider/Steam API/Unity/文件系统。
    /// </summary>
    internal static class JoinApprovalServiceTests
    {
        // 测试用 SteamID（合法的 Individual 账号）
        private static readonly CSteamID HostId = new CSteamID(76561199030780228UL);
        private static readonly CSteamID ClientId = new CSteamID(76561199721762479UL);
        private static readonly CSteamID ClientId2 = new CSteamID(76561198000000001UL);

        // v3 辅助：模拟 Prefix -> 主线程 drain 的完整路径
        private static void EnqueueAndDrain(CSteamID steamId)
        {
            P2PJoinApprovalService.TryEnqueueRejectedTransportId(steamId.m_SteamID);
            P2PJoinApprovalService.DrainCapturedRejectsOnMainThread();
        }

        // ===== 1. 首次 WHITELISTED ID 登记 =====
        internal static bool Test_Record_FirstRejection_Registers()
        {
            var runtime = new FakeApprovalRuntimeContext
            {
                IsActiveP2PHostValue = true,
                LocalUserValue = HostId,
                RealtimeValue = 1000f
            };
            var whitelist = new FakeApprovalWhitelistProxy { ContainsResult = false };

            using (P2PJoinApprovalService.InstallTestDependencies(runtime, whitelist))
            {
                P2PJoinApprovalService.ResetForSession();
                EnqueueAndDrain(ClientId);

                var pending = P2PJoinApprovalService.GetPendingRequests();
                if (pending.Count != 1) return Fail("pending should have 1 entry", "count=" + pending.Count);
                if (pending[0].SteamId.m_SteamID != ClientId.m_SteamID) return Fail("SteamId mismatch", pending[0].SteamId.m_SteamID.ToString());
                if (pending[0].AttemptCount != 1) return Fail("AttemptCount should be 1", "count=" + pending[0].AttemptCount);
                if (pending[0].FirstSeenRealtime != 1000f) return Fail("FirstSeen should be 1000", "firstSeen=" + pending[0].FirstSeenRealtime);
                if (whitelist.ContainsCallCount != 1) return Fail("Contains should be called once", "count=" + whitelist.ContainsCallCount);
            }
            return true;
        }

        // ===== 2. 无效/Nil/房主自身/非 P2P/白名单未启用时不登记 =====
        internal static bool Test_Record_InvalidConditions_NotRegistered()
        {
            // 2a: 非 P2P 房主
            {
                var runtime = new FakeApprovalRuntimeContext
                {
                    IsActiveP2PHostValue = false,
                    LocalUserValue = HostId,
                    RealtimeValue = 1000f
                };
                var whitelist = new FakeApprovalWhitelistProxy { ContainsResult = false };

                using (P2PJoinApprovalService.InstallTestDependencies(runtime, whitelist))
                {
                    P2PJoinApprovalService.ResetForSession();
                    EnqueueAndDrain(ClientId);

                    if (P2PJoinApprovalService.PendingCount != 0) return Fail("non-P2P-host should not register", "count=" + P2PJoinApprovalService.PendingCount);
                    if (whitelist.ContainsCallCount != 0) return Fail("Contains should not be called when not active host", "count=" + whitelist.ContainsCallCount);
                }
            }

            // 2b: Nil SteamID
            {
                var runtime = new FakeApprovalRuntimeContext
                {
                    IsActiveP2PHostValue = true,
                    LocalUserValue = HostId,
                    RealtimeValue = 1000f
                };
                var whitelist = new FakeApprovalWhitelistProxy { ContainsResult = false };

                using (P2PJoinApprovalService.InstallTestDependencies(runtime, whitelist))
                {
                    P2PJoinApprovalService.ResetForSession();
                    // Nil 在 TryEnqueue 阶段就被过滤（steamId == 0UL）
                    P2PJoinApprovalService.TryEnqueueRejectedTransportId(0UL);
                    P2PJoinApprovalService.DrainCapturedRejectsOnMainThread();

                    if (P2PJoinApprovalService.PendingCount != 0) return Fail("Nil SteamID should not register", "count=" + P2PJoinApprovalService.PendingCount);
                }
            }

            // 2c: 房主自身
            {
                var runtime = new FakeApprovalRuntimeContext
                {
                    IsActiveP2PHostValue = true,
                    LocalUserValue = HostId,
                    RealtimeValue = 1000f
                };
                var whitelist = new FakeApprovalWhitelistProxy { ContainsResult = false };

                using (P2PJoinApprovalService.InstallTestDependencies(runtime, whitelist))
                {
                    P2PJoinApprovalService.ResetForSession();
                    EnqueueAndDrain(HostId);

                    if (P2PJoinApprovalService.PendingCount != 0) return Fail("host self should not register", "count=" + P2PJoinApprovalService.PendingCount);
                }
            }

            // 2d: 已在白名单中
            {
                var runtime = new FakeApprovalRuntimeContext
                {
                    IsActiveP2PHostValue = true,
                    LocalUserValue = HostId,
                    RealtimeValue = 1000f
                };
                var whitelist = new FakeApprovalWhitelistProxy { ContainsResult = true };

                using (P2PJoinApprovalService.InstallTestDependencies(runtime, whitelist))
                {
                    P2PJoinApprovalService.ResetForSession();
                    EnqueueAndDrain(ClientId);

                    if (P2PJoinApprovalService.PendingCount != 0) return Fail("already-whitelisted should not register", "count=" + P2PJoinApprovalService.PendingCount);
                }
            }
            return true;
        }

        // ===== 3. 同 ID 去重和 AttemptCount =====
        internal static bool Test_Record_DedupAndAttemptCount()
        {
            var runtime = new FakeApprovalRuntimeContext
            {
                IsActiveP2PHostValue = true,
                LocalUserValue = HostId,
                RealtimeValue = 1000f
            };
            var whitelist = new FakeApprovalWhitelistProxy { ContainsResult = false };

            using (P2PJoinApprovalService.InstallTestDependencies(runtime, whitelist))
            {
                P2PJoinApprovalService.ResetForSession();

                // 第一次登记
                EnqueueAndDrain(ClientId);
                if (P2PJoinApprovalService.PendingCount != 1) return Fail("first register should have 1", "count=" + P2PJoinApprovalService.PendingCount);

                // 5 秒后再次登记 - 应更新 AttemptCount
                runtime.AdvanceTime(6f);
                EnqueueAndDrain(ClientId);

                var pending = P2PJoinApprovalService.GetPendingRequests();
                if (pending.Count != 1) return Fail("should still have 1 entry (dedup)", "count=" + pending.Count);
                if (pending[0].AttemptCount != 2) return Fail("AttemptCount should be 2", "count=" + pending[0].AttemptCount);
                if (pending[0].FirstSeenRealtime != 1000f) return Fail("FirstSeen should remain 1000", "firstSeen=" + pending[0].FirstSeenRealtime);
                if (pending[0].LastSeenRealtime != 1006f) return Fail("LastSeen should be 1006", "lastSeen=" + pending[0].LastSeenRealtime);
            }
            return true;
        }

        // ===== 4. 5 秒限频、16 条上限、120 秒过期 =====
        internal static bool Test_Record_RateLimit_Cap_Expiry()
        {
            // 4a: 5 秒限频
            {
                var runtime = new FakeApprovalRuntimeContext
                {
                    IsActiveP2PHostValue = true,
                    LocalUserValue = HostId,
                    RealtimeValue = 1000f
                };
                var whitelist = new FakeApprovalWhitelistProxy { ContainsResult = false };

                using (P2PJoinApprovalService.InstallTestDependencies(runtime, whitelist))
                {
                    P2PJoinApprovalService.ResetForSession();
                    EnqueueAndDrain(ClientId);

                    // 4 秒后再次 - 应被限频
                    runtime.AdvanceTime(4f);
                    EnqueueAndDrain(ClientId);

                    var pending = P2PJoinApprovalService.GetPendingRequests();
                    if (pending.Count != 1) return Fail("rate-limit should keep 1 entry", "count=" + pending.Count);
                    if (pending[0].AttemptCount != 1) return Fail("AttemptCount should remain 1 (rate-limited)", "count=" + pending[0].AttemptCount);
                    if (pending[0].LastSeenRealtime != 1000f) return Fail("LastSeen should remain 1000 (rate-limited)", "lastSeen=" + pending[0].LastSeenRealtime);
                }
            }

            // 4b: 16 条上限
            {
                var runtime = new FakeApprovalRuntimeContext
                {
                    IsActiveP2PHostValue = true,
                    LocalUserValue = HostId,
                    RealtimeValue = 1000f
                };
                var whitelist = new FakeApprovalWhitelistProxy { ContainsResult = false };

                using (P2PJoinApprovalService.InstallTestDependencies(runtime, whitelist))
                {
                    P2PJoinApprovalService.ResetForSession();

                    // 登记 16 个不同 ID
                    for (ulong i = 1; i <= 16; i++)
                    {
                        EnqueueAndDrain(new CSteamID(76561198000000000UL + i));
                        runtime.AdvanceTime(6f); // 跳过限频
                    }
                    if (P2PJoinApprovalService.PendingCount != 16) return Fail("should have 16 entries at cap", "count=" + P2PJoinApprovalService.PendingCount);

                    // 第 17 个 - 应驱逐最旧
                    EnqueueAndDrain(new CSteamID(76561198000000017UL));
                    if (P2PJoinApprovalService.PendingCount != 16) return Fail("should still have 16 entries (cap)", "count=" + P2PJoinApprovalService.PendingCount);

                    // 第一个应该被驱逐
                    var pending = P2PJoinApprovalService.GetPendingRequests();
                    bool hasFirst = false;
                    for (int i = 0; i < pending.Count; i++)
                    {
                        if (pending[i].SteamId.m_SteamID == 76561198000000001UL)
                        {
                            hasFirst = true;
                            break;
                        }
                    }
                    if (hasFirst) return Fail("oldest entry should be evicted", "first still present");
                }
            }

            // 4c: 120 秒过期
            {
                var runtime = new FakeApprovalRuntimeContext
                {
                    IsActiveP2PHostValue = true,
                    LocalUserValue = HostId,
                    RealtimeValue = 1000f
                };
                var whitelist = new FakeApprovalWhitelistProxy { ContainsResult = false };

                using (P2PJoinApprovalService.InstallTestDependencies(runtime, whitelist))
                {
                    P2PJoinApprovalService.ResetForSession();
                    EnqueueAndDrain(ClientId);
                    if (P2PJoinApprovalService.PendingCount != 1) return Fail("should have 1 before expiry", "count=" + P2PJoinApprovalService.PendingCount);

                    // 121 秒后查询 - 应被清理
                    runtime.AdvanceTime(121f);
                    if (P2PJoinApprovalService.PendingCount != 0) return Fail("should be expired after 121s", "count=" + P2PJoinApprovalService.PendingCount);
                }
            }
            return true;
        }

        // ===== 5. session reject 后不再弹出；新 session reset 后可重新登记 =====
        internal static bool Test_RejectForSession_BlocksUntilReset()
        {
            var runtime = new FakeApprovalRuntimeContext
            {
                IsActiveP2PHostValue = true,
                LocalUserValue = HostId,
                RealtimeValue = 1000f
            };
            var whitelist = new FakeApprovalWhitelistProxy { ContainsResult = false };

            using (P2PJoinApprovalService.InstallTestDependencies(runtime, whitelist))
            {
                P2PJoinApprovalService.ResetForSession();
                EnqueueAndDrain(ClientId);
                if (P2PJoinApprovalService.PendingCount != 1) return Fail("should have 1 after register", "count=" + P2PJoinApprovalService.PendingCount);

                // 拒绝
                P2PJoinApprovalService.RejectForSession(ClientId);
                if (P2PJoinApprovalService.PendingCount != 0) return Fail("should have 0 after reject", "count=" + P2PJoinApprovalService.PendingCount);
                if (!P2PJoinApprovalService.IsSessionSuppressed(ClientId)) return Fail("should be in suppressed set", "not suppressed");

                // 再次尝试登记 - 应被抑制
                runtime.AdvanceTime(6f);
                EnqueueAndDrain(ClientId);
                if (P2PJoinApprovalService.PendingCount != 0) return Fail("should remain 0 (session-suppressed)", "count=" + P2PJoinApprovalService.PendingCount);

                // 新 session reset 后可重新登记
                P2PJoinApprovalService.ResetForSession();
                if (P2PJoinApprovalService.IsSessionSuppressed(ClientId)) return Fail("should not be suppressed after reset", "still suppressed");
                EnqueueAndDrain(ClientId);
                if (P2PJoinApprovalService.PendingCount != 1) return Fail("should have 1 after new session register", "count=" + P2PJoinApprovalService.PendingCount);
            }
            return true;
        }

        // ===== 6. approve 成功移除 pending =====
        internal static bool Test_Approve_Success_RemovesFromPending()
        {
            var runtime = new FakeApprovalRuntimeContext
            {
                IsActiveP2PHostValue = true,
                LocalUserValue = HostId,
                RealtimeValue = 1000f
            };
            var whitelist = new FakeApprovalWhitelistProxy
            {
                ContainsResult = false,
                TryAddResult = true,
                TryAddFeedback = "添加成功"
            };

            using (P2PJoinApprovalService.InstallTestDependencies(runtime, whitelist))
            {
                P2PJoinApprovalService.ResetForSession();
                EnqueueAndDrain(ClientId);
                if (P2PJoinApprovalService.PendingCount != 1) return Fail("should have 1 pending", "count=" + P2PJoinApprovalService.PendingCount);

                bool ok = P2PJoinApprovalService.Approve(ClientId, out string feedback);
                if (!ok) return Fail("Approve should succeed", feedback);
                if (whitelist.TryAddCallCount != 1) return Fail("TryAdd should be called once", "count=" + whitelist.TryAddCallCount);
                if (whitelist.LastTryAddTarget.m_SteamID != ClientId.m_SteamID) return Fail("TryAdd target mismatch", whitelist.LastTryAddTarget.m_SteamID.ToString());
                if (whitelist.LastTryAddTag != "APPROVED") return Fail("TryAdd tag should be APPROVED", whitelist.LastTryAddTag);
                if (P2PJoinApprovalService.PendingCount != 0) return Fail("pending should be empty after approve", "count=" + P2PJoinApprovalService.PendingCount);
                if (P2PJoinApprovalService.IsSessionSuppressed(ClientId)) return Fail("should not be suppressed after approve", "still suppressed");
            }
            return true;
        }

        // ===== 7. approve 保存/复读失败时不假称成功，并继承 disconnect once =====
        internal static bool Test_Approve_Failure_DoesNotRemove()
        {
            var runtime = new FakeApprovalRuntimeContext
            {
                IsActiveP2PHostValue = true,
                LocalUserValue = HostId,
                RealtimeValue = 1000f
            };
            var whitelist = new FakeApprovalWhitelistProxy
            {
                ContainsResult = false,
                TryAddResult = false,
                TryAddFeedback = "添加失败，已断开 P2P 会话以保护存档：InvalidOperationException"
            };

            using (P2PJoinApprovalService.InstallTestDependencies(runtime, whitelist))
            {
                P2PJoinApprovalService.ResetForSession();
                EnqueueAndDrain(ClientId);
                if (P2PJoinApprovalService.PendingCount != 1) return Fail("should have 1 pending", "count=" + P2PJoinApprovalService.PendingCount);

                bool ok = P2PJoinApprovalService.Approve(ClientId, out string feedback);
                if (ok) return Fail("Approve should fail", "returned true");
                if (whitelist.TryAddCallCount != 1) return Fail("TryAdd should be called once", "count=" + whitelist.TryAddCallCount);
                // 失败时不从 pending 移除（蓝图 §3.2：让房主能看到失败状态）
                if (P2PJoinApprovalService.PendingCount != 1) return Fail("pending should remain 1 after failure", "count=" + P2PJoinApprovalService.PendingCount);
                // feedback 应传递 TryAdd 的反馈
                if (!feedback.Contains("添加失败")) return Fail("feedback should contain failure", feedback);
            }
            return true;
        }

        // ===== 8. capture patch 异常不阻断原版 reject（服务层模拟）=====
        // 蓝图 v3 §3.3：capture patch Prefix 异常只安全记录，原版拒绝必须继续。
        // 此测试模拟 Contains 抛异常时，主线程 drain 不应抛出。
        internal static bool Test_Record_ContainsException_DoesNotThrow()
        {
            var runtime = new FakeApprovalRuntimeContext
            {
                IsActiveP2PHostValue = true,
                LocalUserValue = HostId,
                RealtimeValue = 1000f
            };
            var whitelist = new FakeApprovalWhitelistProxy { ContainsResult = false };

            using (P2PJoinApprovalService.InstallTestDependencies(runtime, whitelist))
            {
                P2PJoinApprovalService.ResetForSession();

                // 模拟 Contains 抛异常（capture patch 精神延伸）
                whitelist.ThrowOnContains = new InvalidOperationException("capture-fail");

                bool threw = false;
                try
                {
                    EnqueueAndDrain(ClientId);
                }
                catch (Exception)
                {
                    threw = true;
                }
                if (threw) return Fail("Drain should not throw on Contains failure", "threw");

                // 应保守登记让房主看到请求
                if (P2PJoinApprovalService.PendingCount != 1) return Fail("should still register on Contains exception (conservative)", "count=" + P2PJoinApprovalService.PendingCount);
            }
            return true;
        }

        // =====================================================================
        // v3 新增测试（蓝图 v3 §4[指令 D]）
        // =====================================================================

        // ===== A9. 队列去重 + 32 上限 + drain 顺序 =====
        internal static bool Test_v3_CaptureQueue_Dedup_Cap_Order()
        {
            var runtime = new FakeApprovalRuntimeContext
            {
                IsActiveP2PHostValue = true,
                LocalUserValue = HostId,
                RealtimeValue = 1000f
            };
            var whitelist = new FakeApprovalWhitelistProxy { ContainsResult = false };

            using (P2PJoinApprovalService.InstallTestDependencies(runtime, whitelist))
            {
                P2PJoinApprovalService.ResetForSession();

                // 9a: 同一 SteamID 重复入队 - 应去重
                P2PJoinApprovalService.TryEnqueueRejectedTransportId(ClientId.m_SteamID);
                P2PJoinApprovalService.TryEnqueueRejectedTransportId(ClientId.m_SteamID);
                P2PJoinApprovalService.TryEnqueueRejectedTransportId(ClientId.m_SteamID);
                if (P2PJoinApprovalService.CaptureQueueDepthForTest != 1)
                    return Fail("queue should dedup to 1", "depth=" + P2PJoinApprovalService.CaptureQueueDepthForTest);

                // 9b: 入队 32 个不同 ID（满 cap）
                for (ulong i = 1; i <= 31; i++)
                {
                    P2PJoinApprovalService.TryEnqueueRejectedTransportId(76561198000000000UL + i);
                }
                int expectedDepth = 32; // 1 (ClientId) + 31 = 32
                if (P2PJoinApprovalService.CaptureQueueDepthForTest != expectedDepth)
                    return Fail("queue should be at cap 32", "depth=" + P2PJoinApprovalService.CaptureQueueDepthForTest);

                // 9c: 第 33 个 - 应被拒绝入队（cap 不扩张）
                P2PJoinApprovalService.TryEnqueueRejectedTransportId(76561198000000099UL);
                if (P2PJoinApprovalService.CaptureQueueDepthForTest != 32)
                    return Fail("queue should remain 32 (cap)", "depth=" + P2PJoinApprovalService.CaptureQueueDepthForTest);

                // 9d: drain 后队列空，pending 仍受 16 上限约束
                P2PJoinApprovalService.DrainCapturedRejectsOnMainThread();
                if (P2PJoinApprovalService.CaptureQueueDepthForTest != 0)
                    return Fail("queue should be empty after drain", "depth=" + P2PJoinApprovalService.CaptureQueueDepthForTest);
                if (P2PJoinApprovalService.PendingCount != 16)
                    return Fail("pending should be capped at 16", "count=" + P2PJoinApprovalService.PendingCount);
            }
            return true;
        }

        // ===== A10. 跨线程 Prefix 入队 + 主线程 drain（蓝图 v3 §3.2/§3.3 亲和性修复核心）=====
        internal static bool Test_v3_CrossThread_Prefix_Enqueue_MainThread_Drain()
        {
            var runtime = new FakeApprovalRuntimeContext
            {
                IsActiveP2PHostValue = true,
                LocalUserValue = HostId,
                RealtimeValue = 1000f
            };
            var whitelist = new FakeApprovalWhitelistProxy { ContainsResult = false };

            using (P2PJoinApprovalService.InstallTestDependencies(runtime, whitelist))
            {
                P2PJoinApprovalService.ResetForSession();

                // 模拟网络线程并发入队（Prefix 上下文）
                int queueDepthObservedFromWorker = -1;
                Exception workerException = null;
                var worker = new Thread(() =>
                {
                    try
                    {
                        // 网络线程只调 TryEnqueue，不访问 Provider/whitelist/Time
                        P2PJoinApprovalService.TryEnqueueRejectedTransportId(ClientId.m_SteamID);
                        P2PJoinApprovalService.TryEnqueueRejectedTransportId(ClientId2.m_SteamID);
                        queueDepthObservedFromWorker = P2PJoinApprovalService.CaptureQueueDepthForTest;
                    }
                    catch (Exception ex)
                    {
                        workerException = ex;
                    }
                });
                worker.Start();
                worker.Join();

                if (workerException != null)
                    return Fail("worker thread Prefix enqueue should not throw", workerException.GetType().Name);
                if (queueDepthObservedFromWorker != 2)
                    return Fail("worker should observe queue depth = 2", "depth=" + queueDepthObservedFromWorker);

                // 网络线程入队后，pending 仍为 0（drain 必须在主线程发生）
                if (P2PJoinApprovalService.PendingCount != 0)
                    return Fail("pending should be 0 before main-thread drain", "count=" + P2PJoinApprovalService.PendingCount);
                if (whitelist.ContainsCallCount != 0)
                    return Fail("Contains should not be called from non-main thread", "count=" + whitelist.ContainsCallCount);

                // 主线程 drain - 此时才访问 whitelist/Time/Provider
                P2PJoinApprovalService.DrainCapturedRejectsOnMainThread();
                if (P2PJoinApprovalService.PendingCount != 2)
                    return Fail("pending should be 2 after main-thread drain", "count=" + P2PJoinApprovalService.PendingCount);
                if (whitelist.ContainsCallCount != 2)
                    return Fail("Contains should be called twice on main thread", "count=" + whitelist.ContainsCallCount);
            }
            return true;
        }

        // ===== A11. Reset 递增 epoch，旧 epoch 条目 drain 时丢弃（蓝图 v3 §3.2/§4.7）=====
        internal static bool Test_v3_Reset_Discards_Old_Epoch()
        {
            var runtime = new FakeApprovalRuntimeContext
            {
                IsActiveP2PHostValue = true,
                LocalUserValue = HostId,
                RealtimeValue = 1000f
            };
            var whitelist = new FakeApprovalWhitelistProxy { ContainsResult = false };

            using (P2PJoinApprovalService.InstallTestDependencies(runtime, whitelist))
            {
                P2PJoinApprovalService.ResetForSession();
                int epochBefore = P2PJoinApprovalService.CurrentEpochForTest;

                // 旧 epoch 入队
                P2PJoinApprovalService.TryEnqueueRejectedTransportId(ClientId.m_SteamID);
                P2PJoinApprovalService.TryEnqueueRejectedTransportId(ClientId2.m_SteamID);
                if (P2PJoinApprovalService.CaptureQueueDepthForTest != 2)
                    return Fail("queue should have 2 old-epoch entries", "depth=" + P2PJoinApprovalService.CaptureQueueDepthForTest);

                // 触发 ResetForSession - epoch 递增
                P2PJoinApprovalService.ResetForSession();
                int epochAfter = P2PJoinApprovalService.CurrentEpochForTest;
                if (epochAfter <= epochBefore)
                    return Fail("epoch should increment on reset", "before=" + epochBefore + " after=" + epochAfter);

                // 注：ResetForSession 已清空队列（蓝图 v3 §4.7 lock(Sync) 内 Clear）
                // 但若队列在 Reset 前已 drain 部分，旧 epoch 条目在 drain 时会被丢弃
                // 此处再入队一个新 epoch 条目
                P2PJoinApprovalService.TryEnqueueRejectedTransportId(ClientId.m_SteamID);
                if (P2PJoinApprovalService.CaptureQueueDepthForTest != 1)
                    return Fail("queue should have 1 new-epoch entry", "depth=" + P2PJoinApprovalService.CaptureQueueDepthForTest);

                P2PJoinApprovalService.DrainCapturedRejectsOnMainThread();
                if (P2PJoinApprovalService.PendingCount != 1)
                    return Fail("only new-epoch entry should register", "count=" + P2PJoinApprovalService.PendingCount);

                // 验证：包含的是 ClientId（新 epoch），ClientId2 被丢弃
                var pending = P2PJoinApprovalService.GetPendingRequests();
                if (pending.Count != 1 || pending[0].SteamId.m_SteamID != ClientId.m_SteamID)
                    return Fail("pending should contain only ClientId (new epoch)", "count=" + pending.Count);
            }
            return true;
        }

        // ===== A12. ResetAfterSession 同样递增 epoch + 清空所有队列 =====
        internal static bool Test_v3_ResetAfterSession_Increments_Epoch_And_Clears()
        {
            var runtime = new FakeApprovalRuntimeContext
            {
                IsActiveP2PHostValue = true,
                LocalUserValue = HostId,
                RealtimeValue = 1000f
            };
            var whitelist = new FakeApprovalWhitelistProxy { ContainsResult = false };

            using (P2PJoinApprovalService.InstallTestDependencies(runtime, whitelist))
            {
                P2PJoinApprovalService.ResetForSession();
                int epochBefore = P2PJoinApprovalService.CurrentEpochForTest;

                EnqueueAndDrain(ClientId);
                EnqueueAndDrain(ClientId2);
                P2PJoinApprovalService.RejectForSession(ClientId);
                if (P2PJoinApprovalService.PendingCount != 1)
                    return Fail("setup: pending should be 1", "count=" + P2PJoinApprovalService.PendingCount);

                // ResetAfterSession - 房主退出 P2P 会话后调用
                P2PJoinApprovalService.ResetAfterSession();
                int epochAfter = P2PJoinApprovalService.CurrentEpochForTest;
                if (epochAfter <= epochBefore)
                    return Fail("epoch should increment on ResetAfterSession", "before=" + epochBefore + " after=" + epochAfter);
                if (P2PJoinApprovalService.PendingCount != 0)
                    return Fail("pending should be cleared", "count=" + P2PJoinApprovalService.PendingCount);
                if (P2PJoinApprovalService.CaptureQueueDepthForTest != 0)
                    return Fail("capture queue should be cleared", "depth=" + P2PJoinApprovalService.CaptureQueueDepthForTest);
                if (P2PJoinApprovalService.IsSessionSuppressed(ClientId))
                    return Fail("suppressed set should be cleared", "still suppressed");
            }
            return true;
        }

        // ===== A13. 业务 drain 脱离 HUD（蓝图 v4 [指令 A/F] + Stage 7-4 [指令 D] P0-APPROVAL-DRAIN-UI-COUPLING-01）=====
        // 场景：ESC 未开（pauseActive=false）-> 审批面板不创建；但 Plugin.Update 业务 drain 仍登记 pending。
        //   这是 v4 修复的核心证明 + Stage 7-4 ESC 门控不改审批内核：面板未开不丢失/冻结已捕获的审批请求。
        internal static bool Test_v4_A13_BusinessDrainSurvivesHudUnavailable()
        {
            var runtime = new FakeApprovalRuntimeContext
            {
                IsActiveP2PHostValue = true,
                LocalUserValue = HostId,
                RealtimeValue = 1000f
            };
            var whitelist = new FakeApprovalWhitelistProxy { ContainsResult = false };

            using (P2PJoinApprovalService.InstallTestDependencies(runtime, whitelist))
            {
                P2PJoinApprovalService.ResetForSession();

                // 模拟 Stage 7-4：ESC 未开（pauseActive=false）-> 审批面板不创建
                using (P2PClientUiEnvironment.OverrideForTest(true))
                {
                    P2PHostSessionUI._testBypassThreadAssert = true;
                    P2PHostSessionUI._testHostActiveOverride = true;
                    P2PPauseMenuSurface._testBypassThreadAssert = true;
                    P2PPauseMenuSurface._testActiveOverride = false; // ESC 未开
                    P2PPauseMenuSurface._testContainerProvider = () => null; // 不应被访问

                    try
                    {
                        // 1. ESC 未开 -> 审批面板不创建
                        P2PHostSessionUI.Destroy();
                        P2PHostSessionUI.Tick();
                        if (P2PHostSessionUI.IsCreatedForTest)
                            return Fail("precondition: panel should NOT create when ESC closed", "created=true");

                        // 2. 客机已被原版 WHITELISTED 拒绝、Prefix 已入队
                        P2PJoinApprovalService.TryEnqueueRejectedTransportId(ClientId.m_SteamID);
                        if (P2PJoinApprovalService.CaptureQueueDepthForTest != 1)
                            return Fail("queue should have 1 enqueued entry", "depth=" + P2PJoinApprovalService.CaptureQueueDepthForTest);

                        // 3. Plugin.Update 业务驱动 drain（不受 ESC 面板是否创建约束）
                        P2PJoinApprovalService.DrainCapturedRejectsOnMainThread();

                        // 4. 关键断言：面板未创建，pending 仍登记 -> 首次加入死锁解除
                        if (P2PJoinApprovalService.PendingCount != 1)
                            return Fail("pending should be 1 despite panel unavailable (P0 fix)", "count=" + P2PJoinApprovalService.PendingCount);
                        var pending = P2PJoinApprovalService.GetPendingRequests();
                        if (pending.Count != 1 || pending[0].SteamId.m_SteamID != ClientId.m_SteamID)
                            return Fail("pending should contain ClientId", "count=" + pending.Count);

                        // 5. 面板仍未创建（drain 没有副作用创建面板）
                        if (P2PHostSessionUI.IsCreatedForTest)
                            return Fail("panel should remain uncreated after business drain", "created=true");
                    }
                    finally
                    {
                        P2PHostSessionUI._testHostActiveOverride = null;
                        P2PHostSessionUI._testBypassThreadAssert = false;
                        P2PPauseMenuSurface._testActiveOverride = null;
                        P2PPauseMenuSurface._testContainerProvider = null;
                        P2PPauseMenuSurface._testBypassThreadAssert = false;
                    }
                }
            }
            return true;
        }

        // ===== A14. 主线程契约负向测试（蓝图 v4 [指令 C/F] P1-APPROVAL-MAIN-THREAD-CONTRACT-03）=====
        // 场景：_testBypassThreadAssert=false 时，RejectForSession/GetPendingRequests/PendingCount
        //   必须抛 ThreadUtil.assertIsGameThread 的 NotSupportedException（测试控制台 gameThread=null）。
        //   证明主线程契约是可验证的接口断言，而非仅靠调用方自律。
        internal static bool Test_v4_A14_MainThreadContract_ThrowsWhenNotBypassed()
        {
            var runtime = new FakeApprovalRuntimeContext
            {
                IsActiveP2PHostValue = true,
                LocalUserValue = HostId,
                RealtimeValue = 1000f
            };
            var whitelist = new FakeApprovalWhitelistProxy { ContainsResult = false };

            using (P2PJoinApprovalService.InstallTestDependencies(runtime, whitelist))
            {
                P2PJoinApprovalService.ResetForSession();

                // 临时关闭 bypass，验证三个方法均抛主线程断言
                bool originalBypass = P2PJoinApprovalService._testBypassThreadAssert;
                P2PJoinApprovalService._testBypassThreadAssert = false;
                try
                {
                    int throwCount = 0;

                    // PendingCount
                    try { _ = P2PJoinApprovalService.PendingCount; }
                    catch (NotSupportedException) { throwCount++; }
                    catch (Exception ex) { return Fail("PendingCount should throw NotSupportedException", ex.GetType().Name); }

                    // GetPendingRequests
                    try { _ = P2PJoinApprovalService.GetPendingRequests(); }
                    catch (NotSupportedException) { throwCount++; }
                    catch (Exception ex) { return Fail("GetPendingRequests should throw NotSupportedException", ex.GetType().Name); }

                    // RejectForSession
                    try { P2PJoinApprovalService.RejectForSession(ClientId); }
                    catch (NotSupportedException) { throwCount++; }
                    catch (Exception ex) { return Fail("RejectForSession should throw NotSupportedException", ex.GetType().Name); }

                    if (throwCount != 3)
                        return Fail("all 3 methods should throw (contract active)", "throwCount=" + throwCount);
                }
                finally
                {
                    P2PJoinApprovalService._testBypassThreadAssert = originalBypass;
                }

                // 恢复 bypass 后应正常工作
                _ = P2PJoinApprovalService.PendingCount;
            }
            return true;
        }

        // ===== 辅助 =====
        private static bool Fail(string msg, string detail)
        {
            Console.WriteLine("    FAIL: " + msg + (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")"));
            return false;
        }
    }
}
