using SDG.Unturned;
using SteamP2PFriends.Client;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using SteamP2PFriends.Shared.Enums;
using Steamworks;
using System;
using UnityEngine;

namespace SteamP2PFriends.Client
{
    /// <summary>
    /// 客机 SteamID 输入 UI（IMGUI 实现，迁移自 LaunchP2PHostManager v2.11.0）。
    ///
    /// 客机流程：
    ///   1. 主菜单点击"通过 SteamID 加入"
    ///   2. 输入房主 SteamUser SteamID（17 位数字）
    ///   3. 点击 Connect
    ///   4. 调 P2PJoinManager.TryConnectToHost(steamId)
    ///   5. P2PJoinManager 调 vanilla Provider.connect(new ServerConnectParameters(new CSteamID(steamId), ""), null, null)
    ///   6. vanilla ClientTransport_SteamNetworkingSockets.Connect 检测 address.IsZero=true
    ///      -> 走 ConnectP2P(steamId) 路径（SteamUser identity）
    ///   7. 房主的 SteamUser P2P listen socket 收到连接 -> 连接建立
    ///   8. P2PJoinManager.Tick 检测 Provider.isConnected=true -> 状态切换为 Connected
    /// </summary>
    public static class SteamIdInputModal
    {
        private static bool _showButton = true;
        private static bool _showInput = false;
        private static string _steamIdInput = "";
        private static string _errorMsg = "";

        /// <summary>
        /// Plugin.OnGUI 每帧调用。
        /// v0.2.3.23 P0-C4：DiagnosticBuildValid=false 时不渲染任何 UI（审计报告-Codex §3 P0-Critical-4）。
        /// </summary>
        public static void OnGUI()
        {
            // v0.2.3.23 P0-C4：INVALID 硬门控 - 客机 UI 入口
            //   审计报告-Codex §3 P0-Critical-4 要求：客机公开连接入口同样检查
            //   实现：DiagnosticBuildValid=false 时完全不渲染 UI，从视觉上阻止客机入口
            if (!SteamP2PFriendsPlugin.DiagnosticBuildValid)
            {
                return;
            }

            // 房主 P2P 服务器活动时不显示（房主不需要这个 UI）
            if (HostManager.IsP2PServerActive) return;
            // 已在游戏中（已连接服务器）时不显示
            if (Provider.isServer || Provider.isClient) return;

            if (_showInput)
            {
                DrawInputDialog();
            }
            else if (_showButton)
            {
                DrawFloatingButton();
            }
        }

        private static void DrawFloatingButton()
        {
            float x = Screen.width - 230;
            float y = 60;
            if (GUI.Button(new Rect(x, y, 210, 32), "通过 SteamID 加入 (P2P)"))
            {
                _showInput = true;
                _errorMsg = "";
            }
        }

        private static void DrawInputDialog()
        {
            const float boxW = 420;
            const float boxH = 220;
            float boxX = (Screen.width - boxW) / 2f;
            float boxY = (Screen.height - boxH) / 2f;

            // 半透明黑色背景
            GUI.color = new Color(0, 0, 0, 0.85f);
            GUI.DrawTexture(new Rect(boxX, boxY, boxW, boxH), Texture2D.whiteTexture);
            GUI.color = Color.white;

            // 标题
            var titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            titleStyle.normal.textColor = Color.yellow;
            GUI.Label(new Rect(boxX + 20, boxY + 15, boxW - 40, 30), "通过 SteamID 加入 P2P 服务器", titleStyle);

            // 说明
            var descStyle = new GUIStyle(GUI.skin.label) { fontSize = 13 };
            descStyle.normal.textColor = Color.white;
            GUI.Label(new Rect(boxX + 20, boxY + 50, boxW - 40, 20),
                "请输入房主的 SteamUser SteamID（17 位数字）", descStyle);
            GUI.Label(new Rect(boxX + 20, boxY + 70, boxW - 40, 20),
                "SteamID 可从房主屏幕左上角复制", descStyle);

            // 当前状态（retry 状态机可见性）
            if (P2PJoinManager.State != EJoinState.Idle)
            {
                var stateStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
                stateStyle.normal.textColor = Color.cyan;
                GUI.Label(new Rect(boxX + 20, boxY + 90, boxW - 40, 20),
                    $"当前状态: {P2PJoinManager.State}", stateStyle);
            }

            // 输入框
            _steamIdInput = GUI.TextField(new Rect(boxX + 20, boxY + 115, boxW - 40, 30), _steamIdInput, 20);

            // 错误信息
            if (!string.IsNullOrEmpty(_errorMsg))
            {
                var errStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
                errStyle.normal.textColor = Color.red;
                GUI.Label(new Rect(boxX + 20, boxY + 150, boxW - 40, 20), _errorMsg, errStyle);
            }

            // 按钮
            if (GUI.Button(new Rect(boxX + 20, boxY + 175, 180, 32), "Connect"))
            {
                OnClickConnect();
            }
            if (GUI.Button(new Rect(boxX + 220, boxY + 175, 180, 32), "取消"))
            {
                _showInput = false;
                _showButton = true;
                _errorMsg = "";
            }
        }

        private static void OnClickConnect()
        {
            // v0.2.3.23 P0-C4：INVALID 硬门控 - 点击 Connect 二次检查
            //   即使 UI 已经渲染（理论不可能），点击时再次校验 DiagnosticBuildValid
            if (!SteamP2PFriendsPlugin.DiagnosticBuildValid)
            {
                _errorMsg = "SteamP2PFriends 自检未通过，客机连接被拒绝。请查看日志。";
                RoleLogger.Error("[Client]",
                    "[SteamIdInputModal] OnClickConnect 拒绝执行：DiagnosticBuildValid=false（P0-C4 二次硬门控）");
                return;
            }

            string input = _steamIdInput?.Trim() ?? "";
            if (string.IsNullOrEmpty(input))
            {
                _errorMsg = "请输入 SteamID";
                return;
            }

            if (!ulong.TryParse(input, out ulong steamId) || steamId == 0)
            {
                _errorMsg = "SteamID 格式无效（应为 17 位数字）";
                return;
            }

            if (steamId == SteamUser.GetSteamID().m_SteamID)
            {
                _errorMsg = "不能加入自己的房间";
                return;
            }

            _errorMsg = "";
            _showInput = false;
            _showButton = true;

            P2PJoinManager.TryConnectToHost(steamId);
        }
    }
}
