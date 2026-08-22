using SDG.Unturned;
using SteamP2PFriends.Patches;
using SteamP2PFriends.Shared;
using Steamworks;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace SteamP2PFriends.Host
{
    internal interface IApprovalRuntimeContext
    {
        bool IsActiveP2PHost { get; }
        CSteamID LocalUser { get; }
        float RealtimeSinceStartup { get; }
    }

    internal interface IApprovalWhitelistProxy
    {
        bool Contains(CSteamID target);
        bool TryAdd(CSteamID target, string tag, out string feedback);
        bool TryRemove(CSteamID target, out string feedback);
    }

    internal sealed class ApprovalRuntimeContext : IApprovalRuntimeContext
    {
        public bool IsActiveP2PHost => HostManager.IsP2PHostMode && Provider.isServer && Provider.isWhitelisted;
        public CSteamID LocalUser => Provider.user;
        public float RealtimeSinceStartup => Time.realtimeSinceStartup;
    }

    internal sealed class ApprovalWhitelistProxy : IApprovalWhitelistProxy
    {
        public bool Contains(CSteamID target)
        {
            return target != CSteamID.Nil && P2PWhitelistService.ContainsForUi(target);
        }

        public bool TryAdd(CSteamID target, string tag, out string feedback)
        {
            return P2PWhitelistService.TryAdd(target, tag, out feedback);
        }

        public bool TryRemove(CSteamID target, out string feedback)
        {
            return P2PWhitelistService.TryRemoveForApprovalRevoke(target, out feedback);
        }
    }

    internal enum QuarantinePromotionResult : byte
    {
        Ignored,
        AlreadyApproved,
        Activated
    }

    internal enum P2PApprovalRegistrationResult : byte
    {
        Ignored = 0,
        Trusted = 1,
        PendingQuarantine = 2,
        CapacityRejected = 3,
        SignalFailed = 4,
        WhitelistCheckFailed = 5
    }

    internal readonly struct P2PPendingApproval
    {
        internal readonly CSteamID SteamId;
        internal readonly float EnteredAt;
        internal readonly float Deadline;

        internal P2PPendingApproval(CSteamID steamId, float enteredAt, float deadline)
        {
            SteamId = steamId;
            EnteredAt = enteredAt;
            Deadline = deadline;
        }
    }

    /// <summary>
    /// Route B admission state machine. Unknown P2P guests pass the native handshake first,
    /// then become PendingQuarantine only after Provider.onServerConnected has created their
    /// SteamPlayer. This manager owns only the in-session state; P2PWhitelistService owns
    /// persistence and its transactional fault handling.
    /// </summary>
    internal static class P2PApprovalManager
    {
        internal static bool UsesRouteB => true;
        internal const int MaxPendingEntries = 16;
        internal const float ApprovalLifetimeSeconds = 30f;
        internal const string ApprovedTag = "APPROVED";

        // This high bit is outside the currently defined native widget flags. It lets the guest
        // suppress local prediction while server-side invoke and damage gates remain authoritative.
        internal const uint QuarantineSignalMask = 0x80000000u;
        internal static readonly EPluginWidgetFlags QuarantineSignalFlag =
            (EPluginWidgetFlags)unchecked((int)QuarantineSignalMask);

        private static readonly ConcurrentDictionary<ulong, P2PPendingApproval> Pending =
            new ConcurrentDictionary<ulong, P2PPendingApproval>();
        private static readonly object AdmissionSync = new object();

        private static IApprovalRuntimeContext _runtime = new ApprovalRuntimeContext();
        private static IApprovalWhitelistProxy _whitelist = new ApprovalWhitelistProxy();
        private static Action<ulong, bool> _testSignalCallback;
        private static Action<ulong, string> _testKickCallback;
        private static Action<ulong, string> _testChatCallback;

        internal static bool _testBypassThreadAssert;

        internal static int PendingCount => Pending.Count;
        internal static bool LifecycleHooksInstalled { get; private set; }

        private static void AssertGameThread()
        {
            if (!_testBypassThreadAssert) ThreadUtil.assertIsGameThread();
        }

        private static float Now => _runtime.RealtimeSinceStartup;

        internal static void InstallProviderLifecycleHooks()
        {
            AssertGameThread();
            Provider.onServerConnected -= OnServerConnected;
            Provider.onServerConnected += OnServerConnected;
            Provider.onServerDisconnected -= OnServerDisconnected;
            Provider.onServerDisconnected += OnServerDisconnected;
            LifecycleHooksInstalled = true;
            RoleLogger.Info("[Host]", "[P2P-Approval] Route B lifecycle hooks installed");
        }

        internal static void UninstallProviderLifecycleHooks()
        {
            if (!LifecycleHooksInstalled) return;
            Provider.onServerConnected -= OnServerConnected;
            Provider.onServerDisconnected -= OnServerDisconnected;
            LifecycleHooksInstalled = false;
            ResetForSession();
            RoleLogger.Info("[Host]", "[P2P-Approval] Route B lifecycle hooks removed");
        }

        /// <summary>
        /// Called from the whitelist postfix during ReadyToConnect. It deliberately changes only
        /// the native whitelist result for an active P2P listen-host. Ban, password, integrity,
        /// capacity, and all other native validation checks still execute unchanged afterwards.
        /// </summary>
        internal static bool CanPermitHandshake(CSteamID steamId)
        {
            if (!_runtime.IsActiveP2PHost || steamId == CSteamID.Nil || !steamId.IsValid()) return false;
            CSteamID localUser = _runtime.LocalUser;
            if (steamId == localUser) return false;
            return true;
        }

        internal static bool IsPending(CSteamID steamId)
        {
            return steamId != CSteamID.Nil && Pending.ContainsKey(steamId.m_SteamID);
        }

        internal static bool TryGetPending(CSteamID steamId, out P2PPendingApproval pending)
        {
            if (steamId == CSteamID.Nil)
            {
                pending = default;
                return false;
            }
            return Pending.TryGetValue(steamId.m_SteamID, out pending);
        }

        internal static IReadOnlyList<P2PPendingApproval> SnapshotPending()
        {
            var snapshot = new List<P2PPendingApproval>(Pending.Values);
            snapshot.Sort((left, right) => left.EnteredAt.CompareTo(right.EnteredAt));
            return snapshot;
        }

        private static void OnServerConnected(CSteamID steamId)
        {
            try
            {
                AssertGameThread();
                RegisterConnected(steamId);
            }
            catch (Exception ex)
            {
                RoleLogger.Error("[Host]", "[P2P-Approval] onServerConnected registration failed: " + ex);
                SafeKick(steamId.m_SteamID, "Unable to initialize host approval.");
            }
        }

        private static void OnServerDisconnected(CSteamID steamId)
        {
            try
            {
                AssertGameThread();
                ForgetDisconnected(steamId);
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]", "[P2P-Approval] onServerDisconnected cleanup failed: " + ex.GetType().Name);
            }
        }

        internal static P2PApprovalRegistrationResult RegisterConnected(CSteamID steamId)
        {
            AssertGameThread();
            if (!_runtime.IsActiveP2PHost || steamId == CSteamID.Nil || !steamId.IsValid() ||
                steamId == _runtime.LocalUser)
                return P2PApprovalRegistrationResult.Ignored;

            SteamPlayer player = FindClient(steamId.m_SteamID);
            if (player == null)
            {
                RoleLogger.Warn("[Host]", "[P2P-Approval] connected SteamID was absent from Provider.clients: " + steamId.m_SteamID);
                SafeKick(steamId.m_SteamID, "Unable to initialize host approval.");
                return P2PApprovalRegistrationResult.SignalFailed;
            }

            bool trusted;
            try
            {
                trusted = _whitelist.Contains(steamId);
            }
            catch (Exception ex)
            {
                RoleLogger.Error("[Host]", "[P2P-Approval] whitelist lookup failed for connected player: " + ex.GetType().Name);
                SafeKick(steamId.m_SteamID, "Unable to verify host approval.");
                return P2PApprovalRegistrationResult.WhitelistCheckFailed;
            }

            if (trusted)
            {
                Pending.TryRemove(steamId.m_SteamID, out _);
                SetQuarantineSignal(steamId, player, false);
                RoleLogger.Info("[Host]", "[P2P-Approval] trusted player entered world: steamId=" + steamId.m_SteamID);
                NotifyWorldConnected(player, QuarantinePromotionResult.AlreadyApproved);
                HostManager.ApplySessionAdminPolicyAfterApproval(player);
                return P2PApprovalRegistrationResult.Trusted;
            }

            P2PPendingApproval entry;
            bool capacityRejected = false;
            lock (AdmissionSync)
            {
                if (!Pending.TryGetValue(steamId.m_SteamID, out entry))
                {
                    if (Pending.Count >= MaxPendingEntries)
                    {
                        capacityRejected = true;
                    }
                    else
                    {
                        float now = Now;
                        entry = new P2PPendingApproval(steamId, now, now + ApprovalLifetimeSeconds);
                        if (!Pending.TryAdd(steamId.m_SteamID, entry))
                        {
                            RoleLogger.Warn("[Host]", "[P2P-Approval] pending registration race lost: steamId=" + steamId.m_SteamID);
                            return P2PApprovalRegistrationResult.Ignored;
                        }
                    }
                }
            }

            if (capacityRejected)
            {
                RoleLogger.Warn("[Host]", "[P2P-Approval] pending capacity reached; kicking steamId=" + steamId.m_SteamID);
                SafeKick(steamId.m_SteamID, "房主待审核队列已满。");
                return P2PApprovalRegistrationResult.CapacityRejected;
            }

            if (!SetQuarantineSignal(steamId, player, true))
            {
                Pending.TryRemove(steamId.m_SteamID, out _);
                SafeKick(steamId.m_SteamID, "Unable to initialize host approval.");
                return P2PApprovalRegistrationResult.SignalFailed;
            }

            RoleLogger.Info("[Host]", "[P2P-Approval] Pending added: steamId=" + steamId.m_SteamID +
                " deadline=" + entry.Deadline.ToString("F1"));
            SendTargetedChat(steamId.m_SteamID, "已进入世界，正在等待房主审核。审核前无法行动或交互，并处于无敌状态。");
            NotifyWorldConnected(player, QuarantinePromotionResult.Activated);
            return P2PApprovalRegistrationResult.PendingQuarantine;
        }

        internal static bool ApprovePlayer(CSteamID steamId, out string feedback)
        {
            AssertGameThread();
            feedback = string.Empty;
            if (!_runtime.IsActiveP2PHost)
            {
                feedback = "当前不是活动 P2P 房主";
                return false;
            }
            if (steamId == CSteamID.Nil || !steamId.IsValid() || !Pending.TryGetValue(steamId.m_SteamID, out _))
            {
                feedback = "该玩家不在待审核列表中";
                return false;
            }

            if (!_whitelist.TryAdd(steamId, ApprovedTag, out feedback))
            {
                RoleLogger.Warn("[Host]", "[P2P-Approval] Approve failed: steamId=" + steamId.m_SteamID + " feedback=" + feedback);
                return false;
            }

            Pending.TryRemove(steamId.m_SteamID, out _);
            SteamPlayer player = FindClient(steamId.m_SteamID);
            if ((player == null && _testSignalCallback == null) || !SetQuarantineSignal(steamId, player, false))
            {
                SafeKick(steamId.m_SteamID, "Approval completed; reconnect required.");
                feedback = "白名单已写入，但解除隔离信号失败；已要求客机重连";
                RoleLogger.Error("[Host]", "[P2P-Approval] approval signal cleanup failed: steamId=" + steamId.m_SteamID);
                return false;
            }

            if (player != null) HostManager.ApplySessionAdminPolicyAfterApproval(player);
            if (!_testBypassThreadAssert)
            {
                try { P2PWorldStatusBroadcaster.OnPlayerApproved(steamId); }
                catch (Exception ex) { RoleLogger.Warn("[Host]", "[WorldBroadcast] approval notify failed: " + ex.GetType().Name); }
            }

            SendTargetedChat(steamId.m_SteamID, "房主已允许你进入，行动限制已解除。");
            feedback = "已允许";
            RoleLogger.Info("[Host]", "[P2P-Approval] Approve success: steamId=" + steamId.m_SteamID);
            return true;
        }

        internal static bool RejectPlayer(CSteamID steamId, out string feedback)
        {
            AssertGameThread();
            feedback = string.Empty;
            if (!_runtime.IsActiveP2PHost)
            {
                feedback = "当前不是活动 P2P 房主";
                return false;
            }
            if (steamId == CSteamID.Nil || !steamId.IsValid() || !Pending.TryRemove(steamId.m_SteamID, out _))
            {
                feedback = "该玩家不在待审核列表中";
                return false;
            }

            SteamPlayer player = FindClient(steamId.m_SteamID);
            if (player != null || _testSignalCallback != null) SetQuarantineSignal(steamId, player, false);
            SafeKick(steamId.m_SteamID, "房主拒绝了你的加入请求。");
            feedback = "已拒绝并断开";
            RoleLogger.Info("[Host]", "[P2P-Approval] Reject success: steamId=" + steamId.m_SteamID);
            return true;
        }

        /// <summary>
        /// Removes a previously authorized remote guest from the persistent whitelist and then
        /// disconnects that guest. The local host identity is never a valid revoke target.
        /// </summary>
        internal static bool RevokePlayer(CSteamID steamId, out string feedback)
        {
            AssertGameThread();
            feedback = string.Empty;
            if (!_runtime.IsActiveP2PHost)
            {
                feedback = "当前不是活动 P2P 房主";
                return false;
            }
            if (steamId == CSteamID.Nil || !steamId.IsValid() || steamId == _runtime.LocalUser)
            {
                feedback = "不能撤销房主自身的授权";
                return false;
            }
            if (Pending.ContainsKey(steamId.m_SteamID))
            {
                feedback = "该玩家仍在待审核列表中";
                return false;
            }
            if (!_whitelist.Contains(steamId))
            {
                feedback = "该玩家不在已授权列表中";
                return false;
            }
            if (!_whitelist.TryRemove(steamId, out feedback))
            {
                RoleLogger.Warn("[Host]", "[P2P-Approval] Revoke failed: steamId=" + steamId.m_SteamID + " feedback=" + feedback);
                return false;
            }

            SteamPlayer player = FindClient(steamId.m_SteamID);
            if (player != null || _testSignalCallback != null) SetQuarantineSignal(steamId, player, false);
            SafeKick(steamId.m_SteamID, "房主已撤销你的加入授权。");
            feedback = "已撤销允许并断开";
            RoleLogger.Info("[Host]", "[P2P-Approval] Revoke success: steamId=" + steamId.m_SteamID);
            return true;
        }

        internal static void Tick()
        {
            AssertGameThread();
            if (!_runtime.IsActiveP2PHost || Pending.IsEmpty) return;

            float now = Now;
            foreach (KeyValuePair<ulong, P2PPendingApproval> pair in Pending)
            {
                if (now < pair.Value.Deadline || !Pending.TryRemove(pair.Key, out _)) continue;

                SteamPlayer player = FindClient(pair.Key);
                if (player != null || _testSignalCallback != null) SetQuarantineSignal(new CSteamID(pair.Key), player, false);
                if (!_testBypassThreadAssert)
                {
                    try { P2PWorldStatusBroadcaster.OnApprovalTimeout(new CSteamID(pair.Key)); }
                    catch (Exception ex) { RoleLogger.Warn("[Host]", "[WorldBroadcast] timeout notify failed: " + ex.GetType().Name); }
                }
                SafeKick(pair.Key, "房主审核超时。");
                RoleLogger.Info("[Host]", "[P2P-Approval] Timeout kick: steamId=" + pair.Key);
            }
        }

        internal static void ForgetDisconnected(CSteamID steamId)
        {
            AssertGameThread();
            if (steamId == CSteamID.Nil) return;
            bool removed = Pending.TryRemove(steamId.m_SteamID, out _);
            Patch_PlayerDashboardPlayersUI.ForgetPlayer(steamId.m_SteamID);
            if (removed)
                RoleLogger.Info("[Host]", "[P2P-Approval] Pending removed after disconnect: steamId=" + steamId.m_SteamID);
        }

        internal static void ResetForSession()
        {
            AssertGameThread();
            Pending.Clear();
            Patch_PlayerDashboardPlayersUI.ResetForSession();
            RoleLogger.Info("[Host]", "[P2P-Approval] session state reset");
        }

        private static bool SetQuarantineSignal(CSteamID steamId, SteamPlayer steamPlayer, bool enabled)
        {
            if (_testSignalCallback != null)
            {
                _testSignalCallback(steamId.m_SteamID, enabled);
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
                RoleLogger.Error("[Host]", "[P2P-Approval] quarantine signal update failed: " + ex.GetType().Name);
                return false;
            }
        }

        private static SteamPlayer FindClient(ulong steamId)
        {
            // The console test host deliberately does not initialize Unturned.Provider.
            if (_testBypassThreadAssert) return null;
            for (int index = 0; index < Provider.clients.Count; index++)
            {
                SteamPlayer player = Provider.clients[index];
                if (!ReferenceEquals(player, null) && !ReferenceEquals(player.playerID, null) &&
                    player.playerID.steamID.m_SteamID == steamId)
                    return player;
            }
            return null;
        }

        private static void NotifyWorldConnected(SteamPlayer player, QuarantinePromotionResult promotion)
        {
            if (_testBypassThreadAssert) return;
            try { P2PWorldStatusBroadcaster.OnPlayerConnected(player, promotion); }
            catch (Exception ex) { RoleLogger.Warn("[Host]", "[WorldBroadcast] connect forward failed: " + ex.GetType().Name); }
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
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]", "[P2P-Approval] targeted chat failed: " + ex.GetType().Name);
            }
        }

        private static void SafeKick(ulong steamId, string reason)
        {
            if (steamId == 0UL) return;
            if (_testKickCallback != null)
            {
                _testKickCallback(steamId, reason);
                return;
            }
            try { Provider.kick(new CSteamID(steamId), reason); }
            catch (Exception ex) { RoleLogger.Warn("[Host]", "[P2P-Approval] kick failed: " + ex.GetType().Name); }
        }

        // Test-only core path: it exercises the state machine without a Unity SteamPlayer.
        internal static P2PApprovalRegistrationResult RegisterConnectedForTest(CSteamID steamId)
        {
            AssertGameThread();
            if (!_runtime.IsActiveP2PHost || steamId == CSteamID.Nil || !steamId.IsValid() || steamId == _runtime.LocalUser)
                return P2PApprovalRegistrationResult.Ignored;
            if (_whitelist.Contains(steamId)) return P2PApprovalRegistrationResult.Trusted;

            lock (AdmissionSync)
            {
                if (Pending.ContainsKey(steamId.m_SteamID)) return P2PApprovalRegistrationResult.PendingQuarantine;
                if (Pending.Count >= MaxPendingEntries) return P2PApprovalRegistrationResult.CapacityRejected;
                float now = Now;
                return Pending.TryAdd(steamId.m_SteamID,
                    new P2PPendingApproval(steamId, now, now + ApprovalLifetimeSeconds))
                    ? P2PApprovalRegistrationResult.PendingQuarantine
                    : P2PApprovalRegistrationResult.Ignored;
            }
        }

        internal static bool CanPermitHandshakeForTest(CSteamID steamId)
        {
            return CanPermitHandshake(steamId);
        }

        internal static IDisposable InstallTestDependencies(IApprovalRuntimeContext runtime,
            IApprovalWhitelistProxy whitelist, Action<ulong, bool> signal = null,
            Action<ulong, string> kick = null, Action<ulong, string> chat = null)
        {
            IApprovalRuntimeContext previousRuntime = _runtime;
            IApprovalWhitelistProxy previousWhitelist = _whitelist;
            Action<ulong, bool> previousSignal = _testSignalCallback;
            Action<ulong, string> previousKick = _testKickCallback;
            Action<ulong, string> previousChat = _testChatCallback;
            bool previousBypass = _testBypassThreadAssert;

            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _whitelist = whitelist ?? throw new ArgumentNullException(nameof(whitelist));
            _testSignalCallback = signal;
            _testKickCallback = kick;
            _testChatCallback = chat;
            _testBypassThreadAssert = true;
            ResetForSession();

            return new RestoreScope(() =>
            {
                Pending.Clear();
                _runtime = previousRuntime;
                _whitelist = previousWhitelist;
                _testSignalCallback = previousSignal;
                _testKickCallback = previousKick;
                _testChatCallback = previousChat;
                _testBypassThreadAssert = previousBypass;
            });
        }

        private sealed class RestoreScope : IDisposable
        {
            private System.Action _restore;
            internal RestoreScope(System.Action restore) { _restore = restore; }
            public void Dispose()
            {
                System.Action restore = _restore;
                _restore = null;
                restore?.Invoke();
            }
        }
    }
}
