using SDG.Unturned;
using SteamP2PFriends.Shared;
using SteamP2PFriends.Shared.Enums;
using Steamworks;
using System;

namespace SteamP2PFriends.Host
{
    /// <summary>
    /// P2P Lobby 生命周期管理器（对齐原版 SteamP2PFriends P2PLobbyManager.cs）。
    ///
    /// 双路径设计：
    ///   - 主路径：CreateRoom -> SteamMatchmaking.CreateLobby -> LobbyCreated_t -> Ready
    ///     写 Lobbies.isHost=true，让 vanilla LinkLobby 走我们的 SetLobbyGameServer 路径
    ///   - 回退路径：12s 超时或 CreateLobby 失败 -> EnableDirectHostMode -> DirectHost
    ///     客机需手动输入 SteamID 加入（P2PJoinManager.TryConnectToHost）
    ///
    /// Plugin.Update 每帧调 Tick() 推进状态机。
    /// </summary>
    public static class P2PLobbyManager
    {
        public static EP2PLobbyState State { get; private set; }
        public static string LastError { get; private set; }
        public static bool IsInRoom => State == EP2PLobbyState.Ready;
        public static bool IsHost => Lobbies.isHost;

#pragma warning disable
        private static CallResult<LobbyCreated_t> _lobbyCreated;
#pragma warning restore
        private static bool _initialized;
        private static float _createStartedRealtime;

        private static bool _linkOnReady;
        private static bool _linkLobbyCalled;

        // v0.2.3.23 P0-C3：pending CallResult 取消所需的 SteamAPICall_t 句柄
        private static SteamAPICall_t _pendingCall;
        private static bool _hasPendingCall;

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            _lobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            Lobbies.lobbiesEntered += OnVanillaLobbiesEntered;
        }

        public static void Shutdown()
        {
            if (!_initialized) return;
            Lobbies.lobbiesEntered -= OnVanillaLobbiesEntered;
            _lobbyCreated = null;
            _initialized = false;
            Reset();
        }

        public static void CreateRoom()
        {
            // v0.2.3.23 P0-C2：未初始化时 fail-closed（审计报告-Codex §3 P0-Critical-2）
            //   未初始化意味着 DiagnosticBuildValid=false 或生命周期损坏，
            //   不得自动降级到 DirectHost 绕过 INVALID 门控。
            if (!_initialized || _lobbyCreated == null)
            {
                Fail("P2PLobbyManager 未初始化，拒绝创建大厅（fail-closed）。");
                return;
            }

            if (State == EP2PLobbyState.Creating) return;

            if (Lobbies.inLobby)
            {
                State = EP2PLobbyState.Ready;
                NotifyStateChanged();
                return;
            }

            if (!SteamUser.BLoggedOn())
            {
                Fail("Steam 未登录，请先登录 Steam 客户端。");
                return;
            }

            LastError = null;
            State = EP2PLobbyState.Creating;
            _createStartedRealtime = UnityEngine.Time.realtimeSinceStartup;
            _linkLobbyCalled = false;
            NotifyStateChanged();

            RoleLogger.Info("[Host]", "[P2P-Lobby] Creating Steam lobby...");
            SteamAPICall_t handle = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePrivate, 8);
            _lobbyCreated.Set(handle);

