using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using SDG.NetPak;
using SDG.NetTransport;
using SDG.Unturned;
using SteamP2PFriends.Client;
using SteamP2PFriends.Host;
using SteamP2PFriends.Patches;
using SteamP2PFriends.Shared;
using SteamP2PFriends.UI;
using Steamworks;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
namespace SteamP2PFriends
{
    /// <summary>
    /// SteamP2PFriends 的 BepInEx 入口。
    /// 该插件仅支持 SteamUser P2P listen-host：房主客户端同时承担原版服务端和本地客户端。
    /// 不启动 U3DS，不修改全局 Dedicated Server 判定，也不伪造原版加载完成状态。
    /// </summary>
    [BepInPlugin("com.yu80rice.steamp2pfriends", "SteamP2PFriends", "0.2.3.61")]
    [BepInDependency("com.yu80rice.launchinventorytidy", BepInDependency.DependencyFlags.SoftDependency)]
    public class SteamP2PFriendsPlugin : BaseUnityPlugin
    {
        public const string HARMONY_ID = "com.yu80rice.steamp2pfriends";

        public static SteamP2PFriendsPlugin Instance { get; private set; }

        public static ConfigEntry<bool> EnableP2PCoop;
        public static ConfigEntry<string> ServerName;
        public static ConfigEntry<byte> MaxPlayers;
        public static ConfigEntry<EGameMode> LastRoomMode;
        public static ConfigEntry<bool> LastRoomCheats;
        public static ConfigEntry<bool> LastRoomPvp;
        public static ConfigEntry<bool> LastRoomKeepInventory;
        public static ConfigEntry<bool> LastRoomKeepSkills;
        public static ConfigEntry<bool> LastRoomKeepExperience;
        public static ConfigEntry<string> GSLT_Login_Token;
        public static ConfigEntry<bool> VerboseLog;
        public static ConfigEntry<bool> RouteDiagnostics;

        public static bool DiagnosticBuildValid { get; private set; } = true;

        /// <summary>
        /// 由 Awake 中 RunRedactionSelfTest 设置，VerifyCriticalPatches 聚合到 DiagnosticBuildValid 阻断门。
        /// </summary>
        public static bool RedactionSelfTestPassed { get; private set; }

        /// <summary>
        /// 由 Awake 中 VerifyP2PWhitelistCaptureRegistration 设置，
        /// VerifyCriticalPatches 聚合到 DiagnosticBuildValid 阻断门。
        /// attribute 单测不足以证明激活；此处用 Harmony.GetPatchInfo 精确验证。
        /// </summary>
        public static bool CaptureRegistrationValid { get; private set; }

        public static bool Stage75TakeoverRegistrationValid { get; private set; }

        public static bool Stage76QuarantineRegistrationValid { get; private set; }
        public static bool Stage78UnifiedRegistrationValid { get; private set; }

        public static bool Stage92SinglePortRegistrationValid { get; private set; }

        public static bool Stage10WorldBroadcastActivationValid { get; private set; } = true;

        private Harmony _harmony;
        private bool _worldStatusActivationFailureLogged;

        private void Awake()
        {
            DontDestroyOnLoad(this.gameObject);
            this.gameObject.hideFlags = HideFlags.HideAndDontSave;

            Instance = this;

            EnableP2PCoop = Config.Bind("General", "EnableP2PCoop", true, "是否启用 P2P 好友联机");
            ServerName = Config.Bind("General", "ServerName", "P2P Co-op", "房主服务器名称");
            MaxPlayers = Config.Bind("General", "MaxPlayers", (byte)4, "最大玩家数（1-32）");
            LastRoomMode = Config.Bind("RoomDefaults", "Mode", EGameMode.EASY,
                "上一次成功启动的房间难度");
            LastRoomCheats = Config.Bind("RoomDefaults", "AllowCheats", true,
                "上一次成功启动的房间是否允许其他玩家使用作弊指令");
            LastRoomPvp = Config.Bind("RoomDefaults", "EnablePvp", false,
                "上一次成功启动的房间是否开启 PVP");
            LastRoomKeepInventory = Config.Bind("RoomDefaults", "KeepInventory", true,
                "上一次成功启动的房间是否死亡保留物品与装备");
            LastRoomKeepSkills = Config.Bind("RoomDefaults", "KeepSkills", true,
                "上一次成功启动的房间是否死亡保留技能等级");
            LastRoomKeepExperience = Config.Bind("RoomDefaults", "KeepExperience", true,
                "上一次成功启动的房间是否死亡保留经验");
            GSLT_Login_Token = Config.Bind("General", "GSLT_Login_Token", "",
                "[已弃用 v0.2.2] P2P 模式固定 SteamUser identity 路线，GSLT 不再参与运行。保留仅为向后兼容旧 cfg。");
            VerboseLog = Config.Bind("Debug", "VerboseDiagnostics", false, "输出额外的故障诊断日志（修改后重启生效）");
            RouteDiagnostics = Config.Bind("Debug", "RouteDiagnostics", false,
                "记录 Steam Networking Sockets 路由诊断；仅与 VerboseDiagnostics 同时开启时生效");

            RoleLogger.Initialize(Logger, VerboseLog.Value);

            // Bind configuration and enter Pending here; real subscription is deferred to Update.
            try
            {
                Stage10WorldBroadcastActivationValid = P2PWorldStatusBroadcaster.Initialize(Config);
                RoleLogger.Info("[Shared]",
                    "[WorldBroadcast] configuration bound; activation state=" +
                    P2PWorldStatusBroadcaster.ActivationState);
            }
            catch (System.Exception wbEx)
            {
                Stage10WorldBroadcastActivationValid = false;
                RoleLogger.Error("[Shared]", "[WorldBroadcast] initialize threw: " + wbEx.GetType().Name);
            }

            RoleLogger.Info("[Shared]",
                $"[Startup] version=0.2.3.61 p2pEnabled={EnableP2PCoop.Value} " +
                $"verboseDiagnostics={VerboseLog.Value} routeDiagnostics={RouteDiagnostics.Value} " +
                $"worldStatus={P2PWorldStatusBroadcaster.ActivationState}");

            try
            {
                SteamRuntime.EnsureInitialized();
                RoleLogger.Info("[Shared]", "[SteamRuntime] 初始化完成");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"SteamRuntime 初始化失败: {ex}");
            }

            try
            {
                _harmony = new Harmony(HARMONY_ID);
                _harmony.PatchAll(typeof(SteamP2PFriendsPlugin).Assembly);
                RoleLogger.Info("[Shared]", "[Harmony] PatchAll 已执行（不保证所有 patch 登记成功）");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"Harmony.PatchAll 失败: {ex}");
            }

            ApplyManualWrapperPatches();
            ApplyManualDiagnosticPatches();

            try
            {
                P2PQuarantineReadyToConnectScopePatch.RegisterManual(_harmony);
                P2PQuarantineServerInvokeGatePatch.RegisterManual(_harmony);
                P2PPlayerListApprovalDecorator.RegisterManual(_harmony);
                P2PListenHostCommandPermissionPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", "[Stage7-6] manual registration failed: " + ex);
            }

