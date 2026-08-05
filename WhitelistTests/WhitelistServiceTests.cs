using SteamP2PFriends.Host;
using SteamP2PFriends.WhitelistTests.Fakes;
using Steamworks;
using System;

namespace SteamP2PFriends.WhitelistTests
{
    /// <summary>
    /// Stage 7-2-2 纯单元测试：P2PWhitelistService 7 大场景。
    /// 蓝图 §3：不启动 Unturned、不触碰 Provider/Steam API/Unity/文件系统。
    /// </summary>
    internal static class WhitelistServiceTests
    {
        // 测试用 SteamID（合法的 Individual 账号）
        private static readonly CSteamID HostId = new CSteamID(76561199030780228UL);
        private static readonly CSteamID TargetId = new CSteamID(76561199721762479UL);
        private const string Tag = "MEMBER";

        // ===== 1. bootstrap 成功 =====
        internal static bool Test_Bootstrap_Success()
        {
            var store = new FakeWhitelistStore();
            var runtime = new FakeWhitelistRuntimeContext
            {
                LocalUserValue = HostId,
                IsActiveP2PHostValue = true
            };
            var gateway = new FakeWhitelistDisconnectGateway();

            using (P2PWhitelistService.InstallTestDependencies(store, runtime, gateway))
            {
                P2PWhitelistService.ResetForP2PStart();
                bool ok = P2PWhitelistService.TryBootstrap(HostId, out string failure);

                if (!ok) return Fail("bootstrap should succeed", failure);
                if (gateway.DisconnectCallCount != 0) return Fail("gateway should not be called", "disconnect=" + gateway.DisconnectCallCount);
                if (runtime.SetWhitelistedCount == 0) return Fail("SetWhitelisted should be called", "count=0");
                if (!runtime.LastWhitelistedValue) return Fail("SetWhitelisted(true) expected", "lastValue=" + runtime.LastWhitelistedValue);
                if (store.AddOrUpdateCount != 1) return Fail("AddOrUpdate should be called once", "count=" + store.AddOrUpdateCount);
                if (store.SaveCount != 1) return Fail("Save should be called once", "count=" + store.SaveCount);
                if (store.LoadCount != 2) return Fail("Load should be called twice (initial + postcondition)", "count=" + store.LoadCount);
                if (store.ContainsCount != 1) return Fail("Contains should be called once", "count=" + store.ContainsCount);
            }
            return true;
        }

        // ===== 2. bootstrap Save/Load/Contains 失败且不调用 disconnect =====
        internal static bool Test_Bootstrap_SaveFailure_NoDisconnect()
        {
            var store = new FakeWhitelistStore { ThrowOnSave = new InvalidOperationException("save-fail") };
            var runtime = new FakeWhitelistRuntimeContext
            {
                LocalUserValue = HostId,
                IsActiveP2PHostValue = true
            };
            var gateway = new FakeWhitelistDisconnectGateway();

            using (P2PWhitelistService.InstallTestDependencies(store, runtime, gateway))
            {
                P2PWhitelistService.ResetForP2PStart();
                bool ok = P2PWhitelistService.TryBootstrap(HostId, out string failure);

                if (ok) return Fail("bootstrap should fail on Save", "returned true");
                if (gateway.DisconnectCallCount != 0) return Fail("gateway should NOT be called on bootstrap failure", "count=" + gateway.DisconnectCallCount);
                if (runtime.LastWhitelistedValue) return Fail("SetWhitelisted should be false on failure", "lastValue=true");
            }
            return true;
        }

