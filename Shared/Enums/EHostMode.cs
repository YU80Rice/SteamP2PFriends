namespace SteamP2PFriends.Shared.Enums
{
    /// <summary>
    /// 房主模式枚举（对齐原版 SteamP2PFriends P2PHostManager.EHostMode）。
    /// </summary>
    public enum EHostMode
    {
        /// <summary>未启动</summary>
        None,
        /// <summary>Steam 好友 P2P 联机（绕过 SteamGameServer 公网路由）</summary>
        P2P,
        /// <summary>局域网测试模式（offlineOnly + 重复 Steam ID 绕过）</summary>
        LAN
    }
}
