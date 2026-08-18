using BepInEx.Logging;
using SDG.Unturned;
using SteamP2PFriends.Host;
using System;
using System.Text.RegularExpressions;

namespace SteamP2PFriends.Shared
{
    /// <summary>Routes operational, warning, error, and opt-in diagnostic log messages.</summary>
    internal static class RoleLogger
    {
        private static ManualLogSource _logger;
        private static readonly string[] DiagnosticMarkers =
        {
            "[Diag]",
            "[P0-",
            "[P1-",
            "[P2-",
            "[P3-",
            "[P4-",
            "[P5-",
            "[P6-",
            "[P7-",
            "[P8-",
            "[P9-",
            "[Stage",
            "[D-",
            "[5B-",
            "Diagnostic",
            "Probe",
            "Trace",
            "LoadingGate",
            "WorldSync",
            "RenderProbe",
            "ManualPatch",
            "RegisterManual",
            "Transpiler",
            "v0.",
            "自检"
        };
        private static readonly Regex LegacyBracketTag = new Regex(
            @"\[(?:P\d+(?:-[A-Za-z0-9]+)*|Stage\d+[A-Za-z0-9-]*)(?:/([^\]]+))?\]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex LegacyToken = new Regex(
            @"\b(?:P\d+(?:-[A-Za-z0-9]+)+|Stage\d+[A-Za-z0-9-]*)\b|\bv\d+\.\d+\.\d+(?:\.\d+)?\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex InternalDiagnosticBracketTag = new Regex(
            @"\s*\[(?:Diag(?:nostic)?|D-[^\]]+|[^\]]*(?:WorldSyncDiag|RenderProbe|LoadingGate|LogProbe|Diagnostic|Trace|Probe)[^\]]*)\]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex InternalDiagnosticToken = new Regex(
            @"\b[A-Za-z_][A-Za-z0-9_]*(?:WorldSyncDiag|RenderProbe|LoadingGate|LogProbe|Diagnostic|Trace|Probe)[A-Za-z0-9_]*\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex RepeatedWhitespace = new Regex(
            @"\s{2,}",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal static void Initialize(ManualLogSource logger, bool verbose)
        {
            _logger = logger;
            PluginLogPolicy.ConfigureEarly(verbose);
        }

        internal static void Info(string role, string msg)
        {
            if (IsDiagnosticMessage(msg))
            {
                Diagnostic(role, msg);
                return;
            }

            _logger?.LogInfo($"[{role}] {NormalizeForOutput(msg)}");
        }

        internal static void InfoVerbose(string role, string msg)
        {
            Diagnostic(role, msg);
        }

        internal static void Diagnostic(string role, string msg)
        {
            if (!PluginLogPolicy.IsVerboseDiagnosticsEnabled) return;
            _logger?.LogInfo($"[{role}] {NormalizeForOutput(msg)}");
        }

        internal static void Warn(string role, string msg)
        {
            _logger?.LogWarning($"[{role}] {NormalizeForOutput(msg)}");
        }

        internal static void Error(string role, string msg)
        {
            _logger?.LogError($"[{role}] {NormalizeForOutput(msg)}");
        }

        /// <summary>Derives a best-effort role prefix without allowing startup failures to escape.</summary>
        internal static string ResolveDynamicRole()
        {
            bool isServer = false, isClient = false, isP2PHost = false;
            try
            {
                isServer = Provider.isServer;
            }
            catch { /* Provider 静态构造可能在极早期失败 */ }
            try
            {
                isClient = Provider.isClient;
            }
            catch { /* ignore */ }
            try
            {
                isP2PHost = HostManager.IsP2PHostMode;
            }
            catch { /* HostManager 静态构造可能失败 */ }

            if (isP2PHost) return "[Host]";
            if (isClient) return "[Client]";
            return "[Shared]";
        }

        internal static void InfoAuto(string msg)
        {
            if (IsDiagnosticMessage(msg))
            {
                InfoAutoVerbose(msg);
                return;
            }

            string role = ResolveDynamicRole();
            string facts = ResolveDynamicFacts();
            _logger?.LogInfo($"[{role}] {facts} {NormalizeForOutput(msg)}");
        }

        internal static void InfoAutoVerbose(string msg)
        {
            if (!PluginLogPolicy.IsVerboseDiagnosticsEnabled) return;
            string role = ResolveDynamicRole();
            string facts = ResolveDynamicFacts();
            _logger?.LogInfo($"[{role}] {facts} {NormalizeForOutput(msg)}");
        }

        internal static void WarnAuto(string msg)
        {
            string role = ResolveDynamicRole();
            string facts = ResolveDynamicFacts();
            _logger?.LogWarning($"[{role}] {facts} {NormalizeForOutput(msg)}");
        }

        internal static void ErrorAuto(string msg)
        {
            string role = ResolveDynamicRole();
            string facts = ResolveDynamicFacts();
            _logger?.LogError($"[{role}] {facts} {NormalizeForOutput(msg)}");
        }

        private static string ResolveDynamicFacts()
        {
            bool isServer = false, isClient = false;
            bool isP2PHost = false;
            string hostMode = "Unknown";
            try { isServer = Provider.isServer; } catch { }
            try { isClient = Provider.isClient; } catch { }
            try
            {
                isP2PHost = HostManager.IsP2PHostMode;
                hostMode = HostManager.HostMode.ToString();
            }
            catch { }

            ulong steamId = 0;
            try
            {
                if (Steamworks.SteamUser.GetSteamID().m_SteamID != 0)
                {
                    steamId = Steamworks.SteamUser.GetSteamID().m_SteamID;
                }
            }
            catch { /* Steamworks 未初始化 */ }

            return $"[isServer={isServer} isClient={isClient} hostMode={hostMode} p2pHost={isP2PHost} steamId={steamId}]";
        }

        internal static bool IsDiagnosticMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;

            foreach (string marker in DiagnosticMarkers)
            {
                if (message.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        internal static string NormalizeForOutput(string message)
        {
            if (string.IsNullOrEmpty(message)) return message ?? string.Empty;

            string normalized = LegacyBracketTag.Replace(message, match =>
            {
                string subsystem = match.Groups[1].Value;
                return string.IsNullOrEmpty(subsystem) ? string.Empty : "[" + subsystem + "]";
            });
            normalized = LegacyToken.Replace(normalized, string.Empty);
            normalized = InternalDiagnosticBracketTag.Replace(normalized, string.Empty);
            normalized = InternalDiagnosticToken.Replace(normalized, "internal monitor");
            return RepeatedWhitespace.Replace(normalized, " ").Trim();
        }
    }
}
