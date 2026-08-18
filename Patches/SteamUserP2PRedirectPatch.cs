using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using SteamP2PFriends.Shared.Enums;
using Steamworks;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// SteamUser-identity P2P/Direct-IP 重定向（P2P 模式启用，LAN 模式放行原生）。
    ///
    /// 核心思路：vanilla ServerTransport_SteamNetworkingSockets 调用
    ///   SteamGameServerNetworkingSockets.CreateListenSocketP2P
    /// 启动 P2P 监听，但 GameServer identity 因 SetDedicatedServer(false) 拿不到 SDR 路由。
    ///
    /// 本 patch 把 12 个关键 SteamGameServerNetworkingSockets 方法重定向到
    /// SteamNetworkingSockets（SteamUser identity）。
    ///
    ///   - P2P 模式（HostMode == EHostMode.P2P）：启用重定向，监听落到 SteamUser identity，
    ///     与 LinkLobbyForP2P 写入的个人 SteamID 精确配对
    ///   - LAN 模式（HostMode == EHostMode.LAN）：放行原生 SteamGameServerNetworkingSockets，
    ///     保留 GS-identity IP 直连路径（127.0.0.1:27015/27016 可用）
    ///   - None 模式（未启动 / 菜单）：放行原生，避免影响 vanilla 单机
    ///
    /// SDK 证据：
    ///   - isteamnetworkingsockets.cs:100 SteamNetworkingSockets.CreateListenSocketP2P（SteamUser 版）
    ///   - isteamgameservernetworkingsockets.cs:100 SteamGameServerNetworkingSockets.CreateListenSocketP2P（GameServer 版）
    ///   - 两者签名完全一致，可直接重定向
    /// </summary>
    public static class SteamUserP2PRedirectPatch
    {
        /// <summary>
        /// 门控：仅 P2P 模式启用重定向。LAN/None 模式放行原生 vanilla 行为。
        /// </summary>
        private static bool ShouldRedirect => HostManager.HostMode == EHostMode.P2P;

        internal static System.Reflection.MethodInfo ResolveCreateListenSocketIPTargetForTest()
        {
            return AccessTools.Method(typeof(SteamGameServerNetworkingSockets),
                nameof(SteamGameServerNetworkingSockets.CreateListenSocketIP),
                new[] { typeof(SteamNetworkingIPAddr).MakeByRefType(), typeof(int), typeof(SteamNetworkingConfigValue_t[]) });
        }

        [HarmonyPatch(typeof(SteamGameServerNetworkingSockets), nameof(SteamGameServerNetworkingSockets.CreateListenSocketIP))]
        [HarmonyPrefix]
        public static bool CreateListenSocketIP_Prefix(ref HSteamListenSocket __result,
            ref SteamNetworkingIPAddr localAddress, int nOptions, SteamNetworkingConfigValue_t[] pOptions)
        {
            if (!ShouldRedirect) return true;

            __result = SteamNetworkingSockets.CreateListenSocketIP(ref localAddress, nOptions, pOptions);
            RoleLogger.Info("[Host]",
                "[DirectIP-SteamUser] CreateListenSocketIP redirected " +
                "handle=" + __result.m_HSteamListenSocket +
                " ipv4=" + localAddress.GetIPv4() +
                " connectionPort=" + localAddress.m_port +
                " valid=" + (__result != HSteamListenSocket.Invalid));
            return false;
        }

        [HarmonyPatch(typeof(SteamGameServerNetworkingSockets), nameof(SteamGameServerNetworkingSockets.CreateListenSocketP2P))]
        [HarmonyPrefix]
        public static bool CreateListenSocketP2P_Prefix(ref HSteamListenSocket __result,
            int nLocalVirtualPort, int nOptions, SteamNetworkingConfigValue_t[] pOptions)
        {
            if (!ShouldRedirect) return true;
            __result = SteamNetworkingSockets.CreateListenSocketP2P(nLocalVirtualPort, nOptions, pOptions);
            RoleLogger.Info("[Host]", $"[P2P-SteamUser] CreateListenSocketP2P 重定向到 SteamUser identity (handle={__result.m_HSteamListenSocket})");
            return false;
        }

        [HarmonyPatch(typeof(SteamGameServerNetworkingSockets), nameof(SteamGameServerNetworkingSockets.AcceptConnection))]
        [HarmonyPrefix]
        public static bool AcceptConnection_Prefix(ref EResult __result, HSteamNetConnection hConn)
        {
            // 不论 ShouldRedirect 与否，都先记录一次调用事实与 connection state
            try
            {
                ulong remoteSteamId = 0;
                ESteamNetworkingConnectionState state = ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_None;
                if (SteamNetworkingSockets.GetConnectionInfo(hConn, out SteamNetConnectionInfo_t info))
                {
                    remoteSteamId = info.m_identityRemote.GetSteamID64();
                    state = info.m_eState;
                }
                RoleLogger.Info("[Host]",
                    $"[Diag] [D-Accept] SteamGameServerNetworkingSockets.AcceptConnection ENTER " +
                    $"handle={hConn.m_HSteamNetConnection} ShouldRedirect={ShouldRedirect} " +
                    $"HostMode={HostManager.HostMode} remote={remoteSteamId} state={state}");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Host]", $"[Diag] [D-Accept] Prefix 诊断异常（不阻断）: {ex.Message}");
            }

            if (!ShouldRedirect)
            {
                // 不重定向：放行原生 SteamGameServerNetworkingSockets.AcceptConnection
                return true;
            }

            // 重定向到 SteamUser 接口
            EResult er = SteamNetworkingSockets.AcceptConnection(hConn);
            __result = er;
            try
            {
                RoleLogger.Info("[Host]",
                    $"[Diag] [D-Accept] AcceptConnection RETURNED (redirected) " +
                    $"handle={hConn.m_HSteamNetConnection} EResult={er}({(int)er})");
            }
            catch { }
            return false;
        }

        /// <summary>
        /// ShouldRedirect=true 时 Prefix 已记录，Postfix 跳过避免重复。
        /// </summary>
        [HarmonyPatch(typeof(SteamGameServerNetworkingSockets), nameof(SteamGameServerNetworkingSockets.AcceptConnection))]
        [HarmonyPostfix]
        public static void AcceptConnection_Postfix(EResult __result, HSteamNetConnection hConn)
        {
            if (ShouldRedirect) return; // redirected case already logged in Prefix
            try
            {
                RoleLogger.Info("[Host]",
                    $"[Diag] [D-Accept] AcceptConnection RETURNED (native, not redirected) " +
                    $"handle={hConn.m_HSteamNetConnection} EResult={__result}({(int)__result})");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Host]", $"[Diag] [D-Accept] Postfix 诊断异常（不阻断）: {ex.Message}");
            }
        }

        [HarmonyPatch(typeof(SteamGameServerNetworkingSockets), nameof(SteamGameServerNetworkingSockets.SetConnectionPollGroup))]
        [HarmonyPrefix]
        public static bool SetConnectionPollGroup_Prefix(ref bool __result, HSteamNetConnection hConn, HSteamNetPollGroup hPollGroup)
        {
            try
            {
                ulong remoteSteamId = 0;
                ESteamNetworkingConnectionState state = ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_None;
                if (SteamNetworkingSockets.GetConnectionInfo(hConn, out SteamNetConnectionInfo_t info))
                {
                    remoteSteamId = info.m_identityRemote.GetSteamID64();
                    state = info.m_eState;
                }
                RoleLogger.Info("[Host]",
                    $"[Diag] [D-PollGroup] SteamGameServerNetworkingSockets.SetConnectionPollGroup ENTER " +
                    $"handle={hConn.m_HSteamNetConnection} pollGroup={hPollGroup.m_HSteamNetPollGroup} " +
                    $"ShouldRedirect={ShouldRedirect} HostMode={HostManager.HostMode} " +
                    $"remote={remoteSteamId} state={state}");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Host]", $"[Diag] [D-PollGroup] Prefix 诊断异常（不阻断）: {ex.Message}");
            }

            if (!ShouldRedirect) return true;
            bool ok = SteamNetworkingSockets.SetConnectionPollGroup(hConn, hPollGroup);
            __result = ok;
            try
            {
                RoleLogger.Info("[Host]",
                    $"[Diag] [D-PollGroup] SetConnectionPollGroup RETURNED (redirected) " +
                    $"handle={hConn.m_HSteamNetConnection} result={ok}");
            }
            catch { }
            return false;
        }

        /// <summary>
        /// </summary>
        [HarmonyPatch(typeof(SteamGameServerNetworkingSockets), nameof(SteamGameServerNetworkingSockets.SetConnectionPollGroup))]
        [HarmonyPostfix]
        public static void SetConnectionPollGroup_Postfix(bool __result, HSteamNetConnection hConn, HSteamNetPollGroup hPollGroup)
        {
            if (ShouldRedirect) return; // redirected case already logged in Prefix
            try
            {
                RoleLogger.Info("[Host]",
                    $"[Diag] [D-PollGroup] SetConnectionPollGroup RETURNED (native, not redirected) " +
                    $"handle={hConn.m_HSteamNetConnection} pollGroup={hPollGroup.m_HSteamNetPollGroup} result={__result}");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Host]", $"[Diag] [D-PollGroup] Postfix 诊断异常（不阻断）: {ex.Message}");
            }
        }

        [HarmonyPatch(typeof(SteamGameServerNetworkingSockets), nameof(SteamGameServerNetworkingSockets.CloseConnection))]
        [HarmonyPrefix]
        public static bool CloseConnection_Prefix(ref bool __result, HSteamNetConnection hPeer,
            int nReason, string pszDebug, bool bEnableLinger)
        {
            try
            {
                ulong remoteSteamId = 0;
                string endReasonName = ((ESteamNetConnectionEnd)nReason).ToString();
                if (SteamNetworkingSockets.GetConnectionInfo(hPeer, out SteamNetConnectionInfo_t info))
                {
                    remoteSteamId = info.m_identityRemote.GetSteamID64();
                }
                RoleLogger.Info("[Host]",
                    $"[Diag] [D9] SteamGameServerNetworkingSockets.CloseConnection " +
                    $"handle={hPeer.m_HSteamNetConnection} remote={remoteSteamId} " +
                    $"reason={nReason}({endReasonName}) debug=\"{pszDebug ?? "<null>"}\" linger={bEnableLinger}");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Host]", $"[Diag] [D9] CloseConnection 诊断异常（不阻断）: {ex.Message}");
            }

            if (!ShouldRedirect) return true;
            __result = SteamNetworkingSockets.CloseConnection(hPeer, nReason, pszDebug, bEnableLinger);
            return false;
        }

        [HarmonyPatch(typeof(SteamGameServerNetworkingSockets), nameof(SteamGameServerNetworkingSockets.CreatePollGroup))]
        [HarmonyPrefix]
        public static bool CreatePollGroup_Prefix(ref HSteamNetPollGroup __result)
        {
            if (!ShouldRedirect) return true;
            __result = SteamNetworkingSockets.CreatePollGroup();
            return false;
        }

        [HarmonyPatch(typeof(SteamGameServerNetworkingSockets), nameof(SteamGameServerNetworkingSockets.DestroyPollGroup))]
        [HarmonyPrefix]
        public static bool DestroyPollGroup_Prefix(ref bool __result, HSteamNetPollGroup hPollGroup)
        {
            if (!ShouldRedirect) return true;
            __result = SteamNetworkingSockets.DestroyPollGroup(hPollGroup);
            return false;
        }

        [HarmonyPatch(typeof(SteamGameServerNetworkingSockets), nameof(SteamGameServerNetworkingSockets.ReceiveMessagesOnPollGroup))]
        [HarmonyPrefix]
        public static bool ReceiveMessagesOnPollGroup_Prefix(ref int __result,
            HSteamNetPollGroup hPollGroup, System.IntPtr[] ppOutMessages, int nMaxMessages)
        {
            if (!ShouldRedirect) return true;
            __result = SteamNetworkingSockets.ReceiveMessagesOnPollGroup(hPollGroup, ppOutMessages, nMaxMessages);
            return false;
        }

        [HarmonyPatch(typeof(SteamGameServerNetworkingSockets), nameof(SteamGameServerNetworkingSockets.CloseListenSocket))]
        [HarmonyPrefix]
        public static bool CloseListenSocket_Prefix(ref bool __result, HSteamListenSocket hSocket)
        {
            if (!ShouldRedirect) return true;
            __result = SteamNetworkingSockets.CloseListenSocket(hSocket);
            return false;
        }

        [HarmonyPatch(typeof(SteamGameServerNetworkingSockets), nameof(SteamGameServerNetworkingSockets.SendMessageToConnection))]
        [HarmonyPrefix]
        public static bool SendMessageToConnection_Prefix(ref EResult __result,
            HSteamNetConnection hConn, System.IntPtr pData, uint cbData, int nSendFlags, out long pOutMessageNumber)
        {
            if (!ShouldRedirect)
            {
                pOutMessageNumber = 0;
                return true;
            }
            __result = SteamNetworkingSockets.SendMessageToConnection(hConn, pData, cbData, nSendFlags, out pOutMessageNumber);
            return false;
        }

        [HarmonyPatch(typeof(SteamGameServerNetworkingSockets), nameof(SteamGameServerNetworkingSockets.ReceiveMessagesOnConnection))]
        [HarmonyPrefix]
        public static bool ReceiveMessagesOnConnection_Prefix(ref int __result,
            HSteamNetConnection hConn, System.IntPtr[] ppOutMessages, int nMaxMessages)
        {
            if (!ShouldRedirect) return true;
            __result = SteamNetworkingSockets.ReceiveMessagesOnConnection(hConn, ppOutMessages, nMaxMessages);
            return false;
        }

        [HarmonyPatch(typeof(SteamGameServerNetworkingSockets), nameof(SteamGameServerNetworkingSockets.GetConnectionInfo))]
        [HarmonyPrefix]
        public static bool GetConnectionInfo_Prefix(ref bool __result,
            HSteamNetConnection hConn, out SteamNetConnectionInfo_t pInfo)
        {
            if (!ShouldRedirect)
            {
                pInfo = default(SteamNetConnectionInfo_t);
                return true;
            }
            __result = SteamNetworkingSockets.GetConnectionInfo(hConn, out pInfo);
            return false;
        }

        [HarmonyPatch(typeof(SteamGameServerNetworkingSockets), nameof(SteamGameServerNetworkingSockets.SetConnectionName))]
        [HarmonyPrefix]
        public static bool SetConnectionName_Prefix(HSteamNetConnection hPeer, string pszName)
        {
            if (!ShouldRedirect) return true;
            SteamNetworkingSockets.SetConnectionName(hPeer, pszName);
            return false;
        }
    }
}
