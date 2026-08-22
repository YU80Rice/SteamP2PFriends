using HarmonyLib;
using SDG.Provider.Services.Multiplayer.Server;
using SDG.Unturned;
using SteamP2PFriends.Patches;
using SteamP2PFriends.Shared;
using SteamP2PFriends.Shared.Enums;
using SteamP2PFriends.UI;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SteamP2PFriends.Host
{
    /// <summary>
    /// SteamUser P2P listen-host 房主协调器。
    /// 启动和退出均采用会话化状态；任何关键初始化失败均经 AbortHostStart 回滚，禁止部分启动。
    /// </summary>
    public static class HostManager
    {
        public const ushort DefaultLanQueryPort = 27015;

        private static bool _isStarting;
        private static EHostMode _hostMode = EHostMode.None;
        private static P2PRoomRules _activeRoomRules;

        public static bool IsP2PServerActive { get; private set; }
        public static bool SteamReadyHandled { get; private set; }
        internal static EHostMode HostMode => _hostMode;
        internal static bool IsStarting => _isStarting;

        /// <summary>
        /// 仅当 _hostMode == P2P 且 IsP2PServerActive 时返回 true。
        /// </summary>
        public static bool IsP2PHostMode => _hostMode == EHostMode.P2P && IsP2PServerActive;

        private static bool _savedOfflineOnly;
        private static bool _restoredOfflineOnly;
        private static bool _offlineAuthEnabled;

        // listen 循环反射方法缓存
        private static MethodInfo _listenServerMethod;
        private static MethodInfo _releaseAllMethod;
        private static bool _listenMethodsResolved;
        private static bool _listenMethodsFailed;
        private static float _lastQueueNotifyTime;
        private static float _lastListenHeartbeatTime;
        private static float _lastListenErrorLogTime;
        private static int _listenTickCount;
        private static bool _loggedListenActive;
        private static bool _listenBusy;

        // 审计员要求：必须同时检测 LoadingUI.isBlocked + Time.timeScale==0 + MenuUI 状态
        private static bool _lastEscPaused;
        private static float _escPausedSince;
        private static float _lastEscPausedHeartbeatTime;
        private static bool _escPauseDetectorEnabled;

        private static bool _subscribedServerHosted;

        private static Guid _stage6BStartToken = Guid.Empty;

        /// <summary>
        /// 启动 P2P Listen Server（由 MenuPlaySingleplayerUIPatch 调用）。
        /// </summary>
        public static void StartP2PServer(string mapName, string serverName, byte maxPlayers, EGameMode mode,
            bool cheats, P2PRoomRules roomRules = null)
        {
            if (!RequireGameThread(nameof(StartP2PServer))) return;

            // The final host entry must require both the diagnostic build and the completed
            // Route B lifecycle registration, before any session state can be mutated.
            if (!SteamP2PFriendsPlugin.IsP2PEntryReady)
            {
                RoleLogger.Error("[Host]",
                    "!!! StartP2PServer 拒绝执行：P2P entry is not ready（硬门控）!!!");
                try { MenuUI.alert("SteamP2PFriends 尚未完成联机初始化，P2P 服务器启动被拒绝。请查看日志。"); } catch { }
                return;
            }

            if (!P2PWorldStatusBroadcaster.IsReadyForHostStart)
            {
                RoleLogger.Warn("[Host]",
                    "StartP2PServer rejected: world broadcast activation not ready (state=" +
                    P2PWorldStatusBroadcaster.ActivationState + ")");
                try { MenuUI.alert("SteamP2PFriends 正在完成初始化，请稍后重试创建房间。"); } catch { }
                return;
            }

            if (_isStarting)
            {
                RoleLogger.Warn("[Host]", "P2P 服务器正在启动中，忽略重复请求。");
                return;
            }

            //   入口守门必须在 ResetHostSession() 之前执行，否则 ResetHostSession 的 finally
            //   守门条件：活动会话存在 / P2P 已激活 / Provider 仍处于 server 状态 -> 拒绝并返回。
            if (IsP2PServerActive || Stage6ASessionContext.IsActive || Provider.isServer)
            {
                RoleLogger.Error("[Host]",
                    "[Stage6A] StartP2PServer rejected: previous host session is still active " +
                    $"(IsP2PServerActive={IsP2PServerActive}, IsActive={Stage6ASessionContext.IsActive}, " +
                    $"isServer={Provider.isServer})");
                try { MenuUI.alert("当前联机会话尚未结束，无法启动新的房间。"); } catch { }
                return;
            }

            try
            {
                RoleLogger.Info("[Host]", $"StartP2PServer: map={mapName} name={serverName} maxPlayers={maxPlayers} mode={mode} cheats={cheats}");
                RoleLogger.Info("[Host]", "[Shared] 角色切换为房主");

                _isStarting = true;
                SteamReadyHandled = false;

                //   开新服前清除 RegionSync 计数 + RenderProbe 状态，避免上一局会话残留计数
                //   导致新会话同 SteamID 玩家的诊断日志被静默。
                try
                {
                    Patches.BarricadeManagerRegionSyncPatch.ResetAll();
                    Patches.StructureManagerRegionSyncPatch.ResetAll();
                    Patches.ItemManagerRegionSyncPatch.ResetAll();
                    Patches.ResourceManagerRegionSyncPatch.ResetAll();
                    Patches.ObjectManagerRegionSyncPatch.ResetAll();
                    Patches.LevelObjectRemoteCollisionPatch.ResetAll();
                    RemotePlayerRenderProbe.ResetAll();
                    Patches.WorldSyncDiagnosticCore.ResetAll();
                    Patches.P2PListenHostCommandPermissionPatch.ResetForSession();
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Host]", $"StartP2PServer ResetAll 失败: {ex.Message}");
                }                _hostMode = EHostMode.P2P;

                LevelInfo level = Level.getLevel(mapName);
                if (level == null)
                {
                    throw new InvalidOperationException($"Level not found: {mapName}");
                }

                ResetHostSession();
                _activeRoomRules = roomRules;

                // B 方 ConfigureCommonServerSettings；测试版固定 SteamUser P2P-only
                ConfigureCommonServerSettings(level, mode, maxPlayers, cheats);
                // 依据：U3-SDK Provider.cs:2054 singleplayer() 设 Dedicator.serverID = "Singleplayer_" + Characters.selected
                Stage6ASessionContext.BeginSession(EHostMode.P2P, Characters.selected);
                Provider.serverID = "Singleplayer_" + Stage6ASessionContext.CachedSlot;
                DetectLegacyP2PSaveDirectory();
                Provider.serverName = string.IsNullOrEmpty(serverName)
                    ? SteamFriends.GetPersonaName() + " 的好友房间"
                    : serverName;
                Provider.ip = 0;
                Provider.bindAddress = null;
                Provider.port = DefaultLanQueryPort;

                // B 方 PrepareClientHostSession（ConfigData.CreateDefault + LoadGameplayConfig）
                PrepareClientHostSession();

                string stage6BFailure;
                if (!TryPrepareStage6BForP2PStart(level, out stage6BFailure))
                    throw new InvalidOperationException(stage6BFailure);

                // 原因：客机票据锁定房主个人 SteamID（ClientMessageHandler_Verify.cs:17-20），
                // 但主机校验在匿名 GameServer identity（Provider.cs:5300）-> 身份错配 -> AUTH_VERIFICATION 拒绝。
                // offlineOnly=true 让 vanilla 跳过票据校验（ServerMessageHandler_Authenticate.cs:38-47）。
                EnableListenHostOfflineAuth();

                // 原因：
                //   1. offlineOnly=true 已跳过票据校验，GSLT 不再解决任何认证问题
                //   2. GSLT token 会触发 serverVisibility=Internet，导致 SteamGameServer 尝试注册公网 SDR 路由
                //      但 listen server 模式 appInfo.isDedicated=false -> SDR 路由不可用（FACT.md 铁律）
                //   4. GS-ID "Server Code" 在 F-8 已弃用，Rich Presence/Lobby/剪贴板统一用 SteamUser ID
                // 因此 P2P 模式强制 LAN 可见性 + 清空 GSLT token + 禁用 FakeIP，避免任何 GS-identity 路径。
                ConfigureP2POnlyBranch();

                EnsurePreviousGameServerClosed();

                MenuUI.closeAll();
                LoadingUI.SetLoadingText("Loading_MainMenu");

                Provider.onEnemyConnected += OnPlayerConnectedToServer;
                RoleLogger.Info("[Host]", "已订阅 Provider.onEnemyConnected");

                //   蓝图强制时序：所有既有前置成功后、紧接 StartHostingCore() 前。
                //   bootstrap 失败抛 InvalidOperationException -> 外层 catch -> AbortHostStart 收敛。
                //   bootstrap 失败不调 Provider.disconnect()（设计 §4.3）。
                P2PWhitelistService.ResetForP2PStart();
                string whitelistFailure;
                if (!P2PWhitelistService.TryBootstrap(Provider.user, out whitelistFailure))
                    throw new InvalidOperationException("P2P whitelist bootstrap failed: " + whitelistFailure);

                try { P2PApprovalManager.ResetForSession(); }
                catch (Exception apEx) { RoleLogger.Warn("[Host]", "[P2P-Approval] Route B reset failed (non-blocking): " + apEx); }
                try { SteamPersonaDisplay.ResetForSession(); }
                catch (Exception nameEx) { RoleLogger.Warn("[Host]", "[P2P-Persona] ResetForSession 异常（不阻断）: " + nameEx.GetType().Name); }
                //   不得重复订阅死亡事件；ResetForSession 只清状态。
                try { P2PWorldStatusBroadcaster.ResetForSession(); }
                catch (Exception wbEx) { RoleLogger.Warn("[Host]", "[WorldBroadcast] ResetForSession (Start) 异常（不阻断）: " + wbEx.GetType().Name); }

                StartHostingCore();

                if (!_isStarting) return;  // AbortHostStart 已回滚

                // host() 完成后再设 _isClient=true（listen server 双 true 要求）
                EnsureListenServerClientFlag();
                OverrideServerToHostUser();

                SteamGameServerCallbacksWatcher.StartWatching();
                HasCheatsGuardWatcher.StartGuard(cheats);

                EnsureCommanderInitialized();

                IServerMultiplayerService serverMp = Provider.provider?.multiplayerService?.serverMultiplayerService;
                if (serverMp != null)
                {
                    if (SteamRuntime.IsGameServerAlive())
                    {
                        RoleLogger.Info("[Host]", "检测到 Steam 后端已就绪，安全执行自连与地图解锁…");
                        OnSteamServerReady();
                    }
                    else
                    {
                        RoleLogger.Info("[Host]", "等待 Steam 后端首次 ready 回调…");
                        serverMp.ready += OnSteamServerReady;
                    }
                }

                IsP2PServerActive = true;
                Stage6ASessionContext.MarkStartSucceeded();
                Stage6ASaveRoundtripObserver.Begin(
                    Stage6ASessionContext.SessionId,
                    Stage6ASessionContext.CachedSlot);
                LogStage6ASessionStart();
                RoleLogger.Info("[Host]", "Provider.host() 已调用，Listen Server 标志已修正，等待 onServerHosted 回调。");
            }
            catch (Exception ex)
            {
                RoleLogger.Error("[Host]", $"StartP2PServer 失败: {ex}");
                AbortHostStart("创建房间失败，请查看日志。");
            }
        }

        /// <summary>
        /// 启动 LAN 测试服务器（同账号双开场景）。
        /// </summary>
        public static void StartLanServer(string mapName, EGameMode mode, byte maxPlayers, bool cheats, ushort queryPort)
        {
            if (!RequireGameThread(nameof(StartLanServer))) return;

            if (!SteamP2PFriendsPlugin.DiagnosticBuildValid)
            {
                RoleLogger.Error("[Host]",
                    "!!! StartLanServer 拒绝执行：DiagnosticBuildValid=false（P0-C4 硬门控）!!!");
                try { MenuUI.alert("SteamP2PFriends 自检未通过，LAN 服务器启动被拒绝。请查看日志。"); } catch { }
                return;
            }

            if (!P2PWorldStatusBroadcaster.IsReadyForHostStart)
            {
                RoleLogger.Warn("[Host]",
                    "StartLanServer rejected: world broadcast activation not ready (state=" +
                    P2PWorldStatusBroadcaster.ActivationState + ")");
                try { MenuUI.alert("SteamP2PFriends 正在完成初始化，请稍后重试创建房间。"); } catch { }
                return;
            }

            if (_isStarting)
            {
                RoleLogger.Warn("[Host]", "P2P 服务器正在启动中，忽略重复请求。");
                return;
            }

            try
            {
                RoleLogger.Info("[Host]", $"StartLanServer: map={mapName} queryPort={queryPort} maxPlayers={maxPlayers} mode={mode} cheats={cheats}");

                _isStarting = true;
                SteamReadyHandled = false;
                _hostMode = EHostMode.LAN;

                LevelInfo level = Level.getLevel(mapName);
                if (level == null)
                {
                    throw new InvalidOperationException($"Level not found: {mapName}");
                }

                ResetHostSession();

                ConfigureCommonServerSettings(level, mode, maxPlayers, cheats);
                Provider.serverID = "LAN_" + queryPort;
                Provider.serverName = "局域网测试房间";
                Provider.bindAddress = null;
                Provider.ip = 0;
                Provider.port = queryPort;

                PrepareClientHostSession();
                EnableListenHostOfflineAuth();
                EnsurePreviousGameServerClosed();

                MenuUI.closeAll();
                LoadingUI.SetLoadingText("Loading_MainMenu");

                Provider.onEnemyConnected += OnPlayerConnectedToServer;

                StartHostingCore();

                if (!_isStarting) return;

                EnsureListenServerClientFlag();
                OverrideServerToHostUser();

                SteamGameServerCallbacksWatcher.StartWatching();
                HasCheatsGuardWatcher.StartGuard(cheats);

                EnsureCommanderInitialized();

                IServerMultiplayerService serverMp = Provider.provider?.multiplayerService?.serverMultiplayerService;
                if (serverMp != null)
                {
                    if (SteamRuntime.IsGameServerAlive())
                    {
                        OnSteamServerReady();
                    }
                    else
                    {
                        serverMp.ready += OnSteamServerReady;
                    }
                }

                IsP2PServerActive = true;
            }
            catch (Exception ex)
            {
                RoleLogger.Error("[Host]", $"StartLanServer 失败: {ex}");
                AbortHostStart("启动局域网主机失败，请查看日志。");
            }
        }

        private static void StartHostingCore()
        {
            _listenTickCount = 0;
            _loggedListenActive = false;
            _lastListenHeartbeatTime = 0f;

            if (!_subscribedServerHosted)
            {
                Provider.onServerHosted += OnServerHosted;
                _subscribedServerHosted = true;
            }

            RoleLogger.Info("[Host]", $"[P2P] Calling Provider.host() mode={_hostMode}");
            Provider.host();

            // OnServerHosted runs inside host(); if it aborted, IsStarting is already false.
            if (!_isStarting)
            {
                RoleLogger.Warn("[Host]", "[P2P] Host start was aborted during Provider.host()");
                return;
            }

            if (!Provider.isServer)
            {
                throw new InvalidOperationException("Provider.host() finished but Provider.isServer is still false");
            }

            object serverTransport = ReflectionUtil.GetStaticField(typeof(Provider), "serverTransport");
            if (serverTransport == null)
            {
                throw new InvalidOperationException("Provider.host() finished but serverTransport is null (GameServer open may have failed)");
            }

            RoleLogger.Info("[Host]", $"[P2P] Provider.host() OK transport={serverTransport.GetType().Name}");
        }

        /// <summary>
        /// vanilla Provider.onServerHosted 回调（B 方同步事务性订阅）。
        /// 在 host() 完成、地图加载就绪后触发，是 listen server 真正就绪的标志。
        /// </summary>
        private static void OnServerHosted()
        {
            if (!_isStarting) return;  // AbortHostStart 已回滚

            RoleLogger.Info("[Host]", "!!! Provider.onServerHosted 回调触发 - listen server 已就绪 !!!");

            try
            {
                Type providerType = typeof(Provider);
                CSteamID localUser = Provider.user;

                RoleLogger.Info("[Host]",
                    $"[Diag] OnServerHosted: Provider.user={localUser.m_SteamID} " +
                    $"_server={((CSteamID)ReflectionUtil.GetStaticField(providerType, "_server")).m_SteamID} " +
                    $"_client={((CSteamID)ReflectionUtil.GetStaticField(providerType, "_client")).m_SteamID} " +
                    $"isServer={Provider.isServer} isClient={Provider.isClient}");

                // B 方：listen server 双 true 状态
                ReflectionUtil.SetStaticField(providerType, "_isClient", true);
                ReflectionUtil.SetStaticField(providerType, "_server", localUser);
                ReflectionUtil.SetStaticField(providerType, "_client", localUser);
                ReflectionUtil.SetStaticField(providerType, "_clientHash", Hash.SHA1(localUser));

                // B 方：serverTransport null 守卫
                object serverTransport = ReflectionUtil.GetStaticField(providerType, "serverTransport");
                RoleLogger.Info("[Host]",
                    $"[P2P] OnServerHosted isServer={Provider.isServer} isClient={Provider.isClient} transport=" +
                    (serverTransport != null ? serverTransport.GetType().Name : "null"));

                if (serverTransport == null)
                {
                    throw new InvalidOperationException("serverTransport is null in OnServerHosted");
                }

                LogListenSocketDiagnostics();

                try
                {
                    SteamP2PFriends.Shared.SnsDiagnosticUtil.SnapshotRelayAuthReadiness("[Host]", "ListenSocket-created");
                }
                catch (Exception ex)
                {
                    RoleLogger.Warn("[Host]", $"[Diag] SnapshotRelayAuthReadiness 异常（不阻断）: {ex.Message}");
                }

                // 根因：vanilla MasterBundleValidation.initialize 仅 dedicated server 调用，
                // listen server 模式下 serverHashes=null，导致服务端 effective hash 计算与客机端不一致。
                try
                {
                    int populatedCount = MasterBundleHashInitializer.PopulateServerHashes();
                    RoleLogger.Info("[Host]",
                        $"[OnServerHosted] v0.2.3.17 MasterBundleHashInitializer.PopulateServerHashes populated={populatedCount}");
                    if (populatedCount <= 0)
                    {
                        RoleLogger.Warn("[Host]",
                            "[OnServerHosted] !! MasterBundleHashInitializer 未填充任何 bundle（可能 vanilla 版本变化或 allMasterBundles 未就绪）");
                    }
                }
                catch (Exception ex)
                {
                    RoleLogger.Error("[Host]", $"[OnServerHosted] MasterBundleHashInitializer 异常（不阻断）: {ex}");
                }

                if (_hostMode == EHostMode.P2P)
                {
                    if (!_isStarting || _stage6BStartToken == Guid.Empty)
                        throw new InvalidOperationException("Stage6B server mapping rejected: P2P start state invalid");
                    string stage6BFailure;
                    if (!Stage6BWorkshopSession.TryApplyServerMapping(Level.getLevel(Provider.map), _stage6BStartToken, out stage6BFailure))
                        throw new InvalidOperationException("Stage6B server mapping rejected: " + stage6BFailure);
                }

                //   LoadClientHostedLevel 前，反射调用 LIT BeginScope("p2p", map, slot)。
                //   LIT 缺席：日志跳过；LIT 已安装但作用域失败：抛异常走外层 catch -> AbortHostStart。
                if (_hostMode == EHostMode.P2P)
                    InitializeOptionalLITP2PFaultScope();

                // B 方：主动加载地图（根治客机进度条卡死）
                LoadClientHostedLevel();

                //   检测 spawns 就绪（onLevelLoaded level=2）后调用 generateItems 全地图循环。
                //   详见 Patches/ItemManagerP0B6RegenerateOnLevelLoadedPatch.cs。

                // 模式分支
                if (_hostMode == EHostMode.P2P)
                {
                    //   未初始化时返回 false，必须抛异常进入 AbortHostStart 事务性回滚。
                    bool lobbyOk = P2PLobbyManager.CreateRoomAndLinkOnReady();
                    if (!lobbyOk)
                    {
                        throw new InvalidOperationException(
                            "P2PLobbyManager.CreateRoomAndLinkOnReady 返回 false（未初始化或 fail-closed 拒绝）");
                    }
                    //（F-7 offlineOnly=true 时 ready 回调可能不触发）
                    PublishP2PRichPresence();
                    RoleLogger.Info("[Host]",
                        $"!!! 房主 SteamUser SteamID = {GetLocalSteamIdString()} !!! " +
                        $"(客机请用此 ID 通过 P2P 多人联机菜单 -> 作为客机 -> 请求加入)");
                }
                else if (_hostMode == EHostMode.LAN)
                {
                    RoleLogger.Info("[Host]",
                        $"[P2P] LAN host ready queryPort={Provider.port} connectionPort={Provider.GetServerConnectionPort()}");
                }
            }
            catch (Exception ex)
            {
                RoleLogger.Error("[Host]", $"OnServerHosted failed: {ex}");
                AbortHostStart("主机加载地图失败，请查看日志。");
            }
        }

        /// <summary>
        ///
        /// 调用时机：OnServerHosted 内，_server/_client 已对齐到 Provider.user、serverTransport 非空、
        ///
        /// 行为契约：
        ///   - LIT 未安装：日志 Info "[LIT] not installed; P2P fault scope bridge skipped." 后正常返回，P2P 继续启动。
        ///     BeginScope API 缺失或签名不匹配 / BeginScope 返回 false：抛 InvalidOperationException，
        ///     由外层 OnServerHosted catch 捕获并调用 AbortHostStart，房主启动 fail-closed 中止。
        ///   - LIT 已安装且 BeginScope 返回 true：日志 Info "[LIT] P2P fault scope ready: ..."，P2P 继续启动。
        ///
        /// 反射契约：
        ///   - 仅按 AppDomain 程序集名 "LaunchInventoryTidy" 发现 LIT 类型，不增加编译时引用。
        ///   - 仅调用公共静态方法 BeginScope(string mode, string mapName, int saveSlot) -> bool。
        ///   - LIT 内部不感知 SteamP2PFriends 类型，所有 P2P 上下文由本方法注入。
        /// </summary>
        private static void InitializeOptionalLITP2PFaultScope()
        {
            if (_hostMode != EHostMode.P2P)
                return;

            if (!Stage6ASessionContext.IsActive ||
                Stage6ASessionContext.HostMode != EHostMode.P2P ||
                Stage6ASessionContext.CachedSlot < 0 ||
                Stage6ASessionContext.CachedSlot > 4)
                throw new InvalidOperationException(
                    "LIT scope rejected: Stage6A P2P context is not stable.");

            if (!Provider.isServer || !Provider.isClient ||
                Provider.server == CSteamID.Nil ||
                Provider.client == CSteamID.Nil ||
                Provider.server != Provider.client)
                throw new InvalidOperationException(
                    "LIT scope rejected: Listen Host identity is not stable.");

            string mapName = Provider.map;
            if (string.IsNullOrWhiteSpace(mapName))
                throw new InvalidOperationException(
                    "LIT scope rejected: Provider.map is empty.");

            Type litType = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!string.Equals(
                        assembly.GetName().Name,
                        "LaunchInventoryTidy",
                        StringComparison.Ordinal))
                    continue;

                litType = assembly.GetType(
                    "LaunchInventoryTidy.LaunchInventoryTidyPlugin",
                    false);
                break;
            }

            if (litType == null)
            {
                RoleLogger.Info("[Host]",
                    "[LIT] not installed; P2P fault scope bridge skipped.");
                return;
            }

            MethodInfo beginScope = litType.GetMethod(
                "BeginScope",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string), typeof(string), typeof(int) },
                null);

            if (beginScope == null || beginScope.ReturnType != typeof(bool))
                throw new InvalidOperationException(
                    "LIT scope bridge rejected: required BeginScope API is unavailable.");

            object invoked;
            try
            {
                invoked = beginScope.Invoke(null, new object[]
                {
                    "p2p",
                    mapName,
                    Stage6ASessionContext.CachedSlot
                });
            }
            catch (TargetInvocationException ex)
            {
                throw new InvalidOperationException(
                    "LIT scope bridge threw an exception.",
                    ex.InnerException ?? ex);
            }

            if (!(invoked is bool) || !((bool)invoked))
                throw new InvalidOperationException(
                    "LIT scope bridge returned failure; host start is aborted fail-closed.");

            RoleLogger.Info("[Host]",
                "[LIT] P2P fault scope ready: map=" + mapName +
                " slot=" + Stage6ASessionContext.CachedSlot +
                " steamId=" + Provider.server.m_SteamID);
        }

        /// <summary>
        /// 验证 SteamUserP2PRedirectPatch 已将 CreateListenSocketP2P 重定向到 SteamNetworkingSockets。
        /// </summary>
        private static void LogListenSocketDiagnostics()
        {
            try
            {
                if (_hostMode != EHostMode.P2P) return;

                RoleLogger.Info("[Host]",
                    "[Diag] P2P 模式下 listen socket 应由 SteamUserP2PRedirectPatch 重定向到 " +
                    "SteamNetworkingSockets.CreateListenSocketP2P(0)，使用 SteamUser identity。");
                RoleLogger.Info("[Host]",
                    $"[Diag] 房主 SteamUser ID={SteamUser.GetSteamID().m_SteamID}，" +
                    $"客机 ConnectP2P(SteamUser ID, 0) 应命中此 listen socket。");
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]", $"[Diag] LogListenSocketDiagnostics 异常（不阻断）: {ex.Message}");
            }
        }

        /// <summary>
        /// Steam 后端 ready 回调（A 方保留，双订阅 fallback）。
        /// </summary>
        private static void OnSteamServerReady()
        {
            if (SteamReadyHandled) return;
            SteamReadyHandled = true;

            try
            {
                RoleLogger.Info("[Host]", "Steam 后端 ready，开始写 Rich Presence…");
                OverrideServerToHostUser();
                PublishP2PRichPresence();
                // P2P-only 不依赖 GameServer 广告。SteamUser Rich Presence 已在上方发布。
                if (_hostMode != EHostMode.P2P)
                {
                    try { SteamRuntime.SetAdvertiseServerActive(true); } catch { }
                }
                RoleLogger.Info("[Host]", $"!!! 房主 SteamUser SteamID = {GetLocalSteamIdString()} !!! (Rich Presence 已发布)");
            }
            catch (Exception ex)
            {
                RoleLogger.Error("[Host]", $"OnSteamServerReady 失败: {ex}");
            }
        }

        /// <summary>
        /// 加载房主本地地图（B 方完整移植，根治客机进度条卡死）。
        /// </summary>
        private static void LoadClientHostedLevel()
        {
            Type providerType = typeof(Provider);
            LevelInfo level = Level.getLevel(Provider.map);
            if (level == null)
            {
                throw new InvalidOperationException($"Level not found: {Provider.map}");
            }

            // B 方：重置 lag/pings 防止误判
            ReflectionUtil.SetStaticMember(providerType, "timeLastPacketWasReceivedFromServer", Time.realtimeSinceStartup);
            ReflectionUtil.SetStaticMember(providerType, "pings", new float[4]);
            ReflectionUtil.InvokeStatic(providerType, "lag", 0f);

            // B 方：物理材质网络表
            ReflectionUtil.InvokeStatic(typeof(PhysicsMaterialNetTable), "ServerPopulateTable");

            // B 方：Level.load(level, true) server-authoritative
            RoleLogger.Info("[Host]", $"[P2P] Loading level \"{Provider.map}\"...");
            Level.load(level, true);
            ReflectionUtil.InvokeStatic(providerType, "loadGameMode");
            ReflectionUtil.InvokeStatic(providerType, "applyLevelModeConfigOverrides");

            // B 方：再次重置（避免加载时长被误判为 lag）
            ReflectionUtil.SetStaticMember(providerType, "timeLastPacketWasReceivedFromServer", Time.realtimeSinceStartup);
            RoleLogger.Info("[Host]", $"[P2P] Loaded client-hosted level \"{Provider.map}\"");
        }

        /// <summary>
        /// 准备 client-host 会话配置（B 方完整移植）。
        /// </summary>
        private static void PrepareClientHostSession()
        {
            Type providerType = typeof(Provider);

            if (_hostMode == EHostMode.P2P)
            {
                if (!Stage6ASessionContext.IsActive ||
                    Stage6ASessionContext.HostMode != EHostMode.P2P ||
                    Characters.selected != Stage6ASessionContext.CachedSlot)
                {
                    throw new InvalidOperationException(
                        $"[Stage6A] P2P session not active or slot mismatch: " +
                        $"hostMode={_hostMode}, isActive={Stage6ASessionContext.IsActive}, " +
                        $"ctxHostMode={Stage6ASessionContext.HostMode}, " +
                        $"ctxSlot={Stage6ASessionContext.CachedSlot}, " +
                        $"selected={Characters.selected}; aborting");
                }
            }

            ConfigData configData = ConfigData.CreateDefault(true);
            configData.Server.Use_FakeIP = false;  // v2.9 验证不可行
            configData.Server.VAC_Secure = false;  // listen server 不做 VAC

            ReflectionUtil.SetStaticField(providerType, "_configData", configData);

            // 依据：U3-SDK Provider.cs:2097 _modeConfigDataOverrides.Clear() 必须在 LoadGameplayConfig 之前调用
            // 真实类型：Provider.cs:4528-4531 private static Dictionary<FieldInfo, object> _modeConfigDataOverrides
            FieldInfo overridesField = AccessTools.Field(providerType, "_modeConfigDataOverrides");
            if (overridesField == null)
            {
                throw new InvalidOperationException("[Stage6A] _modeConfigDataOverrides field not found");
            }
            object overridesValue = overridesField.GetValue(null);
            System.Collections.IDictionary overrides = overridesValue as System.Collections.IDictionary;
            if (overrides == null)
            {
                throw new InvalidOperationException(
                    $"[Stage6A] _modeConfigDataOverrides is null or not IDictionary (actual: {(overridesValue == null ? "null" : overridesValue.GetType().FullName)})");
            }
            overrides.Clear();
            if (overrides.Count != 0)
            {
                throw new InvalidOperationException(
                    $"[Stage6A] _modeConfigDataOverrides.Clear() failed; Count={overrides.Count}");
            }

            ReflectionUtil.InvokeStatic(providerType, "LoadGameplayConfig", true);

            // B 方：LoadGameplayConfig 可能替换 _configData，重读并再次强制安全默认
            ConfigData loadedConfig = (ConfigData)ReflectionUtil.GetStaticField(providerType, "_configData") ?? configData;
            loadedConfig.Server.Use_FakeIP = false;
            loadedConfig.Server.VAC_Secure = false;
            ReflectionUtil.SetStaticField(providerType, "_configData", loadedConfig);

            ModeConfigData modeConfig = loadedConfig.getModeConfig(Provider.mode);
            if (modeConfig == null)
            {
                modeConfig = new ModeConfigData(Provider.mode);
                modeConfig.InitSingleplayerDefaults();
            }

            ApplyP2PRoomRules(modeConfig, _activeRoomRules);

            ReflectionUtil.SetStaticField(providerType, "_modeConfigData", modeConfig);
            ReflectionUtil.SetStaticField(providerType, "isVacActive", false);
            ReflectionUtil.SetStaticField(providerType, "isThirdpartyAntiCheatActive", false);
            ReflectionUtil.SetStaticField(providerType, "_currentServerAdvertisement", null);

            //   白名单初始化移交 P2PWhitelistService.TryBootstrap() 经 IWhitelistStore.Load() 完成。
            //   SteamBlacklist/Adminlist.load() 保留。
            SteamBlacklist.load();
            SteamAdminlist.load();
            PlayerInventory.skillsets = PlayerInventory.SKILLSETS_CLIENT;

            RoleLogger.Info("[Host]", "[P2P] PrepareClientHostSession 完成（ConfigData + LoadGameplayConfig + ModeConfig + Blacklist/Adminlist；Whitelist 经 P2PWhitelistService.TryBootstrap）");
        }

        private static void ConfigureCommonServerSettings(LevelInfo level, EGameMode gameMode, byte maxPlayers, bool cheats)
        {
            Dedicator.serverVisibility = ESteamServerVisibility.LAN;
            Provider.map = level.name;
            Provider.maxPlayers = maxPlayers;
            Provider.queueSize = (byte)Math.Max(8, maxPlayers * 2);
            Provider.serverPassword = string.Empty;
            Provider.isPvP = _activeRoomRules == null || _activeRoomRules.EnablePvp;
            Provider.isWhitelisted = false;
            Provider.hideAdmins = false;
            Provider.hasCheats = cheats;
            Provider.filterName = false;
            Provider.mode = gameMode;
            Provider.isGold = false;
            Provider.gameMode = null;
            Provider.cameraMode = ECameraMode.BOTH;
            Commander.init();
        }

        private static void ApplyP2PRoomRules(ModeConfigData modeConfig, P2PRoomRules rules)
        {
            if (modeConfig == null || rules == null) return;

            rules.ApplyTo(modeConfig);

            RoleLogger.Info("[Host]", "[RoomRules] pvp=" + rules.EnablePvp +
                " keepInventory=" + rules.KeepInventoryOnDeath +
                " keepSkills=" + rules.KeepSkillsOnDeath +
                " keepExperience=" + rules.KeepExperienceOnDeath);
        }

        /// <summary>
        /// GSLT 分支选择（仅 LAN 测试模式使用）。
        /// </summary>
        private static void ConfigureGsltBranch()
        {
            try
            {
                string gsltToken = SteamP2PFriendsPlugin.GSLT_Login_Token?.Value?.Trim() ?? "";
                if (!string.IsNullOrEmpty(gsltToken) && gsltToken.Length == 32)
                {
                    Dedicator.serverVisibility = ESteamServerVisibility.Internet;
                    Provider.configData.Browser.Login_Token = gsltToken;
                    RoleLogger.Info("[Host]", $"[P2P-GSLT] 分支 A：启用正规登录通道（token 长度=32），serverVisibility=Internet");
                }
                else
                {
                    Dedicator.serverVisibility = ESteamServerVisibility.LAN;
                    Provider.configData.Browser.Login_Token = "";
                    if (string.IsNullOrEmpty(gsltToken))
                    {
                        RoleLogger.Warn("[Host]", "[P2P-GSLT] 检测到未配置 GSLT，已安全回退至局域网（LAN）联机模式");
                    }
                    else
                    {
                        RoleLogger.Warn("[Host]", $"[P2P-GSLT] GSLT 令牌长度异常（{gsltToken.Length} 位，应为 32 位），已回退至 LAN 模式");
                    }
                }
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]", $"[P2P-GSLT] 分支选择异常（不阻断）: {ex.Message}");
            }
        }

        /// <summary>
        /// 强制 LAN 可见性 + 清空 GSLT token + 禁用 FakeIP + 禁用 VAC，
        /// 避免 SteamGameServer 试图注册公网 SDR 路由（listen server 铁律：不可行）。
        /// </summary>
        private static void ConfigureP2POnlyBranch()
        {
            try
            {
                Dedicator.serverVisibility = ESteamServerVisibility.LAN;
                if (Provider.configData?.Browser != null)
                {
                    Provider.configData.Browser.Login_Token = "";
                }
                if (Provider.configData?.Server != null)
                {
                    Provider.configData.Server.Use_FakeIP = false;
                    Provider.configData.Server.VAC_Secure = false;
                }
                RoleLogger.Info("[Host]",
                    "[P2P-Only] P2P 模式已固定 SteamUser identity 路线：serverVisibility=LAN, GSLT=空, FakeIP=false, VAC=false。");
                RoleLogger.Info("[Host]",
                    "[P2P-Only] 传输 identity 走 SteamNetworkingSockets.CreateListenSocketP2P(0)；实际 ICE/SDR 数据路由由诊断日志取证。");
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]", $"[P2P-Only] 配置异常（不阻断）: {ex.Message}");
            }
        }

        private static void EnsureListenServerClientFlag()
        {
            try
            {
                FieldInfo fi = AccessTools.Field(typeof(Provider), "_isClient");
                if (fi != null)
                {
                    fi.SetValue(null, true);
                }

                FieldInfo clientField = AccessTools.Field(typeof(Provider), "_client");
                if (clientField != null)
                {
                    CSteamID userId = Provider.user;
                    clientField.SetValue(null, userId);
                    RoleLogger.Info("[Host]",
                        $"[P2P] Provider._isClient=true, _client=Provider.user={userId.m_SteamID} (isServer={Provider.isServer}, isClient={Provider.isClient})");
                }
            }
            catch (Exception ex)
            {
                RoleLogger.Error("[Host]", $"EnsureListenServerClientFlag 失败: {ex}");
            }
        }

        /// <summary>
        /// A 方核心创新：覆盖 _server 从 AnonID 到 Provider.user。
        /// 让客户端 Provider.connect 走 ConnectP2P(SteamUser ID) 命中被重定向后的 listen socket。
        /// </summary>
        private static void OverrideServerToHostUser()
        {
            try
            {
                FieldInfo serverField = AccessTools.Field(typeof(Provider), "_server");
                if (serverField == null)
                {
                    RoleLogger.Warn("[Host]", "无法反射 Provider._server 字段。");
                    return;
                }

                CSteamID userId = Provider.user;
                CSteamID currentServer = (CSteamID)serverField.GetValue(null);

                if (currentServer.m_SteamID != userId.m_SteamID)
                {
                    serverField.SetValue(null, userId);
                    RoleLogger.Info("[Host]",
                        $"[P2P] Provider._server 已从 AnonID ({currentServer.m_SteamID}) 覆盖为 Provider.user ({userId.m_SteamID})");
                }
            }
            catch (Exception ex)
            {
                RoleLogger.Error("[Host]", $"OverrideServerToHostUser 失败: {ex}");
            }
        }

        /// <summary>
        /// 重定向后 GS identity 上没有监听 socket，好友经 Rich Presence 加入或手输"Server Code"
        /// 必然连空。统一发布个人 SteamID，与 lobby、剪贴板路径一致。
        /// </summary>
        private static void PublishP2PRichPresence()
        {
            try
            {
                string hostSteamId = SteamUser.GetSteamID().m_SteamID.ToString();
                if (!string.IsNullOrEmpty(hostSteamId) && hostSteamId != "0")
                {
                    SteamRuntime.SetRichPresence("connect", hostSteamId);
                    SteamRuntime.SetRichPresence("steam_display", "P2P Co-op");
                    SteamRuntime.SetRichPresence("level_name", Provider.map);
                    RoleLogger.Info("[Host]", $"[P2P] Rich Presence 已发布: +connect {hostSteamId}, map={Provider.map}");
                }
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]", $"PublishP2PRichPresence 失败: {ex.Message}");
            }
        }

        private static void ResetHostSession()
        {
            try
            {
                RoleLogger.Info("[Host]", "[P2P] 正在清理 Provider 静态残留…");

                try
                {
                    Patches.ItemManagerP0B6RegenerateOnLevelLoadedPatch.ResetRegenerationFlag();
                }
                catch (Exception ex)
                {
                    RoleLogger.Error("[Host]", $"[P0-B-6] ResetRegenerationFlag 异常（不阻断）: {ex}");
                }

                ReflectionUtil.SetStaticField(typeof(Provider), "isDedicatedUGCInstalled", false);

                FieldInfo monitorFi = AccessTools.Field(typeof(Provider), "dswUpdateMonitor");
                if (monitorFi != null)
                {
                    object monitor = monitorFi.GetValue(null);
                    if (monitor is UnityEngine.Object uobj)
                    {
                        try { UnityEngine.Object.Destroy(uobj); } catch { }
                    }
                    monitorFi.SetValue(null, null);
                }


                object nilSteamId = SteamRuntime.CreateCSteamID(0);
                if (nilSteamId != null)
                {
                    ReflectionUtil.SetStaticField(typeof(Provider), "_server", nilSteamId);
                    ReflectionUtil.SetStaticField(typeof(Provider), "_client", nilSteamId);
                }

                RoleLogger.Info("[Host]", "[P2P] Provider 静态残留清理完成。");
            }
            catch (Exception ex)
            {
                RoleLogger.Error("[Host]", $"ResetHostSession 失败: {ex}");
            }
            finally
            {
                Stage6ASessionContext.Reset();
            }
        }

        /// <summary>
        /// 事务性回滚（B 方完整移植）。
        /// </summary>
        private static void AbortHostStart(string userMessage)
        {
            RoleLogger.Error("[Host]", $"!!! AbortHostStart: {userMessage} !!!");

            bool wasP2P = _hostMode == EHostMode.P2P;
            try
            {
                try
                {
                    _isStarting = false;
                    IsP2PServerActive = false;
                SteamReadyHandled = false;
                _hostMode = EHostMode.None;
                _activeRoomRules = null;

                try
                {
                    Patches.ItemManagerP0B6RegenerateOnLevelLoadedPatch.ResetRegenerationFlag();
                }
                catch (Exception ex)
                {
                    RoleLogger.Error("[Host]", $"[P0-B-6] ResetRegenerationFlag (Abort) 异常（不阻断）: {ex}");
                }

                try
                {
                    Patches.AuthoritativeItemGenerationGatePatch.ResetForSession();
                }
                catch (Exception ex)
                {
                    RoleLogger.Error("[Host]", $"[ItemAuthorityGate] ResetForSession (Abort) failed: {ex.Message}");
                }

                UnsubscribeAll();
                SteamGameServerCallbacksWatcher.StopWatching();
                HasCheatsGuardWatcher.StopGuard();

                try
                {
                    P2PLobbyManager.ResetForAbort();
                }
                catch (Exception ex)
                {
                    RoleLogger.Warn("[Host]", $"P2PLobbyManager.ResetForAbort 异常（不阻断）: {ex.Message}");
                }

                if (_offlineAuthEnabled)
                {
                    RestoreListenHostOfflineAuth();
                }

                if (Provider.isConnected || Provider.isServer)
                {
                    Provider.disconnect();
                }

                TryCloseGameServerApi();

                try { MenuUI.closeAll(); } catch { }
                try { LoadingUI.SetLoadingText("Loading_MainMenu"); } catch { }
                try { MenuPlayUI.open(); } catch { }
                try { MenuUI.alert(userMessage); } catch { }
            }
            catch (Exception ex)
            {
                RoleLogger.Error("[Host]", $"AbortHostStart cleanup failed: {ex}");
            }
            finally
            {
                //   AbortHostStart 永不 arm、永不将状态写为 SaveObserved；仅 Complete("StartAbort")。
                try
                {
                    if (Stage6ASessionContext.IsActive)
                    {
                        LogStage6ASessionEnd("StartAbort");
                    }
                }
                finally
                {
                    try
                    {
                        Stage6ASaveRoundtripObserver.Complete("StartAbort");
                    }
                    finally
                    {
                        Stage6ASessionContext.Reset();
                    }
                }
            }
            }
            finally
            {
                if (wasP2P)
                {
                    try { Patches.LevelObjectRemoteCollisionPatch.ResetAll(); }
                    catch (Exception collisionEx) { RoleLogger.Warn("[Host]", "[LevelObjectCollision] ResetAll (Abort) 异常: " + collisionEx.GetType().Name); }
                    string stage6BFailure;
                    if (!TryCleanupStage6BForExit(out stage6BFailure))
                        RoleLogger.Error("[Host]", "[Stage6B] exit cleanup failed: " + stage6BFailure);
                }
                //   蓝图 §4.5：仅清 _persistenceFaulted 等；不清空/不保存 SteamWhitelist.list
                //   蓝图 §2.3：LAN 路径不得调用 service，故仅 wasP2P 时调用
                if (wasP2P)
                {
                    try { P2PWhitelistService.ResetAfterP2PExit(); }
                    catch (Exception wlEx)
                    {
                        RoleLogger.Error("[Host]", "[P2P-WL] ResetAfterP2PExit (Abort) 异常（不阻断）: " + wlEx);
                    }
                    try { P2PApprovalManager.ResetForSession(); }
                    catch (Exception apEx) { RoleLogger.Warn("[Host]", "[P2P-Approval] Route B reset failed (Abort): " + apEx); }
                    try { SteamPersonaDisplay.ResetAfterSession(); }
                    catch (Exception nameEx) { RoleLogger.Warn("[Host]", "[P2P-Persona] ResetAfterSession (Abort) 异常（不阻断）: " + nameEx.GetType().Name); }
                    try { P2PWorldStatusBroadcaster.ResetForSession(); }
                    catch (Exception wbEx) { RoleLogger.Warn("[Host]", "[WorldBroadcast] ResetForSession (Exit) 异常（不阻断）: " + wbEx.GetType().Name); }
                }
            }
        }

        public static void StopP2PServer()
        {
            bool wasP2P = _hostMode == EHostMode.P2P;
            try
            {
                try
                {
                    bool wasLan = _hostMode == EHostMode.LAN;
                RoleLogger.Info("[Host]", $"[P2P] StopP2PServer mode={_hostMode} ticks={_listenTickCount}");

                _isStarting = false;
                SteamReadyHandled = false;
                IsP2PServerActive = false;
                _hostMode = EHostMode.None;
                _activeRoomRules = null;
                _listenTickCount = 0;
                _loggedListenActive = false;
                _lastListenHeartbeatTime = 0f;

                try
                {
                    Patches.AuthoritativeItemGenerationGatePatch.ResetForSession();
                }
                catch (Exception ex)
                {
                    RoleLogger.Error("[Host]", $"[ItemAuthorityGate] ResetForSession (Stop) failed: {ex.Message}");
                }

                //   reason 只能为 DisconnectCompleted；不得把 SaveObserved 写入 SessionContext。
                if (Stage6ASessionContext.IsActive && Stage6ASessionContext.HostMode == EHostMode.P2P)
                {
                    try
                    {
                        LogStage6ASessionEnd("DisconnectCompleted");
                    }
                    finally
                    {
                        try
                        {
                            Stage6ASaveRoundtripObserver.Complete("DisconnectCompleted");
                        }
                        finally
                        {
                            Stage6ASessionContext.Reset();
                        }
                    }
                }

                UnsubscribeAll();
                SteamGameServerCallbacksWatcher.StopWatching();
                HasCheatsGuardWatcher.StopGuard();

                try
                {
                    P2PLobbyManager.ResetForAbort();
                }
                catch (Exception ex)
                {
                    RoleLogger.Warn("[Host]", $"P2PLobbyManager.ResetForAbort (StopP2PServer) 异常（不阻断）: {ex.Message}");
                }

                if (wasLan || _offlineAuthEnabled)
                {
                    RestoreListenHostOfflineAuth();
                }

                if (!Provider.isServer)
                {
                    TryCloseGameServerApi();
                }
            }
            catch (Exception ex)
            {
                RoleLogger.Error("[Host]", $"StopP2PServer 失败: {ex}");
            }
            //   即使 Stop 内任何语句（日志/状态清理/Unsubscribe）抛出，也能保证 End/Reset 执行。
            //   Reset 幂等：再次 Reset 已清空上下文无副作用。
            finally
            {
                if (Stage6ASessionContext.IsActive && Stage6ASessionContext.HostMode == EHostMode.P2P)
                {
                    try
                    {
                        LogStage6ASessionEnd("DisconnectCompleted");
                    }
                    finally
                    {
                        try
                        {
                            Stage6ASaveRoundtripObserver.Complete("DisconnectCompleted");
                        }
                        finally
                        {
                            Stage6ASessionContext.Reset();
                        }
                    }
                }
            }
            }
            finally
            {
                if (wasP2P)
                {
                    try { Patches.LevelObjectRemoteCollisionPatch.ResetAll(); }
                    catch (Exception collisionEx) { RoleLogger.Warn("[Host]", "[LevelObjectCollision] ResetAll (Stop) 异常: " + collisionEx.GetType().Name); }
                    string stage6BFailure;
                    if (!TryCleanupStage6BForExit(out stage6BFailure))
                        RoleLogger.Error("[Host]", "[Stage6B] exit cleanup failed: " + stage6BFailure);
                }
                //   蓝图 §4.5：仅清 _persistenceFaulted 等；不清空/不保存 SteamWhitelist.list
                //   蓝图 §2.3：LAN 路径不得调用 service，故仅 wasP2P 时调用
                if (wasP2P)
                {
                    try { P2PWhitelistService.ResetAfterP2PExit(); }
                    catch (Exception wlEx)
                    {
                        RoleLogger.Error("[Host]", "[P2P-WL] ResetAfterP2PExit (Stop) 异常（不阻断）: " + wlEx);
                    }
                    try { P2PApprovalManager.ResetForSession(); }
                    catch (Exception apEx) { RoleLogger.Warn("[Host]", "[P2P-Approval] Route B reset failed (Stop): " + apEx); }
                    try { SteamPersonaDisplay.ResetAfterSession(); }
                    catch (Exception nameEx) { RoleLogger.Warn("[Host]", "[P2P-Persona] ResetAfterSession (Stop) 异常（不阻断）: " + nameEx.GetType().Name); }
                }
            }
        }


        private static bool TryPrepareStage6BForP2PStart(LevelInfo level, out string failure)
        {
            ThreadUtil.assertIsGameThread();
            failure = null;
            if (_hostMode != EHostMode.P2P)
            {
                failure = "Stage6B start preparation requires P2P mode";
                return false;
            }
            if (!TryCleanupStage6BForExit(out failure) ||
                Stage6BWorkshopSession.CurrentState != EStage6BWorkshopState.Cleared)
                return false;
            if (!Stage6BWorkshopSession.TryBuildValidatedPlan(level, out failure) ||
                !Stage6BWorkshopSession.TryCommitBeforeHost(out failure))
                return false;
            _stage6BStartToken = Stage6BWorkshopSession.GetCommittedTokenOrThrow();
            return true;
        }

        internal static bool TryCleanupStage6BForExit(out string failure)
        {
            failure = null;
            try
            {
                ThreadUtil.assertIsGameThread();
                bool cleaned = Stage6BWorkshopSession.TryStrictWorkshopCleanup(out failure);
                _stage6BStartToken = Guid.Empty;
                if (!cleaned || Stage6BWorkshopSession.CurrentState != EStage6BWorkshopState.Cleared ||
                    !Stage6BWorkshopSession.HasNoRequirementPlan)
                {
                    Stage6BWorkshopSession.MarkCleanupFaulted();
                    if (String.IsNullOrEmpty(failure)) failure = "Stage6B cleanup postcondition failed";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                _stage6BStartToken = Guid.Empty;
                Stage6BWorkshopSession.MarkCleanupFaulted();
                failure = "Stage6B cleanup gateway exception: " + ex.GetType().Name;
                return false;
            }
        }

        internal static bool IsStage6BCurrentP2PExitEligible
        {
            get { return _hostMode == EHostMode.P2P && Stage6BWorkshopSession.HasActiveP2PSession; }
        }

        internal static void TryCleanupStage6BForDisconnectFinalizer(string location)
        {
            try
            {
                if (!IsStage6BCurrentP2PExitEligible)
                    return;
                string failure;
                if (!TryCleanupStage6BForExit(out failure))
                    RoleLogger.Error("[Host]", "[Stage6B] disconnect cleanup failed at " + location + ": " + failure);
            }
            catch
            {
                // A Harmony Finalizer must never replace Provider.disconnect's original exception.
            }
        }

        //   只读，不写状态，不反射，不访问 Provider 或 Unity 对象；
        //   LAN、单人、U3DS、启动中止、已完成 Stop 时返回 false；
        //   Prefix 和 Finalizer 以此为第一道环境隔离。
        //   不包含 Provider.isServer，否则原版 disconnect 已修改 flags 时 Finalizer 无法记录此前已 arm 的 P2P 失败。
        internal static bool IsStage6ANativeSaveObservationActive
        {
            get
            {
                return IsP2PServerActive &&
                       Stage6ASessionContext.IsActive &&
                       Stage6ASessionContext.HostMode == EHostMode.P2P &&
                       Stage6ASessionContext.StartSucceeded;
            }
        }

        //   门 3：仅 IsP2PServerActive=true && StartSucceeded=true 后才 arm。
        internal static void TryArmStage6ANativeSaveObservation()
        {
            ThreadUtil.assertIsGameThread();
            if (!IsP2PServerActive ||
                !Stage6ASessionContext.IsActive ||
                Stage6ASessionContext.HostMode != EHostMode.P2P ||
                !Stage6ASessionContext.StartSucceeded)
                return;

            Stage6ASaveRoundtripObserver.ArmForNativeShutdown();
        }

        //   门 9：Finalizer 只在非空 __exception 时标失败；不吞异常、不改原版字段。
        internal static void MarkStage6ANativeSaveObservationFailure(Exception exception)
        {
            ThreadUtil.assertIsGameThread();
            Stage6ASaveRoundtripObserver.MarkNativeDisconnectFailed(exception);
        }

        private static void EnsurePreviousGameServerClosed()
        {
            TryCloseGameServerApi();
        }

        private static void TryCloseGameServerApi()
        {
            try
            {
                if (Provider.provider?.multiplayerService?.serverMultiplayerService == null) return;

                bool hosting = Provider.provider.multiplayerService.serverMultiplayerService.isHosting;
                if (!hosting) return;

                RoleLogger.Info("[Host]", "[P2P] Closing leftover Steam GameServer API");
                Provider.provider.multiplayerService.serverMultiplayerService.close();
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]", $"TryCloseGameServerApi 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 每帧调用（由 Plugin.Update 驱动）：listen server 消息泵 + 心跳日志。
        /// </summary>
        public static void TickListen()
        {
            if (_listenBusy || !ShouldProcessClientHostListen()) return;
            if (!EnsureListenMethods()) return;

            try
            {
                DetectEscPauseState();
            }
            catch (Exception ex)
            {
                // 异常隔离：不阻断 listenServer tick
                if (Time.realtimeSinceStartup - _lastListenErrorLogTime > 5f)
                {
                    RoleLogger.Warn("[Host]", $"[P0-D] DetectEscPauseState 异常（不阻断）: {ex.Message}");
                }
            }

            _listenBusy = true;
            try
            {
                _releaseAllMethod?.Invoke(null, null);
                _listenServerMethod.Invoke(null, null);
                _listenTickCount++;

                if (!_loggedListenActive)
                {
                    _loggedListenActive = true;
                    RoleLogger.Info("[Host]",
                        $"[P2P] Client-host listen loop active (mode={_hostMode}, map={Provider.map}, port={Provider.port})");
                }

                float now = Time.realtimeSinceStartup;
                if (now - _lastListenHeartbeatTime > 10f)
                {
                    _lastListenHeartbeatTime = now;
                    RoleLogger.Info("[Host]",
                        $"[P2P] Host listen heartbeat mode={_hostMode} clients={Provider.clients?.Count ?? -1} pending={Provider.pending?.Count ?? -1} ticks={_listenTickCount}");

                    if (Provider.clients != null && Provider.clients.Count > 0)
                    {
                        foreach (SteamPlayer sp in Provider.clients)
                        {
                            try
                            {
                                if (ReferenceEquals(sp, null) || ReferenceEquals(sp.playerID, null)) continue;
                                ulong sid = sp.playerID.steamID.m_SteamID;
                                string name = sp.playerID.playerName ?? "unknown";
                                RoleLogger.Info("[Host]",
                                    $"[Diag] client: name={name} steamID={sid} hasTransport={sp.transportConnection != null}");
                            }
                            catch { }
                        }
                    }
                }

                if (now - _lastQueueNotifyTime > 6f)
                {
                    _lastQueueNotifyTime = now;
                    object providerInstance = ReflectionUtil.GetStaticField(typeof(Provider), "steam");
                    if (providerInstance != null)
                    {
                        ReflectionUtil.InvokeInstance(providerInstance, "NotifyClientsInQueueOfPosition");
                    }
                }
            }
            catch (Exception ex)
            {
                float now = Time.realtimeSinceStartup;
                if (now - _lastListenErrorLogTime > 5f)
                {
                    _lastListenErrorLogTime = now;
                    RoleLogger.Warn("[Host]", $"[P2P] Client-host listenServer failed: {ex.Message}");
                }
            }
            finally
            {
                _listenBusy = false;
            }
        }


        /// <summary>
        /// 供 ListenRegionSync send 日志在输出时附加 escPaused 前缀。
        /// </summary>
        public static bool EscPauseDetectorEnabled => _escPauseDetectorEnabled;

        /// <summary>
        /// 供 ListenRegionSync send 日志在输出时附加 escPaused=True/False 前缀。
        /// </summary>
        public static bool IsEscPausedCurrent => _lastEscPaused;

        /// <summary>
        ///
        ///   必须同时检测以下 3 项指示器，任一为真即判定为暂停：
        ///   1. LoadingUI.isBlocked（ESC 暂停时为 true，但加载期间也为 true）
        ///   2. Time.timeScale == 0（ESC 暂停的强信号）
        ///   3. MenuUI 暂停菜单激活（最直接的 ESC 暂停证据）
        ///
        /// 严禁清单：
        ///   - ❌ 强制推进 RegionSync 发送
        ///   - ❌ Patch Provider.listenServer 移除 ESC 守卫
        ///   - ❌ 在 ESC 暂停期间自动恢复 Time.timeScale
        ///   - ❌ 阻止房主按 ESC
        /// </summary>
        private static void DetectEscPauseState()
        {
            bool isPaused = IsEscPaused();

            if (!_escPauseDetectorEnabled)
            {
                _escPauseDetectorEnabled = true;
                RoleLogger.Info("[Host]",
                    $"[P0-D] ESC 暂停状态检测已启用（初始状态: paused={isPaused}）");
            }

            if (isPaused != _lastEscPaused)
            {
                if (isPaused)
                {
                    _escPausedSince = Time.realtimeSinceStartup;
                    _lastEscPausedHeartbeatTime = _escPausedSince;
                    RoleLogger.Warn("[Host]",
                        $"[P0-D] ESC 暂停开始 t={Time.realtimeSinceStartup:F2}s（LoadingUI.isBlocked={LoadingUI.isBlocked}, timeScale={Time.timeScale:F2}, menuUIActive={IsMenuUIActive()})");
                }
                else
                {
                    float duration = Time.realtimeSinceStartup - _escPausedSince;
                    RoleLogger.Warn("[Host]",
                        $"[P0-D] ESC 暂停结束 t={Time.realtimeSinceStartup:F2}s 持续 {duration:F2}s");
                }
                _lastEscPaused = isPaused;
            }

            // 持续暂停期间，每 5 秒输出一次心跳
            if (isPaused)
            {
                float now = Time.realtimeSinceStartup;
                if (now - _lastEscPausedHeartbeatTime > 5f)
                {
                    _lastEscPausedHeartbeatTime = now;
                    float duration = now - _escPausedSince;
                    RoleLogger.Warn("[Host]",
                        $"[P0-D] ESC 持续暂停中 t={now:F2}s 已持续 {duration:F2}s（LoadingUI.isBlocked={LoadingUI.isBlocked}, timeScale={Time.timeScale:F2}, menuUIActive={IsMenuUIActive()})");
                }
            }
        }

        /// <summary>
        /// 任一指示器为真即判定为暂停（保守策略，避免漏检）。
        /// </summary>
        private static bool IsEscPaused()
        {
            try
            {
                // 指示器 1：LoadingUI.isBlocked（ESC 暂停时为 true，但加载期间也为 true）
                // 客机连接期间的"加载等待"阶段也会为 true，不能单独作为 ESC 暂停证据
                if (LoadingUI.isBlocked)
                {
                    // 但如果同时 timeScale==0，则很可能是 ESC 暂停（加载期间 timeScale 不会变 0）
                    if (Time.timeScale == 0f)
                    {
                        return true;
                    }
                    // LoadingUI.isBlocked 单独为 true 时不判定为 ESC 暂停（可能是加载期间）
                }

                // 指示器 2：Time.timeScale == 0（ESC 暂停的强信号）
                // 某些模组可能修改 timeScale，但 vanilla ESC 暂停一定设 timeScale=0
                if (Time.timeScale == 0f)
                {
                    return true;
                }

                // 指示器 3：MenuUI 暂停菜单激活（最直接的 ESC 暂停证据）
                if (IsMenuUIActive())
                {
                    return true;
                }

                return false;
            }
            catch (Exception)
            {
                // IsEscPaused 异常不写入日志（避免每帧刷屏），由 DetectEscPauseState 调用方捕获
                return false;
            }
        }

        /// <summary>
        /// vanilla MenuUI.window 是 SleekWindow，暂停时某些子窗口激活。
        /// 此方法尝试多种方式检测，任一成功即返回 true。
        /// </summary>
        private static bool IsMenuUIActive()
        {
            try
            {
                // 方案 1：MenuUI.window == null 表示菜单未初始化（游戏内）
                // 方案 2：MenuUI.window != null 但 specific pause window inactive
                // vanilla Unturned 的 MenuUI 结构复杂，这里用简化判定：
                //   - 如果 MenuUI.window != null 且 Provider.isLoading==false 且 Level.isLoaded==true
                //     且 Time.timeScale==0，则很可能是 ESC 暂停
                //   - 此判定已由 IsEscPaused 的指示器 2 覆盖，这里返回 false 作为 fallback

                // 方案 3（反射）：尝试读取 MenuUI 的 specific pause container
                // 暂不实现，保持保守（依赖 timeScale + LoadingUI 双重指示器）

                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool EnsureListenMethods()
        {
            if (_listenMethodsResolved) return !_listenMethodsFailed;

            _listenMethodsResolved = true;
            try
            {
                _listenServerMethod = ReflectionUtil.GetStaticMethod(typeof(Provider), "listenServer");

                Type poolType = ReflectionUtil.FindType("SDG.Unturned.TransportConnectionListPool");
                if (poolType != null)
                {
                    _releaseAllMethod = ReflectionUtil.GetStaticMethod(poolType, "ReleaseAll");
                }
                else
                {
                    RoleLogger.Warn("[Host]", "[P2P] TransportConnectionListPool not found; continuing without ReleaseAll");
                }

                RoleLogger.Info("[Host]", "[P2P] Resolved client-host listen methods");
                return true;
            }
            catch (Exception ex)
            {
                _listenMethodsFailed = true;
                RoleLogger.Error("[Host]", $"[P2P] Failed to resolve listenServer reflection: {ex.Message}");
                return false;
            }
        }

        internal static bool ShouldProcessClientHostListen()
        {
            return IsP2PServerActive
                && _hostMode != EHostMode.None
                && !Dedicator.IsDedicatedServer
                && Provider.isConnected
                && Provider.isServer
                && Level.isLoaded;
        }

        private static bool RequireGameThread(string operation)
        {
            try
            {
                ThreadUtil.assertIsGameThread();
                return true;
            }
            catch (Exception ex)
            {
                RoleLogger.Error("[Host]",
                    $"{operation} rejected: game-thread assertion failed ({ex.GetType().Name})");
                return false;
            }
        }

        private static void EnableListenHostOfflineAuth()
        {
            if (_offlineAuthEnabled) return;

            CommandLineFlag offlineOnly = (CommandLineFlag)ReflectionUtil.GetStaticField(typeof(Dedicator), "offlineOnly");
            _savedOfflineOnly = offlineOnly.value;
            offlineOnly.value = true;
            _restoredOfflineOnly = false;
            _offlineAuthEnabled = true;

            RoleLogger.Warn("[Host]",
                "[P2P] offlineOnly=true：listen-host Beta 暂跳过 SteamGameServer 票据校验；禁止将其作为公开服务器或正式版认证方案。");
        }

        private static void RestoreListenHostOfflineAuth()
        {
            if (!_offlineAuthEnabled || _restoredOfflineOnly) return;

            CommandLineFlag offlineOnly = (CommandLineFlag)ReflectionUtil.GetStaticField(typeof(Dedicator), "offlineOnly");
            offlineOnly.value = _savedOfflineOnly;
            _restoredOfflineOnly = true;
            _offlineAuthEnabled = false;
            RoleLogger.Info("[Host]", $"[P2P] offlineOnly restored to {_savedOfflineOnly}");
        }

        private static void OnPlayerConnectedToServer(SteamPlayer player)
        {
            try
            {
                // SteamPlayerID 重载 == 但未判空（FACT.md 警告），必须用 ReferenceEquals
                if (ReferenceEquals(player, null) || ReferenceEquals(player.playerID, null)) return;
                string playerName = player.playerID.playerName ?? "unknown";
                ulong steamId = player.playerID.steamID.m_SteamID;
                RoleLogger.Info("[Host]", $"[P2P] 检测到玩家 {playerName} 连入 (steamID={steamId})");

                // Route B transitions after Provider.onServerConnected. At this earlier event the
                // player must not receive either a join broadcast or temporary admin privileges.
            }
            catch (Exception ex)
            {
                RoleLogger.Error("[Host]", $"OnPlayerConnectedToServer 失败: {ex}");
            }
        }

        /// <summary>
        /// Applies the room's "allow others cheats" setting to the current connection only.
        /// Vanilla admin/unadmin are used only as Reliable state replication helpers; the in-memory
        /// persistent list is restored to its exact pre-call snapshot before returning.
        /// </summary>
        internal static void ApplySessionAdminPolicyAfterApproval(SteamPlayer player)
        {
            try
            {
                ThreadUtil.assertIsGameThread();
                if (ReferenceEquals(player, null) || ReferenceEquals(player.playerID, null)) return;

                CSteamID playerSteamId = player.playerID.steamID;
                bool isLocalHost = playerSteamId == Provider.user;
                EP2PSessionAdminAction action = P2PSessionAdminPolicy.Decide(Provider.hasCheats, isLocalHost);

                if (action == EP2PSessionAdminAction.Preserve)
                {
                    RoleLogger.Info("[Host]",
                        $"[P2P-SessionAdmin] actor={MaskSteamIdForSessionAdmin(playerSteamId)} localHost=true " +
                        $"desired=preserve effective={player.isAdmin} persistentListMutated=false");
                    return;
                }

                bool desired = action == EP2PSessionAdminAction.Grant;
                SetTransientAdminState(player, desired);
                RoleLogger.Info("[Host]",
                    $"[P2P-SessionAdmin] actor={MaskSteamIdForSessionAdmin(playerSteamId)} localHost={isLocalHost} " +
                    $"desired={desired} effective={player.isAdmin} persistentListMutated=false");
            }
            catch (Exception ex)
            {
                RoleLogger.Error("[Host]", $"[P2P-SessionAdmin] policy failed closed: {ex}");
                try
                {
                    if (!ReferenceEquals(player, null) && !ReferenceEquals(player.playerID, null) &&
                        player.playerID.steamID != Provider.user)
                    {
                        Provider.kick(player.playerID.steamID, "Unable to enforce room permissions.");
                    }
                }
                catch (Exception kickEx)
                {
                    RoleLogger.Error("[Host]", "[P2P-SessionAdmin] fail-closed kick failed: " + kickEx.Message);
                }
            }
        }

        private static void SetTransientAdminState(SteamPlayer player, bool desired)
        {
            ThreadUtil.assertIsGameThread();
            if (player.isAdmin == desired) return;

            List<SteamAdminID> adminList = SteamAdminlist.list;
            if (adminList == null)
                throw new InvalidOperationException("SteamAdminlist.list is null during session admin projection");

            CSteamID playerSteamId = player.playerID.steamID;
            int existingIndex = FindAdminIndex(adminList, playerSteamId);
            SteamAdminID existing = existingIndex >= 0 ? adminList[existingIndex] : null;
            CSteamID originalJudge = existing == null ? CSteamID.Nil : existing.judgeID;
            int originalCount = adminList.Count;

            if (desired)
            {
                try
                {
                    // SteamAdminlist.admin returns early for an existing list entry without replicating
                    // Admined, so temporarily remove the persistent entry to force the vanilla state message.
                    if (existing != null) adminList.RemoveAt(existingIndex);
                    // Reuse the vanilla Reliable Admined replication, then restore the persistent list snapshot.
                    SteamAdminlist.admin(playerSteamId, Provider.user);
                }
                finally
                {
                    int addedIndex = FindAdminIndex(adminList, playerSteamId);
                    if (addedIndex >= 0) adminList.RemoveAt(addedIndex);
                    if (existing != null)
                    {
                        existing.judgeID = originalJudge;
                        int restoreIndex = Math.Min(existingIndex, adminList.Count);
                        adminList.Insert(restoreIndex, existing);
                    }
                }
            }
            else
            {
                try
                {
                    // Reuse the vanilla Reliable Unadmined replication. A pre-existing persistent entry
                    // is reinserted immediately so the P2P room toggle does not edit Adminlist.dat state.
                    SteamAdminlist.unadmin(playerSteamId);
                }
                finally
                {
                    if (existing != null && FindAdminIndex(adminList, playerSteamId) < 0)
                    {
                        int restoreIndex = Math.Min(existingIndex, adminList.Count);
                        adminList.Insert(restoreIndex, existing);
                    }
                }
            }

            bool persistentEntryRestored = existing == null
                ? FindAdminIndex(adminList, playerSteamId) < 0
                : FindAdminIndex(adminList, playerSteamId) >= 0 && existing.judgeID == originalJudge;
            if (adminList.Count != originalCount || !persistentEntryRestored)
                throw new InvalidOperationException("session admin projection changed persistent admin list snapshot");
        }

        private static int FindAdminIndex(List<SteamAdminID> adminList, CSteamID playerSteamId)
        {
            for (int index = 0; index < adminList.Count; index++)
            {
                SteamAdminID entry = adminList[index];
                if (entry != null && entry.playerID == playerSteamId) return index;
            }
            return -1;
        }

        private static string MaskSteamIdForSessionAdmin(CSteamID steamId)
        {
            string value = steamId.m_SteamID.ToString();
            if (value.Length <= 8) return "masked";
            return value.Substring(0, 8) + "..." + value.Substring(value.Length - 4);
        }

        private static void EnsureCommanderInitialized()
        {
            try
            {
                if (Commander.commands == null)
                {
                    Commander.init();
                    RoleLogger.Info("[Host]", $"[P2P] Commander.init() 已补调（commands.Count: {Commander.commands?.Count ?? 0}）");
                }
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]", $"EnsureCommanderInitialized 失败: {ex.Message}");
            }
        }

        private static string GetLocalSteamIdString()
        {
            try
            {
                object sid = SteamRuntime.GetLocalSteamID();
                if (sid == null) return "unknown";
                FieldInfo fi = AccessTools.Field(sid.GetType(), "m_SteamID");
                if (fi != null && fi.FieldType == typeof(ulong))
                {
                    return ((ulong)fi.GetValue(sid)).ToString();
                }
                return sid.ToString();
            }
            catch { return "unknown"; }
        }

        private static void UnsubscribeAll()
        {
            try
            {
                Provider.onEnemyConnected -= OnPlayerConnectedToServer;
                if (_subscribedServerHosted)
                {
                    Provider.onServerHosted -= OnServerHosted;
                    _subscribedServerHosted = false;
                }
            }
            catch { }
        }


        /// <summary>
        ///   由 StartP2PServer 现有外层 catch 路由到 AbortHostStart，不在同一次调用中 Reset 后继续。
        /// </summary>
        private static class Stage6ASessionContext
        {
            public static string SessionId { get; private set; }
            public static EHostMode HostMode { get; private set; }
            public static int CachedSlot { get; private set; } = -1;
            public static bool StartAttempted { get; private set; }
            public static bool StartSucceeded { get; private set; }
            public static bool EndRecorded { get; private set; }
            public static string ExitReason { get; private set; }

            public static bool IsActive => SessionId != null && !EndRecorded;

            /// <summary>
            /// 开始新会话。若发现未结束旧会话，抛 InvalidOperationException 中止当前启动。
            /// 调用方（StartP2PServer）必须将此异常路由到 AbortHostStart，不得吞掉异常后继续 host()。
            /// </summary>
            public static void BeginSession(EHostMode mode, int slot)
            {
                if (SessionId != null && !EndRecorded)
                {
                    string oldSessionId = SessionId;
                    EHostMode oldHostMode = HostMode;
                    int oldSlot = CachedSlot;
                    RoleLogger.Error("[Host]", $"[Stage6A] BeginSession aborted: previous session not ended; " +
                        $"oldSessionId={oldSessionId}, oldHostMode={oldHostMode}, oldSlot={oldSlot}; " +
                        $"defensive Reset issued; caller MUST route to AbortHostStart, MUST NOT continue host()");
                    Reset();
                    throw new InvalidOperationException(
                        $"Stage6ASessionContext.BeginSession aborted: previous session {oldSessionId} not ended");
                }
                //   依据：U3-SDK Customization.FREE_CHARACTERS=1 + PRO_CHARACTERS=4 = 5 个槽位（0-4）。
                //   非 Pro 用户只能使用槽位 0，但此处仅做硬范围校验，Pro 权限由 vanilla 兜底。
                if (slot < 0 || slot > 4)
                {
                    RoleLogger.Error("[Host]", $"[Stage6A] BeginSession aborted: slot out of range; slot={slot}");
                    throw new InvalidOperationException(
                        $"Stage6ASessionContext.BeginSession aborted: slot {slot} out of valid range [0, 4]");
                }
                SessionId = Guid.NewGuid().ToString("N");
                HostMode = mode;
                CachedSlot = slot;
                StartAttempted = true;
                StartSucceeded = false;
                EndRecorded = false;
                ExitReason = null;
            }

            public static void MarkStartSucceeded()
            {
                if (SessionId == null) return;
                StartSucceeded = true;
            }

            public static void EndSession(string reason)
            {
                if (SessionId == null || EndRecorded) return;
                EndRecorded = true;
                ExitReason = reason;
            }

            public static void Reset()
            {
                SessionId = null;
                HostMode = EHostMode.None;
                CachedSlot = -1;
                StartAttempted = false;
                StartSucceeded = false;
                EndRecorded = false;
                ExitReason = null;
            }
        }

        /// <summary>
        ///   新增 targetWorldDirectory=/Worlds/<serverID>，区分根目录与目标世界目录。
        /// </summary>
        private static void LogStage6ASessionStart()
        {
            string hostSteamIdMasked = SteamUser.GetSteamID().m_SteamID.ToString();
            if (hostSteamIdMasked.Length > 6)
            {
                hostSteamIdMasked = hostSteamIdMasked.Substring(0, 3) + "..." +
                    hostSteamIdMasked.Substring(hostSteamIdMasked.Length - 3);
            }
            RoleLogger.Info("[Host]", $"[Stage6A-SessionStart] " +
                $"sessionId={Stage6ASessionContext.SessionId} " +
                $"hostMode={Stage6ASessionContext.HostMode} " +
                $"cachedSlot={Stage6ASessionContext.CachedSlot} " +
                $"serverID={Provider.serverID} " +
                $"map={Provider.map} " +
                $"hostSteamId={hostSteamIdMasked} " +
                $"savedataRoot={ServerSavedata.directory} " +
                $"targetWorldDirectory=/Worlds/{Provider.serverID} " +
                $"startSucceeded={Stage6ASessionContext.StartSucceeded} " +
                $"expectedWorldPath=/Worlds/{Provider.serverID}/Level/<LevelName>/ " +
                $"expectedPlayerPath=/Worlds/{Provider.serverID}/Players/<SteamID>_<CharID>/<LevelName>/ " +
                $"startedAt={DateTime.UtcNow:o}");
        }

        /// <summary>
        /// 退出日志不得宣称"保存完成"；保存成功必须以 SaveManager.onPostSave、文件证据和再次进入后的实际状态共同裁决。
        ///   stopPathEntered 仅在真实 Stop 路径为 true；cleanupPathEntered 在 Stop 或 Abort 任一清理路径均为 true。
        /// </summary>
        private static void LogStage6ASessionEnd(string reason)
        {
            Stage6ASessionContext.EndSession(reason);

            bool isStopPath = reason == "DisconnectCompleted";
            bool isCleanupPath = isStopPath || reason == "StartAbort";

            RoleLogger.Info("[Host]", $"[Stage6A-SessionEnd] " +
                $"sessionId={Stage6ASessionContext.SessionId} " +
                $"hostMode={Stage6ASessionContext.HostMode} " +
                $"cachedSlot={Stage6ASessionContext.CachedSlot} " +
                $"serverID={Provider.serverID} " +
                $"disconnectCompleted={isStopPath} " +
                $"stopPathEntered={isStopPath} " +
                $"cleanupPathEntered={isCleanupPath} " +
                $"exitReason={reason} " +
                $"sessionStartedAt=<see Start log> " +
                $"sessionEndedAt={DateTime.UtcNow:o} " +
                $"note=Save completion must be verified by SaveManager.onPostSave + file evidence + re-entry state");
        }

        /// <summary>
        ///   legacyServerId 使用掩码；新增 targetServerId=Singleplayer_<slot>；占位符替换为实际值。
        /// </summary>
        private static void DetectLegacyP2PSaveDirectory()
        {
            try
            {
                ulong hostSteamId = SteamUser.GetSteamID().m_SteamID;
                string hostSteamIdMasked = hostSteamId.ToString();
                if (hostSteamIdMasked.Length > 6)
                {
                    hostSteamIdMasked = hostSteamIdMasked.Substring(0, 3) + "..." +
                        hostSteamIdMasked.Substring(hostSteamIdMasked.Length - 3);
                }
                string legacyServerId = "P2P_" + hostSteamIdMasked;
                string relativePath = "Worlds/P2P_" + hostSteamId;
                string absolutePath = System.IO.Path.Combine(ReadWrite.PATH, relativePath);

                if (System.IO.Directory.Exists(absolutePath))
                {
                    RoleLogger.Warn("[Host]", $"[Stage6A-Legacy] Stage 6A 已将后续 P2P 会话的存档目标切换为当前 Singleplayer_<slot>；" +
                        $"检测到的历史 P2P_<SteamID> 数据尚未导入，插件不会自动迁移、覆盖、合并或删除。" +
                        $" legacyDirectoryExists=true" +
                        $" legacyServerId={legacyServerId}" +
                        $" targetServerId={Provider.serverID}");
                }
                else
                {
                    RoleLogger.Info("[Host]", $"[Stage6A-Legacy] 未检测到历史 P2P 存档目录。" +
                        $" legacyDirectoryExists=false" +
                        $" targetServerId={Provider.serverID}");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Host]", $"[Stage6A-Legacy] 检测历史目录失败：{ex.Message}");
            }
        }
    }
}
