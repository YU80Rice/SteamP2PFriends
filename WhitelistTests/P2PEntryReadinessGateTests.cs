using SteamP2PFriends.Shared;

namespace SteamP2PFriends.WhitelistTests
{
    internal static class P2PEntryReadinessGateTests
    {
        internal static bool Test_E1_EarlyMenuCannotExposeEntry()
        {
            var gate = new P2PEntryReadinessGate();
            return !gate.IsReady(diagnosticBuildValid: true, authHandshakeRegistrationValid: true) &&
                !gate.RouteBLifecycleReadyForTest;
        }

        internal static bool Test_E2_FailedLifecycleCannotExposeEntry()
        {
            var gate = new P2PEntryReadinessGate();
            return !gate.TryMarkRouteBLifecycleReady(lifecycleHooksInstalled: false, routeBRegistrationValid: true) &&
                !gate.TryMarkRouteBLifecycleReady(lifecycleHooksInstalled: true, routeBRegistrationValid: false) &&
                !gate.IsReady(diagnosticBuildValid: true, authHandshakeRegistrationValid: true);
        }

        internal static bool Test_E3_SuccessIsIdempotentAndResetFailsClosed()
        {
            var gate = new P2PEntryReadinessGate();
            if (!gate.TryMarkRouteBLifecycleReady(lifecycleHooksInstalled: true, routeBRegistrationValid: true)) return false;
            if (!gate.TryMarkRouteBLifecycleReady(lifecycleHooksInstalled: true, routeBRegistrationValid: true)) return false;
            if (!gate.IsReady(diagnosticBuildValid: true, authHandshakeRegistrationValid: true) ||
                gate.IsReady(diagnosticBuildValid: false, authHandshakeRegistrationValid: true)) return false;
            gate.Reset();
            return !gate.IsReady(diagnosticBuildValid: true, authHandshakeRegistrationValid: true) &&
                !gate.RouteBLifecycleReadyForTest;
        }

        internal static bool Test_E4_HandshakeCompatibilityFailureCannotExposeEntry()
        {
            var gate = new P2PEntryReadinessGate();
            if (!gate.TryMarkRouteBLifecycleReady(lifecycleHooksInstalled: true, routeBRegistrationValid: true))
                return false;
            return !gate.IsReady(diagnosticBuildValid: true, authHandshakeRegistrationValid: false);
        }
    }
}
