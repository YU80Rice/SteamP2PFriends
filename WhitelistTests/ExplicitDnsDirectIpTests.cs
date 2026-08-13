using SDG.Unturned;
using SteamP2PFriends.Client;
using SteamP2PFriends.Shared;
using SteamP2PFriends.UI;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Unturned.SystemEx;

namespace SteamP2PFriends.WhitelistTests
{
    /// <summary>
    /// Stage 9-3 (v2): explicit DNS direct-IP tests (D1-D18 + UI lifecycle).
    /// </summary>
    internal static class ExplicitDnsDirectIpTests
    {
        // ===== D1: arbitrary DNS + random port accepted, no Sakura suffix hardcode =====
        internal static bool Test_D1_ArbitraryDnsAndRandomPortAccepted()
        {
            bool ok1 = UnifiedJoinAddressClassifier.TryBuildExplicitDnsEndpoint(
                "frp-cat.com", 31567, out string host1, out ushort port1);
            bool ok2 = UnifiedJoinAddressClassifier.TryBuildExplicitDnsEndpoint(
                "node-a123.example.net", 65535, out string host2, out ushort port2);
            bool ok3 = UnifiedJoinAddressClassifier.TryBuildExplicitDnsEndpoint(
                "someserver.xyz", 1, out string host3, out ushort port3);
            return ok1 && host1 == "frp-cat.com" && port1 == 31567 &&
                   ok2 && host2 == "node-a123.example.net" && port2 == 65535 &&
                   ok3 && host3 == "someserver.xyz" && port3 == 1;
        }

        // ===== D2: inline port overrides field =====
        internal static bool Test_D2_InlinePortOverridesField()
        {
            bool ok = UnifiedJoinAddressClassifier.TryBuildExplicitDnsEndpoint(
                "host.example:26655", 27015, out string host, out ushort port);
            return ok && host == "host.example" && port == 26655;
        }

        // ===== D3: invalid DNS matrix rejected =====
        internal static bool Test_D3_InvalidDnsMatrixRejected()
        {
            return !UnifiedJoinAddressClassifier.TryBuildExplicitDnsEndpoint("http://evil.com", 27015, out _, out _) &&
                   !UnifiedJoinAddressClassifier.TryBuildExplicitDnsEndpoint("26.196.34.90", 27016, out _, out _) &&
                   !UnifiedJoinAddressClassifier.TryBuildExplicitDnsEndpoint("76561199030780228", 27015, out _, out _) &&
                   !UnifiedJoinAddressClassifier.TryBuildExplicitDnsEndpoint("2001:db8::1", 27015, out _, out _) &&
                   !UnifiedJoinAddressClassifier.TryBuildExplicitDnsEndpoint("a..b.com", 27015, out _, out _) &&
                   !UnifiedJoinAddressClassifier.TryBuildExplicitDnsEndpoint("-bad.com", 27015, out _, out _) &&
                   !UnifiedJoinAddressClassifier.TryBuildExplicitDnsEndpoint("bad-.com", 27015, out _, out _) &&
                   !UnifiedJoinAddressClassifier.TryBuildExplicitDnsEndpoint("host with space.com", 27015, out _, out _) &&
                   !UnifiedJoinAddressClassifier.TryBuildExplicitDnsEndpoint("host.com", 0, out _, out _) &&
                   !UnifiedJoinAddressClassifier.TryBuildExplicitDnsEndpoint("host.com:99999", 27015, out _, out _);
        }

        // ===== D4: mode off leaves DNS vanilla (pure function level) =====
        internal static bool Test_D4_ModeOffLeavesDnsVanilla()
        {
            ExplicitDnsDirectIpModeUI.ResetForTest();
            bool enabledByDefault = ExplicitDnsDirectIpModeUI.IsEnabled;
            return enabledByDefault == false;
        }

        // ===== D5: mode on routes DNS (pure function level) =====
        internal static bool Test_D5_ModeOnRoutesDnsOnly()
        {
            bool dns = UnifiedJoinAddressClassifier.TryBuildExplicitDnsEndpoint(
                "myhost.frp", 31567, out _, out _);
            bool ipNotDns = !UnifiedJoinAddressClassifier.TryBuildExplicitDnsEndpoint(
                "10.0.0.5", 31567, out _, out _);
            bool steamNotDns = !UnifiedJoinAddressClassifier.TryBuildExplicitDnsEndpoint(
                "76561199030780228", 31567, out _, out _);
            return dns && ipNotDns && steamNotDns;
        }

