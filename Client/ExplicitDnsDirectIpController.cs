using SDG.Unturned;
using SteamP2PFriends.Shared;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Unturned.SystemEx;
using UnityEngine;

namespace SteamP2PFriends.Client
{
    /// <summary>
    /// Stage 9-3 (v2): runtime contract used by the DNS controller to commit a resolved connection
    /// on the main thread. Test implementations substitute a fake to prove worker never connects.
    /// TryCaptureCurrentInput reads the live vanilla UI fields + DNS toggle on the main thread so
    /// the commit step can revalidate the exact current snapshot (no "display B, connect A").
    /// </summary>
    internal interface IExplicitDnsConnectRuntime
    {
        float Realtime { get; }
        bool IsMenuActive { get; }
        bool IsAlreadyConnected { get; }
        void AssertMainThread();
        bool TryCaptureCurrentInput(out string rawHost, out ushort portFieldValue, out bool explicitDnsEnabled);
        void Connect(IPv4Address address, ushort sharedPort, string password);
        void ShowAlert(string message);
    }

    /// <summary>
    /// Production runtime backed by vanilla Provider.connect. AssertMainThread must NOT swallow the
    /// exception (v2 指令 A); the controller catches it and fail-closes.
    /// </summary>
    internal sealed class ProductionDnsConnectRuntime : IExplicitDnsConnectRuntime
    {
        public float Realtime
        {
            get { try { return Time.realtimeSinceStartup; } catch { return 0f; } }
        }

        public bool IsMenuActive
        {
            get { try { return MenuPlayConnectUI.active; } catch { return false; } }
        }

        public bool IsAlreadyConnected
        {
            get { try { return Provider.isConnected; } catch { return false; } }
        }

        public void AssertMainThread()
        {
            // v2 指令 A: must NOT swallow. The controller catches and fail-closes.
            ThreadUtil.assertIsGameThread();
        }

