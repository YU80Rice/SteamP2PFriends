using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Client;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using SteamP2PFriends.Shared.Enums;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// Beta-5 P0-GROUP-STATE-SYNC-01 的只读诊断运行时。
    /// 不写 groupID/rank，不调用 ServerAssignToGroup/ReceiveGroupState/leaveGroup。
    /// </summary>
    internal static class P2PGroupStateProbeRuntime
    {
        private const float LogIntervalSeconds = 3f;
        private static readonly Dictionary<string, float> NextLogAtByKey = new Dictionary<string, float>();

        internal static bool IsActiveP2PSession
        {
            get
            {
                if (HostManager.IsP2PHostMode)
                    return Provider.isServer;

                if (!Provider.isClient || !Provider.isConnected)
                    return false;

                return IsClientP2PState(P2PJoinManager.State);
            }
        }

        internal static bool ShouldRunForTest(
            bool isP2PHostMode,
            bool isServer,
            bool isClient,
            bool isConnected,
            EJoinState joinState)
        {
            if (isP2PHostMode) return isServer;
            return isClient && isConnected && IsClientP2PState(joinState);
        }

        private static bool IsClientP2PState(EJoinState state)
        {
            return state == EJoinState.ServerAccepted ||
                   state == EJoinState.LocalPlayerCreated ||
                   state == EJoinState.InitialStateReceived ||
                   state == EJoinState.GameplayReady ||
                   state == EJoinState.Connected;
        }

        internal static string MaskIdForTest(ulong value)
        {
            return MaskId(value);
        }

        private static string MaskId(ulong value)
        {
            return value == 0UL ? "none" : "..." + (value % 10000UL).ToString("D4");
        }

        private static bool TryEnter(string eventName, ulong playerId)
        {
            // 断言必须在任何 Provider/Player/Unity 状态读取之前，且不得被下层 catch 吞掉。
            ThreadUtil.assertIsGameThread();
            if (!IsActiveP2PSession) return false;

            float now = Time.realtimeSinceStartup;
            string key = eventName + ":" + playerId;
            float next;
            if (NextLogAtByKey.TryGetValue(key, out next) && now < next) return false;
            NextLogAtByKey[key] = now + LogIntervalSeconds;
            return true;
        }

        internal static void LogPlayerState(string eventName, string phase, PlayerQuests quests)
        {
            ThreadUtil.assertIsGameThread();
            if (!IsActiveP2PSession) return;
            ulong playerId = TryGetPlayerId(quests);
            if (!TryEnter(eventName + "/" + phase, playerId)) return;

            try
            {
                if (ReferenceEquals(quests, null))
                {
                    RoleLogger.Info("[Shared]", "[P2P-GroupProbe] event=" + eventName +
                        " phase=" + phase + " role=" + Role() + " quests=null");
                    return;
                }

                CSteamID groupId = quests.groupID;
                GroupInfo info = groupId == CSteamID.Nil ? null : GroupManager.getGroupInfo(groupId);
                uint members = ReferenceEquals(info, null) ? 0U : info.members;

                RoleLogger.Info("[Shared]", "[P2P-GroupProbe] event=" + eventName +
                    " phase=" + phase +
                    " role=" + Role() +
                    " player=" + MaskId(playerId) +
                    " group=" + MaskId(groupId.m_SteamID) +
                    " rank=" + quests.groupRank +
                    " member=" + quests.isMemberOfAGroup +
                    " groupKnown=" + (!ReferenceEquals(info, null)) +
                    " members=" + members +
                    " connected=" + Provider.isConnected);
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Shared]", "[P2P-GroupProbe] event=" + eventName +
                    " phase=" + phase + " snapshot failed: " + ex.GetType().Name);
            }
        }

        internal static void LogIncomingGroupState(PlayerQuests quests, CSteamID incomingGroup, EPlayerGroupRank incomingRank)
        {
            ThreadUtil.assertIsGameThread();
            if (!IsActiveP2PSession) return;
            ulong playerId = TryGetPlayerId(quests);
            if (!TryEnter("ReceiveGroupState/incoming", playerId)) return;

            RoleLogger.Info("[Shared]", "[P2P-GroupProbe] event=ReceiveGroupState phase=incoming" +
                " role=" + Role() +
                " player=" + MaskId(playerId) +
                " incomingGroup=" + MaskId(incomingGroup.m_SteamID) +
                " incomingRank=" + incomingRank);
        }

        internal static void LogAllPlayerStates(string eventName, string phase)
        {
            // 全局事件使用独立节流键；断言和 P2P 门在 TryEnter 内完成。
            if (!TryEnter(eventName + "/" + phase, 0UL)) return;

            try
            {
                int emitted = 0;
                Player localPlayer = Player.LocalPlayer;
                if (!ReferenceEquals(localPlayer, null) && !ReferenceEquals(localPlayer.quests, null))
                {
                    LogPlayerState(eventName + "/local", phase, localPlayer.quests);
                    emitted++;
                }

                List<SteamPlayer> clients = Provider.clients;
                if (clients != null)
                {
                    for (int i = 0; i < clients.Count; i++)
                    {
                        SteamPlayer steamPlayer = clients[i];
                        Player player = ReferenceEquals(steamPlayer, null) ? null : steamPlayer.player;
                        if (ReferenceEquals(player, null) || ReferenceEquals(player.quests, null)) continue;
                        LogPlayerState(eventName + "/client", phase, player.quests);
                        emitted++;
                    }
                }

                RoleLogger.Info("[Shared]", "[P2P-GroupProbe] event=" + eventName +
                    " phase=" + phase + " role=" + Role() +
                    " logicalPath=/Groups.dat playersObserved=" + emitted +
                    " map=" + (Provider.map ?? "none"));
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Shared]", "[P2P-GroupProbe] event=" + eventName +
                    " phase=" + phase + " aggregate failed: " + ex.GetType().Name);
            }
        }

        private static ulong TryGetPlayerId(PlayerQuests quests)
        {
            try
            {
                if (ReferenceEquals(quests, null) || ReferenceEquals(quests.player, null) ||
                    ReferenceEquals(quests.player.channel, null) ||
                    ReferenceEquals(quests.player.channel.owner, null) ||
                    ReferenceEquals(quests.player.channel.owner.playerID, null))
                    return 0UL;

                return quests.player.channel.owner.playerID.steamID.m_SteamID;
            }
            catch
            {
                return 0UL;
            }
        }

        private static string Role()
        {
            return HostManager.IsP2PHostMode && Provider.isServer ? "host" : "client";
        }
    }

    [HarmonyPatch(typeof(PlayerQuests), nameof(PlayerQuests.ReceiveGroupState))]
    internal static class P2PGroupStateProbe_ReceiveGroupState
    {
        [HarmonyPrefix]
        private static void Prefix(PlayerQuests __instance, CSteamID __0, EPlayerGroupRank __1)
        {
            P2PGroupStateProbeRuntime.LogIncomingGroupState(__instance, __0, __1);
            P2PGroupStateProbeRuntime.LogPlayerState("ReceiveGroupState", "before", __instance);
        }

        [HarmonyPostfix]
        private static void Postfix(PlayerQuests __instance)
        {
            P2PGroupStateProbeRuntime.LogPlayerState("ReceiveGroupState", "after", __instance);
        }
    }

    [HarmonyPatch(typeof(GroupManager), "load")]
    internal static class P2PGroupStateProbe_GroupLoad
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            P2PGroupStateProbeRuntime.LogAllPlayerStates("GroupManager.load", "before");
        }

        [HarmonyPostfix]
        private static void Postfix()
        {
            P2PGroupStateProbeRuntime.LogAllPlayerStates("GroupManager.load", "after");
        }
    }

    [HarmonyPatch(typeof(GroupManager), "save")]
    internal static class P2PGroupStateProbe_GroupSave
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            P2PGroupStateProbeRuntime.LogAllPlayerStates("GroupManager.save", "before");
        }

        [HarmonyPostfix]
        private static void Postfix()
        {
            P2PGroupStateProbeRuntime.LogAllPlayerStates("GroupManager.save", "after");
        }
    }

    [HarmonyPatch(typeof(PlayerQuests), "ReceiveCreateGroupRequest")]
    internal static class P2PGroupStateProbe_ReceiveCreateGroup
    {
        [HarmonyPrefix]
        private static void Prefix(PlayerQuests __instance)
        {
            P2PGroupStateProbeRuntime.LogPlayerState("ReceiveCreateGroupRequest", "before", __instance);
        }

        [HarmonyPostfix]
        private static void Postfix(PlayerQuests __instance)
        {
            P2PGroupStateProbeRuntime.LogPlayerState("ReceiveCreateGroupRequest", "after", __instance);
        }
    }

    [HarmonyPatch(typeof(PlayerQuests), "ReceiveAcceptGroupInvitationRequest")]
    internal static class P2PGroupStateProbe_ReceiveAcceptGroup
    {
        [HarmonyPrefix]
        private static void Prefix(PlayerQuests __instance)
        {
            P2PGroupStateProbeRuntime.LogPlayerState("ReceiveAcceptGroupInvitationRequest", "before", __instance);
        }

        [HarmonyPostfix]
        private static void Postfix(PlayerQuests __instance)
        {
            P2PGroupStateProbeRuntime.LogPlayerState("ReceiveAcceptGroupInvitationRequest", "after", __instance);
        }
    }

    [HarmonyPatch]
    internal static class P2PGroupStateProbe_SendInitialPlayerState
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo[] methods = typeof(PlayerQuests).GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name == "SendInitialPlayerState") yield return methods[i];
            }
        }

        [HarmonyPrefix]
        private static void Prefix(PlayerQuests __instance)
        {
            P2PGroupStateProbeRuntime.LogPlayerState("SendInitialPlayerState", "before", __instance);
        }

        [HarmonyPostfix]
        private static void Postfix(PlayerQuests __instance)
        {
            P2PGroupStateProbeRuntime.LogPlayerState("SendInitialPlayerState", "after", __instance);
        }
    }
}
