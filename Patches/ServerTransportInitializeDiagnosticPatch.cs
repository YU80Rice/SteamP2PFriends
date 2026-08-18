using HarmonyLib;
using SDG.NetTransport.SteamNetworkingSockets;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using SteamP2PFriends.Shared.Enums;
using Steamworks;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    ///
    /// 目的：第一次双机测试发现 SteamUserP2PRedirectPatch 的 12 个 Prefix 日志从未打印，
    /// 怀疑 Harmony patch 登记了但运行时未执行。本 patch 直接拦截 Initialize 方法，
    /// 记录：
    ///   1. Initialize 是否真的被调用
    ///   2. 调用时的 HostMode
    ///   3. clUseP2pSocket/clUseIpSocket 命令行标志值（决定 vanilla 是否调 CreateListenSocketP2P）
    ///   4. Postfix 后 p2pListenSocket 字段值（判断 socket 是否创建成功）
    ///
    /// 如果 Prefix 日志打印但 SteamUserP2PRedirectPatch 的 Prefix 日志没打印，
    /// 说明 Harmony patch 了 Steamworks.NET 方法但运行时调用绕过了 detour。
    ///
    ///   - A-1：HSteamListenSocket.ToString() 返回数字字符串，Invalid=0 的 ToString 为 "0"，
    ///     原先的 p2pVal.ToString() != "Invalid" 判断永远为 true，导致 handle=0 被误报为已创建。
    ///     改为强类型比较 socket != HSteamListenSocket.Invalid，并输出数值 handle 和 valid 标志。
    ///   - A-2：CommandLineFlag 是类（引用类型），未覆盖 ToString，原直接插值输出类型名。
    ///     改为转换为 CommandLineFlag 并读取 .value 字段（bool）。
    /// </summary>
    [HarmonyPatch(typeof(ServerTransport_SteamNetworkingSockets), "Initialize")]
    public static class ServerTransportInitializeDiagnosticPatch
    {
        [HarmonyPrefix]
        public static void Prefix(object __instance)
        {
            RoleLogger.Info("[Host]",
                $"[Diag] ServerTransport.Initialize Prefix HostMode={HostManager.HostMode} instance={__instance?.GetType().Name}");

            try
            {
                FieldInfo clUseP2pSocketFi = AccessTools.Field(typeof(ServerTransport_SteamNetworkingSockets), "clUseP2pSocket");
                FieldInfo clUseIpSocketFi = AccessTools.Field(typeof(ServerTransport_SteamNetworkingSockets), "clUseIpSocket");

                if (clUseP2pSocketFi != null)
                {
                    object p2pFlagObj = clUseP2pSocketFi.GetValue(null);
                    object ipFlagObj = clUseIpSocketFi?.GetValue(null);

                    // A-2 修复：CommandLineFlag 是类，需读取 .value 字段
                    bool? p2pValue = (p2pFlagObj as CommandLineFlag)?.value;
                    bool? ipValue = (ipFlagObj as CommandLineFlag)?.value;

                    RoleLogger.Info("[Host]",
                        $"[Diag] clUseP2pSocket.value={p2pValue?.ToString() ?? "null"} " +
                        $"clUseIpSocket.value={ipValue?.ToString() ?? "null"} " +
                        $"(true=创建对应 socket，vanilla 默认两者都 true；" +
                        $"若 P2P=false 检查命令行 -SNS_DisableP2PSocket)");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Host]", $"[Diag] 读取 CommandLineFlag 异常（不阻断）: {ex.Message}");
            }
        }

        [HarmonyPostfix]
        public static void Postfix(object __instance)
        {
            try
            {
                FieldInfo p2pListenSocketFi = AccessTools.Field(typeof(ServerTransport_SteamNetworkingSockets), "p2pListenSocket");
                FieldInfo ipListenSocketFi = AccessTools.Field(typeof(ServerTransport_SteamNetworkingSockets), "ipListenSocket");

                object p2pVal = p2pListenSocketFi?.GetValue(__instance);
                object ipVal = ipListenSocketFi?.GetValue(__instance);

                // A-1 修复：HSteamListenSocket.ToString() 返回数字字符串（"0" 表示 Invalid）
                // 改为强类型比较并输出数值 handle 与 valid 标志
                HSteamListenSocket p2pSocket = (p2pVal is HSteamListenSocket s1) ? s1 : HSteamListenSocket.Invalid;
                HSteamListenSocket ipSocket = (ipVal is HSteamListenSocket s2) ? s2 : HSteamListenSocket.Invalid;
                bool p2pValid = p2pSocket != HSteamListenSocket.Invalid;
                bool ipValid = ipSocket != HSteamListenSocket.Invalid;

                RoleLogger.Info("[Host]",
                    $"[Diag] ServerTransport.Initialize Postfix " +
                    $"p2pListenSocket.handle={p2pSocket.m_HSteamListenSocket} valid={p2pValid} " +
                    $"ipListenSocket.handle={ipSocket.m_HSteamListenSocket} valid={ipValid} " +
                    $"(handle>0 表示创建成功，handle=0 表示未创建)");

                if (p2pValid)
                {
                    RoleLogger.Info("[Host]",
                        "[Diag] p2pListenSocket 已创建 (handle>0) -> vanilla 确实调用了 CreateListenSocketP2P。" +
                        "如果 [P2P-SteamUser] CreateListenSocketP2P 重定向 日志未打印，说明 Harmony detour 被绕过。");
                }
                else
                {
                    RoleLogger.Warn("[Host]",
                        "[Diag] p2pListenSocket=Invalid (handle=0) -> vanilla 未创建 P2P socket" +
                        "（clUseP2pSocket=false 或命令行 -SNS_DisableP2PSocket）。");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Host]", $"[Diag] Postfix 读取字段异常（不阻断）: {ex.Message}");
            }
        }
    }
}