        internal static bool Test_Bootstrap_LoadFailure_NoDisconnect()
        {
            var store = new FakeWhitelistStore { ThrowOnLoad = new InvalidOperationException("load-fail") };
            var runtime = new FakeWhitelistRuntimeContext
            {
                LocalUserValue = HostId,
                IsActiveP2PHostValue = true
            };
            var gateway = new FakeWhitelistDisconnectGateway();

            using (P2PWhitelistService.InstallTestDependencies(store, runtime, gateway))
            {
                P2PWhitelistService.ResetForP2PStart();
                bool ok = P2PWhitelistService.TryBootstrap(HostId, out string failure);

                if (ok) return Fail("bootstrap should fail on Load", "returned true");
                if (gateway.DisconnectCallCount != 0) return Fail("gateway should NOT be called on bootstrap failure", "count=" + gateway.DisconnectCallCount);
                if (runtime.LastWhitelistedValue) return Fail("SetWhitelisted should be false on failure", "lastValue=true");
            }
            return true;
        }

        internal static bool Test_Bootstrap_ContainsFailure_NoDisconnect()
        {
            var store = new FakeWhitelistStore { ContainsResult = false };
            var runtime = new FakeWhitelistRuntimeContext
            {
                LocalUserValue = HostId,
                IsActiveP2PHostValue = true
            };
            var gateway = new FakeWhitelistDisconnectGateway();

            using (P2PWhitelistService.InstallTestDependencies(store, runtime, gateway))
            {
                P2PWhitelistService.ResetForP2PStart();
                bool ok = P2PWhitelistService.TryBootstrap(HostId, out string failure);

                if (ok) return Fail("bootstrap should fail on Contains==false", "returned true");
                if (gateway.DisconnectCallCount != 0) return Fail("gateway should NOT be called on bootstrap postcondition failure", "count=" + gateway.DisconnectCallCount);
                if (runtime.LastWhitelistedValue) return Fail("SetWhitelisted should be false on postcondition failure", "lastValue=true");
            }
            return true;
        }

        // ===== 3. Add Save、Load、Contains 失败各调用 gateway 恰好一次 =====
        internal static bool Test_Add_SaveFailure_GatewayOnce()
        {
            var store = new FakeWhitelistStore { ThrowOnSave = new InvalidOperationException("save-fail") };
            var runtime = new FakeWhitelistRuntimeContext
            {
                LocalUserValue = HostId,
                IsActiveP2PHostValue = true
            };
            var gateway = new FakeWhitelistDisconnectGateway();

            using (P2PWhitelistService.InstallTestDependencies(store, runtime, gateway))
            {
                P2PWhitelistService.ResetForP2PStart();
                bool ok = P2PWhitelistService.TryAdd(TargetId, Tag, out string feedback);

                if (ok) return Fail("TryAdd should fail on Save", "returned true");
                if (gateway.DisconnectCallCount != 1) return Fail("gateway should be called exactly once", "count=" + gateway.DisconnectCallCount);
            }
            return true;
        }

        internal static bool Test_Add_LoadFailure_GatewayOnce()
        {
            var store = new FakeWhitelistStore { ThrowOnLoad = new InvalidOperationException("load-fail") };
            var runtime = new FakeWhitelistRuntimeContext
            {
                LocalUserValue = HostId,
                IsActiveP2PHostValue = true
            };
            var gateway = new FakeWhitelistDisconnectGateway();

            using (P2PWhitelistService.InstallTestDependencies(store, runtime, gateway))
            {
                P2PWhitelistService.ResetForP2PStart();
                bool ok = P2PWhitelistService.TryAdd(TargetId, Tag, out string feedback);

                if (ok) return Fail("TryAdd should fail on Load", "returned true");
                if (gateway.DisconnectCallCount != 1) return Fail("gateway should be called exactly once", "count=" + gateway.DisconnectCallCount);
            }
            return true;
        }

        internal static bool Test_Add_ContainsFailure_GatewayOnce()
        {
            var store = new FakeWhitelistStore { ContainsResult = false };
            var runtime = new FakeWhitelistRuntimeContext
            {
                LocalUserValue = HostId,
                IsActiveP2PHostValue = true
            };
            var gateway = new FakeWhitelistDisconnectGateway();

            using (P2PWhitelistService.InstallTestDependencies(store, runtime, gateway))
            {
                P2PWhitelistService.ResetForP2PStart();
                bool ok = P2PWhitelistService.TryAdd(TargetId, Tag, out string feedback);

                if (ok) return Fail("TryAdd should fail on Contains==false postcondition", "returned true");
                if (gateway.DisconnectCallCount != 1) return Fail("gateway should be called exactly once on postcondition failure", "count=" + gateway.DisconnectCallCount);
            }
            return true;
        }

