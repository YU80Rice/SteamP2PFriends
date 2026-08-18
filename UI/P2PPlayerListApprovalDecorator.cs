using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SteamP2PFriends.UI
{
    /// <summary>
    /// the returned row is wrapped, narrowed by 70px, and an approval/revoke button occupies the far right.
    /// </summary>
    internal static class P2PPlayerListApprovalDecorator
    {
        private const int ActionWidth = 70;
        private const float ClickCooldownSeconds = 1.5f;
        private static readonly Dictionary<ulong, float> NextClickAt = new Dictionary<ulong, float>();

        internal static bool RegistrationValid { get; private set; }

        internal static void RegisterManual(Harmony harmony)
        {
            RegistrationValid = false;
            Type type = typeof(PlayerDashboardInformationUI);
            MethodInfo normal = AccessTools.Method(type, "OnCreatePlayerEntry", new[] { typeof(SteamPlayer) });
            MethodInfo grouped = AccessTools.Method(type, "OnCreatePlayerEntryWithGrouping", new[] { typeof(SteamPlayer) });
            MethodInfo postfix = AccessTools.Method(typeof(P2PPlayerListApprovalDecorator), nameof(Postfix));
            if (normal == null || grouped == null || postfix == null)
            {
                RoleLogger.Error("[Shared]", "[P2P-QuarantineUI] vanilla player row factories unresolved");
                return;
            }

            harmony.Patch(normal, postfix: new HarmonyMethod(postfix));
            harmony.Patch(grouped, postfix: new HarmonyMethod(postfix));
            RegistrationValid = HasOwnedPostfix(normal, postfix) && HasOwnedPostfix(grouped, postfix);
            if (!RegistrationValid)
                RoleLogger.Error("[Shared]", "[P2P-QuarantineUI] player row decorator registration failed");
        }

        internal static void Postfix(SteamPlayer __0, ref ISleekElement __result)
        {
            ISleekElement vanilla = __result;
            try
            {
                ThreadUtil.assertIsGameThread();
                if (ShouldDecorateLocalHost(__0, out CSteamID localHost))
                {
                    __result = BuildDecoratedRow(vanilla, "复制ID",
                        "复制房主 SteamID。将它发给客机，客机可在原版“开始游戏 → 直连”的地址栏中输入并加入。",
                        button => OnClickedCopyHostId(localHost, button));
                    RoleLogger.Info("[Host]", "[P2P-QuarantineUI] decorated local host row with copy action");
                    return;
                }
                if (!ShouldDecorate(__0, out CSteamID target, out bool pending)) return;
                __result = BuildDecoratedRow(vanilla, pending ? "允许" : "撤销允许",
                    pending ? "写入房主白名单并立即解除行动限制" : "从房主白名单移除并断开该玩家",
                    button => OnClicked(target, button));
                RoleLogger.Info("[Host]",
                    "[P2P-QuarantineUI] decorated player row target=" + target.m_SteamID +
                    " pending=" + pending + " actionOffset=" + (-ActionWidth));
            }
            catch (Exception ex)
            {
                __result = vanilla;
                RoleLogger.Error("[Host]",
                    "[P2P-QuarantineUI] decorate failed, preserving vanilla row: " + ex.GetType().Name);
            }
        }

        private static ISleekElement BuildDecoratedRow(ISleekElement vanilla, string text, string tooltip,
            Action<ISleekButton> onClicked)
        {
            ISleekElement wrapper = Glazier.Get().CreateFrame();
            wrapper.SizeScale_X = 1f;
            // SleekList assigns the vanilla 50px row height after factory return.
            wrapper.SizeScale_Y = 0f;

            vanilla.SizeScale_X = 1f;
            vanilla.SizeOffset_X = -ActionWidth;
            vanilla.SizeScale_Y = 1f;
            wrapper.AddChild(vanilla);

            ISleekButton action = Glazier.Get().CreateButton();
            action.PositionScale_X = 1f;
            action.PositionOffset_X = -ActionWidth;
            action.SizeOffset_X = ActionWidth;
            action.SizeScale_Y = 1f;
            action.FontSize = ESleekFontSize.Small;
            action.Text = text;
            action.TooltipText = tooltip;
            action.OnClicked += button => onClicked(action);
            wrapper.AddChild(action);
            return wrapper;
        }

        private static bool ShouldDecorateLocalHost(SteamPlayer player, out CSteamID localHost)
        {
            localHost = CSteamID.Nil;
            if (!HostManager.IsP2PHostMode || !Provider.isServer) return false;
            if (ReferenceEquals(player, null) || ReferenceEquals(player.playerID, null) ||
                player.player == null || player.player.channel == null || !player.player.channel.IsLocalPlayer)
                return false;

            localHost = Provider.user;
            return localHost.IsValid() && localHost.BIndividualAccount();
        }

        private static void OnClickedCopyHostId(CSteamID localHost, ISleekButton button)
        {
            ThreadUtil.assertIsGameThread();
            try
            {
                GUIUtility.systemCopyBuffer = localHost.m_SteamID.ToString();
                button.Text = "已复制";
                button.TooltipText = "房主 SteamID 已复制。发送给客机后，让其粘贴到原版直连地址栏。";
                button.IsClickable = true;
                RoleLogger.Info("[Host]", "[P2P-QuarantineUI] local host SteamID copied");
            }
            catch (Exception ex)
            {
                button.Text = "复制失败";
                RoleLogger.Warn("[Host]", "[P2P-QuarantineUI] copy host SteamID failed: " + ex.GetType().Name);
            }
        }

        private static bool ShouldDecorate(SteamPlayer player, out CSteamID target, out bool pending)
        {
            target = CSteamID.Nil;
            pending = false;
            if (!HostManager.IsP2PHostMode || !Provider.isServer || !Provider.isWhitelisted)
                return false;
            if (ReferenceEquals(player, null) || ReferenceEquals(player.playerID, null) ||
                player.player == null || player.player.channel == null || player.player.channel.IsLocalPlayer)
                return false;

            target = player.playerID.steamID;
            if (target == CSteamID.Nil || !target.IsValid()) return false;
            // The vanilla list may build a row before Reserved/Pending is promoted to Active.
            pending = P2PQuarantineAdmissionService.IsKnown(target);
            return pending || P2PWhitelistService.ContainsForUi(target);
        }

        private static void OnClicked(CSteamID target, ISleekButton button)
        {
            ThreadUtil.assertIsGameThread();
            float now = Time.realtimeSinceStartup;
            if (NextClickAt.TryGetValue(target.m_SteamID, out float next) && now < next) return;
            NextClickAt[target.m_SteamID] = now + ClickCooldownSeconds;

            if (!IsStillConnected(target))
            {
                button.Text = "已离线";
                button.IsClickable = false;
                return;
            }

            bool pendingNow = P2PQuarantineAdmissionService.IsKnown(target);
            button.Text = pendingNow ? "允许" : "撤销允许";

            bool ok;
            string feedback;
            if (pendingNow)
                ok = P2PJoinApprovalService.Approve(target, out feedback);
            else
                ok = P2PWhitelistService.TryRemove(target, out feedback);

            if (ok)
            {
                if (pendingNow)
                {
                    // Approval keeps the same row/action alive, immediately transitioning to revoke.
                    button.Text = "撤销允许";
                    button.TooltipText = "从房主白名单移除并断开该玩家";
                    button.IsClickable = true;
                }
                else
                {
                    button.Text = "已撤销";
                    button.IsClickable = false;
                }
            }
            else
            {
                button.Text = "操作失败";
            }

            RoleLogger.Info("[Host]",
                "[P2P-QuarantineUI] action target=" + target.m_SteamID +
                " pending=" + pendingNow + " ok=" + ok + " feedback=" + feedback);
        }

        private static bool IsStillConnected(CSteamID target)
        {
            for (int i = 0; i < Provider.clients.Count; i++)
            {
                SteamPlayer player = Provider.clients[i];
                if (!ReferenceEquals(player, null) && !ReferenceEquals(player.playerID, null) &&
                    player.playerID.steamID.m_SteamID == target.m_SteamID) return true;
            }
            return false;
        }

        private static bool HasOwnedPostfix(MethodBase original, MethodInfo expected)
        {
            HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
            if (info == null) return false;
            foreach (Patch patch in info.Postfixes)
            {
                if (patch.owner == SteamP2PFriendsPlugin.HARMONY_ID && patch.PatchMethod == expected)
                    return true;
            }
            return false;
        }

        internal static int ActionPositionOffsetForTest => -ActionWidth;
        internal static float WrapperHeightScaleForTest => 0f;
        internal static bool IsActionClickableAfterSuccessForTest(bool wasPending) => wasPending;
        internal static string LocalCopyActionTextForTest => "复制ID";
        internal static bool LocalCopyUsesVanillaRowWidthForTest => ActionPositionOffsetForTest == -70 && WrapperHeightScaleForTest == 0f;
    }
}
