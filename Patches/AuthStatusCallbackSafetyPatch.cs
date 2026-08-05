using HarmonyLib;
using SDG.NetTransport.SteamNetworkingSockets;
using SteamP2PFriends.Shared;
using Steamworks;
using System;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.8 P0-B 修复（审计 v0.2.3.7 报告 Critical-2）：
    ///   封闭 vanilla auth status callback 原始 m_debugMsg 写入 Player.log 的路径。
    ///
    /// 背景：
    ///   vanilla ServerTransport_SteamNetworkingSockets.OnSteamNetAuthenticationStatusChanged 与
    ///   ClientTransport_SteamNetworkingSockets.OnSteamNetAuthenticationStatusChanged 在 callback.m_debugMsg
    ///   非空时直接以 `Log("Readiness ... {0} \"{1}\"", m_eAvail, m_debugMsg)` 形式写入日志。
    ///   由于本插件 CallbackCreateGameServerRedirectPatch 已将 CreateGameServer 重定向到 Create（SteamUser 管道），
    ///   该 callback 在 P2P 模式下确实会被触发，原始 m_debugMsg 会进入 Unity Player.log。
    ///
    ///   v0.2.3.7 返修报告曾以 "Steam 决定的 m_debugMsg 不含用户敏感输入" 作为安全保证，
    ///   但未提供代码事实或运行证据。审计 v0.2.3.7 报告 Critical-2 明确要求封闭此路径。
    ///
    /// 实现策略（审计 v0.2.3.7 报告 Critical-2 "最小修正" 方案 1）：
    ///   1. Prefix patch 双端 OnSteamNetAuthenticationStatusChanged
    ///   2. 在 Prefix 中读取原始 m_debugMsg 并通过 RedactSensitiveNetworkData 脱敏后
    ///      以插件自己的日志格式输出（含 m_eAvail + 脱敏后的 m_debugMsg）
    ///   3. 通过 property setter 把 callback.m_debugMsg 替换为安全占位符字符串
    ///      （setter 内部调用 InteropHelp.StringToByteArrayUTF8 写入共享 byte 数组，
    ///       vanilla handler 后续读取时得到的是这个占位符）
    ///   4. 不修改 m_eAvail 字段，vanilla ClientTransport.HandleAuth(m_eAvail) 行为不受影响
    ///   5. vanilla Log 调用仍会执行，但写入的 m_debugMsg 已是占位符
    ///
    /// 安全保证：
    ///   - callback 是 struct（值类型），Prefix 中看到的是副本；但 m_debugMsg_ 是 byte[] 引用类型字段，
    ///     副本与原始 struct 中的 m_debugMsg_ 指向同一个 byte 数组对象
    ///   - property setter 通过 InteropHelp.StringToByteArrayUTF8(value, m_debugMsg_, 256) 直接写入该 byte 数组
    ///   - 因此 Prefix 中调用 setter 修改的内容对 vanilla 方法可见
    ///
    /// 不修改：
    ///   - m_eAvail 字段（vanilla HandleAuth 依赖）
    ///   - vanilla Log 调用本身（仍执行，但参数已脱敏）
    ///   - callback 触发时序
    /// </summary>
    [HarmonyPatch(typeof(ServerTransport_SteamNetworkingSockets), "OnSteamNetAuthenticationStatusChanged")]
    public static class ServerAuthStatusCallbackSafetyPatch
    {
        private const string RedactedPlaceholder = "[REDACTED-AUTH-STATUS]";

        [HarmonyPrefix]
        private static void Prefix(SteamNetAuthenticationStatus_t callback)
        {
            try
            {
                // 读取原始 m_debugMsg（getter 内部调用 InteropHelp.ByteArrayToStringUTF8 解码 byte 数组）
                string rawDebug = callback.m_debugMsg ?? "";
                string redactedDebug = SnsDiagnosticUtil.RedactSensitiveNetworkData(rawDebug);

                // 插件自己输出一行脱敏后的 readiness snapshot（含 m_eAvail + 脱敏 m_debugMsg）
                //   替代 vanilla Log 中可能含敏感内容的那一行
                RoleLogger.Info("[Host]",
                    $"[Diag] [D-Auth-Callback] ServerTransport.OnSteamNetAuthenticationStatusChanged " +
                    $"m_eAvail={callback.m_eAvail}({(int)callback.m_eAvail}) " +
                    $"m_debugMsg=\"{redactedDebug}\"");

                // 通过 property setter 替换 m_debugMsg 内容
                //   setter 调用 InteropHelp.StringToByteArrayUTF8(value, m_debugMsg_, 256)
                //   写入共享 byte 数组，vanilla handler 后续读取时得到的是占位符
                //   不修改 m_eAvail 字段，vanilla 无其他副作用（ServerTransport 不调 HandleAuth）
                try
                {
                    callback.m_debugMsg = RedactedPlaceholder;
                }
                catch (Exception ex)
                {
                    // setter 异常时不阻断 vanilla callback（m_eAvail 仍可读）
                    //   但记录警告：m_debugMsg 可能仍含原始内容
                    RoleLogger.Warn("[Host]",
                        $"[Diag] [D-Auth-Callback] ServerTransport m_debugMsg setter 异常（vanilla Log 可能仍含原始内容）: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]",
                    $"[Diag] [D-Auth-Callback] ServerTransport Prefix 异常（不阻断 vanilla，但 m_debugMsg 可能泄漏到 Player.log）: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(ClientTransport_SteamNetworkingSockets), "OnSteamNetAuthenticationStatusChanged")]
    public static class ClientAuthStatusCallbackSafetyPatch
    {
        private const string RedactedPlaceholder = "[REDACTED-AUTH-STATUS]";

        [HarmonyPrefix]
        private static void Prefix(SteamNetAuthenticationStatus_t callback)
        {
            try
            {
                string rawDebug = callback.m_debugMsg ?? "";
                string redactedDebug = SnsDiagnosticUtil.RedactSensitiveNetworkData(rawDebug);

                RoleLogger.Info("[Client]",
                    $"[Diag] [D-Auth-Callback] ClientTransport.OnSteamNetAuthenticationStatusChanged " +
                    $"m_eAvail={callback.m_eAvail}({(int)callback.m_eAvail}) " +
                    $"m_debugMsg=\"{redactedDebug}\"");

                // 通过 property setter 替换 m_debugMsg 内容
                //   vanilla ClientTransport.OnSteamNetAuthenticationStatusChanged 在 Log 后还会调
                //   HandleAuth(callback.m_eAvail)，HandleAuth 只读 m_eAvail 不读 m_debugMsg，
                //   所以替换 m_debugMsg 不影响 HandleAuth 行为
                try
                {
                    callback.m_debugMsg = RedactedPlaceholder;
                }
                catch (Exception ex)
                {
                    RoleLogger.Warn("[Client]",
                        $"[Diag] [D-Auth-Callback] ClientTransport m_debugMsg setter 异常（vanilla Log 可能仍含原始内容）: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Client]",
                    $"[Diag] [D-Auth-Callback] ClientTransport Prefix 异常（不阻断 vanilla，但 m_debugMsg 可能泄漏到 Player.log）: {ex.Message}");
            }
        }
    }
}