        // ===== 3d. Add Snapshot 失败：gateway exactly once + Save/Load 未调用 + fault 锁存 =====
        // P0-WL-SNAPSHOT-THROW-01（Codex 134th）
        internal static bool Test_Add_SnapshotFailure_GatewayOnce()
        {
            var store = new FakeWhitelistStore { ThrowOnSnapshot = new InvalidOperationException("snapshot-fail") };
            var runtime = new FakeWhitelistRuntimeContext
            {
                LocalUserValue = HostId,
                IsActiveP2PHostValue = true
            };
            var gateway = new FakeWhitelistDisconnectGateway();

            using (P2PWhitelistService.InstallTestDependencies(store, runtime, gateway))
            {
                P2PWhitelistService.ResetForP2PStart();
                bool ok = P2PWhitelistService.TryAdd(TargetId, Tag, out string feedback);

                if (ok) return Fail("TryAdd should fail on Snapshot throw", "returned true");
                if (gateway.DisconnectCallCount != 1) return Fail("gateway should be called exactly once on Snapshot failure", "count=" + gateway.DisconnectCallCount);
                if (store.SnapshotCount != 1) return Fail("Snapshot should be attempted once", "count=" + store.SnapshotCount);
                if (store.SaveCount != 0) return Fail("Save should NOT be called when Snapshot fails before Save", "count=" + store.SaveCount);
                if (store.LoadCount != 0) return Fail("Load should NOT be called when Snapshot fails before Load", "count=" + store.LoadCount);
                if (store.AddOrUpdateCount != 0) return Fail("AddOrUpdate should NOT be called when Snapshot fails", "count=" + store.AddOrUpdateCount);

                // 第二次 TryAdd 应被 fault latch 拒绝
                store.ThrowOnSnapshot = null;
                bool ok2 = P2PWhitelistService.TryAdd(TargetId, Tag, out string feedback2);
                if (ok2) return Fail("second TryAdd should be rejected by fault latch", "returned true");
                if (gateway.DisconnectCallCount != 1) return Fail("gateway should NOT be called again on fault-latched rejection", "count=" + gateway.DisconnectCallCount);
            }
            return true;
        }

        // ===== 4c. Remove Snapshot 失败：gateway exactly once + Save/Load 未调用 + fault 锁存 =====
        // P0-WL-SNAPSHOT-THROW-01（Codex 134th）
        internal static bool Test_Remove_SnapshotFailure_GatewayOnce()
        {
            var store = new FakeWhitelistStore { ThrowOnSnapshot = new InvalidOperationException("snapshot-fail") };
            var runtime = new FakeWhitelistRuntimeContext
            {
                LocalUserValue = HostId,
                IsActiveP2PHostValue = true
            };
            var gateway = new FakeWhitelistDisconnectGateway();

            using (P2PWhitelistService.InstallTestDependencies(store, runtime, gateway))
            {
                P2PWhitelistService.ResetForP2PStart();
                bool ok = P2PWhitelistService.TryRemove(TargetId, out string feedback);

                if (ok) return Fail("TryRemove should fail on Snapshot throw", "returned true");
                if (gateway.DisconnectCallCount != 1) return Fail("gateway should be called exactly once on Snapshot failure", "count=" + gateway.DisconnectCallCount);
                if (store.SnapshotCount != 1) return Fail("Snapshot should be attempted once", "count=" + store.SnapshotCount);
                if (store.SaveCount != 0) return Fail("Save should NOT be called when Snapshot fails before Save", "count=" + store.SaveCount);
                if (store.LoadCount != 0) return Fail("Load should NOT be called when Snapshot fails before Load", "count=" + store.LoadCount);
                if (store.RemoveCount != 0) return Fail("Remove should NOT be called when Snapshot fails", "count=" + store.RemoveCount);

                // 第二次 TryRemove 应被 fault latch 拒绝
                store.ThrowOnSnapshot = null;
                bool ok2 = P2PWhitelistService.TryRemove(TargetId, out string feedback2);
                if (ok2) return Fail("second TryRemove should be rejected by fault latch", "returned true");
                if (gateway.DisconnectCallCount != 1) return Fail("gateway should NOT be called again on fault-latched rejection", "count=" + gateway.DisconnectCallCount);
            }
            return true;
        }

