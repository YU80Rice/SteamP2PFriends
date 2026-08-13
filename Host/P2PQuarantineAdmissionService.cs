using SDG.NetTransport;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace SteamP2PFriends.Host
{
    internal enum QuarantinePhase : byte
    {
        None = 0,
        Reserved = 1,
        Pending = 2,
        Active = 3
    }

    /// <summary>
    /// Stage 10 指令 A: explicit result of a connected-player promotion. Produced inside
    /// quarantine by the real transaction outcome; callers must not guess state via IsKnown.
    /// Only AlreadyApproved and Activated may trigger the corresponding join broadcast; both
    /// Rejected* results must never broadcast "joined" before the kick.
    /// </summary>
    internal enum QuarantinePromotionResult : byte
    {
        Ignored,
        AlreadyApproved,
        Activated,
        RejectedMissingReservation,
        RejectedSignalFailure
    }

    internal readonly struct QuarantineEntry
    {
        internal readonly ulong SteamId;
        internal readonly QuarantinePhase Phase;
        internal readonly float ReservedAt;
        internal readonly float ActiveAt;
        internal readonly float Deadline;
        internal readonly int NextChatAnnouncement;
        internal readonly object TransportToken;

        internal QuarantineEntry(ulong steamId, QuarantinePhase phase, float reservedAt,
            float activeAt, float deadline, int nextChatAnnouncement, object transportToken)
        {
            SteamId = steamId;
            Phase = phase;
            ReservedAt = reservedAt;
            ActiveAt = activeAt;
            Deadline = deadline;
            NextChatAnnouncement = nextChatAnnouncement;
            TransportToken = transportToken;
        }

        internal QuarantineEntry WithPhase(QuarantinePhase phase, float activeAt, float deadline,
            int nextChatAnnouncement = 0)
        {
            return new QuarantineEntry(SteamId, phase, ReservedAt, activeAt, deadline,
                nextChatAnnouncement, TransportToken);
        }

        internal QuarantineEntry WithNextChatAnnouncement(int nextChatAnnouncement)
        {
            return new QuarantineEntry(SteamId, Phase, ReservedAt, ActiveAt, Deadline,
                nextChatAnnouncement, TransportToken);
        }
    }

    /// <summary>
    /// Stage 7-6 server-authoritative admission quarantine.
    /// Unknown P2P guests receive a one-connection permit at ReadyToConnect, then are blocked
    /// after fully joining until the host persists approval or the 30 second deadline expires.
    /// </summary>
    internal static class P2PQuarantineAdmissionService
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<ulong, QuarantineEntry> Entries =
            new Dictionary<ulong, QuarantineEntry>();

        internal const int MaxConcurrentEntries = 4;
        internal const float ReservationLifetimeSeconds = 15f;
        internal const float ActiveLifetimeSeconds = 30f;
        internal const string ApprovedTag = "APPROVED";

        // Owner-only bit outside the currently defined EPluginWidgetFlags range.
        // Startup registration verification fails closed if the game begins using this bit.
        internal const uint QuarantineSignalMask = 0x80000000u;
        internal static readonly EPluginWidgetFlags QuarantineSignalFlag =
            (EPluginWidgetFlags)unchecked((int)QuarantineSignalMask);

        internal static bool _testBypassThreadAssert;
        internal static bool? _testActiveHost;
        internal static Func<float> _testTimeProvider;
        internal static Func<CSteamID, bool> _testWhitelistContains;
        internal static Action<ulong, bool> _testSignalCallback;
        internal static Action<ulong, string> _testKickCallback;
        internal static Action<ulong, string> _testChatCallback;
        internal static ulong? _testLocalUserSteamId;

        internal static int EntryCountForTest
        {
            get { lock (Sync) return Entries.Count; }
        }

        private static void AssertGameThread()
        {
            if (!_testBypassThreadAssert) ThreadUtil.assertIsGameThread();
        }

        private static float Now => _testTimeProvider != null
            ? _testTimeProvider()
            : GetUnityTime();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static float GetUnityTime()
        {
            return Time.realtimeSinceStartup;
        }

        private static bool IsActiveP2PHost => _testActiveHost ??
            (HostManager.IsP2PHostMode && Provider.isServer && Provider.isWhitelisted);

        internal static bool IsSignalBitCompatible()
        {
            uint defined = 0u;
            Array values = Enum.GetValues(typeof(EPluginWidgetFlags));
            for (int i = 0; i < values.Length; i++)
            {
                defined |= unchecked((uint)(int)(EPluginWidgetFlags)values.GetValue(i));
            }
            return (defined & QuarantineSignalMask) == 0u;
        }

        internal static bool TryReserve(CSteamID steamId, ITransportConnection transport)
        {
            AssertGameThread();
            return TryReserveCore(steamId.m_SteamID, transport, Now);
        }

        internal static bool TryReserveForTest(ulong steamId, object transportToken, float now)
        {
            return TryReserveCore(steamId, transportToken, now);
        }

        private static bool TryReserveCore(ulong steamId, object transportToken, float now)
        {
            if (!IsActiveP2PHost || steamId == 0UL || transportToken == null) return false;

            CSteamID id = new CSteamID(steamId);
            ulong localUser = _testLocalUserSteamId ?? Provider.user.m_SteamID;
            if (!id.IsValid() || steamId == localUser) return false;
            if (P2PJoinApprovalService.IsSessionSuppressed(id)) return false;

            lock (Sync)
            {
                PurgeExpiredReservationsUnsafe(now);

                if (Entries.TryGetValue(steamId, out QuarantineEntry existing))
                {
                    return ReferenceEquals(existing.TransportToken, transportToken);
                }

                if (Entries.Count >= MaxConcurrentEntries) return false;

                Entries.Add(steamId, new QuarantineEntry(
                    steamId, QuarantinePhase.Reserved, now, 0f, 0f, 0, transportToken));
                RoleLogger.Info("[Host]",
                    "[P2P-Quarantine] admission reserved steamId=" + steamId);
                return true;
            }
        }

        internal static void BindPending(CSteamID steamId, ITransportConnection transport)
        {
            AssertGameThread();
            BindPendingCore(steamId.m_SteamID, transport);
        }

        internal static void BindPendingForTest(ulong steamId, object transportToken)
        {
            BindPendingCore(steamId, transportToken);
        }

        private static void BindPendingCore(ulong steamId, object transportToken)
        {
            if (steamId == 0UL || transportToken == null) return;
            lock (Sync)
            {
                if (!Entries.TryGetValue(steamId, out QuarantineEntry entry)) return;
                if (!ReferenceEquals(entry.TransportToken, transportToken)) return;
                if (entry.Phase != QuarantinePhase.Reserved) return;
                Entries[steamId] = entry.WithPhase(QuarantinePhase.Pending, 0f, 0f);
            }
        }

        /// <summary>
        /// Stage 10 指令 A: promotes a connected player and returns the explicit transaction
        /// result. Only AlreadyApproved and Activated may trigger the join broadcast; the two
        /// Rejected* results must never broadcast "joined" before the kick.
        /// </summary>
        internal static QuarantinePromotionResult PromoteConnected(SteamPlayer steamPlayer)
        {
            AssertGameThread();
            if (!IsActiveP2PHost || ReferenceEquals(steamPlayer, null) ||
                ReferenceEquals(steamPlayer.playerID, null)) return QuarantinePromotionResult.Ignored;

            CSteamID id = steamPlayer.playerID.steamID;
            if (id == CSteamID.Nil || !id.IsValid() || id == Provider.user)
                return QuarantinePromotionResult.Ignored;

            if (ContainsWhitelist(id))
            {
                RemoveEntry(id.m_SteamID);
                return QuarantinePromotionResult.AlreadyApproved;
            }

            bool promoted = false;
            float now = Now;
            lock (Sync)
            {
                if (Entries.TryGetValue(id.m_SteamID, out QuarantineEntry entry) &&
                    (entry.Phase == QuarantinePhase.Reserved || entry.Phase == QuarantinePhase.Pending))
                {
                    Entries[id.m_SteamID] = entry.WithPhase(
                        QuarantinePhase.Active, now, now + ActiveLifetimeSeconds, 25);
                    promoted = true;
                }
            }

            if (!promoted)
            {
                RoleLogger.Error("[Host]",
                    "[P2P-Quarantine] connected unknown player had no reservation; kicking fail-closed");
                Kick(id.m_SteamID, "Admission quarantine state missing.");
                return QuarantinePromotionResult.RejectedMissingReservation;
            }

            if (!SetSignal(steamPlayer, true))
            {
                RemoveEntry(id.m_SteamID);
                Kick(id.m_SteamID, "Unable to initialize approval quarantine.");
                return QuarantinePromotionResult.RejectedSignalFailure;
            }

            RoleLogger.Info("[Host]",
                "[P2P-Quarantine] active steamId=" + id.m_SteamID + " deadline=30s");
            SendTargetedChat(id.m_SteamID,
                "你正在等待房主审核，最长等待 30 秒。审核前无法行动或交互，但处于无敌状态。");
            return QuarantinePromotionResult.Activated;
        }

        internal static bool PromoteForTest(ulong steamId, object transportToken, float now)
        {
            lock (Sync)
            {
                if (!Entries.TryGetValue(steamId, out QuarantineEntry entry) ||
                    !ReferenceEquals(entry.TransportToken, transportToken) ||
                    (entry.Phase != QuarantinePhase.Reserved && entry.Phase != QuarantinePhase.Pending))
                    return false;
                Entries[steamId] = entry.WithPhase(
                    QuarantinePhase.Active, now, now + ActiveLifetimeSeconds, 25);
                return true;
            }
        }

        internal static bool IsActive(CSteamID steamId)
        {
            if (steamId == CSteamID.Nil) return false;
            lock (Sync)
            {
                return Entries.TryGetValue(steamId.m_SteamID, out QuarantineEntry entry) &&
                       entry.Phase == QuarantinePhase.Active;
            }
        }

        internal static bool IsKnown(CSteamID steamId)
        {
            if (steamId == CSteamID.Nil) return false;
            lock (Sync) return Entries.ContainsKey(steamId.m_SteamID);
        }

        internal static bool ReleaseAfterPersistentApproval(CSteamID steamId, out string failure)
        {
            AssertGameThread();
            failure = string.Empty;
            if (!IsActiveP2PHost || steamId == CSteamID.Nil || !steamId.IsValid())
            {
                failure = "invalid host or SteamID";
                return false;
            }
            if (!ContainsWhitelist(steamId))
            {
                failure = "whitelist postcondition false";
                return false;
            }

            bool existed = RemoveEntry(steamId.m_SteamID);
            bool signalCleared = true;
            if (_testSignalCallback != null)
            {
                _testSignalCallback(steamId.m_SteamID, false);
            }
            else
            {
                SteamPlayer player = FindClient(steamId.m_SteamID);
                if (player != null) signalCleared = SetSignal(player, false);
            }
            if (!signalCleared)
            {
                Kick(steamId.m_SteamID, "Approval completed; reconnect required.");
                failure = "approval persisted but client signal cleanup failed";
                return false;
            }

            RoleLogger.Info("[Host]",
                "[P2P-Quarantine] released steamId=" + steamId.m_SteamID +
                " hadEntry=" + existed);
            SendTargetedChat(steamId.m_SteamID, "房主已允许你进入，行动限制已解除。");
            return true;
        }

        internal static void Tick()
        {
            AssertGameThread();
            if (!IsActiveP2PHost) return;

            float now = Now;
            List<ulong> expired = null;
            List<KeyValuePair<ulong, int>> chatAnnouncements = null;
            lock (Sync)
            {
                PurgeExpiredReservationsUnsafe(now);
                foreach (KeyValuePair<ulong, QuarantineEntry> pair in Entries)
                {
                    if (pair.Value.Phase == QuarantinePhase.Active && now >= pair.Value.Deadline)
                    {
                        if (expired == null) expired = new List<ulong>(MaxConcurrentEntries);
                        expired.Add(pair.Key);
                    }
                    else if (pair.Value.Phase == QuarantinePhase.Active &&
                             pair.Value.NextChatAnnouncement >= 5)
                    {
                        int remaining = Math.Max(0, (int)Math.Ceiling(pair.Value.Deadline - now));
                        if (remaining <= pair.Value.NextChatAnnouncement)
                        {
                            if (chatAnnouncements == null)
                                chatAnnouncements = new List<KeyValuePair<ulong, int>>(MaxConcurrentEntries);
                            chatAnnouncements.Add(new KeyValuePair<ulong, int>(
                                pair.Key, pair.Value.NextChatAnnouncement));
                        }
                    }
                }
                if (chatAnnouncements != null)
                {
                    for (int i = 0; i < chatAnnouncements.Count; i++)
                    {
                        ulong id = chatAnnouncements[i].Key;
                        if (Entries.TryGetValue(id, out QuarantineEntry entry) &&
                            entry.Phase == QuarantinePhase.Active)
                        {
                            Entries[id] = entry.WithNextChatAnnouncement(
                                chatAnnouncements[i].Value - 5);
                        }
                    }
                }
                if (expired != null)
                {
                    for (int i = 0; i < expired.Count; i++) Entries.Remove(expired[i]);
                }
            }

            if (chatAnnouncements != null)
            {
                for (int i = 0; i < chatAnnouncements.Count; i++)
                {
                    SendTargetedChat(chatAnnouncements[i].Key,
                        "等待房主审核：剩余约 " + chatAnnouncements[i].Value + " 秒。");
                }
            }

            if (expired == null) return;
            for (int i = 0; i < expired.Count; i++)
            {
                ulong id = expired[i];
                if (_testSignalCallback != null)
                    _testSignalCallback(id, false);
                else
                {
                    SteamPlayer player = FindClient(id);
                    if (player != null) SetSignal(player, false);
                }
                // Stage 10 指令 C: write the expected-departure marker + broadcast BEFORE the Kick,
                // so the subsequent onEnemyDisconnected consumes the marker and does NOT add a
                // second ordinary "left" message. A broadcaster exception must not block the Kick.
                try
                {
                    P2PWorldStatusBroadcaster.OnApprovalTimeout(new CSteamID(id));
                }
                catch (Exception bcEx)
                {
                    RoleLogger.Warn("[Host]",
                        "[WorldBroadcast] timeout broadcast failed (kick continues): " + bcEx.GetType().Name);
                }
                Kick(id, "Host approval timed out (30 seconds).");
                RoleLogger.Info("[Host]", "[P2P-Quarantine] timeout kick steamId=" + id);
            }
        }

        internal static void OnRejected(ITransportConnection transport)
        {
            AssertGameThread();
            if (transport == null) return;
            lock (Sync)
            {
                ulong found = 0UL;
                foreach (KeyValuePair<ulong, QuarantineEntry> pair in Entries)
                {
                    if (ReferenceEquals(pair.Value.TransportToken, transport))
                    {
                        found = pair.Key;
                        break;
                    }
                }
                if (found != 0UL) Entries.Remove(found);
            }
        }

        internal static void OnDisconnected(CSteamID steamId)
        {
            AssertGameThread();
            if (steamId != CSteamID.Nil) RemoveEntry(steamId.m_SteamID);
        }

        internal static void ResetForSession()
        {
            AssertGameThread();
            lock (Sync) Entries.Clear();
            RoleLogger.Info("[Host]", "[P2P-Quarantine] session state reset");
        }

        internal static QuarantinePhase GetPhaseForTest(ulong steamId)
        {
            lock (Sync)
            {
                return Entries.TryGetValue(steamId, out QuarantineEntry entry)
                    ? entry.Phase
                    : QuarantinePhase.None;
            }
        }

        internal static float GetDeadlineForTest(ulong steamId)
        {
            lock (Sync)
            {
                return Entries.TryGetValue(steamId, out QuarantineEntry entry)
                    ? entry.Deadline
                    : 0f;
            }
        }

        private static bool ContainsWhitelist(CSteamID id)
        {
            if (_testWhitelistContains != null) return _testWhitelistContains(id);
            IReadOnlyList<SteamWhitelistID> snapshot = P2PWhitelistService.SnapshotForUi();
            for (int i = 0; i < snapshot.Count; i++)
            {
                if (snapshot[i].steamID.m_SteamID == id.m_SteamID) return true;
            }
            return false;
        }

        private static bool SetSignal(SteamPlayer steamPlayer, bool enabled)
        {
            ulong id = steamPlayer?.playerID?.steamID.m_SteamID ?? 0UL;
            if (_testSignalCallback != null)
            {
                _testSignalCallback(id, enabled);
                return true;
            }
            try
            {
                Player player = steamPlayer?.player;
                if (player == null) return false;
                EPluginWidgetFlags flags = player.pluginWidgetFlags;
                EPluginWidgetFlags desired = enabled
                    ? flags | QuarantineSignalFlag
                    : flags & ~QuarantineSignalFlag;
                player.setAllPluginWidgetFlags(desired);
                return true;
            }
            catch (Exception ex)
            {
                RoleLogger.Error("[Host]",
                    "[P2P-Quarantine] signal update failed: " + ex.GetType().Name);
                return false;
            }
        }

        private static SteamPlayer FindClient(ulong steamId)
        {
            for (int i = 0; i < Provider.clients.Count; i++)
            {
                SteamPlayer player = Provider.clients[i];
                if (!ReferenceEquals(player, null) && !ReferenceEquals(player.playerID, null) &&
                    player.playerID.steamID.m_SteamID == steamId) return player;
            }
            return null;
        }

        private static void Kick(ulong steamId, string reason)
        {
            if (_testKickCallback != null)
            {
                _testKickCallback(steamId, reason);
                return;
            }
            Provider.kick(new CSteamID(steamId), reason);
        }

        private static void SendTargetedChat(ulong steamId, string text)
        {
            if (_testChatCallback != null)
            {
                _testChatCallback(steamId, text);
                return;
            }
            try
            {
                SteamPlayer player = FindClient(steamId);
                if (player == null) return;
                ChatManager.serverSendMessage(text, Palette.SERVER, toPlayer: player,
                    mode: EChatMode.WELCOME, useRichTextFormatting: false);
                RoleLogger.Info("[Host]",
                    "[P2P-Quarantine] targeted chat sent steamId=" + steamId + " text=" + text);
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]",
                    "[P2P-Quarantine] targeted chat failed: " + ex.GetType().Name);
            }
        }

        private static bool RemoveEntry(ulong steamId)
        {
            lock (Sync) return Entries.Remove(steamId);
        }

        private static void PurgeExpiredReservationsUnsafe(float now)
        {
            List<ulong> expired = null;
            foreach (KeyValuePair<ulong, QuarantineEntry> pair in Entries)
            {
                if (pair.Value.Phase == QuarantinePhase.Reserved &&
                    now - pair.Value.ReservedAt >= ReservationLifetimeSeconds)
                {
                    if (expired == null) expired = new List<ulong>();
                    expired.Add(pair.Key);
                }
            }
            if (expired == null) return;
            for (int i = 0; i < expired.Count; i++) Entries.Remove(expired[i]);
        }
    }
}
