using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace SteamP2PFriends.Client
{
    internal static class P2PQuarantineClientView
    {
        private static ISleekElement _boundParent;
        private static ISleekBox _root;
        private static ISleekLabel _countdown;
        private static float _observedAt;
        private static bool _wasActive;
        private static int _nextChatAnnouncement = 25;
        internal static Action<string> _testChatSink;

        internal static bool IsLocalPlayerQuarantined
        {
            get
            {
                if (!P2PClientUiEnvironment.CanTouchClientUi()) return false;
                Player local = Player.LocalPlayer;
                if (local == null) return false;
                uint flags = unchecked((uint)(int)local.pluginWidgetFlags);
                return (flags & P2PQuarantineAdmissionService.QuarantineSignalMask) != 0u;
            }
        }

        internal static void Tick()
        {
            ThreadUtil.assertIsGameThread();
            bool active = IsLocalPlayerQuarantined;
            if (!active)
            {
                if (_wasActive || _root != null) Destroy();
                _wasActive = false;
                return;
            }

            if (!_wasActive)
            {
                _observedAt = Time.realtimeSinceStartup;
                _wasActive = true;
                _nextChatAnnouncement = 25;
            }

            EnsureCreated();
            int remaining = Math.Max(0, (int)Math.Ceiling(
                P2PQuarantineAdmissionService.ActiveLifetimeSeconds -
                (Time.realtimeSinceStartup - _observedAt)));
            if (_countdown != null) _countdown.Text = "剩余等待时间：" + remaining + " 秒";
            // Chat countdown is server-authoritative and targeted to this player. The widget flag
            // remains presentation-only so a missed/late UI signal cannot suppress chat messages.
        }

        internal static void Destroy()
        {
            if (_boundParent != null && _root != null)
            {
                try { _boundParent.RemoveChild(_root); } catch { }
            }
            _boundParent = null;
            _root = null;
            _countdown = null;
            _observedAt = 0f;
            _nextChatAnnouncement = 25;
        }

        private static void UpdateChatAnnouncements(bool active, int remaining)
        {
            if (!active)
            {
                AnnounceLocal("房主审核状态已结束。");
                _nextChatAnnouncement = 25;
                return;
            }

            if (_nextChatAnnouncement < 5 || remaining > _nextChatAnnouncement) return;
            int announced = _nextChatAnnouncement;
            while (_nextChatAnnouncement >= 5 && remaining <= _nextChatAnnouncement)
                _nextChatAnnouncement -= 5;
            AnnounceLocal("等待房主审核：剩余约 " + announced + " 秒。");
        }

        internal static void ResetCountdownForTest()
        {
            _nextChatAnnouncement = 25;
        }

        internal static void UpdateCountdownAnnouncementsForTest(bool active, int remaining)
        {
            UpdateChatAnnouncements(active, remaining);
        }

        private static void AnnounceLocal(string text)
        {
            if (_testChatSink != null)
            {
                _testChatSink(text);
                return;
            }
            try { AnnounceLocalRuntime(text); }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Client]",
                    "[P2P-QuarantineUI] local chat announcement failed: " + ex.GetType().Name);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AnnounceLocalRuntime(string text)
        {
            // Local insertion only: no SendChatRequest and no server/network traffic.
            ChatManager.receiveChatMessage(
                Steamworks.CSteamID.Nil,
                string.Empty,
                EChatMode.WELCOME,
                Palette.SERVER,
                false,
                text);
        }

        private static void EnsureCreated()
        {
            if (!P2PClientUiEnvironment.CanTouchClientUi()) return;
            ISleekElement parent = PlayerUI.container;
            if (parent == null) return;
            if (_root != null && ReferenceEquals(parent, _boundParent)) return;

            Destroy();
            try
            {
                _boundParent = parent;
                _root = Glazier.Get().CreateBox();
                _root.PositionScale_X = 0.5f;
                _root.PositionOffset_X = -210;
                _root.PositionOffset_Y = 35;
                _root.SizeOffset_X = 420;
                _root.SizeOffset_Y = 112;

                ISleekLabel title = Glazier.Get().CreateLabel();
                title.PositionOffset_X = 10;
                title.PositionOffset_Y = 8;
                title.SizeOffset_X = 400;
                title.SizeOffset_Y = 30;
                title.FontSize = ESleekFontSize.Medium;
                title.Text = "等待房主审核";
                _root.AddChild(title);

                ISleekLabel hint = Glazier.Get().CreateLabel();
                hint.PositionOffset_X = 10;
                hint.PositionOffset_Y = 40;
                hint.SizeOffset_X = 400;
                hint.SizeOffset_Y = 32;
                hint.FontSize = ESleekFontSize.Small;
                hint.Text = "审核通过前无法移动、交互、使用物品或指令，并处于无敌状态。";
                _root.AddChild(hint);

                _countdown = Glazier.Get().CreateLabel();
                _countdown.PositionOffset_X = 10;
                _countdown.PositionOffset_Y = 76;
                _countdown.SizeOffset_X = 400;
                _countdown.SizeOffset_Y = 24;
                _countdown.FontSize = ESleekFontSize.Small;
                _root.AddChild(_countdown);

                _boundParent.AddChild(_root);
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Client]",
                    "[P2P-QuarantineUI] waiting view build failed: " + ex.GetType().Name);
                Destroy();
            }
        }
    }
}
