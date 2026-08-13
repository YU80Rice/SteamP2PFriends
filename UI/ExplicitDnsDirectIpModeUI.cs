using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Client;
using SteamP2PFriends.Shared;
using System;
using System.Reflection;

namespace SteamP2PFriends.UI
{
    /// <summary>
    /// Stage 9-3 (v2) [指令 A + C + G]: explicit DNS direct-connect mode toggle injected into the
    /// vanilla MenuPlayConnectUI layout. Uses ISleekToggle (Glazier), never IMGUI/OnGUI.
    ///
    /// Layout: a 30px row between the password field and the connect button; the vanilla connect
    /// button is moved from Y=45 to Y=85. The toggle defaults to false and is never persisted as
    /// default-on, so U3DS/vanilla DNS routing is never hijacked by default.
    ///
    /// Parent lifecycle: _boundParent is tracked; repeated EnsureCreated with the same parent is a
    /// no-op, a parent change destroys and rebuilds, and Destroy detaches all children.
    ///
    /// v2 指令 C: turning the toggle OFF immediately cancels any in-flight DNS resolution.
    /// v2 指令 G: the moved connect button's original Y is stored; Destroy best-effort restores it.
    ///            If the button cannot be moved, fail-closed: do NOT create the toggle (avoid overlap).
    /// </summary>
    internal static class ExplicitDnsDirectIpModeUI
    {
        private static ISleekElement _boundParent;
        private static ISleekToggle _toggle;
        private static bool _created;

        // v2 指令 G: connect button restore state.
        private static System.Action _restoreConnectButton;
        private static float _originalConnectButtonY;
        private static bool _connectButtonMoved;

        internal static bool _testBypassGlazier;
        internal static bool _testBypassThreadAssert;
        internal static Func<ISleekElement> _testParentProvider;
        // v2 指令 G test hook: fake connect button Y reader/writer for UI3.
        internal static Func<float> _testConnectButtonYReader;
        internal static System.Action<float> _testConnectButtonYWriter;

        /// <summary>Whether the player checked "plugin DNS direct-connect (FRP)".</summary>
        internal static bool IsEnabled
        {
            get
            {
                try { return _toggle != null && _toggle.Value; }
                catch { return false; }
            }
        }

        internal static ISleekElement BoundParentForTest => _boundParent;
        internal static bool IsCreatedForTest => _created;
        internal static bool ConnectButtonMovedForTest => _connectButtonMoved;
        internal static float OriginalConnectButtonYForTest => _originalConnectButtonY;

        /// <summary>
        /// Binds to the vanilla MenuPlayConnectUI container (via reflection) and creates the toggle
        /// if not already bound. Parent change destroys and rebuilds. Called from
        /// MenuPlayConnectP2PIndicatorPatch.Postfix (existing patch, no new constructor patch).
        /// </summary>
        internal static void EnsureCreated()
        {
            if (!_testBypassThreadAssert) ThreadUtil.assertIsGameThread();

            ISleekElement container = ResolveParent();
            if (container == null)
            {
                // Menu UI not available: fail-closed, never partially create.
                Destroy();
                return;
            }

            if (_created && ReferenceEquals(_boundParent, container)) return;

            Destroy();
            _boundParent = container;

            try
            {
                // v2 指令 G: move the button first (works with test hooks or real reflection).
                // Only create the toggle if the button can be moved (fail-closed to avoid overlap).
                if (!RepositionConnectButton())
                {
                    RoleLogger.Warn("[Client]", "[DNS-UI] cannot move connect button; toggle not created (fail-closed)");
                    _boundParent = null;
                    return;
                }

                if (_testBypassGlazier) { _created = true; return; }

                _toggle = Glazier.Get().CreateToggle();
                _toggle.PositionOffset_X = -300;
                _toggle.PositionOffset_Y = 40;
                _toggle.PositionScale_X = 0.5f;
                _toggle.PositionScale_Y = 0.5f;
                _toggle.SizeOffset_X = 30;
                _toggle.SizeOffset_Y = 30;
                _toggle.AddLabel("插件域名直连（FRP）", ESleekSide.RIGHT);
                _toggle.TooltipText = "启用后将域名解析为 IPv4，并直接连接填写的 UDP 端口；不会执行原版 U3DS/A2S 查询。";
                _toggle.Value = false; // never persist as default-on
                _toggle.OnValueChanged += OnToggleChanged;
                _boundParent.AddChild(_toggle);

                _created = true;
                RoleLogger.Info("[Client]", "[DNS-UI] Explicit DNS direct-connect toggle created");
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Client]", "[DNS-UI] EnsureCreated failed: " + ex.GetType().Name);
                Destroy();
            }
        }

