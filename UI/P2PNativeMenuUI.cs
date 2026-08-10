using SDG.Unturned;
using SteamP2PFriends.Client;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using Steamworks;
using System;
using UnityEngine;

namespace SteamP2PFriends.UI
{
    // =====================================================================
    // Stage 7-3 v3 原生 P2P 多级菜单（Codex 接管蓝图 v3 §3.4）
    // v3 P0-UI-U3DS-EAGER-01 + P0-UI-PLAYER-CONTAINER-LIFECYCLE-02 修复：
    //   - CanTouchClientUi 守卫（U3DS 不创建）
    //   - parent identity 生命周期（MenuUI.container 变更 -> Destroy + 重建）
    //   - ThreadUtil.assertIsGameThread 显式断言
    //   - 零 OnGUI；仅 Glazier/ISleek*/SleekFullscreenBox
    // =====================================================================

    internal static class P2PNativeMenuUI
    {
        private static ISleekElement _boundParent;
        private static SleekFullscreenBox _roleMenuContainer;
        private static SleekFullscreenBox _joinMenuContainer;
        private static bool _created;

        private static string _selectedMapName = "";
        private static string _steamIdInput = "";

        private static ISleekLabel _mySteamIdLabel;
        private static ISleekLabel _joinErrorLabel;
        private static ISleekLabel _copyStatusLabel;
        private static float _copyStatusUntil;
        private static ISleekField _steamIdField;
        private static ISleekLabel _selectedMapLabel;
        private static ISleekField _serverNameField;
        private static ISleekUInt8Field _maxPlayersField;
        private static ISleekToggle _cheatsToggle;
        private static ISleekToggle _pvpToggle;
        private static ISleekToggle _keepInventoryToggle;
        private static ISleekToggle _keepSkillsToggle;
        private static ISleekToggle _keepExperienceToggle;
        private static ISleekButton _modeButton;
        private static ISleekButton _roomCopySteamIdButton;
        private static string _sessionServerName = "P2P Co-op";
        private static byte _sessionMaxPlayers = 4;
        private static EGameMode _sessionMode = EGameMode.EASY;
        private static bool _sessionCheats = true;
        private static bool _sessionPvp;
        private static bool _sessionKeepInventory = true;
        private static bool _sessionKeepSkills = true;
        private static bool _sessionKeepExperience = true;

        // v3 测试 hook
        internal static bool _testBypassThreadAssert;
        internal static Func<ISleekElement> _testParentProvider;
        internal static bool IsCreatedForTest => _created;
        internal static ISleekElement BoundParentForTest => _boundParent;
        internal static string RoomCopyButtonTextForTest => "复制房主 SteamID";
        internal static string RoomCopyUsageForTest => "开始游戏 → 直连";

        // ===== 生命周期（蓝图 v3 §3.4）=====

        /// <summary>
        /// 蓝图 v3 §3.4：仅主线程；CanTouchClientUi 守卫 + parent identity。
        /// 菜单 UI 不需要 P2P host 守卫（菜单阶段房主未启动）。
        /// </summary>
        internal static void EnsureCreated()
        {
            if (!_testBypassThreadAssert)
            {
                ThreadUtil.assertIsGameThread();
            }

            if (!P2PClientUiEnvironment.CanTouchClientUi())
            {
                Destroy();
                return;
            }

            // 测试模式：跳过 MenuUI.container ECall + Glazier
            if (_testBypassGlazier)
            {
                ISleekElement testParent = _testParentProvider?.Invoke();
                if (testParent == null) { Destroy(); return; }
                if (!_created || !ReferenceEquals(_boundParent, testParent))
                {
                    Destroy();
                    _boundParent = testParent;
                    _created = true;
                }
                return;
            }

            ISleekElement current = _testParentProvider?.Invoke() ?? MenuUI.container;
            if (current == null)
            {
                // 容器已撤销时，旧视图不可再作为“可见且可取消”的依据。
                // 等待控制器会在同帧看到 EnsureApprovalWaitVisible=false 并 fail-closed 停止重试。
                if (_created || _waitBox != null)
                {
                    Destroy();
                }
                return;
            }

            if (_created && ReferenceEquals(_boundParent, current))
            {
                return;
            }

            Destroy();
            _boundParent = current;

            try
            {
                if (_testBypassGlazier) { _created = true; return; }
                _roleMenuContainer = CreateContainer();
                _boundParent.AddChild(_roleMenuContainer);
                BuildRoleMenu(_roleMenuContainer);

                // Stage 7-8: legacy standalone SteamID page is intentionally not created.
                // Joining is exclusively routed through vanilla MenuPlayConnectUI.

                _created = true;
                RoleLogger.Info("[Shared]", "[P2P-Menu] P2PNativeMenuUI 已创建并绑定 parent");
            }
            catch (Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P2P-Menu] EnsureCreated 失败（不阻断 P2P 内核）: {ex}");
                Destroy();
            }
        }

