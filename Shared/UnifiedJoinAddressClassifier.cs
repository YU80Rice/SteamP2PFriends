using Steamworks;
using Unturned.SystemEx;

namespace SteamP2PFriends.Shared
{
    internal enum UnifiedJoinAddressKind : byte
    {
        Vanilla = 0,
        SteamP2P = 1
    }

    /// <summary>
    /// Stage 7-8: side-effect-free protocol classifier for the vanilla direct-connect field.
    /// Only individual Steam accounts are claimed by the plugin. IP/DNS, URLs and game-server
    /// Steam codes remain owned by MenuPlayConnectUI.
    /// </summary>
    internal static class UnifiedJoinAddressClassifier
    {
        internal static UnifiedJoinAddressKind Classify(string raw, out ulong steamId)
        {
            steamId = 0UL;
            string value = (raw ?? string.Empty).Trim();
            if (value.Length < 6 || !ulong.TryParse(value, out ulong parsed))
            {
                return UnifiedJoinAddressKind.Vanilla;
            }

            CSteamID candidate = new CSteamID(parsed);
            if (!candidate.IsValid() || !candidate.BIndividualAccount())
            {
                return UnifiedJoinAddressKind.Vanilla;
            }

            steamId = parsed;
            return UnifiedJoinAddressKind.SteamP2P;
        }

        /// <summary>
        /// Parses the numeric IPv4 forms owned by the plugin's query-less listen-host route.
        /// The shared port remains the vanilla query port, while Steam Networking Sockets
        /// listens for game traffic on queryPort + 1.
        /// </summary>
        internal static bool TryBuildDirectIpEndpoint(string raw, ushort portFieldValue,
            out IPv4Address address, out ushort queryPort, out ushort connectionPort)
        {
            address = default;
            queryPort = 0;
            connectionPort = 0;

            string host = (raw ?? string.Empty).Trim();
            if (host.Length == 0 || host.IndexOf('/') >= 0) return false;

            ushort selectedPort = portFieldValue;
            int delimiter = host.LastIndexOf(':');
            if (delimiter >= 0)
            {
                // IPv6 is intentionally outside Stage 9 scope. More than one colon is not IPv4:port.
                if (delimiter != host.IndexOf(':')) return false;
                if (!ushort.TryParse(host.Substring(delimiter + 1), out selectedPort)) return false;
                host = host.Substring(0, delimiter).Trim();
            }

            if (selectedPort == 0 || selectedPort == ushort.MaxValue) return false;
            if (!IPv4Address.TryParse(host, out address) || address.IsZero) return false;

            queryPort = selectedPort;
            connectionPort = (ushort)(selectedPort + 1);
            return true;
        }
    }
}
