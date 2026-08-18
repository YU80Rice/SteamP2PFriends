using HarmonyLib;
using SDG.NetPak;
using SDG.NetTransport;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// Adds bounded, read-only evidence to the native Verify/Authenticate handshake.
    /// The patch neither accesses ticket payloads nor changes a return value or exception.
    /// </summary>
    internal static class AuthHandshakeJournalPatch
    {
        internal static bool RegistrationValid { get; private set; }

        internal static MethodInfo GetVerifyTargetMethod()
        {
            Type type = AccessTools.TypeByName("SDG.Unturned.ClientMessageHandler_Verify");
            Type reader = AccessTools.TypeByName("SDG.NetPak.NetPakReader");
            return type == null || reader == null ? null : AccessTools.Method(type, "ReadMessage", new[] { reader });
        }

        internal static MethodInfo GetAuthenticateTargetMethod()
        {
            Type type = AccessTools.TypeByName("SDG.Unturned.ServerMessageHandler_Authenticate");
            Type reader = AccessTools.TypeByName("SDG.NetPak.NetPakReader");
            return type == null || reader == null ? null :
                AccessTools.Method(type, "ReadMessage", new[] { typeof(ITransportConnection), reader });
        }

        internal static MethodInfo GetSendAuthenticateTargetMethod()
        {
            Type type = AccessTools.TypeByName("SDG.Unturned.NetMessages");
            Type writer = type == null ? null : type.GetNestedType("ClientWriteHandler",
                BindingFlags.Public | BindingFlags.NonPublic);
            return type == null || writer == null ? null : AccessTools.Method(type, "SendMessageToServer",
                new[] { typeof(EServerMessage), typeof(ENetReliability), writer });
        }

        internal static void RegisterManual(Harmony harmony)
        {
            RegistrationValid = false;
            MethodInfo verify = GetVerifyTargetMethod();
            MethodInfo authenticate = GetAuthenticateTargetMethod();
            MethodInfo sendToServer = GetSendAuthenticateTargetMethod();
            MethodInfo verifyPrefix = AccessTools.Method(typeof(AuthHandshakeJournalPatch), nameof(Verify_Prefix));
            MethodInfo verifyFinalizer = AccessTools.Method(typeof(AuthHandshakeJournalPatch), nameof(Verify_Finalizer));
            MethodInfo authenticatePrefix = AccessTools.Method(typeof(AuthHandshakeJournalPatch), nameof(Authenticate_Prefix));
            MethodInfo authenticatePostfix = AccessTools.Method(typeof(AuthHandshakeJournalPatch), nameof(Authenticate_Postfix));
            MethodInfo authenticateFinalizer = AccessTools.Method(typeof(AuthHandshakeJournalPatch), nameof(Authenticate_Finalizer));
            MethodInfo sendPrefix = AccessTools.Method(typeof(AuthHandshakeJournalPatch), nameof(SendToServer_Prefix));
            MethodInfo sendFinalizer = AccessTools.Method(typeof(AuthHandshakeJournalPatch), nameof(SendToServer_Finalizer));

            if (verify == null || authenticate == null || sendToServer == null ||
                verifyPrefix == null || verifyFinalizer == null || authenticatePrefix == null ||
                authenticatePostfix == null || authenticateFinalizer == null || sendPrefix == null || sendFinalizer == null)
            {
                RoleLogger.Error("[Shared]", "[P2P-Connection] auth handshake journal target resolution failed");
                return;
            }

            EnsurePatch(harmony, verify, verifyPrefix, verifyFinalizer);
            EnsurePatch(harmony, authenticate, authenticatePrefix, authenticatePostfix, authenticateFinalizer);
            EnsurePatch(harmony, sendToServer, sendPrefix, sendFinalizer);

            RegistrationValid = HasOwnedPatch(verify, verifyPrefix, PatchKind.Prefix) &&
                HasOwnedPatch(verify, verifyFinalizer, PatchKind.Finalizer) &&
                HasOwnedPatch(authenticate, authenticatePrefix, PatchKind.Prefix) &&
                HasOwnedPatch(authenticate, authenticatePostfix, PatchKind.Postfix) &&
                HasOwnedPatch(authenticate, authenticateFinalizer, PatchKind.Finalizer) &&
                HasOwnedPatch(sendToServer, sendPrefix, PatchKind.Prefix) &&
                HasOwnedPatch(sendToServer, sendFinalizer, PatchKind.Finalizer);

            if (RegistrationValid)
                RoleLogger.Info("[Shared]", "[P2P-Connection] auth handshake journal registered (event-only)");
            else
                RoleLogger.Error("[Shared]", "[P2P-Connection] auth handshake journal registration failed");
        }

        private static void Verify_Prefix(NetPakReader reader)
        {
            P2PConnectionJournal.ClientVerifyReceived(Provider.server);
        }

        private static void Verify_Finalizer(Exception __exception)
        {
            if (__exception != null)
                P2PConnectionJournal.ClientVerifyHandlerFailed(__exception);
        }

        private static void SendToServer_Prefix(EServerMessage index, ENetReliability reliability)
        {
            if (index == EServerMessage.Authenticate)
                P2PConnectionJournal.ClientAuthenticateSendCalling(reliability);
        }

        private static void SendToServer_Finalizer(EServerMessage index, Exception __exception)
        {
            if (index != EServerMessage.Authenticate) return;
            if (__exception == null)
                P2PConnectionJournal.ClientAuthenticateSendReturned();
            else
                P2PConnectionJournal.ClientAuthenticateSendFailed(__exception);
        }

        private static void Authenticate_Prefix(ITransportConnection transportConnection, NetPakReader reader)
        {
            P2PConnectionJournal.HostAuthenticateReceived(transportConnection);
        }

        private static void Authenticate_Postfix(ITransportConnection transportConnection, NetPakReader reader)
        {
            P2PConnectionJournal.HostAuthenticateHandlerReturned(transportConnection);
        }

        private static void Authenticate_Finalizer(Exception __exception)
        {
            if (__exception != null)
                P2PConnectionJournal.HostAuthenticateHandlerFailed(__exception);
        }

        private static void EnsurePatch(Harmony harmony, MethodInfo target, MethodInfo prefix,
            MethodInfo finalizer)
        {
            if (!HasOwnedPatch(target, prefix, PatchKind.Prefix))
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            if (!HasOwnedPatch(target, finalizer, PatchKind.Finalizer))
                harmony.Patch(target, finalizer: new HarmonyMethod(finalizer));
        }

        private static void EnsurePatch(Harmony harmony, MethodInfo target, MethodInfo prefix,
            MethodInfo postfix, MethodInfo finalizer)
        {
            if (!HasOwnedPatch(target, prefix, PatchKind.Prefix))
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            if (!HasOwnedPatch(target, postfix, PatchKind.Postfix))
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            if (!HasOwnedPatch(target, finalizer, PatchKind.Finalizer))
                harmony.Patch(target, finalizer: new HarmonyMethod(finalizer));
        }

        private static bool HasOwnedPatch(MethodBase target, MethodInfo expected, PatchKind kind)
        {
            HarmonyLib.Patches patches = Harmony.GetPatchInfo(target);
            if (patches == null) return false;

            System.Collections.Generic.IEnumerable<Patch> entries = kind == PatchKind.Prefix
                ? patches.Prefixes
                : kind == PatchKind.Postfix ? patches.Postfixes : patches.Finalizers;
            foreach (Patch patch in entries)
            {
                if (patch.owner == SteamP2PFriendsPlugin.HARMONY_ID && patch.PatchMethod == expected)
                    return true;
            }
            return false;
        }

        private enum PatchKind
        {
            Prefix,
            Postfix,
            Finalizer
        }
    }
}
