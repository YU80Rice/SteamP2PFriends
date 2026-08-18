using SDG.NetTransport;
using SDG.Unturned;
using SteamP2PFriends.Shared.Enums;
using Steamworks;
using System;
using UnityEngine;

namespace SteamP2PFriends.Shared
{
    /// <summary>
    /// Always-on, event-only connection journal for support cases. It never reads ticket bytes,
    /// changes network state, or runs from a periodic tick.
    /// </summary>
    internal static class P2PConnectionJournal
    {
        private const string Marker = "[P2P-Connection]";

        internal static void ClientConnectCalling(ulong targetSteamId, int attempt)
        {
            Write("[Client]", "CLIENT_CONNECT_CALLING",
                "targetSteamId=" + targetSteamId + " attempt=" + attempt);
        }

        internal static void ClientConnectCallReturned(ulong targetSteamId)
        {
            Write("[Client]", "CLIENT_CONNECT_CALL_RETURNED", "targetSteamId=" + targetSteamId);
        }

        internal static void ClientStateChanged(ulong targetSteamId, EJoinState previous,
            EJoinState current, string cause, ESteamConnectionFailureInfo failureInfo)
        {
            Write("[Client]", "CLIENT_STATE",
                "targetSteamId=" + targetSteamId + " from=" + previous + " to=" + current +
                " cause=" + SafeText(cause) + " failureInfo=" + failureInfo + "(" + (int)failureInfo + ")");
        }

        internal static void ClientVerifyReceived(CSteamID serverSteamId)
        {
            Write("[Client]", "CLIENT_VERIFY_RECEIVED", "serverSteamId=" + serverSteamId.m_SteamID);
        }

        internal static void ClientVerifyHandlerFailed(Exception exception)
        {
            Write("[Client]", "CLIENT_VERIFY_HANDLER_FAILED", "exceptionType=" + ExceptionType(exception));
        }

        internal static void ClientAuthenticateSendCalling(ENetReliability reliability)
        {
            Write("[Client]", "CLIENT_AUTHENTICATE_SEND_CALLING", "reliability=" + reliability);
        }

        internal static void ClientAuthenticateSendReturned()
        {
            Write("[Client]", "CLIENT_AUTHENTICATE_SEND_CALL_RETURNED", "result=no-exception");
        }

        internal static void ClientAuthenticateSendFailed(Exception exception)
        {
            Write("[Client]", "CLIENT_AUTHENTICATE_SEND_FAILED", "exceptionType=" + ExceptionType(exception));
        }

        internal static void HostAuthenticateReceived(ITransportConnection transportConnection)
        {
            try
            {
                SteamPending pending = Provider.findPendingPlayer(transportConnection);
                SteamPlayerID playerId = ReferenceEquals(pending, null) ? null : pending.playerID;
                ulong remoteSteamId = ReferenceEquals(playerId, null) ? 0UL : playerId.steamID.m_SteamID;
                Write("[Host]", "HOST_AUTHENTICATE_RECEIVED",
                    "remoteSteamId=" + remoteSteamId + " pendingFound=" + (!ReferenceEquals(pending, null)) +
                    " transport=" + TransportName(transportConnection));
            }
            catch (Exception exception)
            {
                Write("[Host]", "HOST_AUTHENTICATE_RECEIVE_OBSERVER_FAILED",
                    "exceptionType=" + ExceptionType(exception));
            }
        }

        internal static void HostAuthenticateHandlerReturned(ITransportConnection transportConnection)
        {
            Write("[Host]", "HOST_AUTHENTICATE_HANDLER_RETURNED",
                "transport=" + TransportName(transportConnection));
        }

        internal static void HostAuthenticateHandlerFailed(Exception exception)
        {
            Write("[Host]", "HOST_AUTHENTICATE_HANDLER_FAILED", "exceptionType=" + ExceptionType(exception));
        }

        internal static void HostAccepted(ulong remoteSteamId)
        {
            try
            {
                Write("[Host]", "HOST_ACCEPT_RETURNED",
                    "remoteSteamId=" + remoteSteamId + " clients=" + Count(Provider.clients) +
                    " pending=" + Count(Provider.pending));
            }
            catch (Exception exception)
            {
                Write("[Host]", "HOST_ACCEPT_OBSERVER_FAILED", "exceptionType=" + ExceptionType(exception));
            }
        }

        internal static void HostRejected(ulong remoteSteamId, ESteamRejection rejection, string source)
        {
            Write("[Host]", "HOST_REJECTED",
                "remoteSteamId=" + remoteSteamId + " rejection=" + rejection + "(" + (int)rejection +
                ") source=" + SafeText(source));
        }

        internal static void HostRejected(ITransportConnection transportConnection,
            ESteamRejection rejection, string source)
        {
            ulong remoteSteamId = 0UL;
            try
            {
                SteamPending pending = Provider.findPendingPlayer(transportConnection);
                if (!ReferenceEquals(pending, null) && !ReferenceEquals(pending.playerID, null))
                    remoteSteamId = pending.playerID.steamID.m_SteamID;
            }
            catch
            {
                // The original rejection is authoritative. Pending identity is only observational.
            }

            Write("[Host]", "HOST_REJECTED",
                "remoteSteamId=" + remoteSteamId + " rejection=" + rejection + "(" + (int)rejection +
                ") source=" + SafeText(source) + " transport=" + TransportName(transportConnection));
        }

        internal static void ClientAcceptedReceived(CSteamID serverSteamId)
        {
            Write("[Client]", "CLIENT_ACCEPTED_RECEIVED", "serverSteamId=" + serverSteamId.m_SteamID);
        }

        internal static void LocalDisconnectRequested(string reason)
        {
            try
            {
                Write("[Client]", "LOCAL_DISCONNECT_REQUESTED",
                    "reason=" + SafeText(reason) + " isConnected=" + Provider.isConnected +
                    " failureInfo=" + Provider.connectionFailureInfo + "(" + (int)Provider.connectionFailureInfo + ")");
            }
            catch (Exception exception)
            {
                Write("[Client]", "LOCAL_DISCONNECT_OBSERVER_FAILED", "exceptionType=" + ExceptionType(exception));
            }
        }

        private static void Write(string role, string eventName, string facts)
        {
            try
            {
                RoleLogger.Info(role,
                    Marker + " event=" + eventName + " t=" + Time.realtimeSinceStartup.ToString("F3") + "s " + facts);
            }
            catch
            {
                // Logging must not affect the observed connection path.
            }
        }

        private static string TransportName(ITransportConnection transportConnection)
        {
            return transportConnection == null ? "null" : transportConnection.GetType().Name;
        }

        private static int Count<T>(System.Collections.Generic.ICollection<T> values)
        {
            return values == null ? -1 : values.Count;
        }

        private static string ExceptionType(Exception exception)
        {
            return exception == null ? "none" : exception.GetType().Name;
        }

        private static string SafeText(string value)
        {
            if (string.IsNullOrEmpty(value)) return "(empty)";
            return value.Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}
