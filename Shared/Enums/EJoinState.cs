namespace SteamP2PFriends.Shared.Enums
{
    /// <summary>
    ///
    /// 审计员要求的目标状态机：
    ///   Idle
    ///   -> TransportConnected        (SNS Connected)
    ///   -> LevelReady                (Ready to connect)
    ///   -> VerificationPending       (Connection pending verification)
    ///   -> AuthSent                  (Authenticating with server)
    ///   -> ServerAccepted            (Accepted by server, onClientConnected)
    ///   -> LocalPlayerCreated        (本地 Player 对象存在)
    ///   -> InitialStateReceived      (关键初始状态收齐)
    ///   -> GameplayReady             (可移动、交互)
    ///   任意非终态失败 -> Disconnecting -> TeardownComplete -> Idle/允许手动重连
    ///
    ///   - ServerAccepted 由 ClientMessageHandler_Accepted.ReadMessage Postfix 推进（D-7）。
    ///   - LocalPlayerCreated 由 Tick 检测本地 Player.player != null（G-2 允许的"Tick 一致性检查"，
    ///   - InitialStateReceived 第一版与 LocalPlayerCreated 等价（bitmask hook 留待后续）。
    ///   - GameplayReady 第一版与 InitialStateReceived 等价。
    ///   - Disconnecting/TeardownComplete 由 watchdog 超时或失败路径推进。
    /// </summary>
    public enum EJoinState
    {
        /// <summary>空闲</summary>
        Idle,
        /// <summary>连接中（已调 Provider.connect，等待 onClientConnected/onClientDisconnected）</summary>
        Connecting,
        ServerAccepted,
        LocalPlayerCreated,
        InitialStateReceived,
        GameplayReady,
        Connected,
        /// <summary>正在断开连接（已调 Provider.disconnect，等待 onClientDisconnected）</summary>
        Disconnecting,
        /// <summary>断开完成，静态状态已清空，允许手动重连</summary>
        TeardownComplete,
        TeardownFailed,
        /// <summary>连接失败（Provider.connect 抛异常或 dismiss）</summary>
        Failed,
        /// <summary>连接超时（watchdog 超时，已冻结自动重试）</summary>
        Timeout
    }
}
