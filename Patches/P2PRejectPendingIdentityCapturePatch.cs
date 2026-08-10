using HarmonyLib;
using SDG.NetTransport;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using System;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// Beta-7 名称投影修复：在原版 reject(transport, reason, explanation) 删除
    /// SteamPending 之前读取已解析的 SteamPlayerID。只做线程安全有界入队，
    /// 不修改拒绝原因、不返回 false、不访问 UI/SteamFriends/白名单。
    /// </summary>
    [HarmonyPatch(typeof(Provider), "reject", new Type[]
    {
        typeof(ITransportConnection),
        typeof(ESteamRejection),
        typeof(string)
    })]
    internal static class P2PRejectPendingIdentityCapturePatch
    {
        [HarmonyPrefix]
        private static void Prefix(ITransportConnection transportConnection, ESteamRejection rejection)
        {
            try
            {
                if (transportConnection != null)
                    P2PQuarantineAdmissionService.OnRejected(transportConnection);
                if (rejection != ESteamRejection.WHITELISTED || transportConnection == null) return;

                SteamPending pending = Provider.findPendingPlayer(transportConnection);
                SteamPlayerID playerId = ReferenceEquals(pending, null) ? null : pending.playerID;
                if (ReferenceEquals(playerId, null)) return;

                bool queued = SteamPersonaDisplay.TryEnqueueObservedIdentity(
                    playerId.steamID.m_SteamID,
                    playerId.characterName,
                    playerId.playerName,
                    playerId.nickName);

                RoleLogger.Info("[Host]", queued
                    ? "[P2P-IdentityCapture] pending identity queued before WHITELISTED removal"
                    : "[P2P-IdentityCapture] pending identity had no usable display name");
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]",
                    "[P2P-IdentityCapture] reject-prefix capture failed; vanilla reject continues: " +
                    ex.GetType().Name);
            }
        }
    }
}
