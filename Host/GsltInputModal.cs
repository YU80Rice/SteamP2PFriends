using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using System;

namespace SteamP2PFriends.Host
{
    /// <summary>
    ///
    /// 调用时机：用户点击"多人联机"按钮，且 BepInEx 配置中 GSLT_Login_Token 为空时。
    /// 双步流程：
    ///   第一步（输入面板）：32 位 GSLT Token 输入框 + 【确认并保存】 + 【跳过（匿名启动）】
    ///   第二步（风险警告）：警告文本 + 【取消】 + 【确认匿名启动】
    ///
    /// </summary>
    public static class GsltInputModal
    {
        private static bool _active;
        private static string _tokenInput = "";
        private static bool _showWarningStep;
        private static string _pendingMapName;
        private static string _pendingServerName;
        private static byte _pendingMaxPlayers;
        private static EGameMode _pendingMode;
        private static bool _pendingCheats;

        public static void Show(ISleekElement parent, string mapName, string serverName,
            byte maxPlayers, EGameMode mode, bool cheats)
        {
            if (_active)
            {
                RoleLogger.Warn("[Shared]", "[P2P-Modal] GSLT 模态已存在，忽略重复 Show 调用");
                return;
            }

            _pendingMapName = mapName;
            _pendingServerName = serverName;
            _pendingMaxPlayers = maxPlayers;
            _pendingMode = mode;
            _pendingCheats = cheats;
            _tokenInput = "";
            _showWarningStep = false;
            _active = true;

            RoleLogger.Info("[Shared]", "[P2P-Modal] GSLT 输入模态已激活（IMGUI 渲染）");
        }

        /// <summary>
        /// Plugin.OnGUI 每帧调用。
        /// </summary>
        public static void OnGUI()
        {
            if (!_active) return;

            if (!_showWarningStep)
            {
                DrawInputStep();
            }
            else
            {
                DrawWarningStep();
            }
        }

        private static void DrawInputStep()
        {
            const int W = 500, H = 220;
            int x = (UnityEngine.Screen.width - W) / 2;
            int y = (UnityEngine.Screen.height - H) / 2;

            UnityEngine.GUI.Box(new UnityEngine.Rect(x, y, W, H), "GSLT Token 输入（可选）");

            UnityEngine.GUI.Label(new UnityEngine.Rect(x + 20, y + 40, W - 40, 40),
                "请输入 32 位 GSLT Token（留空则匿名启动，仅 LAN/P2P 模式可用）：");
            _tokenInput = UnityEngine.GUI.TextField(new UnityEngine.Rect(x + 20, y + 80, W - 40, 30), _tokenInput);

            if (UnityEngine.GUI.Button(new UnityEngine.Rect(x + 20, y + 130, 150, 40), "确认并保存"))
            {
                OnConfirmWithToken();
            }
            if (UnityEngine.GUI.Button(new UnityEngine.Rect(x + 180, y + 130, 150, 40), "跳过（匿名）"))
            {
                _showWarningStep = true;
            }
            if (UnityEngine.GUI.Button(new UnityEngine.Rect(x + 340, y + 130, 140, 40), "取消"))
            {
                CloseModal();
            }
        }

        private static void DrawWarningStep()
        {
            const int W = 500, H = 240;
            int x = (UnityEngine.Screen.width - W) / 2;
            int y = (UnityEngine.Screen.height - H) / 2;

            UnityEngine.GUI.Box(new UnityEngine.Rect(x, y, W, H), "匿名启动风险警告");

            UnityEngine.GUI.Label(new UnityEngine.Rect(x + 20, y + 40, W - 40, 100),
                "未配置 GSLT 时：\n" +
                "  - 不会出现在 Internet 服务器列表\n" +
                "  - 好友列表/服务器代码直连仍可工作\n" +
                "  - 仅 LAN 模式（同局域网可加入）\n\n" +
                "如需公网联机，请取消并输入 32 位 GSLT Token。");

            if (UnityEngine.GUI.Button(new UnityEngine.Rect(x + 20, y + 160, 200, 50), "取消"))
            {
                CloseModal();
            }
            if (UnityEngine.GUI.Button(new UnityEngine.Rect(x + 280, y + 160, 200, 50), "确认匿名启动"))
            {
                OnConfirmAnonymous();
            }
        }

        private static void OnConfirmWithToken()
        {
            string token = _tokenInput?.Trim() ?? "";
            if (string.IsNullOrEmpty(token))
            {
                _showWarningStep = true;
                return;
            }
            if (token.Length != 32)
            {
                RoleLogger.Warn("[Shared]", $"[P2P-Modal] GSLT Token 长度异常（{token.Length} 位，应为 32 位）");
                _showWarningStep = true;
                return;
            }

            try
            {
                SteamP2PFriendsPlugin.GSLT_Login_Token.Value = token;
                SteamP2PFriendsPlugin.Instance.Config.Save();
                RoleLogger.Info("[Shared]", "[P2P-Modal] GSLT Token 已保存到配置");
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Shared]", $"[P2P-Modal] 保存 GSLT Token 失败: {ex.Message}");
            }

            CloseModal();
            HostManager.StartP2PServer(_pendingMapName, _pendingServerName, _pendingMaxPlayers, _pendingMode, _pendingCheats);
        }

        private static void OnConfirmAnonymous()
        {
            RoleLogger.Info("[Shared]", "[P2P-Modal] 匿名启动确认（LAN/P2P 模式）");
            CloseModal();
            HostManager.StartP2PServer(_pendingMapName, _pendingServerName, _pendingMaxPlayers, _pendingMode, _pendingCheats);
        }

        private static void CloseModal()
        {
            _active = false;
            _tokenInput = "";
            _showWarningStep = false;
        }
    }
}
