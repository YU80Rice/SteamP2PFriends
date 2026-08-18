using HarmonyLib;
using SDG.NetTransport.SteamNetworkingSockets;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    ///
    /// Vanilla ClientTransport_SteamNetworkingSockets.TryGetQueryPort derives the query port
    /// from the remote connection port minus one (info.m_addrRemote.m_port - 1). Under the
    /// single-port semantics the user enters R and both queryPort and connectionPort equal R,
    /// so favorites / server info / copy-address would otherwise display R-1. This Postfix
    /// projects the query port back to R only when the current parameters are a single-port
    /// Direct-IP connection.
    ///
    /// This projection is display-only. It is never used for whitelist or player authorization.
    /// </summary>
    [HarmonyPatch]
    internal static class DirectIpSinglePortQueryPortPatch
    {
        internal static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(ClientTransport_SteamNetworkingSockets),
                nameof(ClientTransport_SteamNetworkingSockets.TryGetQueryPort),
                new[] { typeof(ushort).MakeByRefType() });
        }

        internal static void Postfix(ref bool __result, ref ushort __0)
        {
            if (!__result) return;

            ServerConnectParameters parameters = Provider.CurrentServerConnectParameters;
            if (!UnifiedJoinAddressClassifier.IsSinglePortDirectIpParameters(parameters)) return;

            __0 = parameters.connectionPort;
        }

        /// <summary>
        /// </summary>
        internal static bool ProjectForTest(bool originalResult, ushort originalQueryPort,
            bool isSinglePort, ushort sharedPort, out ushort projectedPort)
        {
            projectedPort = originalQueryPort;
            if (!originalResult || !isSinglePort || sharedPort == 0) return originalResult;
            projectedPort = sharedPort;
            return true;
        }
    }
}
