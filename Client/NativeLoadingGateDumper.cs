using SDG.Unturned;
using SteamP2PFriends.Shared;
using UnityEngine;

namespace SteamP2PFriends.Client
{
    /// <summary>
    ///
    /// 设计原则：
    ///   - 只读：不写任何 vanilla loading flag，不关闭 LoadingUI，不修改 lastLoading。
    ///   - 输出字段：reason, realtime, frameCount, isConnected, isClient, isServer, queuePosition,
    ///     Assets.isLoading, Provider.isLoading, Provider.isLoadingUGC,
    ///     Level.isLoading, isLoadingContent, isLoadingLighting, isLoadingVehicles,
    ///     isLoadingBarricades, isLoadingStructures, isLoadingArea, isExiting,
    ///     Player.isLoading, isLoadingInventory, isLoadingLife, isLoadingClothing,
    ///     Player.LocalPlayer != null, PlayerUI.instance != null,
    ///     LoadingUI.isBlocked, local SteamPlayer/netId/instanceId
    ///
    /// 触发时机（由 P2PJoinManager / Plugin.Update / 各 Postfix 调用）：
    ///   1. 调用 Provider.connect 前
    ///   2. QueuePositionChanged Postfix
    ///   3. Accepted Postfix
    ///   4. 本地 Player.InitializePlayer Postfix
    ///   5. bitmask 0xFF 时
    ///   6. Accepted 后每 1 秒一次，持续 15 秒；之后每 5 秒一次
    ///   7. watchdog 报警时
    ///   8. onClientDisconnected 前后
    /// </summary>
    internal static class NativeLoadingGateDumper
    {
        private static float _lastPeriodicDumpTime;
        private static float _acceptedTime;
        private static bool _acceptedTrackingActive;

        /// <summary>
        /// 立即输出一次加载门快照（只读）。
        /// </summary>
        internal static void Dump(string reason)
        {
            if (!PluginLogPolicy.IsVerboseDiagnosticsEnabled) return;

            try
            {
                float realtime = Time.realtimeSinceStartup;
                int frameCount = Time.frameCount;

                bool isConnected = SafeGet(() => Provider.isConnected);
                bool isClient = SafeGet(() => Provider.isClient);
                bool isServer = SafeGet(() => Provider.isServer);
                byte queuePosition = SafeGet(() => Provider.queuePosition);

                bool assetsLoading = SafeGet(() => Assets.isLoading);
                bool providerLoading = SafeGet(() => Provider.isLoading);
                bool providerLoadingUGC = SafeGet(() => Provider.isLoadingUGC);

                bool levelLoading = SafeGet(() => Level.isLoading);
                bool levelLoadingContent = SafeGet(() => Level.isLoadingContent);
                bool levelLoadingLighting = SafeGet(() => Level.isLoadingLighting);
                bool levelLoadingVehicles = SafeGet(() => Level.isLoadingVehicles);
                bool levelLoadingBarricades = SafeGet(() => Level.isLoadingBarricades);
                bool levelLoadingStructures = SafeGet(() => Level.isLoadingStructures);
                bool levelLoadingArea = SafeGet(() => Level.isLoadingArea);
                bool levelExiting = SafeGet(() => Level.isExiting);

                bool playerLoading = SafeGet(() => Player.isLoading);
                bool playerLoadingInventory = SafeGet(() => Player.isLoadingInventory);
                bool playerLoadingLife = SafeGet(() => Player.isLoadingLife);
                bool playerLoadingClothing = SafeGet(() => Player.isLoadingClothing);

                Player localPlayer = Player.LocalPlayer;
                bool hasLocalPlayer = !ReferenceEquals(localPlayer, null);
                bool hasPlayerUI = SafeGet(() => PlayerUI.window != null);

                bool loadingUIBlocked = SafeGet(() => LoadingUI.isBlocked);

                // 本地 SteamPlayer / netId / instanceId
                string localPlayerInfo = "n/a";
                if (hasLocalPlayer)
                {
                    try
                    {
                        ulong steamId = 0;
                        uint netId = 0;
                        int instanceId = localPlayer.GetInstanceID();
                        if (localPlayer.channel?.owner?.playerID?.steamID != null)
                        {
                            steamId = localPlayer.channel.owner.playerID.steamID.m_SteamID;
                        }
                        try { netId = localPlayer.channel?.owner?.GetNetId().id ?? 0; } catch { }
                        localPlayerInfo = $"steamId={steamId} netId={netId} instanceId={instanceId}";
                    }
                    catch
                    {
                        localPlayerInfo = "extract-failed";
                    }
                }

                RoleLogger.Info("[Client]",
                    $"[NativeLoadingGate] reason={reason} " +
                    $"t={realtime:F2}s frame={frameCount} " +
                    $"conn={isConnected} isClient={isClient} isServer={isServer} queuePos={queuePosition} | " +
                    $"Assets.isLoading={assetsLoading} | " +
                    $"Provider.isLoading={providerLoading} Provider.isLoadingUGC={providerLoadingUGC} | " +
                    $"Level.isLoading={levelLoading} " +
                    $"Content={levelLoadingContent} Lighting={levelLoadingLighting} " +
                    $"Vehicles={levelLoadingVehicles} Barricades={levelLoadingBarricades} " +
                    $"Structures={levelLoadingStructures} Area={levelLoadingArea} isExiting={levelExiting} | " +
                    $"Player.isLoading={playerLoading} " +
                    $"Inv={playerLoadingInventory} Life={playerLoadingLife} Cloth={playerLoadingClothing} | " +
                    $"LocalPlayer={hasLocalPlayer} PlayerUI={hasPlayerUI} | " +
                    $"LoadingUI.isBlocked={loadingUIBlocked} | " +
                    $"localPlayer=[{localPlayerInfo}]");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Client]", $"[NativeLoadingGate] Dump 异常（不阻断）: reason={reason} ex={ex.Message}");
            }
        }

        /// <summary>
        /// 启动 Accepted 后的周期性快照（每 1s 一次持续 15s，之后每 5s 一次）。
        /// 由 P2PJoinManager.OnClientConnected 调用。
        /// </summary>
        internal static void StartPostAcceptedTracking()
        {
            if (!PluginLogPolicy.IsVerboseDiagnosticsEnabled)
            {
                _acceptedTrackingActive = false;
                return;
            }

            _acceptedTime = Time.realtimeSinceStartup;
            _acceptedTrackingActive = true;
            _lastPeriodicDumpTime = _acceptedTime;
        }

        /// <summary>
        /// 停止 Accepted 后的周期性快照。
        /// </summary>
        internal static void StopPostAcceptedTracking()
        {
            _acceptedTrackingActive = false;
        }

        /// <summary>
        /// 由 Plugin.Update 调用，按节奏触发周期性快照。
        /// </summary>
        internal static void Tick()
        {
            if (!PluginLogPolicy.IsVerboseDiagnosticsEnabled || !_acceptedTrackingActive) return;

            float now = Time.realtimeSinceStartup;
            float elapsed = now - _acceptedTime;

            // 15s 内每 1s 一次；之后每 5s 一次
            float interval = (elapsed < 15f) ? 1f : 5f;

            if (now - _lastPeriodicDumpTime >= interval)
            {
                _lastPeriodicDumpTime = now;
                Dump($"PostAccepted-periodic(elapsed={elapsed:F1}s)");
            }
        }

        private static T SafeGet<T>(System.Func<T> getter)
        {
            try
            {
                return getter();
            }
            catch
            {
                return default(T);
            }
        }
    }
}
