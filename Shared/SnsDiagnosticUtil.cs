using SteamP2PFriends.Shared;
using Steamworks;
using System;
using System.Text;
using System.Text.RegularExpressions;

namespace SteamP2PFriends.Shared
{
    /// <summary>
    /// v0.2.3.7 P0-A/P0-B/P0-E/P0-2/P0-3/P0-5 共享 SNS 诊断工具（Codex 第七次审计返修）。
    ///
    /// v0.2.3.7 修订（审计 v0.2.3.6 报告 Critical-2/High-2/High-3）：
    ///   - P0-2：脱敏方法扩展覆盖范围到 11+5 类（新增 JSON secret key:value / HTTP(S) URL / 裸 FQDN）。
    ///     secret payload 字符集扩展到 base64url（- 和 _）+ 任意非空白非引号非括号字符。
    ///     fail-closed residual scan 扩展到 hostname/URI/secret-key/长 base64url，不只 IP。
    ///     endDebug 强制走统一脱敏入口，不再绕过。
    ///     新增 6 项审计 Critical-2 合成输入为回归测试用例。
    ///     失败日志只记 case 名，不打印 needle/redacted output（避免泄漏）。
    ///   - P0-3：RunRedactionSelfTest 返回 bool，异常视为失败，调用方聚合到 DiagnosticBuildValid 阻断门。
    ///   - P0-5：SnapshotRelayAuthReadiness 同时记录 API 返回值（relayAvail/authAvail）与 struct m_eAvail。
    ///   - P0-B（保留）：GetAuthenticationStatus 调用 + m_debugMsg 脱敏输出 + 终态 Prefix 触发 readiness。
    ///   - P0-E（保留）：GetDetailedConnectionStatus 正数返回时按所需长度重试一次（上限 256 KiB）。
    ///
    /// v0.2.3.6 保留修订（审计 v0.2.3.5 验收报告 P0-A/P0-B/P0-E）：
    ///   - 脱敏方法改名 RedactSensitiveNetworkData。
    ///   - 终态 Prefix 同步触发 relay/auth readiness snapshot。
    ///
    /// 严格禁止：
    ///   - 修改 ICE/SDR/认证配置
    ///   - 强制中继
    ///   - Patch GetConfigValue 本身
    ///   - 完整落盘 STUN/TURN 地址、本地/公网候选地址、ticket/cert 内容
    ///
    /// 所有日志只读、不修改任何 SNS 状态。
    /// </summary>
    public static class SnsDiagnosticUtil
    {
        // P0-E：Detailed status 重试上限（256 KiB）
        private const int DetailedStatusMaxRetryBytes = 256 * 1024;
        private const int DetailedStatusInitialBufferBytes = 32768;