        // ===== 5c. LocalUser 无效（Nil）：TryAdd 拒绝且不 Snapshot/Save/disconnect =====
        // P1-WL-LOCALUSER-VALIDATION-01（Codex 134th）
        internal static bool Test_Add_InvalidLocalUser_Rejected()
        {
            var store = new FakeWhitelistStore();
            var runtime = new FakeWhitelistRuntimeContext
            {
                LocalUserValue = CSteamID.Nil,
                IsActiveP2PHostValue = true
            };
            var gateway = new FakeWhitelistDisconnectGateway();

            using (P2PWhitelistService.InstallTestDependencies(store, runtime, gateway))
            {
                P2PWhitelistService.ResetForP2PStart();
                bool ok = P2PWhitelistService.TryAdd(TargetId, Tag, out string feedback);

                if (ok) return Fail("TryAdd should be rejected when LocalUser is Nil", "returned true");
                if (gateway.DisconnectCallCount != 0) return Fail("gateway should NOT be called on invalid LocalUser", "count=" + gateway.DisconnectCallCount);
                if (store.SnapshotCount != 0) return Fail("Snapshot should NOT be called on invalid LocalUser", "count=" + store.SnapshotCount);
                if (store.SaveCount != 0) return Fail("Save should NOT be called on invalid LocalUser", "count=" + store.SaveCount);
                if (store.AddOrUpdateCount != 0) return Fail("AddOrUpdate should NOT be called on invalid LocalUser", "count=" + store.AddOrUpdateCount);
            }
            return true;
        }

        // ===== 5d. LocalUser 无效（Nil）：TryRemove 拒绝且不 Snapshot/Save/disconnect =====
        // P1-WL-LOCALUSER-VALIDATION-01（Codex 134th）
        internal static bool Test_Remove_InvalidLocalUser_Rejected()
        {
            var store = new FakeWhitelistStore();
            var runtime = new FakeWhitelistRuntimeContext
            {
                LocalUserValue = CSteamID.Nil,
                IsActiveP2PHostValue = true
            };
            var gateway = new FakeWhitelistDisconnectGateway();

            using (P2PWhitelistService.InstallTestDependencies(store, runtime, gateway))
            {
                P2PWhitelistService.ResetForP2PStart();
                bool ok = P2PWhitelistService.TryRemove(TargetId, out string feedback);

                if (ok) return Fail("TryRemove should be rejected when LocalUser is Nil", "returned true");
                if (gateway.DisconnectCallCount != 0) return Fail("gateway should NOT be called on invalid LocalUser", "count=" + gateway.DisconnectCallCount);
                if (store.SnapshotCount != 0) return Fail("Snapshot should NOT be called on invalid LocalUser", "count=" + store.SnapshotCount);
                if (store.SaveCount != 0) return Fail("Save should NOT be called on invalid LocalUser", "count=" + store.SaveCount);
                if (store.RemoveCount != 0) return Fail("Remove should NOT be called on invalid LocalUser", "count=" + store.RemoveCount);
            }
            return true;
        }

