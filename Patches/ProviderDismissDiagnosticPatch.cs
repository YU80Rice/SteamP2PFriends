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
    /// v0.2.3.2 P0-D D-8：Provider.dismiss + RemoveClient Prefix+Postfix 诊断 patch。
    ///
    /// v0.2.3.2 P0-7 修复（Codex 第三次审计）：
    ///   - session 清理从 dismiss Prefix 移到 Postfix（原 Prefix 先清理导致后续 RemoveClient 日志 sid=n/a）。
    ///   - RemoveClient Postfix 也调用 RemoveSession（双重保险，RemoveSession 幂等）。
    ///   - 所有日志使用 FormatPrefixFor 输出精确 sid。
    ///
    /// v0.2.3.1 P0-5：新增 reject/kick/refuse 原因记录（在 ProviderRejectDiagnosticPatch.cs）。
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
                // P0-7 修复：不在 Prefix 清理 session（否则后续 RemoveClient 日志 sid=n/a）
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
                // P0-7 修复：在 Postfix 清理 session（dismiss 完成后）
                DiagnosticContext.RemoveSession(steamID.m_SteamID);
                RoleLogger.Info("[Host]",
                    $"{DiagnosticContext.FormatPrefixFor(steamID.m_SteamID, "Provider.dismiss RETURNED")} " +
                    $"steamId={steamID.m_SteamID} session_removed=true");

                // P0-E 修复：清理 PlayerInitializationTracker
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

                // P0-7 修复：在 Postfix 清理 session（RemoveSession 幂等，dismiss Postfix 可能已清理）
                if (steamId != 0)
                {
                    DiagnosticContext.RemoveSession(steamId);
                }

                // P0-E 修复：清理 tracker
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
