using SteamP2PFriends.Patches;

namespace SteamP2PFriends.WhitelistTests
{
    internal static class AuthorityGenerationGateTests
    {
        internal static bool Test_G1_FirstCommitBlocksSecondProducer()
        {
            AuthoritativeRegionGenerationLedger ledger =
                AuthoritativeItemGenerationGatePatch.CreateLedgerForTests(8);

            if (!ledger.TryBegin(2, 3, out RegionGenerationToken first)) return false;
            if (!first.OwnsTransaction) return false;
            if (!ledger.Commit(first)) return false;
            if (ledger.GetState(2, 3) != RegionGenerationState.Committed) return false;
            if (ledger.TryBegin(2, 3, out RegionGenerationToken second)) return false;
            return !second.OwnsTransaction;
        }

        internal static bool Test_G2_AbortAllowsRetry()
        {
            AuthoritativeRegionGenerationLedger ledger =
                AuthoritativeItemGenerationGatePatch.CreateLedgerForTests(8);

            if (!ledger.TryBegin(1, 1, out RegionGenerationToken first)) return false;
            if (!ledger.Abort(first)) return false;
            if (ledger.GetState(1, 1) != RegionGenerationState.NotStarted) return false;
            return ledger.TryBegin(1, 1, out RegionGenerationToken retry) && retry.OwnsTransaction;
        }

        internal static bool Test_G3_ResetInvalidatesOldEpoch()
        {
            AuthoritativeRegionGenerationLedger ledger =
                AuthoritativeItemGenerationGatePatch.CreateLedgerForTests(8);

            if (!ledger.TryBegin(4, 4, out RegionGenerationToken oldToken)) return false;
            int oldEpoch = oldToken.Epoch;
            ledger.Reset();
            if (ledger.Epoch == oldEpoch) return false;
            if (ledger.Commit(oldToken)) return false;
            if (ledger.GetState(4, 4) != RegionGenerationState.NotStarted) return false;
            return ledger.TryBegin(4, 4, out RegionGenerationToken newToken)
                && newToken.OwnsTransaction
                && newToken.Epoch == ledger.Epoch;
        }

        internal static bool Test_G4_PreparingRejectsReentry()
        {
            AuthoritativeRegionGenerationLedger ledger =
                AuthoritativeItemGenerationGatePatch.CreateLedgerForTests(8);

            if (!ledger.TryBegin(6, 2, out RegionGenerationToken owner)) return false;
            if (ledger.GetState(6, 2) != RegionGenerationState.Preparing) return false;
            if (ledger.TryBegin(6, 2, out RegionGenerationToken reentrant)) return false;
            if (reentrant.OwnsTransaction) return false;
            return ledger.Commit(owner)
                && ledger.GetState(6, 2) == RegionGenerationState.Committed;
        }
    }
}