        // ===== 4. Remove Save 失败调用一次；Remove=false 不 Save、不 disconnect =====
        internal static bool Test_Remove_SaveFailure_GatewayOnce()
        {
            var store = new FakeWhitelistStore { ThrowOnSave = new InvalidOperationException("save-fail") };
            var runtime = new FakeWhitelistRuntimeContext
            {
                LocalUserValue = HostId,
                IsActiveP2PHostValue = true
            };
            var gateway = new FakeWhitelistDisconnectGateway();

            using (P2PWhitelistService.InstallTestDependencies(store, runtime, gateway))
            {
                P2PWhitelistService.ResetForP2PStart();
                bool ok = P2PWhitelistService.TryRemove(TargetId, out string feedback);

                if (ok) return Fail("TryRemove should fail on Save", "returned true");
                if (gateway.DisconnectCallCount != 1) return Fail("gateway should be called exactly once", "count=" + gateway.DisconnectCallCount);
            }
            return true;
        }

        internal static bool Test_Remove_NoOp_NoSave_NoDisconnect()
        {
            var store = new FakeWhitelistStore { RemoveResult = false };
            var runtime = new FakeWhitelistRuntimeContext
            {
                LocalUserValue = HostId,
                IsActiveP2PHostValue = true
            };
            var gateway = new FakeWhitelistDisconnectGateway();

            using (P2PWhitelistService.InstallTestDependencies(store, runtime, gateway))
            {
                P2PWhitelistService.ResetForP2PStart();
                bool ok = P2PWhitelistService.TryRemove(TargetId, out string feedback);

                if (ok) return Fail("TryRemove should return false on no-op", "returned true");
                if (gateway.DisconnectCallCount != 0) return Fail("gateway should NOT be called on Remove=false no-op", "count=" + gateway.DisconnectCallCount);
                if (store.SaveCount != 0) return Fail("Save should NOT be called on Remove=false no-op", "count=" + store.SaveCount);
                if (store.LoadCount != 0) return Fail("Load should NOT be called on Remove=false no-op", "count=" + store.LoadCount);
            }
            return true;
        }

        // ===== 5. Add/Remove 房主自身拒绝且不 Snapshot/Save/disconnect =====
        internal static bool Test_Add_Self_Rejected()
        {
            var store = new FakeWhitelistStore();
            var runtime = new FakeWhitelistRuntimeContext
            {
                LocalUserValue = HostId,
                IsActiveP2PHostValue = true
            };
            var gateway = new FakeWhitelistDisconnectGateway();

            using (P2PWhitelistService.InstallTestDependencies(store, runtime, gateway))
            {
                P2PWhitelistService.ResetForP2PStart();
                bool ok = P2PWhitelistService.TryAdd(HostId, Tag, out string feedback);

                if (ok) return Fail("TryAdd(self) should be rejected", "returned true");
                if (gateway.DisconnectCallCount != 0) return Fail("gateway should NOT be called on self-rejection", "count=" + gateway.DisconnectCallCount);
                if (store.SnapshotCount != 0) return Fail("Snapshot should NOT be called on self-rejection", "count=" + store.SnapshotCount);
                if (store.SaveCount != 0) return Fail("Save should NOT be called on self-rejection", "count=" + store.SaveCount);
                if (store.AddOrUpdateCount != 0) return Fail("AddOrUpdate should NOT be called on self-rejection", "count=" + store.AddOrUpdateCount);
            }
            return true;
        }

        internal static bool Test_Remove_Self_Rejected()
        {
            var store = new FakeWhitelistStore();
            var runtime = new FakeWhitelistRuntimeContext
            {
                LocalUserValue = HostId,
                IsActiveP2PHostValue = true
            };
            var gateway = new FakeWhitelistDisconnectGateway();

            using (P2PWhitelistService.InstallTestDependencies(store, runtime, gateway))
            {
                P2PWhitelistService.ResetForP2PStart();
                bool ok = P2PWhitelistService.TryRemove(HostId, out string feedback);

                if (ok) return Fail("TryRemove(self) should be rejected", "returned true");
                if (gateway.DisconnectCallCount != 0) return Fail("gateway should NOT be called on self-rejection", "count=" + gateway.DisconnectCallCount);
                if (store.SnapshotCount != 0) return Fail("Snapshot should NOT be called on self-rejection", "count=" + store.SnapshotCount);
                if (store.SaveCount != 0) return Fail("Save should NOT be called on self-rejection", "count=" + store.SaveCount);
                if (store.RemoveCount != 0) return Fail("Remove should NOT be called on self-rejection", "count=" + store.RemoveCount);
            }
            return true;
        }

