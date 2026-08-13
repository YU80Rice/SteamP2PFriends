using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Client;
using SteamP2PFriends.Shared;
using SteamP2PFriends.UI;
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

            // Priority 1: individual SteamID -> P2P (unaffected by the DNS toggle).
            if (route == UnifiedJoinAddressKind.SteamP2P)
            {
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

            ISleekUInt16Field portField = GetStaticField<ISleekUInt16Field>("portField");
            ISleekField passwordField = GetStaticField<ISleekField>("passwordField");
            ushort portValue = portField == null ? (ushort)0 : portField.Value;
            string password = passwordField == null ? string.Empty : passwordField.Text;

            // Priority 2: numeric IPv4 -> synchronous single-port Direct-IP.
            if (UnifiedJoinAddressClassifier.TryBuildDirectIpEndpoint(raw, portValue,
                out Unturned.SystemEx.IPv4Address address, out ushort queryPort,
                out ushort connectionPort))
            {
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

                var parameters = new ServerConnectParameters(address, queryPort, connectionPort, password);
                // Stage 9-2: single-port semantics. The entered port is both query and connection.
                RoleLogger.Info("[Client]", "[DirectIP-SinglePort] connect " +
                    "address=" + address + " sharedPort=" + connectionPort +
                    " queryPort=" + queryPort + " connectionPort=" + connectionPort);
                Provider.connect(parameters, null, null);
                return false;
            }

            // Priority 3: only when the player explicitly checks the DNS direct-connect toggle.
            if (ExplicitDnsDirectIpModeUI.IsEnabled)
            {
                if (!UnifiedJoinAddressClassifier.TryBuildExplicitDnsEndpoint(
                        raw, portValue, out string host, out ushort sharedPort))
                {
                    ShowAlertBestEffort("域名或端口无效，未发起连接。");
                    return false;
                }

                if (!SteamP2PFriendsPlugin.DiagnosticBuildValid)
                {
                    ShowAlertBestEffort("SteamP2PFriends 自检未通过，域名直连已禁用。");
                    return false;
                }

                if (Provider.isConnected)
                {
                    ShowAlertBestEffort("已连接到服务器，请先断开再发起域名直连。");
                    return false;
                }

                bool started = ExplicitDnsDirectIpService.Instance.TryBegin(host, sharedPort, password);
                if (!started)
                {
                    ShowAlertBestEffort("域名解析任务繁忙或当前状态不允许连接。");
                }
                else
                {
                    // v2 指令 H: shape-only log, never the full domain or a substring of it.
                    RoleLogger.Info("[Client]",
                        "[DirectIP-DNS] begin hostShape=" +
                        SteamP2PFriends.Client.ExplicitDnsDirectIpController.DescribeHostForLog(host) +
                        " sharedPort=" + sharedPort);
                }
                return false;
            }

            // Priority 4: everything else (DNS with toggle off, URLs, vanilla game-server codes)
            // remains entirely vanilla-owned.
            return true;
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

            // Stage 9-3: lazily create the explicit DNS direct-connect toggle in the vanilla
            // MenuPlayConnectUI layout (reuses this existing patch, no new constructor patch).
            ExplicitDnsDirectIpModeUI.EnsureCreated();

            ISleekField hostField = MenuPlayConnectP2PRoutePatch.GetStaticField<ISleekField>("hostField");
            if (hostField == null) return;

            // SteamID P2P hint.
            if (UnifiedJoinAddressClassifier.Classify(hostField.Text, out _) == UnifiedJoinAddressKind.SteamP2P)
            {
                ISleekBox info = MenuPlayConnectP2PRoutePatch.GetStaticField<ISleekBox>("serverCodeInfoBox");
                ISleekUInt16Field port = MenuPlayConnectP2PRoutePatch.GetStaticField<ISleekUInt16Field>("portField");
                if (info == null || port == null) return;

                info.Text = "Steam P2P 房主 ID（插件联机）";
                info.TooltipText = "已识别为个人 Steam ID，将使用 Steam P2P 连接；授权仍以 Steam ID 为准。";
                info.IsVisible = true;
                port.IsVisible = false;
                return;
            }

            ISleekUInt16Field ipPort = MenuPlayConnectP2PRoutePatch.GetStaticField<ISleekUInt16Field>("portField");
            if (ipPort == null) return;

            // Stage 9-3: explicit DNS mode hint when the toggle is enabled and a legal domain is present.
            if (ExplicitDnsDirectIpModeUI.IsEnabled &&
                UnifiedJoinAddressClassifier.TryBuildExplicitDnsEndpoint(
                    hostField.Text, ipPort.Value, out _, out _))
            {
                ISleekBox dnsInfo = MenuPlayConnectP2PRoutePatch.GetStaticField<ISleekBox>("serverCodeInfoBox");
                if (dnsInfo != null)
                {
                    dnsInfo.Text = "插件域名直连（FRP）";
                    dnsInfo.TooltipText = "将把域名解析为 IPv4 并直接连接填写的 UDP 端口；不执行原版 U3DS/A2S 查询。";
                    dnsInfo.IsVisible = true;
                }
                return;
            }

            // Stage 9-2: single-port Direct-IP hint for numeric IPv4. Never auto-rewrite the port.
            if (!UnifiedJoinAddressClassifier.TryBuildDirectIpEndpoint(
                hostField.Text, ipPort.Value, out _, out _, out _))
            {
                return;
            }

            ISleekBox ipInfo = MenuPlayConnectP2PRoutePatch.GetStaticField<ISleekBox>("serverCodeInfoBox");
            if (ipInfo == null) return;

            ipInfo.Text = "插件 Direct-IP（单端口 UDP）";
            ipInfo.TooltipText = "端口请填实际可达的 UDP 端口；局域网/Radmin 默认为 27016；SakuraFRP 填隧道的远端 UDP 端口。";
            ipInfo.IsVisible = true;
        }
    }
}
