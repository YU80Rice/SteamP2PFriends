using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Client;
using SteamP2PFriends.Shared;
using Steamworks;
using System;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// Routes individual Steam IDs to P2P and numeric IPv4 endpoints to the query-less
    /// SteamUser listen-host connection path. DNS, URLs and game-server codes remain vanilla.
    /// </summary>
    [HarmonyPatch]
    internal static class MenuPlayConnectP2PRoutePatch
    {
        internal static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(MenuPlayConnectUI), "onClickedConnectButton",
                new[] { typeof(ISleekElement) });
        }

        internal static bool Prefix()
        {
            ThreadUtil.assertIsGameThread();

            ISleekField hostField = GetStaticField<ISleekField>("hostField");
            string raw = hostField == null ? string.Empty : hostField.Text;
            UnifiedJoinAddressKind route = UnifiedJoinAddressClassifier.Classify(raw, out ulong targetId);
            if (route != UnifiedJoinAddressKind.SteamP2P)
            {
                ISleekUInt16Field portField = GetStaticField<ISleekUInt16Field>("portField");
                ISleekField passwordField = GetStaticField<ISleekField>("passwordField");
                ushort portValue = portField == null ? (ushort)0 : portField.Value;
                if (!UnifiedJoinAddressClassifier.TryBuildDirectIpEndpoint(raw, portValue,
                    out Unturned.SystemEx.IPv4Address address, out ushort queryPort,
                    out ushort connectionPort))
                {
                    // DNS names, URLs and vanilla game-server codes remain entirely vanilla-owned.
                    return true;
                }

                if (!SteamP2PFriendsPlugin.DiagnosticBuildValid)
                {
                    ShowAlertBestEffort("SteamP2PFriends self-check failed. Direct-IP is disabled.");
                    return false;
                }

                if (Provider.isConnected)
                {
                    ShowAlertBestEffort("Already connected. Disconnect before starting Direct-IP.");
                    return false;
                }

                string password = passwordField == null ? string.Empty : passwordField.Text;
                var parameters = new ServerConnectParameters(address, queryPort, connectionPort, password);
                RoleLogger.Info("[Client]", "[DirectIP-SteamUser] query-less connect " +
                    "address=" + address + " queryPort=" + queryPort +
                    " connectionPort=" + connectionPort);
                Provider.connect(parameters, null, null);
                return false;
            }

            if (!SteamP2PFriendsPlugin.DiagnosticBuildValid)
            {
                ShowAlertBestEffort("SteamP2PFriends 自检未通过，Steam P2P 连接已禁用。");
                return false;
            }

            CSteamID local = SteamUser.GetSteamID();
            if (local.IsValid() && local.m_SteamID == targetId)
            {
                ShowAlertBestEffort("不能连接到自己的 Steam P2P 房间。");
                return false;
            }

            bool started = P2PJoinManager.TryConnectToHost(targetId);
            RoleLogger.Info("[Client]", "[UnifiedConnect] route=SteamP2P target=" + targetId +
                " started=" + started);
            if (!started)
            {
                ShowAlertBestEffort("当前状态无法发起 Steam P2P 连接，请稍后重试。");
            }
            return false;
        }

        internal static T GetStaticField<T>(string name) where T : class
        {
            FieldInfo field = AccessTools.Field(typeof(MenuPlayConnectUI), name);
            return field == null ? null : field.GetValue(null) as T;
        }

        private static void ShowAlertBestEffort(string message)
        {
            try { MenuUI.alert(message); }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Client]", "[UnifiedConnect] alert unavailable: " + ex.Message);
            }
        }
    }

    /// <summary>Projects Steam P2P recognition into the existing vanilla route hint.</summary>
    [HarmonyPatch]
    internal static class MenuPlayConnectP2PIndicatorPatch
    {
        internal static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(MenuPlayConnectUI), "RefreshServerCodeInfo");
        }

        internal static void Postfix()
        {
            if (!P2PClientUiEnvironment.CanTouchClientUi()) return;

            ISleekField hostField = MenuPlayConnectP2PRoutePatch.GetStaticField<ISleekField>("hostField");
            if (hostField == null ||
                UnifiedJoinAddressClassifier.Classify(hostField.Text, out _) != UnifiedJoinAddressKind.SteamP2P)
            {
                return;
            }

            ISleekBox info = MenuPlayConnectP2PRoutePatch.GetStaticField<ISleekBox>("serverCodeInfoBox");
            ISleekUInt16Field port = MenuPlayConnectP2PRoutePatch.GetStaticField<ISleekUInt16Field>("portField");
            if (info == null || port == null) return;

            info.Text = "Steam P2P 房主 ID（插件联机）";
            info.TooltipText = "已识别为个人 Steam ID，将使用 Steam P2P 连接；授权仍以 Steam ID 为准。";
            info.IsVisible = true;
            port.IsVisible = false;
        }
    }
}