        // ===== 6. judgeID 等于 fake runtime LocalUser =====
        internal static bool Test_Add_JudgeId_Equals_LocalUser()
        {
            var store = new FakeWhitelistStore();
            var runtime = new FakeWhitelistRuntimeContext
            {
                LocalUserValue = HostId,
                IsActiveP2PHostValue = true
            };
            var gateway = new FakeWhitelistDisconnectGateway();

            using (P2PWhitelistService.InstallTestDependencies(store, runtime, gateway))
            {
                P2PWhitelistService.ResetForP2PStart();
                bool ok = P2PWhitelistService.TryAdd(TargetId, Tag, out string feedback);

                if (!ok) return Fail("TryAdd should succeed", feedback);
                if (!store.LastAddJudge.HasValue) return Fail("LastAddJudge should be set", "null");
                if (store.LastAddJudge.Value != runtime.LocalUserValue)
                    return Fail("judgeID should equal fake runtime LocalUser",
                        $"judge={store.LastAddJudge.Value.m_SteamID} localUser={runtime.LocalUserValue.m_SteamID}");
            }
            return true;
        }

        // ===== 7. 每个失败后 persistence fault 阻止第二次 mutate；Reset 后恢复 =====
        internal static bool Test_PersistenceFault_Blocks_Second_Mutate_And_Reset_Restores()
        {
            var store = new FakeWhitelistStore { ThrowOnSave = new InvalidOperationException("save-fail") };
            var runtime = new FakeWhitelistRuntimeContext
            {
                LocalUserValue = HostId,
                IsActiveP2PHostValue = true
            };
            var gateway = new FakeWhitelistDisconnectGateway();

            using (P2PWhitelistService.InstallTestDependencies(store, runtime, gateway))
            {
                // 第一次 TryAdd 失败
                P2PWhitelistService.ResetForP2PStart();
                bool ok1 = P2PWhitelistService.TryAdd(TargetId, Tag, out string feedback1);
                if (ok1) return Fail("first TryAdd should fail", "returned true");
                if (gateway.DisconnectCallCount != 1) return Fail("gateway should be called once after first failure", "count=" + gateway.DisconnectCallCount);
                int saveCountAfterFirst = store.SaveCount;

                // 第二次 TryAdd 应被 persistenceFaulted 拒绝
                store.ThrowOnSave = null; // 清除失败模式
                bool ok2 = P2PWhitelistService.TryAdd(TargetId, Tag, out string feedback2);
                if (ok2) return Fail("second TryAdd should be rejected by fault latch", "returned true");
                if (gateway.DisconnectCallCount != 1) return Fail("gateway should NOT be called again on fault-latched rejection", "count=" + gateway.DisconnectCallCount);
                if (store.SaveCount != saveCountAfterFirst) return Fail("Save should NOT be called on fault-latched rejection", "count=" + store.SaveCount);

                // Reset 后第三次 TryAdd 应成功
                P2PWhitelistService.ResetForP2PStart();
                bool ok3 = P2PWhitelistService.TryAdd(TargetId, Tag, out string feedback3);
                if (!ok3) return Fail("third TryAdd should succeed after Reset", feedback3);
                if (gateway.DisconnectCallCount != 1) return Fail("gateway should still be 1 after successful retry", "count=" + gateway.DisconnectCallCount);
                if (store.SaveCount <= saveCountAfterFirst) return Fail("Save count should increase after successful retry", "count=" + store.SaveCount);
            }
            return true;
        }

        // ===== 辅助 =====

        private static bool Fail(string message, string detail)
        {
            Console.WriteLine("  FAIL: " + message + (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")"));
            return false;
        }

        private static bool Pass()
        {
            return true;
        }
    }
}