            try
            {
                Patches.AssetIntegritySnapshotPatch.RuntimeProbe();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"AssetIntegritySnapshotPatch.RuntimeProbe 失败: {ex}");
            }

            //   1. _harmony.GetPatchedMethods() + Harmony.GetPatchInfo() 双重验证
            //   2. Server-side Prefix 签名验证（methodInfo.DeclaringType + GetParameters()）
            //   3. 整体 try-catch 包裹，任一登记失败不阻断 Plugin.Awake 后续步骤
            try
            {
                RegisterAssetIntegritySnapshotPatches();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"RegisterAssetIntegritySnapshotPatches 整体异常（不阻断）: {ex}");
            }

            ApplyV2AuditFixPatches();

            //   （与 Patches/P0EDiagnostic/UseableBarricadeDiagnosticPatch.cs:99 同一模式）
            //   仅在 Awake 中登记一次，避免插件重载时重复登记。
            //   ResetCallbackRegistered 状态纳入 DiagnosticBuildValid。
            try
            {
                Patches.WorldSyncDiagnosticCore.RegisterSessionResetCallback(
                    Patches.P0EBarricadeLifecycle.BarricadeLifecycleHelper.ResetHitLogs);
                Patches.P0EBarricadeLifecycle.BarricadeLifecycleRegistration.MarkResetCallbackRegistered();
                RoleLogger.Info("[Shared]",
                    "[5B-1B/Plugin] OK RegisterSessionResetCallback(BarricadeLifecycleHelper.ResetHitLogs) 已登记 + MarkResetCallbackRegistered");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]",
                    $"[5B-1B/Plugin] RegisterSessionResetCallback 失败: {ex.Message}");
            }

            //   仅两个 Transpiler，不全局伪造 Dedicator.IsDedicatedServer
            //   失败 fail-closed（DiagnosticBuildValid=false），不影响其他 patch
            try
            {
                Patches.P0EBarricadeLifecycle.BarricadeLifecycleRegistration.RegisterAtomically(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]",
                    $"[5B-1B/Plugin] BarricadeLifecycleRegistration.RegisterAtomically 异常: {ex.Message}");
            }

            // 否则 IsSubscribed=false 必然触发 DIAGNOSTIC BUILD INVALID
            try
            {
                Patches.UnityLogBridgePatch.Initialize();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"UnityLogBridgePatch.Initialize 失败: {ex}");
            }

            // 原生 SNS 输出仅用于受控诊断，失败不得改变 P2P 功能门控。
            try
            {
                SteamP2PFriends.Client.NativeSnsLogProbe.Enable(RouteDiagnostics.Value, VerboseLog.Value);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"NativeSnsLogProbe.Enable 失败: {ex}");
            }

            //   异常和任一 FAIL 都视为失败，聚合到 DiagnosticBuildValid 阻断门
            //   不允许通过关闭诊断配置绕过
            bool redactionSelfTestPassed = false;
            try
            {
                redactionSelfTestPassed = SteamP2PFriends.Shared.SnsDiagnosticUtil.RunRedactionSelfTest();
                RedactionSelfTestPassed = redactionSelfTestPassed;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"RunRedactionSelfTest 异常（视为 FAIL）: {ex.Message}");
                RedactionSelfTestPassed = false;
                redactionSelfTestPassed = false;
            }

            //   Provider.reject(ITransportConnection, ESteamRejection) 的 Prefix 恰为
            //   P2PWhitelistRequestCapturePatch.Prefix 且 owner == HARMONY_ID。
            //   失败则 P2P fail-closed（DiagnosticBuildValid=false）。attribute 单测不足以证明激活。
            bool captureRegistrationOk = false;
            try
            {
                captureRegistrationOk = VerifyP2PWhitelistCaptureRegistration();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"VerifyP2PWhitelistCaptureRegistration 异常（视为 FAIL）: {ex}");
                captureRegistrationOk = false;
            }
            CaptureRegistrationValid = captureRegistrationOk;

            bool stage75RegistrationOk = false;
            try
            {
                stage75RegistrationOk = VerifyStage75TakeoverRegistrations();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", "VerifyStage75TakeoverRegistrations 异常（视为 FAIL）: " + ex);
                stage75RegistrationOk = false;
            }
            Stage75TakeoverRegistrationValid = stage75RegistrationOk;

            Stage76QuarantineRegistrationValid =
                P2PQuarantineReadyToConnectScopePatch.RegistrationValid &&
                P2PQuarantineServerInvokeGatePatch.RegistrationValid &&
                P2PPlayerListApprovalDecorator.RegistrationValid &&
                VerifyStage76AttributeRegistrations() &&
                P2PQuarantineAdmissionService.IsSignalBitCompatible();
            if (!Stage76QuarantineRegistrationValid)
            {
                RoleLogger.Error("[Shared]",
                    "[Stage7-6] !!! quarantine registration/signal compatibility gate failed");
            }

            Stage78UnifiedRegistrationValid = VerifyStage78UnifiedConnectRegistrations() &&
                P2PListenHostCommandPermissionPatch.RegistrationValid;
            if (!Stage78UnifiedRegistrationValid)
            {
                RoleLogger.Error("[Shared]",
                    "[Stage7-8] !!! unified connect route/indicator registration gate failed");
            }

            Stage92SinglePortRegistrationValid = VerifyStage92SinglePortRegistration();
            if (!Stage92SinglePortRegistrationValid)
            {
                RoleLogger.Error("[Shared]",
                    "[Stage9-2] !!! single-port Direct-IP query projection registration gate failed");
            }

            HarmonyCompatibilityAudit.Reset();
            VerifyCriticalPatches(redactionSelfTestPassed);

            //   旧实现无条件初始化 P2PLobbyManager/ClientLobbyListener/P2PJoinManager，
            //   即使 DiagnosticBuildValid=false 也允许 P2P 入口存在，违反审计要求。
            if (!DiagnosticBuildValid)
            {
                RoleLogger.Error("[Shared]",
                    "[Shared] !!! DiagnosticBuildValid=false，跳过 P2P-Lobby/ClientLobbyListener/P2PJoinManager 初始化（P0-1 INVALID 门控）!!!");
                return;
            }

            try
            {
                P2PLobbyManager.Initialize();
                ClientLobbyListener.Initialize();
                P2PJoinManager.Initialize();
                RoleLogger.Info("[Shared]", "[P2P-Lobby] P2PLobbyManager + ClientLobbyListener + P2PJoinManager 已初始化");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"Lobby 初始化失败: {ex}");
            }

            //   定期采样远程玩家 GameObject 渲染状态，收集房主看不到客机模型的证据。
            //   不静默跳过 PlayerConnected loopback NotSupportedException，不直接调用 ClientMessageHandler.ReadMessage。
            try
            {
                SteamP2PFriends.Host.RemotePlayerRenderProbe.Initialize();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"RemotePlayerRenderProbe.Initialize 失败: {ex}");
            }

            try
            {
                SteamP2PFriends.Client.ClientRemotePlayerRenderProbe.Initialize();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"ClientRemotePlayerRenderProbe.Initialize 失败: {ex}");
            }

            //   订阅 Provider.onEnemyDisconnected(SteamPlayer) 替代 onClientDisconnected。
            //   vanilla Provider.onClientDisconnected 是本地客机断开事件（房主端不触发），
            //   onEnemyDisconnected(SteamPlayer) 才是远端玩家断开事件（房主端触发）。
            //   修复：ListenRegionSync 计数器 + RenderProbe 状态在客机断开后未清除，
            //         导致同一 SteamID 重连后丢失诊断日志（计数器不复位导致后续日志被静默）。
            //   保留 onClientDisconnected 订阅作为 fallback（客机端自己断开时仍清除本地状态）。
            try
            {
                Provider.onEnemyDisconnected += OnEnemyDisconnectedHandler;
                RoleLogger.Info("[Shared]", "[Shared] 已订阅 Provider.onEnemyDisconnected（RegionSync/RenderProbe 计数代次复位 - 远端玩家断开）");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"订阅 Provider.onEnemyDisconnected 失败: {ex}");
            }

            try
            {
                Provider.onClientDisconnected += OnClientDisconnectedHandler;
                RoleLogger.Info("[Shared]", "[Shared] 已订阅 Provider.onClientDisconnected（fallback - 本地客机断开）");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"订阅 Provider.onClientDisconnected 失败: {ex}");
            }
        }

        /// <summary>
        /// 触发三个 patch 的 OnClientDisconnected，清除已断线 SteamID 的计数/状态。
        /// </summary>
        private static void OnEnemyDisconnectedHandler(SteamPlayer player)
        {
            try
            {
                ulong steamId = 0;
                try
                {
                    if (!ReferenceEquals(player, null) && !ReferenceEquals(player.playerID, null))
                    {
                        steamId = player.playerID.steamID.m_SteamID;
                    }
                }
                catch { }

                if (steamId != 0UL)
                {
                    // callback BEFORE quarantine cleanup. The broadcaster reads its own session
                    // projection state (registered only for AlreadyApproved/Activated) and cleans up
                    // player-level state unconditionally. No second Provider.onEnemyDisconnected
                    // subscription is added.
                    try { P2PWorldStatusBroadcaster.OnPlayerDisconnected(player); }
                    catch (System.Exception bdEx)
                    {
                        RoleLogger.Warn("[Host]",
                            "[WorldBroadcast] disconnect forward failed: " + bdEx.GetType().Name);
                    }

                    try { P2PQuarantineAdmissionService.OnDisconnected(new CSteamID(steamId)); }
                    catch (System.Exception qEx)
                    {
                        RoleLogger.Warn("[Host]",
                            "[P2P-Quarantine] disconnect cleanup failed: " + qEx.GetType().Name);
                    }
                }

                // 审计明确：OnEnemyDisconnectedHandler 当前没有调用 RemotePlayerClothingVisibleBridgePatch.OnClientDisconnected()，
                // 字典在客机断开后不会被清理。此日志用于 4C 离线证明该断线清理缺陷。
                // R2：增加 contained=<bool> 字段，证明断开的 SteamID 是否仍存在字典中（多客机场景下避免错误归因）。
                int p0s3RetryCount = 0;
                bool p0s3Contained = false;
                try
                {
                    p0s3RetryCount = Patches.RemotePlayerClothingVisibleBridgePatch.RetryStatesCount;
                    p0s3Contained = Patches.RemotePlayerClothingVisibleBridgePatch.ContainsRetryState(steamId);
                }
                catch { }
                RoleLogger.Info("[Shared]",
                    $"[P0-S3] E-4 OnEnemyDisconnectedHandler 入口观察 " +
                    $"steamId={DiagnosticMaskUtil.MaskSteamId(steamId)} contained={p0s3Contained} retryStatesCount={p0s3RetryCount}");

                //   单 SteamID 事件驱动清理。仅删除指定 SteamID 的 RetryState，
                //   不影响其他在线玩家。Tick 同主线程访问，无需锁。
                try
                {
                    Patches.RemotePlayerClothingVisibleBridgePatch.RemoveRetryState(steamId);
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"OnEnemyDisconnectedHandler(P0-S3 RemoveRetryState) 异常: {ex.Message}");
                }

                RoleLogger.Info("[Shared]",
                    $"[P1] onEnemyDisconnected 触发 steamId={DiagnosticMaskUtil.MaskSteamId(steamId)}（清除 RegionSync/RenderProbe 计数）");

                Patches.BarricadeManagerRegionSyncPatch.OnClientDisconnected();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"OnEnemyDisconnectedHandler(Barricade) 异常: {ex.Message}");
            }

            try
            {
                Patches.StructureManagerRegionSyncPatch.OnClientDisconnected();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"OnEnemyDisconnectedHandler(Structure) 异常: {ex.Message}");
            }

            try
            {
                SteamP2PFriends.Host.RemotePlayerRenderProbe.OnClientDisconnected();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"OnEnemyDisconnectedHandler(RenderProbe) 异常: {ex.Message}");
            }

            try
            {
                SteamP2PFriends.Client.ClientRemotePlayerRenderProbe.OnClientDisconnected();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"OnEnemyDisconnectedHandler(ClientRenderProbe) 异常: {ex.Message}");
            }

            try
            {
                Patches.PlayerMovementTellStateDiagnosticPatch.OnClientDisconnected();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"OnEnemyDisconnectedHandler(D-Vis-9) 异常: {ex.Message}");
            }

            try
            {
                Patches.PlayerMovementTellStateCallerDiagnosticPatch.OnClientDisconnected();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"OnEnemyDisconnectedHandler(D-Vis-15) 异常: {ex.Message}");
            }
            try
            {
                Patches.NetMessageDeliveryPathDiagnosticPatch.OnClientDisconnected();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"OnEnemyDisconnectedHandler(D-Vis-16) 异常: {ex.Message}");
            }

            // D-Vis-8 节流状态复位
            try
            {
                Patches.SteamChannelTransportDiagnosticPatch.OnClientDisconnected();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"OnEnemyDisconnectedHandler(D-Vis-8) 异常: {ex.Message}");
            }

            try
            {
                Patches.PlayerManagerBroadcastPatch.OnClientDisconnected();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"OnEnemyDisconnectedHandler(P0-S2) 异常: {ex.Message}");
            }
            try
            {
                Patches.PlayerManagerBroadcastDiagnosticPatch.OnClientDisconnected();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"OnEnemyDisconnectedHandler(P1-S5) 异常: {ex.Message}");
            }

            try
            {
                Patches.WorldSyncDiagnosticCore.OnClientDisconnected();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"OnEnemyDisconnectedHandler(WorldSyncDiag) 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 触发三个 patch 的 OnClientDisconnected，清除已断线 SteamID 的计数/状态。
        /// </summary>
        private static void OnClientDisconnectedHandler()
        {
            try
            {
                Patches.BarricadeManagerRegionSyncPatch.OnClientDisconnected();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"BarricadeManagerRegionSyncPatch.OnClientDisconnected 异常: {ex.Message}");
            }

            try
            {
                Patches.StructureManagerRegionSyncPatch.OnClientDisconnected();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"StructureManagerRegionSyncPatch.OnClientDisconnected 异常: {ex.Message}");
            }

            try
            {
                SteamP2PFriends.Host.RemotePlayerRenderProbe.OnClientDisconnected();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"RemotePlayerRenderProbe.OnClientDisconnected 异常: {ex.Message}");
            }

            // D-Vis-8 节流状态复位
            try
            {
                Patches.SteamChannelTransportDiagnosticPatch.OnClientDisconnected();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"SteamChannelTransportDiagnosticPatch.OnClientDisconnected 异常: {ex.Message}");
            }

            try
            {
                Patches.PlayerManagerBroadcastPatch.OnClientDisconnected();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"PlayerManagerBroadcastPatch.OnClientDisconnected 异常: {ex.Message}");
            }
            try
            {
                Patches.PlayerManagerBroadcastDiagnosticPatch.OnClientDisconnected();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"PlayerManagerBroadcastDiagnosticPatch.OnClientDisconnected 异常: {ex.Message}");
            }

            try
            {
                Patches.WorldSyncDiagnosticCore.OnClientDisconnected();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"WorldSyncDiagnosticCore.OnClientDisconnected 异常: {ex.Message}");
            }
        }

        private void Update()
        {
            // self-heal once Unturned's game thread registration becomes available.
            try
            {
                bool stage10Ok = P2PWorldStatusBroadcaster.TryActivateOnGameThread();
                Stage10WorldBroadcastActivationValid = stage10Ok;
                if (stage10Ok)
                {
                    _worldStatusActivationFailureLogged = false;
                }
                else
                {
                    DiagnosticBuildValid = false;
                    if (!_worldStatusActivationFailureLogged)
                    {
                        _worldStatusActivationFailureLogged = true;
                        RoleLogger.Error("[Shared]",
                            "[WorldBroadcast] activation failed on game thread; P2P entry disabled fail-closed");
                    }
                }
            }
            catch (System.Exception ex)
            {
                Stage10WorldBroadcastActivationValid = false;
                DiagnosticBuildValid = false;
                if (!_worldStatusActivationFailureLogged)
                {
                    _worldStatusActivationFailureLogged = true;
                    RoleLogger.Error("[Shared]",
                        "[WorldBroadcast] activation threw: " + ex.GetType().Name);
                }
            }

            // If Unturned has not registered its game thread yet, do not run the rest of the
            // plugin Update (which contains its own assertIsGameThread contract). A later frame
            // retries activation, so this is a temporary startup wait rather than a permanent hang.
            if (P2PWorldStatusBroadcaster.ShouldSuspendPluginUpdate)
                return;

            if (!DiagnosticBuildValid)
            {
                //   若重试成功且其他自检通过，下次 Awake 重新加载时 DiagnosticBuildValid 可恢复 true
                //   当前会话内 DiagnosticBuildValid 不会变，但至少 NativeSnsLogProbe 能启用供诊断
                try
                {
                    SteamP2PFriends.Client.NativeSnsLogProbe.RetryEnableIfSteamworksReady();
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Shared]", $"[P2P-Update] NativeSnsLogProbe retry 异常: {ex.Message}");
                }
                return;
            }
            if (!EnableP2PCoop.Value) return;

            //   业务 drain 不得依赖 HUD 是否创建；此断言把主线程契约固化到接口边界。
            ThreadUtil.assertIsGameThread();

            try
            {
                SteamP2PFriends.Client.NativeSnsLogProbe.RetryEnableIfSteamworksReady();
                SteamGameServerCallbacksWatcher.Tick();
                HasCheatsGuardWatcher.Tick();
                HostManager.TickListen();
                P2PLobbyManager.Tick();
                P2PJoinManager.Tick();
                // 指令 G: must run from Plugin.Update, not inside any UI Tick/CanTouchClientUi block.
                SteamP2PFriends.Client.ExplicitDnsDirectIpService.Tick();

                //   不可由 PlayerUI/MenuUI 是否创建决定是否消费 reject queue。
                //   HUD 未创建 / parent=null / CanTouchClientUi=false 时，pending 仍须登记。
                //   仅在 P2P 房主活动时 drain；不活动时不得消费。
                //   原版 WHITELISTED 拒绝保持不变；此修复不放宽任何陌生玩家准入。
                if (HostManager.IsP2PHostMode && Provider.isServer && Provider.isWhitelisted)
                {
                    SteamPersonaDisplay.DrainObservedCharacterNamesOnMainThread();
                    P2PQuarantineAdmissionService.Tick();
                }

                //   U3DS / Dedicated 探测 fail-closed 时不触碰任何 UI 类型属性。
                //   Tick 自己 EnsureCreated（含 parent identity 生命周期检查）。
                if (SteamP2PFriends.Shared.P2PClientUiEnvironment.CanTouchClientUi())
                {
                    P2PNativeMenuUI.Tick();
                    if (!HostManager.IsP2PHostMode)
                    {
                        P2PQuarantineClientView.Tick();
                    }
                }
                NativeLoadingGateDumper.Tick();
                SteamP2PFriends.Shared.ConnectionLifecycleTracker.Tick();
                SteamP2PFriends.Client.NativeSnsLogProbe.Tick();
                //   0.5s 检查间隔，采样时间点 0/1/3/10/30/60s + 状态变化驱动（active/renderer/position 变化时额外采样）
                SteamP2PFriends.Host.RemotePlayerRenderProbe.Tick();
                //   弥补 D-Vis-6 客机端 0 次的设计限制（Provider.isServer=false 时才运行）
                SteamP2PFriends.Client.ClientRemotePlayerRenderProbe.Tick();
                //   Player.InitializePlayer Postfix 登记重试任务，本 Tick 在 0/1/3s 时机尝试 NotifyClothingIsVisible
                Patches.RemotePlayerClothingVisibleBridgePatch.Tick();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Shared]", $"[P2P-Update] Tick 异常: {ex.Message}");
            }
        }

        // 旧 HostSteamIdDisplayService / SteamIdInputModal / P2PWhitelistModal 的 IMGUI 调用已全部删除。

        private void OnDestroy()
        {
            try
            {
                try
                {
                    Provider.onClientDisconnected -= OnClientDisconnectedHandler;
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"解订阅 Provider.onClientDisconnected 失败: {ex.Message}");
                }

                P2PLobbyManager.Shutdown();
                ClientLobbyListener.Shutdown();
                P2PJoinManager.Shutdown();
                // destroy the DNS toggle UI (restoring the vanilla connect button Y).
                SteamP2PFriends.Client.ExplicitDnsDirectIpService.Shutdown();
                try { P2PWorldStatusBroadcaster.Shutdown(); }
                catch (System.Exception wbEx) { RoleLogger.Warn("[Shared]", "[WorldBroadcast] Shutdown 异常: " + wbEx.GetType().Name); }
                Patches.UnityLogBridgePatch.Shutdown();
                SteamP2PFriends.Shared.ConnectionLifecycleTracker.Shutdown();
                SteamP2PFriends.Client.NativeSnsLogProbe.Disable();
                SteamP2PFriends.Host.RemotePlayerRenderProbe.Shutdown();
                SteamP2PFriends.Client.ClientRemotePlayerRenderProbe.Shutdown();
                Patches.UnityTagErrorSourceDiagnosticPatch.Shutdown();
                Patches.PlayerLifecycleReadyDiagnosticPatch.Shutdown();
                Patches.BarricadeManagerRegionSyncPatch.ResetAll();
                Patches.StructureManagerRegionSyncPatch.ResetAll();
                Patches.ItemManagerRegionSyncPatch.ResetAll();
                Patches.ResourceManagerRegionSyncPatch.ResetAll();
                Patches.ObjectManagerRegionSyncPatch.ResetAll();
                SteamP2PFriends.Host.RemotePlayerRenderProbe.ResetAll();
                SteamP2PFriends.Client.ClientRemotePlayerRenderProbe.ResetAll();
                Patches.WorldSyncDiagnosticCore.ResetAll();
                try { P2PNativeMenuUI.Destroy(); } catch (System.Exception ex) { RoleLogger.Warn("[Shared]", $"[P2P] P2PNativeMenuUI.Destroy 异常: {ex.Message}"); }
                try { P2PQuarantineClientView.Destroy(); } catch (System.Exception ex) { RoleLogger.Warn("[Shared]", $"[P2P] P2PQuarantineClientView.Destroy 异常: {ex.Message}"); }
                try { P2PQuarantineAdmissionService.ResetForSession(); } catch (System.Exception ex) { RoleLogger.Warn("[Shared]", $"[P2P] P2PQuarantineAdmissionService.Reset 异常: {ex.Message}"); }
                _harmony?.UnpatchSelf();
                RoleLogger.Info("[Shared]", "[P2P] SteamP2PFriends 已卸载");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P2P-OnDestroy] 异常: {ex}");
            }
        }

        /// <summary>
        /// PatchAll 后用 Harmony.GetPatchInfo 精确验证 Provider.reject(ITransportConnection, ESteamRejection)
        /// 的 Prefix 恰为 P2PWhitelistRequestCapturePatch.Prefix 且 owner == HARMONY_ID。
        /// 失败则 DiagnosticBuildValid=false（由 VerifyCriticalPatches 聚合 CaptureRegistrationValid）。
        /// attribute 单测不足以证明激活；此处读取运行时 patch info。
        /// </summary>
        private bool VerifyP2PWhitelistCaptureRegistration()
        {
            MethodInfo original = AccessTools.Method(typeof(Provider), "reject",
                new[] { typeof(ITransportConnection), typeof(ESteamRejection) });
            MethodInfo expectedPrefix = AccessTools.Method(typeof(P2PWhitelistRequestCapturePatch), "Prefix");
            HarmonyLib.Patches info = original == null ? null : Harmony.GetPatchInfo(original);
            bool installed = false;
            if (info != null)
            {
                foreach (Patch prefix in info.Prefixes)
                {
                    if (prefix.owner == HARMONY_ID && prefix.PatchMethod == expectedPrefix)
                    {
                        installed = true;
                        break;
                    }
                }
            }
            if (!installed)
            {
                RoleLogger.Error("[Shared]",
                    "[P2P-Approval] !!! Provider.reject capture Prefix absent; P2P fail-closed " +
                    $"(original={(original == null ? "null" : original.Name)}, " +
                    $"info={(info == null ? "null" : info.Prefixes.Count.ToString() + " prefixes")})");
            }
            return installed;
        }

        private bool VerifyStage75TakeoverRegistrations()
        {
            MethodInfo identityPostfix = AccessTools.Method(typeof(P2PPendingIdentityCapturePatch), "Postfix");
            ConstructorInfo pendingConstructor = null;
            ConstructorInfo[] constructors = typeof(SteamPending).GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < constructors.Length; i++)
            {
                ParameterInfo[] parameters = constructors[i].GetParameters();
                if (parameters.Length > 1 && parameters[1].ParameterType == typeof(SteamPlayerID))
                {
                    if (pendingConstructor != null)
                    {
                        RoleLogger.Error("[Shared]", "[Stage7-5] multiple SteamPending identity constructors; fail-closed");
                        return false;
                    }
                    pendingConstructor = constructors[i];
                }
            }

            MethodInfo groupOriginal = AccessTools.Method(typeof(PlayerQuests), nameof(PlayerQuests.ReceiveGroupState),
                new[] { typeof(CSteamID), typeof(EPlayerGroupRank) });
            MethodInfo groupPrefix = AccessTools.Method(typeof(P2PGroupStateProbe_ReceiveGroupState), "Prefix");
            MethodInfo groupPostfix = AccessTools.Method(typeof(P2PGroupStateProbe_ReceiveGroupState), "Postfix");
            MethodInfo rejectIdentityOriginal = AccessTools.Method(typeof(Provider), "reject",
                new[] { typeof(ITransportConnection), typeof(ESteamRejection), typeof(string) });
            MethodInfo rejectIdentityPrefix = AccessTools.Method(typeof(P2PRejectPendingIdentityCapturePatch), "Prefix");

            bool identityOk = HasOwnedPatch(pendingConstructor, identityPostfix, false);
            bool rejectIdentityOk = HasOwnedPatch(rejectIdentityOriginal, rejectIdentityPrefix, true);
            bool groupPrefixOk = HasOwnedPatch(groupOriginal, groupPrefix, true);
            bool groupPostfixOk = HasOwnedPatch(groupOriginal, groupPostfix, false);
            bool allOk = identityOk && rejectIdentityOk && groupPrefixOk && groupPostfixOk;

            if (allOk)
            {
                RoleLogger.Info("[Shared]",
                    "[Stage7-5] OK identity capture + reject-time fallback + group probe registered");
            }
            else
            {
                RoleLogger.Error("[Shared]", "[Stage7-5] !!! DIAGNOSTIC BUILD INVALID: takeover patch missing" +
                    " identity=" + identityOk +
                    " rejectIdentity=" + rejectIdentityOk +
                    " groupPrefix=" + groupPrefixOk +
                    " groupPostfix=" + groupPostfixOk);
            }
            return allOk;
        }

        private bool HasOwnedPatch(MethodBase original, MethodInfo expectedPatch, bool prefix)
        {
            if (original == null || expectedPatch == null) return false;
            HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
            if (info == null) return false;
            IEnumerable<Patch> patches = prefix ? info.Prefixes : info.Postfixes;
            foreach (Patch patch in patches)
            {
                if (patch.owner == HARMONY_ID && patch.PatchMethod == expectedPatch) return true;
            }
            return false;
        }

        private bool VerifyStage76AttributeRegistrations()
        {
            MethodInfo whitelistOriginal = AccessTools.Method(typeof(SteamWhitelist),
                nameof(SteamWhitelist.checkWhitelisted), new[] { typeof(CSteamID) });
            MethodInfo whitelistPostfix = AccessTools.Method(
                typeof(P2PQuarantineWhitelistPermitPatch), nameof(P2PQuarantineWhitelistPermitPatch.Postfix));

            MethodInfo damageOriginal = AccessTools.Method(typeof(PlayerLife), nameof(PlayerLife.askDamage),
                new[]
                {
                    typeof(byte), typeof(Vector3), typeof(EDeathCause), typeof(ELimb), typeof(CSteamID),
                    typeof(EPlayerKill).MakeByRefType(), typeof(bool), typeof(ERagdollEffect), typeof(bool), typeof(bool)
                });
            MethodInfo damagePrefix = AccessTools.Method(
                typeof(P2PQuarantineDamageGuardPatch), nameof(P2PQuarantineDamageGuardPatch.Prefix));

            MethodInfo inputOriginal = AccessTools.Method(typeof(PlayerInput), "FixedUpdate");
            MethodInfo inputPrefix = AccessTools.Method(
                typeof(P2PQuarantineClientInputPatch), nameof(P2PQuarantineClientInputPatch.Prefix));

            bool permitOk = HasOwnedPatch(whitelistOriginal, whitelistPostfix, false);
            bool damageOk = HasOwnedPatch(damageOriginal, damagePrefix, true);
            bool inputOk = HasOwnedPatch(inputOriginal, inputPrefix, true);
            if (!permitOk || !damageOk || !inputOk)
            {
                RoleLogger.Error("[Shared]",
                    "[Stage7-6] attribute patch missing permit=" + permitOk +
                    " damage=" + damageOk + " input=" + inputOk);
            }
            return permitOk && damageOk && inputOk;
        }

        private bool VerifyStage78UnifiedConnectRegistrations()
        {
            MethodBase routeOriginal = Patches.MenuPlayConnectP2PRoutePatch.TargetMethod();
            MethodInfo routePrefix = AccessTools.Method(typeof(Patches.MenuPlayConnectP2PRoutePatch), "Prefix");
            MethodBase indicatorOriginal = Patches.MenuPlayConnectP2PIndicatorPatch.TargetMethod();
            MethodInfo indicatorPostfix = AccessTools.Method(typeof(Patches.MenuPlayConnectP2PIndicatorPatch), "Postfix");

            bool routeOk = HasOwnedPatch(routeOriginal, routePrefix, true);
            bool indicatorOk = HasOwnedPatch(indicatorOriginal, indicatorPostfix, false);
            if (!routeOk || !indicatorOk)
            {
                RoleLogger.Error("[Shared]", "[Stage7-8] patch missing route=" + routeOk +
                    " indicator=" + indicatorOk);
            }
            else
            {
                RoleLogger.Info("[Shared]", "[Stage7-8] OK vanilla connect SteamID route + indicator registered");
            }
            return routeOk && indicatorOk;
        }

        /// <summary>
        /// under our Harmony owner with the exact Postfix MethodInfo (not just attribute presence).
        /// Failure aggregates into DiagnosticBuildValid=false.
        /// </summary>
        private bool VerifyStage92SinglePortRegistration()
        {
            MethodBase original = Patches.DirectIpSinglePortQueryPortPatch.TargetMethod();
            MethodInfo postfix = AccessTools.Method(
                typeof(Patches.DirectIpSinglePortQueryPortPatch),
                nameof(Patches.DirectIpSinglePortQueryPortPatch.Postfix));

            bool ok = HasOwnedPatch(original, postfix, false);
            if (!ok)
            {
                RoleLogger.Error("[Shared]",
                    "[Stage9-2] Direct-IP single-port query projection patch missing " +
                    "(original=" + (original == null ? "null" : original.Name) +
                    " postfix=" + (postfix == null ? "null" : postfix.Name) + ")");
            }
            else
            {
                RoleLogger.Info("[Shared]",
                    "[Stage9-2] OK single-port Direct-IP query projection registered");
            }
            return ok;
        }

        private bool VerifyStage10DeathCommitRegistration()
        {
            MethodBase original = Patches.P2PWorldDeathCommitPatch.TargetMethod();
            MethodInfo prefix = AccessTools.Method(typeof(Patches.P2PWorldDeathCommitPatch),
                nameof(Patches.P2PWorldDeathCommitPatch.Prefix));
            MethodInfo postfix = AccessTools.Method(typeof(Patches.P2PWorldDeathCommitPatch),
                nameof(Patches.P2PWorldDeathCommitPatch.Postfix));

            bool ok = HasOwnedPatch(original, prefix, true) &&
                      HasOwnedPatch(original, postfix, false);
            if (ok)
                RoleLogger.Info("[Shared]",
                    "[Stage10] OK authoritative death commit fallback registered");
            else
                RoleLogger.Error("[Shared]",
                    "[Stage10] authoritative death commit fallback registration missing");
            return ok;
        }

        private bool VerifyStage10ChatAvatarRegistration()
        {
            MethodInfo prefix = AccessTools.Method(typeof(Patches.P2PWorldChatAvatarPatch),
                nameof(Patches.P2PWorldChatAvatarPatch.PrefixProject));
            MethodInfo v1 = AccessTools.PropertySetter(typeof(SleekChatEntryV1),
                "representingChatMessage");
            MethodInfo v2 = AccessTools.PropertySetter(typeof(SleekChatEntryV2),
                "representingChatMessage");
            bool ok = HasOwnedPatch(v1, prefix, true) && HasOwnedPatch(v2, prefix, true);
            if (ok)
                RoleLogger.Info("[Shared]", "[Stage10] OK chat avatar projection registered V1+V2");
            else
                RoleLogger.Error("[Shared]", "[Stage10] chat avatar projection registration missing");
            return ok;
        }

        private bool VerifyInventoryUiProjectionRegistration()
        {
            System.Type[] dragArgs =
            {
                typeof(byte), typeof(byte), typeof(byte), typeof(byte),
                typeof(byte), typeof(byte), typeof(byte)
            };
            System.Type[] swapArgs =
            {
                typeof(byte), typeof(byte), typeof(byte), typeof(byte),
                typeof(byte), typeof(byte), typeof(byte), typeof(byte)
            };
            System.Type[] dropArgs = { typeof(byte), typeof(byte), typeof(byte) };

            MethodInfo drag = AccessTools.Method(typeof(PlayerInventory),
                nameof(PlayerInventory.ReceiveDragItem), dragArgs);
            MethodInfo swap = AccessTools.Method(typeof(PlayerInventory),
                nameof(PlayerInventory.ReceiveSwapItem), swapArgs);
            MethodInfo drop = AccessTools.Method(typeof(PlayerInventory),
                nameof(PlayerInventory.ReceiveDropItem), dropArgs);

            bool ok = Patches.ListenHostInventoryUiProjectionPatch.ReflectionContractAvailable &&
                      HasOwnedPatch(drag,
                          Patches.ListenHostInventoryUiProjectionPatch.DragPostfix, false) &&
                      HasOwnedPatch(swap,
                          Patches.ListenHostInventoryUiProjectionPatch.SwapPostfix, false) &&
                      HasOwnedPatch(drop,
                          Patches.ListenHostInventoryUiProjectionPatch.DropPostfix, false);
            if (ok)
                RoleLogger.Info("[Shared]",
                    "[InventoryUI-Reconcile] OK listen-host drag/swap/drop projection repair registered");
            else
                RoleLogger.Error("[Shared]",
                    "[InventoryUI-Reconcile] projection repair registration or reflection contract missing");
            return ok;
        }

        /// <summary>
        /// 任一关键 Patch 缺失时输出 DIAGNOSTIC BUILD INVALID 并禁用所有 P2P 入口。
        ///   （declaring type + method name），不只依赖 owner + 数量。
        /// </summary>
        private void VerifyCriticalPatches(bool redactionSelfTestPassed)
        {
            // 若 Awake 中 UnityLogBridgePatch.Initialize 漏调或顺序错误，此处兜底
            if (!Patches.UnityLogBridgePatch.IsSubscribed && !Patches.UnityLogBridgePatch.IsFailed)
            {
                try
                {
                    Patches.UnityLogBridgePatch.Initialize();
                    RoleLogger.Warn("[Shared]", "[Diag] VerifyCriticalPatches 防御性 Initialize D-11（Awake 未订阅）");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"VerifyCriticalPatches 防御性 Initialize 失败: {ex}");
                }
            }

            try
            {
                Patches.PlayerManagerBroadcastPatch.ReverifyOwnersAfterAllRegistrations(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"PlayerManagerBroadcastPatch.ReverifyOwnersAfterAllRegistrations 异常: {ex}");
            }
            try
            {
                Patches.RemotePlayerClothingVisibleBridgePatch.ReverifyOwnersAfterAllRegistrations(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"RemotePlayerClothingVisibleBridgePatch.ReverifyOwnersAfterAllRegistrations 异常: {ex}");
            }

            RoleLogger.Diagnostic("[Shared]", "[PatchValidation] validating required P2P hooks and ownership.");

            bool allOk = true;

            if (!HarmonyCompatibilityAudit.InspectOwnedPatchedMethods(_harmony))
            {
                RoleLogger.Error("[Shared]",
                    "[Compat] !!! blocking foreign Harmony conflict found during full owned-target scan");
                allOk = false;
            }

            //   自检任一 FAIL 或异常都视为失败，强制 DiagnosticBuildValid=false
            if (!redactionSelfTestPassed)
            {
                RoleLogger.Error("[Shared]",
                    "[Diag] !!! DIAGNOSTIC BUILD INVALID: P0-3 脱敏自检未通过 (RedactionSelfTestPassed=false) " +
                    "审计 Critical-2 fail-closed 要求");
                allOk = false;
            }
            else
            {
                RoleLogger.Info("[Shared]",
                    "[Diag] OK P0-3 脱敏自检通过 (RedactionSelfTestPassed=true，fail-closed 已激活)");
            }

            allOk &= VerifyPatch(typeof(Provider), "accept",
                new System.Type[] {
                    typeof(SteamPlayerID), typeof(bool), typeof(bool), typeof(byte), typeof(byte), typeof(byte),
                    typeof(Color), typeof(Color), typeof(Color), typeof(Color), typeof(bool),
                    typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int),
                    typeof(int[]), typeof(string[]), typeof(string[]),
                    typeof(EPlayerSkillset), typeof(string), typeof(Steamworks.CSteamID), typeof(EClientPlatform)
                },
                "Provider.accept(internal overload, 25 args)", requirePrefix: true, requireFinalizer: true);

            allOk &= VerifyPatch(typeof(Provider), "addPlayer",
                new System.Type[] {
                    typeof(SDG.NetTransport.ITransportConnection), typeof(NetId), typeof(SteamPlayerID),
                    typeof(Vector3), typeof(byte),
                    typeof(bool), typeof(bool), typeof(int),
                    typeof(byte), typeof(byte), typeof(byte),
                    typeof(Color), typeof(Color), typeof(Color), typeof(Color), typeof(bool),
                    typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int),
                    typeof(int[]), typeof(string[]), typeof(string[]),
                    typeof(EPlayerSkillset), typeof(string), typeof(Steamworks.CSteamID), typeof(EClientPlatform)
                },
                "Provider.addPlayer(internal overload, 30 args)", requirePrefix: true, requirePostfix: true);

            // 公开 Beta 固定验证完整的兼容性补丁集。
                // 套1: InitializePlayerStatePatch（状态机所有者，Prefix 返回 bool）- Prefix/Postfix/Finalizer
                // 套2: PlayerInitializeDiagnosticPatch（纯观察，void Prefix）- Prefix/Postfix/Finalizer
                HarmonyLib.Patches initPatches = null;
                System.Reflection.MethodInfo initMethod = null;
                try
                {
                    initMethod = AccessTools.Method(typeof(Player), "InitializePlayer");
                    if (initMethod != null)
                    {
                        initPatches = Harmony.GetPatchInfo(initMethod);
                    }
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"[Diag] Player.InitializePlayer GetPatchInfo 异常: {ex.Message}");
                    allOk = false;
                }

                int initPrefixCount = HarmonyCompatibilityAudit.CountOwned(initPatches?.Prefixes);
                int initPostfixCount = HarmonyCompatibilityAudit.CountOwned(initPatches?.Postfixes);
                int initFinalizerCount = HarmonyCompatibilityAudit.CountOwned(initPatches?.Finalizers);

                // 数量验证：必须各有 2 个（两套 patch）
                if (initPrefixCount < 2 || initPostfixCount < 2 || initFinalizerCount < 2)
                {
                    RoleLogger.Error("[Shared]",
                        $"[Diag] !!! Player.InitializePlayer patch 数量不足: " +
                        $"prefixes={initPrefixCount}(期望>=2) postfixes={initPostfixCount}(期望>=2) " +
                        $"finalizers={initFinalizerCount}(期望>=2)");
                    allOk = false;
                }

                if (initMethod != null &&
                    !HarmonyCompatibilityAudit.Inspect(initMethod, "Player.InitializePlayer"))
                {
                    RoleLogger.Error("[Shared]",
                        "[Compat] Player.InitializePlayer has a blocking foreign Harmony patch");
                    allOk = false;
                }

                // 精确方法验证：InitializePlayerStatePatch.Prefix/Postfix/Finalizer（状态机所有者）
                if (initPatches != null)
                {
                    if (!VerifyPatchMethod(initPatches.Prefixes,
                            typeof(SteamP2PFriends.Patches.InitializePlayerStatePatch),
                            "Prefix", "Player.InitializePlayer (P0-E E-3 Prefix)", "Prefix")) allOk = false;
                    if (!VerifyPatchMethod(initPatches.Postfixes,
                            typeof(SteamP2PFriends.Patches.InitializePlayerStatePatch),
                            "Postfix", "Player.InitializePlayer (P0-E E-3 Postfix)", "Postfix")) allOk = false;
                    if (!VerifyPatchMethod(initPatches.Finalizers,
                            typeof(SteamP2PFriends.Patches.InitializePlayerStatePatch),
                            "Finalizer", "Player.InitializePlayer (P0-E E-3 Finalizer)", "Finalizer")) allOk = false;

                    // 精确方法验证：PlayerInitializeDiagnosticPatch.Prefix/Postfix/Finalizer（纯观察）
                    if (!VerifyPatchMethod(initPatches.Prefixes,
                            typeof(SteamP2PFriends.Patches.PlayerInitializeDiagnosticPatch),
                            "Prefix", "Player.InitializePlayer (Diag Prefix)", "Prefix")) allOk = false;
                    if (!VerifyPatchMethod(initPatches.Postfixes,
                            typeof(SteamP2PFriends.Patches.PlayerInitializeDiagnosticPatch),
                            "Postfix", "Player.InitializePlayer (Diag Postfix)", "Postfix")) allOk = false;
                    if (!VerifyPatchMethod(initPatches.Finalizers,
                            typeof(SteamP2PFriends.Patches.PlayerInitializeDiagnosticPatch),
                            "Finalizer", "Player.InitializePlayer (Diag Finalizer)", "Finalizer")) allOk = false;
                }

                if (allOk)
                {
                    RoleLogger.Info("[Shared]",
                        $"[Diag] OK Player.InitializePlayer 双 patch 链验证通过 " +
                        $"(Prefix={initPrefixCount}/Postfix={initPostfixCount}/Finalizer={initFinalizerCount})");
                }

                // 验证 8 个组件的 InitializePlayer 上有 BitmaskPostfixCache<T>.Postfix 登记
                allOk &= VerifyBitmaskPostfix<PlayerClothing>("P1-G PlayerClothing");
                allOk &= VerifyBitmaskPostfix<PlayerInventory>("P1-G PlayerInventory");
                allOk &= VerifyBitmaskPostfix<PlayerLife>("P1-G PlayerLife");
                allOk &= VerifyBitmaskPostfix<PlayerStance>("P1-G PlayerStance");
                allOk &= VerifyBitmaskPostfix<PlayerMovement>("P1-G PlayerMovement");
                allOk &= VerifyBitmaskPostfix<PlayerLook>("P1-G PlayerLook");
                allOk &= VerifyBitmaskPostfix<PlayerInteract>("P1-G PlayerInteract");
                allOk &= VerifyBitmaskPostfix<PlayerInput>("P1-G PlayerInput");

                // SteamPlayer constructor
                System.Reflection.ConstructorInfo ctor = typeof(SteamPlayer).GetConstructor(new System.Type[] {
                    typeof(SDG.NetTransport.ITransportConnection), typeof(NetId), typeof(SteamPlayerID), typeof(Transform),
                    typeof(bool), typeof(bool), typeof(int), typeof(byte), typeof(byte), typeof(byte),
                    typeof(Color), typeof(Color), typeof(Color), typeof(Color), typeof(bool),
                    typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int),
                    typeof(int[]), typeof(string[]), typeof(string[]),
                    typeof(EPlayerSkillset), typeof(string), typeof(Steamworks.CSteamID), typeof(EClientPlatform)
                });
                if (ctor == null)
                {
                    RoleLogger.Error("[Shared]", "[Diag] !!! DIAGNOSTIC BUILD INVALID: SteamPlayer.ctor(29 args) 反射失败");
                    allOk = false;
                }
                else
                {
                    HarmonyLib.Patches patches = Harmony.GetPatchInfo(ctor);
                    int postfixCount = HarmonyCompatibilityAudit.CountOwned(patches?.Postfixes);
                    if (postfixCount == 0)
                    {
                        RoleLogger.Error("[Shared]",
                            $"[Diag] !!! DIAGNOSTIC BUILD INVALID: SteamPlayer.ctor Postfix 未登记 (P0-C)");
                        allOk = false;
                    }
                    else
                    {
                        bool compatible = HarmonyCompatibilityAudit.Inspect(ctor, "SteamPlayer.ctor");
                        allOk &= compatible;
                        bool methodOk = VerifyPatchMethod(patches?.Postfixes,
                            typeof(SteamP2PFriends.Patches.SteamPlayerIsLocalServerHostPatch),
                            "Postfix", "SteamPlayer.ctor (P0-C Postfix)", "Postfix");
                        allOk &= methodOk;
                        if (compatible && methodOk)
                        {
                            RoleLogger.Info("[Shared]",
                                $"[Diag] OK SteamPlayer.ctor Postfix 已登记 (postfixes={postfixCount})");
                        }
                    }
                }
            // 实际登记：所有方法 Prefix + Finalizer（无 Postfix）
            // Provider.SendInitialGlobalState(SteamPlayer)
            allOk &= VerifyPatch(typeof(Provider), "SendInitialGlobalState",
                new System.Type[] { typeof(SteamPlayer) },
                "D-13 Provider.SendInitialGlobalState(SteamPlayer)", requirePrefix: true, requireFinalizer: true);
            // PhysicsMaterialNetTable.Send(ITransportConnection)
            allOk &= VerifyPatch(typeof(SDG.Unturned.PhysicsMaterialNetTable), "Send",
                new System.Type[] { typeof(SDG.NetTransport.ITransportConnection) },
                "D-13 PhysicsMaterialNetTable.Send", requirePrefix: true, requireFinalizer: true);
            // LightingManager.SendInitialGlobalState(SteamPlayer)
            allOk &= VerifyPatch(typeof(LightingManager), "SendInitialGlobalState",
                new System.Type[] { typeof(SteamPlayer) },
                "D-13 LightingManager.SendInitialGlobalState", requirePrefix: true, requireFinalizer: true);
            // VehicleManager.SendInitialGlobalState(SteamPlayer)
            allOk &= VerifyPatch(typeof(VehicleManager), "SendInitialGlobalState",
                new System.Type[] { typeof(SteamPlayer) },
                "D-13 VehicleManager.SendInitialGlobalState", requirePrefix: true, requireFinalizer: true);
            // AnimalManager.SendInitialGlobalState(ITransportConnection)
            allOk &= VerifyPatch(typeof(AnimalManager), "SendInitialGlobalState",
                new System.Type[] { typeof(SDG.NetTransport.ITransportConnection) },
                "D-13 AnimalManager.SendInitialGlobalState", requirePrefix: true, requireFinalizer: true);
            // LevelManager.SendInitialGlobalState(SteamPlayer)
            allOk &= VerifyPatch(typeof(LevelManager), "SendInitialGlobalState",
                new System.Type[] { typeof(SteamPlayer) },
                "D-13 LevelManager.SendInitialGlobalState", requirePrefix: true, requireFinalizer: true);
            // ZombieManager.SendInitialGlobalState(SteamPlayer)
            allOk &= VerifyPatch(typeof(ZombieManager), "SendInitialGlobalState",
                new System.Type[] { typeof(SteamPlayer) },
                "D-13 ZombieManager.SendInitialGlobalState", requirePrefix: true, requireFinalizer: true);
            // Player.SendInitialPlayerState(SteamPlayer)
            allOk &= VerifyPatch(typeof(Player), "SendInitialPlayerState",
                new System.Type[] { typeof(SteamPlayer) },
                "D-13 Player.SendInitialPlayerState(SteamPlayer)", requirePrefix: true, requireFinalizer: true);
            // Player.SendInitialPlayerState(List<ITransportConnection>)
            allOk &= VerifyPatch(typeof(Player), "SendInitialPlayerState",
                new System.Type[] { typeof(System.Collections.Generic.List<SDG.NetTransport.ITransportConnection>) },
                "D-13 Player.SendInitialPlayerState(List)", requirePrefix: true, requireFinalizer: true);
            // Provider.AddClientToThirdpartyAntiCheat(ITransportConnection, SteamPlayerID, SteamPlayer)
            allOk &= VerifyPatch(typeof(Provider), "AddClientToThirdpartyAntiCheat",
                new System.Type[] { typeof(SDG.NetTransport.ITransportConnection), typeof(SteamPlayerID), typeof(SteamPlayer) },
                "D-13 Provider.AddClientToThirdpartyAntiCheat", requirePrefix: true, requireFinalizer: true);
            // Provider.dismiss (ProviderDismissDiagnosticPatch) - Prefix + Postfix
            allOk &= VerifyPatch(typeof(Provider), "dismiss",
                new System.Type[] { typeof(Steamworks.CSteamID) },
                "D-13 Provider.dismiss", requirePrefix: true, requirePostfix: true);
            // Provider.RemoveClient (ProviderDismissDiagnosticPatch) - Prefix + Postfix
            allOk &= VerifyPatch(typeof(Provider), "RemoveClient",
                new System.Type[] { typeof(SteamPlayer) },
                "D-13 Provider.RemoveClient", requirePrefix: true, requirePostfix: true);

            allOk &= VerifyPatch(typeof(PlayerClothing), "InitializePlayer",
                "D-3b PlayerClothing.InitializePlayer", requireFinalizer: true);
            allOk &= VerifyPatch(typeof(PlayerInventory), "InitializePlayer",
                "D-3b PlayerInventory.InitializePlayer", requireFinalizer: true);
            allOk &= VerifyPatch(typeof(PlayerLife), "InitializePlayer",
                "D-3b PlayerLife.InitializePlayer", requireFinalizer: true);
            allOk &= VerifyPatch(typeof(PlayerSkills), "InitializePlayer",
                "D-3b PlayerSkills.InitializePlayer", requireFinalizer: true);
            allOk &= VerifyPatch(typeof(PlayerCrafting), "InitializePlayer",
                "D-3b PlayerCrafting.InitializePlayer", requireFinalizer: true);
            allOk &= VerifyPatch(typeof(PlayerStance), "InitializePlayer",
                "D-3b PlayerStance.InitializePlayer", requireFinalizer: true);
            allOk &= VerifyPatch(typeof(PlayerMovement), "InitializePlayer",
                "D-3b PlayerMovement.InitializePlayer", requireFinalizer: true);
            allOk &= VerifyPatch(typeof(PlayerLook), "InitializePlayer",
                "D-3b PlayerLook.InitializePlayer", requireFinalizer: true);
            allOk &= VerifyPatch(typeof(PlayerInteract), "InitializePlayer",
                "D-3b PlayerInteract.InitializePlayer", requireFinalizer: true);
            allOk &= VerifyPatch(typeof(PlayerAnimator), "InitializePlayer",
                "D-3b PlayerAnimator.InitializePlayer", requireFinalizer: true);
            allOk &= VerifyPatch(typeof(PlayerEquipment), "InitializePlayer",
                "D-3b PlayerEquipment.InitializePlayer", requireFinalizer: true);
            allOk &= VerifyPatch(typeof(PlayerInput), "InitializePlayer",
                "D-3b PlayerInput.InitializePlayer", requireFinalizer: true);
            allOk &= VerifyPatch(typeof(PlayerVoice), "InitializePlayer",
                "D-3b PlayerVoice.InitializePlayer", requireFinalizer: true);
            allOk &= VerifyPatch(typeof(PlayerWorkzone), "InitializePlayer",
                "D-3b PlayerWorkzone.InitializePlayer", requireFinalizer: true);
            allOk &= VerifyPatch(typeof(PlayerQuests), "InitializePlayer",
                "D-3b PlayerQuests.InitializePlayer", requireFinalizer: true);
            // Player.InitializePlayerStart (private, Player.cs:1542) - Prefix + Finalizer
            allOk &= VerifyPatch(typeof(Player), "InitializePlayerStart",
                new System.Type[0],
                "D-3b Player.InitializePlayerStart", requirePrefix: true, requireFinalizer: true);

            // Provider.reject 4 个重载
            allOk &= VerifyPatch(typeof(Provider), "reject",
                new System.Type[] { typeof(Steamworks.CSteamID), typeof(ESteamRejection) },
                "D-5 Provider.reject(CSteamID,ESteamRejection)", requirePrefix: true);
            allOk &= VerifyPatch(typeof(Provider), "reject",
                new System.Type[] { typeof(Steamworks.CSteamID), typeof(ESteamRejection), typeof(string) },
                "D-5 Provider.reject(CSteamID,ESteamRejection,string)", requirePrefix: true);
            allOk &= VerifyPatch(typeof(Provider), "reject",
                new System.Type[] { typeof(SDG.NetTransport.ITransportConnection), typeof(ESteamRejection) },
                "D-5 Provider.reject(ITransport,ESteamRejection)", requirePrefix: true);
            allOk &= VerifyPatch(typeof(Provider), "reject",
                new System.Type[] { typeof(SDG.NetTransport.ITransportConnection), typeof(ESteamRejection), typeof(string) },
                "D-5 Provider.reject(ITransport,ESteamRejection,string)", requirePrefix: true);
            // Provider.kick(CSteamID, string)
            allOk &= VerifyPatch(typeof(Provider), "kick",
                new System.Type[] { typeof(Steamworks.CSteamID), typeof(string) },
                "D-5 Provider.kick(CSteamID,string)", requirePrefix: true);
            // Provider.refuseGarbageConnection 2 个重载
            allOk &= VerifyPatch(typeof(Provider), "refuseGarbageConnection",
                new System.Type[] { typeof(Steamworks.CSteamID), typeof(string) },
                "D-5 Provider.refuseGarbageConnection(CSteamID,string)", requirePrefix: true);
            allOk &= VerifyPatch(typeof(Provider), "refuseGarbageConnection",
                new System.Type[] { typeof(SDG.NetTransport.ITransportConnection), typeof(string) },
                "D-5 Provider.refuseGarbageConnection(ITransport,string)", requirePrefix: true);

            // NetMessages.SendMessageToClient + SendMessageToClients (Prefix + Finalizer)
            allOk &= VerifyNetMessagesPatches();

            // 该方法由 ClientAcceptedHandlerDiagnosticPatch.RegisterManual 登记到 internal class
            // 自检通过 ReflectionUtil 反射内部类型，若失败已在 RegisterManual 阶段报错
            // 这里仅检查 Provider.accept 是否会被 ReadMessage 触发（已由 accept 自检覆盖）

            allOk &= VerifyPatch(typeof(Steamworks.SteamGameServerNetworkingSockets), "CloseConnection",
                "D-9 SteamGameServerNetworkingSockets.CloseConnection", requirePrefix: true);

            // ClientTransport / ServerTransport OnSteamNetConnectionStatusChanged 由 RouteDiagnosticsPatch 登记到具体重载
            // 这里检查 SteamGameServerNetworkingSockets 的 SteamUser API 域重定向方法都已登记。
            allOk &= VerifyPatch(typeof(Steamworks.SteamGameServerNetworkingSockets), "CreateListenSocketIP",
                "D-10/CreateListenSocketIP", requirePrefix: true);
            allOk &= VerifyPatch(typeof(Steamworks.SteamGameServerNetworkingSockets), "CreateListenSocketP2P",
                "D-10/CreateListenSocketP2P", requirePrefix: true);
            allOk &= VerifyPatch(typeof(Steamworks.SteamGameServerNetworkingSockets), "AcceptConnection",
                "D-10/AcceptConnection", requirePrefix: true, requirePostfix: true);
            allOk &= VerifyPatchMethodPair(
                typeof(Steamworks.SteamGameServerNetworkingSockets), "AcceptConnection", null,
                typeof(Patches.SteamUserP2PRedirectPatch),
                nameof(Patches.SteamUserP2PRedirectPatch.AcceptConnection_Prefix),
                nameof(Patches.SteamUserP2PRedirectPatch.AcceptConnection_Postfix),
                "D-10/AcceptConnection precise method");
            allOk &= VerifyPatch(typeof(Steamworks.SteamGameServerNetworkingSockets), "SetConnectionPollGroup",
                "D-10/SetConnectionPollGroup", requirePrefix: true, requirePostfix: true);
            allOk &= VerifyPatchMethodPair(
                typeof(Steamworks.SteamGameServerNetworkingSockets), "SetConnectionPollGroup", null,
                typeof(Patches.SteamUserP2PRedirectPatch),
                nameof(Patches.SteamUserP2PRedirectPatch.SetConnectionPollGroup_Prefix),
                nameof(Patches.SteamUserP2PRedirectPatch.SetConnectionPollGroup_Postfix),
                "D-10/SetConnectionPollGroup precise method");
            allOk &= VerifyPatch(typeof(Steamworks.SteamGameServerNetworkingSockets), "CreatePollGroup",
                "D-10/CreatePollGroup", requirePrefix: true);
            allOk &= VerifyPatch(typeof(Steamworks.SteamGameServerNetworkingSockets), "DestroyPollGroup",
                "D-10/DestroyPollGroup", requirePrefix: true);
            allOk &= VerifyPatch(typeof(Steamworks.SteamGameServerNetworkingSockets), "ReceiveMessagesOnPollGroup",
                "D-10/ReceiveMessagesOnPollGroup", requirePrefix: true);
            allOk &= VerifyPatch(typeof(Steamworks.SteamGameServerNetworkingSockets), "CloseListenSocket",
                "D-10/CloseListenSocket", requirePrefix: true);
            allOk &= VerifyPatch(typeof(Steamworks.SteamGameServerNetworkingSockets), "SendMessageToConnection",
                "D-10/SendMessageToConnection", requirePrefix: true);
            allOk &= VerifyPatch(typeof(Steamworks.SteamGameServerNetworkingSockets), "ReceiveMessagesOnConnection",
                "D-10/ReceiveMessagesOnConnection", requirePrefix: true);
            allOk &= VerifyPatch(typeof(Steamworks.SteamGameServerNetworkingSockets), "GetConnectionInfo",
                "D-10/GetConnectionInfo", requirePrefix: true);
            allOk &= VerifyPatch(typeof(Steamworks.SteamGameServerNetworkingSockets), "SetConnectionName",
                "D-10/SetConnectionName", requirePrefix: true);

            allOk &= VerifyPatch(typeof(Steamworks.Callback<Steamworks.SteamNetConnectionStatusChangedCallback_t>), "CreateGameServer",
                "Callback<ConnStatus>.CreateGameServer", requirePrefix: true);
            allOk &= VerifyPatch(typeof(Steamworks.Callback<Steamworks.SteamNetAuthenticationStatus_t>), "CreateGameServer",
                "Callback<AuthStatus>.CreateGameServer", requirePrefix: true);

            allOk &= VerifyPatch(typeof(SDG.NetTransport.SteamNetworkingSockets.ServerTransport_SteamNetworkingSockets), "Initialize",
                "ServerTransport.Initialize", requirePrefix: true);

            // ClientTransport_SteamNetworkingSockets.OnSteamNetConnectionStatusChanged - ClientSnsStatusDiagnosticPatch
            allOk &= VerifyPatch(typeof(SDG.NetTransport.SteamNetworkingSockets.ClientTransport_SteamNetworkingSockets),
                "OnSteamNetConnectionStatusChanged",
                "D-10 ClientTransport.OnSteamNetConnectionStatusChanged",
                requirePrefix: true, requirePostfix: true);
            //   ClientSnsStatusDiagnosticPatch.Prefix / .Postfix（private static，反射可访问 metadata）
            allOk &= VerifyPatchMethodPair(
                typeof(SDG.NetTransport.SteamNetworkingSockets.ClientTransport_SteamNetworkingSockets),
                "OnSteamNetConnectionStatusChanged", null,
                typeof(Patches.ClientSnsStatusDiagnosticPatch),
                "Prefix", "Postfix",
                "D-10 ClientTransport.OnSteamNetConnectionStatusChanged precise method");
            // ServerTransport_SteamNetworkingSockets.OnSteamNetConnectionStatusChanged - ServerSnsStatusDiagnosticPatch
            allOk &= VerifyPatch(typeof(SDG.NetTransport.SteamNetworkingSockets.ServerTransport_SteamNetworkingSockets),
                "OnSteamNetConnectionStatusChanged",
                "D-10 ServerTransport.OnSteamNetConnectionStatusChanged",
                requirePrefix: true, requirePostfix: true);
            allOk &= VerifyPatchMethodPair(
                typeof(SDG.NetTransport.SteamNetworkingSockets.ServerTransport_SteamNetworkingSockets),
                "OnSteamNetConnectionStatusChanged", null,
                typeof(Patches.ServerSnsStatusDiagnosticPatch),
                "Prefix", "Postfix",
                "D-10 ServerTransport.OnSteamNetConnectionStatusChanged precise method");

            // 1. LightingManager.ReceiveInitialLightingState (static, 9 args)
            allOk &= VerifyPatch(typeof(LightingManager), "ReceiveInitialLightingState",
                new System.Type[] {
                    typeof(uint), typeof(uint), typeof(uint), typeof(byte), typeof(byte),
                    typeof(System.Guid), typeof(float), typeof(NetId), typeof(int)
                },
                "P0-C LightingManager.ReceiveInitialLightingState", requirePrefix: true, requirePostfix: true, requireFinalizer: true);
            // 2. VehicleManager.ReceiveMultipleVehicles (static, in ClientInvocationContext)
            allOk &= VerifyPatch(typeof(VehicleManager), "ReceiveMultipleVehicles",
                "P0-C VehicleManager.ReceiveMultipleVehicles", requirePrefix: true, requirePostfix: true, requireFinalizer: true);
            // 3. BarricadeManager.ReceiveMultipleBarricades (static, in ClientInvocationContext)
            allOk &= VerifyPatch(typeof(BarricadeManager), "ReceiveMultipleBarricades",
                "P0-C BarricadeManager.ReceiveMultipleBarricades", requirePrefix: true, requirePostfix: true, requireFinalizer: true);
            // 4. StructureManager.ReceiveMultipleStructures (static, in ClientInvocationContext)
            allOk &= VerifyPatch(typeof(StructureManager), "ReceiveMultipleStructures",
                "P0-C StructureManager.ReceiveMultipleStructures", requirePrefix: true, requirePostfix: true, requireFinalizer: true);
            // 5. PlayerInventory.ReceiveInventory (instance, in ClientInvocationContext)
            allOk &= VerifyPatch(typeof(PlayerInventory), "ReceiveInventory",
                "P0-C PlayerInventory.ReceiveInventory", requirePrefix: true, requirePostfix: true, requireFinalizer: true);
            // 6. PlayerLife.ReceiveLifeStats (instance, 7 args)
            allOk &= VerifyPatch(typeof(PlayerLife), "ReceiveLifeStats",
                new System.Type[] {
                    typeof(byte), typeof(byte), typeof(byte), typeof(byte), typeof(byte), typeof(bool), typeof(bool)
                },
                "P0-C PlayerLife.ReceiveLifeStats", requirePrefix: true, requirePostfix: true, requireFinalizer: true);
            // 7. PlayerClothing.ReceiveClothingState (instance, in ClientInvocationContext)
            allOk &= VerifyPatch(typeof(PlayerClothing), "ReceiveClothingState",
                "P0-C PlayerClothing.ReceiveClothingState", requirePrefix: true, requirePostfix: true, requireFinalizer: true);

            try
            {
                System.Type qpcType = AccessTools.TypeByName("SDG.Unturned.ClientMessageHandler_QueuePositionChanged");
                if (qpcType == null)
                {
                    RoleLogger.Error("[Shared]", "[Diag] !!! P0-B ClientMessageHandler_QueuePositionChanged: TypeByName 返回 null");
                    allOk = false;
                }
                else
                {
                    allOk &= VerifyPatch(qpcType, "ReadMessage",
                        new System.Type[] { typeof(NetPakReader) },
                        "P0-B ClientMessageHandler_QueuePositionChanged.ReadMessage", requirePostfix: true);
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[Diag] P0-B QueuePositionChanged 自检异常: {ex.Message}");
                allOk = false;
            }

            allOk &= VerifyPatch(typeof(PlayerInput), "FixedUpdate",
                "P0-D PlayerInput.FixedUpdate", requirePrefix: true);
            allOk &= VerifyPatch(typeof(PlayerMovement), "simulate",
                new System.Type[0],
                "P0-D PlayerMovement.simulate(0 args)", requirePrefix: true);
            allOk &= VerifyPatch(typeof(PlayerMovement), "simulate",
                new System.Type[] {
                    typeof(uint), typeof(int), typeof(bool), typeof(bool),
                    typeof(Vector3), typeof(Quaternion),
                    typeof(float), typeof(float), typeof(float), typeof(float), typeof(float)
                },
                "P0-D PlayerMovement.simulate(11 args, driving)", requirePrefix: true);
            allOk &= VerifyPatch(typeof(PlayerMovement), "simulate",
                new System.Type[] {
                    typeof(uint), typeof(int), typeof(int), typeof(int),
                    typeof(float), typeof(float),
                    typeof(bool), typeof(bool), typeof(float)
                },
                "P0-D PlayerMovement.simulate(9 args, walking)", requirePrefix: true);

            //   - Prefix: DisconnectTracerPatch.Prefix（在 vanilla teardown 前抓取 handle 状态）
            //   - Postfix: DisconnectTracerPatch.Postfix（记录 vanilla 调用方 reason）
            allOk &= VerifyPatch(typeof(Provider), "RequestDisconnect",
                new System.Type[] { typeof(string) },
                "P1-C Provider.RequestDisconnect(string)", requirePrefix: true, requirePostfix: true);
            //   DisconnectTracerPatch.Prefix / .Postfix（private static，反射可访问 metadata）
            allOk &= VerifyPatchMethodPair(
                typeof(Provider), "RequestDisconnect", new System.Type[] { typeof(string) },
                typeof(Patches.DisconnectTracerPatch),
                "Prefix", "Postfix",
                "P1-C Provider.RequestDisconnect(string) precise method");

            //   双端 OnSteamNetAuthenticationStatusChanged 各加 Prefix，替换 m_debugMsg 为占位符
            //   封闭 vanilla Log 把原始 m_debugMsg 写入 Unity Player.log 的路径
            //   两个 patch 类均只有 Prefix（无 Postfix），用 VerifyPatch + VerifyPatchMethod 单独验证
            allOk &= VerifyPatch(typeof(SDG.NetTransport.SteamNetworkingSockets.ServerTransport_SteamNetworkingSockets),
                "OnSteamNetAuthenticationStatusChanged",
                "P0-B ServerTransport.OnSteamNetAuthenticationStatusChanged", requirePrefix: true);
            allOk &= VerifyAuthCallbackSafetyPatchMethod(
                typeof(SDG.NetTransport.SteamNetworkingSockets.ServerTransport_SteamNetworkingSockets),
                "OnSteamNetAuthenticationStatusChanged",
                typeof(Patches.ServerAuthStatusCallbackSafetyPatch),
                "Prefix",
                "P0-B ServerTransport.OnSteamNetAuthenticationStatusChanged precise method");
            allOk &= VerifyPatch(typeof(SDG.NetTransport.SteamNetworkingSockets.ClientTransport_SteamNetworkingSockets),
                "OnSteamNetAuthenticationStatusChanged",
                "P0-B ClientTransport.OnSteamNetAuthenticationStatusChanged", requirePrefix: true);
            allOk &= VerifyAuthCallbackSafetyPatchMethod(
                typeof(SDG.NetTransport.SteamNetworkingSockets.ClientTransport_SteamNetworkingSockets),
                "OnSteamNetAuthenticationStatusChanged",
                typeof(Patches.ClientAuthStatusCallbackSafetyPatch),
                "Prefix",
                "P0-B ClientTransport.OnSteamNetAuthenticationStatusChanged precise method");

            // 旧版关键 patch (informational only, 不参与 allOk)
            // 上面已经覆盖

            RoleLogger.Info("[Shared]",
                $"[Diag] v2 审计放行 patch 状态: " +
                $"SteamPlayerIsLocalServerHostPatch.Enabled={Patches.SteamPlayerIsLocalServerHostPatch.Enabled}, " +
                $"PlayerUpdateGuardPatch.Enabled={Patches.PlayerUpdateGuardPatch.Enabled}, " +
                $"PlayerMovementInitializePlayerPrefixPatch.Enabled={Patches.PlayerMovementInitializePlayerPrefixPatch.Enabled}, " +
                $"GameplayReadyBitmaskPatch.Enabled={Patches.GameplayReadyBitmaskPatch.Enabled}");

            if (!Patches.UnityLogBridgePatch.IsSubscribed || Patches.UnityLogBridgePatch.IsFailed)
            {
                RoleLogger.Error("[Shared]",
                    $"[Diag] !!! DIAGNOSTIC BUILD INVALID: D-11 Unity bridge 未订阅 " +
                    $"(IsSubscribed={Patches.UnityLogBridgePatch.IsSubscribed}, " +
                    $"IsFailed={Patches.UnityLogBridgePatch.IsFailed})");
                allOk = false;
            }
            else
            {
                RoleLogger.Info("[Shared]",
                    "[Diag] OK D-11 Unity logMessageReceivedThreaded bridge subscribed (blocking)");
            }

            // 不能只依赖 Harmony 元数据数量与 owner，必须同时检查 RegisterManual 返回值
            bool p0cAll = Patches.InitialStateReceiveDiagnosticPatch.AllRegistrationsSucceeded;
            if (!p0cAll)
            {
                RoleLogger.Error("[Shared]",
                    $"[Diag] !!! DIAGNOSTIC BUILD INVALID: P0-C AllRegistrationsSucceeded=false " +
                    $"summary={Patches.InitialStateReceiveDiagnosticPatch.RegistrationSummary}");
                allOk = false;
            }
            else
            {
                RoleLogger.Info("[Shared]",
                    $"[Diag] OK P0-C AllRegistrationsSucceeded=true " +
                    $"summary={Patches.InitialStateReceiveDiagnosticPatch.RegistrationSummary}");
            }

            //   三个 ClientMethodHandle.SendAndLoopback* Prefix 必须全部手动登记成功，
            //   且 PatchMethod.DeclaringType==ClientMethodLoopbackPatch + 方法名匹配。
            //   任一缺失强制 DiagnosticBuildValid=false。
            //   AllRegistrationsSucceeded 是 RegisterManual 返回值的快照，
            //   精确自检是对 Harmony 元数据的独立验证（双保险）。
            //   AccessTools.DeclaredMethod 从 ClientMethodHandle 声明类型精确解析 private InvokeLoopback
            //   派生类型 GetMethod 找不到基类 private 方法，旧 ReflectionUtil.InvokeInstance 必失败
            bool loopbackRegOk = Patches.ClientMethodLoopbackPatch.AllRegistrationsSucceeded;
            if (!loopbackRegOk)
            {
                RoleLogger.Error("[Shared]",
                    $"[Diag] !!! DIAGNOSTIC BUILD INVALID: ClientMethodLoopbackPatch AllRegistrationsSucceeded=false " +
                    $"summary={Patches.ClientMethodLoopbackPatch.RegistrationSummary}");
                allOk = false;
            }
            else
            {
                RoleLogger.Info("[Shared]",
                    $"[Diag] OK ClientMethodLoopbackPatch AllRegistrationsSucceeded=true " +
                    $"summary={Patches.ClientMethodLoopbackPatch.RegistrationSummary}");
            }

            //   VerifyInvokeLoopbackMethod 已在 RegisterManual 开头执行，
            //   此处仅读取结果做阻断门聚合。
            //   验证：DeclaringType==ClientMethodHandle + Name==InvokeLoopback + 参数==NetPakWriter + 返回 void
            bool invokeLoopbackOk = Patches.ClientMethodLoopbackPatch.InvokeLoopbackResolved;
            if (!invokeLoopbackOk)
            {
                RoleLogger.Error("[Shared]",
                    $"[Diag] !!! DIAGNOSTIC BUILD INVALID: ClientMethodLoopbackPatch InvokeLoopbackResolved=false " +
                    $"summary={Patches.ClientMethodLoopbackPatch.InvokeLoopbackSummary}");
                allOk = false;
            }
            else
            {
                RoleLogger.Info("[Shared]",
                    $"[Diag] OK ClientMethodLoopbackPatch InvokeLoopbackResolved=true " +
                    $"summary={Patches.ClientMethodLoopbackPatch.InvokeLoopbackSummary}");
            }

            // 精确方法验证：3 个 Prefix 的 DeclaringType + Name 必须匹配
            // Own prefix identity must be exact; foreign patch policy is evaluated separately.
            allOk &= VerifyClientMethodLoopbackPrefix(
                typeof(ClientMethodHandle), "SendAndLoopbackIfLocal",
                new System.Type[] {
                    typeof(ENetReliability),
                    typeof(SDG.NetTransport.ITransportConnection),
                    typeof(NetPakWriter)
                },
                Patches.ClientMethodLoopbackPatch.PrefixIfLocalName,
                "ClientMethodLoopback/IfLocal");

            allOk &= VerifyClientMethodLoopbackPrefix(
                typeof(ClientMethodHandle), "SendAndLoopbackIfAnyAreLocal",
                new System.Type[] {
                    typeof(ENetReliability),
                    typeof(System.Collections.Generic.List<SDG.NetTransport.ITransportConnection>),
                    typeof(NetPakWriter)
                },
                Patches.ClientMethodLoopbackPatch.PrefixIfAnyAreLocalName,
                "ClientMethodLoopback/IfAnyAreLocal");

            allOk &= VerifyClientMethodLoopbackPrefix(
                typeof(ClientMethodHandle), "SendAndLoopback",
                new System.Type[] {
                    typeof(ENetReliability),
                    typeof(System.Collections.Generic.List<SDG.NetTransport.ITransportConnection>),
                    typeof(NetPakWriter)
                },
                Patches.ClientMethodLoopbackPatch.PrefixSendAndLoopbackName,
                "ClientMethodLoopback/SendAndLoopback");

            //   - AllRegistrationsSucceeded=true
            //   - ReplacementCount=1（Transpiler 替换点精确 1 个）
            //   - SignatureResolved=true（onRegionUpdated private instance 7 args 签名匹配）
            //   - SendRegionPrefixRegistered=true（Prefix 登记成功）
            //   - TranspilerOwnerVerified=true（owner=com.yu80rice.steamp2pfriends + method=OnRegionUpdated_Transpiler + count=1）
            //   - PrefixOwnerVerified=true（owner=com.yu80rice.steamp2pfriends + method=SendRegion_Prefix + count=1）
            //   任一不满足强制 DiagnosticBuildValid=false
            bool barricadeRegionOk = Patches.BarricadeManagerRegionSyncPatch.AllRegistrationsSucceeded;
            if (!barricadeRegionOk)
            {
                RoleLogger.Error("[Shared]",
                    $"[Diag] !!! DIAGNOSTIC BUILD INVALID: BarricadeManagerRegionSyncPatch " +
                    $"summary={Patches.BarricadeManagerRegionSyncPatch.RegistrationSummary} " +
                    $"replacement={Patches.BarricadeManagerRegionSyncPatch.ReplacementCount} " +
                    $"signature={Patches.BarricadeManagerRegionSyncPatch.SignatureResolved} " +
                    $"sendRegionPrefix={Patches.BarricadeManagerRegionSyncPatch.SendRegionPrefixRegistered} " +
                    $"transpilerOwner={Patches.BarricadeManagerRegionSyncPatch.TranspilerOwnerVerified} " +
                    $"prefixOwner={Patches.BarricadeManagerRegionSyncPatch.PrefixOwnerVerified}");
                allOk = false;
            }
            else
            {
                RoleLogger.Info("[Shared]",
                    $"[Diag] OK BarricadeManagerRegionSyncPatch: " +
                    $"replacement={Patches.BarricadeManagerRegionSyncPatch.ReplacementCount}/1 " +
                    $"signature={Patches.BarricadeManagerRegionSyncPatch.SignatureResolved} " +
                    $"sendRegionPrefix={Patches.BarricadeManagerRegionSyncPatch.SendRegionPrefixRegistered} " +
                    $"transpilerOwner={Patches.BarricadeManagerRegionSyncPatch.TranspilerOwnerSummary} " +
                    $"prefixOwner={Patches.BarricadeManagerRegionSyncPatch.PrefixOwnerSummary}");
            }

            bool structureRegionOk = Patches.StructureManagerRegionSyncPatch.AllRegistrationsSucceeded;
            if (!structureRegionOk)
            {
                RoleLogger.Error("[Shared]",
                    $"[Diag] !!! DIAGNOSTIC BUILD INVALID: StructureManagerRegionSyncPatch " +
                    $"summary={Patches.StructureManagerRegionSyncPatch.RegistrationSummary} " +
                    $"replacement={Patches.StructureManagerRegionSyncPatch.ReplacementCount} " +
                    $"signature={Patches.StructureManagerRegionSyncPatch.SignatureResolved} " +
                    $"askStructuresPrefix={Patches.StructureManagerRegionSyncPatch.AskStructuresPrefixRegistered} " +
                    $"transpilerOwner={Patches.StructureManagerRegionSyncPatch.TranspilerOwnerVerified} " +
                    $"prefixOwner={Patches.StructureManagerRegionSyncPatch.PrefixOwnerVerified}");
                allOk = false;
            }
            else
            {
                RoleLogger.Info("[Shared]",
                    $"[Diag] OK StructureManagerRegionSyncPatch: " +
                    $"replacement={Patches.StructureManagerRegionSyncPatch.ReplacementCount}/1 " +
                    $"signature={Patches.StructureManagerRegionSyncPatch.SignatureResolved} " +
                    $"askStructuresPrefix={Patches.StructureManagerRegionSyncPatch.AskStructuresPrefixRegistered} " +
                    $"transpilerOwner={Patches.StructureManagerRegionSyncPatch.TranspilerOwnerSummary} " +
                    $"prefixOwner={Patches.StructureManagerRegionSyncPatch.PrefixOwnerSummary}");
            }

            bool p0S1S2Ok = Patches.PlayerManagerBroadcastPatch.AllRegistrationsSucceeded;
            if (!p0S1S2Ok)
            {
                RoleLogger.Error("[Shared]",
                    $"[Diag] !!! DIAGNOSTIC BUILD INVALID: PlayerManagerBroadcastPatch " +
                    $"P0-S1={Patches.PlayerManagerBroadcastPatch.P0S1_Registered} " +
                    $"P0-S2={Patches.PlayerManagerBroadcastPatch.P0S2_Registered} " +
                    $"replacementCount={Patches.PlayerManagerBroadcastPatch.P0S1_ReplacementCount} " +
                    $"P0S1_owner={Patches.PlayerManagerBroadcastPatch.P0S1_TranspilerOwnerVerified} " +
                    $"P0S2_owner={Patches.PlayerManagerBroadcastPatch.P0S2_PrefixOwnerVerified} " +
                    $"P0S2_reflection={Patches.PlayerManagerBroadcastPatch.P0S2_ReflectionComplete}");
                allOk = false;
            }
            else
            {
                RoleLogger.Info("[Shared]",
                    $"[Diag] OK PlayerManagerBroadcastPatch: " +
                    $"P0-S1={Patches.PlayerManagerBroadcastPatch.P0S1_Registered} " +
                    $"P0-S2={Patches.PlayerManagerBroadcastPatch.P0S2_Registered} " +
                    $"replacement={Patches.PlayerManagerBroadcastPatch.P0S1_ReplacementCount}/1 " +
                    $"P0S1_owner={Patches.PlayerManagerBroadcastPatch.P0S1_TranspilerOwnerSummary} " +
                    $"P0S2_owner={Patches.PlayerManagerBroadcastPatch.P0S2_PrefixOwnerSummary} " +
                    $"P0S2_reflection={Patches.PlayerManagerBroadcastPatch.P0S2_ReflectionComplete}");
            }

            bool p0S3Ok = Patches.RemotePlayerClothingVisibleBridgePatch.AllRegistrationsSucceeded;
            if (!p0S3Ok)
            {
                RoleLogger.Error("[Shared]",
                    $"[Diag] !!! DIAGNOSTIC BUILD INVALID: RemotePlayerClothingVisibleBridgePatch " +
                    $"P0-S3={Patches.RemotePlayerClothingVisibleBridgePatch.P0S3_Registered} " +
                    $"P0S3_owner={Patches.RemotePlayerClothingVisibleBridgePatch.P0S3_PostfixOwnerVerified} " +
                    $"P0S3_ownerSummary={Patches.RemotePlayerClothingVisibleBridgePatch.P0S3_PostfixOwnerSummary} " +
                    $"P0S3_reflection={Patches.RemotePlayerClothingVisibleBridgePatch.P0S3_ReflectionComplete}");
                allOk = false;
            }
            else
            {
                RoleLogger.Info("[Shared]",
                    $"[Diag] OK RemotePlayerClothingVisibleBridgePatch: " +
                    $"P0-S3={Patches.RemotePlayerClothingVisibleBridgePatch.P0S3_Registered} " +
                    $"P0S3_owner={Patches.RemotePlayerClothingVisibleBridgePatch.P0S3_PostfixOwnerSummary} " +
                    $"P0S3_reflection={Patches.RemotePlayerClothingVisibleBridgePatch.P0S3_ReflectionComplete}");
            }

            bool p1S5Ok = Patches.PlayerManagerBroadcastDiagnosticPatch.AllRegistrationsSucceeded;
            if (!p1S5Ok)
            {
                RoleLogger.Warn("[Shared]",
                    $"[Diag] WARN PlayerManagerBroadcastDiagnosticPatch P1-S5 登记不全（不阻断联机）: " +
                    $"P1-S5={Patches.PlayerManagerBroadcastDiagnosticPatch.P1S5_Registered} " +
                    $"updatePost={Patches.PlayerManagerBroadcastDiagnosticPatch.UpdatePostfixRegistered} " +
                    $"sendPre={Patches.PlayerManagerBroadcastDiagnosticPatch.SendPrefixRegistered} " +
                    $"sendPost={Patches.PlayerManagerBroadcastDiagnosticPatch.SendPostfixRegistered} " +
                    $"sendFinal={Patches.PlayerManagerBroadcastDiagnosticPatch.SendFinalizerRegistered} " +
                    $"receivePost={Patches.PlayerManagerBroadcastDiagnosticPatch.ReceivePostfixRegistered}");
            }
            else
            {
                RoleLogger.Info("[Shared]",
                    $"[Diag] OK PlayerManagerBroadcastDiagnosticPatch: P1-S5={Patches.PlayerManagerBroadcastDiagnosticPatch.P1S5_Registered} " +
                    $"updatePost={Patches.PlayerManagerBroadcastDiagnosticPatch.UpdatePostfixRegistered} " +
                    $"sendPre={Patches.PlayerManagerBroadcastDiagnosticPatch.SendPrefixRegistered} " +
                    $"sendPost={Patches.PlayerManagerBroadcastDiagnosticPatch.SendPostfixRegistered} " +
                    $"sendFinal={Patches.PlayerManagerBroadcastDiagnosticPatch.SendFinalizerRegistered} " +
                    $"receivePost={Patches.PlayerManagerBroadcastDiagnosticPatch.ReceivePostfixRegistered}");
            }

            //   审计 §5 要求：精确验证 Prefix 登记一次
            //   失败时聚合到 DiagnosticBuildValid=false（INVALID 门控）
            try
            {
                if (!Patches.PlayerClothingLoadAppearanceFixPatch.VerifyRegistration())
                {
                    allOk = false;
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P0-S4] VerifyRegistration 整体异常: {ex.Message}");
                allOk = false;
            }

            //   - 目标类型、方法名、参数类型精确匹配
            //   - 预期 Prefix/Postfix owner 为本插件
            //   - own count 精确等于预期，缺失/重复均失败
            //   - 结果进入 DiagnosticBuildValid 阻断门
            //   任一链路失败即 DiagnosticBuildValid=false
            try
            {
                if (!Patches.ItemManagerWorldSyncDiagnosticPatch.VerifyRegistration())
                {
                    allOk = false;
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Item] VerifyRegistration 整体异常: {ex.Message}");
                allOk = false;
            }

            try
            {
                if (!Patches.AuthoritativeItemGenerationGatePatch.AllRegistrationsSucceeded
                    || !Patches.AuthoritativeItemGenerationGatePatch.VerifyRegistration())
                {
                    RoleLogger.Error("[Shared]",
                        $"[ItemAuthorityGate] DIAGNOSTIC BUILD INVALID: {Patches.AuthoritativeItemGenerationGatePatch.RegistrationSummary}");
                    allOk = false;
                }
                else
                {
                    RoleLogger.Info("[Shared]",
                        $"[ItemAuthorityGate] registration verified: {Patches.AuthoritativeItemGenerationGatePatch.RegistrationSummary}");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[ItemAuthorityGate] VerifyRegistration exception: {ex.Message}");
                allOk = false;
            }

            try
            {
                if (!Patches.InventoryWorldAuthorityProbe.AllRegistrationsSucceeded
                    || !Patches.InventoryWorldAuthorityProbe.VerifyRegistration())
                {
                    RoleLogger.Error("[Shared]",
                        $"[Alpha-AuthorityProbe] DIAGNOSTIC BUILD INVALID: {Patches.InventoryWorldAuthorityProbe.RegistrationSummary}");
                    allOk = false;
                }
                else
                {
                    RoleLogger.Info("[Shared]",
                        $"[Alpha-AuthorityProbe] registration verified: {Patches.InventoryWorldAuthorityProbe.RegistrationSummary}");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[Alpha-AuthorityProbe] VerifyRegistration exception: {ex.Message}");
                allOk = false;
            }

            try
            {
                if (!Patches.ResourceManagerWorldSyncDiagnosticPatch.VerifyRegistration())
                {
                    allOk = false;
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Resource] VerifyRegistration 整体异常: {ex.Message}");
                allOk = false;
            }

            try
            {
                if (!Patches.ObjectManagerWorldSyncDiagnosticPatch.VerifyRegistration())
                {
                    allOk = false;
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Object] VerifyRegistration 整体异常: {ex.Message}");
                allOk = false;
            }

            try
            {
                if (!Patches.VehicleManagerWorldSyncDiagnosticPatch.VerifyRegistration())
                {
                    allOk = false;
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Vehicle] VerifyRegistration 整体异常: {ex.Message}");
                allOk = false;
            }

            try
            {
                if (!Patches.AnimalManagerWorldSyncDiagnosticPatch.VerifyRegistration())
                {
                    allOk = false;
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Animal] VerifyRegistration 整体异常: {ex.Message}");
                allOk = false;
            }

            try
            {
                if (!Patches.ZombieManagerWorldSyncDiagnosticPatch.VerifyRegistration())
                {
                    allOk = false;
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Zombie] VerifyRegistration 整体异常: {ex.Message}");
                allOk = false;
            }

            //   ZombieManagerP0DGenerateZombiesPatch：onBoundUpdated Prefix supplement
            //   聚合至 DiagnosticBuildValid 阻断门，失败强制 INVALID。
            try
            {
                if (!Patches.ZombieManagerP0DGenerateZombiesPatch.VerifyRegistration())
                {
                    allOk = false;
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P0-D/Zombie] VerifyRegistration 整体异常: {ex.Message}");
                allOk = false;
            }

            //   ZombieLifecyclePatch：onBoundUpdated Prefix(VeryLow) + Postfix(High) + Finalizer(High)
            //   含 ZombieLifecycleOwnerVerify 精确 owner/MethodInfo/Priority 自检。
            //   聚合至 DiagnosticBuildValid 阻断门，失败强制 INVALID。
            try
            {
                if (!Patches.P0EZombieLifecycle.ZombieLifecyclePatch.VerifyRegistration())
                {
                    allOk = false;
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P0-E-Zombie-v6.6] VerifyRegistration 整体异常: {ex.Message}");
                allOk = false;
            }

            //   ZombieManagerP0C1SendZombieStatesPatch：updateRegionsAndSendZombieStates Transpiler
            //   聚合至 DiagnosticBuildValid 阻断门，失败强制 INVALID。
            try
            {
                if (!Patches.ZombieManagerP0C1SendZombieStatesPatch.AllRegistrationsSucceeded)
                {
                    RoleLogger.Error("[Shared]",
                        $"[P0-C-1/Zombie] !!! DIAGNOSTIC BUILD INVALID: summary={Patches.ZombieManagerP0C1SendZombieStatesPatch.RegistrationSummary} " +
                        $"replacement={Patches.ZombieManagerP0C1SendZombieStatesPatch.ReplacementCount} " +
                        $"signature={Patches.ZombieManagerP0C1SendZombieStatesPatch.SignatureResolved} " +
                        $"transpilerOwner={Patches.ZombieManagerP0C1SendZombieStatesPatch.TranspilerOwnerVerified}");
                    allOk = false;
                }
                else
                {
                    RoleLogger.Info("[Shared]",
                        $"[P0-C-1/Zombie] OK summary={Patches.ZombieManagerP0C1SendZombieStatesPatch.RegistrationSummary}");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P0-C-1/Zombie] VerifyRegistration 整体异常: {ex.Message}");
                allOk = false;
            }

            //   VehicleManagerP0C1ReplicationPatch：Update Transpiler + OnUpdate Postfix
            //   聚合至 DiagnosticBuildValid 阻断门，失败强制 INVALID。
            try
            {
                if (!Patches.VehicleManagerP0C1ReplicationPatch.AllRegistrationsSucceeded)
                {
                    RoleLogger.Error("[Shared]",
                        $"[P0-C-1/Vehicle] !!! DIAGNOSTIC BUILD INVALID: summary={Patches.VehicleManagerP0C1ReplicationPatch.RegistrationSummary} " +
                        $"transpilerReplacement={Patches.VehicleManagerP0C1ReplicationPatch.TranspilerReplacementCount} " +
                        $"signature={Patches.VehicleManagerP0C1ReplicationPatch.UpdateSignatureResolved} " +
                        $"onUpdatePostfix={Patches.VehicleManagerP0C1ReplicationPatch.OnUpdatePostfixRegistered} " +
                        $"transpilerOwner={Patches.VehicleManagerP0C1ReplicationPatch.TranspilerOwnerVerified}");
                    allOk = false;
                }
                else
                {
                    RoleLogger.Info("[Shared]",
                        $"[P0-C-1/Vehicle] OK summary={Patches.VehicleManagerP0C1ReplicationPatch.RegistrationSummary}");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P0-C-1/Vehicle] VerifyRegistration 整体异常: {ex.Message}");
                allOk = false;
            }

            //   AnimalManagerP0C2SendAnimalStatesPatch：Update Transpiler
            //   聚合至 DiagnosticBuildValid 阻断门，失败强制 INVALID。
            try
            {
                if (!Patches.AnimalManagerP0C2SendAnimalStatesPatch.AllRegistrationsSucceeded)
                {
                    RoleLogger.Error("[Shared]",
                        $"[P0-C-2/Animal] !!! DIAGNOSTIC BUILD INVALID: summary={Patches.AnimalManagerP0C2SendAnimalStatesPatch.RegistrationSummary} " +
                        $"replacement={Patches.AnimalManagerP0C2SendAnimalStatesPatch.ReplacementCount} " +
                        $"signature={Patches.AnimalManagerP0C2SendAnimalStatesPatch.SignatureResolved} " +
                        $"transpilerOwner={Patches.AnimalManagerP0C2SendAnimalStatesPatch.TranspilerOwnerVerified}");
                    allOk = false;
                }
                else
                {
                    RoleLogger.Info("[Shared]",
                        $"[P0-C-2/Animal] OK summary={Patches.AnimalManagerP0C2SendAnimalStatesPatch.RegistrationSummary}");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P0-C-2/Animal] VerifyRegistration 整体异常: {ex.Message}");
                allOk = false;
            }

            //   ItemManagerP0B3PreGeneratePatch：onLevelLoaded Transpiler
            //   聚合至 DiagnosticBuildValid 阻断门，失败强制 INVALID。
            try
            {
                if (!Patches.ItemManagerP0B3PreGeneratePatch.AllRegistrationsSucceeded)
                {
                    RoleLogger.Error("[Shared]",
                        $"[P0-B-3/Item] !!! DIAGNOSTIC BUILD INVALID: summary={Patches.ItemManagerP0B3PreGeneratePatch.RegistrationSummary} " +
                        $"replacement={Patches.ItemManagerP0B3PreGeneratePatch.ReplacementCount} " +
                        $"totalDedicatedCalls={Patches.ItemManagerP0B3PreGeneratePatch.TotalDedicatedCalls} " +
                        $"signature={Patches.ItemManagerP0B3PreGeneratePatch.SignatureResolved} " +
                        $"transpilerOwner={Patches.ItemManagerP0B3PreGeneratePatch.TranspilerOwnerVerified}");
                    allOk = false;
                }
                else
                {
                    RoleLogger.Info("[Shared]",
                        $"[P0-B-3/Item] OK summary={Patches.ItemManagerP0B3PreGeneratePatch.RegistrationSummary}");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P0-B-3/Item] VerifyRegistration 整体异常: {ex.Message}");
                allOk = false;
            }

            //   NetMessagesPlayerConnectedLoopbackPatch：SendMessageToClient Prefix
            //   聚合至 DiagnosticBuildValid 阻断门，失败强制 INVALID。
            try
            {
                if (!Patches.NetMessagesPlayerConnectedLoopbackPatch.AllRegistrationsSucceeded)
                {
                    RoleLogger.Error("[Shared]",
                        $"[P0-PlayerVisibility] !!! DIAGNOSTIC BUILD INVALID: summary={Patches.NetMessagesPlayerConnectedLoopbackPatch.RegistrationSummary} " +
                        $"prefix={Patches.NetMessagesPlayerConnectedLoopbackPatch.PrefixRegistered} " +
                        $"prefixOwner={Patches.NetMessagesPlayerConnectedLoopbackPatch.PrefixOwnerVerified}");
                    allOk = false;
                }
                else
                {
                    RoleLogger.Info("[Shared]",
                        $"[P0-PlayerVisibility] OK summary={Patches.NetMessagesPlayerConnectedLoopbackPatch.RegistrationSummary}");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P0-PlayerVisibility] VerifyRegistration 整体异常: {ex.Message}");
                allOk = false;
            }

            //   PlayerUIPauseTimeScalePatch：PlayerUI.updatePauseTimeScale Prefix
            //   修复 24th 测试中 timeScale=0.00 持续 33.32s 导致客机世界停滞的问题。
            //   聚合至 DiagnosticBuildValid 阻断门，失败强制 INVALID。
            try
            {
                if (!Patches.PlayerUIPauseTimeScalePatch.AllRegistrationsSucceeded)
                {
                    RoleLogger.Error("[Shared]",
                        $"[P0-D-ESC] !!! DIAGNOSTIC BUILD INVALID: summary={Patches.PlayerUIPauseTimeScalePatch.RegistrationSummary} " +
                        $"prefix={Patches.PlayerUIPauseTimeScalePatch.PrefixRegistered} " +
                        $"prefixOwner={Patches.PlayerUIPauseTimeScalePatch.PrefixOwnerVerified}");
                    allOk = false;
                }
                else
                {
                    RoleLogger.Info("[Shared]",
                        $"[P0-D-ESC] OK summary={Patches.PlayerUIPauseTimeScalePatch.RegistrationSummary}");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P0-D-ESC] VerifyRegistration 整体异常: {ex.Message}");
                allOk = false;
            }

            //   VehicleEnterDiagnosticPatch：VehicleManager.enterVehicle + ReceiveEnterVehicleRequest Prefix
            //   仅诊断日志，不修改 vanilla 验证逻辑。
            //   聚合至 DiagnosticBuildValid 阻断门，失败强制 INVALID。
            try
            {
                if (!Patches.VehicleEnterDiagnosticPatch.AllRegistrationsSucceeded)
                {
                    RoleLogger.Error("[Shared]",
                        $"[P0-C-1-V-a] !!! DIAGNOSTIC BUILD INVALID: summary={Patches.VehicleEnterDiagnosticPatch.RegistrationSummary} " +
                        $"enterVehicle={Patches.VehicleEnterDiagnosticPatch.EnterVehiclePrefixRegistered} " +
                        $"receiveEnterVehicleRequest={Patches.VehicleEnterDiagnosticPatch.ReceiveEnterVehicleRequestPrefixRegistered}");
                    allOk = false;
                }
                else
                {
                    RoleLogger.Info("[Shared]",
                        $"[P0-C-1-V-a] OK summary={Patches.VehicleEnterDiagnosticPatch.RegistrationSummary}");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P0-C-1-V-a] VerifyRegistration 整体异常: {ex.Message}");
                allOk = false;
            }

            //   - ItemManagerRegionSyncPatch
            //   - ResourceManagerRegionSyncPatch
            //   - ObjectManagerRegionSyncPatch
            //   每个要求：signature=true, replacement=1/1, prefix=true, transpilerOwner=true, prefixOwner=true
            //   任一失败强制 DiagnosticBuildValid=false
            bool itemRegionOk = Patches.ItemManagerRegionSyncPatch.AllRegistrationsSucceeded;
            if (!itemRegionOk)
            {
                RoleLogger.Error("[Shared]",
                    $"[Diag] !!! DIAGNOSTIC BUILD INVALID: ItemManagerRegionSyncPatch " +
                    $"summary={Patches.ItemManagerRegionSyncPatch.RegistrationSummary} " +
                    $"replacement={Patches.ItemManagerRegionSyncPatch.ReplacementCount} " +
                    $"signature={Patches.ItemManagerRegionSyncPatch.SignatureResolved} " +
                    $"askItemsPrefix={Patches.ItemManagerRegionSyncPatch.AskItemsPrefixRegistered} " +
                    $"transpilerOwner={Patches.ItemManagerRegionSyncPatch.TranspilerOwnerVerified} " +
                    $"prefixOwner={Patches.ItemManagerRegionSyncPatch.PrefixOwnerVerified}");
                allOk = false;
            }
            else
            {
                RoleLogger.Info("[Shared]",
                    $"[Diag] OK ItemManagerRegionSyncPatch: " +
                    $"replacement={Patches.ItemManagerRegionSyncPatch.ReplacementCount}/1 " +
                    $"signature={Patches.ItemManagerRegionSyncPatch.SignatureResolved} " +
                    $"askItemsPrefix={Patches.ItemManagerRegionSyncPatch.AskItemsPrefixRegistered} " +
                    $"transpilerOwner={Patches.ItemManagerRegionSyncPatch.TranspilerOwnerSummary} " +
                    $"prefixOwner={Patches.ItemManagerRegionSyncPatch.PrefixOwnerSummary}");
            }

            bool resourceRegionOk = Patches.ResourceManagerRegionSyncPatch.AllRegistrationsSucceeded;
            if (!resourceRegionOk)
            {
                RoleLogger.Error("[Shared]",
                    $"[Diag] !!! DIAGNOSTIC BUILD INVALID: ResourceManagerRegionSyncPatch " +
                    $"summary={Patches.ResourceManagerRegionSyncPatch.RegistrationSummary} " +
                    $"replacement={Patches.ResourceManagerRegionSyncPatch.ReplacementCount} " +
                    $"signature={Patches.ResourceManagerRegionSyncPatch.SignatureResolved} " +
                    $"sendResourcesWritePrefix={Patches.ResourceManagerRegionSyncPatch.SendResourcesWritePrefixRegistered} " +
                    $"transpilerOwner={Patches.ResourceManagerRegionSyncPatch.TranspilerOwnerVerified} " +
                    $"prefixOwner={Patches.ResourceManagerRegionSyncPatch.PrefixOwnerVerified}");
                allOk = false;
            }
            else
            {
                RoleLogger.Info("[Shared]",
                    $"[Diag] OK ResourceManagerRegionSyncPatch: " +
                    $"replacement={Patches.ResourceManagerRegionSyncPatch.ReplacementCount}/1 " +
                    $"signature={Patches.ResourceManagerRegionSyncPatch.SignatureResolved} " +
                    $"sendResourcesWritePrefix={Patches.ResourceManagerRegionSyncPatch.SendResourcesWritePrefixRegistered} " +
                    $"transpilerOwner={Patches.ResourceManagerRegionSyncPatch.TranspilerOwnerSummary} " +
                    $"prefixOwner={Patches.ResourceManagerRegionSyncPatch.PrefixOwnerSummary}");
            }

            bool objectRegionOk = Patches.ObjectManagerRegionSyncPatch.AllRegistrationsSucceeded;
            if (!objectRegionOk)
            {
                RoleLogger.Error("[Shared]",
                    $"[Diag] !!! DIAGNOSTIC BUILD INVALID: ObjectManagerRegionSyncPatch " +
                    $"summary={Patches.ObjectManagerRegionSyncPatch.RegistrationSummary} " +
                    $"replacement={Patches.ObjectManagerRegionSyncPatch.ReplacementCount} " +
                    $"signature={Patches.ObjectManagerRegionSyncPatch.SignatureResolved} " +
                    $"askObjectsPrefix={Patches.ObjectManagerRegionSyncPatch.AskObjectsPrefixRegistered} " +
                    $"transpilerOwner={Patches.ObjectManagerRegionSyncPatch.TranspilerOwnerVerified} " +
                    $"prefixOwner={Patches.ObjectManagerRegionSyncPatch.PrefixOwnerVerified}");
                allOk = false;
            }
            else
            {
                RoleLogger.Info("[Shared]",
                    $"[Diag] OK ObjectManagerRegionSyncPatch: " +
                    $"replacement={Patches.ObjectManagerRegionSyncPatch.ReplacementCount}/1 " +
                    $"signature={Patches.ObjectManagerRegionSyncPatch.SignatureResolved} " +
                    $"askObjectsPrefix={Patches.ObjectManagerRegionSyncPatch.AskObjectsPrefixRegistered} " +
                    $"transpilerOwner={Patches.ObjectManagerRegionSyncPatch.TranspilerOwnerSummary} " +
                    $"prefixOwner={Patches.ObjectManagerRegionSyncPatch.PrefixOwnerSummary}");
            }

            //   - UseableBarricadeDiagnosticPatch：8 DP（startPrimary/check/checkSpace/checkClaims/ReceiveBarricadeNone/simulate/build/dropBarricade）
            //   - ZombieEntityMappingDiagnosticPatch：7 DP（SendZombies/ReceiveZombies/SendZombieStates/ReceiveZombieStates/onBoundUpdated/sendZombieDead+Alive/ReceiveZombieDead+Alive）
            //   - PlayerManagerCullingDiagnosticPatch：3 DP（SendPlayerStates_Write Prefix/ReceivePlayerStates Postfix/tellState Prefix）
            //   任一失败强制 DiagnosticBuildValid=false，聚合至阻断门。
            try
            {
                if (!Patches.P0EDiagnostic.UseableBarricadeDiagnosticPatch.AllRegistrationsSucceeded)
                {
                    RoleLogger.Error("[Shared]",
                        $"[P0-E-2-Diag] !!! DIAGNOSTIC BUILD INVALID: UseableBarricadeDiagnosticPatch " +
                        $"dp1={Patches.P0EDiagnostic.UseableBarricadeDiagnosticPatch.DP1_StartPrimary_Registered} " +
                        $"dp2={Patches.P0EDiagnostic.UseableBarricadeDiagnosticPatch.DP2_Check_Registered} " +
                        $"dp3={Patches.P0EDiagnostic.UseableBarricadeDiagnosticPatch.DP3_CheckSpace_Registered} " +
                        $"dp4={Patches.P0EDiagnostic.UseableBarricadeDiagnosticPatch.DP4_CheckClaims_Registered} " +
                        $"dp5={Patches.P0EDiagnostic.UseableBarricadeDiagnosticPatch.DP5_ReceiveBarricadeNone_Registered} " +
                        $"dp5Finalizer={Patches.P0EDiagnostic.UseableBarricadeDiagnosticPatch.DP5_Finalizer_Registered} " +
                        $"owner5Finalizer={Patches.P0EDiagnostic.UseableBarricadeDiagnosticPatch.DP5_Finalizer_OwnerVerified} " +
                        $"ownerSummary=\"{Patches.P0EDiagnostic.UseableBarricadeDiagnosticPatch.DP5_Finalizer_OwnerSummary}\" " +
                        $"dp6={Patches.P0EDiagnostic.UseableBarricadeDiagnosticPatch.DP6_Simulate_Registered} " +
                        $"dp7={Patches.P0EDiagnostic.UseableBarricadeDiagnosticPatch.DP7_Build_Registered} " +
                        $"dp8={Patches.P0EDiagnostic.UseableBarricadeDiagnosticPatch.DP8_DropBarricade_Registered}");
                    allOk = false;
                }
                else
                {
                    RoleLogger.Info("[Shared]",
                        $"[P0-E-2-Diag] OK " +
                        $"dp1={Patches.P0EDiagnostic.UseableBarricadeDiagnosticPatch.DP1_StartPrimary_Registered} " +
                        $"dp2={Patches.P0EDiagnostic.UseableBarricadeDiagnosticPatch.DP2_Check_Registered} " +
                        $"dp3={Patches.P0EDiagnostic.UseableBarricadeDiagnosticPatch.DP3_CheckSpace_Registered} " +
                        $"dp4={Patches.P0EDiagnostic.UseableBarricadeDiagnosticPatch.DP4_CheckClaims_Registered} " +
                        $"dp5={Patches.P0EDiagnostic.UseableBarricadeDiagnosticPatch.DP5_ReceiveBarricadeNone_Registered} " +
                        $"dp5Finalizer={Patches.P0EDiagnostic.UseableBarricadeDiagnosticPatch.DP5_Finalizer_Registered} " +
                        $"owner5Finalizer={Patches.P0EDiagnostic.UseableBarricadeDiagnosticPatch.DP5_Finalizer_OwnerVerified} " +
                        $"ownerSummary=\"{Patches.P0EDiagnostic.UseableBarricadeDiagnosticPatch.DP5_Finalizer_OwnerSummary}\" " +
                        $"dp6={Patches.P0EDiagnostic.UseableBarricadeDiagnosticPatch.DP6_Simulate_Registered} " +
                        $"dp7={Patches.P0EDiagnostic.UseableBarricadeDiagnosticPatch.DP7_Build_Registered} " +
                        $"dp8={Patches.P0EDiagnostic.UseableBarricadeDiagnosticPatch.DP8_DropBarricade_Registered}");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P0-E-2-Diag] VerifyRegistration 整体异常: {ex.Message}");
                allOk = false;
            }
            try
            {
                if (!Patches.P0EDiagnostic.ZombieEntityMappingDiagnosticPatch.AllRegistrationsSucceeded)
                {
                    RoleLogger.Error("[Shared]",
                        $"[P0-E-1-Diag/Zombie] !!! DIAGNOSTIC BUILD INVALID: ZombieEntityMappingDiagnosticPatch " +
                        $"dp1={Patches.P0EDiagnostic.ZombieEntityMappingDiagnosticPatch.DP1_SendZombiesWrite_Registered} " +
                        $"dp2={Patches.P0EDiagnostic.ZombieEntityMappingDiagnosticPatch.DP2_ReceiveZombies_Registered} " +
                        $"dp3={Patches.P0EDiagnostic.ZombieEntityMappingDiagnosticPatch.DP3_SendZombieStatesWrite_Registered} " +
                        $"dp4={Patches.P0EDiagnostic.ZombieEntityMappingDiagnosticPatch.DP4_ReceiveZombieStates_Registered} " +
                        $"dp5={Patches.P0EDiagnostic.ZombieEntityMappingDiagnosticPatch.DP5_OnBoundUpdated_Registered} " +
                        $"dp6={Patches.P0EDiagnostic.ZombieEntityMappingDiagnosticPatch.DP6_SendZombieDead_Registered} " +
                        $"dp7={Patches.P0EDiagnostic.ZombieEntityMappingDiagnosticPatch.DP7_ReceiveZombieDead_Registered} " +
                        $"dp8_7={Patches.P0EDiagnostic.ZombieEntityMappingDiagnosticPatch.DP8_7_Destroy_Registered} " +
                        $"owner8_7={Patches.P0EDiagnostic.ZombieEntityMappingDiagnosticPatch.DP8_7_Destroy_OwnerVerified} " +
                        $"reflectionFailed={Patches.P0EDiagnostic.ZombieEntityMappingDiagnosticPatch.ReflectionFailed}");
                    allOk = false;
                }
                else
                {
                    RoleLogger.Info("[Shared]",
                        $"[P0-E-1-Diag/Zombie] OK " +
                        $"dp1={Patches.P0EDiagnostic.ZombieEntityMappingDiagnosticPatch.DP1_SendZombiesWrite_Registered} " +
                        $"dp2={Patches.P0EDiagnostic.ZombieEntityMappingDiagnosticPatch.DP2_ReceiveZombies_Registered} " +
                        $"dp3={Patches.P0EDiagnostic.ZombieEntityMappingDiagnosticPatch.DP3_SendZombieStatesWrite_Registered} " +
                        $"dp4={Patches.P0EDiagnostic.ZombieEntityMappingDiagnosticPatch.DP4_ReceiveZombieStates_Registered} " +
                        $"dp5={Patches.P0EDiagnostic.ZombieEntityMappingDiagnosticPatch.DP5_OnBoundUpdated_Registered} " +
                        $"dp6={Patches.P0EDiagnostic.ZombieEntityMappingDiagnosticPatch.DP6_SendZombieDead_Registered} " +
                        $"dp7={Patches.P0EDiagnostic.ZombieEntityMappingDiagnosticPatch.DP7_ReceiveZombieDead_Registered} " +
                        $"dp8_7={Patches.P0EDiagnostic.ZombieEntityMappingDiagnosticPatch.DP8_7_Destroy_Registered} " +
                        $"owner8_7={Patches.P0EDiagnostic.ZombieEntityMappingDiagnosticPatch.DP8_7_Destroy_OwnerVerified} " +
                        $"reflectionFailed={Patches.P0EDiagnostic.ZombieEntityMappingDiagnosticPatch.ReflectionFailed}");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P0-E-1-Diag/Zombie] VerifyRegistration 整体异常: {ex.Message}");
                allOk = false;
            }
            try
            {
                if (!Patches.P0EDiagnostic.PlayerManagerCullingDiagnosticPatch.AllRegistrationsSucceeded)
                {
                    RoleLogger.Error("[Shared]",
                        $"[P0-E-1-Diag/Culling] !!! DIAGNOSTIC BUILD INVALID: PlayerManagerCullingDiagnosticPatch " +
                        $"dp1={Patches.P0EDiagnostic.PlayerManagerCullingDiagnosticPatch.DP1_SendPlayerStatesWritePrefix_Registered} " +
                        $"dp2={Patches.P0EDiagnostic.PlayerManagerCullingDiagnosticPatch.DP2_ReceivePlayerStatesPostfix_Registered} " +
                        $"dp3={Patches.P0EDiagnostic.PlayerManagerCullingDiagnosticPatch.DP3_TellStatePrefix_Registered}");
                    allOk = false;
                }
                else
                {
                    RoleLogger.Info("[Shared]",
                        $"[P0-E-1-Diag/Culling] OK " +
                        $"dp1={Patches.P0EDiagnostic.PlayerManagerCullingDiagnosticPatch.DP1_SendPlayerStatesWritePrefix_Registered} " +
                        $"dp2={Patches.P0EDiagnostic.PlayerManagerCullingDiagnosticPatch.DP2_ReceivePlayerStatesPostfix_Registered} " +
                        $"dp3={Patches.P0EDiagnostic.PlayerManagerCullingDiagnosticPatch.DP3_TellStatePrefix_Registered}");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P0-E-1-Diag/Culling] VerifyRegistration 整体异常: {ex.Message}");
                allOk = false;
            }

            //   BarricadeLifecycleRegistration 原子登记 + owner/priority/ReplacementApplied 自检
            //   失败 fail-closed（DiagnosticBuildValid=false），聚合至阻断门。
            //   仅两个 Transpiler：equip + checkClaims；不全局伪造 Dedicator.IsDedicatedServer。
            try
            {
                if (!Patches.P0EBarricadeLifecycle.BarricadeLifecycleRegistration.DiagnosticBuildValid)
                {
                    RoleLogger.Error("[Shared]",
                        $"[5B-1B] !!! DIAGNOSTIC BUILD INVALID: BarricadeLifecycleRegistration " +
                        $"registrationSucceeded={Patches.P0EBarricadeLifecycle.BarricadeLifecycleRegistration.IsRegistrationSucceeded} " +
                        $"rollbackAttempted={Patches.P0EBarricadeLifecycle.BarricadeLifecycleRegistration.WasRollbackAttempted} " +
                        $"rollbackClean={Patches.P0EBarricadeLifecycle.BarricadeLifecycleRegistration.IsRollbackClean} " +
                        $"equipReplacementApplied={Patches.P0EBarricadeLifecycle.BarricadeLifecycleRegistration.EquipReplacementApplied} " +
                        $"checkClaimsReplacementApplied={Patches.P0EBarricadeLifecycle.BarricadeLifecycleRegistration.CheckClaimsReplacementApplied}");
                    allOk = false;
                }
                else
                {
                    RoleLogger.Info("[Shared]",
                        $"[5B-1B] OK BarricadeLifecycleRegistration " +
                        $"registrationSucceeded={Patches.P0EBarricadeLifecycle.BarricadeLifecycleRegistration.IsRegistrationSucceeded} " +
                        $"equipReplacementApplied={Patches.P0EBarricadeLifecycle.BarricadeLifecycleRegistration.EquipReplacementApplied} " +
                        $"checkClaimsReplacementApplied={Patches.P0EBarricadeLifecycle.BarricadeLifecycleRegistration.CheckClaimsReplacementApplied}");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[5B-1B] VerifyRegistration 整体异常: {ex.Message}");
                allOk = false;
            }

            //   attribute 单测不足以证明激活；VerifyP2PWhitelistCaptureRegistration 用
            //   Harmony.GetPatchInfo 精确验证 owner + Prefix MethodInfo。失败强制 INVALID。
            try
            {
                if (!CaptureRegistrationValid)
                {
                    RoleLogger.Error("[Shared]",
                        "[P2P-Approval] !!! DIAGNOSTIC BUILD INVALID: Provider.reject capture Prefix 未激活 " +
                        "(CaptureRegistrationValid=false) - P2P fail-closed");
                    allOk = false;
                }
                else
                {
                    RoleLogger.Info("[Shared]",
                        "[P2P-Approval] OK Provider.reject capture Prefix 已激活 (owner=" + HARMONY_ID + ")");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P2P-Approval] CaptureRegistration 聚合异常: {ex.Message}");
                allOk = false;
            }

            try
            {
                if (!Stage75TakeoverRegistrationValid)
                {
                    RoleLogger.Error("[Shared]",
                        "[Stage7-5] !!! DIAGNOSTIC BUILD INVALID: takeover registration gate failed");
                    allOk = false;
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", "[Stage7-5] registration aggregate failed: " + ex.Message);
                allOk = false;
            }

            try
            {
                if (!Stage76QuarantineRegistrationValid)
                {
                    RoleLogger.Error("[Shared]",
                        "[Stage7-6] !!! DIAGNOSTIC BUILD INVALID: quarantine admission/UI registration gate failed");
                    allOk = false;
                }
                else
                {
                    RoleLogger.Info("[Shared]",
                        "[Stage7-6] OK ReadyToConnect scope + InvokeMethod gate + U-list decorator + signal bit");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", "[Stage7-6] registration aggregate failed: " + ex.Message);
                allOk = false;
            }

            try
            {
                if (!Stage78UnifiedRegistrationValid)
                {
                    RoleLogger.Error("[Shared]",
                        "[Stage7-8] !!! DIAGNOSTIC BUILD INVALID: unified connect registration gate failed");
                    allOk = false;
                }
                else
                {
                    RoleLogger.Info("[Shared]",
                        "[Stage7-8] OK original connect route preserved; individual SteamID interception active");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", "[Stage7-8] registration aggregate failed: " + ex.Message);
                allOk = false;
            }

            try
            {
                if (!Stage92SinglePortRegistrationValid)
                {
                    RoleLogger.Error("[Shared]",
                        "[Stage9-2] !!! DIAGNOSTIC BUILD INVALID: single-port Direct-IP query projection gate failed");
                    allOk = false;
                }
                else
                {
                    RoleLogger.Info("[Shared]",
                        "[Stage9-2] OK single-port Direct-IP query projection active");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", "[Stage9-2] registration aggregate failed: " + ex.Message);
                allOk = false;
            }

            // makes the diagnostic build invalid.
            try
            {
                if (!VerifyStage10DeathCommitRegistration())
                {
                    allOk = false;
                }
                if (!VerifyStage10ChatAvatarRegistration())
                {
                    allOk = false;
                }
                if (!VerifyInventoryUiProjectionRegistration())
                {
                    allOk = false;
                }
                if (!Stage10WorldBroadcastActivationValid ||
                    P2PWorldStatusBroadcaster.ActivationState ==
                        P2PWorldStatusBroadcaster.EWorldBroadcastActivationState.Failed)
                {
                    RoleLogger.Error("[Shared]",
                        "[Stage10] !!! DIAGNOSTIC BUILD INVALID: world-status broadcast activation failed " +
                        "(deferred subscribe to PlayerLife.onPlayerDied failed)");
                    allOk = false;
                }
                else
                {
                    RoleLogger.Info("[Shared]",
                        "[Stage10] OK world-status broadcast activation state=" +
                        P2PWorldStatusBroadcaster.ActivationState);
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", "[Stage10] activation aggregate failed: " + ex.Message);
                allOk = false;
            }

            DiagnosticBuildValid = allOk;

            try
            {
                HarmonyCompatibilityAudit.WriteReport(DiagnosticBuildValid);
            }
            catch (System.Exception ex)
            {
                // Compatibility evidence is diagnostic output. A filesystem failure must not change P2P behavior.
                RoleLogger.Warn("[Shared]", "[Compat] failed to write compatibility report: " + ex.Message);
            }

            // 原生 SNS 输出是可选日志源，不能影响连接、白名单或世界同步的功能门控。
            try
            {
                bool routeDiagOn = RouteDiagnostics != null && RouteDiagnostics.Value;
                bool verboseOn = VerboseLog != null && VerboseLog.Value;
                if (routeDiagOn && verboseOn)
                {
                    if (SteamP2PFriends.Client.NativeSnsLogProbe.EnableFailed)
                    {
                        RoleLogger.Warn("[Shared]",
                            $"[Diag] NativeSnsLogProbe 不可用，继续使用常规日志：" +
                            $"(IsEnabled={SteamP2PFriends.Client.NativeSnsLogProbe.IsEnabled}, " +
                            $"EnableFailed={SteamP2PFriends.Client.NativeSnsLogProbe.EnableFailed})");
                    }
                    else if (!SteamP2PFriends.Client.NativeSnsLogProbe.IsEnabled
                             && SteamP2PFriends.Client.NativeSnsLogProbe.WaitingForSteamworks)
                    {
                        RoleLogger.Info("[Shared]",
                            "[Diag] NativeSnsLogProbe 等待 Steamworks 初始化后重试。");
                    }
                    else if (!SteamP2PFriends.Client.NativeSnsLogProbe.IsEnabled)
                    {
                        RoleLogger.Warn("[Shared]",
                            $"[Diag] NativeSnsLogProbe 未启用，继续使用常规日志：" +
                            $"(IsEnabled={SteamP2PFriends.Client.NativeSnsLogProbe.IsEnabled}, " +
                            $"EnableFailed={SteamP2PFriends.Client.NativeSnsLogProbe.EnableFailed}, " +
                            $"WaitingForSteamworks={SteamP2PFriends.Client.NativeSnsLogProbe.WaitingForSteamworks})");
                    }
                    else
                    {
                        RoleLogger.Info("[Shared]",
                            "[Diag] NativeSnsLogProbe 已启用。");
                    }
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Shared]", $"[Diag] NativeSnsLogProbe 状态读取异常（不阻断）: {ex.Message}");
            }

            if (allOk)
            {
                RoleLogger.Info("[Shared]", "[Diag] 关键补丁自检通过 (DiagnosticBuildValid=true)");
            }
            else
            {
                RoleLogger.Error("[Shared]", "========================================");
                RoleLogger.Error("[Shared]", "!!! DIAGNOSTIC BUILD INVALID !!!");
                RoleLogger.Error("[Shared]", "!!! 关键 Patch 未登记、重复或存在策略阻断的冲突，已禁用所有 P2P 入口 !!!");
                RoleLogger.Error("[Shared]", "!!! Update/OnGUI 已挂起，禁止进入双机测试 !!!");
                RoleLogger.Error("[Shared]", "!!! 请检查 Harmony 目标签名 / 程序集版本 / compatibility report !!!");
                RoleLogger.Error("[Shared]", "========================================");
                //   新增 Item/Resource/Object RegionSync Transpiler + Prefix 精确 1/1 自检。
                //   目标：解除 listen server 模式下"主机不向远程客机发送 Item/Resource/Object RPC"的诅咒。
                //   INVALID 时仅通过 DiagnosticBuildValid 临时阻断所有 P2P 入口（OnGUI/Update 顶部
                //   if (!DiagnosticBuildValid) return; 已是硬门控），不再永久篡改 EnableP2PCoop.Value。
                //   原因：EnableP2PCoop.Value = false 会通过 BepInEx ConfigEntry setter 自动持久化到
                //   com.yu80rice.steamp2pfriends.cfg，单向不可逆，下次启动 VALID 时不会自动恢复 true，
                //   篡改了用户合法配置。INVALID 时 cfg 保持用户设定值，VALID 时自然恢复。
            }
        }

        /// <summary>
        /// NetMessages 是 internal class，编译时无法 typeof()，运行时通过 AccessTools.TypeByName 反射。
        /// 实际登记：SendMessageToClient + SendMessageToClients (Prefix + Finalizer)
        /// </summary>
        private static bool VerifyNetMessagesPatches()
        {
            try
            {
                System.Type netMessagesType = AccessTools.TypeByName("SDG.Unturned.NetMessages");
                if (netMessagesType == null)
                {
                    RoleLogger.Error("[Shared]", "[Diag] !!! D-5 NetMessages: TypeByName 返回 null");
                    return false;
                }

                System.Type clientWriteHandlerType = netMessagesType.GetNestedType("ClientWriteHandler");
                if (clientWriteHandlerType == null)
                {
                    RoleLogger.Error("[Shared]", "[Diag] !!! D-5 NetMessages.ClientWriteHandler: GetNestedType 返回 null");
                    return false;
                }

                bool ok = true;

                // NetMessages.SendMessageToClient(EClientMessage, ENetReliability, ITransportConnection, ClientWriteHandler)
                ok &= VerifyPatch(netMessagesType, "SendMessageToClient",
                    new System.Type[] {
                        typeof(EClientMessage), typeof(ENetReliability),
                        typeof(SDG.NetTransport.ITransportConnection), clientWriteHandlerType
                    },
                    "D-5 NetMessages.SendMessageToClient", requirePrefix: true, requireFinalizer: true);

                // NetMessages.SendMessageToClients(EClientMessage, ENetReliability, List<ITransportConnection>, ClientWriteHandler)
                ok &= VerifyPatch(netMessagesType, "SendMessageToClients",
                    new System.Type[] {
                        typeof(EClientMessage), typeof(ENetReliability),
                        typeof(System.Collections.Generic.List<SDG.NetTransport.ITransportConnection>), clientWriteHandlerType
                    },
                    "D-5 NetMessages.SendMessageToClients(List)", requirePrefix: true, requireFinalizer: true);

                return ok;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[Diag] VerifyNetMessagesPatches 异常: {ex.Message}");
                return false;
            }
        }

        private static bool VerifyPatch(System.Type targetType, string methodName,
            System.Type[] paramTypes, string description,
            bool requirePrefix = false, bool requirePostfix = false, bool requireFinalizer = false)
        {
            try
            {
                if (targetType == null)
                {
                    RoleLogger.Error("[Shared]", $"[Diag] !!! {description}: targetType=null");
                    return false;
                }

                System.Reflection.MethodInfo method = AccessTools.Method(targetType, methodName, paramTypes);
                if (method == null)
                {
                    RoleLogger.Error("[Shared]",
                        $"[Diag] !!! {description}: AccessTools.Method 返回 null（方法未找到）type={targetType.FullName} argCount={paramTypes?.Length ?? 0}");
                    return false;
                }

                HarmonyLib.Patches patches = Harmony.GetPatchInfo(method);
                int prefixCount = HarmonyCompatibilityAudit.CountOwned(patches?.Prefixes);
                int postfixCount = HarmonyCompatibilityAudit.CountOwned(patches?.Postfixes);
                int finalizerCount = HarmonyCompatibilityAudit.CountOwned(patches?.Finalizers);

                bool ok = true;
                if (requirePrefix && prefixCount == 0)
                {
                    RoleLogger.Error("[Shared]", $"[Diag] !!! {description}: Prefix 未登记");
                    ok = false;
                }
                if (requirePostfix && postfixCount == 0)
                {
                    RoleLogger.Error("[Shared]", $"[Diag] !!! {description}: Postfix 未登记");
                    ok = false;
                }
                if (requireFinalizer && finalizerCount == 0)
                {
                    RoleLogger.Error("[Shared]", $"[Diag] !!! {description}: Finalizer 未登记");
                    ok = false;
                }

                if (!HarmonyCompatibilityAudit.Inspect(method, description))
                {
                    RoleLogger.Error("[Shared]", $"[Compat] !!! {description}: blocking foreign Harmony patch");
                    ok = false;
                }

                if (ok)
                {
                    RoleLogger.Info("[Shared]",
                        $"[Diag] OK {description}: ownPrefixes={prefixCount} ownPostfixes={postfixCount} ownFinalizers={finalizerCount}");
                }
                return ok;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[Diag] VerifyPatch({description}) 异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 检查 Patches 列表中是否存在 patch.PatchMethod 来自 expectedDeclaringType 的 expectedMethodName。
        /// 泛型类型用 GetGenericTypeDefinition 比较。
        /// </summary>
        private static bool VerifyPatchMethod(
            System.Collections.Generic.IList<HarmonyLib.Patch> list,
            System.Type expectedDeclaringType,
            string expectedMethodName,
            string description,
            string kind)
        {
            if (list == null || list.Count == 0)
            {
                RoleLogger.Error("[Shared]",
                    $"[Diag] !!! {description}: {kind} 未登记（期望 {expectedDeclaringType.FullName}.{expectedMethodName}）");
                return false;
            }

            bool found = false;
            foreach (HarmonyLib.Patch p in list)
            {
                if (p.owner != HARMONY_ID) continue;
                System.Reflection.MethodInfo pm = p.PatchMethod;
                if (ReferenceEquals(pm, null)) continue;
                System.Type dt = pm.DeclaringType;
                if (ReferenceEquals(dt, null)) continue;

                bool typeMatch;
                if (expectedDeclaringType.IsGenericTypeDefinition)
                {
                    // 泛型类型比较：BitmaskPostfixCache<T> 的 DeclaringType 是 BitmaskPostfixCache<PlayerClothing> 等
                    typeMatch = dt.IsGenericType && dt.GetGenericTypeDefinition() == expectedDeclaringType;
                }
                else
                {
                    typeMatch = dt == expectedDeclaringType;
                }

                if (typeMatch && pm.Name == expectedMethodName)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                RoleLogger.Error("[Shared]",
                    $"[Diag] !!! {description}: {kind} 期望 {expectedDeclaringType.FullName}.{expectedMethodName} 未登记");
                return false;
            }
            return true;
        }

        /// <summary>
        ///   不仅检查 owner 和数量，还检查 patch.PatchMethod 的 DeclaringType 和 Name 是否匹配预期。
        ///   用于 5 组关键 patch：AcceptConnection / SetConnectionPollGroup / ClientSns D10 / ServerSns D10 / RequestDisconnect。
        ///   任一缺失或名字不匹配都返回 false。
        /// </summary>
        private static bool VerifyPatchMethodPair(
            System.Type targetType, string methodName, System.Type[] paramTypes,
            System.Type expectedPatchDeclaringType,
            string expectedPrefixName, string expectedPostfixName,
            string description)
        {
            try
            {
                if (targetType == null)
                {
                    RoleLogger.Error("[Shared]", $"[Diag] !!! {description}: targetType=null");
                    return false;
                }

                System.Reflection.MethodInfo method = AccessTools.Method(targetType, methodName, paramTypes);
                if (method == null)
                {
                    RoleLogger.Error("[Shared]",
                        $"[Diag] !!! {description}: AccessTools.Method 返回 null type={targetType.FullName} method={methodName}");
                    return false;
                }

                HarmonyLib.Patches patches = Harmony.GetPatchInfo(method);
                bool ok = true;

                // 精确方法验证（declaring type + method name）
                ok &= VerifyPatchMethod(patches?.Prefixes, expectedPatchDeclaringType, expectedPrefixName, description, "Prefix");
                ok &= VerifyPatchMethod(patches?.Postfixes, expectedPatchDeclaringType, expectedPostfixName, description, "Postfix");

                if (!HarmonyCompatibilityAudit.Inspect(method, description))
                {
                    RoleLogger.Error("[Shared]", $"[Compat] !!! {description}: blocking foreign Harmony patch");
                    ok = false;
                }

                if (ok)
                {
                    RoleLogger.Info("[Shared]",
                        $"[Diag] OK {description}: {expectedPatchDeclaringType.Name}.{expectedPrefixName} + " +
                        $"{expectedPostfixName} 精确验证通过");
                }
                return ok;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[Diag] VerifyPatchMethodPair({description}) 异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        ///   用于 auth callback safety patch（只有 Prefix，无 Postfix）。
        ///   不仅检查 owner 和数量，还检查 patch.PatchMethod 的 DeclaringType 和 Name 是否匹配预期。
        ///   任一缺失或名字不匹配都返回 false。
        /// </summary>
        private static bool VerifyAuthCallbackSafetyPatchMethod(
            System.Type targetType, string methodName,
            System.Type expectedPatchDeclaringType,
            string expectedPrefixName,
            string description)
        {
            try
            {
                if (targetType == null)
                {
                    RoleLogger.Error("[Shared]", $"[Diag] !!! {description}: targetType=null");
                    return false;
                }

                System.Reflection.MethodInfo method = AccessTools.Method(targetType, methodName);
                if (method == null)
                {
                    RoleLogger.Error("[Shared]",
                        $"[Diag] !!! {description}: AccessTools.Method 返回 null type={targetType.FullName} method={methodName}");
                    return false;
                }

                HarmonyLib.Patches patches = Harmony.GetPatchInfo(method);
                bool ok = true;

                // 精确方法验证（declaring type + method name）
                ok &= VerifyPatchMethod(patches?.Prefixes, expectedPatchDeclaringType, expectedPrefixName, description, "Prefix");

                if (!HarmonyCompatibilityAudit.Inspect(method, description))
                {
                    RoleLogger.Error("[Shared]", $"[Compat] !!! {description}: blocking foreign Harmony patch");
                    ok = false;
                }

                if (ok)
                {
                    RoleLogger.Info("[Shared]",
                        $"[Diag] OK {description}: {expectedPatchDeclaringType.Name}.{expectedPrefixName} 精确验证通过");
                }
                return ok;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[Diag] VerifyAuthCallbackSafetyPatchMethod({description}) 异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 总数触发误报，而是交给 HarmonyCompatibilityAudit 按 transport 关键目标策略裁决。
        /// </summary>
        private static bool VerifyClientMethodLoopbackPrefix(
            System.Type targetType, string methodName, System.Type[] paramTypes,
            string expectedPrefixName, string description)
        {
            try
            {
                if (targetType == null)
                {
                    RoleLogger.Error("[Shared]", $"[Diag] !!! {description}: targetType=null");
                    return false;
                }

                System.Reflection.MethodInfo method = AccessTools.Method(targetType, methodName, paramTypes);
                if (method == null)
                {
                    RoleLogger.Error("[Shared]",
                        $"[Diag] !!! {description}: AccessTools.Method 返回 null type={targetType.FullName} method={methodName} argCount={paramTypes?.Length ?? 0}");
                    return false;
                }

                HarmonyLib.Patches patches = Harmony.GetPatchInfo(method);
                int ownPrefixCount = HarmonyCompatibilityAudit.CountOwned(patches?.Prefixes);
                if (ownPrefixCount == 0)
                {
                    RoleLogger.Error("[Shared]",
                        $"[Diag] !!! {description}: own Prefix 未登记");
                    return false;
                }

                int exactMatchCount = 0;
                if (patches?.Prefixes != null)
                {
                    foreach (HarmonyLib.Patch p in patches.Prefixes)
                    {
                        if (p.owner != HARMONY_ID) continue;
                        System.Reflection.MethodInfo pm = p.PatchMethod;
                        if (ReferenceEquals(pm, null)) continue;
                        if (pm.DeclaringType == typeof(Patches.ClientMethodLoopbackPatch)
                            && pm.Name == expectedPrefixName)
                        {
                            exactMatchCount++;
                        }
                    }
                }

                bool ok = true;

                // 检查 1: own total == 1. Other owners are classified separately.
                if (ownPrefixCount != 1)
                {
                    RoleLogger.Error("[Shared]",
                        $"[Diag] !!! {description}: ownPrefixCount={ownPrefixCount} 期望=1 (exactMatch={exactMatchCount})");
                    ok = false;
                }

                // 检查 2: exact == 1
                if (exactMatchCount != 1)
                {
                    RoleLogger.Error("[Shared]",
                        $"[Diag] !!! {description}: exactMatchCount={exactMatchCount} 期望=1 (期望 {typeof(Patches.ClientMethodLoopbackPatch).FullName}.{expectedPrefixName})");
                    ok = false;
                }

                // SendAndLoopback* belongs to the exclusive P2P transport contract.
                if (!HarmonyCompatibilityAudit.Inspect(method, description))
                {
                    RoleLogger.Error("[Shared]",
                        $"[Compat] !!! {description}: blocking foreign Harmony patch");
                    ok = false;
                }

                if (ok)
                {
                    RoleLogger.Info("[Shared]",
                        $"[Diag] OK {description}: ownTotal=1 exact=1 ({typeof(Patches.ClientMethodLoopbackPatch).Name}.{expectedPrefixName})");
                }
                return ok;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[Diag] VerifyClientMethodLoopbackPrefix({description}) 异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 使用泛型方法以获取具体的 BitmaskPostfixCache<T> 类型。
        /// </summary>
        private static bool VerifyBitmaskPostfix<T>(string description)
        {
            try
            {
                System.Reflection.MethodInfo method = AccessTools.Method(typeof(T), "InitializePlayer");
                if (method == null)
                {
                    RoleLogger.Error("[Shared]", $"[Diag] !!! {description}: InitializePlayer 反射失败");
                    return false;
                }

                HarmonyLib.Patches patches = Harmony.GetPatchInfo(method);
                int postfixCount = HarmonyCompatibilityAudit.CountOwned(patches?.Postfixes);
                if (postfixCount == 0)
                {
                    RoleLogger.Error("[Shared]", $"[Diag] !!! {description}: Postfix 未登记");
                    return false;
                }

                if (!HarmonyCompatibilityAudit.Inspect(method, description))
                {
                    RoleLogger.Error("[Shared]", $"[Compat] !!! {description}: blocking foreign Harmony patch");
                    return false;
                }

                // 精确方法验证：BitmaskPostfixCache<T>.Postfix（泛型类型定义比较）
                System.Type expectedGenericType = typeof(SteamP2PFriends.Patches.BitmaskPostfixCache<T>);
                bool found = false;
                foreach (HarmonyLib.Patch p in patches.Postfixes)
                {
                    if (p.owner != HARMONY_ID) continue;
                    System.Reflection.MethodInfo pm = p.PatchMethod;
                    if (ReferenceEquals(pm, null)) continue;
                    System.Type dt = pm.DeclaringType;
                    if (ReferenceEquals(dt, null)) continue;

                    if (dt.IsGenericType && dt.GetGenericTypeDefinition() == expectedGenericType.GetGenericTypeDefinition()
                        && pm.Name == "Postfix")
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    RoleLogger.Error("[Shared]",
                        $"[Diag] !!! {description}: BitmaskPostfixCache<{typeof(T).Name}>.Postfix 未登记");
                    return false;
                }

                RoleLogger.Info("[Shared]", $"[Diag] OK {description}: BitmaskPostfix Postfix 已登记");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[Diag] VerifyBitmaskPostfix({description}) 异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 使用 AccessTools.Method(Type, string) 反射（依赖 Harmony 模糊匹配）。
        /// 支持 require* 参数 + own-owner 验证 + 第三方兼容性策略，并返回 bool 参与 allOk 聚合。
        /// </summary>
        private static bool VerifyPatch(System.Type targetType, string methodName, string description,
            bool requirePrefix = false, bool requirePostfix = false, bool requireFinalizer = false)
        {
            try
            {
                if (targetType == null)
                {
                    RoleLogger.Error("[Shared]", $"[Diag] !!! {description}: targetType=null");
                    return false;
                }

                System.Reflection.MethodInfo method = AccessTools.Method(targetType, methodName);
                if (method == null)
                {
                    RoleLogger.Error("[Shared]",
                        $"[Diag] !!! {description}: AccessTools.Method 返回 null（方法未找到）type={targetType.FullName}");
                    return false;
                }

                HarmonyLib.Patches patches = Harmony.GetPatchInfo(method);
                int prefixCount = HarmonyCompatibilityAudit.CountOwned(patches?.Prefixes);
                int postfixCount = HarmonyCompatibilityAudit.CountOwned(patches?.Postfixes);
                int finalizerCount = HarmonyCompatibilityAudit.CountOwned(patches?.Finalizers);

                bool ok = true;
                if (requirePrefix && prefixCount == 0)
                {
                    RoleLogger.Error("[Shared]", $"[Diag] !!! {description}: Prefix 未登记");
                    ok = false;
                }
                if (requirePostfix && postfixCount == 0)
                {
                    RoleLogger.Error("[Shared]", $"[Diag] !!! {description}: Postfix 未登记");
                    ok = false;
                }
                if (requireFinalizer && finalizerCount == 0)
                {
                    RoleLogger.Error("[Shared]", $"[Diag] !!! {description}: Finalizer 未登记");
                    ok = false;
                }

                if (!HarmonyCompatibilityAudit.Inspect(method, description))
                {
                    RoleLogger.Error("[Shared]", $"[Compat] !!! {description}: blocking foreign Harmony patch");
                    ok = false;
                }

                if (ok)
                {
                    RoleLogger.Info("[Shared]",
                        $"[Diag] OK {description}: ownPrefixes={prefixCount} ownPostfixes={postfixCount} ownFinalizers={finalizerCount}");
                }
                return ok;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[Diag] VerifyPatch({description}) 异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        ///
        /// 包含：
        ///
        /// 公共 Beta 始终登记完整的兼容性补丁集。
        /// </summary>
        private void ApplyV2AuditFixPatches()
        {
            ApplyV2AuditFixPatches_Core();
        }

        /// <summary>
        ///
        /// 但 PatchAll 在处理泛型类 ClientStaticMethod&lt;...&gt;.Invoke 的 attribute 时静默失败，
        /// 导致两个 Prefix 都未注册到 vanilla 方法上（第九次-4 双机测试确认）。
        ///
        /// 修复方案：改为 manual registration，与 14 个 SteamGameServerNetworkingSockets wrapper patch 一致。
        ///
        ///   1. _harmony.GetPatchedMethods() + Harmony.GetPatchInfo() 双重验证
        ///   2. Server-side Prefix 签名验证（methodInfo.DeclaringType + GetParameters()）
        ///   3. 整体 try-catch 包裹，任一登记失败不阻断 Plugin.Awake 后续步骤
        /// </summary>
        private void RegisterAssetIntegritySnapshotPatches()
        {
            RoleLogger.Info("[Shared]", "[Diag] === v0.2.3.16 AssetIntegritySnapshot 手动登记（ServerInvokePrefix 参数名 arg1..arg6 匹配 vanilla）===");

            // ===== Client-side: Assets.ReceiveKickForHashMismatch =====
            try
            {
                System.Reflection.MethodInfo clientTarget = AccessTools.Method(
                    typeof(SDG.Unturned.Assets),
                    "ReceiveKickForHashMismatch",
                    new System.Type[] {
                        typeof(System.Guid), typeof(string), typeof(string),
                        typeof(byte[]), typeof(string), typeof(string)
                    });

                if (clientTarget == null)
                {
                    RoleLogger.Error("[Shared]",
                        "[ManualPatch] FAIL AssetIntegritySnapshot/Client: AccessTools.Method returned null");
                }
                else
                {
                    // 签名验证（审计员要求）：输出 DeclaringType + Parameters
                    RoleLogger.Info("[Shared]",
                        $"[ManualPatch] AssetIntegritySnapshot/Client target resolved: declaringType={clientTarget.DeclaringType?.FullName} " +
                        $"params=[{string.Join(", ", System.Array.ConvertAll(clientTarget.GetParameters(), p => p.ParameterType.Name + " " + p.Name))}]");

                    System.Reflection.MethodInfo clientPrefix = AccessTools.Method(
                        typeof(Patches.AssetIntegritySnapshotPatch),
                        "ClientReceiveKickPrefix");

                    if (clientPrefix == null)
                    {
                        RoleLogger.Error("[Shared]",
                            "[ManualPatch] FAIL AssetIntegritySnapshot/Client: prefix MethodInfo null");
                    }
                    else
                    {
                        _harmony.Patch(clientTarget, prefix: new HarmonyMethod(clientPrefix));

                        // 双重验证 1：Harmony.GetPatchInfo
                        HarmonyLib.Patches patchInfo = Harmony.GetPatchInfo(clientTarget);
                        int prefixCount = patchInfo?.Prefixes?.Count ?? 0;
                        RoleLogger.Info("[Shared]",
                            $"[ManualPatch] OK AssetIntegritySnapshot/Client 已手动登记 (prefixes={prefixCount})");

                        // 双重验证 2：_harmony.GetPatchedMethods() 包含检查
                        bool contains = false;
                        foreach (var pm in _harmony.GetPatchedMethods())
                        {
                            if (pm == clientTarget) { contains = true; break; }
                        }
                        if (contains)
                        {
                            RoleLogger.Info("[Shared]",
                                "[ManualPatch] 验证 OK: Assets.ReceiveKickForHashMismatch 已 patch（GetPatchedMethods 包含）");
                        }
                        else
                        {
                            RoleLogger.Error("[Shared]",
                                "[ManualPatch] 验证 FAIL: Assets.ReceiveKickForHashMismatch 未 patch（GetPatchedMethods 不包含）");
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[ManualPatch] FAIL AssetIntegritySnapshot/Client: {ex}");
            }

            // ===== Server-side: ClientStaticMethod<Guid,string,string,byte[],string,string>.Invoke =====
            try
            {
                System.Type genericType = typeof(SDG.Unturned.ClientStaticMethod<
                    System.Guid, string, string, byte[], string, string>);

                System.Reflection.MethodInfo serverTarget = AccessTools.Method(
                    genericType,
                    "Invoke",
                    new System.Type[] {
                        typeof(SDG.NetTransport.ENetReliability),
                        typeof(SDG.NetTransport.ITransportConnection),
                        typeof(System.Guid), typeof(string), typeof(string),
                        typeof(byte[]), typeof(string), typeof(string)
                    });

                if (serverTarget == null)
                {
                    RoleLogger.Error("[Shared]",
                        "[ManualPatch] FAIL AssetIntegritySnapshot/Server: AccessTools.Method returned null");
                }
                else
                {
                    // 签名验证（审计员要求）：输出 DeclaringType + Parameters
                    RoleLogger.Info("[Shared]",
                        $"[ManualPatch] AssetIntegritySnapshot/Server target resolved: declaringType={serverTarget.DeclaringType?.FullName} " +
                        $"params=[{string.Join(", ", System.Array.ConvertAll(serverTarget.GetParameters(), p => p.ParameterType.Name + " " + p.Name))}]");

                    System.Reflection.MethodInfo serverPrefix = AccessTools.Method(
                        typeof(Patches.AssetIntegritySnapshotPatch),
                        "ServerInvokePrefix");

                    if (serverPrefix == null)
                    {
                        RoleLogger.Error("[Shared]",
                            "[ManualPatch] FAIL AssetIntegritySnapshot/Server: prefix MethodInfo null");
                    }
                    else
                    {
                        _harmony.Patch(serverTarget, prefix: new HarmonyMethod(serverPrefix));

                        // 双重验证 1：Harmony.GetPatchInfo
                        HarmonyLib.Patches patchInfo = Harmony.GetPatchInfo(serverTarget);
                        int prefixCount = patchInfo?.Prefixes?.Count ?? 0;
                        RoleLogger.Info("[Shared]",
                            $"[ManualPatch] OK AssetIntegritySnapshot/Server 已手动登记 (prefixes={prefixCount})");

                        // 双重验证 2：_harmony.GetPatchedMethods() 包含检查
                        bool contains = false;
                        foreach (var pm in _harmony.GetPatchedMethods())
                        {
                            if (pm == serverTarget) { contains = true; break; }
                        }
                        if (contains)
                        {
                            RoleLogger.Info("[Shared]",
                                "[ManualPatch] 验证 OK: ClientStaticMethod<...>.Invoke 已 patch（GetPatchedMethods 包含）");
                        }
                        else
                        {
                            RoleLogger.Error("[Shared]",
                                "[ManualPatch] 验证 FAIL: ClientStaticMethod<...>.Invoke 未 patch（GetPatchedMethods 不包含）");
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[ManualPatch] FAIL AssetIntegritySnapshot/Server: {ex}");
            }
        }

        /// <summary>
        ///
        /// 登记以下修复 patch：
        ///
        /// 公共 Beta 始终登记完整的兼容性补丁集。
        /// </summary>
        private void ApplyV2AuditFixPatches_Core()
        {
            RoleLogger.Info("[Shared]", "[Diag] === v2 审计放行后修复 patch 登记 ===");

            // 仅状态日志
            try
            {
                Patches.PlayerMovementInitializePlayerPrefixPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"PlayerMovementInitializePlayerPrefixPatch.RegisterManual 失败: {ex}");
            }

            // 完整兼容性补丁集。
                try
                {
                    Patches.SteamPlayerIsLocalServerHostPatch.RegisterManual(_harmony);
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"SteamPlayerIsLocalServerHostPatch.RegisterManual 失败: {ex}");
                }

                try
                {
                    Patches.PlayerUpdateGuardPatch.RegisterManual(_harmony);
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"PlayerUpdateGuardPatch.RegisterManual 失败: {ex}");
                }

                try
                {
                    Patches.GameplayReadyBitmaskPatch.RegisterManual(_harmony);
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"GameplayReadyBitmaskPatch.RegisterManual 失败: {ex}");
                }
            RoleLogger.Info("[Shared]", "[Diag] === v2 审计放行后修复 patch 登记完成 ===");
        }

        private void ApplyManualWrapperPatches()
        {
            RoleLogger.Info("[Shared]", "[Diag] === 手动登记 15 个关键 wrapper patch ===");

            TryManualPatch(typeof(Steamworks.SteamGameServerNetworkingSockets), "CreateListenSocketIP",
                typeof(SteamUserP2PRedirectPatch), nameof(SteamUserP2PRedirectPatch.CreateListenSocketIP_Prefix));

            TryManualPatch(typeof(Steamworks.SteamGameServerNetworkingSockets), "CreateListenSocketP2P",
                typeof(SteamUserP2PRedirectPatch), nameof(SteamUserP2PRedirectPatch.CreateListenSocketP2P_Prefix));
            TryManualPatch(typeof(Steamworks.SteamGameServerNetworkingSockets), "AcceptConnection",
                typeof(SteamUserP2PRedirectPatch), nameof(SteamUserP2PRedirectPatch.AcceptConnection_Prefix));
            //   PatchAll 未自动登记 [HarmonyPostfix] attribute（根因未明，可能 Harmony 2.9 + Steamworks.NET 重载解析问题），
            //   VerifyPatchMethodPair 检测到 Postfix 缺失会强制 INVALID。
            //   修复：显式手动登记 Postfix，与 Prefix 配对。
            TryManualPatchPostfix(typeof(Steamworks.SteamGameServerNetworkingSockets), "AcceptConnection",
                typeof(SteamUserP2PRedirectPatch), nameof(SteamUserP2PRedirectPatch.AcceptConnection_Postfix));
            TryManualPatch(typeof(Steamworks.SteamGameServerNetworkingSockets), "CloseConnection",
                typeof(SteamUserP2PRedirectPatch), nameof(SteamUserP2PRedirectPatch.CloseConnection_Prefix));
            TryManualPatch(typeof(Steamworks.SteamGameServerNetworkingSockets), "SetConnectionPollGroup",
                typeof(SteamUserP2PRedirectPatch), nameof(SteamUserP2PRedirectPatch.SetConnectionPollGroup_Prefix));
            TryManualPatchPostfix(typeof(Steamworks.SteamGameServerNetworkingSockets), "SetConnectionPollGroup",
                typeof(SteamUserP2PRedirectPatch), nameof(SteamUserP2PRedirectPatch.SetConnectionPollGroup_Postfix));
            TryManualPatch(typeof(Steamworks.SteamGameServerNetworkingSockets), "CreatePollGroup",
                typeof(SteamUserP2PRedirectPatch), nameof(SteamUserP2PRedirectPatch.CreatePollGroup_Prefix));
            TryManualPatch(typeof(Steamworks.SteamGameServerNetworkingSockets), "DestroyPollGroup",
                typeof(SteamUserP2PRedirectPatch), nameof(SteamUserP2PRedirectPatch.DestroyPollGroup_Prefix));
            TryManualPatch(typeof(Steamworks.SteamGameServerNetworkingSockets), "ReceiveMessagesOnPollGroup",
                typeof(SteamUserP2PRedirectPatch), nameof(SteamUserP2PRedirectPatch.ReceiveMessagesOnPollGroup_Prefix));
            TryManualPatch(typeof(Steamworks.SteamGameServerNetworkingSockets), "CloseListenSocket",
                typeof(SteamUserP2PRedirectPatch), nameof(SteamUserP2PRedirectPatch.CloseListenSocket_Prefix));
            TryManualPatch(typeof(Steamworks.SteamGameServerNetworkingSockets), "SendMessageToConnection",
                typeof(SteamUserP2PRedirectPatch), nameof(SteamUserP2PRedirectPatch.SendMessageToConnection_Prefix));
            TryManualPatch(typeof(Steamworks.SteamGameServerNetworkingSockets), "ReceiveMessagesOnConnection",
                typeof(SteamUserP2PRedirectPatch), nameof(SteamUserP2PRedirectPatch.ReceiveMessagesOnConnection_Prefix));
            TryManualPatch(typeof(Steamworks.SteamGameServerNetworkingSockets), "GetConnectionInfo",
                typeof(SteamUserP2PRedirectPatch), nameof(SteamUserP2PRedirectPatch.GetConnectionInfo_Prefix));
            TryManualPatch(typeof(Steamworks.SteamGameServerNetworkingSockets), "SetConnectionName",
                typeof(SteamUserP2PRedirectPatch), nameof(SteamUserP2PRedirectPatch.SetConnectionName_Prefix));

            TryManualPatch(typeof(Steamworks.Callback<Steamworks.SteamNetConnectionStatusChangedCallback_t>), "CreateGameServer",
                typeof(CallbackCreateGameServerRedirectPatch), nameof(CallbackCreateGameServerRedirectPatch.ConnStatus_CreateGameServer_Prefix));
            TryManualPatch(typeof(Steamworks.Callback<Steamworks.SteamNetAuthenticationStatus_t>), "CreateGameServer",
                typeof(CallbackCreateGameServerRedirectPatch), nameof(CallbackCreateGameServerRedirectPatch.AuthStatus_CreateGameServer_Prefix));

            //   RegisterManual 返回 AllRegistrationsSucceeded，VerifyCriticalPatches 阻断门读取此值。
            try
            {
                Patches.ClientMethodLoopbackPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"ClientMethodLoopbackPatch.RegisterManual 失败: {ex}");
            }

            //   Transpiler 替换 onRegionUpdated step 2 中 Dedicator.IsDedicatedServer() 调用为
            //   ListenRegionSyncEligibility.IsDedicatedOrP2PRemoteRecipient(player)，
            //   仅对远程非 loopback 玩家开放 SendRegion 资格。
            try
            {
                Patches.BarricadeManagerRegionSyncPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"BarricadeManagerRegionSyncPatch.RegisterManual 失败: {ex}");
            }

            //   Transpiler 替换 onRegionUpdated step 1 中 Dedicator.IsDedicatedServer() 调用。
            try
            {
                Patches.StructureManagerRegionSyncPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"StructureManagerRegionSyncPatch.RegisterManual 失败: {ex}");
            }

            //   ItemManagerRegionSyncPatch Transpiler + askItems Prefix
            //   Transpiler 替换 onRegionUpdated step 5 中 Dedicator.IsDedicatedServer() 调用，
            //   仅对远程非 loopback 玩家开放 askItems 资格。
            try
            {
                Patches.ItemManagerRegionSyncPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"ItemManagerRegionSyncPatch.RegisterManual 失败: {ex}");
            }

            //   ResourceManagerRegionSyncPatch Transpiler + SendResources_Write Prefix
            //   Transpiler 替换 onRegionUpdated step 3 中 Dedicator.IsDedicatedServer() 调用。
            //   SendResources 为 ClientStaticMethod 字段无独立 ask 方法，Prefix 挂在 SendResources_Write。
            try
            {
                Patches.ResourceManagerRegionSyncPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"ResourceManagerRegionSyncPatch.RegisterManual 失败: {ex}");
            }

            //   ObjectManagerRegionSyncPatch Transpiler + askObjects Prefix
            //   Transpiler 替换 onRegionUpdated step 4 中 Dedicator.IsDedicatedServer() 调用。
            try
            {
                Patches.ObjectManagerRegionSyncPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"ObjectManagerRegionSyncPatch.RegisterManual 失败: {ex}");
            }

            RoleLogger.Info("[Shared]", "[Diag] === 手动登记完成 ===");
        }

        /// <summary>
        /// 用于 AcceptConnection / SetConnectionPollGroup 的 Postfix（PatchAll 未自动登记 [HarmonyPostfix]）。
        /// 仅在本插件的精确 Postfix 已登记时 SKIP；外部 Postfix 不得阻止自身登记。
        /// </summary>
        private void TryManualPatchPostfix(System.Type targetType, string methodName,
            System.Type patchClass, string patchMethodName)
        {
            string label = $"{targetType?.Name}.{methodName}#Postfix";
            try
            {
                if (targetType == null)
                {
                    RoleLogger.Error("[Shared]", $"[ManualPatch] !!! {patchClass.Name}.{patchMethodName}: targetType=null");
                    return;
                }

                System.Reflection.MethodInfo original = AccessTools.Method(targetType, methodName);
                if (original == null)
                {
                    RoleLogger.Error("[Shared]",
                        $"[ManualPatch] !!! {label}: AccessTools.Method 返回 null");
                    return;
                }

                System.Reflection.MethodInfo postfix = AccessTools.Method(patchClass, patchMethodName);
                if (postfix == null)
                {
                    RoleLogger.Error("[Shared]",
                        $"[ManualPatch] !!! {label}: Postfix 方法未找到 {patchClass.FullName}.{patchMethodName}");
                    return;
                }

                HarmonyLib.Patches existing = Harmony.GetPatchInfo(original);
                if (existing?.Postfixes != null)
                {
                    foreach (HarmonyLib.Patch patch in existing.Postfixes)
                    {
                        if (patch.owner == HARMONY_ID && patch.PatchMethod == postfix)
                        {
                            RoleLogger.Info("[Shared]",
                                $"[ManualPatch] SKIP {label} 本插件 Postfix 已精确登记");
                            return;
                        }
                    }
                }

                _harmony.Patch(original, postfix: new HarmonyMethod(postfix));

                HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
                bool ownPostfixInstalled = false;
                if (info?.Postfixes != null)
                {
                    foreach (HarmonyLib.Patch patch in info.Postfixes)
                    {
                        if (patch.owner == HARMONY_ID && patch.PatchMethod == postfix)
                        {
                            ownPostfixInstalled = true;
                            break;
                        }
                    }
                }
                if (!ownPostfixInstalled)
                {
                    RoleLogger.Error("[Shared]",
                        $"[ManualPatch] !!! {label}: Harmony.Patch 调用后本插件 Postfix 仍未登记");
                    return;
                }

                RoleLogger.Info("[Shared]",
                    $"[ManualPatch] OK {label} 本插件 Postfix 已手动登记");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[ManualPatch] !!! {label} 异常: {ex}");
            }
        }

        /// <summary>
        /// </summary>
        private void ApplyManualDiagnosticPatches()
        {
            RoleLogger.Info("[Shared]", "[Diag] === 手动登记 internal 类型诊断 patch ===");
            try
            {
                Patches.NetMessagesSendDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"NetMessagesSendDiagnosticPatch.RegisterManual 失败: {ex}");
            }
            try
            {
                Patches.ClientAcceptedHandlerDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"ClientAcceptedHandlerDiagnosticPatch.RegisterManual 失败: {ex}");
            }

            try
            {
                Patches.ProviderAcceptStageDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"ProviderAcceptStageDiagnosticPatch.RegisterManual 失败: {ex}");
            }

            try
            {
                Patches.PlayerComponentInitializeDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"PlayerComponentInitializeDiagnosticPatch.RegisterManual 失败: {ex}");
            }

            try
            {
                Patches.ProviderRejectDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"ProviderRejectDiagnosticPatch.RegisterManual 失败: {ex}");
            }

            try
            {
                Patches.QueuePositionChangedDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"QueuePositionChangedDiagnosticPatch.RegisterManual 失败: {ex}");
            }

            bool p0cOk = false;
            try
            {
                p0cOk = Patches.InitialStateReceiveDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"InitialStateReceiveDiagnosticPatch.RegisterManual 失败: {ex}");
                p0cOk = false;
            }
            RoleLogger.Info("[Shared]",
                $"[P0-C] AllRegistrationsSucceeded={Patches.InitialStateReceiveDiagnosticPatch.AllRegistrationsSucceeded} " +
                $"summary={Patches.InitialStateReceiveDiagnosticPatch.RegistrationSummary} " +
                $"registerReturned={p0cOk}");

            try
            {
                Patches.LocalRegionProgressDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"LocalRegionProgressDiagnosticPatch.RegisterManual 失败: {ex}");
            }

            try
            {
                Patches.DisconnectTracerPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"DisconnectTracerPatch.RegisterManual 失败: {ex}");
            }

            //   - D-Vis-2: PlayerEquipment.ReceiveSlot/ReceiveUpdateState/ReceiveEquip
            //   - D-Vis-3: PlayerInput.ReceiveSimulateMispredictedInputs
            //   - D-Vis-4: PlayerAnimator.ReceiveLean/ReceiveGesture
            //   - D-Vis-5: SteamChannel.send（含节流 1 条/秒/调用方 + hex 摘要）
            //   - D-Vis-7: PlayerClothing.sendUpdateShirtQuality
            //   - D-Vis-8: SteamChannel.GetOwnerTransportConnection（含节流 10 秒/steamId）
            //   - D-Vis-6: 扩展 RemotePlayerRenderProbe 采样（不需要 patch 登记）
            try
            {
                Patches.PlayerClothingVisibilityDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"PlayerClothingVisibilityDiagnosticPatch.RegisterManual 失败: {ex}");
            }
            try
            {
                Patches.PlayerLookAnimatorDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"PlayerLookAnimatorDiagnosticPatch.RegisterManual 失败: {ex}");
            }
            try
            {
                Patches.SteamChannelTransportDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"SteamChannelTransportDiagnosticPatch.RegisterManual 失败: {ex}");
            }

            //   - D-Vis-9: PlayerMovement.tellState（位置同步追踪）
            //   - D-Vis-10: PlayerAnimator InitializePlayer+NotifyClothingIsVisible+onLifeUpdated+PlayerClothing.ReceiveClothingState 4 patch 目标
            //   - D-Vis-11: LoadingUI.Update（5 个 isLoading 标志位追踪）
            //   - D-Vis-12: Player.InitializePlayer+PlayerClothing.ReceiveClothingState+Player.OnDestroy 3 patch 点
            //   - D-Vis-13: Player.InitializePlayer 阶段标记（耗时追踪）
            //   - D-Vis-14: Unity Tag 错误源头追踪
            try
            {
                Patches.PlayerMovementTellStateDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"PlayerMovementTellStateDiagnosticPatch.RegisterManual 失败: {ex}");
            }
            try
            {
                Patches.PlayerAnimatorSmrEnabledDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"PlayerAnimatorSmrEnabledDiagnosticPatch.RegisterManual 失败: {ex}");
            }
            try
            {
                Patches.LoadingUIUpdateDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"LoadingUIUpdateDiagnosticPatch.RegisterManual 失败: {ex}");
            }
            try
            {
                Patches.PlayerIsLoadingClothingDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"PlayerIsLoadingClothingDiagnosticPatch.RegisterManual 失败: {ex}");
            }
            try
            {
                Patches.PlayerInitializePlayerStageDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"PlayerInitializePlayerStageDiagnosticPatch.RegisterManual 失败: {ex}");
            }
            try
            {
                Patches.UnityTagErrorSourceDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"UnityTagErrorSourceDiagnosticPatch.RegisterManual 失败: {ex}");
            }

            //   - D-Vis-15: PlayerMovement.tellState 调用方追踪（Postfix + StackTrace，验证议题 A/B 独立性）
            //   - D-Vis-16（调整后）: NetMessage 投递路径追踪（区分主机自身 vs 客机）
            //   - D-Vis-17: Player 生命周期就绪追踪（onPlayerCreated + bitmask + isLoadingClothing）
            //   - D-Vis-18: InitializePlayer 分阶段计时（clothing/movement/animator/quests 4 阶段）
            try
            {
                Patches.PlayerMovementTellStateCallerDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"PlayerMovementTellStateCallerDiagnosticPatch.RegisterManual 失败: {ex}");
            }
            try
            {
                Patches.NetMessageDeliveryPathDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"NetMessageDeliveryPathDiagnosticPatch.RegisterManual 失败: {ex}");
            }
            try
            {
                Patches.PlayerLifecycleReadyDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"PlayerLifecycleReadyDiagnosticPatch.RegisterManual 失败: {ex}");
            }
            try
            {
                Patches.PlayerInitializePlayerStagedTimingDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"PlayerInitializePlayerStagedTimingDiagnosticPatch.RegisterManual 失败: {ex}");
            }

            try
            {
                Patches.PlayerManagerBroadcastPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"PlayerManagerBroadcastPatch.RegisterManual 失败: {ex}");
            }
            try
            {
                Patches.RemotePlayerClothingVisibleBridgePatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"RemotePlayerClothingVisibleBridgePatch.RegisterManual 失败: {ex}");
            }
            try
            {
                Patches.PlayerManagerBroadcastDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"PlayerManagerBroadcastDiagnosticPatch.RegisterManual 失败: {ex}");
            }

            //   6 个 WorldSyncDiagnostic patch（Item/Resource/Object/Vehicle/Animal/Zombie Manager）
            //   因类级 [HarmonyPatch] 缺失，PatchAll 未登记（运行日志证据：6 组 VerifyRegistration 全 FAIL）。
            //   RegisterManual 返回值不绕过 VerifyRegistration，最终权威仍是 6 个 VerifyRegistration 聚合至 DiagnosticBuildValid。
            RegisterWorldSyncDiagnosticPatches();

            RoleLogger.Info("[Shared]", "[Diag] === internal 诊断 patch 登记完成 ===");
        }

        /// <summary>
        /// 最终由 VerifyCriticalPatches 聚合 6 个 VerifyRegistration 结果至 DiagnosticBuildValid 阻断门。
        /// </summary>
        private void RegisterWorldSyncDiagnosticPatches()
        {
            RoleLogger.Info("[Shared]", "[Diag] === v0.2.3.37-P0-B-6-P0-D-ESC-2 手动登记 6 个 WorldSyncDiagnostic patch + 3 个 P0-C-1/C-2 patch + 1 个 P0-B-3 patch（含 P0-B-4 诊断 Prefix/Postfix）+ 1 个 P0-PlayerVisibility patch + 1 个 P0-D-ESC-2 patch + 1 个 P0-C-1-V-a 诊断 patch（identity-based 幂等）===");

            try
            {
                Patches.ItemManagerWorldSyncDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"ItemManagerWorldSyncDiagnosticPatch.RegisterManual 失败: {ex}");
            }

            try
            {
                Patches.AuthoritativeItemGenerationGatePatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"AuthoritativeItemGenerationGatePatch.RegisterManual failed: {ex}");
            }

            // Alpha inventory/world authority probe: read-only fingerprints and transaction tracing.
            try
            {
                Patches.InventoryWorldAuthorityProbe.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"InventoryWorldAuthorityProbe.RegisterManual failed: {ex}");
            }

            try
            {
                Patches.ResourceManagerWorldSyncDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"ResourceManagerWorldSyncDiagnosticPatch.RegisterManual 失败: {ex}");
            }

            try
            {
                Patches.ObjectManagerWorldSyncDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"ObjectManagerWorldSyncDiagnosticPatch.RegisterManual 失败: {ex}");
            }

            try
            {
                Patches.VehicleManagerWorldSyncDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"VehicleManagerWorldSyncDiagnosticPatch.RegisterManual 失败: {ex}");
            }

            try
            {
                Patches.AnimalManagerWorldSyncDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"AnimalManagerWorldSyncDiagnosticPatch.RegisterManual 失败: {ex}");
            }

            try
            {
                Patches.ZombieManagerWorldSyncDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"ZombieManagerWorldSyncDiagnosticPatch.RegisterManual 失败: {ex}");
            }

            //   ZombieManagerP0DGenerateZombiesPatch：onBoundUpdated Prefix supplement
            //   必须在 ZombieManagerWorldSyncDiagnosticPatch 之后登记，确保 Priority.Low 生效。
            //   VerifyRegistration 聚合至 DiagnosticBuildValid 阻断门。
            try
            {
                Patches.ZombieManagerP0DGenerateZombiesPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"ZombieManagerP0DGenerateZombiesPatch.RegisterManual 失败: {ex}");
            }

            //   ZombieLifecyclePatch：onBoundUpdated Prefix(VeryLow) + Postfix(High) + Finalizer(High)
            //   编码约束：不新增 Tick / Transpiler / 主动 generate/destroy；Finalizer 始终原样返回 __exception。
            //   VerifyRegistration 聚合至 DiagnosticBuildValid 阻断门。
            try
            {
                Patches.P0EZombieLifecycle.ZombieLifecyclePatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"ZombieLifecyclePatch.RegisterManual 失败: {ex}");
            }

            //   ZombieManagerP0C1SendZombieStatesPatch：updateRegionsAndSendZombieStates Transpiler
            //   替换 L1662 IsDedicatedServer -> IsDedicatedOrP2PHost。
            //   VerifyRegistration 聚合至 DiagnosticBuildValid 阻断门。
            try
            {
                Patches.ZombieManagerP0C1SendZombieStatesPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"ZombieManagerP0C1SendZombieStatesPatch.RegisterManual 失败: {ex}");
            }

            //   VehicleManagerP0C1ReplicationPatch：Update Transpiler（L2918）+ OnUpdate Postfix（位移检测+MarkForReplicationUpdate）
            //   VerifyRegistration 聚合至 DiagnosticBuildValid 阻断门。
            try
            {
                Patches.VehicleManagerP0C1ReplicationPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"VehicleManagerP0C1ReplicationPatch.RegisterManual 失败: {ex}");
            }

            //   AnimalManagerP0C2SendAnimalStatesPatch：Update Transpiler（L1057）
            //   替换 IsDedicatedServer -> IsDedicatedOrP2PHost。
            //   VerifyRegistration 聚合至 DiagnosticBuildValid 阻断门。
            try
            {
                Patches.AnimalManagerP0C2SendAnimalStatesPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"AnimalManagerP0C2SendAnimalStatesPatch.RegisterManual 失败: {ex}");
            }

            //   ItemManagerP0B3PreGeneratePatch：onLevelLoaded Transpiler（L941）
            //   替换 IsDedicatedServer -> IsDedicatedOrP2PHost，listen host 在 onLevelLoaded 时全地图预生成物品。
            //   VerifyRegistration 聚合至 DiagnosticBuildValid 阻断门。
            try
            {
                Patches.ItemManagerP0B3PreGeneratePatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"ItemManagerP0B3PreGeneratePatch.RegisterManual 失败: {ex}");
            }

            //   NetMessagesPlayerConnectedLoopbackPatch：SendMessageToClient Prefix
            //   仅当 index==PlayerConnected && transportConnection is TransportConnection_Loopback 时跳过。
            //   VerifyRegistration 聚合至 DiagnosticBuildValid 阻断门。
            try
            {
                Patches.NetMessagesPlayerConnectedLoopbackPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"NetMessagesPlayerConnectedLoopbackPatch.RegisterManual 失败: {ex}");
            }

            //   PlayerUIPauseTimeScalePatch：PlayerUI.updatePauseTimeScale Prefix
            //   当 listen host + 有远端客机时强制保持 timeScale=1 + AudioListener.pause=false。
            //   修复 24th 测试中 timeScale=0.00 持续 33.32s 导致客机世界停滞的问题。
            //   VerifyRegistration 聚合至 DiagnosticBuildValid 阻断门。
            try
            {
                Patches.PlayerUIPauseTimeScalePatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"PlayerUIPauseTimeScalePatch.RegisterManual 失败: {ex}");
            }

            //   VehicleEnterDiagnosticPatch：VehicleManager.enterVehicle + ReceiveEnterVehicleRequest Prefix
            //   仅诊断日志，不修改 vanilla 验证逻辑。确定登车失败具体环节后再决定是否实施驾驶同步。
            //   VerifyRegistration 聚合至 DiagnosticBuildValid 阻断门。
            try
            {
                Patches.VehicleEnterDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"VehicleEnterDiagnosticPatch.RegisterManual 失败: {ex}");
            }

            //   - UseableBarricadeDiagnosticPatch：8 DP（startPrimary/check/checkSpace/checkClaims/ReceiveBarricadeNone/simulate/build/dropBarricade）
            //   - ZombieEntityMappingDiagnosticPatch：7 DP（SendZombies/ReceiveZombies/SendZombieStates/ReceiveZombieStates/onBoundUpdated/sendZombieDead+Alive/ReceiveZombieDead+Alive）
            //   - PlayerManagerCullingDiagnosticPatch：3 DP（SendPlayerStates_Write Prefix/ReceivePlayerStates Postfix/tellState Prefix）
            //   阶段 2 完成后等待阶段 2 重审，不自动进入阶段 3（双机诊断测试）或阶段 5（功能修复）。
            try
            {
                Patches.P0EDiagnostic.UseableBarricadeDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"UseableBarricadeDiagnosticPatch.RegisterManual 失败: {ex}");
            }
            try
            {
                Patches.P0EDiagnostic.ZombieEntityMappingDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"ZombieEntityMappingDiagnosticPatch.RegisterManual 失败: {ex}");
            }
            try
            {
                Patches.P0EDiagnostic.PlayerManagerCullingDiagnosticPatch.RegisterManual(_harmony);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"PlayerManagerCullingDiagnosticPatch.RegisterManual 失败: {ex}");
            }

            RoleLogger.Info("[Shared]", "[Diag] === v0.2.3.37-P0-B-6-P0-D-ESC-2 WorldSyncDiagnostic patch 登记完成 ===");
        }

        private void TryManualPatch(System.Type targetType, string methodName,
            System.Type patchClass, string patchMethodName)
        {
            string label = $"{targetType?.Name}.{methodName}";
            try
            {
                if (targetType == null)
                {
                    RoleLogger.Error("[Shared]", $"[ManualPatch] !!! {patchClass.Name}.{patchMethodName}: targetType=null");
                    return;
                }

                System.Reflection.MethodInfo original = AccessTools.Method(targetType, methodName);
                if (original == null)
                {
                    RoleLogger.Error("[Shared]",
                        $"[ManualPatch] !!! {label}: AccessTools.Method 返回 null");
                    return;
                }

                System.Reflection.MethodInfo prefix = AccessTools.Method(patchClass, patchMethodName);
                if (prefix == null)
                {
                    RoleLogger.Error("[Shared]",
                        $"[ManualPatch] !!! {label}: Prefix 方法未找到 {patchClass.FullName}.{patchMethodName}");
                    return;
                }

                HarmonyLib.Patches existing = Harmony.GetPatchInfo(original);
                if (existing?.Prefixes != null)
                {
                    foreach (HarmonyLib.Patch patch in existing.Prefixes)
                    {
                        if (patch.owner == HARMONY_ID && patch.PatchMethod == prefix)
                        {
                            RoleLogger.Info("[Shared]",
                                $"[ManualPatch] SKIP {label} 本插件 Prefix 已精确登记");
                            return;
                        }
                    }
                }

                _harmony.Patch(original, prefix: new HarmonyMethod(prefix));

                HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
                bool ownPrefixInstalled = false;
                if (info?.Prefixes != null)
                {
                    foreach (HarmonyLib.Patch patch in info.Prefixes)
                    {
                        if (patch.owner == HARMONY_ID && patch.PatchMethod == prefix)
                        {
                            ownPrefixInstalled = true;
                            break;
                        }
                    }
                }
                if (!ownPrefixInstalled)
                {
                    RoleLogger.Error("[Shared]",
                        $"[ManualPatch] !!! {label}: Harmony.Patch 调用后本插件 Prefix 仍未登记");
                    return;
                }

                RoleLogger.Info("[Shared]",
                    $"[ManualPatch] OK {label} 本插件 Prefix 已手动登记");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[ManualPatch] !!! {label} 异常: {ex}");
            }
        }
    }
}