        /// <summary>Detaches all children, restores the moved button, and clears state.</summary>
        internal static void Destroy()
        {
            if (!_testBypassThreadAssert) ThreadUtil.assertIsGameThread();
            if (_toggle != null)
            {
                try
                {
                    _toggle.OnValueChanged -= OnToggleChanged;
                    if (_boundParent != null) TryRemoveChild(_boundParent, _toggle);
                }
                catch { }
            }
            _toggle = null;
            _boundParent = null;
            _created = false;

            // v2 指令 G: best-effort restore the vanilla connect button's original Y.
            if (_connectButtonMoved && _restoreConnectButton != null)
            {
                try { _restoreConnectButton(); }
                catch { }
            }
            _restoreConnectButton = null;
            _originalConnectButtonY = 0f;
            _connectButtonMoved = false;
        }

        internal static void ResetForTest()
        {
            // Preserve the current bypass flags so Destroy does not assert on the test thread.
            bool prevGlazier = _testBypassGlazier;
            bool prevThread = _testBypassThreadAssert;
            _testBypassGlazier = true;
            _testBypassThreadAssert = true;
            _testParentProvider = null;
            _testConnectButtonYReader = null;
            _testConnectButtonYWriter = null;
            Destroy();
            _testBypassGlazier = prevGlazier;
            _testBypassThreadAssert = prevThread;
        }

        private static void OnToggleChanged(ISleekToggle toggle, bool value)
        {
            RoleLogger.InfoVerbose("[Client]",
                "[DNS-UI] explicit DNS direct-connect mode=" + value);
            // v2 指令 C: turning the mode OFF immediately cancels any in-flight DNS resolution.
            if (!value)
            {
                try { ExplicitDnsDirectIpService.CancelAndReset(); }
                catch (Exception ex)
                {
                    RoleLogger.Warn("[Client]", "[DNS-UI] toggle-off cancel failed: " + ex.GetType().Name);
                }
            }
        }

        /// <summary>
        /// Moves the vanilla connect button from Y=45 to Y=85 and records the original Y for restore.
        /// Returns false if the button or its Y cannot be resolved/written (fail-closed).
        /// </summary>
        private static bool RepositionConnectButton()
        {
            try
            {
                if (_testConnectButtonYReader != null && _testConnectButtonYWriter != null)
                {
                    {
                        _originalConnectButtonY = _testConnectButtonYReader();
                        _testConnectButtonYWriter(85f);
                        float restoreTestY = _originalConnectButtonY;
                        _restoreConnectButton = () => _testConnectButtonYWriter(restoreTestY);
                        _connectButtonMoved = true;
                        return true;
                    }
                }

                FieldInfo field = AccessTools.Field(typeof(MenuPlayConnectUI), "connectButton");
                if (field == null) return false;
                object button = field.GetValue(null);
                if (button == null) return false;

                ISleekElement sleekButton = button as ISleekElement;
                if (sleekButton == null) return false;

                ISleekElement restoreButton = sleekButton;
                {
                    _originalConnectButtonY = sleekButton.PositionOffset_Y;
                    sleekButton.PositionOffset_Y = 85f;
                    float restoreRealY = _originalConnectButtonY;
                    _restoreConnectButton = () => { restoreButton.PositionOffset_Y = restoreRealY; };
                }
                _connectButtonMoved = true;
                return true;
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Client]", "[DNS-UI] RepositionConnectButton failed: " + ex.GetType().Name);
                _connectButtonMoved = false;
                _restoreConnectButton = null;
                return false;
            }
        }

        private static ISleekElement ResolveParent()
        {
            if (_testParentProvider != null)
            {
                try { return _testParentProvider(); }
                catch { return null; }
            }
            try
            {
                FieldInfo field = AccessTools.Field(typeof(MenuPlayConnectUI), "container");
                return field == null ? null : field.GetValue(null) as ISleekElement;
            }
            catch
            {
                return null;
            }
        }

        private static void TryRemoveChild(ISleekElement parent, ISleekElement child)
        {
            try { parent.RemoveChild(child); }
            catch { }
        }
    }
}
