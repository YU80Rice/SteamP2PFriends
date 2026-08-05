using SDG.Unturned;
using SteamP2PFriends.Shared;
using Steamworks;
using UnityEngine;

namespace SteamP2PFriends.Host
{
    /// <summary>
    /// 房主 SteamUser SteamID 显示服务（迁移自 LaunchP2PHostManager v2.11.0）。
    ///
    /// v2.10.0 改用 SteamUser identity 后应显示房主真实 SteamUser SteamID（不是 GameServer AnonID）。
    /// Plugin.Update 每帧调 Tick()（占位），Plugin.OnGUI 每帧调 OnGUI() 显示。
    /// </summary>
    public static class HostSteamIdDisplayService
    {
        private static bool _active;
        private static string _steamIdStr = "";
        private static string _steamIdFormatted = "";
        private static string _copyStatus = "";
        private static float _copyStatusUntil;

        public static void StartDisplay()
        {
            try
            {
                CSteamID userSteamId = SteamUser.GetSteamID();
                if (!userSteamId.IsValid())
                {
                    userSteamId = Provider.user;
                }
                if (userSteamId.IsValid())
                {
                    _steamIdStr = userSteamId.m_SteamID.ToString();
                    _steamIdFormatted = FormatWithDashes(_steamIdStr);
                    _active = true;
                    RoleLogger.Info("[Host]",
                        $"[P2P-SteamUser] !!! 房主 SteamUser SteamID = {_steamIdFormatted} (raw={_steamIdStr}) !!!");
                    RoleLogger.Info("[Host]", "[P2P-SteamUser] In-game SteamID copy overlay is ready.");
                }
                else
                {
                    RoleLogger.Warn("[Host]", "[P2P-SteamUser] Provider.user 无效，无法显示 SteamID");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Host]", $"StartDisplay 异常: {ex.Message}");
            }
        }

        public static void StopDisplay()
        {
            _active = false;
            _steamIdStr = "";
            _steamIdFormatted = "";
            _copyStatus = "";
            _copyStatusUntil = 0f;
        }

        public static void Tick()
        {
            // 占位：未来可加心跳/状态刷新逻辑
        }

        public static void OnGUI()
        {
            // The overlay is only available to the active P2P listen host.
            if (!HostManager.IsP2PHostMode) return;

            // Start on the first frame after the host has entered the world.
            if (!_active)
            {
                StartDisplay();
            }
            if (string.IsNullOrEmpty(_steamIdStr)) return;

            var idStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 22,
                fontStyle = FontStyle.Bold
            };
            idStyle.normal.textColor = Color.cyan;
            GUI.Label(new Rect(10, 10, 700, 32), $"\u623f\u4e3b SteamID: {_steamIdFormatted}", idStyle);

            if (GUI.Button(new Rect(10, 48, 220, 32), "\u590d\u5236\u623f\u4e3b SteamID"))
            {
                CopyToClipboard();
            }

            if (Time.unscaledTime < _copyStatusUntil)
            {
                var statusStyle = new GUIStyle(GUI.skin.label) { fontSize = 14 };
                statusStyle.normal.textColor = Color.green;
                GUI.Label(new Rect(240, 53, 320, 24), _copyStatus, statusStyle);
            }
        }

        private static void CopyToClipboard()
        {
            GUIUtility.systemCopyBuffer = _steamIdStr;
            _copyStatus = "\u5df2\u590d\u5236\u5230\u526a\u8d34\u677f";
            _copyStatusUntil = Time.unscaledTime + 2f;
            RoleLogger.Info("[Host]", "[P2P-SteamUser] Host copied SteamID from the in-game overlay.");
        }

        private static string FormatWithDashes(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            // 每 4 位插入一个空格，便于阅读
            var chars = new char[raw.Length + raw.Length / 4];
            int j = 0;
            for (int i = 0; i < raw.Length; i++)
            {
                if (i > 0 && i % 4 == 0)
                {
                    chars[j++] = ' ';
                }
                chars[j++] = raw[i];
            }
            return new string(chars, 0, j);
        }
    }
}
