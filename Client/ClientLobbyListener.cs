using SDG.Unturned;
using SteamP2PFriends.Shared;
using Steamworks;

namespace SteamP2PFriends.Client
{
    /// <summary>
    ///
    /// 监听 Steam lobby 自动加入信号：当房主调 SetLobbyGameServer(lobby, 0, 0, hostSteamId) 后，
    /// 所有在 lobby 中的客机收到 LobbyGameCreated_t 回调，本监听器自动触发 P2PJoinManager.TryConnectFromLobby。
    ///
    /// 与 LobbiesGameCreatedPatch 的分工：
    ///   - LobbiesGameCreatedPatch：vanilla Lobbies.onLobbyGameCreated 的 Prefix，阻断 vanilla IP 路径
    ///   - ClientLobbyListener：Steam Callback<LobbyGameCreated_t>，触发 P2P 连接
    ///
    /// Initialize() 由 Plugin.Awake 调用，注册 Callback。
    /// </summary>
    public static class ClientLobbyListener
    {
        private static Callback<LobbyGameCreated_t> _lobbyGameCreated;
        private static bool _initialized;

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                _lobbyGameCreated = Callback<LobbyGameCreated_t>.Create(OnLobbyGameCreated);
                RoleLogger.Info("[Client]", "[P2P-Lobby] LobbyGameCreated_t 回调已注册");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Client]", $"ClientLobbyListener.Initialize 失败: {ex}");
            }
        }

        public static void Shutdown()
        {
            if (!_initialized) return;
            _initialized = false;
            _lobbyGameCreated?.Dispose();
            _lobbyGameCreated = null;
        }

        private static void OnLobbyGameCreated(LobbyGameCreated_t callback)
        {
            try
            {
                if (Provider.isConnected) return;

                CSteamID gameServerSteamId = new CSteamID(callback.m_ulSteamIDGameServer);
                if (gameServerSteamId == CSteamID.Nil) return;

                // 真实 GameServer 信号（IP!=0 || port!=0）让 vanilla 处理
                if (callback.m_unIP != 0 || callback.m_usPort != 0) return;

                // P2P 信号：用 SteamID 走 P2P 连接
                RoleLogger.Info("[Client]",
                    $"[P2P-Lobby] Joining P2P host {gameServerSteamId.m_SteamID} from lobby {callback.m_ulSteamIDLobby}");

                P2PJoinManager.TryConnectFromLobby(gameServerSteamId.m_SteamID);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Client]", $"OnLobbyGameCreated 失败: {ex}");
            }
        }
    }
}