        // ===== D6: worker never connects =====
        internal static bool Test_D6_WorkerNeverConnects()
        {
            // Fake runtime must report the same host/port/mode as the begin request,
            // otherwise the v2 snapshot revalidation legitimately aborts (not a worker test).
            var runtime = new FakeDnsRuntime { Host = "example.com", PortField = 31567, ModeEnabled = true };
            var backend = new FakeDnsBackend();
            var controller = new ExplicitDnsDirectIpController(backend, runtime);

            bool started = controller.TryBegin("example.com", 31567, "");
            if (!started) return false;
            if (runtime.ConnectCount != 0) return false;

            backend.InvokeCompletion(1, new IPAddress[] { IPAddress.Parse("1.2.3.4") });
            if (runtime.ConnectCount != 0) return false;

            controller.Tick();
            return runtime.ConnectCount == 1 && runtime.LastSharedPort == 31567;
        }

        // ===== D7: stale epoch discarded =====
        internal static bool Test_D7_StaleEpochDiscarded()
        {
            var runtime = new FakeDnsRuntime();
            var backend = new FakeDnsBackend();
            var controller = new ExplicitDnsDirectIpController(backend, runtime);

            controller.TryBegin("first.example", 31567, "");
            backend.InvokeCompletion(1, new IPAddress[] { IPAddress.Parse("1.2.3.4") });

            controller.CancelAndReset();

            controller.Tick();
            return runtime.ConnectCount == 0;
        }

        // ===== D8: timeout fail-closed =====
        internal static bool Test_D8_TimeoutFailClosed()
        {
            var runtime = new FakeDnsRuntime { FakeNow = 0f };
            var backend = new FakeDnsBackend();
            var controller = new ExplicitDnsDirectIpController(backend, runtime);

            controller.TryBegin("slow.example", 31567, "");
            runtime.FakeNow = 6f;
            controller.Tick();
            backend.InvokeCompletion(1, new IPAddress[] { IPAddress.Parse("1.2.3.4") });
            controller.Tick();
            return runtime.ConnectCount == 0 && !controller.IsWaiting;
        }

        // ===== D9: address filter =====
        internal static bool Test_D9_ResultAddressFilter()
        {
            IPAddress[] mixed = new[]
            {
                IPAddress.Parse("::1"),
                IPAddress.Parse("0.0.0.0"),
                IPAddress.Parse("255.255.255.255"),
                IPAddress.Parse("224.0.0.1"),
                IPAddress.Parse("9.9.9.9")
            };
            bool ok = ExplicitDnsDirectIpController.TrySelectFirstValidIpv4(
                mixed, out IPv4Address selected, out string reason);
            if (!ok) return false;
            if (selected.ToString() != "9.9.9.9") return false;

            bool allInvalid = !ExplicitDnsDirectIpController.TrySelectFirstValidIpv4(
                new[] { IPAddress.Parse("::1"), IPAddress.Parse("0.0.0.0") },
                out _, out _);
            return allInvalid;
        }

