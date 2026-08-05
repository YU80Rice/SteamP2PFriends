using HarmonyLib;
using SDG.Unturned;
using Steamworks;
using SteamP2PFriends.Shared;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// 拦截 Lobbies.onLobbyGameCreated，P2P 路径跳过 vanilla IP 处理（对齐原版 HarmonyPatches.cs:28-42）。
    ///
    /// 原版策略：当 LobbyGameCreated_t 携带 gameServerSteamId 但 IP=0/port=0 时，
    /// 这是 P2P 信号（SetLobbyGameServer(lobby, 0, 0, hostSteamId) 写入），
    /// return false 阻断 vanilla 走 IP 连接的默认路径。
    ///
    /// 真实 GameServer 信号（IP!=0 || port!=0）让 vanilla 处理。
    /// </summary>
    [HarmonyPatch(typeof(Lobbies), "onLobbyGameCreated")]
    public static class LobbiesGameCreatedPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(LobbyGameCreated_t callback)
        {
            CSteamID gameServerSteamId = new CSteamID(callback.m_ulSteamIDGameServer);
            if (gameServerSteamId != CSteamID.Nil && callback.m_unIP == 0 && callback.m_usPort == 0)
            {
                RoleLogger.InfoVerbose("[Client]", $"[P2P-Lobby] LobbyGameCreated P2P 信号拦截，hostSteamId={gameServerSteamId.m_SteamID}");
                return false;
            }

            return true;
        }
    }
}
