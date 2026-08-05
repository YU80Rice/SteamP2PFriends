using BepInEx.Logging;
using SDG.Unturned;
using SteamP2PFriends.Host;

namespace SteamP2PFriends.Shared
{
    /// <summary>
    /// 双端日志路由器。
    ///
    /// v0.2.3.3 P1-A 修复（Codex 第四次审计外部审计报告）：
    ///   - 新增 InfoAuto/WarnAuto/ErrorAuto 方法，每次输出时实时查询 Provider/HostManager 动态事实。
    ///   - 不再依赖调用方传入的硬编码前缀（避免"角色切换为客机"却显示 [Host] 前缀的 bug）。
    ///   - 输出格式：[[Host]|Client]|Shared]] [isServer=X isClient=Y hostMode=Z] {msg}
    ///   - 保留原 Info/Warn/Error 方法（向后兼容，已修改的调用方使用 InfoAuto）。
    /// </summary>
    internal static class RoleLogger
    {
        private static ManualLogSource _logger;
        private static bool _verbose;

        internal static void Initialize(ManualLogSource logger, bool verbose)
        {
            _logger = logger;
            _verbose = verbose;
        }

        // ===== 原签名保留（向后兼容）=====

        internal static void Info(string role, string msg)
        {
            _logger?.LogInfo($"[{role}] {msg}");
        }

        internal static void InfoVerbose(string role, string msg)
        {
            if (_verbose) _logger?.LogInfo($"[{role}] {msg}");
        }

        internal static void Warn(string role, string msg)
        {
            _logger?.LogWarning($"[{role}] {msg}");
        }

        internal static void Error(string role, string msg)
        {
            _logger?.LogError($"[{role}] {msg}");
        }

        // ===== v0.2.3.3 P1-A：动态角色判定 =====

        /// <summary>
        /// 动态推断当前角色前缀：
        /// - 房主（HostManager.IsP2PHostMode=true） -> [Host]
        /// - 客机（Provider.isClient && !HostManager.IsP2PHostMode） -> [Client]
        /// - 其他（启动期/菜单） -> [Shared]
        /// </summary>
        internal static string ResolveDynamicRole()
        {
            bool isServer = false, isClient = false, isP2PHost = false;
            try
            {
                isServer = Provider.isServer;
            }
            catch { /* Provider 静态构造可能在极早期失败 */ }
            try
            {
                isClient = Provider.isClient;
            }
            catch { /* ignore */ }
            try
            {
                isP2PHost = HostManager.IsP2PHostMode;
            }
            catch { /* HostManager 静态构造可能失败 */ }

            if (isP2PHost) return "[Host]";
            if (isClient) return "[Client]";
            return "[Shared]";
        }

        /// <summary>
        /// 输出动态事实前缀的 Info 日志。
        /// </summary>
        internal static void InfoAuto(string msg)
        {
            string role = ResolveDynamicRole();
            string facts = ResolveDynamicFacts();
            _logger?.LogInfo($"[{role}] {facts} {msg}");
        }

        internal static void InfoAutoVerbose(string msg)
        {
            if (!_verbose) return;
            string role = ResolveDynamicRole();
            string facts = ResolveDynamicFacts();
            _logger?.LogInfo($"[{role}] {facts} {msg}");
        }

        internal static void WarnAuto(string msg)
        {
            string role = ResolveDynamicRole();
            string facts = ResolveDynamicFacts();
            _logger?.LogWarning($"[{role}] {facts} {msg}");
        }

        internal static void ErrorAuto(string msg)
        {
            string role = ResolveDynamicRole();
            string facts = ResolveDynamicFacts();
            _logger?.LogError($"[{role}] {facts} {msg}");
        }

        /// <summary>
        /// 动态事实快照：isServer / isClient / hostMode。
        /// 每条 InfoAuto 日志都附带，方便审计员区分角色。
        /// </summary>
        private static string ResolveDynamicFacts()
        {
            bool isServer = false, isClient = false;
            bool isP2PHost = false;
            string hostMode = "Unknown";
            try { isServer = Provider.isServer; } catch { }
            try { isClient = Provider.isClient; } catch { }
            try
            {
                isP2PHost = HostManager.IsP2PHostMode;
                hostMode = HostManager.HostMode.ToString();
            }
            catch { }

            ulong steamId = 0;
            try
            {
                if (Steamworks.SteamUser.GetSteamID().m_SteamID != 0)
                {
                    steamId = Steamworks.SteamUser.GetSteamID().m_SteamID;
                }
            }
            catch { /* Steamworks 未初始化 */ }

            return $"[isServer={isServer} isClient={isClient} hostMode={hostMode} p2pHost={isP2PHost} steamId={steamId}]";
        }
    }
}