        // ===== D10: backend in-flight exact cap =====
        // Two incomplete TCS tasks fill the slots; the third TryBegin is rejected; completion zeroes.
        // v3 指令 D: every TCS created by the resolver must be completed in a finally block and the
        // test must end with InflightForTest == 0. Never mask an unfinished task with ResetForTest.
        internal static bool Test_D10_BackendInflightExactCap()
        {
            var pending = new List<TaskCompletionSource<IPAddress[]>>();
            Func<string, Task<IPAddress[]>> resolver = host =>
            {
                var tcs = new TaskCompletionSource<IPAddress[]>();
                pending.Add(tcs);
                return tcs.Task;
            };
            var backend = new SystemDnsResolveBackend(resolver);
            SystemDnsResolveBackend.ResetForTest();

            bool result;
            try
            {
                bool first = backend.TryBegin(1, "one.example", _ => { });
                bool second = backend.TryBegin(2, "two.example", _ => { });
                bool third = backend.TryBegin(3, "three.example", _ => { });
                if (!first || !second || third) return false;
                if (SystemDnsResolveBackend.InflightForTest != 2) return false;

                // Complete both; count must return to zero.
                pending[0].SetResult(new[] { IPAddress.Parse("1.1.1.1") });
                pending[1].SetResult(new[] { IPAddress.Parse("2.2.2.2") });
                // Allow continuations to run.
                WaitForInflightZero();

                // The 4th backend request leaves one task in flight (the exact leak 指令 D fixes).
                bool fourth = backend.TryBegin(4, "four.example", _ => { });
                if (!fourth) return false;
                if (SystemDnsResolveBackend.InflightForTest != 1) return false;

                result = true;
            }
            finally
            {
                // v3 指令 D: complete/cancel EVERY TCS so the backend continuation decrements
                // _inflight back to zero. TrySetResult is a no-op for already-completed tasks.
                foreach (var tcs in pending) tcs.TrySetResult(new[] { IPAddress.Parse("9.9.9.9") });
                WaitForInflightZero();
            }

            // v3 指令 D: the test must end with no in-flight task leaked into later tests.
            return result && SystemDnsResolveBackend.InflightForTest == 0;
        }

        // ===== D11: queue exact cap under concurrent completion =====
        // After many concurrent completions the queue must never exceed 8.
        internal static bool Test_D11_QueueExactCapConcurrent()
        {
            var runtime = new FakeDnsRuntime();
            var backend = new ConcurrentFakeDnsBackend();
            var controller = new ExplicitDnsDirectIpController(backend, runtime);

            controller.TryBegin("cap.example", 31567, "");

            // Fire many concurrent completions; controller atomically caps the queue at 8.
            backend.FireConcurrentCompletions(1, 20);
            if (controller.PendingQueueCount > 8) return false;

            // After draining, count must be bounded.
            controller.Tick();
            return controller.PendingQueueCount == 0;
        }

        // ===== D12: host changed before commit aborts =====
        internal static bool Test_D12_HostChangedBeforeCommitAborts()
        {
            var runtime = new FakeDnsRuntime { Host = "old.example", PortField = 31567, ModeEnabled = true };
            var backend = new FakeDnsBackend();
            var controller = new ExplicitDnsDirectIpController(backend, runtime);

            controller.TryBegin("old.example", 31567, "");
            // Player edits the address to a different host before DNS returns.
            runtime.Host = "new.example";
            backend.InvokeCompletion(1, new IPAddress[] { IPAddress.Parse("1.2.3.4") });
            controller.Tick();
            return runtime.ConnectCount == 0;
        }

        // ===== D13: port changed before commit aborts =====
        internal static bool Test_D13_PortChangedBeforeCommitAborts()
        {
            var runtime = new FakeDnsRuntime { Host = "host.example", PortField = 31567, ModeEnabled = true };
            var backend = new FakeDnsBackend();
            var controller = new ExplicitDnsDirectIpController(backend, runtime);

            controller.TryBegin("host.example", 31567, "");
            runtime.PortField = 32000; // changed
            backend.InvokeCompletion(1, new IPAddress[] { IPAddress.Parse("1.2.3.4") });
            controller.Tick();
            return runtime.ConnectCount == 0;
        }

        // ===== D14: toggle off before commit aborts =====
        internal static bool Test_D14_ToggleOffBeforeCommitAborts()
        {
            var runtime = new FakeDnsRuntime { Host = "host.example", PortField = 31567, ModeEnabled = true };
            var backend = new FakeDnsBackend();
            var controller = new ExplicitDnsDirectIpController(backend, runtime);

            controller.TryBegin("host.example", 31567, "");
            runtime.ModeEnabled = false; // toggle off
            backend.InvokeCompletion(1, new IPAddress[] { IPAddress.Parse("1.2.3.4") });
            controller.Tick();
            return runtime.ConnectCount == 0;
        }

        // ===== D15: main-thread assert failure aborts =====
        internal static bool Test_D15_MainThreadAssertFailureAborts()
        {
            var runtime = new FakeDnsRuntime { AssertThrows = true };
            var backend = new FakeDnsBackend();
            var controller = new ExplicitDnsDirectIpController(backend, runtime);

            controller.TryBegin("host.example", 31567, "");
            backend.InvokeCompletion(1, new IPAddress[] { IPAddress.Parse("1.2.3.4") });
            controller.Tick();
            return runtime.ConnectCount == 0 && !controller.IsWaiting;
        }

