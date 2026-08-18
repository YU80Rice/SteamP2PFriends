using HarmonyLib;
using SDG.NetTransport.SteamNetworkingSockets;
using SteamP2PFriends.Shared;
using Steamworks;
using System;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    ///
    ///   - 增加 Prefix snapshot：在 handle 关闭前抓取 GetConnectionInfo +
    ///     GetConnectionRealTimeStatus + GetDetailedConnectionStatus。
    ///     原因：原生 HandleState_ProblemDetectedLocally 会先 CloseConnection，
    ///     Postfix 时 handle 已失效，GetDetailedConnectionStatus 返回 -1。
    ///   - 集成 ConnectionLifecycleTracker：在状态转换时通知 tracker，
    ///     由 tracker 周期输出 FindingRoute 0s/1s/5s/10s/20s/25s 快照。
    ///   - 不修改任何连接配置或行为，纯观察。
    /// </summary>
    [HarmonyPatch(typeof(ClientTransport_SteamNetworkingSockets), "OnSteamNetConnectionStatusChanged")]
    public static class ClientSnsStatusDiagnosticPatch
    {
        [HarmonyPrefix]
        private static void Prefix(SteamNetConnectionStatusChangedCallback_t callback)
        {
            try
            {
                string role = "[Client]";
                string label = "ClientTransport";
                HSteamNetConnection handle = callback.m_hConn;
                ESteamNetworkingConnectionState oldState = callback.m_eOldState;
                ESteamNetworkingConnectionState newState = callback.m_info.m_eState;
                ulong remoteSteamId = 0;
                try { remoteSteamId = callback.m_info.m_identityRemote.GetSteamID64(); } catch { }

                SnsDiagnosticUtil.SnapshotTerminalState(role, label, handle, callback);
                ConnectionLifecycleTracker.OnConnectionStateChanged(role, label, handle, oldState, newState, remoteSteamId);
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Client]", $"[Diag] [D10] Client SNS Prefix 异常（不阻断）: {ex.Message}");
            }
        }

        [HarmonyPostfix]
        private static void Postfix(SteamNetConnectionStatusChangedCallback_t callback)
        {
            try
            {
                SnsStatusDiagnosticUtil.LogTransition("[Client]", "ClientTransport", callback);
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Client]", $"[Diag] [D10] Client SNS status Postfix 异常（不阻断）: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(ServerTransport_SteamNetworkingSockets), "OnSteamNetConnectionStatusChanged")]
    public static class ServerSnsStatusDiagnosticPatch
    {
        [HarmonyPrefix]
        private static void Prefix(SteamNetConnectionStatusChangedCallback_t callback)
        {
            try
            {
                string role = "[Host]";
                string label = "ServerTransport";
                HSteamNetConnection handle = callback.m_hConn;
                ESteamNetworkingConnectionState oldState = callback.m_eOldState;
                ESteamNetworkingConnectionState newState = callback.m_info.m_eState;
                ulong remoteSteamId = 0;
                try { remoteSteamId = callback.m_info.m_identityRemote.GetSteamID64(); } catch { }

                SnsDiagnosticUtil.SnapshotTerminalState(role, label, handle, callback);
                ConnectionLifecycleTracker.OnConnectionStateChanged(role, label, handle, oldState, newState, remoteSteamId);
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]", $"[Diag] [D10] Server SNS Prefix 异常（不阻断）: {ex.Message}");
            }
        }

        [HarmonyPostfix]
        private static void Postfix(SteamNetConnectionStatusChangedCallback_t callback)
        {
            try
            {
                SnsStatusDiagnosticUtil.LogTransition("[Host]", "ServerTransport", callback);
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]", $"[Diag] [D10] Server SNS status Postfix 异常（不阻断）: {ex.Message}");
            }
        }
    }

    internal static class SnsStatusDiagnosticUtil
    {
        public static void LogTransition(string role, string transportLabel, SteamNetConnectionStatusChangedCallback_t callback)
        {
            ESteamNetworkingConnectionState oldState = callback.m_eOldState;
            ESteamNetworkingConnectionState newState = callback.m_info.m_eState;
            HSteamNetConnection handle = callback.m_hConn;
            ulong remoteSteamId = 0;
            try { remoteSteamId = callback.m_info.m_identityRemote.GetSteamID64(); } catch { }
            string descriptionRaw = callback.m_info.m_szConnectionDescription ?? "<empty>";
            int endReason = callback.m_info.m_eEndReason;
            string endDebugRaw = callback.m_info.m_szEndDebug ?? "<empty>";

            //   旧实现直接拼入日志，绕过脱敏，可能泄漏 hostname/ticket/cert 等敏感内容
            string endDebug = SnsDiagnosticUtil.RedactSensitiveNetworkData(endDebugRaw);
            string description = SnsDiagnosticUtil.RedactSensitiveNetworkData(descriptionRaw);

            string oldName = oldState.ToString();
            string newName = newState.ToString();
            string endReasonName = ((ESteamNetConnectionEnd)endReason).ToString();

            // 路由诊断模式不强制开 - 始终输出，这是关键诊断
            RoleLogger.Info(role,
                $"[Diag] [D10] {transportLabel}.OnSteamNetConnectionStatusChanged " +
                $"handle={handle.m_HSteamNetConnection} {oldName} -> {newName} " +
                $"remote={remoteSteamId} endReason={endReason}({endReasonName}) " +
                $"endDebug=\"{endDebug}\" description=\"{description}\"");

            // Postfix 不再重复调用（避免 handle 失效后返回 -1）。
        }
    }
}