        /// <summary>
        /// 蓝图 v3 §3.4：从 _boundParent 解绑所有子项，清空字段。
        /// </summary>
        internal static void Destroy()
        {
            if (!_testBypassThreadAssert)
            {
                ThreadUtil.assertIsGameThread();
            }

            DetachApprovalWaitView();

            if (_boundParent != null)
            {
                TryRemoveChild(_boundParent, _roleMenuContainer);
                TryRemoveChild(_boundParent, _joinMenuContainer);
            }

            _roleMenuContainer = null;
            _joinMenuContainer = null;
            _boundParent = null;
            _selectedMapName = "";
            _steamIdInput = "";
            _mySteamIdLabel = null;
            _joinErrorLabel = null;
            _copyStatusLabel = null;
            _steamIdField = null;
            _selectedMapLabel = null;
            _serverNameField = null;
            _maxPlayersField = null;
            _cheatsToggle = null;
            _pvpToggle = null;
            _keepInventoryToggle = null;
            _keepSkillsToggle = null;
            _keepExperienceToggle = null;
            _modeButton = null;
            _roomCopySteamIdButton = null;
            _copyStatusUntil = 0f;
            _created = false;
        }

        private static void TryRemoveChild(ISleekElement parent, ISleekElement child)
        {
            if (parent == null || child == null) return;
            try { parent.RemoveChild(child); }
            catch (Exception ex) { RoleLogger.Warn("[Shared]", $"[P2P-Menu] RemoveChild 异常: {ex.Message}"); }
        }

        // ===== 导航 =====

        internal static void OpenRoleMenu(LevelInfo selectedLevel)
        {
            EnsureCreated();
            if (!_created)
            {
                RoleLogger.Warn("[Shared]", "[P2P-Menu] OpenRoleMenu 失败：菜单未创建");
                return;
            }

            _selectedMapName = selectedLevel?.name ?? "";
            _sessionServerName = SteamP2PFriendsPlugin.ServerName?.Value ?? "P2P Co-op";
            _sessionMaxPlayers = SteamP2PFriendsPlugin.MaxPlayers?.Value ?? 4;
            if (_sessionMaxPlayers < 2) _sessionMaxPlayers = 2;
            _sessionMode = NormalizePersistedMode(
                SteamP2PFriendsPlugin.LastRoomMode?.Value ?? PlaySettings.singleplayerMode);
            _sessionCheats = SteamP2PFriendsPlugin.LastRoomCheats?.Value ?? PlaySettings.singleplayerCheats;
            _sessionPvp = SteamP2PFriendsPlugin.LastRoomPvp?.Value ?? false;
            _sessionKeepInventory = SteamP2PFriendsPlugin.LastRoomKeepInventory?.Value ?? true;
            _sessionKeepSkills = SteamP2PFriendsPlugin.LastRoomKeepSkills?.Value ?? true;
            _sessionKeepExperience = SteamP2PFriendsPlugin.LastRoomKeepExperience?.Value ?? true;
            RefreshSessionSettingsView();

            try
            {
                MenuPlaySingleplayerUI.close();
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Shared]", $"[P2P-Menu] MenuPlaySingleplayerUI.close 异常: {ex.Message}");
            }