        // ===== D16: success clears password and pending =====
        internal static bool Test_D16_SuccessClearsPasswordAndPending()
        {
            var runtime = new FakeDnsRuntime();
            var backend = new FakeDnsBackend();
            var controller = new ExplicitDnsDirectIpController(backend, runtime);

            controller.TryBegin("host.example", 31567, "secret-pass");
            backend.InvokeCompletion(1, new IPAddress[] { IPAddress.Parse("1.2.3.4") });
            controller.Tick();

            // After success, no pending secret remains.
            return !controller.HasPendingSecretForTest && runtime.ConnectCount == 1;
        }

        // ===== D17: backend sync throw fails closed =====
        internal static bool Test_D17_BackendSyncThrowFailsClosed()
        {
            var runtime = new FakeDnsRuntime();
            var backend = new ThrowingFakeDnsBackend();
            var controller = new ExplicitDnsDirectIpController(backend, runtime);

            bool started = controller.TryBegin("host.example", 31567, "");
            return !started && !controller.IsWaiting && !controller.HasPendingSecretForTest;
        }

        // ===== D18: logs contain no host substring =====
        internal static bool Test_D18_LogsContainNoHostSubstring()
        {
            // Shape description must never contain the host or any substring of it.
            string desc = ExplicitDnsDirectIpController.DescribeHostForLog("frp-cat.com");
            if (desc.Contains("frp") || desc.Contains("cat") || desc.Contains("frp-cat.com")) return false;
            if (!desc.Contains("len=11") || !desc.Contains("labels=2")) return false;

            string empty = ExplicitDnsDirectIpController.DescribeHostForLog(null);
            return empty.Contains("len=0") && empty.Contains("labels=0");
        }

        // ===== D19: CancelAndReset drains queue without counter drift =====
        internal static bool Test_D19_CancelDrainsQueueWithoutDrift()
        {
            var runtime = new FakeDnsRuntime();
            var backend = new ConcurrentFakeDnsBackend();
            var controller = new ExplicitDnsDirectIpController(backend, runtime);

            controller.TryBegin("cap.example", 31567, "");
            backend.FireConcurrentCompletions(1, 20); // queue capped at 8, count=8
            if (controller.PendingQueueCount != 8) return false;

            controller.CancelAndReset();
            // Atomic dequeue helper must have drained every entry -> count back to 0.
            if (controller.PendingQueueCount != 0) return false;
            if (!controller.IsWaiting) return true;
            return false;
        }

        // ===== D20: cancel then late completion is drained by idle Tick =====
        // v3 指令 A: a completion that arrives AFTER cancel must be dropped by the epoch pre-check,
        // and if it wins the race window it must be drained by idle Tick, never accumulating.
        internal static bool Test_D20_LateCompletionAfterCancelIsDrained()
        {
            var runtime = new FakeDnsRuntime { Host = "late.example", PortField = 31567, ModeEnabled = true };
            var backend = new FakeDnsBackend();
            var controller = new ExplicitDnsDirectIpController(backend, runtime);

            if (!controller.TryBegin("late.example", 31567, "")) return false;
            // Cancel before the result is delivered.
            controller.CancelAndReset();
            if (controller.PendingQueueCount != 0) return false;

            // Late completion arrives on the stale epoch.
            backend.InvokeCompletion(1, new IPAddress[] { IPAddress.Parse("1.2.3.4") });
            // Idle Tick (not waiting) must drain whatever slipped into the queue.
            controller.Tick();
            return controller.PendingQueueCount == 0 && runtime.ConnectCount == 0 && !controller.IsWaiting;
        }

