using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// Route B P-key player-list decorator. Unturned's current P-key players tab lives in
    /// PlayerDashboardInformationUI, whose two row factories are patched manually because both
    /// are private static methods. Rows are recreated by the native list, so this class keeps no
    /// UI element references that can leak across a rebuild or disconnect.
    /// </summary>
    internal static class Patch_PlayerDashboardPlayersUI
    {
        private const int ButtonWidth = 64;
        private const int ButtonGap = 4;
        private const int PendingActionWidth = (ButtonWidth * 2) + ButtonGap;
        private const float ClickCooldownSeconds = 1.5f;
        private static readonly Dictionary<ulong, float> NextClickAt = new Dictionary<ulong, float>();

        internal static bool RegistrationValid { get; private set; }
        internal static int PendingActionWidthForTest => PendingActionWidth;
        internal static int SingleActionWidthForTest => ButtonWidth;
        internal static string LocalCopyActionTextForTest => "复制ID";
        internal static string RevokeActionTextForTest => "撤销允许";
        internal static int AllowButtonOffsetForTest => -PendingActionWidth;
        internal static int RejectButtonOffsetForTest => -ButtonWidth;
        internal static float WrapperHeightScaleForTest => 0f;

        internal static void RegisterManual(Harmony harmony)
        {
            RegistrationValid = false;
            Type type = typeof(PlayerDashboardInformationUI);
            MethodInfo normal = AccessTools.Method(type, "OnCreatePlayerEntry", new[] { typeof(SteamPlayer) });
            MethodInfo grouped = AccessTools.Method(type, "OnCreatePlayerEntryWithGrouping", new[] { typeof(SteamPlayer) });
            MethodInfo postfix = AccessTools.Method(typeof(Patch_PlayerDashboardPlayersUI), nameof(Postfix));
            if (normal == null || grouped == null || postfix == null)
            {
                RoleLogger.Error("[Shared]", "[P2P-Approval] P-key player row factories unresolved");
                return;
            }

            if (!HasOwnedPostfix(normal, postfix)) harmony.Patch(normal, postfix: new HarmonyMethod(postfix));
            if (!HasOwnedPostfix(grouped, postfix)) harmony.Patch(grouped, postfix: new HarmonyMethod(postfix));
            RegistrationValid = HasOwnedPostfix(normal, postfix) && HasOwnedPostfix(grouped, postfix);
            if (RegistrationValid)
                RoleLogger.Info("[Host]", "[P2P-Approval] P-key player list approval decorator registered");
            else
                RoleLogger.Error("[Shared]", "[P2P-Approval] P-key player row decorator registration failed");
        }

        internal static void Postfix(SteamPlayer __0, ref ISleekElement __result)
        {
            ISleekElement vanilla = __result;
            try
            {
                ThreadUtil.assertIsGameThread();
                if (IsLocalHost(__0, out CSteamID localHost))
                {
                    __result = BuildSingleActionRow(vanilla, "复制ID", "复制房主 SteamID", -ButtonWidth,
                        button => OnCopyHostId(localHost, button));
                    return;
                }
                if (!TryGetRemoteAction(__0, out CSteamID target, out bool pending)) return;
                __result = pending
                    ? BuildPendingRow(vanilla, target)
                    : BuildSingleActionRow(vanilla, "撤销允许", "撤销白名单授权并安全断开该玩家", -ButtonWidth,
                        button => OnRevoke(target, button));
            }
            catch (Exception ex)
            {
                __result = vanilla;
                RoleLogger.Error("[Host]", "[P2P-Approval] P-key row decoration failed; vanilla row preserved: " + ex.GetType().Name);
            }
        }

        internal static void ForgetPlayer(ulong steamId)
        {
            if (steamId != 0UL) NextClickAt.Remove(steamId);
        }

        internal static void ResetForSession()
        {
            NextClickAt.Clear();
        }

        private static bool IsLocalHost(SteamPlayer player, out CSteamID localHost)
        {
            localHost = CSteamID.Nil;
            if (!HostManager.IsP2PHostMode || !Provider.isServer || ReferenceEquals(player, null) ||
                ReferenceEquals(player.playerID, null) || player.player == null || player.player.channel == null ||
                !player.player.channel.IsLocalPlayer)
                return false;
            localHost = Provider.user;
            return localHost.IsValid() && localHost.BIndividualAccount();
        }

        private static bool TryGetRemoteAction(SteamPlayer player, out CSteamID target, out bool pending)
        {
            target = CSteamID.Nil;
            pending = false;
            if (!HostManager.IsP2PHostMode || !Provider.isServer || !Provider.isWhitelisted) return false;
            if (ReferenceEquals(player, null) || ReferenceEquals(player.playerID, null) ||
                player.player == null || player.player.channel == null || player.player.channel.IsLocalPlayer)
                return false;
            target = player.playerID.steamID;
            if (target == CSteamID.Nil || !target.IsValid()) return false;
            pending = P2PApprovalManager.IsPending(target);
            return pending || P2PWhitelistService.ContainsForUi(target);
        }

        private static ISleekElement BuildPendingRow(ISleekElement vanilla, CSteamID target)
        {
            ISleekElement wrapper = Glazier.Get().CreateFrame();
            wrapper.SizeScale_X = 1f;
            wrapper.SizeScale_Y = 0f; // Native SleekList owns the row height.

            vanilla.SizeScale_X = 1f;
            vanilla.SizeOffset_X = -PendingActionWidth;
            vanilla.SizeScale_Y = 1f;
            wrapper.AddChild(vanilla);

            ISleekButton approve = CreateActionButton("允许", "批准此玩家并写入房主白名单", -PendingActionWidth);
            approve.OnClicked += button => OnApprove(target, approve, null);
            wrapper.AddChild(approve);

            ISleekButton reject = CreateActionButton("拒绝", "拒绝此玩家并安全断开连接", -ButtonWidth);
            reject.OnClicked += button => OnReject(target, approve, reject);
            wrapper.AddChild(reject);
            return wrapper;
        }

        private static ISleekElement BuildSingleActionRow(ISleekElement vanilla, string text, string tooltip,
            int offsetX, Action<ISleekButton> onClicked)
        {
            ISleekElement wrapper = Glazier.Get().CreateFrame();
            wrapper.SizeScale_X = 1f;
            wrapper.SizeScale_Y = 0f;
            vanilla.SizeScale_X = 1f;
            vanilla.SizeOffset_X = -ButtonWidth;
            vanilla.SizeScale_Y = 1f;
            wrapper.AddChild(vanilla);

            ISleekButton action = CreateActionButton(text, tooltip, offsetX);
            action.OnClicked += button => onClicked(action);
            wrapper.AddChild(action);
            return wrapper;
        }

        private static ISleekButton CreateActionButton(string text, string tooltip, int offsetX)
        {
            ISleekButton button = Glazier.Get().CreateButton();
            button.PositionScale_X = 1f;
            button.PositionOffset_X = offsetX;
            button.SizeOffset_X = ButtonWidth;
            button.SizeScale_Y = 1f;
            button.FontSize = ESleekFontSize.Small;
            button.Text = text;
            button.TooltipText = tooltip;
            return button;
        }

        private static bool IsClickAllowed(CSteamID target)
        {
            float now = Time.realtimeSinceStartup;
            if (NextClickAt.TryGetValue(target.m_SteamID, out float next) && now < next) return false;
            NextClickAt[target.m_SteamID] = now + ClickCooldownSeconds;
            return true;
        }

        private static void OnApprove(CSteamID target, ISleekButton approve, ISleekButton reject)
        {
            ThreadUtil.assertIsGameThread();
            if (!IsClickAllowed(target)) return;
            bool ok = P2PApprovalManager.ApprovePlayer(target, out string feedback);
            approve.Text = ok ? "已允许" : "失败";
            approve.IsClickable = !ok;
            if (reject != null) reject.IsClickable = !ok;
            RoleLogger.Info("[Host]", "[P2P-Approval] P-key approve click: steamId=" + target.m_SteamID +
                " ok=" + ok + " feedback=" + feedback);
        }

        private static void OnReject(CSteamID target, ISleekButton approve, ISleekButton reject)
        {
            ThreadUtil.assertIsGameThread();
            if (!IsClickAllowed(target)) return;
            bool ok = P2PApprovalManager.RejectPlayer(target, out string feedback);
            reject.Text = ok ? "已拒绝" : "失败";
            reject.IsClickable = !ok;
            approve.IsClickable = !ok;
            RoleLogger.Info("[Host]", "[P2P-Approval] P-key reject click: steamId=" + target.m_SteamID +
                " ok=" + ok + " feedback=" + feedback);
        }

        private static void OnRevoke(CSteamID target, ISleekButton button)
        {
            ThreadUtil.assertIsGameThread();
            if (!IsClickAllowed(target)) return;
            bool ok = P2PApprovalManager.RevokePlayer(target, out string feedback);
            button.Text = ok ? "已撤销" : "失败";
            button.IsClickable = !ok;
            RoleLogger.Info("[Host]", "[P2P-Approval] P-key revoke click: steamId=" + target.m_SteamID +
                " ok=" + ok + " feedback=" + feedback);
        }

        private static void OnCopyHostId(CSteamID localHost, ISleekButton button)
        {
            ThreadUtil.assertIsGameThread();
            try
            {
                GUIUtility.systemCopyBuffer = localHost.m_SteamID.ToString();
                button.Text = "已复制";
                button.TooltipText = "房主 SteamID 已复制。";
                RoleLogger.Info("[Host]", "[P2P-Approval] P-key host SteamID copied");
            }
            catch (Exception ex)
            {
                button.Text = "复制失败";
                RoleLogger.Warn("[Host]", "[P2P-Approval] P-key host SteamID copy failed: " + ex.GetType().Name);
            }
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
    }
}
