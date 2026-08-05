using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using System;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// 在单机地图选择界面原生注入"多人联机"按钮（迁移自 LaunchP2PHostManager v2.11.0）。
    ///
    /// 策略：废弃对 Provider.singleplayer 的暴力拦截（Prefix），让原版 PLAY 按钮保持纯净的单机启动。
    /// 改在 MenuPlaySingleplayerUI 构造函数 Postfix 中动态绘制 ISleekButton，
    /// 让玩家显式选择单机 (PLAY) 或 P2P 联机 (多人联机)。
    ///
    /// UI 布局：resetButton 在 Y=480，新按钮置 Y=520（同等宽度 200、高度 30、间隙 10）。
    /// </summary>
    [HarmonyPatch(typeof(MenuPlaySingleplayerUI), MethodType.Constructor)]
    public static class MenuPlaySingleplayerUIPatch
    {
        private const float MultiplayerButton_Y = 520f;
        private const float MultiplayerButton_Height = 30f;

        public static void Postfix()
        {
            try
            {
                // v0.2.3.23 P0-C4：INVALID 硬门控 - 不注入多人按钮
                //   审计报告-Codex §3 P0-Critical-4 要求：INVALID 时不注入按钮或注入 disabled 状态
                //   实现：INVALID 时完全不注入，从 UI 根本上阻止入口
                if (!SteamP2PFriendsPlugin.DiagnosticBuildValid)
                {
                    RoleLogger.Warn("[Shared]",
                        "[P2P-UI] DiagnosticBuildValid=false，不注入多人联机按钮（P0-C4 INVALID 硬门控）");
                    return;
                }

                FieldInfo containerFi = AccessTools.Field(typeof(MenuPlaySingleplayerUI), "container");
                if (containerFi == null)
                {
                    RoleLogger.Error("[Shared]", "[P2P-UI] 无法反射 MenuPlaySingleplayerUI.container 字段。");
                    return;
                }

                object containerObj = containerFi.GetValue(null);
                if (!(containerObj is ISleekElement container))
                {
                    RoleLogger.Error("[Shared]", "[P2P-UI] container 为 null 或非 ISleekElement。");
                    return;
                }

                ISleekButton multiplayerButton = Glazier.Get().CreateButton();
                multiplayerButton.PositionOffset_X = -305f;
                multiplayerButton.PositionOffset_Y = MultiplayerButton_Y;
                multiplayerButton.PositionScale_X = 0.5f;
                multiplayerButton.SizeOffset_X = 200f;
                multiplayerButton.SizeOffset_Y = MultiplayerButton_Height;
                multiplayerButton.Text = "多人联机";
                multiplayerButton.TooltipText = "以当前单人存档直接拉起多人服务器，并在 Steam 上发布联机凭证";
                multiplayerButton.OnClicked += OnClickedMultiplayerButton;
                container.AddChild(multiplayerButton);

                RoleLogger.Info("[Shared]",
                    $"[P2P-UI] 多人联机按钮已注入 MenuPlaySingleplayerUI (Y={MultiplayerButton_Y})");
            }
            catch (Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P2P-UI] 注入按钮失败: {ex}");
            }
        }

        private static void OnClickedMultiplayerButton(ISleekElement button)
        {
            try
            {
                // v0.2.3.23 P0-C4：按钮点击处理器二次检查 DiagnosticBuildValid
                //   即使按钮已注入，状态可能在运行时变为 INVALID（理论上不可能，但防御性检查）
                if (!SteamP2PFriendsPlugin.DiagnosticBuildValid)
                {
                    RoleLogger.Error("[Shared]",
                        "[P2P-UI] DiagnosticBuildValid=false，拒绝启动 P2P 服务器（P0-C4 二次硬门控）");
                    try { MenuUI.alert("SteamP2PFriends 自检未通过，多人联机功能不可用。请查看日志。"); } catch { }
                    return;
                }

                FieldInfo selectedLevelFi = AccessTools.Field(typeof(MenuPlaySingleplayerUI), "selectedLevel");
                LevelInfo selectedLevel = selectedLevelFi?.GetValue(null) as LevelInfo;
                if (selectedLevel == null)
                {
                    RoleLogger.Warn("[Shared]", "[P2P-UI] 未选择地图，无法启动 P2P 服务器。");
                    return;
                }

                string mapName = selectedLevel.name;
                if (string.IsNullOrEmpty(mapName))
                {
                    RoleLogger.Warn("[Shared]", "[P2P-UI] 选中地图名称为空。");
                    return;
                }

                Provider.map = mapName;
                string serverName = SteamP2PFriendsPlugin.ServerName?.Value ?? "P2P Co-op";
                byte maxPlayers = SteamP2PFriendsPlugin.MaxPlayers?.Value ?? 4;
                EGameMode mode = PlaySettings.singleplayerMode;
                bool cheats = PlaySettings.singleplayerCheats;

                // v0.2.2 T-1 修复：P2P-only 模式不再询问 GSLT，直接启动 P2P 服务器。
                // 原因：
                //   1. offlineOnly=true 已跳过 SteamGameServer 票据校验，GSLT 不再解决任何认证问题
                //   2. P2P 模式固定 SteamUser identity 路线，GSLT 会触发 Internet 可见性 -> SDR 路由尝试（listen server 不可行）
                //   3. GSLT_Login_Token 配置项保留仅为向后兼容旧 cfg，实际不参与运行
                RoleLogger.Info("[Shared]",
                    $"[P2P-UI] 多人联机按钮点击：直接启动 P2P 服务器（SteamUser identity，GSLT 已禁用）" +
                    $" (map={mapName}, mode={mode}, cheats={cheats}, maxPlayers={maxPlayers})");

                HostManager.StartP2PServer(mapName, serverName, maxPlayers, mode, cheats);
            }
            catch (Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P2P-UI] 启动 P2P 服务器失败: {ex}");
            }
        }
    }
}
