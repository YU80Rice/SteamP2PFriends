using SteamP2PFriends.Shared;
using Steamworks;
using System;

namespace SteamP2PFriends.Client
{
    /// <summary>
    ///
    ///   - 记录 relationship、persona state、双方 Steam 登录状态。
    ///   - 不阻止连接、不自动修改关系。
    ///   - 仅在客机端 TryConnectToHost 前调用一次，记录诊断信息。
    ///
    ///   - 因 HasFriend=false 阻止 ConnectP2P
    ///   - 自动添加好友
    ///   - 修改任何好友关系
    /// </summary>
    public static class FriendStatusObserver
    {
        /// <summary>
        /// 在客机端 TryConnectToHost 前记录一次好友/在线状态。
        /// 不阻止连接，不修改关系。
        /// </summary>
        public static void RecordBeforeConnect(ulong targetSteamId)
        {
            try
            {
                if (targetSteamId == 0)
                {
                    RoleLogger.Warn("[Client]", "[Diag] [D-Friend] RecordBeforeConnect: targetSteamId=0，跳过");
                    return;
                }

                ulong selfSteamId = 0;
                try
                {
                    selfSteamId = SteamUser.GetSteamID().m_SteamID;
                }
                catch (Exception ex)
                {
                    RoleLogger.Warn("[Client]", $"[Diag] [D-Friend] 获取本地 SteamUser ID 异常: {ex.Message}");
                }

                // 本地 persona state
                EPersonaState selfPersona = EPersonaState.k_EPersonaStateOffline;
                try
                {
                    selfPersona = SteamFriends.GetPersonaState();
                }
                catch (Exception ex)
                {
                    RoleLogger.Warn("[Client]", $"[Diag] [D-Friend] GetPersonaState 异常: {ex.Message}");
                }

                // 远端 relationship
                EFriendRelationship relationship = EFriendRelationship.k_EFriendRelationshipNone;
                try
                {
                    CSteamID targetId = new CSteamID(targetSteamId);
                    relationship = SteamFriends.GetFriendRelationship(targetId);
                }
                catch (Exception ex)
                {
                    RoleLogger.Warn("[Client]", $"[Diag] [D-Friend] GetFriendRelationship 异常: {ex.Message}");
                }

                // 远端 persona state
                EPersonaState targetPersona = EPersonaState.k_EPersonaStateOffline;
                try
                {
                    CSteamID targetId = new CSteamID(targetSteamId);
                    targetPersona = SteamFriends.GetFriendPersonaState(targetId);
                }
                catch (Exception ex)
                {
                    RoleLogger.Warn("[Client]", $"[Diag] [D-Friend] GetFriendPersonaState 异常: {ex.Message}");
                }

                // HasFriend（k_EFriendFlagImmediate = 0x04，"regular" friend）
                bool isRegularFriend = false;
                try
                {
                    CSteamID targetId = new CSteamID(targetSteamId);
                    isRegularFriend = SteamFriends.HasFriend(targetId, EFriendFlags.k_EFriendFlagImmediate);
                }
                catch (Exception ex)
                {
                    RoleLogger.Warn("[Client]", $"[Diag] [D-Friend] HasFriend 异常: {ex.Message}");
                }

                RoleLogger.Info("[Client]",
                    $"[Diag] [D-Friend] RecordBeforeConnect " +
                    $"self={selfSteamId} selfPersona={selfPersona} " +
                    $"target={targetSteamId} " +
                    $"relationship={relationship}({(int)relationship}) " +
                    $"targetPersona={targetPersona}({(int)targetPersona}) " +
                    $"isRegularFriend(Immediate)={isRegularFriend} " +
                    $"[记录用途，不阻止连接]");

                // 若非好友，仅输出 Info 提示，不阻止连接
                if (relationship != EFriendRelationship.k_EFriendRelationshipFriend)
                {
                    RoleLogger.Info("[Client]",
                        $"[Diag] [D-Friend] 注意：与 target={targetSteamId} 不是 k_EFriendRelationshipFriend。 " +
                        $"审计报告 4.5 明确禁止阻止连接，仅记录此事实用于诊断。");
                }
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Client]", $"[Diag] [D-Friend] RecordBeforeConnect 异常（不阻断）: {ex.Message}");
            }
        }
    }
}