            try
            {
                if (_joinMenuContainer != null) _joinMenuContainer.AnimateOutOfView(0, 1);
                _roleMenuContainer?.AnimateIntoView();
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Shared]", $"[P2P-Menu] OpenRoleMenu animate 异常: {ex.Message}");
            }
        }

        internal static void OpenJoinMenu()
        {
            if (!_created) return;

            try
            {
                RefreshMySteamIdLabel();
                if (_joinErrorLabel != null) _joinErrorLabel.Text = "";
                _roleMenuContainer?.AnimateOutOfView(0, 1);
                _joinMenuContainer?.AnimateIntoView();
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Shared]", $"[P2P-Menu] OpenJoinMenu 异常: {ex.Message}");
            }
        }

        // ===== 容器与 UI 构建 =====

        private static SleekFullscreenBox CreateContainer()
        {
            var container = new SleekFullscreenBox();
            container.PositionOffset_X = 10;
            container.PositionOffset_Y = 10;
            container.PositionScale_Y = 1;
            container.SizeOffset_X = -20;
            container.SizeOffset_Y = -20;
            container.SizeScale_X = 1;
            container.SizeScale_Y = 1;
            return container;
        }

        private static void BuildRoleMenu(SleekFullscreenBox container)
        {
            ISleekLabel titleLabel = Glazier.Get().CreateLabel();
            titleLabel.PositionOffset_X = -200;
            titleLabel.PositionOffset_Y = 70;
            titleLabel.PositionScale_X = 0.5f;
            titleLabel.SizeOffset_X = 400;
            titleLabel.SizeOffset_Y = 40;
            titleLabel.FontSize = ESleekFontSize.Large;
            titleLabel.TextAlignment = TextAnchor.MiddleCenter;
            titleLabel.Text = "创建多人房间";
            container.AddChild(titleLabel);

            ISleekButton hostButton = Glazier.Get().CreateButton();
            hostButton.PositionOffset_X = -100;
            hostButton.PositionOffset_Y = 550;
            hostButton.PositionScale_X = 0.5f;
            hostButton.SizeOffset_X = 200;
            hostButton.SizeOffset_Y = 40;
            hostButton.FontSize = ESleekFontSize.Medium;
            hostButton.Text = "创建房间";
            hostButton.TooltipText = "按当前设置启动 Steam P2P 房间";
            hostButton.OnClicked += OnClickedHost;
            container.AddChild(hostButton);

            _roomCopySteamIdButton = Glazier.Get().CreateButton();
            _roomCopySteamIdButton.PositionOffset_X = -100;
            _roomCopySteamIdButton.PositionOffset_Y = 505;
            _roomCopySteamIdButton.PositionScale_X = 0.5f;
            _roomCopySteamIdButton.SizeOffset_X = 200;
            _roomCopySteamIdButton.SizeOffset_Y = 30;
            _roomCopySteamIdButton.FontSize = ESleekFontSize.Medium;
            _roomCopySteamIdButton.Text = "复制房主 SteamID";
            _roomCopySteamIdButton.TooltipText =
                "复制你的个人 SteamID。发送给客机后，客机在原版“开始游戏 → 直连”的顶部地址栏粘贴即可加入。";
            _roomCopySteamIdButton.OnClicked += OnClickedCopyRoomSteamId;
            container.AddChild(_roomCopySteamIdButton);

            ISleekButton backButton = Glazier.Get().CreateButton();
            backButton.PositionOffset_X = -100;
            backButton.PositionOffset_Y = 600;
            backButton.PositionScale_X = 0.5f;
            backButton.SizeOffset_X = 200;
            backButton.SizeOffset_Y = 30;
            backButton.FontSize = ESleekFontSize.Medium;
            backButton.Text = "返回";
            backButton.OnClicked += OnClickedBackFromRole;
            container.AddChild(backButton);

            _selectedMapLabel = Glazier.Get().CreateLabel();
            _selectedMapLabel.PositionOffset_X = -250;
            _selectedMapLabel.PositionOffset_Y = 120;
            _selectedMapLabel.PositionScale_X = 0.5f;
            _selectedMapLabel.SizeOffset_X = 500;
            _selectedMapLabel.SizeOffset_Y = 30;
            _selectedMapLabel.TextAlignment = TextAnchor.MiddleCenter;
            container.AddChild(_selectedMapLabel);

            _serverNameField = Glazier.Get().CreateStringField();
            _serverNameField.PositionOffset_X = -150;
            _serverNameField.PositionOffset_Y = 160;
            _serverNameField.PositionScale_X = 0.5f;
            _serverNameField.SizeOffset_X = 300;
            _serverNameField.SizeOffset_Y = 30;
            _serverNameField.MaxLength = 48;
            _serverNameField.AddLabel("房间名称:", ESleekSide.LEFT);
            _serverNameField.OnTextChanged += OnServerNameChanged;
            container.AddChild(_serverNameField);

            _maxPlayersField = Glazier.Get().CreateUInt8Field();
            _maxPlayersField.PositionOffset_X = -150;
            _maxPlayersField.PositionOffset_Y = 200;
            _maxPlayersField.PositionScale_X = 0.5f;
            _maxPlayersField.SizeOffset_X = 300;
            _maxPlayersField.SizeOffset_Y = 30;
            _maxPlayersField.AddLabel("最大玩家:", ESleekSide.LEFT);
            _maxPlayersField.OnValueChanged += OnMaxPlayersChanged;
            container.AddChild(_maxPlayersField);

            _modeButton = Glazier.Get().CreateButton();
            _modeButton.PositionOffset_X = -150;
            _modeButton.PositionOffset_Y = 240;
            _modeButton.PositionScale_X = 0.5f;
            _modeButton.SizeOffset_X = 300;
            _modeButton.SizeOffset_Y = 30;
            _modeButton.OnClicked += OnClickedCycleMode;
            container.AddChild(_modeButton);

            _cheatsToggle = Glazier.Get().CreateToggle();
            _cheatsToggle.PositionOffset_X = -150;
            _cheatsToggle.PositionOffset_Y = 280;
            _cheatsToggle.PositionScale_X = 0.5f;
            _cheatsToggle.SizeOffset_X = 30;
            _cheatsToggle.SizeOffset_Y = 30;
            _cheatsToggle.AddLabel("允许作弊指令", ESleekSide.RIGHT);
            _cheatsToggle.OnValueChanged += OnCheatsChanged;
            container.AddChild(_cheatsToggle);

            _pvpToggle = CreateRuleToggle(container, 325, "开启 PVP", OnPvpChanged);
            _keepInventoryToggle = CreateRuleToggle(container, 370, "死亡保留物品与装备", OnKeepInventoryChanged);
            _keepSkillsToggle = CreateRuleToggle(container, 415, "死亡保留技能等级", OnKeepSkillsChanged);
            _keepExperienceToggle = CreateRuleToggle(container, 460, "死亡保留经验", OnKeepExperienceChanged);
        }

        private static ISleekToggle CreateRuleToggle(SleekFullscreenBox container, float y, string label,
            Toggled handler)
        {
            ISleekToggle toggle = Glazier.Get().CreateToggle();
            toggle.PositionOffset_X = -150;
            toggle.PositionOffset_Y = y;
            toggle.PositionScale_X = 0.5f;
            toggle.SizeOffset_X = 30;
            toggle.SizeOffset_Y = 30;
            toggle.AddLabel(label, ESleekSide.RIGHT);
            toggle.OnValueChanged += handler;
            container.AddChild(toggle);
            return toggle;
        }

        private static void BuildJoinMenu(SleekFullscreenBox container)
        {
            ISleekLabel titleLabel = Glazier.Get().CreateLabel();
            titleLabel.PositionOffset_X = -250;
            titleLabel.PositionOffset_Y = 100;
            titleLabel.PositionScale_X = 0.5f;
            titleLabel.SizeOffset_X = 500;
            titleLabel.SizeOffset_Y = 30;
            titleLabel.FontSize = ESleekFontSize.Large;
            titleLabel.TextAlignment = TextAnchor.MiddleCenter;
            titleLabel.Text = "通过 SteamID 请求加入";
            container.AddChild(titleLabel);

            _mySteamIdLabel = Glazier.Get().CreateLabel();
            _mySteamIdLabel.PositionOffset_X = -250;
            _mySteamIdLabel.PositionOffset_Y = 150;
            _mySteamIdLabel.PositionScale_X = 0.5f;
            _mySteamIdLabel.SizeOffset_X = 500;
            _mySteamIdLabel.SizeOffset_Y = 30;
            _mySteamIdLabel.FontSize = ESleekFontSize.Medium;
            _mySteamIdLabel.TextAlignment = TextAnchor.MiddleCenter;
            _mySteamIdLabel.Text = "我的 SteamID: ...";
            container.AddChild(_mySteamIdLabel);

            ISleekButton copyButton = Glazier.Get().CreateButton();
            copyButton.PositionOffset_X = -100;
            copyButton.PositionOffset_Y = 190;
            copyButton.PositionScale_X = 0.5f;
            copyButton.SizeOffset_X = 200;
            copyButton.SizeOffset_Y = 30;
            copyButton.FontSize = ESleekFontSize.Medium;
            copyButton.Text = "复制我的 SteamID";
            copyButton.TooltipText = "将本机 SteamID 写入剪贴板，作为可信旁路备用方案";
            copyButton.OnClicked += OnClickedCopyMySteamId;
            container.AddChild(copyButton);

            _steamIdField = Glazier.Get().CreateStringField();
            _steamIdField.PositionOffset_X = -250;
            _steamIdField.PositionOffset_Y = 240;
            _steamIdField.PositionScale_X = 0.5f;
            _steamIdField.SizeOffset_X = 500;
            _steamIdField.SizeOffset_Y = 30;
            _steamIdField.MaxLength = 20;
            _steamIdField.AddLabel("房主 SteamID:", ESleekSide.LEFT);
            _steamIdField.Text = "";
            _steamIdField.OnTextChanged += OnSteamIdTextChanged;
            _steamIdField.OnTextSubmitted += OnSteamIdTextSubmitted;
            container.AddChild(_steamIdField);

            ISleekButton joinButton = Glazier.Get().CreateButton();
            joinButton.PositionOffset_X = -100;
            joinButton.PositionOffset_Y = 290;
            joinButton.PositionScale_X = 0.5f;
            joinButton.SizeOffset_X = 200;
            joinButton.SizeOffset_Y = 30;
            joinButton.FontSize = ESleekFontSize.Medium;
            joinButton.Text = "请求加入";
            joinButton.TooltipText = "向房主发起 P2P 连接请求；若被白名单拒绝，请让房主在审批面板批准后再次点击";
            joinButton.OnClicked += OnClickedRequestJoin;
            container.AddChild(joinButton);

            _joinErrorLabel = Glazier.Get().CreateLabel();
            _joinErrorLabel.PositionOffset_X = -250;
            _joinErrorLabel.PositionOffset_Y = 330;
            _joinErrorLabel.PositionScale_X = 0.5f;
            _joinErrorLabel.SizeOffset_X = 500;
            _joinErrorLabel.SizeOffset_Y = 60;
            _joinErrorLabel.FontSize = ESleekFontSize.Medium;
            _joinErrorLabel.TextAlignment = TextAnchor.MiddleCenter;
            _joinErrorLabel.Text = "";
            container.AddChild(_joinErrorLabel);

            _copyStatusLabel = Glazier.Get().CreateLabel();
            _copyStatusLabel.PositionOffset_X = -100;
            _copyStatusLabel.PositionOffset_Y = 220;
            _copyStatusLabel.PositionScale_X = 0.5f;
            _copyStatusLabel.SizeOffset_X = 200;
            _copyStatusLabel.SizeOffset_Y = 20;
            _copyStatusLabel.FontSize = ESleekFontSize.Small;
            _copyStatusLabel.TextAlignment = TextAnchor.MiddleCenter;
            _copyStatusLabel.Text = "";
            _copyStatusLabel.IsVisible = false;
            container.AddChild(_copyStatusLabel);

            ISleekButton backButton = Glazier.Get().CreateButton();
            backButton.PositionOffset_X = -100;
            backButton.PositionOffset_Y = 410;
            backButton.PositionScale_X = 0.5f;
            backButton.SizeOffset_X = 200;
            backButton.SizeOffset_Y = 30;
            backButton.FontSize = ESleekFontSize.Medium;
            backButton.Text = "返回";
            backButton.OnClicked += OnClickedBackFromJoin;
            container.AddChild(backButton);
        }

        // ===== 事件处理 =====

        private static void OnClickedHost(ISleekElement button)
        {
            TryStartHost();
        }

        private static void OnClickedClient(ISleekElement button)
        {
            try
            {
                _roleMenuContainer?.AnimateOutOfView(0, 1);
                MenuPlayConnectUI.open();
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Client]", "[UnifiedConnect] 打开原版直连页面失败: " + ex.Message);
            }
        }

        private static void OnServerNameChanged(ISleekField field, string text)
        {
            _sessionServerName = (text ?? string.Empty).Trim();
        }

        private static void OnClickedCopyRoomSteamId(ISleekElement element)
        {
            ThreadUtil.assertIsGameThread();
            try
            {
                CSteamID local = SteamUser.GetSteamID();
                if (!local.IsValid() || !local.BIndividualAccount())
                {
                    if (_roomCopySteamIdButton != null) _roomCopySteamIdButton.Text = "SteamID 不可用";
                    return;
                }

                GUIUtility.systemCopyBuffer = local.m_SteamID.ToString();
                if (_roomCopySteamIdButton != null)
                {
                    _roomCopySteamIdButton.Text = "已复制房主 SteamID";
                    _roomCopySteamIdButton.TooltipText =
                        "已复制。把它发送给客机，客机粘贴到原版直连地址栏即可使用 Steam P2P 加入。";
                }
                RoleLogger.Info("[Host]", "[P2P-Menu] room host SteamID copied");
            }
            catch (Exception ex)
            {
                if (_roomCopySteamIdButton != null) _roomCopySteamIdButton.Text = "复制失败";
                RoleLogger.Warn("[Host]", "[P2P-Menu] copy room SteamID failed: " + ex.GetType().Name);
            }
        }

        private static void OnMaxPlayersChanged(ISleekUInt8Field field, byte value)
        {
            _sessionMaxPlayers = value;
        }

        private static void OnCheatsChanged(ISleekToggle toggle, bool value)
        {
            _sessionCheats = value;
        }

        private static void OnPvpChanged(ISleekToggle toggle, bool value) => _sessionPvp = value;
        private static void OnKeepInventoryChanged(ISleekToggle toggle, bool value) => _sessionKeepInventory = value;
        private static void OnKeepSkillsChanged(ISleekToggle toggle, bool value) => _sessionKeepSkills = value;
        private static void OnKeepExperienceChanged(ISleekToggle toggle, bool value) => _sessionKeepExperience = value;

        private static void OnClickedCycleMode(ISleekElement button)
        {
            switch (_sessionMode)
            {
                case EGameMode.EASY: _sessionMode = EGameMode.NORMAL; break;
                case EGameMode.NORMAL: _sessionMode = EGameMode.HARD; break;
                default: _sessionMode = EGameMode.EASY; break;
            }
            RefreshSessionSettingsView();
        }

        private static void RefreshSessionSettingsView()
        {
            if (_selectedMapLabel != null) _selectedMapLabel.Text = "地图：" + (_selectedMapName ?? string.Empty);
            if (_serverNameField != null) _serverNameField.Text = _sessionServerName ?? string.Empty;
            if (_maxPlayersField != null) _maxPlayersField.Value = _sessionMaxPlayers;
            if (_cheatsToggle != null) _cheatsToggle.Value = _sessionCheats;
            if (_pvpToggle != null) _pvpToggle.Value = _sessionPvp;
            if (_keepInventoryToggle != null) _keepInventoryToggle.Value = _sessionKeepInventory;
            if (_keepSkillsToggle != null) _keepSkillsToggle.Value = _sessionKeepSkills;
            if (_keepExperienceToggle != null) _keepExperienceToggle.Value = _sessionKeepExperience;
            if (_roomCopySteamIdButton != null)
            {
                _roomCopySteamIdButton.Text = "复制房主 SteamID";
                _roomCopySteamIdButton.TooltipText =
                    "复制你的个人 SteamID。发送给客机后，客机在原版“开始游戏 → 直连”的顶部地址栏粘贴即可加入。";
            }
            if (_modeButton != null)
            {
                string label = _sessionMode == EGameMode.EASY ? "休闲（简单）" :
                    _sessionMode == EGameMode.NORMAL ? "冒险（普通）" : "硬核（困难）";
                _modeButton.Text = "房间玩法：" + label + "（点击切换）";
            }
        }

        private static void OnClickedBackFromRole(ISleekElement button)
        {
            try
            {
                _roleMenuContainer?.AnimateOutOfView(0, 1);
                MenuPlaySingleplayerUI.open();
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Shared]", $"[P2P-Menu] BackFromRole 异常: {ex.Message}");
            }
        }

        private static void OnClickedBackFromJoin(ISleekElement button)
        {
            try
            {
                _joinMenuContainer?.AnimateOutOfView(0, 1);
                _roleMenuContainer?.AnimateIntoView();
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Shared]", $"[P2P-Menu] BackFromJoin 异常: {ex.Message}");
            }
        }

        private static void OnSteamIdTextChanged(ISleekField field, string text)
        {
            _steamIdInput = text ?? "";
        }

        private static void OnSteamIdTextSubmitted(ISleekField field)
        {
            TryJoin();
        }

        private static void OnClickedRequestJoin(ISleekElement button)
        {
            TryJoin();
        }

        private static void OnClickedCopyMySteamId(ISleekElement button)
        {
            try
            {
                CSteamID myId = SteamUser.GetSteamID();
                if (myId == CSteamID.Nil || !myId.IsValid())
                {
                    if (_joinErrorLabel != null) _joinErrorLabel.Text = "本机 SteamID 当前不可用";
                    return;
                }
                GUIUtility.systemCopyBuffer = myId.m_SteamID.ToString();
                _copyStatusUntil = Time.unscaledTime + 2f;
                if (_copyStatusLabel != null)
                {
                    _copyStatusLabel.Text = "已复制到剪贴板";
                    _copyStatusLabel.IsVisible = true;
                }
                RoleLogger.Info("[Client]", $"[P2P-Menu] 已复制本机 SteamID: {myId.m_SteamID}");
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Client]", $"[P2P-Menu] CopyMySteamId 异常: {ex.Message}");
            }
        }

        // ===== 业务 =====

        private static void TryStartHost()
        {
            try
            {
                if (!SteamP2PFriendsPlugin.DiagnosticBuildValid)
                {
                    MenuUI.alert("SteamP2PFriends 自检未通过，多人联机功能不可用。请查看日志。");
                    return;
                }

                if (string.IsNullOrEmpty(_selectedMapName))
                {
                    MenuUI.alert("未选择地图，无法启动 P2P 服务器。");
                    return;
                }

                LevelInfo level = Level.getLevel(_selectedMapName);
                Level.UpdateLevelReference(ref level);
                if (level == null || level.IsMissingAnyDependencies())
                {
                    MenuUI.alert("选中地图缺失依赖，无法启动。请检查 Workshop 订阅。");
                    return;
                }

                Provider.map = level.name;
                string serverName = (_sessionServerName ?? string.Empty).Trim();
                if (serverName.Length == 0)
                {
                    MenuUI.alert("房间名称不能为空。");
                    return;
                }
                byte maxPlayers = _sessionMaxPlayers;
                if (maxPlayers < 2 || maxPlayers > 24)
                {
                    MenuUI.alert("最大玩家数必须在 2 到 24 之间。");
                    return;
                }
                EGameMode mode = _sessionMode;
                bool cheats = _sessionCheats;
                var roomRules = new P2PRoomRules(
                    _sessionPvp,
                    _sessionKeepInventory,
                    _sessionKeepSkills,
                    _sessionKeepExperience);

                RoleLogger.Info("[Shared]",
                    $"[P2P-Menu] 创建房间 (map={level.name}, mode={mode}, cheats={cheats}, " +
                    $"maxPlayers={maxPlayers}, pvp={_sessionPvp}, keepInventory={_sessionKeepInventory}, " +
                    $"keepSkills={_sessionKeepSkills}, keepExperience={_sessionKeepExperience})");

                _roleMenuContainer?.AnimateOutOfView(0, 1);

                HostManager.StartP2PServer(level.name, serverName, maxPlayers, mode, cheats, roomRules);
                PersistLastRoomSettings(serverName, maxPlayers, mode, cheats, roomRules);
            }
            catch (Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P2P-Menu] TryStartHost 失败: {ex}");
                MenuUI.alert("启动 P2P 服务器失败，请查看日志。");
            }
        }

        private static EGameMode NormalizePersistedMode(EGameMode mode)
        {
            return mode == EGameMode.EASY || mode == EGameMode.NORMAL || mode == EGameMode.HARD
                ? mode
                : EGameMode.EASY;
        }

        internal static EGameMode NormalizePersistedModeForTest(EGameMode mode)
        {
            return NormalizePersistedMode(mode);
        }

        /// <summary>
        /// Persists only reusable room rules after all validation has passed. Map selection remains
        /// vanilla-owned, while passwords, approval state, Steam IDs and session admin state are excluded.
        /// Persistence failure is non-fatal and must never prevent hosting.
        /// </summary>
        private static void PersistLastRoomSettings(string serverName, byte maxPlayers, EGameMode mode,
            bool cheats, P2PRoomRules roomRules)
        {
            try
            {
                if (SteamP2PFriendsPlugin.ServerName != null) SteamP2PFriendsPlugin.ServerName.Value = serverName;
                if (SteamP2PFriendsPlugin.MaxPlayers != null) SteamP2PFriendsPlugin.MaxPlayers.Value = maxPlayers;
                if (SteamP2PFriendsPlugin.LastRoomMode != null) SteamP2PFriendsPlugin.LastRoomMode.Value = NormalizePersistedMode(mode);
                if (SteamP2PFriendsPlugin.LastRoomCheats != null) SteamP2PFriendsPlugin.LastRoomCheats.Value = cheats;
                if (SteamP2PFriendsPlugin.LastRoomPvp != null) SteamP2PFriendsPlugin.LastRoomPvp.Value = roomRules.EnablePvp;
                if (SteamP2PFriendsPlugin.LastRoomKeepInventory != null) SteamP2PFriendsPlugin.LastRoomKeepInventory.Value = roomRules.KeepInventoryOnDeath;
                if (SteamP2PFriendsPlugin.LastRoomKeepSkills != null) SteamP2PFriendsPlugin.LastRoomKeepSkills.Value = roomRules.KeepSkillsOnDeath;
                if (SteamP2PFriendsPlugin.LastRoomKeepExperience != null) SteamP2PFriendsPlugin.LastRoomKeepExperience.Value = roomRules.KeepExperienceOnDeath;
                SteamP2PFriendsPlugin.Instance?.Config.Save();
                RoleLogger.Info("[Shared]", "[P2P-Menu] last room settings persisted");
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Shared]", "[P2P-Menu] unable to persist room settings: " + ex.GetType().Name);
            }
        }

        private static void TryJoin()
        {
            try
            {
                if (!SteamP2PFriendsPlugin.DiagnosticBuildValid)
                {
                    SetJoinError("SteamP2PFriends 自检未通过，客机连接被拒绝。请查看日志。");
                    return;
                }

                string input = (_steamIdInput ?? "").Trim();
                if (string.IsNullOrEmpty(input))
                {
                    SetJoinError("请输入房主 SteamID");
                    return;
                }

                if (!ulong.TryParse(input, out ulong steamId) || steamId == 0)
                {
                    SetJoinError("SteamID 格式无效（应为 17 位数字）");
                    return;
                }

                CSteamID target = new CSteamID(steamId);
                if (!target.IsValid())
                {
                    SetJoinError("SteamID 无效");
                    return;
                }

                CSteamID myId = SteamUser.GetSteamID();
                if (steamId == myId.m_SteamID)
                {
                    SetJoinError("不能加入自己的房间");
                    return;
                }

                SetJoinError("");
                RoleLogger.Info("[Client]", $"[P2P-Menu] 请求加入：发起 P2P 连接到 {steamId}");

                _joinMenuContainer?.AnimateOutOfView(0, 1);
                MenuPlaySingleplayerUI.open();

                P2PJoinManager.TryConnectToHost(steamId);

                SetJoinError("已发起请求。若房主尚未批准，请让房主查看待审批列表，批准后再次点击加入。");
            }
            catch (Exception ex)
            {
                RoleLogger.Error("[Client]", $"[P2P-Menu] TryJoin 失败: {ex}");
                SetJoinError("请求加入失败，请查看日志。");
            }
        }

        private static void RefreshMySteamIdLabel()
        {
            if (_mySteamIdLabel == null) return;
            try
            {
                CSteamID myId = SteamUser.GetSteamID();
                if (myId == CSteamID.Nil || !myId.IsValid())
                {
                    _mySteamIdLabel.Text = "我的 SteamID: 当前不可用";
                }
                else
                {
                    _mySteamIdLabel.Text = "我的 SteamID: " + myId.m_SteamID.ToString();
                }
            }
            catch (Exception ex)
            {
                _mySteamIdLabel.Text = "我的 SteamID: 读取失败";
                RoleLogger.Warn("[Client]", $"[P2P-Menu] RefreshMySteamIdLabel 异常: {ex.Message}");
            }
        }

        private static void SetJoinError(string msg)
        {
            if (_joinErrorLabel != null)
            {
                _joinErrorLabel.Text = msg ?? "";
            }
        }

        // ===== Tick（蓝图 v3 §3.4）=====

        /// <summary>
        /// 蓝图 v3 §3.4 + §4.5：Plugin.Update 调用。
        /// EnsureCreated -> 复制状态可见性更新。
        /// </summary>
        internal static void Tick()
        {
            if (!_testBypassThreadAssert)
            {
                ThreadUtil.assertIsGameThread();
            }

            EnsureCreated();
            if (!_created) return;

            if (_copyStatusLabel == null) return;
            try
            {
                if (Time.unscaledTime < _copyStatusUntil)
                {
                    if (!_copyStatusLabel.IsVisible) _copyStatusLabel.IsVisible = true;
                }
                else
                {
                    if (_copyStatusLabel.IsVisible) _copyStatusLabel.IsVisible = false;
                }
            }
            catch
            {
            }
        }

        // ===== Stage 7-5 v3 客机等待审批 UI（parent identity 生命周期）=====

        private static ISleekElement _waitBoundParent;
        private static ISleekBox _waitBox;
        private static ISleekLabel _waitLabel;
        private static ISleekButton _waitCancelButton;
        private static System.Action _waitCancelCallback;
        internal static bool _testBypassGlazier;
        private static bool _waitVisible;
        private static int _waitSecondsRemaining;
        internal static bool IsWaitVisibleForTest => _waitVisible;
        internal static int WaitSecondsRemainingForTest => _waitSecondsRemaining;
        internal static ISleekElement WaitBoundParentForTest => _waitBoundParent;

        // 仅销毁视图；绝不修改 P2PApprovalWaitController 的预算或连接状态
        private static void DetachApprovalWaitView()
        {
            if (_waitBoundParent != null && _waitBox != null)
                TryRemoveChild(_waitBoundParent, _waitBox);
            _waitBoundParent = null;
            _waitBox = null;
            _waitLabel = null;
            _waitCancelButton = null;
            _waitVisible = false;
        }

        /// <summary>
        /// v3 [P0-WAIT-UI-PARENT-LIFECYCLE-03]：确保等待 UI 可见且绑定到当前 parent。
        /// parent 变更 -> 先 Detach 旧视图，再在新 parent 上重建。返回 false = 不可见/不可取消。
        /// </summary>
        internal static bool EnsureApprovalWaitVisible(ulong hostSteamId, int seconds, System.Action cancel)
        {
            if (!_testBypassThreadAssert) ThreadUtil.assertIsGameThread();
            if (!P2PClientUiEnvironment.CanTouchClientUi()) return false;

            EnsureCreated();
            if (!_created || _boundParent == null) return false;

            // 已绑定同一 parent -> 仅更新倒计时
            if (_waitBox != null && ReferenceEquals(_waitBoundParent, _boundParent))
            {
                _waitCancelCallback = cancel;
                UpdateApprovalWait(seconds);
                return true;
            }

            // parent 变更或首次 -> Detach 旧视图 + 重建
            DetachApprovalWaitView();
            _waitCancelCallback = cancel;
            _waitVisible = true;
            _waitSecondsRemaining = seconds;

            if (_testBypassGlazier)
            {
                _waitBoundParent = _boundParent;
                return true;
            }

            try
            {
                _waitBoundParent = _boundParent;
                _waitBox = Glazier.Get().CreateBox();
                _waitBox.PositionScale_X = 0.5f;
                _waitBox.PositionScale_Y = 0.5f;
                _waitBox.PositionOffset_X = -150;
                _waitBox.PositionOffset_Y = -80;
                _waitBox.SizeOffset_X = 300;
                _waitBox.SizeOffset_Y = 160;
                _waitBox.IsVisible = true;

                ISleekLabel title = Glazier.Get().CreateLabel();
                title.PositionOffset_X = 10; title.PositionOffset_Y = 10;
                title.SizeOffset_X = 280; title.SizeOffset_Y = 24;
                title.FontSize = ESleekFontSize.Medium;
                title.Text = "等待房主审批";
                _waitBox.AddChild(title);

                _waitLabel = Glazier.Get().CreateLabel();
                _waitLabel.PositionOffset_X = 10; _waitLabel.PositionOffset_Y = 44;
                _waitLabel.SizeOffset_X = 280; _waitLabel.SizeOffset_Y = 24;
                _waitLabel.FontSize = ESleekFontSize.Small;
                _waitLabel.Text = "距下次尝试：" + seconds + "秒";
                _waitBox.AddChild(_waitLabel);

                ISleekLabel hint = Glazier.Get().CreateLabel();
                hint.PositionOffset_X = 10; hint.PositionOffset_Y = 72;
                hint.SizeOffset_X = 280; hint.SizeOffset_Y = 36;
                hint.FontSize = ESleekFontSize.Small;
                hint.Text = "服务器已拒绝本次连接；房主批准后将自动重试。";
                _waitBox.AddChild(hint);

                _waitCancelButton = Glazier.Get().CreateButton();
                _waitCancelButton.PositionOffset_X = 75; _waitCancelButton.PositionOffset_Y = 115;
                _waitCancelButton.SizeOffset_X = 150; _waitCancelButton.SizeOffset_Y = 30;
                _waitCancelButton.FontSize = ESleekFontSize.Small;
                _waitCancelButton.Text = "取消";
                _waitCancelButton.OnClicked += OnWaitCancelClicked;
                _waitBox.AddChild(_waitCancelButton);

                _waitBoundParent.AddChild(_waitBox);
                return true;
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Client]", "[P2P-Wait] view build failed: " + ex.GetType().Name);
                DetachApprovalWaitView();
                _waitVisible = false;
                return false;
            }
        }

        internal static void UpdateApprovalWait(int secondsRemaining)
        {
            if (!_testBypassThreadAssert) ThreadUtil.assertIsGameThread();
            _waitSecondsRemaining = secondsRemaining;
            if (_testBypassGlazier) return;
            try { if (_waitLabel != null) _waitLabel.Text = "距下次尝试：" + secondsRemaining + "秒"; }
            catch { }
        }

        /// <summary>v3：通过 _waitBoundParent 移除视图（即使 _boundParent 已改变）。</summary>
        internal static void HideApprovalWait()
        {
            if (!_testBypassThreadAssert) ThreadUtil.assertIsGameThread();
            _waitVisible = false;
            _waitSecondsRemaining = 0;
            _waitCancelCallback = null;
            DetachApprovalWaitView();
        }

        private static void OnWaitCancelClicked(ISleekElement button)
        {
            try { _waitCancelCallback?.Invoke(); } catch { }
        }
    }
}
