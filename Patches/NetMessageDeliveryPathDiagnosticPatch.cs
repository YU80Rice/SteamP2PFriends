using HarmonyLib;
using SDG.NetPak;
using SDG.NetTransport;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using Steamworks;
using System.Collections.Generic;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    ///
    /// 76561199030780228 是主机自身 SteamID，Loopback 异常与客机通信无关）。调整为追踪所有 NetMessage
    /// 发送给客机的实际投递路径与成功率。
    ///
    /// 目标（调整后）：
    ///   1. Postfix NetMessages.SendMessageToClient（已有 D-5 patch 基础），记录：
    ///      - 目标 SteamID（区分主机自身 vs 客机）
    ///      - 消息类型
    ///      - 使用的 transport 类型
    ///      - 是否成功
    ///   2. 追踪 PlayerMovement.tellState 的发送路径（是否走 SendMessageToClient）
    ///   3. 追踪 [SteamCall] RPC 的实际发送 API
    ///
    /// 关键诊断价值：
    ///   - 验证 L624 Loopback 异常确实是主机->自身通信（remote=主机 SteamID）
    ///   - 验证客机（remote=客机 SteamID）消息通过 SteamNetworkingSockets 投递
    ///   - 统计客机收到的消息类型分布（PlayerConnected/Accepted/PingResponse 等）
    ///   - 为议题 A/B 独立性判定提供证据（若客机收到大量消息但 tellState 0 triggers -> 支持独立性）
    ///
    /// 实现说明：
    ///   - NetMessages 是 internal static class，必须通过反射手动登记
    ///   - 与现有 NetMessagesSendDiagnosticPatch（D-5）共存，本 patch 添加第二个 Postfix
    ///   - ITransportConnection 目标 SteamID 通过反射获取（TransportConnection_SteamNetworkingSockets
    ///     可能没有 public SteamId 属性，需尝试多种字段名）
    ///
    /// 严格禁止：
    ///   - 修改原方法参数或返回值
    ///   - 修改 vanilla 网络层
    ///   - 吞异常（Finalizer void 签名，保留原异常）
    /// </summary>
    public static class NetMessageDeliveryPathDiagnosticPatch
    {
        public static bool DVis16_Registered { get; private set; }

        public static bool AllRegistrationsSucceeded => DVis16_Registered;

        // 消息类型分布统计（按目标类型 + 消息类型 + transport 类型聚合）
        private static readonly Dictionary<string, int> _deliveryStats = new Dictionary<string, int>();
        private static readonly object _statsLock = new object();

        // 反射缓存：TransportConnection_SteamNetworkingSockets 的 SteamId 字段
        private static FieldInfo _snsSteamIdField;
        private static PropertyInfo _snsSteamIdProp;
        private static bool _reflectionCached;

        public static bool RegisterManual(Harmony harmony)
        {
            DVis16_Registered = RegisterDVis16(harmony);
            RoleLogger.Info("[Shared]",
                $"[D-Vis] NetMessageDeliveryPathDiagnosticPatch 汇总: D-Vis-16={DVis16_Registered}");
            return AllRegistrationsSucceeded;
        }

        private static bool RegisterDVis16(Harmony harmony)
        {
            const string Label = "D-Vis-16 NetMessage Delivery Path";
            try
            {
                CacheReflection();

                System.Type netMessagesType = AccessTools.TypeByName("SDG.Unturned.NetMessages");
                if (netMessagesType == null)
                {
                    RoleLogger.Error("[Shared]", $"[D-Vis-16] !!! {Label} NetMessages type not found");
                    return false;
                }

                System.Type clientWriteHandlerType = netMessagesType.GetNestedType("ClientWriteHandler");
                if (clientWriteHandlerType == null)
                {
                    RoleLogger.Error("[Shared]", $"[D-Vis-16] !!! {Label} ClientWriteHandler not found");
                    return false;
                }

                // SendMessageToClient(EClientMessage, ENetReliability, ITransportConnection, ClientWriteHandler)
                MethodInfo original = AccessTools.Method(netMessagesType, "SendMessageToClient",
                    new System.Type[] { typeof(EClientMessage), typeof(ENetReliability),
                        typeof(ITransportConnection), clientWriteHandlerType });
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[D-Vis-16] !!! {Label} SendMessageToClient 反射失败");
                    return false;
                }

                MethodInfo postfix = typeof(Hooks).GetMethod(nameof(Hooks.SendMessageToClientDeliveryPostfix),
                    BindingFlags.Static | BindingFlags.NonPublic);

                harmony.Patch(original, postfix: new HarmonyMethod(postfix));

                // 验证与 D-5 共存
                HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
                int prefixCount = info?.Prefixes?.Count ?? 0;
                int postfixCount = info?.Postfixes?.Count ?? 0;
                int finalizerCount = info?.Finalizers?.Count ?? 0;
                RoleLogger.Info("[Shared]",
                    $"[D-Vis-16] OK {Label} 已登记 (Postfix)。当前 SendMessageToClient patches: " +
                    $"prefixes={prefixCount}(含 D-5), postfixes={postfixCount}(含 D-Vis-16), " +
                    $"finalizers={finalizerCount}(含 D-5)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[D-Vis-16] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        // ====================== Hooks ======================

        private static class Hooks
        {
            /// <summary>
            /// Postfix for SendMessageToClient。记录目标 SteamID + transport + 消息类型 + 成功状态。
            /// 注意：Postfix 在原方法返回后执行，若原方法抛异常则 Postfix 不执行（Finalizer 才会执行）。
            /// 因此本 Postfix 只记录"成功投递"的情况。失败情况由 D-5 Finalizer 记录。
            /// </summary>
            internal static void SendMessageToClientDeliveryPostfix(
                EClientMessage index, ENetReliability reliability, ITransportConnection transportConnection)
            {
                try
                {
                    if (!ShouldLogDVis()) return;

                    string transportType = transportConnection?.GetType().Name ?? "null";
                    ulong targetSteamId = TryGetTargetSteamId(transportConnection);
                    string steamIdStr = targetSteamId != 0UL
                        ? DiagnosticMaskUtil.MaskSteamId(targetSteamId)
                        : "<unknown>";

                    // 区分主机自身 vs 客机
                    bool isHostSelf = IsHostSelfTransport(transportType, targetSteamId);
                    string targetKind = isHostSelf ? "HOST_SELF" : "REMOTE_CLIENT";

                    // 统计聚合
                    string statKey = $"{targetKind}|{index}|{transportType}";
                    lock (_statsLock)
                    {
                        if (!_deliveryStats.ContainsKey(statKey)) _deliveryStats[statKey] = 0;
                        _deliveryStats[statKey]++;
                    }

                    RoleLogger.Info("[Host]",
                        $"[D-Vis-16] SendMessageToClient SEND_CALL_RETURNED msg={index}({(int)index}) " +
                        $"reliability={reliability} transport={transportType} " +
                        $"target={steamIdStr} targetKind={targetKind}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Host]", $"[D-Vis-16] Delivery Postfix 异常（不阻断）: {ex.Message}");
                }
            }
        }

        // ====================== Helpers ======================

        private static void CacheReflection()
        {
            if (_reflectionCached) return;
            _reflectionCached = true;
            try
            {
                // TransportConnection_SteamNetworkingSockets 可能有 SteamId 字段或属性
                System.Type snsType = AccessTools.TypeByName("SDG.NetTransport.TransportConnection_SteamNetworkingSockets");
                if (snsType == null)
                {
                    RoleLogger.Warn("[Shared]", "[D-Vis-16] TransportConnection_SteamNetworkingSockets type not found");
                    return;
                }

                // 尝试多种可能的字段名
                _snsSteamIdField = AccessTools.Field(snsType, "steamId")
                    ?? AccessTools.Field(snsType, "_steamId")
                    ?? AccessTools.Field(snsType, "SteamId")
                    ?? AccessTools.Field(snsType, "remoteSteamId");

                // 尝试属性
                _snsSteamIdProp = AccessTools.Property(snsType, "SteamId")
                    ?? AccessTools.Property(snsType, "steamId");

                if (_snsSteamIdField == null && _snsSteamIdProp == null)
                {
                    RoleLogger.Warn("[Shared]", "[D-Vis-16] TransportConnection_SteamNetworkingSockets 未能反射到 SteamId 字段/属性，targetSteamId 将为 0");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Shared]", $"[D-Vis-16] 反射缓存异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 从 ITransportConnection 反射获取目标 SteamID。
        /// 对于 TransportConnection_SteamNetworkingSockets，尝试读取 steamId 字段/属性。
        /// 对于 TransportConnection_Loopback，返回 0（主机自身，无远程 SteamID）。
        /// </summary>
        private static ulong TryGetTargetSteamId(ITransportConnection tc)
        {
            try
            {
                if (tc == null) return 0UL;
                string typeName = tc.GetType().Name;

                // Loopback 为主机自身，无远程 SteamID
                if (typeName == "TransportConnection_Loopback") return 0UL;

                // SteamNetworkingSockets 尝试反射获取
                if (_snsSteamIdField != null)
                {
                    object val = _snsSteamIdField.GetValue(tc);
                    if (val is CSteamID csid) return csid.m_SteamID;
                    if (val is ulong raw) return raw;
                }
                if (_snsSteamIdProp != null)
                {
                    object val = _snsSteamIdProp.GetValue(tc, null);
                    if (val is CSteamID csid) return csid.m_SteamID;
                    if (val is ulong raw) return raw;
                }

                return 0UL;
            }
            catch
            {
                return 0UL;
            }
        }

        /// <summary>
        /// 判断是否为主机自身的 transport（Loopback 或 targetSteamId == Provider.user）。
        /// </summary>
        private static bool IsHostSelfTransport(string transportType, ulong targetSteamId)
        {
            try
            {
                if (transportType == "TransportConnection_Loopback") return true;
                if (targetSteamId != 0UL && Provider.user.IsValid())
                {
                    return targetSteamId == Provider.user.m_SteamID;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool ShouldLogDVis()
        {
            try
            {
                return SteamP2PFriendsPlugin.VerboseLog != null
                    && SteamP2PFriendsPlugin.VerboseLog.Value
                    && SteamP2PFriendsPlugin.RouteDiagnostics != null
                    && SteamP2PFriendsPlugin.RouteDiagnostics.Value;
            }
            catch
            {
                return false;
            }
        }

        // ====================== 统计快照 ======================

        /// <summary>
        /// 输出当前投递路径统计快照（供 Plugin 周期性调用或客机断开时调用）。
        /// </summary>
        public static void LogStatsSnapshot()
        {
            try
            {
                lock (_statsLock)
                {
                    RoleLogger.Info("[Host]", $"[D-Vis-16] === 投递路径统计快照 total={_deliveryStats.Count} ===");
                    foreach (var kv in _deliveryStats)
                    {
                        RoleLogger.Info("[Host]", $"[D-Vis-16]   {kv.Key} => {kv.Value}");
                    }
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Host]", $"[D-Vis-16] LogStatsSnapshot 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 清除统计（客机断开时调用）。
        /// </summary>
        public static void OnClientDisconnected()
        {
            try
            {
                lock (_statsLock)
                {
                    _deliveryStats.Clear();
                }
            }
            catch { /* ignore */ }
        }
    }
}