        // ===== D21: repeated cancel + late result never wedges a later TryBegin =====
        // v3 指令 A: at least 10 rounds; the next TryBegin must still succeed (queue never wedged).
        internal static bool Test_D21_RepeatedLateResultsNeverWedgeBegin()
        {
            var runtime = new FakeDnsRuntime { Host = "round.example", PortField = 31567, ModeEnabled = true };
            var backend = new ConcurrentFakeDnsBackend();
            var controller = new ExplicitDnsDirectIpController(backend, runtime);

            const int Rounds = 10;
            for (int i = 1; i <= Rounds; i++)
            {
                if (!controller.TryBegin("round.example", 31567, "")) return false;
                // Cancel while the request is in flight...
                controller.CancelAndReset();
                // ...then hammer it with many stale late completions racing the epoch.
                backend.FireConcurrentCompletions(i, 20);
                // Idle Tick drains whatever won the race window.
                controller.Tick();
                if (controller.PendingQueueCount != 0) return false;
            }

            // Round Rounds+1 must still begin successfully after all the churn.
            bool ok = controller.TryBegin("final.example", 31567, "");
            return ok && controller.IsWaiting && controller.PendingQueueCount == 0;
        }

        // ===== D22: DNS-path connect log contains no resolved node IP =====
        // v3 指令 C: the production connect log for the DNS path must never leak the resolved
        // SakuraFRP node IPv4 (address.ToString(), bytes, integer value, or substring).
        internal static bool Test_D22_DnsConnectLogContainsNoResolvedIp()
        {
            const string resolved = "203.0.113.42";

            // Assert on the REAL production builder (not a copied literal).
            string logLine = ProductionDnsConnectRuntime.BuildConnectLogLine(31567);

            if (logLine.Contains(resolved)) return false;
            if (logLine.Contains("203") || logLine.Contains("0.113") || logLine.Contains("113.42")) return false;
            if (!logLine.Contains("addressFamily=IPv4")) return false;
            if (!logLine.Contains("sharedPort=31567")) return false;

            // Also verify the controller drives Connect with the resolved address but never
            // forwards the address to the log (the pure builder has no address parameter at all).
            var runtime = new FakeDnsRuntime { Host = "node.frp", PortField = 31567, ModeEnabled = true };
            var backend = new FakeDnsBackend();
            var controller = new ExplicitDnsDirectIpController(backend, runtime);

            controller.TryBegin("node.frp", 31567, "");
            backend.InvokeCompletion(1, new IPAddress[] { IPAddress.Parse(resolved) });
            controller.Tick();
            return runtime.ConnectCount == 1 && runtime.LastAddressValue != 0;
        }

        // ===== D23: duplicate begin must not drain a live current-epoch result =====
        // v4 指令: TryBegin with a request already resolving returns false WITHOUT touching the
        // queue. A valid current-epoch result that Tick has not yet consumed must survive the
        // duplicate click and commit on the next Tick (no spurious timeout, no wrong connect).
        internal static bool Test_D23_DuplicateBeginDoesNotDrainCurrentResult()
        {
            var runtime = new FakeDnsRuntime { Host = "host.example", PortField = 26655, ModeEnabled = true };
            var backend = new FakeDnsBackend();
            var controller = new ExplicitDnsDirectIpController(backend, runtime);

            // 1. Begin succeeds.
            if (!controller.TryBegin("host.example", 26655, "")) return false;

            // 2. Completion enqueues a valid current-epoch result; queue must be 1.
            backend.InvokeCompletion(1, new IPAddress[] { IPAddress.Parse("1.2.3.4") });
            if (controller.PendingQueueCount != 1) return false;

            // 3. Duplicate begin before Tick must be rejected...
            if (controller.TryBegin("host.example", 26655, "")) return false;

            // 4. ...and must NOT drain the live result (queue stays 1).
            if (controller.PendingQueueCount != 1) return false;

            // 5. Tick commits the original request: connected once, queue empty, still resolving
            //    then idle (i.e. not lost to a timeout).
            controller.Tick();
            return runtime.ConnectCount == 1 &&
                   runtime.LastSharedPort == 26655 &&
                   controller.PendingQueueCount == 0 &&
                   !controller.IsWaiting;
        }

        // ===== UI lifecycle =====
        private static readonly ISleekElement FakeParent1 = new FakeSleekElement();
        private static readonly ISleekElement FakeParent2 = new FakeSleekElement();

