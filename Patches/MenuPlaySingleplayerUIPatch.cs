using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using SteamP2PFriends.UI;
using System;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    ///
    /// 蓝图 v2 §4.6：本 patch 只负责打开原生角色菜单，不直接启动 host。
    /// 启动逻辑由 P2PNativeMenuUI.TryStartHost 承接，包含地图引用刷新和依赖复验。
    /// </summary>
    [HarmonyPatch(typeof(MenuPlaySingleplayerUI), MethodType.Constructor)]
    public static class MenuPlaySingleplayerUIPatch
    {
        private const float MultiplayerButton_Y = 520f;
        private const float MultiplayerButton_Height = 30f;
        private static ISleekElement _boundContainer;
        private static ISleekButton _multiplayerButton;

        public static void Postfix()
        {
            EnsureMultiplayerButton();
        }

        /// <summary>
        /// May run from the menu constructor or from the Route B game-thread completion path.
        /// It is idempotent for the current native menu container and removes stale instances
        /// before rebinding after a menu rebuild.
        /// </summary>
        internal static void EnsureMultiplayerButton()
        {
            try
            {
                if (!SteamP2PFriendsPlugin.IsP2PEntryReady)
                {
                    DestroyMultiplayerButton();
                    return;
                }

                ThreadUtil.assertIsGameThread();

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

                if (ReferenceEquals(_boundContainer, container) && _multiplayerButton != null)
                    return;

                DestroyMultiplayerButton();
                _multiplayerButton = Glazier.Get().CreateButton();
                _multiplayerButton.PositionOffset_X = -305f;
                _multiplayerButton.PositionOffset_Y = MultiplayerButton_Y;
                _multiplayerButton.PositionScale_X = 0.5f;
                _multiplayerButton.SizeOffset_X = 200f;
                _multiplayerButton.SizeOffset_Y = MultiplayerButton_Height;
                _multiplayerButton.Text = "多人联机";
                _multiplayerButton.TooltipText = "打开 P2P 多人联机菜单：选择作为房主或客机";
                _multiplayerButton.OnClicked += OnClickedMultiplayerButton;
                container.AddChild(_multiplayerButton);
                _boundContainer = container;

                RoleLogger.Info("[Shared]",
                    $"[P2P-UI] 多人联机按钮已注入 MenuPlaySingleplayerUI (Y={MultiplayerButton_Y})");
            }
            catch (Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P2P-UI] 注入按钮失败: {ex}");
            }
        }

        internal static void DestroyMultiplayerButton()
        {
            if (_boundContainer != null && _multiplayerButton != null)
            {
                try { _boundContainer.RemoveChild(_multiplayerButton); }
                catch (Exception ex) { RoleLogger.Warn("[Shared]", "[P2P-UI] 移除多人联机按钮失败: " + ex.GetType().Name); }
            }
            _multiplayerButton = null;
            _boundContainer = null;
        }

        private static void OnClickedMultiplayerButton(ISleekElement button)
        {
            try
            {
                if (!SteamP2PFriendsPlugin.IsP2PEntryReady)
                {
                    RoleLogger.Error("[Shared]",
                        "[P2P-UI] Route B 未就绪，拒绝打开 P2P 菜单（硬门控）");
                    try { MenuUI.alert("SteamP2PFriends 尚未完成联机初始化，请稍后再试并查看日志。"); } catch { }
                    return;
                }

                // 蓝图 v2 §4.6：只负责打开角色菜单；启动逻辑由 P2PNativeMenuUI.TryStartHost 承接
                FieldInfo selectedLevelFi = AccessTools.Field(typeof(MenuPlaySingleplayerUI), "selectedLevel");
                LevelInfo selectedLevel = selectedLevelFi?.GetValue(null) as LevelInfo;
                if (selectedLevel == null)
                {
                    RoleLogger.Warn("[Shared]", "[P2P-UI] 未选择地图，无法打开 P2P 菜单。");
                    try { MenuUI.alert("请先在列表中选择一个地图。"); } catch { }
                    return;
                }

                RoleLogger.Info("[Shared]",
                    $"[P2P-UI] 多人联机按钮点击：打开原生角色菜单 (selectedLevel={selectedLevel.name})");

                P2PNativeMenuUI.OpenRoleMenu(selectedLevel);
            }
            catch (Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P2P-UI] 打开 P2P 菜单失败: {ex}");
            }
        }
    }
}