            // v0.2.3.23 P0-C3：记录 pending 句柄用于 AbortHostStart 时取消
            _pendingCall = handle;
            _hasPendingCall = true;
        }

        /// <summary>
        /// 由 HostManager.OnServerHosted 调用：异步创建 lobby，Tick 检测 Ready 后自动 LinkLobbyForP2P。
        /// v0.2.3.23 P0-C2：返回 bool 表示是否真正进入 Creating 流程。
        /// </summary>
        public static bool CreateRoomAndLinkOnReady()
        {
            // v0.2.3.23 P0-C2：fail-closed 守卫
            //   未初始化时返回 false，调用方必须检查返回值并进入 AbortHostStart。
            //   不得在此处自动 EnableDirectHostMode（审计明确禁止降级绕过 INVALID 门控）。
            if (!_initialized || _lobbyCreated == null)
            {
                Fail("P2PLobbyManager 未初始化，拒绝创建大厅（fail-closed）。");
                return false;
            }

            _linkOnReady = true;
            _linkLobbyCalled = false;
            CreateRoom();
            return State == EP2PLobbyState.Creating || State == EP2PLobbyState.Ready;
        }

        public static void EnableDirectHostMode()
        {
            if (!SteamUser.BLoggedOn())
            {
                Fail("Steam 未登录，无法使用直连模式。");
                return;
            }

            LastError = null;
            State = EP2PLobbyState.Failed;  // 降级标记
            RoleLogger.Info("[Host]", $"[P2P-Lobby] Direct P2P host mode enabled for {SteamUser.GetSteamID().m_SteamID}");
            NotifyStateChanged();
        }

        /// <summary>
        /// Plugin.Update 每帧调用。
        /// </summary>
        public static void Tick()
        {
            if (State == EP2PLobbyState.Creating && Lobbies.inLobby)
            {
                State = EP2PLobbyState.Ready;
                LastError = null;
                NotifyStateChanged();
                TryAutoLinkLobby();
                return;
            }

            if (State != EP2PLobbyState.Creating) return;

            if (UnityEngine.Time.realtimeSinceStartup - _createStartedRealtime > 12f)
            {
                Fail("创建 Steam 大厅超时。");
                EnableDirectHostMode();
            }
        }

        /// <summary>
        /// State 变为 Ready 时自动调 LinkLobbyForP2P（如果 _linkOnReady=true）。
        /// </summary>
        private static void TryAutoLinkLobby()
        {
            if (!_linkOnReady || _linkLobbyCalled) return;
            if (State != EP2PLobbyState.Ready) return;

            _linkLobbyCalled = true;
            LinkLobbyForP2P();
        }

        /// <summary>
        /// 写 lobby metadata：SetLobbyGameServer(lobby, 0, 0, hostSteamId)。
        /// 客机收 LobbyGameCreated_t 后用 SteamID 走 P2P 连接。
        /// </summary>
        public static void LinkLobbyForP2P()
        {
            try
            {
                CSteamID hostSteamId = SteamUser.GetSteamID();
                if (Lobbies.inLobby && Lobbies.isHost)
                {
                    SteamMatchmaking.SetLobbyGameServer(Lobbies.currentLobby, 0, 0, hostSteamId);
                    RoleLogger.Info("[Host]", $"[P2P-Lobby] Linked lobby {Lobbies.currentLobby.m_SteamID} to P2P host {hostSteamId.m_SteamID}");
                }
                else
                {
                    RoleLogger.Info("[Host]", $"[P2P-Lobby] Direct P2P host ready at {hostSteamId.m_SteamID}");
                }
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]", $"LinkLobbyForP2P 失败: {ex.Message}");
            }
        }

        public static void InviteFriends()
        {
            if (State == EP2PLobbyState.Ready && Lobbies.inLobby)
            {
                if (!Lobbies.canOpenInvitations)
                {
                    MenuUI.alert("需要开启 Steam 覆盖层才能邀请好友。");
                    return;
                }
                Lobbies.openInvitations();
                return;
            }

            // DirectHost 模式：复制 SteamID 到剪贴板
            try
            {
                UnityEngine.GUIUtility.systemCopyBuffer = SteamUser.GetSteamID().m_SteamID.ToString();
                RoleLogger.Info("[Host]", "[P2P-Lobby] SteamID 已复制到剪贴板");
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]", $"复制 SteamID 失败: {ex.Message}");
            }

            if (SteamUtils.IsOverlayEnabled())
            {
                SteamFriends.ActivateGameOverlay("Friends");
                MenuUI.alert("已复制你的 Steam ID。请让好友在「加入好友」中输入此 ID。");
            }
            else
            {
                MenuUI.alert($"请让好友在「加入好友」中输入你的 Steam ID：{SteamUser.GetSteamID().m_SteamID}");
            }
        }

        private static void OnLobbyCreated(LobbyCreated_t callback, bool ioFailure)
        {
            // v0.2.3.23 P0-C3：回调触发即表示 pending 已完成，清除 pending 标记
            _hasPendingCall = false;
            _pendingCall = default(SteamAPICall_t);

            if (ioFailure)
            {
                Fail("Steam 网络 IO 失败，无法创建大厅。");
                EnableDirectHostMode();
                return;
            }

            RoleLogger.Info("[Host]", $"[P2P-Lobby] LobbyCreated result={callback.m_eResult} lobby={callback.m_ulSteamIDLobby}");

            if (callback.m_eResult != EResult.k_EResultOK)
            {
                Fail(DescribeResult(callback.m_eResult));
                EnableDirectHostMode();
                return;
            }

            try
            {
                ReflectionUtil.SetStaticField(typeof(Lobbies), "isHost", true);
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]", $"设置 Lobbies.isHost 失败: {ex.Message}");
            }

            if (Lobbies.inLobby)
            {
                State = EP2PLobbyState.Ready;
                LastError = null;
                NotifyStateChanged();
            }
        }

        private static void OnVanillaLobbiesEntered()
        {
            if (State == EP2PLobbyState.Creating || State == EP2PLobbyState.None)
            {
                State = EP2PLobbyState.Ready;
                LastError = null;
                NotifyStateChanged();
            }
        }

        private static void Fail(string message)
        {
            State = EP2PLobbyState.Failed;
            LastError = message;
            RoleLogger.Warn("[Host]", $"[P2P-Lobby] {message}");
            NotifyStateChanged();
        }

        private static void Reset()
        {
            State = EP2PLobbyState.None;
            LastError = null;
            // v0.2.3.23 P0-C3：Reset 也需清理 pending 与链接标志
            _linkOnReady = false;
            _linkLobbyCalled = false;
            _createStartedRealtime = 0f;
            CancelPendingCall();
        }

        /// <summary>
        /// v0.2.3.23 P0-C3：AbortHostStart / StopP2PServer / 新会话开始时调用。
        /// 审计报告-Codex §3 P0-Critical-3 要求：
        ///   - State=None
        ///   - LastError 清理
        ///   - _linkOnReady=false / _linkLobbyCalled=false
        ///   - _createStartedRealtime=0
        ///   - 若有 pending CallResult，调用取消接口
        ///   - 不把 _initialized 改为 false，不丢失事件订阅
        /// </summary>
        public static void ResetForAbort()
        {
            try
            {
                RoleLogger.Info("[Host]",
                    $"[P2P-Lobby] ResetForAbort: stateBefore={State} hasPendingCall={_hasPendingCall} initialized={_initialized}");
                State = EP2PLobbyState.None;
                LastError = null;
                _linkOnReady = false;
                _linkLobbyCalled = false;
                _createStartedRealtime = 0f;
                CancelPendingCall();
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]", $"ResetForAbort 异常（不阻断）: {ex.Message}");
            }
        }

        /// <summary>
        /// v0.2.3.23 P0-C3：取消 pending CallResult。
        /// Steamworks.NET 的 CallResult 没有公开 Cancel API，但可以解除 Set 绑定。
        /// 通过重新 Set(default) 解除 callback 绑定，并标记 pending 已取消。
        /// </summary>
        private static void CancelPendingCall()
        {
            if (!_hasPendingCall) return;
            try
            {
                // CallResult&lt;T&gt;.Set(default) 解除当前绑定的 SteamAPICall_t
                // 已完成或已取消的 call 会自然被忽略
                if (_lobbyCreated != null)
                {
                    _lobbyCreated.Set(default(SteamAPICall_t));
                }
                RoleLogger.Info("[Host]",
                    $"[P2P-Lobby] CancelPendingCall: pending SteamAPICall_t={_pendingCall.m_SteamAPICall} 已解除绑定");
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]", $"CancelPendingCall 异常（不阻断）: {ex.Message}");
            }
            _hasPendingCall = false;
            _pendingCall = default(SteamAPICall_t);
        }

        private static void NotifyStateChanged()
        {
            // v0.2 不使用 StateChanged 事件，状态变化由 Tick 轮询消费
        }

        private static string DescribeResult(EResult result)
        {
            switch (result)
            {
                case EResult.k_EResultAccessDenied:
                    return "Steam 拒绝创建大厅 (AccessDenied)，已切换直连模式。";
                case EResult.k_EResultNoConnection:
                    return "Steam 网络不可用，请确认 Steam 在线。";
                case EResult.k_EResultFail:
                    return "Steam 创建大厅失败。";
                case EResult.k_EResultLimitExceeded:
                    return "Steam 大厅数量已达上限。";
                default:
                    return $"Steam 创建大厅失败：{result}";
            }
        }
    }
}
