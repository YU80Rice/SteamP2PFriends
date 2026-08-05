using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using Steamworks;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.1 P0-D D-8 扩展：Provider.reject + kick + refuseGarbageConnection 诊断 patch。
    ///
    /// v0.2.3.1 P0-5 修复（审计要求补齐）：
    ///   - reject(CSteamID, ESteamRejection) 和 reject(CSteamID, ESteamRejection, string)
    ///   - reject(ITransportConnection, ESteamRejection) 和 reject(ITransportConnection, ESteamRejection, string)
    ///   - kick(CSteamID, string)
    ///   - refuseGarbageConnection(CSteamID, string) 和 refuseGarbageConnection(ITransportConnection, string)
    ///   - 记录 SteamID、rejection reason、explanation、调用栈
    /// </summary>
    public static class ProviderRejectDiagnosticPatch
    {
        public static void RegisterManual(Harmony harmony)
        {
            try
            {
                // reject(CSteamID, ESteamRejection)
                RegisterOne(harmony, typeof(Provider), "reject",
                    new System.Type[] { typeof(CSteamID), typeof(ESteamRejection) },
                    nameof(Reject_CSteamID_Prefix), null);

                // reject(CSteamID, ESteamRejection, string)
                RegisterOne(harmony, typeof(Provider), "reject",
                    new System.Type[] { typeof(CSteamID), typeof(ESteamRejection), typeof(string) },
                    nameof(Reject_CSteamID_Explanation_Prefix), null);

                // reject(ITransportConnection, ESteamRejection)
                RegisterOne(harmony, typeof(Provider), "reject",
                    new System.Type[] { typeof(SDG.NetTransport.ITransportConnection), typeof(ESteamRejection) },
                    nameof(Reject_Transport_Prefix), null);

                // reject(ITransportConnection, ESteamRejection, string)
                RegisterOne(harmony, typeof(Provider), "reject",
                    new System.Type[] { typeof(SDG.NetTransport.ITransportConnection), typeof(ESteamRejection), typeof(string) },
                    nameof(Reject_Transport_Explanation_Prefix), null);

                // kick(CSteamID, string)
                RegisterOne(harmony, typeof(Provider), "kick",
                    new System.Type[] { typeof(CSteamID), typeof(string) },
                    nameof(Kick_Prefix), null);

                // refuseGarbageConnection(CSteamID, string)
                RegisterOne(harmony, typeof(Provider), "refuseGarbageConnection",
                    new System.Type[] { typeof(CSteamID), typeof(string) },
                    nameof(Refuse_CSteamID_Prefix), null);

                // refuseGarbageConnection(ITransportConnection, string)
                RegisterOne(harmony, typeof(Provider), "refuseGarbageConnection",
                    new System.Type[] { typeof(SDG.NetTransport.ITransportConnection), typeof(string) },
                    nameof(Refuse_Transport_Prefix), null);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[ManualPatch] ProviderRejectDiagnosticPatch.RegisterManual 失败: {ex}");
            }
        }

        private static void RegisterOne(Harmony harmony, System.Type targetType, string methodName,
            System.Type[] paramTypes, string prefixName, string finalizerName)
        {
            try
            {
                System.Reflection.MethodInfo original = AccessTools.Method(targetType, methodName, paramTypes);
                if (original == null)
                {
                    RoleLogger.Warn("[Shared]",
                        $"[ManualPatch] !!! {targetType.Name}.{methodName}({paramTypes.Length} args): method not found");
                    return;
                }

                HarmonyMethod prefix = null;
                if (!string.IsNullOrEmpty(prefixName))
                {
                    System.Reflection.MethodInfo p = AccessTools.Method(typeof(ProviderRejectDiagnosticPatch), prefixName);
                    if (p != null) prefix = new HarmonyMethod(p);
                }

                harmony.Patch(original, prefix: prefix);

                HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
                int prefixCount = info?.Prefixes?.Count ?? 0;
                RoleLogger.Info("[Shared]",
                    $"[ManualPatch] OK {targetType.Name}.{methodName}({paramTypes.Length} args) 已登记 " +
                    $"(prefixes={prefixCount})");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[ManualPatch] !!! {targetType.Name}.{methodName} 注册异常: {ex}");
            }
        }

        public static void Reject_CSteamID_Prefix(CSteamID steamID, ESteamRejection rejection)
        {
            try
            {
                RoleLogger.Warn("[Host]",
                    $"{DiagnosticContext.FormatPrefixFor(steamID.m_SteamID, "Provider.reject ENTER")} " +
                    $"steamId={steamID.m_SteamID} rejection={rejection}({(int)rejection})");
            }
            catch { }
        }

        public static void Reject_CSteamID_Explanation_Prefix(CSteamID steamID, ESteamRejection rejection, string explanation)
        {
            try
            {
                RoleLogger.Warn("[Host]",
                    $"{DiagnosticContext.FormatPrefixFor(steamID.m_SteamID, "Provider.reject ENTER")} " +
                    $"steamId={steamID.m_SteamID} rejection={rejection}({(int)rejection}) explanation=\"{explanation}\"");
            }
            catch { }
        }

        public static void Reject_Transport_Prefix(SDG.NetTransport.ITransportConnection transportConnection, ESteamRejection rejection)
        {
            try
            {
                string transportType = transportConnection?.GetType().Name ?? "null";
                RoleLogger.Warn("[Host]",
                    $"{DiagnosticContext.FormatPrefix("Provider.reject ENTER")} " +
                    $"transport={transportType} rejection={rejection}({(int)rejection})");
            }
            catch { }
        }

        public static void Reject_Transport_Explanation_Prefix(SDG.NetTransport.ITransportConnection transportConnection, ESteamRejection rejection, string explanation)
        {
            try
            {
                string transportType = transportConnection?.GetType().Name ?? "null";
                RoleLogger.Warn("[Host]",
                    $"{DiagnosticContext.FormatPrefix("Provider.reject ENTER")} " +
                    $"transport={transportType} rejection={rejection}({(int)rejection}) explanation=\"{explanation}\"");
            }
            catch { }
        }

        public static void Kick_Prefix(CSteamID steamID, string reason)
        {
            try
            {
                RoleLogger.Warn("[Host]",
                    $"{DiagnosticContext.FormatPrefixFor(steamID.m_SteamID, "Provider.kick ENTER")} " +
                    $"steamId={steamID.m_SteamID} reason=\"{reason}\"");
            }
            catch { }
        }

        public static void Refuse_CSteamID_Prefix(CSteamID remoteId, string reason)
        {
            try
            {
                RoleLogger.Warn("[Host]",
                    $"{DiagnosticContext.FormatPrefixFor(remoteId.m_SteamID, "Provider.refuseGarbageConnection ENTER")} " +
                    $"steamId={remoteId.m_SteamID} reason=\"{reason}\"");
            }
            catch { }
        }

        public static void Refuse_Transport_Prefix(SDG.NetTransport.ITransportConnection transportConnection, string reason)
        {
            try
            {
                string transportType = transportConnection?.GetType().Name ?? "null";
                RoleLogger.Warn("[Host]",
                    $"{DiagnosticContext.FormatPrefix("Provider.refuseGarbageConnection ENTER")} " +
                    $"transport={transportType} reason=\"{reason}\"");
            }
            catch { }
        }
    }
}
