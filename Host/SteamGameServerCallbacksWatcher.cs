using SDG.Unturned;
using SteamP2PFriends.Shared;
using Steamworks;
using UnityEngine;

namespace SteamP2PFriends.Host
{
    /// <summary>
    /// SteamGameServer 回调动力泵（10Hz，static + Tick 模式，由 Plugin.Update 直驱）。
    ///
    /// 根因：BepInEx 注入的 MonoBehaviour 不保证在游戏主循环内驱动 Update，
    /// 故必须自建 GameServer.RunCallbacks() 周期调用，否则：
    ///   - SteamServersConnected_t 永不派发 -> SteamReadyHandled 永不触发
    ///   - SteamNetConnectionStatusChangedCallback_t 永不派发 -> 客机连接事件收不到
    ///
    /// 演进历史（LaunchP2PHostManager）：
    ///   v1.7 60-120Hz -> IPC 死锁
    ///   v1.9 禁用 -> LogOn 超时离线
    ///   v2.0 自动停止 -> 回调饥饿
    ///   v2.5 限频 10Hz 永不关闭 = 既保证派发又避免死锁
    ///
    /// v0.2 重构：从 MonoBehaviour 改为 static class + Tick()，避免 BepInEx 环境
    /// AddComponent 创建的 MonoBehaviour Update 不被 Unity 调用的陷阱。
    /// </summary>
    public static class SteamGameServerCallbacksWatcher
    {
        private static bool _running;
        private static float _accumulator;
        private const float INTERVAL = 0.1f;  // 100ms = 10Hz

        internal static void StartWatching()
        {
            _running = true;
            _accumulator = 0f;
            RoleLogger.Info("[Host]", "[P2P-Callbacks] 动力泵已启动（10Hz GameServer.RunCallbacks）");
        }

        internal static void StopWatching()
        {
            if (_running)
            {
                _running = false;
                RoleLogger.Info("[Host]", "[P2P-Callbacks] 动力泵已停止");
            }
        }

        /// <summary>
        /// 每帧由 Plugin.Update 调用。10Hz 限频调 GameServer.RunCallbacks()。
        /// </summary>
        public static void Tick()
        {
            if (!_running) return;
            if (!HostManager.IsP2PServerActive)
            {
                StopWatching();
                return;
            }

            _accumulator += Time.deltaTime;
            if (_accumulator < INTERVAL) return;
            _accumulator = 0f;

            try
            {
                GameServer.RunCallbacks();
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Host]", $"[P2P-Callbacks] GameServer.RunCallbacks 异常（不停止，下帧重试）: {ex.Message}");
            }
        }
    }
}
