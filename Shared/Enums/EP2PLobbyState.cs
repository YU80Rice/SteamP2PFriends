namespace SteamP2PFriends.Shared.Enums
{
    /// <summary>
    /// P2P Lobby 状态机（对齐原版 SteamP2PFriends P2PLobbyManager.EP2PLobbyState）。
    /// </summary>
    public enum EP2PLobbyState
    {
        /// <summary>未创建</summary>
        None,
        /// <summary>创建中（等待 LobbyCreated_t 回调，12s 超时）</summary>
        Creating,
        /// <summary>已就绪（lobby 创建成功，房主可写入 game server metadata）</summary>
        Ready,
        /// <summary>创建失败（超时或异常，已降级到 DirectHost 模式）</summary>
        Failed
    }
}