        public bool TryCaptureCurrentInput(out string rawHost, out ushort portFieldValue, out bool explicitDnsEnabled)
        {
            rawHost = null;
            portFieldValue = 0;
            explicitDnsEnabled = false;
            try
            {
                explicitDnsEnabled = SteamP2PFriends.UI.ExplicitDnsDirectIpModeUI.IsEnabled;
                var hostField = SteamP2PFriends.Patches.MenuPlayConnectP2PRoutePatch.GetStaticField<ISleekField>("hostField");
                var portField = SteamP2PFriends.Patches.MenuPlayConnectP2PRoutePatch.GetStaticField<ISleekUInt16Field>("portField");
                if (hostField == null || portField == null) return false;
                rawHost = hostField.Text;
                portFieldValue = portField.Value;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Connect(IPv4Address address, ushort sharedPort, string password)
        {
            var parameters = new ServerConnectParameters(address, sharedPort, sharedPort, password);
            // v3 指令 C: never log the resolved node IPv4 on the DNS path (SakuraFRP may hide it
            // from the player). Only family + ports are emitted. Pure builder is test-verifiable.
            RoleLogger.Info("[Client]", BuildConnectLogLine(sharedPort));
            Provider.connect(parameters, null, null);
        }

        /// <summary>
        /// v3 指令 C: pure builder for the DNS-path connect log. Must never contain the resolved
        /// node IPv4 (address.ToString, bytes, integer value or any substring). D22 asserts on it.
        /// </summary>
        internal static string BuildConnectLogLine(ushort sharedPort)
        {
            return "[DirectIP-DNS] main-thread connect addressFamily=IPv4" +
                   " sharedPort=" + sharedPort + " queryPort=" + sharedPort +
                   " connectionPort=" + sharedPort;
        }

        public void ShowAlert(string message)
        {
            try { MenuUI.alert(message); } catch { }
        }
    }

    /// <summary>
    /// Stage 9-3 (v2): main-thread DNS direct-IP controller.
    ///
    /// Thread model:
    ///   - Async DNS worker only constructs an immutable ExplicitDnsResult and enqueues it via an
    ///     atomic bounded counter. It never calls Provider/Unity/Glazier/UI.
    ///   - Plugin.Update drives Tick() on the main thread, which drains the queue, revalidates the
    ///     live UI snapshot (指令 B), filters the address set, and calls Provider.connect.
    ///
    /// State machine: Idle -> Resolving -> (Connected | Failed | Timeout | Canceled) -> Idle.
    /// epoch increments on every begin/cancel/reset; stale results are discarded by epoch mismatch.
    /// Timeout 5s; in-flight <= 2; queue <= 8 (atomic counter).
    /// </summary>
    internal sealed class ExplicitDnsDirectIpController
    {
        private const int MaxInflight = 2;
        private const int MaxQueue = 8;
        private const float TimeoutSeconds = 5f;

        private readonly IExplicitDnsResolveBackend _backend;
        private readonly IExplicitDnsConnectRuntime _runtime;

        private readonly ConcurrentQueue<ExplicitDnsResult> _queue =
            new ConcurrentQueue<ExplicitDnsResult>();
        private int _queuedCount;
        private int _epoch;
        private bool _waiting;
        private float _startedAt;
        private string _pendingHost;
        private ushort _pendingPort;
        private string _pendingPassword;

        internal ExplicitDnsDirectIpController(
            IExplicitDnsResolveBackend backend,
            IExplicitDnsConnectRuntime runtime)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        }

        internal bool IsWaiting => _waiting;
        internal int CurrentEpoch => Volatile.Read(ref _epoch);
        internal int PendingQueueCount => Volatile.Read(ref _queuedCount);
        internal bool HasPendingSecretForTest =>
            !string.IsNullOrEmpty(_pendingPassword) || !string.IsNullOrEmpty(_pendingHost) || _pendingPort != 0;

        /// <summary>Begins an explicit DNS resolution. Returns false on cap/state violations.</summary>
        internal bool TryBegin(string host, ushort sharedPort, string password)
        {
            // v4 指令: when a request is already resolving, reject immediately WITHOUT touching the
            // result queue. Draining here would delete a live current-epoch result that Tick has
            // not yet consumed (double-click / repeat click -> spurious timeout).
            if (_waiting) return false;

            // v3 指令 A: drain any late results from a previous canceled/expired request so a
            // historical stale queue can never wedge TryBegin (capacity check below is then safe).
            // Only run in the Idle state (already proven by the _waiting check above).
            DrainQueuedResults();

            if (string.IsNullOrEmpty(host) || sharedPort == 0) return false;
            if (Volatile.Read(ref _queuedCount) >= MaxQueue) return false;

            int epoch = Interlocked.Increment(ref _epoch);
            try
            {
                if (!_backend.TryBegin(epoch, host, EnqueueResult))
                {
                    // v2 指令 I: epoch must only monotonically increase; do NOT decrement.
                    ClearPendingState(epoch);
                    return false;
                }
            }
            catch (Exception ex)
            {
                // v2 指令 I: backend synchronous exception converges; never leak to Harmony/UI.
                RoleLogger.Warn("[Client]",
                    "[DirectIP-DNS] backend TryBegin threw (" + ex.GetType().Name + "); fail-closed.");
                ClearPendingState(epoch);
                return false;
            }

            _waiting = true;
            _startedAt = _runtime.Realtime;
            _pendingHost = host;
            _pendingPort = sharedPort;
            _pendingPassword = password ?? string.Empty;
            return true;
        }

        /// <summary>Main-thread drain and commit. Must be called from Plugin.Update.</summary>
        internal void Tick()
        {
            // v3 指令 A: when idle, drain any late completions so stale results never accumulate
            // into a permanent wedge (repeated cancel -> late completion would otherwise fill the
            // 8-slot queue and permanently block TryBegin).
            if (!_waiting)
            {
                DrainQueuedResults();
                return;
            }

            // v2 指令 A: main-thread assertion gate first; fail-closed without connecting.
            if (!TryAssertMainThread(out string threadReason))
            {
                _runtime.ShowAlert(threadReason ?? "域名直连线程状态无效。");
                CancelAndReset();
                return;
            }

            float now = _runtime.Realtime;
            if (now - _startedAt >= TimeoutSeconds)
            {
                CancelAndReset();
                _runtime.ShowAlert("域名解析超时，未发起连接。");
                return;
            }

            ExplicitDnsResult result;
            while (TryDequeueResult(out result))
            {
                if (result.Epoch != CurrentEpoch) continue; // stale
                if (!_waiting) return;

                // v2 指令 B: revalidate live UI snapshot + original requested snapshot.
                if (!RevalidatePreconditions(out string rejectReason))
                {
                    _runtime.ShowAlert(rejectReason ?? "域名解析已取消，未发起连接。");
                    CancelAndReset();
                    return;
                }

                if (result.ErrorType != null)
                {
                    _runtime.ShowAlert("域名解析失败（" + result.ErrorType + "），未发起连接。");
                    CancelAndReset();
                    return;
                }

                if (!TrySelectFirstValidIpv4(result.Addresses, out IPv4Address selected, out string filterReason))
                {
                    _runtime.ShowAlert(filterReason ?? "未找到有效 IPv4 地址，未发起连接。");
                    CancelAndReset();
                    return;
                }

                RoleLogger.Info("[Client]",
                    "[DirectIP-DNS] resolved hostShape=" + DescribeHostForLog(_pendingHost) +
                    " candidateCount=" + result.Addresses.Length +
                    " sharedPort=" + _pendingPort + " queryPort=" + _pendingPort +
                    " connectionPort=" + _pendingPort);

                // v2 指令 D: clear password + pending BEFORE connect; secret lifetime bounded.
                ushort port = _pendingPort;
                string password = _pendingPassword ?? string.Empty;
                ClearPendingState(CurrentEpoch);

                try
                {
                    _runtime.Connect(selected, port, password);
                }
                finally
                {
                    password = null;
                }
                return;
            }
        }

        internal void CancelAndReset()
        {
            ClearPendingState(CurrentEpoch);
            while (TryDequeueResult(out _)) { }
        }

        /// <summary>Clears pending secret + host + port and invalidates the epoch.</summary>
        private void ClearPendingState(int epoch)
        {
            _waiting = false;
            _pendingHost = null;
            _pendingPort = 0;
            _pendingPassword = null;
            if (epoch == CurrentEpoch) Interlocked.Increment(ref _epoch);
        }

        // ===== v2 指令 A: main-thread assertion =====

        private bool TryAssertMainThread(out string reason)
        {
            try
            {
                _runtime.AssertMainThread();
                reason = null;
                return true;
            }
            catch (Exception ex)
            {
                reason = "域名直连线程状态无效（" + ex.GetType().Name + "）。";
                return false;
            }
        }

        // ===== v2 指令 B: live snapshot revalidation =====

        private bool RevalidatePreconditions(out string rejectReason)
        {
            rejectReason = null;
            if (!TryAssertMainThread(out string threadReason))
            {
                rejectReason = threadReason;
                return false;
            }
            if (!SteamP2PFriendsPlugin.DiagnosticBuildValid)
            {
                rejectReason = "SteamP2PFriends 自检未通过，域名直连已禁用。";
                return false;
            }
            if (!_runtime.IsMenuActive)
            {
                rejectReason = "直连菜单已关闭，域名解析已取消。";
                return false;
            }
            if (_runtime.IsAlreadyConnected)
            {
                rejectReason = "已连接到服务器，未发起新的域名直连。";
                return false;
            }
            if (!_waiting || string.IsNullOrEmpty(_pendingHost) || _pendingPort == 0)
            {
                rejectReason = "域名解析已取消，未发起连接。";
                return false;
            }

            // v2 指令 B: re-read the live UI fields + toggle, then re-run the same pure function.
            if (!_runtime.TryCaptureCurrentInput(
                    out string currentRaw, out ushort currentPortField, out bool modeEnabled) ||
                !modeEnabled)
            {
                rejectReason = "域名直连模式已关闭或界面输入不可用。";
                return false;
            }

            if (!UnifiedJoinAddressClassifier.TryBuildExplicitDnsEndpoint(
                    currentRaw, currentPortField, out string currentHost, out ushort currentSharedPort))
            {
                rejectReason = "当前域名或端口已无效。";
                return false;
            }

            if (!string.Equals(currentHost, _pendingHost, StringComparison.Ordinal) ||
                currentSharedPort != _pendingPort)
            {
                rejectReason = "连接信息已改变，请重新点击连接。";
                return false;
            }

            return true;
        }

        // ===== v2 指令 E: atomic bounded queue =====

        private void EnqueueResult(ExplicitDnsResult result)
        {
            if (result == null) return;

            // v3 指令 A: check epoch BEFORE reserving queue capacity. If the epoch already
            // advanced (cancel/expire/reset), drop immediately without touching the counter.
            if (result.Epoch != CurrentEpoch) return;

            while (true)
            {
                int observed = Volatile.Read(ref _queuedCount);
                if (observed >= MaxQueue) return;
                if (Interlocked.CompareExchange(
                        ref _queuedCount, observed + 1, observed) == observed)
                    break;
            }

            // v3 指令 A: re-check epoch AFTER reserving capacity; if it became stale meanwhile,
            // release the reserved slot and drop. This closes the cancel-vs-completion race.
            if (result.Epoch != CurrentEpoch)
            {
                Interlocked.Decrement(ref _queuedCount);
                return;
            }

            try { _queue.Enqueue(result); }
            catch
            {
                Interlocked.Decrement(ref _queuedCount);
                throw;
            }
        }

        private bool TryDequeueResult(out ExplicitDnsResult result)
        {
            if (!_queue.TryDequeue(out result)) return false;
            Interlocked.Decrement(ref _queuedCount);
            return true;
        }

        /// <summary>
        /// v3 指令 A: drains the queue on the main thread. Called by idle Tick and by TryBegin
        /// before the capacity check so a historical stale queue can never wedge a new request.
        /// </summary>
        private void DrainQueuedResults()
        {
            while (TryDequeueResult(out _)) { }
        }

        /// <summary>
        /// v2 指令 D: accepts only InterNetwork (IPv4). Rejects 0.0.0.0, 255.255.255.255,
        /// multicast 224.0.0.0/4 and IPv6. Uses the first valid address in DNS return order.
        /// </summary>
        internal static bool TrySelectFirstValidIpv4(IPAddress[] addresses,
            out IPv4Address selected, out string reason)
        {
            selected = default;
            reason = null;
            if (addresses == null || addresses.Length == 0)
            {
                reason = "DNS 未返回地址。";
                return false;
            }

            for (int i = 0; i < addresses.Length; i++)
            {
                IPAddress addr = addresses[i];
                if (addr == null) continue;
                if (addr.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue; // IPv6 not accepted in this stage
                }

                byte[] bytes = addr.GetAddressBytes();
                if (bytes == null || bytes.Length != 4) continue;

                uint v = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) |
                         ((uint)bytes[2] << 8) | bytes[3];

                // 0.0.0.0
                if (v == 0u) continue;
                // 255.255.255.255
                if (v == 0xFFFFFFFFu) continue;
                // multicast 224.0.0.0/4
                if ((v >> 28) == 0xEu) continue;

                selected = new IPv4Address(v);
                reason = null;
                return true;
            }

            reason = "DNS 结果中没有有效 IPv4 地址。";
            return false;
        }

