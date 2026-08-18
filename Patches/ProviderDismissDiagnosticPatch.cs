using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using Steamworks;
using System.Diagnostics;
using System.Text;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    ///
    ///   - session 清理从 dismiss Prefix 移到 Postfix（原 Prefix 先清理导致后续 RemoveClient 日志 sid=n/a）。
    ///   - RemoveClient Postfix 也调用 RemoveSession（双重保险，RemoveSession 幂等）。
    ///   - 所有日志使用 FormatPrefixFor 输出精确 sid。
    ///
    /// </summary>
    [HarmonyPatch(typeof(Provider), "dismiss")]
    public static class ProviderDismissDiagnosticPatch
    {
        [HarmonyPrefix]
        public static void Prefix(CSteamID steamID)
        {
            try
            {
                string stack = StackTraceHelper.CaptureShortStack();
                RoleLogger.Info("[Host]",
                    $"{DiagnosticContext.FormatPrefixFor(steamID.m_SteamID, "Provider.dismiss ENTER")} " +
                    $"steamId={steamID.m_SteamID} stack=\n{stack}");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Host]", $"[Diag] dismiss Prefix 异常（不阻断）: {ex.Message}");
            }
        }

        [HarmonyPostfix]
        public static void Postfix(CSteamID steamID)
        {
            try
            {
                DiagnosticContext.RemoveSession(steamID.m_SteamID);
                RoleLogger.Info("[Host]",
                    $"{DiagnosticContext.FormatPrefixFor(steamID.m_SteamID, "Provider.dismiss RETURNED")} " +
                    $"steamId={steamID.m_SteamID} session_removed=true");

                if (Provider.clients != null)
                {
                    foreach (SteamPlayer sp in Provider.clients)
                    {
                        if (ReferenceEquals(sp, null) || ReferenceEquals(sp.playerID, null)) continue;
                        if (sp.playerID.steamID == steamID && sp.player != null)
                        {
                            PlayerInitializationTracker.Remove(sp.player);
                            break;
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Host]", $"[Diag] dismiss Postfix 异常（不阻断）: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(Provider), "RemoveClient")]
    public static class ProviderRemoveClientDiagnosticPatch
    {
        [HarmonyPrefix]
        public static void Prefix(SteamPlayer clientToRemove)
        {
            try
            {
                ulong steamId = 0;
                string name = "n/a";
                if (!ReferenceEquals(clientToRemove, null) && !ReferenceEquals(clientToRemove.playerID, null))
                {
                    steamId = clientToRemove.playerID.steamID.m_SteamID;
                    name = clientToRemove.playerID.playerName ?? "n/a";
                }

                string stack = StackTraceHelper.CaptureShortStack();
                RoleLogger.Info("[Host]",
                    $"{DiagnosticContext.FormatPrefixFor(steamId, "Provider.RemoveClient ENTER")} " +
                    $"steamId={steamId} name=\"{name}\" " +
                    $"clients_before={Provider.clients?.Count ?? -1} stack=\n{stack}");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Host]", $"[Diag] RemoveClient Prefix 异常（不阻断）: {ex.Message}");
            }
        }

        [HarmonyPostfix]
        public static void Postfix(SteamPlayer clientToRemove)
        {
            try
            {
                ulong steamId = 0;
                if (!ReferenceEquals(clientToRemove, null) && !ReferenceEquals(clientToRemove.playerID, null))
                {
                    steamId = clientToRemove.playerID.steamID.m_SteamID;
                }

                if (steamId != 0)
                {
                    DiagnosticContext.RemoveSession(steamId);
                }

                if (!ReferenceEquals(clientToRemove, null) && clientToRemove.player != null)
                {
                    PlayerInitializationTracker.Remove(clientToRemove.player);
                }

                RoleLogger.Info("[Host]",
                    $"{DiagnosticContext.FormatPrefixFor(steamId, "Provider.RemoveClient RETURNED")} " +
                    $"steamId={steamId} clients_after={Provider.clients?.Count ?? -1}");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Host]", $"[Diag] RemoveClient Postfix 异常（不阻断）: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 辅助：捕获前 8 帧调用栈，跳过 Harmony 内部帧。
    /// </summary>
    internal static class StackTraceHelper
    {
        public static string CaptureShortStack(int maxFrames = 10)
        {
            try
            {
                StackTrace st = new StackTrace(2, false);
                StackFrame[] frames = st.GetFrames();
                if (frames == null) return "<no frames>";

                StringBuilder sb = new StringBuilder();
                int count = 0;
                foreach (StackFrame f in frames)
                {
                    if (count >= maxFrames) break;
                    System.Reflection.MethodBase m = f.GetMethod();
                    if (m == null) continue;
                    string declType = m.DeclaringType?.Name ?? "?";
                    sb.Append("  at ").Append(declType).Append('.').Append(m.Name).Append(" (")
                      .Append(f.GetFileName() ?? "?").Append(':').Append(f.GetFileLineNumber()).AppendLine(")");
                    count++;
                }
                return sb.ToString();
            }
            catch
            {
                return "<stack capture failed>";
            }
        }
    }
}