        internal static bool Test_UI_DnsToggleParentLifecycle()
        {
            ExplicitDnsDirectIpModeUI.ResetForTest();
            ExplicitDnsDirectIpModeUI._testBypassGlazier = true;
            ExplicitDnsDirectIpModeUI._testBypassThreadAssert = true;
            ExplicitDnsDirectIpModeUI._testParentProvider = () => FakeParent1;
            // v2 指令 G: Provide button Y hooks so RepositionConnectButton succeeds in test env.
            float fakeY = 45f;
            ExplicitDnsDirectIpModeUI._testConnectButtonYReader = () => fakeY;
            ExplicitDnsDirectIpModeUI._testConnectButtonYWriter = y => { fakeY = y; };

            try
            {
                ExplicitDnsDirectIpModeUI.EnsureCreated();
                if (!ExplicitDnsDirectIpModeUI.IsCreatedForTest) return false;
                if (!ReferenceEquals(ExplicitDnsDirectIpModeUI.BoundParentForTest, FakeParent1))
                    return false;

                ExplicitDnsDirectIpModeUI.EnsureCreated();
                if (!ReferenceEquals(ExplicitDnsDirectIpModeUI.BoundParentForTest, FakeParent1))
                    return false;

                ExplicitDnsDirectIpModeUI._testParentProvider = () => FakeParent2;
                ExplicitDnsDirectIpModeUI.EnsureCreated();
                if (!ReferenceEquals(ExplicitDnsDirectIpModeUI.BoundParentForTest, FakeParent2))
                    return false;

                ExplicitDnsDirectIpModeUI._testParentProvider = () => null;
                ExplicitDnsDirectIpModeUI.EnsureCreated();
                return !ExplicitDnsDirectIpModeUI.IsCreatedForTest;
            }
            finally
            {
                ExplicitDnsDirectIpModeUI.ResetForTest();
            }
        }

        internal static bool Test_UI_DnsToggleDefaultOff()
        {
            ExplicitDnsDirectIpModeUI.ResetForTest();
            bool off = !ExplicitDnsDirectIpModeUI.IsEnabled;
            ExplicitDnsDirectIpModeUI.ResetForTest();
            return off;
        }

        // ===== UI3: Destroy restores connect button Y =====
        internal static bool Test_UI3_DestroyRestoresConnectButtonY()
        {
            float originalY = 45f;
            float currentY = 45f;
            ExplicitDnsDirectIpModeUI.ResetForTest();
            ExplicitDnsDirectIpModeUI._testBypassGlazier = true;
            ExplicitDnsDirectIpModeUI._testBypassThreadAssert = true;
            ExplicitDnsDirectIpModeUI._testParentProvider = () => FakeParent1;
            ExplicitDnsDirectIpModeUI._testConnectButtonYReader = () => currentY;
            ExplicitDnsDirectIpModeUI._testConnectButtonYWriter = y => { currentY = y; };

            try
            {
                ExplicitDnsDirectIpModeUI.EnsureCreated();
                // Button moved to 85.
                if (currentY != 85f) return false;
                if (!ExplicitDnsDirectIpModeUI.ConnectButtonMovedForTest) return false;

                ExplicitDnsDirectIpModeUI.Destroy();
                // Y restored to original.
                if (currentY != originalY) return false;
                if (ExplicitDnsDirectIpModeUI.IsCreatedForTest) return false;
                return true;
            }
            finally
            {
                ExplicitDnsDirectIpModeUI.ResetForTest();
            }
        }

        // ===== UI4: unified Shutdown destroys the toggle and restores the button =====
        // v3 指令 B: production plugin OnDestroy must call the unified
        // ExplicitDnsDirectIpService.Shutdown(), which cancels in-flight DNS AND destroys the
        // DNS toggle (restoring the vanilla connect button Y). Proves the production path, not
        // just a direct Destroy() call.
        internal static bool Test_UI4_ProductionShutdownDestroysToggle()
        {
            float originalY = 45f;
            float currentY = 45f;
            ExplicitDnsDirectIpModeUI.ResetForTest();
            ExplicitDnsDirectIpModeUI._testBypassGlazier = true;
            ExplicitDnsDirectIpModeUI._testBypassThreadAssert = true;
            ExplicitDnsDirectIpModeUI._testParentProvider = () => FakeParent1;
            ExplicitDnsDirectIpModeUI._testConnectButtonYReader = () => currentY;
            ExplicitDnsDirectIpModeUI._testConnectButtonYWriter = y => { currentY = y; };

            try
            {
                ExplicitDnsDirectIpModeUI.EnsureCreated();
                if (!ExplicitDnsDirectIpModeUI.IsCreatedForTest) return false;
                if (currentY != 85f) return false;

                // The unified production teardown path (exactly what plugin OnDestroy calls).
                ExplicitDnsDirectIpService.Shutdown();

                if (ExplicitDnsDirectIpModeUI.IsCreatedForTest) return false;
                if (currentY != originalY) return false;
                return true;
            }
            finally
            {
                ExplicitDnsDirectIpModeUI.ResetForTest();
            }
        }

