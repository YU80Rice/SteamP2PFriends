namespace SteamP2PFriends.Host
{
    internal enum EP2PSessionAdminAction
    {
        Preserve = 0,
        Grant = 1,
        Revoke = 2
    }

    /// <summary>
    /// Decides the effective admin state for the current P2P connection only.
    /// This policy must never mutate SteamAdminlist because the room toggle is session-scoped.
    /// </summary>
    internal static class P2PSessionAdminPolicy
    {
        internal static EP2PSessionAdminAction Decide(bool allowOthersCheats, bool isLocalHost)
        {
            if (isLocalHost)
                return allowOthersCheats ? EP2PSessionAdminAction.Grant : EP2PSessionAdminAction.Preserve;

            return allowOthersCheats ? EP2PSessionAdminAction.Grant : EP2PSessionAdminAction.Revoke;
        }
    }
}
