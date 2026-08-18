using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace SteamP2PFriends.Client
{
    /// <summary>
    /// thread can discard stale results. The worker only constructs this immutable snapshot and
    /// enqueues it; it never touches Unity, Glazier, Provider or UI.
    /// </summary>
    internal sealed class ExplicitDnsResult
    {
        internal readonly int Epoch;
        internal readonly string Host;
        internal readonly IPAddress[] Addresses;
        internal readonly string ErrorType;

        internal ExplicitDnsResult(int epoch, string host,
            IPAddress[] addresses, string errorType)
        {
            Epoch = epoch;
            Host = host;
            Addresses = addresses ?? Array.Empty<IPAddress>();
            ErrorType = errorType;
        }
    }

    /// <summary>
    /// </summary>
    internal interface IExplicitDnsResolveBackend
    {
        bool TryBegin(int epoch, string host,
            Action<ExplicitDnsResult> completion);
    }

    /// <summary>
    /// Worker/continuation never accesses Unity, Glazier, Provider.connect or the game thread.
    /// In-flight is capped (MaxInflight). 指令 F: constructor accepts an injectable resolver so
    /// tests can fault-inject with incomplete TaskCompletionSource tasks (no fake ResetForTest).
    /// </summary>
    internal sealed class SystemDnsResolveBackend : IExplicitDnsResolveBackend
    {
        private const int MaxInflight = 2;
        private readonly Func<string, Task<IPAddress[]>> _resolveAsync;
        private static int _inflight;

        internal static int InflightForTest => Volatile.Read(ref _inflight);
        internal const int MaxInflightForTest = MaxInflight;

        internal SystemDnsResolveBackend(
            Func<string, Task<IPAddress[]>> resolveAsync = null)
        {
            _resolveAsync = resolveAsync ?? Dns.GetHostAddressesAsync;
        }

        public bool TryBegin(int epoch, string host,
            Action<ExplicitDnsResult> completion)
        {
            if (completion == null || string.IsNullOrEmpty(host)) return false;
            if (Interlocked.Increment(ref _inflight) > MaxInflight)
            {
                Interlocked.Decrement(ref _inflight);
                return false;
            }

            try
            {
                _resolveAsync(host).ContinueWith(task =>
                {
                    try
                    {
                        if (task.Status == TaskStatus.RanToCompletion)
                            completion(new ExplicitDnsResult(epoch, host, task.Result, null));
                        else
                            completion(new ExplicitDnsResult(epoch, host,
                                Array.Empty<IPAddress>(),
                                task.Exception?.GetBaseException().GetType().Name ?? "DnsCanceled"));
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _inflight);
                    }
                }, TaskScheduler.Default);
                return true;
            }
            catch
            {
                Interlocked.Decrement(ref _inflight);
                throw;
            }
        }

        internal static void ResetForTest()
        {
            Volatile.Write(ref _inflight, 0);
        }
    }
}
