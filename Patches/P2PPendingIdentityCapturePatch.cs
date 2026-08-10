using HarmonyLib;
using SDG.NetTransport;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// 在原版 SteamPending 已解析 SteamPlayerID 时，捕获“SteamID -> characterName”的显示投影。
    /// 这里只允许有界入队；不得访问 Provider/UI/SteamFriends，不得改变原版构造结果。
    /// </summary>
    [HarmonyPatch]
    internal static class P2PPendingIdentityCapturePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            ConstructorInfo[] constructors = typeof(SteamPending).GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            for (int i = 0; i < constructors.Length; i++)
            {
                ParameterInfo[] parameters = constructors[i].GetParameters();
                if (parameters.Length > 1 && parameters[1].ParameterType == typeof(SteamPlayerID))
                    yield return constructors[i];
            }
        }

        [HarmonyPostfix]
        private static void Postfix(ITransportConnection __0, SteamPlayerID __1)
        {
            try
            {
                if (ReferenceEquals(__1, null)) return;
                P2PQuarantineAdmissionService.BindPending(__1.steamID, __0);
                SteamPersonaDisplay.TryEnqueueObservedIdentity(
                    __1.steamID.m_SteamID,
                    __1.characterName,
                    __1.playerName,
                    __1.nickName);
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Shared]",
                    "[P2P-IdentityCapture] enqueue failed; vanilla pending continues: " + ex.GetType().Name);
            }
        }
    }
}
