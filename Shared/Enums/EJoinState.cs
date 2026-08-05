namespace SteamP2PFriends.Shared.Enums
{
    /// <summary>
    /// 客机连接状态机（v0.2.3 P1-G 扩展）。
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
    /// v0.2.3 第一版简化：
    ///   - ServerAccepted 由 ClientMessageHandler_Accepted.ReadMessage Postfix 推进（D-7）。
    ///   - LocalPlayerCreated 由 Tick 检测本地 Player.player != null（G-2 允许的"Tick 一致性检查"，
    ///     完整事件 hook 留待 P1-G 第二版）。
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
        /// <summary>v0.2.3：已收到 Accepted（onClientConnected 触发）</summary>
        ServerAccepted,
        /// <summary>v0.2.3：本地 Player 对象已创建（Tick 一致性检查）</summary>
        LocalPlayerCreated,
        /// <summary>v0.2.3：关键初始状态已收齐（第一版与 LocalPlayerCreated 等价，bitmask hook 留待后续）</summary>
        InitialStateReceived,
        /// <summary>v0.2.3：Gameplay 就绪（第一版与 InitialStateReceived 等价）</summary>
        GameplayReady,
        /// <summary>v0.2.3：已连接（兼容旧代码，等价于 GameplayReady）</summary>
        Connected,
        /// <summary>正在断开连接（已调 Provider.disconnect，等待 onClientDisconnected）</summary>
        Disconnecting,
        /// <summary>断开完成，静态状态已清空，允许手动重连</summary>
        TeardownComplete,
        /// <summary>v0.2.3.2 P0-9：teardown 超时且静态状态未清空，禁止重连（需手动重启游戏）</summary>
        TeardownFailed,
        /// <summary>连接失败（Provider.connect 抛异常或 dismiss）</summary>
        Failed,
        /// <summary>连接超时（watchdog 超时，已冻结自动重试）</summary>
        Timeout
    }
}
