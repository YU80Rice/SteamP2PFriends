using SDG.Unturned;
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
        /// UDP port. queryPort == connectionPort == the entered port (single-port semantics).
        /// SakuraFRP maps any remote UDP port R to the host's local 27016; the client enters R.
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
                if (delimiter != host.IndexOf(':')) return false;
                if (!ushort.TryParse(host.Substring(delimiter + 1), out selectedPort)) return false;
                host = host.Substring(0, delimiter).Trim();
            }

            // Single-port semantics: reject port 0, but 65535 is valid (no +1 overflow anymore).
            if (selectedPort == 0) return false;
            if (!IPv4Address.TryParse(host, out address) || address.IsZero) return false;

            queryPort = selectedPort;
            connectionPort = selectedPort;
            return true;
        }

        /// <summary>
        /// both the query port and the connection port. Used only for query-port projection and
        /// tests. Must never be used for whitelist or player authorization.
        /// </summary>
        internal static bool IsSinglePortDirectIpParameters(ServerConnectParameters parameters)
        {
            return parameters != null
                && !parameters.address.IsZero
                && parameters.connectionPort != 0
                && parameters.queryPort == parameters.connectionPort;
        }

        /// <summary>
        /// mode. Accepts any legal ASCII DNS hostname plus the entered port (single-port semantics).
        /// Never hardcodes a SakuraFRP / provider suffix, node IP or remote port.
        /// Rejects numeric IPv4, SteamIDs, URLs, IPv6/multi-colon, empty labels, consecutive dots,
        /// leading/trailing hyphens, oversize labels and port 0.
        /// </summary>
        internal static bool TryBuildExplicitDnsEndpoint(
            string rawHost, ushort portFieldValue,
            out string normalizedHost, out ushort sharedPort)
        {
            normalizedHost = null;
            sharedPort = 0;

            string value = (rawHost ?? string.Empty).Trim();
            if (value.Length == 0 || value.Length > 253 || value.IndexOf('/') >= 0)
                return false;

            ushort selectedPort = portFieldValue;
            int colon = value.LastIndexOf(':');
            if (colon >= 0)
            {
                if (colon != value.IndexOf(':')) return false;
                if (!ushort.TryParse(value.Substring(colon + 1), out selectedPort)) return false;
                value = value.Substring(0, colon).Trim();
            }

            if (selectedPort == 0) return false;
            if (IPv4Address.TryParse(value, out uint _)) return false;
            if (Classify(value, out _) == UnifiedJoinAddressKind.SteamP2P) return false;
            if (!IsValidAsciiDnsName(value)) return false;

            normalizedHost = value.TrimEnd('.').ToLowerInvariant();
            sharedPort = selectedPort;
            return normalizedHost.Length > 0;
        }

        /// <summary>
        /// Allowed characters: A-Z a-z 0-9 '-' '.'. Rejects empty labels, consecutive dots,
        /// leading/trailing hyphens per label, and non-ASCII / control characters.
        /// IDN/Punycode is not converted; an "xn--" input is validated as ordinary ASCII labels.
        /// </summary>
        internal static bool IsValidAsciiDnsName(string candidate)
        {
            string value = candidate ?? string.Empty;
            if (value.Length == 0 || value.Length > 253) return false;
            if (value[0] == '.' || value[value.Length - 1] == '.') return false;

            int labelStart = 0;
            for (int i = 0; i <= value.Length; i++)
            {
                if (i == value.Length || value[i] == '.')
                {
                    int labelLength = i - labelStart;
                    if (labelLength == 0 || labelLength > 63) return false;
                    if (value[labelStart] == '-' || value[i - 1] == '-') return false;
                    for (int j = labelStart; j < i; j++)
                    {
                        char c = value[j];
                        bool valid = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                                     (c >= '0' && c <= '9') || c == '-';
                        if (!valid) return false;
                    }
                    labelStart = i + 1;
                }
            }
            return true;
        }
    }
}
