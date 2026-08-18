using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using SteamP2PFriends.Shared.Enums;
using Steamworks;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    ///
    /// 致命痛点：vanilla ServerTransport_SteamNetworkingSockets.Initialize 用
    ///   Callback<SteamNetConnectionStatusChangedCallback_t>.CreateGameServer(handler)
    ///   Callback<SteamNetAuthenticationStatus_t>.CreateGameServer(handler)
    /// 注册 callback。这些 callback 由 SteamGameServer.RunCallbacks() 驱动。
    ///
    /// 但 SteamUserP2PRedirectPatch 把 P2P 监听重定向到 SteamNetworkingSockets（SteamUser identity）后，
    /// 连接事件由 SteamAPI.RunCallbacks() 分发（SteamUser 管道）。
    /// vanilla 注册的 GameServer callback 永远不会被触发 -> 服务端收不到任何连接事件！
    ///
    /// 本 patch 拦截 Callback<T>.CreateGameServer，重定向到 Callback<T>.Create（SteamUser 版）。
    ///
    /// </summary>
    public static class CallbackCreateGameServerRedirectPatch
    {
        private static bool ShouldRedirect => HostManager.HostMode == EHostMode.P2P;

        [HarmonyPatch(typeof(Callback<SteamNetConnectionStatusChangedCallback_t>), "CreateGameServer")]
        [HarmonyPrefix]
        public static bool ConnStatus_CreateGameServer_Prefix(
            ref Callback<SteamNetConnectionStatusChangedCallback_t> __result,
            Callback<SteamNetConnectionStatusChangedCallback_t>.DispatchDelegate func)
        {
            if (!ShouldRedirect) return true;
            __result = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(func);
            RoleLogger.Info("[Host]", "[P2P-SteamUser] Callback<SteamNetConnectionStatusChangedCallback_t>.CreateGameServer 重定向到 SteamUser Create");
            return false;
        }

        [HarmonyPatch(typeof(Callback<SteamNetAuthenticationStatus_t>), "CreateGameServer")]
        [HarmonyPrefix]
        public static bool AuthStatus_CreateGameServer_Prefix(
            ref Callback<SteamNetAuthenticationStatus_t> __result,
            Callback<SteamNetAuthenticationStatus_t>.DispatchDelegate func)
        {
            if (!ShouldRedirect) return true;
            __result = Callback<SteamNetAuthenticationStatus_t>.Create(func);
            RoleLogger.Info("[Host]", "[P2P-SteamUser] Callback<SteamNetAuthenticationStatus_t>.CreateGameServer 重定向到 SteamUser Create");
            return false;
        }
    }
}
