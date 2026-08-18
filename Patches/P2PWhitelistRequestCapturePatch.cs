using HarmonyLib;
using SDG.NetTransport;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using System;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    ///
    ///   - Prefix 只调 TryEnqueueRejectedTransportId（不访问 Unity/Provider/whitelist）
    ///   - 业务逻辑（Provider/Time/Snapshot）全部移到主线程 drain
    ///   - Prefix 继续为 void，任何异常只记录且继续原版 reject
    ///   - 不 patch ServerMessageHandler_ReadyToConnect，不读取 NetPakReader，不使用 transpiler
    /// </summary>
    [HarmonyPatch(typeof(Provider), "reject", new System.Type[] {
        typeof(ITransportConnection),
        typeof(ESteamRejection)
    })]
    public static class P2PWhitelistRequestCapturePatch
    {
        /// <summary>
        /// Prefix：仅提取 transport SteamID 并投递到队列。
        /// 永不访问 Unity/Provider/whitelist；永不抛给原版 reject。
        /// </summary>
        public static void Prefix(ITransportConnection transportConnection, ESteamRejection rejection)
        {
            try
            {
                if (transportConnection != null)
                    P2PQuarantineAdmissionService.OnRejected(transportConnection);
                if (rejection != ESteamRejection.WHITELISTED) return;
                if (transportConnection == null) return;
                if (!transportConnection.TryGetSteamId(out ulong transportSteamId)) return;

                // 蓝图 v3 §3.3：仅投递到队列，主线程 drain 才执行业务逻辑
                P2PJoinApprovalService.TryEnqueueRejectedTransportId(transportSteamId);
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]",
                    $"[P2P-Capture] enqueue failed; vanilla reject continues: {ex.GetType().Name}: {ex.Message}");
            }
            // 必须 void；绝不 return false；绝不读取/写入原版参数
        }
    }
}
