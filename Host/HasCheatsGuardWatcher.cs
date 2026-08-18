using SDG.Unturned;
using SteamP2PFriends.Shared;

namespace SteamP2PFriends.Host
{
    /// <summary>
    /// hasCheats 反作弊守卫（static + Tick 模式，由 Plugin.Update 直驱）。
    ///
    /// 根因：BepInEx 注入式插件可被滥用为作弊工具，需 guard 机制防止 Provider.hasCheats 被外部篡改。
    /// Provider.hasCheats 是 public static 字段（不是属性），无法用 Harmony patch getter（ldsfld IL 指令），
    /// 故采用每帧轮询降维打击 22 个命令的 if(!hasCheats) return 检查。
    ///
    /// </summary>
    public static class HasCheatsGuardWatcher
    {
        private static bool _running;
        private static bool _expectedCheats;
        private static int _violationCount;

        internal static void StartGuard(bool cheats)
        {
            _running = true;
            _expectedCheats = cheats;
            _violationCount = 0;
            RoleLogger.Info("[Host]", $"[P2P-Guard] HasCheatsGuard 已启动，期望 hasCheats={cheats}");
        }

        internal static void StopGuard()
        {
            if (_running)
            {
                _running = false;
                RoleLogger.Info("[Host]", $"[P2P-Guard] HasCheatsGuard 已停止（总违规计数={_violationCount}）");
            }
        }

        /// <summary>
        /// 每帧由 Plugin.Update 调用。检查 hasCheats 是否被篡改。
        /// </summary>
        public static void Tick()
        {
            if (!_running) return;
            if (!HostManager.IsP2PServerActive)
            {
                StopGuard();
                return;
            }

            if (Provider.hasCheats != _expectedCheats)
            {
                Provider.hasCheats = _expectedCheats;
                _violationCount++;
                RoleLogger.Warn("[Host]",
                    $"[P2P-Guard] hasCheats 被篡改，已恢复为 {_expectedCheats}（违规计数={_violationCount}）");
            }
        }
    }
}
