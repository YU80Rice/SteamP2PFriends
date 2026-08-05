using System;

namespace SteamP2PFriends.Shared
{
    /// <summary>
    /// v0.2.3.18 新增：D-Vis 诊断 patch 专用的脱敏工具类。
    ///
    /// 设计原则：
    ///   - 不输出完整 SteamID（仅前 8 位 + 后 4 位，如 76561199...2479）
    ///   - 不输出真实 IP 地址
    ///   - 不输出可能泄露用户隐私的信息
    ///   - 0UL 视为无效，返回 "invalid"
    ///
    /// 严格禁止：
    ///   - 在日志中输出未脱敏的 SteamID / IP / 用户名
    /// </summary>
    public static class DiagnosticMaskUtil
    {
        /// <summary>
        /// 脱敏 SteamID（ulong）：保留前 8 位 + 后 4 位，中间用 ... 占位。
        /// 0UL 视为无效。
        /// </summary>
        public static string MaskSteamId(ulong steamId)
        {
            if (steamId == 0UL) return "invalid";
            string s = steamId.ToString();
            if (s.Length <= 12) return $"short({s.Length})";
            return $"{s.Substring(0, 8)}...{s.Substring(s.Length - 4)}";
        }

        /// <summary>
        /// 脱敏 SteamID（CSteamID）：提取 m_SteamID 后调用 MaskSteamId(ulong)。
        /// </summary>
        public static string MaskSteamId(Steamworks.CSteamID steamId)
        {
            try
            {
                return MaskSteamId(steamId.m_SteamID);
            }
            catch (Exception)
            {
                return "invalid";
            }
        }
    }
}
