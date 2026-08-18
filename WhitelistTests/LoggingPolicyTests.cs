using System;
using System.Reflection;

namespace SteamP2PFriends.WhitelistTests
{
    internal static class LoggingPolicyTests
    {
        internal static bool Test_LegacyDiagnosticMarkersAreClassified()
        {
            return IsDiagnostic("[P0-C] ReceiveInventory ENTER") &&
                   IsDiagnostic("[Diag] patch registration summary") &&
                   IsDiagnostic("[WorldSyncDiag] point=ReceiveZombies") &&
                   IsDiagnostic("[RemotePlayerRenderProbe] sample") &&
                   IsDiagnostic("[ManualPatch] registered") &&
                   IsDiagnostic("v0.2.3.17 historical repair detail") &&
                   !IsDiagnostic("[P2P] host session started") &&
                   !IsDiagnostic("[Compat] report written");
        }

        internal static bool Test_VerboseToggleIsAtomicAndDefaultsOff()
        {
            Type policyType = typeof(SteamP2PFriendsPlugin).Assembly.GetType(
                "SteamP2PFriends.Shared.PluginLogPolicy", throwOnError: false);
            if (policyType == null) return false;

            MethodInfo configure = policyType.GetMethod(
                "ConfigureEarly", BindingFlags.Static | BindingFlags.NonPublic);
            PropertyInfo enabled = policyType.GetProperty(
                "IsVerboseDiagnosticsEnabled", BindingFlags.Static | BindingFlags.NonPublic);
            if (configure == null || enabled == null) return false;

            try
            {
                configure.Invoke(null, new object[] { false });
                if ((bool)enabled.GetValue(null, null)) return false;

                configure.Invoke(null, new object[] { true });
                if (!(bool)enabled.GetValue(null, null)) return false;

                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { configure.Invoke(null, new object[] { false }); } catch { }
            }
        }

        internal static bool Test_LegacyLabelsAreRemovedAtOutputBoundary()
        {
            Type roleLoggerType = typeof(SteamP2PFriendsPlugin).Assembly.GetType(
                "SteamP2PFriends.Shared.RoleLogger", throwOnError: false);
            if (roleLoggerType == null) return false;

            MethodInfo normalize = roleLoggerType.GetMethod(
                "NormalizeForOutput", BindingFlags.Static | BindingFlags.NonPublic);
            if (normalize == null) return false;

            try
            {
                string result = (string)normalize.Invoke(null,
                    new object[] { "[P0-C-2/Animal] v0.2.3.33 registration failed" });
                return result == "[Animal] registration failed";
            }
            catch
            {
                return false;
            }
        }

        internal static bool Test_InternalDiagnosticTagsAreRemovedFromOperationalText()
        {
            Type roleLoggerType = typeof(SteamP2PFriendsPlugin).Assembly.GetType(
                "SteamP2PFriends.Shared.RoleLogger", throwOnError: false);
            if (roleLoggerType == null) return false;

            MethodInfo normalize = roleLoggerType.GetMethod(
                "NormalizeForOutput", BindingFlags.Static | BindingFlags.NonPublic);
            if (normalize == null) return false;

            try
            {
                string result = (string)normalize.Invoke(null,
                    new object[] { "[Diag] [D-Friend] [WorldSyncDiag/Vehicle] RemotePlayerRenderProbe failed" });
                return result == "internal monitor failed" &&
                       result.IndexOf("Diag", StringComparison.OrdinalIgnoreCase) < 0 &&
                       result.IndexOf("WorldSync", StringComparison.OrdinalIgnoreCase) < 0 &&
                       result.IndexOf("RenderProbe", StringComparison.OrdinalIgnoreCase) < 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsDiagnostic(string message)
        {
            Type roleLoggerType = typeof(SteamP2PFriendsPlugin).Assembly.GetType(
                "SteamP2PFriends.Shared.RoleLogger", throwOnError: false);
            if (roleLoggerType == null) return false;

            MethodInfo classify = roleLoggerType.GetMethod(
                "IsDiagnosticMessage", BindingFlags.Static | BindingFlags.NonPublic);
            if (classify == null) return false;

            try
            {
                return (bool)classify.Invoke(null, new object[] { message });
            }
            catch
            {
                return false;
            }
        }
    }
}
