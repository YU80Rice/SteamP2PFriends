using BepInEx.Configuration;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SteamP2PFriends.Host
{
    /// <summary>
    ///
    /// Product semantics (指令 A): every event broadcasts ONE system message to all players in
    /// the current world — host + approved guests + still-quarantined guests — via the unified
    /// sender with fromPlayer=null / toPlayer=null / useRichTextFormatting=false. It NEVER sends
    /// only to the event subject. The quarantine 5s countdown stays a targeted private message.
    ///
    /// PlayerLife.deathKiller (that is the killer, not the victim). Unknown owner -> Nil -> no
    /// broadcast and no cooldown pollution. Listen-host self identity is accepted only after an
    /// exact Player/PlayerLife match against Player.LocalPlayer.
    ///
    /// AlreadyApproved/Activated promotion result. Approval atomically promotes Quarantined ->
    /// Approved. Disconnect consumes the registered state; a rejected player (no registered state)
    /// disconnects silently (never "approved player left").
    ///
    /// ActivationValid aggregates into DiagnosticBuildValid; the plugin logs "initialized" only on
    /// success.
    ///
    /// outcome), removing death cooldown + projection state + approval generation for that SteamID.
    /// Approval-once dedup is keyed by the current connection generation, so revoke/rejoin/approve
    /// broadcasts a second independent approval.
    ///
    /// 禁止重复订阅 (蓝图 §4.3): connect/disconnect are forwarded from the existing unique
    /// HostManager.OnPlayerConnectedToServer and Plugin.OnEnemyDisconnectedHandler callbacks; the
    /// broadcaster subscribes ONLY once to PlayerLife.onPlayerDied in Initialize() and unsubscribes
    /// symmetrically in Shutdown(). _initialized makes Initialize/Shutdown idempotent.
    ///
    /// Idempotency + throttling (指令 G): per-player death min 2s; global max 8 per 10s applied in
    /// the unified sender for connect/approval/timeout/leave/death alike; over-limit is dropped,
    /// never queued/deferred; at most one summary diagnostic per window.
    /// </summary>
    internal static class P2PWorldStatusBroadcaster
    {
        // ===== Config (指令 I) =====
        internal static ConfigEntry<bool> EnableWorldStatusBroadcast;
        internal static ConfigEntry<bool> BroadcastJoinLeave;
        internal static ConfigEntry<bool> BroadcastDeaths;

        // Backing switches (tests set them directly without a ConfigFile).
        private static bool _enableMaster = true;
        private static bool _broadcastJoinLeave = true;
        private static bool _broadcastDeaths = true;

        internal static bool MasterEnabled => _enableMaster;
        internal static bool JoinLeaveEnabled => _broadcastJoinLeave;
        internal static bool DeathsEnabled => _broadcastDeaths;
        internal static void SetConfigForTest(bool? master = null, bool? joinLeave = null, bool? deaths = null)
        {
            if (master.HasValue) _enableMaster = master.Value;
            if (joinLeave.HasValue) _broadcastJoinLeave = joinLeave.Value;
            if (deaths.HasValue) _broadcastDeaths = deaths.Value;
        }

        // ===== Constants =====
        private const float PlayerDeathCooldownSeconds = 2f;
        private const int GlobalMaxMessagesPerWindow = 8;
        private const float GlobalWindowSeconds = 10f;
        private const float ExpectedDepartureTtlSeconds = 60f;

        // ===== Test hooks =====
        internal static bool _testBypassThreadAssert;
        internal static Func<float> _testTimeProvider;
        internal static Func<int> _testRandomIndexProvider;
        internal static Action<string> _testSendSink;
        internal static bool _testDisableHostGate;
        internal static Func<CSteamID, string> _testNameResolver;
        // v3 (structured slots): test-only hook standing in for Provider.clients so the reliable-
        // killer gate is deterministically testable on the console (no Unity client list).
        internal static Func<CSteamID, string> _testClientNameResolver;
        internal static bool _testPlainPresentation;
        // add/remove calls (console CLR cannot touch the PlayerLife static event).
        internal static Action<System.Action<PlayerLife, EDeathCause, ELimb, CSteamID>> _testSubscribeDeath;
        internal static Action<System.Action<PlayerLife, EDeathCause, ELimb, CSteamID>> _testUnsubscribeDeath;
        internal static Action<System.Action<PlayerLife, EDeathCause, ELimb, CSteamID>> _testSubscribeLegacyDeath;
        internal static Action<System.Action<PlayerLife, EDeathCause, ELimb, CSteamID>> _testUnsubscribeLegacyDeath;
        internal static Func<bool> _testGameThreadReady;
        // would give ChatManager.serverSendMessage; tests inject a fake and assert every parameter.
        internal delegate void P2PWorldChatSend(
            string text, Color color, SteamPlayer fromPlayer, SteamPlayer toPlayer,
            EChatMode mode, string iconURL, bool useRichTextFormatting);
        internal static P2PWorldChatSend _testChatManagerSend;
        // Test capture of the last production chat-send ABI call (fromPlayer/toPlayer/mode/icon/rich).
        internal static SteamPlayer LastCapturedFromPlayer;
        internal static SteamPlayer LastCapturedToPlayer;
        internal static EChatMode LastCapturedMode = EChatMode.SAY;
        internal static string LastCapturedIconUrl = "sentinel";
        internal static bool LastCapturedRichText = true;


        /// <summary>
        /// Production adapter that calls the real ChatManager.serverSendMessage with the exact
        /// world-broadcast ABI: fromPlayer=null, toPlayer=null, mode=WELCOME, iconURL=empty,
        /// useRichTextFormatting=false. Records the parameters so tests can assert the real ABI.
        /// </summary>
        internal static void ProductionChatSend(
            string text, Color color, SteamPlayer fromPlayer, SteamPlayer toPlayer,
            EChatMode mode, string iconURL, bool useRichTextFormatting)
        {
            LastCapturedFromPlayer = fromPlayer;
            LastCapturedToPlayer = toPlayer;
            LastCapturedMode = mode;
            LastCapturedIconUrl = iconURL;
            LastCapturedRichText = useRichTextFormatting;
            ChatManager.serverSendMessage(
                text, color, fromPlayer, toPlayer, mode, iconURL, useRichTextFormatting);
        }

        // ===== State =====
        private static bool _initialized;
        private static int _sessionEpoch;
        private static readonly object Sync = new object();

        // deliberate no-op which is itself "valid": nothing to subscribe because disabled).
        private static bool _activationValid;
        private static EWorldBroadcastActivationState _activationState =
            EWorldBroadcastActivationState.Pending;
        private static bool _pendingLogWritten;

        // expected-departure: SteamID -> (kind, writtenAt) (指令 C)
        private static readonly Dictionary<ulong, ExpectedDepartureEntry> _expectedDeparture =
            new Dictionary<ulong, ExpectedDepartureEntry>();
        // per-player death cooldown (指令 G)
        private static readonly Dictionary<ulong, float> _lastDeathBroadcastAt =
            new Dictionary<ulong, float>();
        private static readonly Dictionary<ulong, EConnectionProjectionState> _connectionState =
            new Dictionary<ulong, EConnectionProjectionState>();
        // global rate window (指令 G, shared by all kinds in the unified sender)
        private static float _windowStartedAt;
        private static bool _windowActive;
        private static int _windowCount;
        private static bool _windowDiagnosticLogged;

        private struct ExpectedDepartureEntry
        {
            internal EWorldBroadcastKind Kind;
            internal float WrittenAt;
        }

        /// <summary>
        /// only after a real promotion result (AlreadyApproved -> Approved, Activated ->
        /// Quarantined). Rejected players are never registered, so their disconnect is silent.
        /// </summary>
        internal enum EConnectionProjectionState : byte
        {
            None = 0,
            Quarantined = 1,
            Approved = 2
        }

        internal enum EWorldBroadcastActivationState : byte
        {
            DisabledValid = 0,
            Pending = 1,
            ActiveValid = 2,
            Failed = 3
        }

        // ===== Lifecycle (指令 J) =====

        /// <summary>
        /// BepInEx Awake only binds configuration and requests activation. Unturned may not have
        /// initialized ThreadUtil.gameThread yet, so the real subscription is deferred to Update.
        /// </summary>
        internal static bool Initialize(ConfigFile config)
        {
            if (config != null)
            {
                EnableWorldStatusBroadcast = config.Bind("World Broadcast", "EnableWorldStatusBroadcast", true,
                    "是否启用世界状态播报（全员系统公告）");
                BroadcastJoinLeave = config.Bind("World Broadcast", "BroadcastJoinLeave", true,
                    "是否播报玩家进入/离开/审核状态");
                BroadcastDeaths = config.Bind("World Broadcast", "BroadcastDeaths", true,
                    "是否播报玩家死亡");
                _enableMaster = EnableWorldStatusBroadcast.Value;
                _broadcastJoinLeave = BroadcastJoinLeave.Value;
                _broadcastDeaths = BroadcastDeaths.Value;
            }
            lock (Sync)
            {
                _initialized = false;
                _pendingLogWritten = false;
                if (!_enableMaster)
                {
                    _activationState = EWorldBroadcastActivationState.DisabledValid;
                    _activationValid = true;
                    RoleLogger.Info("[Host]",
                        "[WorldBroadcast] disabled (EnableWorldStatusBroadcast=false); not subscribing");
                }
                else
                {
                    _activationState = EWorldBroadcastActivationState.Pending;
                    _activationValid = true;
                    RoleLogger.Info("[Host]",
                        "[WorldBroadcast] activation pending: waiting for Unturned game thread");
                }
            }
            return true;
        }

        /// <summary>Compatibility/test entry for the deferred activation attempt.</summary>
        internal static bool InitializeCore()
        {
            return TryActivateOnGameThread();
        }

        internal static bool TryActivateOnGameThread()
        {
            lock (Sync)
            {
                if (_activationState == EWorldBroadcastActivationState.DisabledValid ||
                    _activationState == EWorldBroadcastActivationState.ActiveValid)
                    return true;
                if (_activationState == EWorldBroadcastActivationState.Failed)
                    return false;

                if (!_enableMaster)
                {
                    _activationState = EWorldBroadcastActivationState.DisabledValid;
                    _activationValid = true;
                    return true;
                }

                if (!IsGameThreadReady())
                {
                    _activationState = EWorldBroadcastActivationState.Pending;
                    _activationValid = true;
                    if (!_pendingLogWritten)
                    {
                        _pendingLogWritten = true;
                        RoleLogger.Info("[Host]",
                            "[WorldBroadcast] activation pending: ThreadUtil game thread not ready");
                    }
                    return true;
                }

                try
                {
                    if (!_testBypassThreadAssert) ThreadUtil.assertIsGameThread();
                    var handler = new System.Action<PlayerLife, EDeathCause, ELimb, CSteamID>(OnPlayerDied);
                    var legacyHandler = new System.Action<PlayerLife, EDeathCause, ELimb, CSteamID>(OnRocketLegacyDeath);
                    bool eventAdded = false;
                    bool legacyAdded = false;
                    try
                    {
                        if (_testSubscribeDeath != null) _testSubscribeDeath(handler);
                        else PlayerLife.onPlayerDied += OnPlayerDied;
                        eventAdded = true;

                        if (_testSubscribeLegacyDeath != null) _testSubscribeLegacyDeath(legacyHandler);
                        else PlayerLife.RocketLegacyOnDeath += OnRocketLegacyDeath;
                        legacyAdded = true;
                    }
                    catch
                    {
                        // Transactional activation: never leave one source subscribed after another
                        // source failed. Best-effort rollback preserves a retry-safe Failed state.
                        if (legacyAdded)
                        {
                            try
                            {
                                if (_testUnsubscribeLegacyDeath != null) _testUnsubscribeLegacyDeath(legacyHandler);
                                else PlayerLife.RocketLegacyOnDeath -= OnRocketLegacyDeath;
                            }
                            catch { }
                        }
                        if (eventAdded)
                        {
                            try
                            {
                                if (_testUnsubscribeDeath != null) _testUnsubscribeDeath(handler);
                                else PlayerLife.onPlayerDied -= OnPlayerDied;
                            }
                            catch { }
                        }
                        throw;
                    }
                    _initialized = true;
                    _activationState = EWorldBroadcastActivationState.ActiveValid;
                    _activationValid = true;
                    RoleLogger.Info("[Host]",
                        "[WorldBroadcast] activation succeeded on game thread sources=RocketLegacy+onPlayerDied+doDamageFallback");
                    return true;
                }
                catch (Exception ex)
                {
                    _activationState = EWorldBroadcastActivationState.Failed;
                    _activationValid = false;
                    RoleLogger.Error("[Host]",
                        "[WorldBroadcast] init FAILED (activation invalid): " + ex.GetType().Name);
                    return false;
                }
            }
        }

        private static bool IsGameThreadReady()
        {
            if (_testGameThreadReady != null) return _testGameThreadReady();
            if (_testBypassThreadAssert) return true;
            return ThreadUtil.gameThread != null &&
                   System.Threading.Thread.CurrentThread == ThreadUtil.gameThread;
        }

        /// <summary>OnDestroy / plugin unload: symmetric unsubscribe (指令 J / 蓝图 §4.3).</summary>
        internal static void Shutdown()
        {
            lock (Sync)
            {
                if (!_initialized)
                {
                    _activationState = _enableMaster
                        ? EWorldBroadcastActivationState.Pending
                        : EWorldBroadcastActivationState.DisabledValid;
                    _activationValid = true;
                    return;
                }
                if (!_testBypassThreadAssert) ThreadUtil.assertIsGameThread();
                var handler = new System.Action<PlayerLife, EDeathCause, ELimb, CSteamID>(OnPlayerDied);
                var legacyHandler = new System.Action<PlayerLife, EDeathCause, ELimb, CSteamID>(OnRocketLegacyDeath);
                try
                {
                    if (_testUnsubscribeDeath != null)
                    {
                        _testUnsubscribeDeath(handler);
                    }
                    else
                    {
                        PlayerLife.onPlayerDied -= OnPlayerDied;
                    }
                }
                catch (Exception ex)
                {
                    RoleLogger.Error("[Host]", "[WorldBroadcast] shutdown event unsub failed: " + ex.GetType().Name);
                }
                try
                {
                    if (_testUnsubscribeLegacyDeath != null) _testUnsubscribeLegacyDeath(legacyHandler);
                    else PlayerLife.RocketLegacyOnDeath -= OnRocketLegacyDeath;
                }
                catch (Exception ex)
                {
                    RoleLogger.Error("[Host]", "[WorldBroadcast] shutdown legacy unsub failed: " + ex.GetType().Name);
                }
                _initialized = false;
                _activationState = _enableMaster
                    ? EWorldBroadcastActivationState.Pending
                    : EWorldBroadcastActivationState.DisabledValid;
                _activationValid = true;
            }
            ResetForSessionCore();
        }

        /// <summary>Reset state on new session / stop / abort / disconnect (指令 J). Does NOT resubscribe.</summary>
        internal static void ResetForSession()
        {
            if (!_testBypassThreadAssert) ThreadUtil.assertIsGameThread();
            ResetForSessionCore();
        }

        private static void ResetForSessionCore()
        {
            lock (Sync)
            {
                Interlocked.Increment(ref _sessionEpoch);
                _expectedDeparture.Clear();
                _lastDeathBroadcastAt.Clear();
                _connectionState.Clear();
                _windowStartedAt = 0f;
                _windowActive = false;
                _windowCount = 0;
                _windowDiagnosticLogged = false;
            }
        }

        /// <summary>Test isolation: clear all session state and reset every test hook to defaults.</summary>
        internal static void ResetForTest()
        {
            ResetForSessionCore();
            _testTimeProvider = null;
            _testRandomIndexProvider = null;
            _testSendSink = null;
            _testDisableHostGate = false;
            _testNameResolver = null;
            _testClientNameResolver = null;
            _testPlainPresentation = true;
            _testSubscribeDeath = null;
            _testUnsubscribeDeath = null;
            _testSubscribeLegacyDeath = null;
            _testUnsubscribeLegacyDeath = null;
            _testGameThreadReady = null;
            _testChatManagerSend = null;
            LastCapturedFromPlayer = null;
            LastCapturedToPlayer = null;
            LastCapturedMode = EChatMode.SAY;
            LastCapturedIconUrl = "sentinel";
            LastCapturedRichText = true;
            _enableMaster = true;
            _broadcastJoinLeave = true;
            _broadcastDeaths = true;
            _activationState = EWorldBroadcastActivationState.Pending;
            _activationValid = true;
            _initialized = false;
            _pendingLogWritten = false;
        }

        // ===== Host gate (指令 E / 蓝图 §2.2) =====
        private static bool IsActiveP2PHost
        {
            get
            {
                if (_testDisableHostGate) return true;
                return HostManager.IsP2PHostMode && Provider.isServer && Provider.isWhitelisted;
            }
        }

        private static float Now
        {
            get
            {
                if (_testTimeProvider != null) return _testTimeProvider();
                if (_testBypassThreadAssert) return 0f;
                return GetUnityTime();
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static float GetUnityTime()
        {
            return Time.realtimeSinceStartup;
        }

        private static void AssertGameThread()
        {
            if (!_testBypassThreadAssert) ThreadUtil.assertIsGameThread();
        }

        // ===== Death event (指令 E): only authoritative PlayerLife.onPlayerDied =====

        private static void OnPlayerDied(PlayerLife sender, EDeathCause cause, ELimb limb, CSteamID instigator)
        {
            try
            {
                AssertGameThread();
                if (!IsActiveP2PHost) return;
                if (!MasterEnabled || !DeathsEnabled) return;
                if (sender == null) return;

                // (no fallback to PlayerLife.deathKiller, which is the killer).
                ResolveVictimIdentity(sender, out CSteamID victimId, out string victimName);

                // v3 (structured slots): the authoritative PlayerLife.onPlayerDied event provides
                // the final instigator. The broadcaster validates it against a strict reliability
                // gate (valid, not the victim, present in Provider.clients, safe name) before it
                // may render the selected slot's WithKiller variant.
                HandleDeathCore(victimId, victimName, cause, instigator);
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]", "[WorldBroadcast] OnPlayerDied failed: " + ex.GetType().Name);
            }
        }

        /// <summary>
        /// Primary authoritative source retained by vanilla for Rocket compatibility. This field
        /// belongs to Assembly-CSharp, so using it does not require RocketMod. It fires before
        /// SendDeath/SendDead and supplies the complete victim/cause/limb/killer transaction.
        /// </summary>
        private static void OnRocketLegacyDeath(PlayerLife sender, EDeathCause cause, ELimb limb,
            CSteamID instigator)
        {
            try
            {
                RoleLogger.Info("[Host]",
                    "[WorldBroadcast] RocketLegacy death observed cause=" + cause + " limb=" + limb);
                OnAuthoritativeDeathCommitted(sender, cause, instigator);
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]",
                    "[WorldBroadcast] RocketLegacy death failed: " + ex.GetType().Name);
            }
        }

        /// <summary>
        /// onPlayerDied event remains the preferred source; both sources converge here and the
        /// existing per-victim cooldown prevents duplicate announcements.
        /// </summary>
        internal static void OnAuthoritativeDeathCommitted(PlayerLife sender, EDeathCause cause,
            CSteamID instigator)
        {
            try
            {
                AssertGameThread();
                if (!IsActiveP2PHost || !MasterEnabled || !DeathsEnabled || sender == null) return;

                ResolveVictimIdentity(sender, out CSteamID victimId, out string victimName);

                HandleDeathCore(victimId, victimName, cause, instigator);
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]",
                    "[WorldBroadcast] authoritative death commit failed: " + ex.GetType().Name);
            }
        }

        /// <summary>
        /// Testable death core (production path minus PlayerLife-specific extraction). All checks:
        /// host gate, master/death switches, quarantine fail-closed, per-player cooldown, global rate.
        ///
        /// v3 (structured slots): the death text is built exactly as
        ///   slotIdx = ONE RNG call over the cause's slot count (5 ordinary / 2 SUICIDE)
        ///   killer  = ResolveKillerName(instigator, victimId)   // reliability gate, may be null
        ///   text    = RenderSlot(cause, victimName, killer, slotIdx)
        /// The RNG is called EXACTLY ONCE regardless of killer availability — the killer only
        /// selects the WithKiller/WithoutKiller variant of the already-chosen slot. SUICIDE never
        /// takes a killer. No sixth out-of-catalog line is ever produced.
        /// </summary>
        internal static void HandleDeathCore(CSteamID victimId, string displayName, EDeathCause cause,
            CSteamID instigator)
        {
            if (!IsActiveP2PHost)
            {
                RoleLogger.Info("[Host]", "[WorldBroadcast] death suppressed gate=inactive-host");
                return;
            }
            if (!MasterEnabled || !DeathsEnabled)
            {
                RoleLogger.Info("[Host]", "[WorldBroadcast] death suppressed gate=config");
                return;
            }
            if (victimId == CSteamID.Nil || !victimId.IsValid())
            {
                RoleLogger.Warn("[Host]", "[WorldBroadcast] death suppressed gate=victim-invalid");
                return;
            }

            // Quarantined players must not die; if abnormal, record diagnostic and do not broadcast.
            if (P2PQuarantineAdmissionService.IsActive(victimId))
            {
                RoleLogger.Warn("[Host]",
                    "[WorldBroadcast] quarantined player death ignored (fail-closed) cause=" + cause);
                return;
            }

            if (!TryConsumeDeathBudget(victimId.m_SteamID))
            {
                RoleLogger.Info("[Host]", "[WorldBroadcast] death suppressed gate=rate-limit");
                return;
            }

            string victimName = P2PWorldStatusTemplates.NormalizePlayerName(displayName);
            // v3: ONE RNG call selects the slot index (5 ordinary / 2 SUICIDE).
            int slotIdx = NextRandomIndex(P2PWorldStatusTemplates.SlotCount(cause));
            // v3: reliability-gated killer name (null when not attributable). Never re-rolls RNG.
            string killerName = ResolveKillerName(instigator, victimId);
            string text = _testPlainPresentation
                ? P2PWorldStatusTemplates.RenderSlot(cause, victimName, killerName, slotIdx)
                : P2PWorldStatusTemplates.RenderSlotRich(cause, victimName, killerName, slotIdx);

            SendWorldMessage(text, "death", cause.ToString(),
                Patches.P2PWorldChatAvatarPatch.BuildAvatarMarker(victimId));
        }

        /// <summary>
        /// when ALL of the following hold; otherwise null (the selected slot's WithoutKiller is
        /// used). Never displays a SteamID, never guesses via persona network, never treats
        /// Provider.server as a player.
        ///  1. instigator != Nil && IsValid()
        ///  2. instigator != victimId
        ///  3. an exact SteamID match exists in the current Provider.clients
        ///  4. that player has a safely normalizable characterName / playerName
        /// (The fifth condition — the selected slot defines WithKiller — is enforced in RenderSlot.)
        /// </summary>
        internal static string ResolveKillerName(CSteamID instigator, CSteamID victimId)
        {
            if (instigator == CSteamID.Nil || !instigator.IsValid()) return null;
            if (victimId != CSteamID.Nil && instigator.m_SteamID == victimId.m_SteamID) return null;

            if (_testBypassThreadAssert)
            {
                // Test console: Provider.clients is unavailable, so the reliability gate is driven
                // by the injected client-name resolver (stands in for "connected player").
                if (_testClientNameResolver == null) return null;
                try
                {
                    string t = _testClientNameResolver(instigator);
                    if (string.IsNullOrEmpty(t)) return null;
                    string safe = P2PWorldStatusTemplates.NormalizePlayerName(t);
                    if (safe == P2PWorldStatusTemplates.FallbackPlayerName) return null;
                    return safe;
                }
                catch
                {
                    return null;
                }
            }

            SteamPlayer killer = FindClient(instigator.m_SteamID);
            if (killer == null) return null;
            try
            {
                string name = null;
                if (!ReferenceEquals(killer.playerID, null))
                {
                    name = killer.playerID.characterName;
                    if (string.IsNullOrEmpty(name)) name = killer.playerID.playerName;
                }
                if (string.IsNullOrEmpty(name)) return null;
                string safe = P2PWorldStatusTemplates.NormalizePlayerName(name);
                if (safe == P2PWorldStatusTemplates.FallbackPlayerName) return null;
                return safe;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Select a channel only when it has a complete SteamPlayer owner identity. Listen-host can
        /// expose two component projections where sender.player.channel exists but its owner is not
        /// usable, while PlayerLife.channel is the authoritative server-side channel used by the
        /// same doDamage transaction. A merely non-null channel must not shadow the valid one.
        /// </summary>
        private static SteamChannel GetVictimChannel(PlayerLife sender)
        {
            try
            {
                if (!ReferenceEquals(sender, null) && !ReferenceEquals(sender.player, null) &&
                    HasUsableVictimOwner(sender.player.channel))
                    return sender.player.channel;
            }
            catch { }

            try
            {
                if (!ReferenceEquals(sender, null) && HasUsableVictimOwner(sender.channel))
                    return sender.channel;
            }
            catch { }
            return null;
        }

        private static bool HasUsableVictimOwner(SteamChannel channel)
        {
            try
            {
                return !ReferenceEquals(channel, null) &&
                       !ReferenceEquals(channel.owner, null) &&
                       !ReferenceEquals(channel.owner.playerID, null) &&
                       channel.owner.playerID.steamID.IsValid();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Pure policy mirror used by the console regression harness.</summary>
        internal static int SelectVictimChannelCandidate(bool playerChannelHasUsableOwner,
            bool lifeChannelHasUsableOwner)
        {
            if (playerChannelHasUsableOwner) return 1;
            if (lifeChannelHasUsableOwner) return 2;
            return 0;
        }

        /// <summary>
        /// Resolve the victim's authoritative SteamPlayer by the same NetId identity used by the
        /// game's PlayerLife RPCs. Listen-host can expose distinct server/client CLR projections
        /// of one player, so reference equality alone is insufficient. Exact component references
        /// remain compatibility fallbacks, never name-based guesses.
        /// </summary>
        private static SteamPlayer FindVictimSteamPlayer(PlayerLife sender)
        {
            try
            {
                Player victim = sender?.player;
                NetId senderLifeNetId = NetId.INVALID;
                try { if (!ReferenceEquals(sender, null)) senderLifeNetId = sender.GetNetId(); }
                catch { }
                List<SteamPlayer> clients = Provider.clients;
                if (clients != null)
                {
                    for (int i = 0; i < clients.Count; i++)
                    {
                        SteamPlayer candidate = clients[i];
                        if (ReferenceEquals(candidate, null) ||
                            ReferenceEquals(candidate.player, null) ||
                            ReferenceEquals(candidate.playerID, null))
                            continue;

                        bool sameLife = false;
                        try { sameLife = ReferenceEquals(candidate.player.life, sender); }
                        catch { }

                        bool sameLifeNetId = false;
                        try
                        {
                            NetId candidateLifeNetId = candidate.player.life.GetNetId();
                            sameLifeNetId = ShouldMatchVictimProjection(
                                senderLifeNetId, candidateLifeNetId, false, false);
                        }
                        catch { }

                        bool samePlayer = !ReferenceEquals(victim, null) &&
                                          ReferenceEquals(candidate.player, victim);
                        if (sameLifeNetId || ShouldMatchVictimProjection(
                                NetId.INVALID, NetId.INVALID, samePlayer, sameLife))
                        {
                            return candidate;
                        }
                    }
                }
            }
            catch { }

            try
            {
                SteamChannel channel = GetVictimChannel(sender);
                if (!ReferenceEquals(channel, null) && !ReferenceEquals(channel.owner, null) &&
                    !ReferenceEquals(channel.owner.playerID, null))
                    return channel.owner;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Resolve victim identity without ever consulting the killer. Remote players are matched
        /// to Provider.clients by exact component identity. The listen-host's own SteamPlayer is
        /// not guaranteed to be projected into Provider.clients, so the local Steam identity is
        /// allowed only after an exact match against Player.LocalPlayer or its PlayerLife.
        /// </summary>
        private static void ResolveVictimIdentity(PlayerLife sender, out CSteamID steamId,
            out string displayName)
        {
            steamId = CSteamID.Nil;
            displayName = null;

            try
            {
                SteamPlayer victim = FindVictimSteamPlayer(sender);
                if (!ReferenceEquals(victim, null) &&
                    TryProjectVictimPlayerId(victim.playerID, out steamId, out displayName))
                {
                    return;
                }
            }
            catch { }
        }

        /// <summary>
        /// Project the already-matched authoritative SteamPlayerID. SteamPlayerID overloads ==/!=
        /// without null handling, so all null checks must use ReferenceEquals or they throw and can
        /// silently erase a valid remote identity when called inside a guarded runtime callback.
        /// </summary>
        internal static bool TryProjectVictimPlayerId(SteamPlayerID playerId,
            out CSteamID steamId, out string displayName)
        {
            steamId = CSteamID.Nil;
            displayName = null;
            if (ReferenceEquals(playerId, null)) return false;

            CSteamID id = playerId.steamID;
            if (id == CSteamID.Nil || !id.IsValid()) return false;
            steamId = id;
            displayName = playerId.characterName;
            return true;
        }

        /// <summary>
        /// Pure fail-closed policy used by production and the console harness. Local Steam identity
        /// is never inferred from host mode alone: at least one exact component identity must match.
        /// </summary>
        internal static bool ShouldUseLocalVictimIdentity(bool samePlayer, bool sameLife)
        {
            return samePlayer || sameLife;
        }

        /// <summary>
        /// Unified identity policy for host and guest projections. A non-null matching PlayerLife
        /// NetId is authoritative; exact CLR references are only safe compatibility fallbacks.
        /// </summary>
        internal static bool ShouldMatchVictimProjection(NetId observedLifeNetId,
            NetId candidateLifeNetId, bool samePlayer, bool sameLife)
        {
            if (!observedLifeNetId.IsNull() && observedLifeNetId == candidateLifeNetId)
                return true;
            return samePlayer || sameLife;
        }

        // ===== Event entry points (called from existing unique callbacks) =====

        /// <summary>
        /// Called from HostManager.OnPlayerConnectedToServer (existing unique connect callback).
        /// 指令 A: promotion must already have happened; result is the transaction outcome.
        /// </summary>
        internal static void OnPlayerConnected(SteamPlayer player, QuarantinePromotionResult promotion)
        {
            try
            {
                AssertGameThread();
                if (ReferenceEquals(player, null) || ReferenceEquals(player.playerID, null)) return;
                string name = player.playerID.characterName;
                if (string.IsNullOrEmpty(name)) name = player.playerID.playerName;
                OnPlayerConnectedCore(player.playerID.steamID, name, promotion);
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]", "[WorldBroadcast] OnPlayerConnected failed: " + ex.GetType().Name);
            }
        }

        /// <summary>Testable connect core (production classification + projection registration).</summary>
        internal static void OnPlayerConnectedCore(CSteamID id, string displayName, QuarantinePromotionResult promotion)
        {
            if (!IsActiveP2PHost) return;
            if (!MasterEnabled || !JoinLeaveEnabled) return;
            if (id == CSteamID.Nil || !id.IsValid()) return;

            EWorldBroadcastKind kind;
            EConnectionProjectionState projected;
            switch (promotion)
            {
                case QuarantinePromotionResult.AlreadyApproved:
                    kind = EWorldBroadcastKind.JoinApproved;
                    projected = EConnectionProjectionState.Approved;
                    break;
                case QuarantinePromotionResult.Activated:
                    kind = EWorldBroadcastKind.JoinQuarantined;
                    projected = EConnectionProjectionState.Quarantined;
                    break;
                default:
                    return;
            }

            lock (Sync)
            {
                _connectionState[id.m_SteamID] = projected;
            }

            string name = P2PWorldStatusTemplates.NormalizePlayerName(
                string.IsNullOrEmpty(displayName) ? ResolveName(id, null) : displayName);
            string template = P2PWorldStatusTemplates.GetWorldStatusTemplate(kind)[0];
            string text = _testPlainPresentation
                ? P2PWorldStatusTemplates.Render(template, name)
                : P2PWorldStatusTemplates.RenderRich(template, name);
            SendWorldMessage(text, "connect", kind.ToString());
        }

        /// <summary>Approval transaction committed fully (指令 B). Called from P2PJoinApprovalService.Approve.</summary>
        internal static void OnPlayerApproved(CSteamID steamId)
        {
            try
            {
                AssertGameThread();
                if (!IsActiveP2PHost) return;
                if (!MasterEnabled || !JoinLeaveEnabled) return;
                if (steamId == CSteamID.Nil || !steamId.IsValid()) return;

                // connection projection state IS the generation: it was cleared on disconnect, so a
                // revoke/rejoin/approve gets a fresh broadcast. Within the SAME connection, repeated
                // approval is suppressed by checking the state transition (Quarantined -> Approved).
                lock (Sync)
                {
                    if (!_connectionState.TryGetValue(steamId.m_SteamID, out EConnectionProjectionState prev))
                        return; // no active connection projection -> never broadcast an approval
                    if (prev == EConnectionProjectionState.Approved)
                        return; // already approved this connection -> duplicate click suppressed
                    _connectionState[steamId.m_SteamID] = EConnectionProjectionState.Approved;
                }

                string name = P2PWorldStatusTemplates.NormalizePlayerName(ResolveName(steamId, null));
                string template = P2PWorldStatusTemplates.GetWorldStatusTemplate(
                    EWorldBroadcastKind.ApprovalReleased)[0];
                string text = _testPlainPresentation
                    ? P2PWorldStatusTemplates.Render(template, name)
                    : P2PWorldStatusTemplates.RenderRich(template, name);
                SendWorldMessage(text, "approval", "ApprovalReleased");
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]", "[WorldBroadcast] OnPlayerApproved failed: " + ex.GetType().Name);
            }
        }

        /// <summary>
        /// 指令 C: called before Kick on the quarantine 30s timeout path. Writes the
        /// expected-departure marker THEN broadcasts; the subsequent disconnect consumes it.
        /// </summary>
        internal static void OnApprovalTimeout(CSteamID steamId)
        {
            try
            {
                AssertGameThread();
                if (!IsActiveP2PHost) return;
                OnApprovalTimeoutCore(steamId);
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]", "[WorldBroadcast] OnApprovalTimeout failed: " + ex.GetType().Name);
            }
        }

        /// <summary>Testable timeout core (指令 C).</summary>
        internal static void OnApprovalTimeoutCore(CSteamID steamId)
        {
            if (!IsActiveP2PHost) return;
            if (!MasterEnabled || !JoinLeaveEnabled) return;
            if (steamId == CSteamID.Nil || !steamId.IsValid()) return;

            MarkExpectedDeparture(steamId.m_SteamID, EWorldBroadcastKind.ApprovalTimedOut);

            string name = P2PWorldStatusTemplates.NormalizePlayerName(ResolveName(steamId, null));
            string template = P2PWorldStatusTemplates.GetWorldStatusTemplate(
                EWorldBroadcastKind.ApprovalTimedOut)[0];
            string text = _testPlainPresentation
                ? P2PWorldStatusTemplates.Render(template, name)
                : P2PWorldStatusTemplates.RenderRich(template, name);
            SendWorldMessage(text, "timeout", "ApprovalTimedOut");
        }

        /// <summary>
        /// 指令 C/D: called from Plugin.OnEnemyDisconnectedHandler BEFORE quarantine cleanup.
        /// Consumes an expected-departure marker (suppresses the ordinary "left" message); otherwise
        /// broadcasts LeftApproved / LeftBeforeApproval based ONLY on the registered projection
        /// unconditional.
        /// </summary>
        internal static void OnPlayerDisconnected(SteamPlayer player)
        {
            try
            {
                AssertGameThread();
                if (ReferenceEquals(player, null) || ReferenceEquals(player.playerID, null)) return;
                CSteamID id = player.playerID.steamID;
                string name = player.playerID.characterName;
                if (string.IsNullOrEmpty(name)) name = player.playerID.playerName;
                OnPlayerDisconnectedCore(id, name);
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]", "[WorldBroadcast] OnPlayerDisconnected failed: " + ex.GetType().Name);
            }
        }

        internal static void OnPlayerDisconnectedCore(CSteamID id, string displayName)
        {
            if (id == CSteamID.Nil || !id.IsValid()) return;
            ulong sid = id.m_SteamID;
            if (sid == 0UL) return;

            EConnectionProjectionState state;
            lock (Sync)
            {
                state = _connectionState.TryGetValue(sid, out EConnectionProjectionState s)
                    ? s : EConnectionProjectionState.None;
                // of whether a message is sent).
                _connectionState.Remove(sid);
                _lastDeathBroadcastAt.Remove(sid);
            }

            if (!IsActiveP2PHost) return;
            if (!MasterEnabled || !JoinLeaveEnabled) return;

            // Consume expected-departure marker (suppresses the ordinary "left" message).
            if (TryConsumeExpectedDeparture(sid, out EWorldBroadcastKind timedOutKind))
            {
                RoleLogger.Info("[Host]",
                    "[WorldBroadcast] disconnect consumed expected-departure kind=" + timedOutKind +
                    " sid=" + DiagnosticMaskUtil.MaskSteamId(sid) + " suppressed=true");
                return;
            }

            EWorldBroadcastKind kind;
            switch (state)
            {
                case EConnectionProjectionState.Quarantined:
                    kind = EWorldBroadcastKind.LeftBeforeApproval;
                    break;
                case EConnectionProjectionState.Approved:
                    kind = EWorldBroadcastKind.LeftApproved;
                    break;
                default:
                    return; // rejected / unknown -> silent
            }

            string name = P2PWorldStatusTemplates.NormalizePlayerName(
                string.IsNullOrEmpty(displayName) ? ResolveName(id, null) : displayName);
            string template = P2PWorldStatusTemplates.GetWorldStatusTemplate(kind)[0];
            string text = _testPlainPresentation
                ? P2PWorldStatusTemplates.Render(template, name)
                : P2PWorldStatusTemplates.RenderRich(template, name);
            SendWorldMessage(text, "disconnect", kind.ToString());
        }

        // ===== Expected-departure marker (指令 C) =====

        private static void MarkExpectedDeparture(ulong steamId, EWorldBroadcastKind kind)
        {
            lock (Sync)
            {
                _expectedDeparture[steamId] = new ExpectedDepartureEntry
                {
                    Kind = kind,
                    WrittenAt = Now
                };
            }
        }

        private static bool TryConsumeExpectedDeparture(ulong steamId, out EWorldBroadcastKind kind)
        {
            lock (Sync)
            {
                if (_expectedDeparture.TryGetValue(steamId, out ExpectedDepartureEntry entry))
                {
                    float now = Now;
                    if (now - entry.WrittenAt < ExpectedDepartureTtlSeconds)
                    {
                        _expectedDeparture.Remove(steamId);
                        kind = entry.Kind;
                        return true;
                    }
                    _expectedDeparture.Remove(steamId); // stale -> drop, no consume
                }
                kind = default;
                return false;
            }
        }

        // ===== Death idempotency (per-player) + global throttle (指令 G) =====

        private static bool TryConsumeDeathBudget(ulong steamId)
        {
            float now = Now;
            lock (Sync)
            {
                if (_lastDeathBroadcastAt.TryGetValue(steamId, out float last))
                {
                    if (now - last < PlayerDeathCooldownSeconds) return false;
                }
                _lastDeathBroadcastAt[steamId] = now;
                return true;
            }
        }

        /// <summary>Global 8/10s window shared by connect/approval/timeout/leave/death (指令 G).</summary>
        private static bool TryConsumeGlobalBudget()
        {
            float now = Now;
            lock (Sync)
            {
                if (!_windowActive || now - _windowStartedAt >= GlobalWindowSeconds)
                {
                    _windowStartedAt = now;
                    _windowActive = true;
                    _windowCount = 0;
                    _windowDiagnosticLogged = false;
                }
                if (_windowCount >= GlobalMaxMessagesPerWindow)
                {
                    if (!_windowDiagnosticLogged)
                    {
                        _windowDiagnosticLogged = true;
                        RoleLogger.Warn("[Host]",
                            "[WorldBroadcast] global rate limit reached; dropping broadcast (8/10s)");
                    }
                    return false;
                }
                _windowCount++;
                return true;
            }
        }

        // ===== RNG (指令 H): private, main-thread, injectable, [0,exclusiveMax), fail-closed 0 =====

        private static System.Random _random = new System.Random();

        internal static int NextRandomIndex(int exclusiveMax)
        {
            if (exclusiveMax <= 0) return 0;
            if (_testRandomIndexProvider != null)
            {
                int value = _testRandomIndexProvider();
                if (value < 0 || value >= exclusiveMax) return 0; // fail-closed
                return value;
            }
            return _random.Next(exclusiveMax);
        }

        // ===== Name resolution (指令 F) =====

        private static string ResolveName(CSteamID steamId, PlayerLife sender)
        {
            if (_testNameResolver != null)
            {
                try
                {
                    string t = _testNameResolver(steamId);
                    if (!string.IsNullOrEmpty(t)) return P2PWorldStatusTemplates.NormalizePlayerName(t);
                }
                catch { }
            }

            if (_testBypassThreadAssert) return P2PWorldStatusTemplates.FallbackPlayerName;

            SteamPlayer connected = FindClient(steamId.m_SteamID);
            if (connected != null && !string.IsNullOrEmpty(connected.playerID?.characterName))
                return P2PWorldStatusTemplates.NormalizePlayerName(connected.playerID.characterName);
            if (connected != null && !string.IsNullOrEmpty(connected.playerID?.playerName))
                return P2PWorldStatusTemplates.NormalizePlayerName(connected.playerID.playerName);

            try
            {
                string cached = SteamPersonaDisplay.GetRemoteDisplayName(steamId);
                if (!string.IsNullOrEmpty(cached) && cached != "未知玩家")
                    return P2PWorldStatusTemplates.NormalizePlayerName(cached);
            }
            catch { }

            return P2PWorldStatusTemplates.FallbackPlayerName;
        }

        private static SteamPlayer FindClient(ulong steamId)
        {
            if (_testBypassThreadAssert) return null;
            try
            {
                List<SteamPlayer> clients = Provider.clients;
                if (clients == null) return null;
                for (int i = 0; i < clients.Count; i++)
                {
                    SteamPlayer sp = clients[i];
                    if (!ReferenceEquals(sp, null) && !ReferenceEquals(sp.playerID, null) &&
                        sp.playerID.steamID.m_SteamID == steamId) return sp;
                }
            }
            catch { }
            return null;
        }

        // ===== Unified sender: plugin speaker remains null; trusted presentation rich text only =====

        private static void SendWorldMessage(string text, string kind, string causeOrKind,
            string iconMarker = "")
        {
            if (string.IsNullOrEmpty(text)) return;

            if (!TryConsumeGlobalBudget()) return;

            bool sent = false;
            try
            {
                // Production keeps fromPlayer/toPlayer null so announcements are not filtered as
                // player chat. Rich text is generated only from fixed tags around normalized names.
                // Death iconMarker is consumed locally by P2PWorldChatAvatarPatch and never used as
                // a web URL. Console regression tests retain the historical plain ABI.
                P2PWorldChatSend send = _testChatManagerSend ?? ProductionChatSend;
                if (_testSendSink != null)
                {
                    _testSendSink(text);
                    sent = true;
                }
                else
                {
                    string effectiveIcon = _testPlainPresentation ? string.Empty :
                        (iconMarker ?? string.Empty);
                    send(text, Palette.SERVER, null, null,
                        EChatMode.WELCOME, effectiveIcon, !_testPlainPresentation);
                    sent = true;
                }
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]",
                    "[WorldBroadcast] send failed kind=" + kind + " ex=" + ex.GetType().Name);
            }

            if (sent)
            {
                RoleLogger.Info("[Host]",
                    "[WorldBroadcast] sent kind=" + kind + " cause=" + causeOrKind +
                    " fromPlayer=null toPlayer=null richText=" + (!_testPlainPresentation) +
                    " avatar=" + (string.IsNullOrEmpty(iconMarker) || _testPlainPresentation
                        ? "none" : "steam"));
            }
        }

        // ===== Test introspection =====

        internal static int ExpectedDepartureCountForTest
        {
            get { lock (Sync) return _expectedDeparture.Count; }
        }

        internal static bool IsInitializedForTest => _initialized;
        internal static bool ActivationValidForTest => _activationValid;
        internal static EWorldBroadcastActivationState ActivationState => _activationState;
        internal static bool IsReadyForHostStart =>
            _activationState == EWorldBroadcastActivationState.ActiveValid ||
            _activationState == EWorldBroadcastActivationState.DisabledValid;
        internal static bool ShouldSuspendPluginUpdate =>
            _activationState == EWorldBroadcastActivationState.Pending;
        internal static int SessionEpochForTest
        {
            get { lock (Sync) return _sessionEpoch; }
        }
        internal static EConnectionProjectionState ConnectionStateForTest(ulong steamId)
        {
            lock (Sync)
            {
                return _connectionState.TryGetValue(steamId, out EConnectionProjectionState s)
                    ? s : EConnectionProjectionState.None;
            }
        }
    }
}