        // P0-A：脱敏正则（顺序敏感：先处理多行块，再处理 candidate 行，最后处理单点地址）
        private static readonly Regex _pemCertBlock = new Regex(
            @"-----BEGIN CERTIFICATE-----.*?-----END CERTIFICATE-----",
            RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex _pemPrivateKeyBlock = new Regex(
            @"-----BEGIN (?:RSA |EC |DSA |OPENSSH |ENCRYPTED )?PRIVATE KEY-----.*?-----END (?:RSA |EC |DSA |OPENSSH |ENCRYPTED )?PRIVATE KEY-----",
            RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex _pemGenericBlock = new Regex(
            @"-----BEGIN ([A-Z0-9 ]+)-----.*?-----END \1-----",
            RegexOptions.Singleline | RegexOptions.Compiled);

        // ICE/SDP candidate 行：candidate:<ufrag> <comp> <proto> <prio> <addr> <port> [typ <type>] [raddr <addr> rport <port>] [generation <n>]
        private static readonly Regex _iceCandidateLine = new Regex(
            @"candidate:[^\s\r\n]+ \d+ (udp|tcp|tls) \d+ \S+ \d+(?: typ \w+(?: raddr \S+ rport \d+)?(?: generation \d+)?)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // STUN/TURN URL：stun:host[:port]、turn:host[:port][?transport=...]
        private static readonly Regex _stunTurnUrl = new Regex(
            @"(?:stun|turn|stuns|turns):[^\s\r\n\]\""]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // 括号 IPv6（带或不带端口）：[2001:db8::1]:port 或 [::1]
        private static readonly Regex _bracketedIpv6 = new Regex(
            @"\[[0-9a-fA-F:]+\](?::\d{1,5})?",
            RegexOptions.Compiled);

        // IPv4-mapped IPv6：::ffff:1.2.3.4 或 ::ffff:1.2.3.4:port
        private static readonly Regex _ipv4MappedIpv6 = new Regex(
            @"::ffff:\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}(?::\d{1,5})?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // 压缩 IPv6（含 ::）：2001:db8::1、::1、fe80::1
        private static readonly Regex _compressedIpv6 = new Regex(
            @"(?<![0-9a-fA-F:])[0-9a-fA-F]{0,4}(?::[0-9a-fA-F]{0,4}){0,6}::[0-9a-fA-F]{0,4}(?::[0-9a-fA-F]{0,4}){0,6}(?::\d{1,5})?(?![0-9a-fA-F:])",
            RegexOptions.Compiled);

        // 全长 IPv6（8 段）：2001:0db8:0000:0000:0000:0000:0000:0001
        private static readonly Regex _fullIpv6 = new Regex(
            @"(?<![0-9a-fA-F:])[0-9a-fA-F]{1,4}(?::[0-9a-fA-F]{1,4}){7}(?::\d{1,5})?(?![0-9a-fA-F:])",
            RegexOptions.Compiled);

        // IPv4 + optional port
        private static readonly Regex _ipv4WithPort = new Regex(
            @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}(?::\d{1,5})?",
            RegexOptions.Compiled);

        // hostname:port（限定至少一个点，避免误匹配普通单词）
        private static readonly Regex _hostnameWithPort = new Regex(
            @"\b[a-zA-Z0-9][a-zA-Z0-9\-]{0,62}(?:\.[a-zA-Z0-9][a-zA-Z0-9\-]{0,62})+:\d{1,5}\b",
            RegexOptions.Compiled);

        // ticket/cert/signature/auth token 关键字 + 后续非空 payload（>=8 字符）
        // v0.2.3.7 P0-2 修复（审计 Critical-2）：扩展 payload 字符集到 base64url（- 和 _）+ 任意非空白非引号非括号字符
        //   旧正则 [A-Za-z0-9+/=]{8,} 不接受 - 和 _，导致 ticket=abc_def-12345 / cert: abc-def_123456 泄漏
        //   新正则 [^\s"'<>\[\]{}]{8,} 接受 base64url 字符及任意非空白 token，避免误匹配引号/括号
        private static readonly Regex _secretKeywordPayload = new Regex(
            @"(?i)\b(ticket|cert|certificate|signature|auth_token|authToken|authTicket|sessionTicket|cauth|privkey|private_key)\b\s*[:=]\s*[^\s""'<>\[\]{}]{8,}",
            RegexOptions.Compiled);

        // v0.2.3.7 P0-2 新增（审计 Critical-2），v0.2.3.8 P0-A 修订（审计 v0.2.3.7 Critical-1）：
        //   JSON 引号包围的 secret key:value
        //   覆盖 {"ticket":"abc_def-12345"} / {"cert":"abc-def_123456"} / {"ticket":"short"} 等格式
        //   v0.2.3.7 旧正则 [^"]{8,} 要求 value >= 8 字符，导致 {"ticket":"short"} 等短 value 泄漏
        //   v0.2.3.8 修改：移除 {8,} 长度限制；检测到高风险 key 就整段脱敏（审计明确要求）
        //   empty value 也允许匹配（避免 {"ticket":""} 仍能识别 key），但实际脱敏只对非空 value 有意义
        private static readonly Regex _jsonSecretKeyValue = new Regex(
            @"""(?:ticket|cert|certificate|signature|auth_token|authToken|authTicket|sessionTicket|cauth|privkey|private_key)""\s*:\s*""[^""]*""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // v0.2.3.7 P0-2 新增（审计 Critical-2）：HTTP(S) URL
        //   覆盖 peer=https://hidden.example.org/path 等格式
        private static readonly Regex _httpUrl = new Regex(
            @"\bhttps?://[^\s\r\n""'<>{}\[\]]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // v0.2.3.8 P0-A 修订（审计 v0.2.3.7 Critical-1）：裸 FQDN（不带端口）
        //   v0.2.3.7 限制 TLD 为 2-6 字符纯小写字母，导致以下输入泄漏：
        //     - SERVER.EXAMPLE.COM（大写）
        //     - foo.technology（TLD 10 字符）
        //     - 123node.local（数字开头 label）
        //   v0.2.3.8 修改：
        //     - 大小写不敏感（IgnoreCase）
        //     - TLD 最长 63 字符（DNS label 上限），要求至少含一个字母（避免匹配版本号 1.2.3）
        //     - 允许数字开头 label（合法 hostname 如 123node.local）
        //   误脱敏（如 System.IO.File）比泄漏更可接受（审计明确允许）
        private static readonly Regex _bareFqdn = new Regex(
            @"\b[a-zA-Z0-9][a-zA-Z0-9\-]{0,62}(?:\.[a-zA-Z0-9][a-zA-Z0-9\-]{0,62})*\.(?=[a-zA-Z0-9\-]{1,63}\b)[a-zA-Z0-9\-]*[a-zA-Z][a-zA-Z0-9\-]*\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // v0.2.3.8 P0-A 新增（审计 v0.2.3.7 Critical-1）：单标签 hostname:port（无点）
        //   覆盖 endpoint=LANHOST:27015 等内网单标签 hostname:port 形式
        //   必须在 _hostnameWithPort 之后运行（多点 hostname:port 已被脱敏）
        //   误脱敏（如代码 Method:80）比泄漏更可接受
        private static readonly Regex _singleLabelHostPort = new Regex(
            @"\b[a-zA-Z][a-zA-Z0-9\-]{2,}:\d{1,5}\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // base64 长串（>= 32 字符，可能为 cert/ticket 编码内容）
        // v0.2.3.7 P0-2 修复：扩展字符集到 base64url（- 和 _）
        private static readonly Regex _longBase64 = new Regex(
            @"[A-Za-z0-9+/=_\-]{32,}={0,2}",
            RegexOptions.Compiled);

        // fail-closed 检查：脱敏后再扫一次，若仍含疑似敏感内容则只记稳定摘要
        // v0.2.3.7 P0-2 修复（审计 Critical-2）：residual scan 扩展到 hostname/URI/secret-key/长 base64url，不只 IP
        private static readonly Regex _residualIpv4 = new Regex(
            @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b",
            RegexOptions.Compiled);
        private static readonly Regex _residualIpv6 = new Regex(
            @"(?<![0-9a-fA-F:])[0-9a-fA-F]{1,4}(?::[0-9a-fA-F]{1,4}){2,7}(?![0-9a-fA-F:])",
            RegexOptions.Compiled);
        // v0.2.3.7 新增，v0.2.3.8 P0-A 修订（审计 v0.2.3.7 Critical-1）：residual 裸 FQDN
        //   v0.2.3.7 旧正则限制 TLD 为 2-6 字符纯小写字母，与 _bareFqdn 不一致，导致 SERVER.EXAMPLE.COM 等残留仍能绕过 fail-closed
        //   v0.2.3.8 修改：与 _bareFqdn 完全一致（IgnoreCase + TLD 最长 63 字符 + 至少含一个字母 + 数字开头 label）
        private static readonly Regex _residualFqdn = new Regex(
            @"\b[a-zA-Z0-9][a-zA-Z0-9\-]{0,62}(?:\.[a-zA-Z0-9][a-zA-Z0-9\-]{0,62})*\.(?=[a-zA-Z0-9\-]{1,63}\b)[a-zA-Z0-9\-]*[a-zA-Z][a-zA-Z0-9\-]*\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // v0.2.3.7 新增：residual HTTP(S) URL 协议头
        private static readonly Regex _residualHttpUrl = new Regex(
            @"\bhttps?://",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // v0.2.3.7 新增：residual secret keyword + 分隔符（payload 可能已被部分脱敏，但 key 仍残留）
        private static readonly Regex _residualSecretKeyword = new Regex(
            @"(?i)\b(ticket|cert|certificate|signature|auth_token|authToken|authTicket|sessionTicket|cauth|privkey|private_key)\b\s*[:=]");
        // v0.2.3.7 新增：residual 长 base64url 串（>= 32 字符，含 - 和 _）
        private static readonly Regex _residualLongBase64Url = new Regex(
            @"[A-Za-z0-9+/=_\-]{32,}={0,2}",
            RegexOptions.Compiled);
        // v0.2.3.8 P0-A 新增（审计 v0.2.3.7 Critical-1）：residual 单标签 hostname:port（与 _singleLabelHostPort 一致）
        //   覆盖 LANHOST:27015 等内网单标签 hostname:port 残留检测
        private static readonly Regex _residualSingleLabelHostPort = new Regex(
            @"\b[a-zA-Z][a-zA-Z0-9\-]{2,}:\d{1,5}\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// P0-2：在 handle 关闭前抓取终态。
        /// 在 OnSteamNetConnectionStatusChanged Prefix 中调用，handle 仍有效。
        /// v0.2.3.6 P0-B：终态 Prefix 同步触发 relay/auth readiness snapshot。
        /// </summary>
        public static void SnapshotTerminalState(string role, string transportLabel, HSteamNetConnection handle,
            SteamNetConnectionStatusChangedCallback_t callback)
        {
            try
            {
                ESteamNetworkingConnectionState newState = callback.m_info.m_eState;
                bool isTerminal = newState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer
                    || newState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally;
                if (!isTerminal) return;

                StringBuilder sb = new StringBuilder(1024);
                sb.Append("[Diag] [D10-Term] ").Append(transportLabel).Append(" TERMINAL Prefix snapshot ");
                sb.Append("handle=").Append(handle.m_HSteamNetConnection).Append(' ');

                int endReason = callback.m_info.m_eEndReason;
                // v0.2.3.7 P0-2 修复（审计 Critical-2）：endDebug 必须走统一脱敏入口
                //   旧实现直接拼入日志，绕过脱敏，可能泄漏 hostname/ticket/cert 等敏感内容
                string endDebugRaw = callback.m_info.m_szEndDebug ?? "<empty>";
                string endDebug = RedactSensitiveNetworkData(endDebugRaw);
                int flags = callback.m_info.m_nFlags;
                ulong remoteSteamId = 0;
                try
                {
                    remoteSteamId = callback.m_info.m_identityRemote.GetSteamID64();
                }
                catch { }
                int popRelay = (int)(uint)callback.m_info.m_idPOPRelay;
                int popRemote = (int)(uint)callback.m_info.m_idPOPRemote;

                bool relayed = (flags & Constants.k_nSteamNetworkConnectionInfoFlags_Relayed) != 0;
                bool fast = (flags & Constants.k_nSteamNetworkConnectionInfoFlags_Fast) != 0;
                bool unauth = (flags & Constants.k_nSteamNetworkConnectionInfoFlags_Unauthenticated) != 0;
                bool unenc = (flags & Constants.k_nSteamNetworkConnectionInfoFlags_Unencrypted) != 0;
                bool loopback = (flags & Constants.k_nSteamNetworkConnectionInfoFlags_LoopbackBuffers) != 0;

                sb.Append("remote=").Append(remoteSteamId).Append(' ');
                sb.Append("state=").Append(newState).Append(' ');
                sb.Append("endReason=").Append(endReason).Append('(').Append((ESteamNetConnectionEnd)endReason).Append(") ");
                sb.Append("endDebug=\"").Append(endDebug).Append("\" ");
                sb.Append("flags=0x").Append(flags.ToString("X2")).Append(' ');
                sb.Append("relayed=").Append(relayed).Append(" fast=").Append(fast).Append(' ');
                sb.Append("unauth=").Append(unauth).Append(" unenc=").Append(unenc).Append(' ');
                sb.Append("loopback=").Append(loopback).Append(' ');
                sb.Append("popRelay=").Append(popRelay).Append(" popRemote=").Append(popRemote);

                RoleLogger.Info(role, sb.ToString());

                // P0-B：终态 Prefix 同步触发 relay/auth readiness snapshot（审计 v0.2.3.5 验收报告 High-1）
                try
                {
                    SnapshotRelayAuthReadiness(role, $"Terminal-Prefix-handle{handle.m_HSteamNetConnection}");
                }
                catch (Exception ex)
                {
                    RoleLogger.Warn(role, $"[Diag] [D10-Term] Terminal Prefix readiness snapshot 异常（不阻断）: {ex.Message}");
                }

                // P0-2：GetConnectionRealTimeStatus（handle 仍有效）
                try
                {
                    SteamNetConnectionRealTimeStatus_t rt = default(SteamNetConnectionRealTimeStatus_t);
                    SteamNetConnectionRealTimeLaneStatus_t lane = default(SteamNetConnectionRealTimeLaneStatus_t);
                    EResult rtResult = SteamNetworkingSockets.GetConnectionRealTimeStatus(handle, ref rt, 0, ref lane);
                    if (rtResult == EResult.k_EResultOK)
                    {
                        RoleLogger.Info(role,
                            $"[Diag] [D10-Term] RealTimeStatus handle={handle.m_HSteamNetConnection} " +
                            $"state={rt.m_eState} ping={rt.m_nPing}ms " +
                            $"qLocal={rt.m_flConnectionQualityLocal:F2} qRemote={rt.m_flConnectionQualityRemote:F2} " +
                            $"outPkt={rt.m_flOutPacketsPerSec:F1}/s outBytes={rt.m_flOutBytesPerSec:F0}/s " +
                            $"inPkt={rt.m_flInPacketsPerSec:F1}/s inBytes={rt.m_flInBytesPerSec:F0}/s " +
                            $"sendRate={rt.m_nSendRateBytesPerSecond}/s " +
                            $"pendingUnrel={rt.m_cbPendingUnreliable} pendingRel={rt.m_cbPendingReliable} " +
                            $"sentUnackedRel={rt.m_cbSentUnackedReliable}");
                    }
                    else
                    {
                        RoleLogger.Warn(role,
                            $"[Diag] [D10-Term] GetConnectionRealTimeStatus NOT OK handle={handle.m_HSteamNetConnection} EResult={rtResult}({(int)rtResult})");
                    }
                }
                catch (Exception ex)
                {
                    RoleLogger.Warn(role, $"[Diag] [D10-Term] RealTimeStatus 异常（不阻断）: {ex.Message}");
                }

                // P0-2 + P0-E：GetDetailedConnectionStatus（含正数返回重试）
                SnapshotDetailedConnectionStatus(role, handle);
            }
            catch (Exception ex)
            {
                RoleLogger.Warn(role, $"[Diag] [D10-Term] SnapshotTerminalState 异常（不阻断）: {ex.Message}");
            }
        }

        /// <summary>
        /// P0-2 + P0-E：抓取 GetDetailedConnectionStatus。
        /// 返回值语义（Steamworks.NET 文档）：
        ///   0  = 成功，buffer 已填入 details
        ///   -1 = invalid handle（handle 已关闭）
        ///   正数 = 所需 buffer 大小，当前 buffer 不够，需按返回值重试
        /// v0.2.3.6 P0-E：正数返回时按所需长度（加上终止符余量）重试一次，上限 256 KiB。
        /// 所有最终正文必须经过 RedactSensitiveNetworkData 脱敏。
        /// </summary>
        public static void SnapshotDetailedConnectionStatus(string role, HSteamNetConnection handle)
        {
            try
            {
                int firstResult;
                string firstDetails = null;
                try
                {
                    firstResult = SteamNetworkingSockets.GetDetailedConnectionStatus(handle, out firstDetails, DetailedStatusInitialBufferBytes);
                }
                catch (Exception ex)
                {
                    RoleLogger.Warn(role, $"[Diag] [D10-Term] DetailedConnectionStatus 调用异常: {ex.Message}");
                    return;
                }

                // -1：invalid handle
                if (firstResult == -1)
                {
                    RoleLogger.Warn(role,
                        $"[Diag] [D10-Term] DetailedConnectionStatus INVALID handle={handle.m_HSteamNetConnection} result=-1 (handle already closed)");
                    return;
                }

                // 0：成功
                if (firstResult == 0 && !string.IsNullOrEmpty(firstDetails))
                {
                    EmitDetailedStatus(role, handle, firstDetails, firstResult, retried: false);
                    return;
                }

                // 0 但空字符串：视为无效
                if (firstResult == 0)
                {
                    RoleLogger.Warn(role,
                        $"[Diag] [D10-Term] DetailedConnectionStatus EMPTY handle={handle.m_HSteamNetConnection} result=0");
                    return;
                }

                // 正数：buffer 不够，按所需大小重试一次（加上 16 字节终止符余量，上限 256 KiB）
                int requiredBytes = firstResult + 16;
                if (requiredBytes > DetailedStatusMaxRetryBytes)
                {
                    RoleLogger.Warn(role,
                        $"[Diag] [D10-Term] DetailedConnectionStatus SHORT handle={handle.m_HSteamNetConnection} " +
                        $"required={firstResult} exceeds cap={DetailedStatusMaxRetryBytes}，仅记录所需长度不重试");
                    return;
                }

                int retryResult;
                string retryDetails = null;
                try
                {
                    retryResult = SteamNetworkingSockets.GetDetailedConnectionStatus(handle, out retryDetails, requiredBytes);
                }
                catch (Exception ex)
                {
                    RoleLogger.Warn(role,
                        $"[Diag] [D10-Term] DetailedConnectionStatus retry 异常 handle={handle.m_HSteamNetConnection}: {ex.Message}");
                    return;
                }

                if (retryResult == 0 && !string.IsNullOrEmpty(retryDetails))
                {
                    EmitDetailedStatus(role, handle, retryDetails, firstResult, retried: true, retryResult: retryResult);
                }
                else
                {
                    RoleLogger.Warn(role,
                        $"[Diag] [D10-Term] DetailedConnectionStatus retry 仍失败 handle={handle.m_HSteamNetConnection} " +
                        $"firstResult={firstResult} retryResult={retryResult}");
                }
            }
            catch (Exception ex)
            {
                RoleLogger.Warn(role, $"[Diag] [D10-Term] SnapshotDetailedConnectionStatus 异常: {ex.Message}");
            }
        }

        private static void EmitDetailedStatus(string role, HSteamNetConnection handle,
            string details, int firstResult, bool retried, int retryResult = 0)
        {
            string redacted = RedactSensitiveNetworkData(details);
            string oneLine = redacted.Replace("\r", " ").Replace("\n", " | ");
            string tag = retried
                ? $"retried(firstResult={firstResult}, retryResult={retryResult})"
                : $"first(result={firstResult})";
            RoleLogger.Info(role,
                $"[Diag] [D10-Term] DetailedConnectionStatus handle={handle.m_HSteamNetConnection} {tag}: {oneLine}");
        }

        /// <summary>
        /// P0-2/P0-3：在连接尚未终态时读取一次连接快照（用于生命周期 tracker）。
        /// </summary>
        public static void SnapshotLiveState(string role, string transportLabel, HSteamNetConnection handle, string phaseTag)
        {
            try
            {
                SteamNetConnectionInfo_t info;
                if (!SteamNetworkingSockets.GetConnectionInfo(handle, out info))
                {
                    RoleLogger.Warn(role,
                        $"[Diag] [D10-Life] GetConnectionInfo NOT OK handle={handle.m_HSteamNetConnection} phase={phaseTag}");
                    return;
                }

                ulong remoteSteamId = 0;
                try { remoteSteamId = info.m_identityRemote.GetSteamID64(); } catch { }

                int flags = info.m_nFlags;
                bool relayed = (flags & Constants.k_nSteamNetworkConnectionInfoFlags_Relayed) != 0;
                bool fast = (flags & Constants.k_nSteamNetworkConnectionInfoFlags_Fast) != 0;

                int ping = -1;
                float qLocal = -1f, qRemote = -1f;
                int sendRate = -1;
                try
                {
                    SteamNetConnectionRealTimeStatus_t rt = default(SteamNetConnectionRealTimeStatus_t);
                    SteamNetConnectionRealTimeLaneStatus_t lane = default(SteamNetConnectionRealTimeLaneStatus_t);
                    EResult rtResult = SteamNetworkingSockets.GetConnectionRealTimeStatus(handle, ref rt, 0, ref lane);
                    if (rtResult == EResult.k_EResultOK)
                    {
                        ping = rt.m_nPing;
                        qLocal = rt.m_flConnectionQualityLocal;
                        qRemote = rt.m_flConnectionQualityRemote;
                        sendRate = rt.m_nSendRateBytesPerSecond;
                    }
                }
                catch { }

                RoleLogger.Info(role,
                    $"[Diag] [D10-Life] {transportLabel} phase={phaseTag} handle={handle.m_HSteamNetConnection} " +
                    $"state={info.m_eState} remote={remoteSteamId} " +
                    $"flags=0x{flags:X2} relayed={relayed} fast={fast} " +
                    $"popRelay={(int)(uint)info.m_idPOPRelay} popRemote={(int)(uint)info.m_idPOPRemote} " +
                    $"ping={ping}ms qLocal={qLocal:F2} qRemote={qRemote:F2} sendRate={sendRate}/s");
            }
            catch (Exception ex)
            {
                RoleLogger.Warn(role, $"[Diag] [D10-Life] SnapshotLiveState 异常（不阻断）: {ex.Message}");
            }
        }

        /// <summary>
        /// P0-4：双端多时机记录 Steam relay/auth readiness。
        /// v0.2.3.6 P0-B：增加 GetAuthenticationStatus + m_debugMsg 脱敏输出。
        /// 全部只读，不修改任何 SNS 状态。
        /// </summary>
        public static void SnapshotRelayAuthReadiness(string role, string occasion)
        {
            try
            {
                // 1. Relay network status
                try
                {
                    SteamRelayNetworkStatus_t relay;
                    ESteamNetworkingAvailability relayAvail = SteamNetworkingUtils.GetRelayNetworkStatus(out relay);
                    string relayDebugRaw = relay.m_debugMsg ?? "";
                    string relayDebugRedacted = RedactSensitiveNetworkData(relayDebugRaw);
                    // v0.2.3.7 P0-5 修复（审计 High-3）：同时记录 API 返回值与 struct m_eAvail
                    //   旧实现只记录 struct m_eAvail，丢失 API 返回值，无法识别返回值与 details 状态不一致
                    RoleLogger.Info(role,
                        $"[Diag] [D-Relay] {occasion} GetRelayNetworkStatus " +
                        $"apiReturn={relayAvail}({(int)relayAvail}) " +
                        $"m_eAvail={relay.m_eAvail}({(int)relay.m_eAvail}) " +
                        $"pingMeasurementInProgress={relay.m_bPingMeasurementInProgress} " +
                        $"m_eAvailNetworkConfig={relay.m_eAvailNetworkConfig}({(int)relay.m_eAvailNetworkConfig}) " +
                        $"m_eAvailAnyRelay={relay.m_eAvailAnyRelay}({(int)relay.m_eAvailAnyRelay}) " +
                        $"m_debugMsg=\"{relayDebugRedacted}\"");
                }
                catch (Exception ex)
                {
                    RoleLogger.Warn(role, $"[Diag] [D-Relay] GetRelayNetworkStatus 异常（不阻断）: {ex.Message}");
                }

                // 2. P0-B 新增：Authentication status
                try
                {
                    SteamNetAuthenticationStatus_t auth;
                    ESteamNetworkingAvailability authAvail = SteamNetworkingSockets.GetAuthenticationStatus(out auth);
                    string authDebugRaw = auth.m_debugMsg ?? "";
                    string authDebugRedacted = RedactSensitiveNetworkData(authDebugRaw);
                    // v0.2.3.7 P0-5 修复（审计 High-3）：同时记录 API 返回值与 struct m_eAvail
                    RoleLogger.Info(role,
                        $"[Diag] [D-Auth] {occasion} GetAuthenticationStatus " +
                        $"apiReturn={authAvail}({(int)authAvail}) " +
                        $"m_eAvail={auth.m_eAvail}({(int)auth.m_eAvail}) " +
                        $"m_debugMsg=\"{authDebugRedacted}\"");
                }
                catch (Exception ex)
                {
                    RoleLogger.Warn(role, $"[Diag] [D-Auth] GetAuthenticationStatus 异常（不阻断）: {ex.Message}");
                }

                // 3. 只读查询当前全局 ICE bitmask 和 ICE/SDR penalty
                try
                {
                    ReadGlobalConfigInt(role, occasion,
                        ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_P2P_Transport_ICE_Enable,
                        "P2P_Transport_ICE_Enable");
                    ReadGlobalConfigInt(role, occasion,
                        ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_P2P_Transport_ICE_Penalty,
                        "P2P_Transport_ICE_Penalty");
                    ReadGlobalConfigInt(role, occasion,
                        ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_P2P_Transport_SDR_Penalty,
                        "P2P_Transport_SDR_Penalty");
                }
                catch (Exception ex)
                {
                    RoleLogger.Warn(role, $"[Diag] [D-Relay] ReadGlobalConfigInt 异常（不阻断）: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                RoleLogger.Warn(role, $"[Diag] [D-Relay] SnapshotRelayAuthReadiness 异常（不阻断）: {ex.Message}");
            }
        }

        /// <summary>
        /// P0-4：只读读取一个全局 int 配置。失败不抛异常，仅记录。
        /// </summary>
        private static void ReadGlobalConfigInt(string role, string occasion,
            ESteamNetworkingConfigValue value, string label)
        {
            try
            {
                int bufSize = 16;
                IntPtr buf = IntPtr.Zero;
                try
                {
                    buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(bufSize);
                    ulong cbResult = (ulong)bufSize;
                    ESteamNetworkingConfigDataType dataType;
                    ESteamNetworkingGetConfigValueResult res = SteamNetworkingUtils.GetConfigValue(
                        value,
                        ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global,
                        IntPtr.Zero,
                        out dataType,
                        buf,
                        ref cbResult);
                    if (res == ESteamNetworkingGetConfigValueResult.k_ESteamNetworkingGetConfigValue_OK)
                    {
                        long v = 0;
                        if (dataType == ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32)
                        {
                            v = System.Runtime.InteropServices.Marshal.ReadInt32(buf);
                        }
                        else if (dataType == ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int64)
                        {
                            v = System.Runtime.InteropServices.Marshal.ReadInt64(buf);
                        }
                        else if (dataType == ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Float)
                        {
                            float f = (float)System.Runtime.InteropServices.Marshal.PtrToStructure(buf, typeof(float));
                            RoleLogger.Info(role,
                                $"[Diag] [D-Relay] {occasion} GetConfigValue {label}=float:{f:F4} (dataType={dataType})");
                            return;
                        }
                        RoleLogger.Info(role,
                            $"[Diag] [D-Relay] {occasion} GetConfigValue {label}={v} (dataType={dataType})");
                    }
                    else
                    {
                        RoleLogger.Info(role,
                            $"[Diag] [D-Relay] {occasion} GetConfigValue {label} result={res} (not set or unknown)");
                    }
                }
                finally
                {
                    if (buf != IntPtr.Zero)
                    {
                        System.Runtime.InteropServices.Marshal.FreeHGlobal(buf);
                    }
                }
            }
            catch (Exception ex)
            {
                RoleLogger.Warn(role, $"[Diag] [D-Relay] ReadGlobalConfigInt({label}) 异常（不阻断）: {ex.Message}");
            }
        }

        /// <summary>
        /// P0-A：统一脱敏入口（v0.2.3.6 改名 + 扩展）。
        ///
        /// 覆盖范围（按处理顺序，先处理多行/结构化内容，再处理单点地址）：
        ///   1. PEM 证书 / 私钥 / 通用 PEM 块
        ///   2. ICE/SDP candidate 行
        ///   3. STUN/TURN URL
        ///   4. 括号 IPv6（含端口）
        ///   5. IPv4-mapped IPv6
        ///   6. 压缩 IPv6（含 ::）
        ///   7. 全长 IPv6（8 段）
        ///   8. IPv4 + 端口
        ///   9. hostname:port
        ///   10. ticket/cert/signature/auth_token/private_key 关键字 + base64 payload
        ///   11. 残留长 base64 串（>= 32 字符）
        ///
        /// fail-closed：脱敏后再扫一次，若仍含疑似 IP 或长 base64，只记录稳定摘要。
        /// </summary>
        public static string RedactSensitiveNetworkData(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;

            string redacted = raw;

            // 1. PEM 块（先处理多行块避免后续正则误伤）
            redacted = _pemCertBlock.Replace(redacted, "[PEM-CERT-REDACTED]");
            redacted = _pemPrivateKeyBlock.Replace(redacted, "[PEM-KEY-REDACTED]");
            redacted = _pemGenericBlock.Replace(redacted, "[PEM-BLOCK-REDACTED]");

            // 2. ICE/SDP candidate 行
            redacted = _iceCandidateLine.Replace(redacted, "[CANDIDATE-REDACTED]");

            // 3. STUN/TURN URL
            redacted = _stunTurnUrl.Replace(redacted, "[TURN-URL-REDACTED]");

            // 4. v0.2.3.7 P0-2 新增：HTTP(S) URL（必须在裸 FQDN 之前，避免 URL 内 hostname 被部分脱敏）
            redacted = _httpUrl.Replace(redacted, m => $"[URL:{m.Length}chars-REDACTED]");

            // 5. v0.2.3.7 P0-2 新增：JSON 引号包围的 secret key:value（必须在 _secretKeywordPayload 之前，
            //    避免 JSON key 被 _secretKeywordPayload 部分匹配导致泄漏 value）
            redacted = _jsonSecretKeyValue.Replace(redacted, m => $"\"[json-secret:{m.Length}chars-REDACTED]\"");

            // 6. 括号 IPv6
            redacted = _bracketedIpv6.Replace(redacted, "[IP6-REDACTED]");

            // 7. IPv4-mapped IPv6
            redacted = _ipv4MappedIpv6.Replace(redacted, "[IP6MAPPED-REDACTED]");

            // 8. 压缩 IPv6
            redacted = _compressedIpv6.Replace(redacted, "[IP6-REDACTED]");

            // 9. 全长 IPv6
            redacted = _fullIpv6.Replace(redacted, "[IP6-REDACTED]");

            // 10. IPv4 + 端口
            redacted = _ipv4WithPort.Replace(redacted, "[IP-REDACTED]");

            // 11. hostname:port
            redacted = _hostnameWithPort.Replace(redacted, "[HOST:PORT-REDACTED]");

            // 12. v0.2.3.7 P0-2 修改：ticket/cert/signature 关键字 + payload 必须在裸 FQDN 之前
            //   原因：secret payload 可能含点号（如 cert=abc-def.example.com），
            //   若先跑 _bareFqdn 会只匹配 "def.example.com" 部分，留下 "abc_" 残渣。
            //   先跑 _secretKeywordPayload 可整段脱敏 "cert=abc-def.example.com" -> [SECRET:Nchars-REDACTED]。
            redacted = _secretKeywordPayload.Replace(redacted, m => $"[SECRET:{m.Length}chars-REDACTED]");

            // 13. v0.2.3.7 P0-2 新增：裸 FQDN（不带端口，处理无 secret keyword 前缀的 hostname）
            redacted = _bareFqdn.Replace(redacted, "[FQDN-REDACTED]");

            // 14. v0.2.3.8 P0-A 新增（审计 v0.2.3.7 Critical-1）：单标签 hostname:port
            //   必须在 _hostnameWithPort 之后（多点 hostname:port 已被脱敏）和 _bareFqdn 之后（避免与 FQDN 重叠匹配）
            //   覆盖 LANHOST:27015 等内网单标签 hostname:port 形式
            //   误脱敏（如代码 Method:80）比泄漏更可接受（审计明确允许）
            redacted = _singleLabelHostPort.Replace(redacted, "[HOST:PORT-REDACTED]");

            // 15. 残留长 base64 串（>= 32 字符，含 base64url）
            redacted = _longBase64.Replace(redacted, m => $"[BASE64:{m.Length}chars-REDACTED]");

            // v0.2.3.7 P0-2 修复（审计 Critical-2），v0.2.3.8 P0-A 扩展：
            //   fail-closed 检查扩展到 hostname/URI/secret-key/长 base64url/单标签 hostname:port
            //   若脱敏后仍含疑似 IP / hostname / URI / secret-key 残留 / 长 base64url / 单标签 host:port，只记稳定摘要，不保留原文
            if (_residualIpv4.IsMatch(redacted) || _residualIpv6.IsMatch(redacted)
                || _residualFqdn.IsMatch(redacted) || _residualHttpUrl.IsMatch(redacted)
                || _residualSecretKeyword.IsMatch(redacted) || _residualLongBase64Url.IsMatch(redacted)
                || _residualSingleLabelHostPort.IsMatch(redacted))
            {
                int len = redacted.Length;
                int hash = stableHash(redacted);
                return $"[FAIL-CLOSED len={len} hash=0x{hash:X8} residual-sensitive-detected]";
            }

            return redacted;
        }

        /// <summary>
        /// 向后兼容别名：v0.2.3.5 旧调用方仍使用 RedactSensitiveAddresses。
        /// 内部转发到 RedactSensitiveNetworkData。
        /// </summary>
        public static string RedactSensitiveAddresses(string raw)
        {
            return RedactSensitiveNetworkData(raw);
        }

        /// <summary>稳定 32 位哈希（用于 fail-closed 摘要，不暴露原文）。</summary>
        private static int stableHash(string s)
        {
            // FNV-1a 32-bit
            uint hash = 2166136261u;
            for (int i = 0; i < s.Length; i++)
            {
                hash ^= (uint)s[i];
                hash *= 16777619u;
            }
            return (int)hash;
        }

        /// <summary>
        /// v0.2.3.7 P0-A item 5 + P0-2 + P0-3，v0.2.3.8 P0-A 扩展：确定性脱敏自检（审计 v0.2.3.6 报告 Critical-2/High-2 + v0.2.3.7 报告 Critical-1 要求）。
        /// 启动期执行一次，输出 PASS/FAIL。
        /// v0.2.3.7 修改：
        ///   - 返回 bool（true=全 PASS，false=任一 FAIL 或异常）。
        ///   - 异常也视为失败（返回 false）。
        ///   - 失败日志只记 case 名，不打印 needle/redacted output（避免在失败日志中泄漏敏感内容）。
        ///   - 新增 6 项审计合成输入为回归测试（覆盖 ticket=abc_def-12345 / JSON ticket / 裸 hostname /
        ///     HTTPS hostname / 含 -/_ 的 cert payload / endDebug hostname）。
        /// v0.2.3.8 修改（审计 v0.2.3.7 Critical-1）：
        ///   - 新增 5 项审计合成输入为回归测试（覆盖大写 FQDN / 长 TLD / 短 JSON ticket /
        ///     单标签 hostname:port / 数字开头 hostname），自检总数从 21 增加到 26。
        /// 调用方（VerifyCriticalPatches）应将返回值聚合到 DiagnosticBuildValid 阻断门。
        /// </summary>
        public static bool RunRedactionSelfTest()
        {
            int pass = 0, fail = 0;
            try
            {
                RoleLogger.Info("[Shared]", "[Diag] [D-Redact-SelfTest] === P0-A 脱敏确定性自检开始（v0.2.3.8 含审计 Critical-1 回归用例）===");
                // 用例格式：(label, input, mustNotContainArray)
                var cases = new System.Collections.Generic.List<RedactTestCase>
                {
                    new RedactTestCase("IPv4",
                        "Connection from 192.168.1.100 established",
                        new[] { "192.168.1.100" }),
                    new RedactTestCase("IPv4:port",
                        "Remote endpoint 10.0.0.5:27015 reachable",
                        new[] { "10.0.0.5:27015", "10.0.0.5" }),
                    new RedactTestCase("IPv4-compressed-mapped",
                        "Listen on 172.16.254.1:12345",
                        new[] { "172.16.254.1:12345", "172.16.254.1" }),
                    new RedactTestCase("IPv6-compressed",
                        "Rendezvous with 2001:db8::1 timed out",
                        new[] { "2001:db8::1" }),
                    new RedactTestCase("IPv6-loopback",
                        "Local ::1 connection refused",
                        new[] { "::1" }),
                    new RedactTestCase("IPv6-bracketed-port",
                        "Bind to [2001:db8::1]:443 failed",
                        new[] { "2001:db8::1", "[2001:db8::1]:443" }),
                    new RedactTestCase("IPv4-mapped-IPv6",
                        "Dualstack ::ffff:192.0.2.1:80 accepted",
                        new[] { "192.0.2.1", "::ffff:192.0.2.1:80" }),
                    new RedactTestCase("hostname:port",
                        "STUN server stun.example.com:19302 resolved",
                        new[] { "stun.example.com:19302" }),
                    new RedactTestCase("STUN-URL",
                        "Configured stun:stun.l.google.com:19302 as primary",
                        new[] { "stun.l.google.com:19302", "stun:stun.l.google.com" }),
                    new RedactTestCase("TURN-URL",
                        "TURN relay turn:user@turn.example.org:3478?transport=udp active",
                        new[] { "turn.example.org", "user@turn.example.org" }),
                    new RedactTestCase("ICE-candidate",
                        "candidate:842723892 1 udp 1677729535 192.0.2.3 61665 typ srflx raddr 192.0.2.1 rport 61665 generation 0",
                        new[] { "842723892", "192.0.2.3", "192.0.2.1", "61665" }),
                    new RedactTestCase("PEM-cert-block",
                        "-----BEGIN CERTIFICATE-----\nMIIDXTCCAkWgAwIBAgIJAKDxYJEXAMPLE==\n-----END CERTIFICATE-----",
                        new[] { "MIIDXTCCAkWgAwIBAgIJAKDxYJEXAMPLE", "BEGIN CERTIFICATE" }),
                    new RedactTestCase("ticket-payload",
                        "Auth ticket=TICKET_abc123def456ghi789jkl012mno345pqr678stu901vwx234yz567bytes",
                        new[] { "TICKET_abc123def456ghi789jkl012mno345pqr678stu901vwx234yz567" }),
                    new RedactTestCase("cert-payload",
                        "Received cert=ABCDEF1234567890ABCDEF1234567890ABCDEF1234567890ABCDEF from peer",
                        new[] { "ABCDEF1234567890ABCDEF1234567890ABCDEF1234567890ABCDEF" }),
                    new RedactTestCase("signature-payload",
                        "signature=YXNkZmdoamtsenhjdmJuYW1xcHdlcnR5dWlvcGFzZGZnaGprbHp4Y3ZibmFtcXB3ZXJ0eXVpbw==",
                        new[] { "YXNkZmdoamtsenhjdmJuYW1xcHdlcnR5dWlvcGFz" }),
                    // v0.2.3.7 P0-2 新增：6 项审计 Critical-2 合成回归用例
                    new RedactTestCase("audit-ticket-equals-base64url",
                        "ticket=abc_def-12345",
                        new[] { "abc_def-12345" }),
                    new RedactTestCase("audit-json-ticket-quoted",
                        "{\"ticket\":\"abc_def-12345\"}",
                        new[] { "abc_def-12345" }),
                    new RedactTestCase("audit-bare-hostname-fqdn",
                        "Resolving private-host.example.com failed",
                        new[] { "private-host.example.com" }),
                    new RedactTestCase("audit-enddebug-remote-hostname",
                        "endDebug remote.example.net rendezvous failed",
                        new[] { "remote.example.net" }),
                    new RedactTestCase("audit-https-url-hostname",
                        "peer=https://hidden.example.org/path",
                        new[] { "hidden.example.org", "https://hidden.example.org" }),
                    new RedactTestCase("audit-cert-payload-base64url",
                        "cert: abc-def_123456",
                        new[] { "abc-def_123456" }),
                    // v0.2.3.8 P0-A 新增：5 项审计 v0.2.3.7 Critical-1 合成回归用例
                    new RedactTestCase("audit-uppercase-fqdn",
                        "Resolving SERVER.EXAMPLE.COM failed",
                        new[] { "SERVER.EXAMPLE.COM", "SERVER.EXAMPLE", "EXAMPLE.COM" }),
                    new RedactTestCase("audit-long-tld",
                        "relay foo.technology failed",
                        new[] { "foo.technology" }),
                    new RedactTestCase("audit-short-json-ticket",
                        "{\"ticket\":\"short\"}",
                        new[] { "short" }),
                    new RedactTestCase("audit-single-label-hostport",
                        "endpoint=LANHOST:27015",
                        new[] { "LANHOST:27015", "LANHOST" }),
                    new RedactTestCase("audit-digit-start-hostname",
                        "endpoint=123node.local",
                        new[] { "123node.local" }),
                };

                foreach (var tc in cases)
                {
                    string redacted = RedactSensitiveNetworkData(tc.Input);
                    bool leaked = false;
                    foreach (string needle in tc.MustNotContain)
                    {
                        if (redacted.IndexOf(needle, System.StringComparison.Ordinal) >= 0)
                        {
                            leaked = true;
                            break;
                        }
                    }
                    if (leaked)
                    {
                        fail++;
                        // v0.2.3.7 P0-2 修复：失败日志只记 case 名，不打印 needle/redacted output
                        //   避免在失败日志中泄漏可能含敏感内容的 needle 或部分脱敏输出
                        RoleLogger.Error("[Shared]",
                            $"[Diag] [D-Redact-SelfTest] FAIL [{tc.Label}] case-failed-redaction-leak-detected");
                    }
                    else
                    {
                        pass++;
                        // PASS 日志可以打印 redacted 输出（脱敏已通过验证，输出本身是安全的占位符）
                        RoleLogger.Info("[Shared]",
                            $"[Diag] [D-Redact-SelfTest] PASS [{tc.Label}] redacted=\"{redacted}\"");
                    }
                }

                RoleLogger.Info("[Shared]",
                    $"[Diag] [D-Redact-SelfTest] === 自检完成 pass={pass} fail={fail} total={cases.Count} ===");
                if (fail > 0)
                {
                    RoleLogger.Error("[Shared]",
                        $"[Diag] [D-Redact-SelfTest] !!! 脱敏自检失败 {fail} 例，P0-3 阻断门已生效：DiagnosticBuildValid 将被置 false !!!");
                }
            }
            catch (Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[Diag] [D-Redact-SelfTest] 自检执行异常（视为 FAIL）: {ex.Message}");
                return false;
            }
            return fail == 0;
        }

        private struct RedactTestCase
        {
            public string Label;
            public string Input;
            public string[] MustNotContain;

            public RedactTestCase(string label, string input, string[] mustNotContain)
            {
                Label = label;
                Input = input;
                MustNotContain = mustNotContain;
            }
        }
    }
}