        /// <summary>
        /// v2 指令 H: irreversible shape-only description of the host for logging.
        /// Never emits the full domain or any substring of it.
        /// </summary>
        internal static string DescribeHostForLog(string host)
        {
            if (string.IsNullOrEmpty(host)) return "len=0 labels=0";
            int labels = 1;
            for (int i = 0; i < host.Length; i++)
                if (host[i] == '.') labels++;
            return "len=" + host.Length + " labels=" + labels;
        }
    }

    /// <summary>
    /// Stage 9-3 (v2): production singleton holder. Created lazily with the system DNS backend and
    /// the production connect runtime. Plugin.Update drives Tick; RoutePatch calls TryBegin;
    /// OnDestroy / session reset / toggle-off calls CancelAndReset.
    /// </summary>
    internal static class ExplicitDnsDirectIpService
    {
        private static ExplicitDnsDirectIpController _instance;

        internal static ExplicitDnsDirectIpController Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new ExplicitDnsDirectIpController(
                        new SystemDnsResolveBackend(),
                        new ProductionDnsConnectRuntime());
                }
                return _instance;
            }
        }

        internal static void Tick() => Instance.Tick();
        internal static void CancelAndReset() => Instance.CancelAndReset();

        /// <summary>
        /// v3 指令 B: unified teardown. Cancels any in-flight resolution AND destroys the DNS toggle
        /// UI (restoring the vanilla connect button Y). Called from plugin OnDestroy.
        /// </summary>
        internal static void Shutdown()
        {
            try { if (_instance != null) _instance.CancelAndReset(); } catch { }
            try { SteamP2PFriends.UI.ExplicitDnsDirectIpModeUI.Destroy(); } catch { }
        }

        internal static void ResetForTest()
        {
            if (_instance != null) _instance.CancelAndReset();
            SystemDnsResolveBackend.ResetForTest();
        }
    }
}
