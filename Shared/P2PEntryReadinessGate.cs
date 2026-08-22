namespace SteamP2PFriends.Shared
{
    /// <summary>
    /// Separates a successfully loaded diagnostic build from an operable P2P entry point.
    /// Route B may expose UI only after its Provider lifecycle hooks are installed and verified.
    /// All callers are on the Unturned game thread.
    /// </summary>
    internal sealed class P2PEntryReadinessGate
    {
        private bool _routeBLifecycleReady;

        internal bool IsReady(bool diagnosticBuildValid, bool authHandshakeRegistrationValid)
        {
            return diagnosticBuildValid && authHandshakeRegistrationValid && _routeBLifecycleReady;
        }

        internal bool TryMarkRouteBLifecycleReady(bool lifecycleHooksInstalled, bool routeBRegistrationValid)
        {
            if (!lifecycleHooksInstalled || !routeBRegistrationValid) return false;
            _routeBLifecycleReady = true;
            return true;
        }

        internal void Reset()
        {
            _routeBLifecycleReady = false;
        }

        internal bool RouteBLifecycleReadyForTest => _routeBLifecycleReady;
    }
}
