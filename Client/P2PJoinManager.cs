using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using SteamP2PFriends.Shared.Enums;
using Steamworks;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace SteamP2PFriends.Client
{
    /// <summary>
    ///
    ///   - 删除 PlayerInput.serverBoundsHistory 反射、判断、日志和注释。
    ///   - LocalPlayerCreated 后仅记录 AcceptedAndLocalComponentsInitialized 阶段（不命名真实 GameplayReady）。
    ///   - 30s watchdog 仅报警，不调用 Provider.disconnect / RequestDisconnect。
    ///   - 不强行关闭 LoadingUI，不修改原生 loading flag。
    /// </summary>
    public static class P2PJoinManager
    {
        private static EJoinState _state = EJoinState.Idle;
        private static ulong _targetSteamId;
        private static int _attempt;
        private static float _connectStartTime;
        private static bool _subscribed;
        private static ESteamConnectionFailureInfo _lastFailureInfo;

        // 信号：LocalComponentsInitialized 是否已触发（由 GameplayReadyTracker 回调）
        private static bool _localComponentsInitializedSignaled;

        private static bool _postAcceptedWatchdogFired;

        /// <summary>ServerAccepted 后 watchdog 超时（30s）</summary>
        private const float PostAcceptedWatchdogSeconds = 30f;
        /// <summary>ServerAccepted 阶段进入时间</summary>
        private static float _serverAcceptedTime;
        /// <summary>最后记录的阶段（用于 watchdog 落盘）</summary>
        private static string _lastStage;

        private static float _acceptedAndLocalComponentsTime;

        private static float _disconnectStartTime;
        private const float DisconnectTimeoutSeconds = 10f;

        private const int MaxAttempts = 1;
        private const float TimeoutSeconds = 35f;

        public static EJoinState State => _state;
        public static ESteamConnectionFailureInfo LastFailureInfo => _lastFailureInfo;

        public static bool IsSafeToRetry
        {
            get
            {
                bool stateAllows = _state == EJoinState.Idle ||
                                   _state == EJoinState.TeardownComplete ||
                                   _state == EJoinState.Failed;
                if (!stateAllows || Provider.isConnected) return false;

                // Beta-7：onClientDisconnected 触发后 Level.isExiting 可能仍为 true。
                // 此时立即 Provider.connect 会反复打断原版 teardown 并触发 CONNECT_RATE_LIMITING。
                return !Level.isLoading && !Level.isExiting;
            }
        }

        internal static bool IsSafeToRetryForTest(
            EJoinState state,
            bool providerConnected,
            bool levelLoading,
            bool levelExiting)
        {
            bool stateAllows = state == EJoinState.Idle ||
                               state == EJoinState.TeardownComplete ||
                               state == EJoinState.Failed;
            return stateAllows && !providerConnected && !levelLoading && !levelExiting;
        }

        public static void Initialize()
        {
            if (_subscribed) return;
            _subscribed = true;
            Provider.onClientConnected += OnClientConnected;
            Provider.onClientDisconnected += OnClientDisconnected;
        }

        public static void Shutdown()
        {
            if (!_subscribed) return;
            _subscribed = false;
            Provider.onClientConnected -= OnClientConnected;
            Provider.onClientDisconnected -= OnClientDisconnected;
        }

        /// <summary>
        /// </summary>
        public static bool TryConnectToHost(ulong steamIdRaw)
        {
            ThreadUtil.assertIsGameThread();
            P2PApprovalWaitController.CancelForExplicitUserJoin();
            return TryConnectToHostCore(steamIdRaw, P2PConnectOrigin.ExplicitUserAction);
        }

        internal static bool TryConnectToHostFromApprovalWait(ulong steamIdRaw)
        {
            ThreadUtil.assertIsGameThread();
            return TryConnectToHostCore(steamIdRaw, P2PConnectOrigin.ApprovalWaitRetry);
        }

        private static bool TryConnectToHostCore(ulong steamIdRaw, P2PConnectOrigin origin)
        {
            if (!SteamP2PFriendsPlugin.DiagnosticBuildValid)
            {
                RoleLogger.Error(DynamicRole(),
                    "!!! TryConnectToHostCore 拒绝执行：DiagnosticBuildValid=false（P0-C4 硬门控）!!!");
                try { SafeAlert("SteamP2PFriends 自检未通过，客机连接被拒绝。请查看日志。"); } catch { }
                return false;
            }

            if (_state == EJoinState.TeardownFailed)
            {
                RoleLogger.Error(DynamicRole(),
                    $"[P0-9] 状态=TeardownFailed，禁止重连。Disconnecting 超时且静态状态未清空，需手动重启游戏。");
                SafeAlert("断开超时状态未清空，已冻结重连。请重启游戏。");
                return false;
            }

            if (_state == EJoinState.Connecting || _state == EJoinState.ServerAccepted ||
                _state == EJoinState.LocalPlayerCreated || _state == EJoinState.Disconnecting)
            {
                RoleLogger.Warn(DynamicRole(), $"已有连接进行中（state={_state}），忽略重复请求。需先 disconnect 再重连。");
                return false;
            }

            if (Provider.isConnected)
            {
                RoleLogger.Info(DynamicRole(), "检测到残留会话，进入 Disconnecting 状态。");
                SetState(EJoinState.Disconnecting, "residual-session");
                _disconnectStartTime = Time.realtimeSinceStartup;
                _lastStage = "Disconnecting(residual)";
                NativeLoadingGateDumper.StopPostAcceptedTracking();
                NativeLoadingGateDumper.Dump("P2PJoinManager.TryConnectToHost(residual-disconnect)");
                DisconnectTracer.TraceClientInitiated("P2PJoinManager.TryConnectToHost(residual)");
                Provider.disconnect();
                return false;
            }

            if (steamIdRaw == 0)
            {
                RoleLogger.Warn(DynamicRole(), "SteamID 为 0，无法连接。");
                SafeAlert("请输入好友的 Steam ID。");
                return false;
            }

            if (steamIdRaw == SteamUser.GetSteamID().m_SteamID)
            {
                RoleLogger.Warn(DynamicRole(), "不能加入自己的房间。");
                SafeAlert("不能加入自己的房间。");
                return false;
            }

            RoleLogger.Info(DynamicRole(), "[Shared] 角色切换为客机");

            _targetSteamId = steamIdRaw;
            _attempt = 0;
            _lastFailureInfo = ESteamConnectionFailureInfo.NONE;
            SetState(EJoinState.Connecting, "join-request");
            _connectStartTime = Time.realtimeSinceStartup;
            _lastStage = "Connecting";
            _postAcceptedWatchdogFired = false;
            _acceptedAndLocalComponentsTime = 0f;
            NativeLoadingGateDumper.StopPostAcceptedTracking();

            NativeLoadingGateDumper.Dump("P2PJoinManager.TryConnectToHost(pre-connect)");

            return DoConnect();
        }

        /// <summary>
        /// 手动断开连接，进入 Disconnecting 状态。
        /// </summary>
        public static void RequestDisconnect()
        {
            if (_state == EJoinState.Idle || _state == EJoinState.TeardownComplete) return;

            RoleLogger.Info(DynamicRole(),
                $"[P0-3] 请求断开连接，进入 Disconnecting 状态（from {_state}）。");
            SetState(EJoinState.Disconnecting, "manual-disconnect");
            _disconnectStartTime = Time.realtimeSinceStartup;
            _lastStage = "Disconnecting(manual)";

            NativeLoadingGateDumper.Dump("P2PJoinManager.RequestDisconnect(manual)");
            DisconnectTracer.TraceClientInitiated("P2PJoinManager.RequestDisconnect(manual)");

            if (Provider.isConnected)
            {
                Provider.disconnect();
            }
            else
            {
                CheckTeardownComplete();
            }
        }

        public static void Tick()
        {
            float now = Time.realtimeSinceStartup;

            if (_state == EJoinState.Connecting)
            {
                HandleConnectingTick(now);
                return;
            }

            if (_state == EJoinState.ServerAccepted)
            {
                HandleServerAcceptedTick(now);
                return;
            }

            if (_state == EJoinState.LocalPlayerCreated)
            {
                HandleLocalPlayerCreatedTick(now);
                return;
            }

            if (_state == EJoinState.Disconnecting)
            {
                HandleDisconnectingTick(now);
                return;
            }
        }

        private static void HandleConnectingTick(float now)
        {
            if (now - _connectStartTime > TimeoutSeconds)
            {
                SetState(EJoinState.Timeout, "connect-watchdog");
                RoleLogger.Error(DynamicRole(),
                    $"!!! 连接超时 !!! target={_targetSteamId} elapsed={now - _connectStartTime:F1}s " +
                    $"(lastFailure={_lastFailureInfo}) lastStage={_lastStage}");
                SafeAlert($"连接超时（{TimeoutSeconds}s）。请检查日志并手动重连。({_lastFailureInfo})");
                LogFailureDiagnosticHint(_lastFailureInfo);
                return;
            }
        }

        /// <summary>
        /// ServerAccepted 阶段 Tick。
        /// </summary>
        private static void HandleServerAcceptedTick(float now)
        {
            Player localPlayer = Player.LocalPlayer;
            if (!ReferenceEquals(localPlayer, null))
            {
                SetState(EJoinState.LocalPlayerCreated, "local-player-created");
                _lastStage = "LocalPlayerCreated";
                RoleLogger.Info(DynamicRole(),
                    $"[P1-G] 状态推进: ServerAccepted -> LocalPlayerCreated " +
                    $"(本地 Player 已创建, instance={localPlayer.GetInstanceID()})");
                NativeLoadingGateDumper.Dump("HandleServerAcceptedTick(LocalPlayerCreated)");
            }

            if (!_postAcceptedWatchdogFired &&
                now - _serverAcceptedTime > PostAcceptedWatchdogSeconds)
            {
                _postAcceptedWatchdogFired = true;
                RoleLogger.Error(DynamicRole(),
                    $"[P1-G] !!! ServerAccepted watchdog 超时（仅报警，不断线）!!! " +
                    $"elapsed={now - _serverAcceptedTime:F1}s/{PostAcceptedWatchdogSeconds}s " +
                    $"state={_state} lastStage={_lastStage} target={_targetSteamId}");
                RoleLogger.Error(DynamicRole(),
                    $"[P1-G] watchdog 诊断: Player.LocalPlayer_null={Player.LocalPlayer == null} " +
                    $"isConnected={Provider.isConnected} isServer={Provider.isServer} " +
                    $"connectionFailureInfo={Provider.connectionFailureInfo}");
                NativeLoadingGateDumper.Dump("HandleServerAcceptedTick(watchdog-fired)");
                SetState(EJoinState.Timeout, "server-accepted-watchdog");
                return;
            }

            // 周期性诊断日志（每 5s）
            if (now - _serverAcceptedTime > 5f &&
                Mathf.FloorToInt((now - _serverAcceptedTime) / 5f) >
                Mathf.FloorToInt((now - _serverAcceptedTime - Time.deltaTime) / 5f))
            {
                RoleLogger.Info(DynamicRole(),
                    $"[P1-G] ServerAccepted 等待中: elapsed={now - _serverAcceptedTime:F1}s " +
                    $"state={_state} Player.LocalPlayer_null={Player.LocalPlayer == null} " +
                    $"isConnected={Provider.isConnected}");
                NativeLoadingGateDumper.Dump("HandleServerAcceptedTick(periodic-5s)");
            }
        }

        /// <summary>
        ///
        /// Accepted + LocalPlayer + mask=0xFF + 原生 LoadingUI/五类 loading flags 全部解除后，
        /// 进入 Connected/Operational 终态，停止 watchdog。
        /// 若 30s 内条件未满足，才输出超时；不能在条件已经满足后继续计时。
        ///
        /// 不得命名为或伪造 vanilla GameplayReady（审计 §8）。
        /// </summary>
        private static void HandleLocalPlayerCreatedTick(float now)
        {
            Player localPlayer = Player.LocalPlayer;
            bool localPlayerExists = !ReferenceEquals(localPlayer, null);
            bool componentsReady = localPlayerExists && GameplayReadyTracker.IsLocalComponentsInitialized(localPlayer);
            bool loadingFlagsCleared = CheckAllLoadingFlagsCleared();

            if (componentsReady && loadingFlagsCleared)
            {
                SetState(EJoinState.Connected, "operational");
                _lastStage = "Connected(Operational)";
                _postAcceptedWatchdogFired = true; // 停止 watchdog
                RoleLogger.Info(DynamicRole(),
                    $"[P1-S4] 状态推进: LocalPlayerCreated -> Connected (Operational) " +
                    $"Accepted + LocalPlayer + mask=0xFF + loading flags 全部解除 " +
                    $"elapsed={now - _serverAcceptedTime:F1}s/{PostAcceptedWatchdogSeconds}s " +
                    $"[注意: 此为插件状态机终态，不等于 vanilla GameplayReady 宣告]");
                NativeLoadingGateDumper.Dump("HandleLocalPlayerCreatedTick(Connected-Operational)");
                return;
            }

            // 记录 AcceptedAndLocalComponentsInitialized 阶段（中间信号，不推进状态）
            if (componentsReady && _acceptedAndLocalComponentsTime == 0f)
            {
                _acceptedAndLocalComponentsTime = now;
                _lastStage = "AcceptedAndLocalComponentsInitialized";
                RoleLogger.Info(DynamicRole(),
                    $"[P1-G] 阶段达成: AcceptedAndLocalComponentsInitialized " +
                    $"(Accepted + 8 组件 InitializePlayer 完成, mask=0x{GameplayReadyTracker.GetMask(localPlayer):X2}/0xFF) " +
                    $"[等待 loading flags 解除后进入 Connected]");
                NativeLoadingGateDumper.Dump("HandleLocalPlayerCreatedTick(AcceptedAndLocalComponentsInitialized)");
            }

            if (!_postAcceptedWatchdogFired &&
                now - _serverAcceptedTime > PostAcceptedWatchdogSeconds)
            {
                _postAcceptedWatchdogFired = true;
                RoleLogger.Error(DynamicRole(),
                    $"[P1-G] !!! LocalPlayerCreated watchdog 超时（仅报警，不断线）!!! " +
                    $"elapsed={now - _serverAcceptedTime:F1}s/{PostAcceptedWatchdogSeconds}s " +
                    $"lastStage={_lastStage} target={_targetSteamId} " +
                    $"componentsReady={componentsReady} loadingFlagsCleared={loadingFlagsCleared}");
                RoleLogger.Error(DynamicRole(),
                    $"[P1-G] watchdog 诊断: Player.LocalPlayer_null={!localPlayerExists} " +
                    $"isConnected={Provider.isConnected} connectionFailureInfo={Provider.connectionFailureInfo} " +
                    $"bitmask=0x{(localPlayerExists ? GameplayReadyTracker.GetMask(localPlayer) : 0):X2} " +
                    $"localComponentsInit={_localComponentsInitializedSignaled}");
                NativeLoadingGateDumper.Dump("HandleLocalPlayerCreatedTick(watchdog-fired)");
                SetState(EJoinState.Timeout, "local-player-watchdog");
                return;
            }

            // 周期性日志（每 5s）输出 loading gate 状态
            if (now - _serverAcceptedTime > 5f &&
                Mathf.FloorToInt((now - _serverAcceptedTime) / 5f) >
                Mathf.FloorToInt((now - _serverAcceptedTime - Time.deltaTime) / 5f))
            {
                RoleLogger.Info(DynamicRole(),
                    $"[P1-G] LocalPlayerCreated 等待中: elapsed={now - _serverAcceptedTime:F1}s " +
                    $"mask=0x{(localPlayerExists ? GameplayReadyTracker.GetMask(localPlayer) : 0):X2}/0xFF " +
                    $"componentsReady={componentsReady} loadingFlagsCleared={loadingFlagsCleared} " +
                    $"acceptedAndLocal={(_acceptedAndLocalComponentsTime > 0f)}");
                NativeLoadingGateDumper.Dump("HandleLocalPlayerCreatedTick(periodic-5s)");
            }
        }

        /// <summary>
        /// 用于 Connected/Operational 终态判定。
        /// </summary>
        private static bool CheckAllLoadingFlagsCleared()
        {
            try
            {
                if (Assets.isLoading) return false;
                if (Provider.isLoading) return false;
                if (Level.isLoading) return false;
                if (Player.isLoading) return false;
                if (Player.isLoadingInventory) return false;
                if (Player.isLoadingLife) return false;
                if (Player.isLoadingClothing) return false;
                if (LoadingUI.isBlocked) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// </summary>
        internal static void NotifyLocalComponentsInitialized()
        {
            _localComponentsInitializedSignaled = true;
            // 仅记录信号本身，待 Accepted Postfix 与本信号均已满足后才记录组合阶段
            bool acceptedSeen = _state == EJoinState.Connected || _serverAcceptedTime > 0f;
            string phase = acceptedSeen
                ? "AcceptedAndLocalComponentsInitialized 阶段达成"
                : "LocalComponentsInitialized signaled; waiting/observing Accepted";
            RoleLogger.Info(DynamicRole(),
                $"[P1-G] {phase}（真实 GameplayReady 由原生 loading flag 决定，不由插件宣告）");
            NativeLoadingGateDumper.Dump("NotifyLocalComponentsInitialized(bitmask-0xFF)");
        }

        /// <summary>
        /// </summary>
        internal static void DumpLoadingGate(string reason)
        {
            NativeLoadingGateDumper.Dump(reason);
        }

        /// <summary>
        /// </summary>
        private static void HandleDisconnectingTick(float now)
        {
            CheckTeardownComplete();

            if (_state == EJoinState.Disconnecting &&
                now - _disconnectStartTime > DisconnectTimeoutSeconds)
            {
                bool isConnected = Provider.isConnected;
                // vanilla Player._localPlayer 在 OnDestroy/disconnect 中均不清空（Player.cs:1725-1793, Provider.cs:2597-2693），
                // ReferenceEquals 绕过 == 重载，对已 Destroy 的 Unity Object 仍返回非 null，导致 TeardownFailed 误触发。
                bool hasLocalPlayer = Player.LocalPlayer != null;

                if (!isConnected && !hasLocalPlayer)
                {
                    SetState(EJoinState.TeardownComplete, "disconnect-timeout-state-clean");
                    _lastStage = "TeardownComplete(late)";
                    RoleLogger.Info(DynamicRole(),
                        $"[P0-9] Disconnecting 超时但静态状态已清空，推进 TeardownComplete。");
                }
                else
                {
                    SetState(EJoinState.TeardownFailed, "disconnect-timeout-state-not-clean");
                    _lastStage = "TeardownFailed(timeout, state not clean)";
                    RoleLogger.Error(DynamicRole(),
                        $"!!! Disconnecting 超时且静态状态未清空，标记 TeardownFailed 禁止重连 !!! " +
                        $"isConnected={isConnected} Player.LocalPlayer_exists={hasLocalPlayer} " +
                        $"请手动重启游戏。");
                    SafeAlert("断开超时且状态未清空，已冻结重连。请重启游戏。");
                }
            }
        }

        private static void CheckTeardownComplete()
        {
            if (_state != EJoinState.Disconnecting) return;

            if (!Provider.isConnected && Player.LocalPlayer == null)
            {
                SetState(EJoinState.TeardownComplete, "disconnect-complete");
                _lastStage = "TeardownComplete";
                RoleLogger.Info(DynamicRole(),
                    "[P0-3] 状态推进: Disconnecting -> TeardownComplete " +
                    "(Provider.disconnect 完成, Player.LocalPlayer 已清空)");
            }
        }

        internal static bool TryConnectFromLobby(ulong steamIdRaw)
        {
            //   虽然内部会调 TryConnectToHost（已有门控），但 lobby 入口应在调用前就拒绝，
            //   避免在 DiagnosticBuildValid=false 时仍记录 "LobbyGameCreated 自动连接" 误导日志
            if (!SteamP2PFriendsPlugin.DiagnosticBuildValid)
            {
                RoleLogger.Error(DynamicRole(),
                    "!!! TryConnectFromLobby 拒绝执行：DiagnosticBuildValid=false（P0-C4 硬门控）!!!");
                return false;
            }

            if (_state == EJoinState.Connecting) return false;
            if (steamIdRaw == 0) return false;

            RoleLogger.Info(DynamicRole(), $"[P2P-Lobby] LobbyGameCreated 自动连接: hostSteamId={steamIdRaw}");
            return TryConnectToHost(steamIdRaw);
        }

        /// <summary>
        /// D-7 Accepted.ReadMessage Postfix 仅诊断，不推进状态。
        /// </summary>
        internal static void NotifyServerAccepted()
        {
            RoleLogger.Info(DynamicRole(),
                $"[P1-2] D-7 Accepted.ReadMessage Postfix 触发（仅诊断）。state={_state} " +
                $"isConnected={Provider.isConnected}");
            NativeLoadingGateDumper.Dump("NotifyServerAccepted(Accepted-Postfix)");
        }

        /// <summary>
        /// </summary>
        internal static void NotifyQueuePositionChanged(byte newPosition)
        {
            RoleLogger.Info(DynamicRole(),
                $"[Diag] QueuePositionChanged 触发 position={newPosition} state={_state} " +
                $"isConnected={Provider.isConnected}");
            NativeLoadingGateDumper.Dump($"NotifyQueuePositionChanged(pos={newPosition})");
        }

        private static bool DoConnect()
        {
            try
            {
                RoleLogger.Info(DynamicRole(),
                    $"连接发起: target={_targetSteamId} attempt={_attempt + 1}/{MaxAttempts}");
                RoleLogger.Info(DynamicRole(),
                    $"[Diag] 本地 SteamUser ID={SteamUser.GetSteamID().m_SteamID} " +
                    $"targetSteamId={_targetSteamId} identityCheck={(SteamUser.GetSteamID().m_SteamID == _targetSteamId ? "SAME(self)" : "OK(remote)")}");

                try
                {
                    SteamP2PFriends.Client.FriendStatusObserver.RecordBeforeConnect(_targetSteamId);
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn(DynamicRole(), $"[Diag] FriendStatusObserver.RecordBeforeConnect 异常（不阻断）: {ex.Message}");
                }

                try
                {
                    SteamP2PFriends.Shared.SnsDiagnosticUtil.SnapshotRelayAuthReadiness(DynamicRole(), "ConnectP2P-pre");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn(DynamicRole(), $"[Diag] SnsDiagnosticUtil.SnapshotRelayAuthReadiness 异常（不阻断）: {ex.Message}");
                }

                MenuUI.closeAll();

                CSteamID hostSteamId = new CSteamID(_targetSteamId);
                ServerConnectParameters parameters = new ServerConnectParameters(hostSteamId, string.Empty);
                RoleLogger.Info(DynamicRole(),
                    $"[Diag] ServerConnectParameters constructed: hostSteamId={hostSteamId.m_SteamID} " +
                    $"passwordEmpty={string.IsNullOrEmpty(string.Empty)}");
                P2PConnectionJournal.ClientConnectCalling(_targetSteamId, _attempt + 1);
                Provider.connect(parameters, null, null);
                P2PConnectionJournal.ClientConnectCallReturned(_targetSteamId);
                _connectStartTime = Time.realtimeSinceStartup;
                _lastStage = "Provider.connect called";
                RoleLogger.Info(DynamicRole(),
                    $"[Diag] Provider.connect() 已调用，等待 onClientConnected/onClientDisconnected 回调（超时 {TimeoutSeconds}s）");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error(DynamicRole(), $"DoConnect 失败: {ex}");
                SetState(EJoinState.Failed, "provider-connect-threw");
                return false;
            }
        }

        private static void OnClientConnected()
        {
            ThreadUtil.assertIsGameThread();
            P2PApprovalWaitController.NotifyConnectionAccepted();

            RoleLogger.Info(DynamicRole(),
                $"[Diag] onClientConnected 触发 state={_state} target={_targetSteamId} " +
                $"isConnected={Provider.isConnected} isServer={Provider.isServer}");

            if (_state == EJoinState.Connecting)
            {
                SetState(EJoinState.ServerAccepted, "on-client-connected");
                _serverAcceptedTime = Time.realtimeSinceStartup;
                _lastStage = "ServerAccepted(via onClientConnected)";
                RoleLogger.Info(DynamicRole(),
                    "[P1-G] 状态推进: Connecting -> ServerAccepted (onClientConnected 权威信号)");
                NativeLoadingGateDumper.Dump("OnClientConnected(ServerAccepted)");
            }
        }

        private static void OnClientDisconnected()
        {
            _lastFailureInfo = Provider.connectionFailureInfo;

            RoleLogger.Info(DynamicRole(),
                $"[Diag] onClientDisconnected 触发 state={_state} " +
                $"failureInfo={_lastFailureInfo}({(int)_lastFailureInfo}) isConnected={Provider.isConnected}");
            NativeLoadingGateDumper.StopPostAcceptedTracking();
            NativeLoadingGateDumper.Dump("OnClientDisconnected");

            if (_state == EJoinState.Disconnecting)
            {
                CheckTeardownComplete();
                return;
            }

            if (_state == EJoinState.Idle || _state == EJoinState.TeardownComplete ||
                _state == EJoinState.Failed || _state == EJoinState.Timeout)
            {
                return;
            }

            if (_lastFailureInfo == ESteamConnectionFailureInfo.NONE)
            {
                RoleLogger.Info(DynamicRole(), $"[Client] 断开（NONE，非错误）lastFailure={_lastFailureInfo}");

            if (!Provider.isConnected && Player.LocalPlayer == null)
                {
                    SetState(EJoinState.TeardownComplete, "disconnect-none-state-clean");
                    _lastStage = "TeardownComplete(disconnect no error, state clean)";
                }
                else
                {
                    SetState(EJoinState.Disconnecting, "disconnect-none-wait-cleanup");
                    _disconnectStartTime = Time.realtimeSinceStartup;
                    _lastStage = "Disconnecting(none disconnect, state not clean)";
                    RoleLogger.Info(DynamicRole(),
                        $"[P0-9] NONE 断开但静态状态未清空，进入 Disconnecting 等待清空。");
                }
                return;
            }

            SetState(EJoinState.Failed, "client-disconnected-with-failure");
            RoleLogger.Error(DynamicRole(),
                $"!!! 连接失败 !!! target={_targetSteamId} state={_state} info={_lastFailureInfo}");

            // Legacy WHITELISTED auto-retry is intentionally disabled to prevent Steam rate limiting.
            HandleDisconnectFailureRouting(_lastFailureInfo, _targetSteamId);
        }

        internal static void HandleDisconnectFailureRouting(ESteamConnectionFailureInfo failureInfo, ulong targetSteamId)
        {
            SafeAlert($"连接失败：{failureInfo}");
            if (!_testBypassFailurePresentationRuntime) LogFailureDiagnosticHint(failureInfo);
        }

        private static void LogFailureDiagnosticHint(ESteamConnectionFailureInfo info)
        {
            switch (info)
            {
                case ESteamConnectionFailureInfo.AUTH_VERIFICATION:
                    RoleLogger.Warn(DynamicRole(),
                        "[Diag-Hint] AUTH_VERIFICATION: 票据身份错配。检查房主是否已启用 offlineOnly（F-7 修复）。");
                    break;
                case ESteamConnectionFailureInfo.AUTH_NETWORK_IDENTITY_FAILURE:
                    RoleLogger.Warn(DynamicRole(),
                        "[Diag-Hint] AUTH_NETWORK_IDENTITY_FAILURE: 网络身份不匹配。检查 F-1 重定向是否生效。");
                    break;
                case ESteamConnectionFailureInfo.AUTH_TIMED_OUT:
                case ESteamConnectionFailureInfo.TIMED_OUT:
                case ESteamConnectionFailureInfo.TIMED_OUT_LOGIN:
                    RoleLogger.Warn(DynamicRole(),
                        "[Diag-Hint] 超时类失败: P2P 路由未建立。检查双方 Steam 好友状态、NAT 类型。");
                    break;
                default:
                    RoleLogger.Info(DynamicRole(),
                        $"[Diag-Hint] {info}({(int)info}): 通用失败类型。");
                    break;
            }
        }

        internal static int _testSafeAlertCount;
        // 仅控制台单元测试使用：隔离 Unity/Steam ECall，不绕过生产失败路由判断。
        internal static bool _testBypassFailurePresentationRuntime;

        private static void SafeAlert(string message)
        {
            _testSafeAlertCount++;
            if (_testBypassFailurePresentationRuntime) return;
            SafeAlertRuntime(message);
        }

        // Unity MenuUI property is an ECall. Keep it out of the testable router so the
        // CLR test runner never JIT-compiles a client-only UI access.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SafeAlertRuntime(string message)
        {
            // - 若 MenuUI.window 未就绪，仅记录警告并跳过弹窗（不缓存报警，不阻断）
            // - 不在 alert 失败时触发 disconnect
            // - 附带报警时间，以便区分 30s watchdog 与之后的人工取消
            try
            {
                if (MenuUI.window == null)
                {
                    RoleLogger.Warn(DynamicRole(),
                        $"[SafeAlert] MenuUI.window 未就绪，跳过弹窗并记录报警（不阻断）: " +
                        $"t={Time.realtimeSinceStartup:F1}s msg={message}");
                    return;
                }
                MenuUI.alert(message);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn(DynamicRole(),
                    $"[SafeAlert] MenuUI.alert 异常（不阻断，不 disconnect）: " +
                    $"t={Time.realtimeSinceStartup:F1}s msg={message} ex={ex.Message}");
            }
        }

        /// <summary>
        /// 不再使用调用方传入的硬编码前缀，改用 Provider/HostManager 动态事实推断。
        /// </summary>
        private static string DynamicRole()
        {
            // 客机连接器上下文：仅在客机端运行
            // HostManager.IsP2PHostMode=true 时为房主，否则为客机
            try
            {
                if (HostManager.IsP2PHostMode)
                {
                    return "[Host]";
                }
            }
            catch
            {
                // HostManager 静态构造可能在早期阶段失败
            }
            return "[Client]";
        }

        /// <summary>
        /// </summary>
        internal static void Reset()
        {
            if (_state != EJoinState.TeardownComplete && _state != EJoinState.Failed &&
                _state != EJoinState.Timeout && _state != EJoinState.Idle)
            {
                RoleLogger.Warn(DynamicRole(),
                    $"[P0-3] Reset 被拒绝：当前 state={_state}，必须先到 TeardownComplete。");
                return;
            }

            SetState(EJoinState.Idle, "reset");
            _targetSteamId = 0;
            _attempt = 0;
            _lastFailureInfo = ESteamConnectionFailureInfo.NONE;
            _serverAcceptedTime = 0f;
            _lastStage = null;
            _localComponentsInitializedSignaled = false;
            _postAcceptedWatchdogFired = false;
            _acceptedAndLocalComponentsTime = 0f;
            NativeLoadingGateDumper.StopPostAcceptedTracking();
        }

        private static void SetState(EJoinState next, string cause)
        {
            EJoinState previous = _state;
            _state = next;
            P2PConnectionJournal.ClientStateChanged(_targetSteamId, previous, next, cause, _lastFailureInfo);
        }
    }
}