        private static void WaitForInflightZero()
        {
            for (int i = 0; i < 100; i++)
            {
                if (SystemDnsResolveBackend.InflightForTest == 0) return;
                Thread.Sleep(10);
            }
        }
    }

    /// <summary>Fake DNS backend that captures the completion delegate (no real DNS).</summary>
    internal sealed class FakeDnsBackend : IExplicitDnsResolveBackend
    {
        private Action<ExplicitDnsResult> _completion;
        private int _capturedEpoch;
        private string _capturedHost;

        public bool TryBegin(int epoch, string host, Action<ExplicitDnsResult> completion)
        {
            _capturedEpoch = epoch;
            _capturedHost = host;
            _completion = completion;
            return true;
        }

        internal void InvokeCompletion(int epoch, IPAddress[] addresses, string error = null)
        {
            if (_completion == null) return;
            var result = new ExplicitDnsResult(
                epoch, _capturedHost ?? "host",
                addresses ?? Array.Empty<IPAddress>(), error);
            _completion(result);
        }
    }

    /// <summary>Fake backend that fires many concurrent completions.</summary>
    internal sealed class ConcurrentFakeDnsBackend : IExplicitDnsResolveBackend
    {
        private Action<ExplicitDnsResult> _completion;
        private int _capturedEpoch;
        private string _capturedHost;

        public bool TryBegin(int epoch, string host, Action<ExplicitDnsResult> completion)
        {
            _capturedEpoch = epoch;
            _capturedHost = host;
            _completion = completion;
            return true;
        }

        internal void FireConcurrentCompletions(int epoch, int count)
        {
            if (_completion == null) return;
            var tasks = new Task[count];
            for (int i = 0; i < count; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    _completion(new ExplicitDnsResult(
                        epoch, _capturedHost ?? "host",
                        new[] { IPAddress.Parse("1.2.3.4") }, null));
                });
            }
            Task.WaitAll(tasks);
        }
    }

    /// <summary>Fake backend whose TryBegin throws synchronously.</summary>
    internal sealed class ThrowingFakeDnsBackend : IExplicitDnsResolveBackend
    {
        public bool TryBegin(int epoch, string host, Action<ExplicitDnsResult> completion)
        {
            throw new InvalidOperationException("backend sync failure");
        }
    }

    /// <summary>Fake connect runtime; never touches Provider/Unity/UI.</summary>
    internal sealed class FakeDnsRuntime : IExplicitDnsConnectRuntime
    {
        public float FakeNow;
        public bool MenuActive = true;
        public bool AlreadyConnected = false; // explicit default silences CS0649 (only read)
        public bool AssertThrows;
        public string Host = "host.example";
        public ushort PortField = 31567;
        public bool ModeEnabled = true;

        public int ConnectCount;
        public uint LastAddressValue;
        public ushort LastSharedPort;
        public string LastPassword;

        public float Realtime => FakeNow;
        public bool IsMenuActive => MenuActive;
        public bool IsAlreadyConnected => AlreadyConnected;

        public void AssertMainThread()
        {
            if (AssertThrows) throw new InvalidOperationException("not on game thread");
        }

        public bool TryCaptureCurrentInput(out string rawHost, out ushort portFieldValue, out bool explicitDnsEnabled)
        {
            rawHost = Host;
            portFieldValue = PortField;
            explicitDnsEnabled = ModeEnabled;
            return true;
        }

        public void Connect(IPv4Address address, ushort sharedPort, string password)
        {
            ConnectCount++;
            LastAddressValue = address.value;
            LastSharedPort = sharedPort;
            LastPassword = password;
        }

        public void ShowAlert(string message) { }
    }
}
