using System.Threading;

namespace SteamP2PFriends.Shared
{
    /// <summary>
    /// Controls optional diagnostic tracing without touching Unturned state.
    /// This is safe to configure during early plugin startup before the game thread exists.
    /// </summary>
    internal static class PluginLogPolicy
    {
        private static int _verboseDiagnostics;

        internal static void ConfigureEarly(bool verboseDiagnostics)
        {
            Interlocked.Exchange(ref _verboseDiagnostics, verboseDiagnostics ? 1 : 0);
        }

        internal static bool IsVerboseDiagnosticsEnabled
        {
            get { return Volatile.Read(ref _verboseDiagnostics) == 1; }
        }
    }
}
