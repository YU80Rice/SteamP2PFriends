using HarmonyLib;
using SDG.NetPak;
using SDG.NetTransport;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3 P0-D D-5：NetMessages.SendMessageToClient(s) Prefix+Finalizer 诊断 patch。
    ///
    /// 审计员要求：
    ///   - 记录消息类型（重点 PlayerConnected/Accepted/PlayerDisconnected）、目标 transport 类型、是否抛异常。
    ///   - 先只记录，不改行为。
    ///   - Finalizer void 签名，不吞异常。
    ///
    /// 关键诊断目标：
    ///   - 验证 Provider.accept 向既有玩家发送 PlayerConnected 时是否命中 Loopback transport。
    ///   - 反编译预测会抛 NotSupportedException，但实测日志未出现，需运行时确认。
    ///   - Accepted 是发给远端客机的正常 SNS transport 消息，不经过房主 loopback。
    ///
    /// 实现说明：
    ///   - NetMessages 是 internal static class，编译时无法用 typeof() 访问。
    ///   - 本类不使用 [HarmonyPatch] 特性，改由 SteamP2PFriendsPlugin.ApplyManualWrapperPatches()
    ///     在运行时通过反射手动登记。
    ///   - Prefix/Finalizer 方法签名与 NetMessages.SendMessageToClient 一致：
    ///       public static void SendMessageToClient(EClientMessage index, ENetReliability reliability, ITransportConnection transportConnection, ClientWriteHandler callback)
    /// </summary>
    public static class NetMessagesSendDiagnosticPatch
    {
        /// <summary>
        /// Prefix for SendMessageToClient(EClientMessage, ENetReliability, ITransportConnection, ClientWriteHandler)
        /// </summary>
        public static void SendMessageToClient_Prefix(
            EClientMessage index, ENetReliability reliability, ITransportConnection transportConnection)
        {
            try
            {
                string transportType = transportConnection?.GetType().Name ?? "null";
                RoleLogger.Info("[Host]",
                    $"{DiagnosticContext.FormatPrefix("SendMessageToClient ENTER")} " +
                    $"msg={index}({(int)index}) reliability={reliability} transport={transportType}");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Host]", $"[Diag] SendMessageToClient Prefix 异常（不阻断）: {ex.Message}");
            }
        }

        /// <summary>
        /// Finalizer for SendMessageToClient. void 签名，不吞异常。
        /// </summary>
        public static void SendMessageToClient_Finalizer(
            EClientMessage index, ITransportConnection transportConnection, System.Exception __exception)
        {
            try
            {
                if (__exception != null)
                {
                    string transportType = transportConnection?.GetType().Name ?? "null";
                    RoleLogger.Error("[Host]",
                        $"{DiagnosticContext.FormatPrefix("SendMessageToClient THREW")} " +
                        $"msg={index}({(int)index}) transport={transportType} " +
                        $"exceptionType={__exception.GetType().Name} message={__exception.Message}");
                }
            }
            catch
            {
                // Finalizer 内部异常不得影响原异常传播
            }
            // void 签名，保留原异常
        }

        /// <summary>
        /// Prefix for SendMessageToClients(EClientMessage, ENetReliability, List&lt;ITransportConnection&gt;, ClientWriteHandler)
        /// v0.2.3.2 P0-5 修复：记录完整目标 transport 类型列表（原仅 firstTransport）。
        /// </summary>
        public static void SendMessageToClients_List_Prefix(
            EClientMessage index, ENetReliability reliability,
            System.Collections.Generic.List<ITransportConnection> transportConnections)
        {
            try
            {
                int count = transportConnections?.Count ?? -1;
                string transportTypes = "<none>";
                if (transportConnections != null && transportConnections.Count > 0)
                {
                    var types = new System.Collections.Generic.List<string>(transportConnections.Count);
                    foreach (var tc in transportConnections)
                    {
                        types.Add(tc?.GetType().Name ?? "null");
                    }
                    transportTypes = string.Join(",", types);
                }
                RoleLogger.Info("[Host]",
                    $"{DiagnosticContext.FormatPrefix("SendMessageToClients(List) ENTER")} " +
                    $"msg={index}({(int)index}) reliability={reliability} " +
                    $"count={count} transports=[{transportTypes}]");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Host]", $"[Diag] SendMessageToClients(List) Prefix 异常（不阻断）: {ex.Message}");
            }
        }

        /// <summary>
        /// Finalizer for SendMessageToClients(List). void 签名，不吞异常。
        /// v0.2.3.2 P0-5 修复：补齐 List Finalizer（原仅 Prefix）。
        /// </summary>
        public static void SendMessageToClients_List_Finalizer(
            EClientMessage index,
            System.Collections.Generic.List<ITransportConnection> transportConnections,
            System.Exception __exception)
        {
            try
            {
                int count = transportConnections?.Count ?? -1;
                if (__exception != null)
                {
                    RoleLogger.Error("[Host]",
                        $"{DiagnosticContext.FormatPrefix("SendMessageToClients(List) THREW")} " +
                        $"msg={index}({(int)index}) count={count} " +
                        $"exceptionType={__exception.GetType().Name} message={__exception.Message}");
                }
                else
                {
                    RoleLogger.Info("[Host]",
                        $"{DiagnosticContext.FormatPrefix("SendMessageToClients(List) OK")} " +
                        $"msg={index}({(int)index}) count={count}");
                }
            }
            catch
            {
                // Finalizer 内部异常不得影响原异常传播
            }
            // void 签名，保留原异常
        }

        /// <summary>
        /// 由 Plugin.ApplyManualWrapperPatches() 调用，通过反射手动登记 NetMessages patch。
        /// </summary>
        public static void RegisterManual(Harmony harmony)
        {
            try
            {
                System.Type netMessagesType = AccessTools.TypeByName("SDG.Unturned.NetMessages");
                if (netMessagesType == null)
                {
                    RoleLogger.Error("[Shared]", "[ManualPatch] !!! NetMessages type not found");
                    return;
                }

                // NetMessages.ClientWriteHandler 是 public nested delegate，但 NetMessages 本身 internal，
                // 编译时无法用 typeof(NetMessages.ClientWriteHandler)。运行时反射获取。
                System.Type clientWriteHandlerType = netMessagesType.GetNestedType("ClientWriteHandler");
                if (clientWriteHandlerType == null)
                {
                    RoleLogger.Error("[Shared]", "[ManualPatch] !!! NetMessages.ClientWriteHandler not found");
                    return;
                }

                // SendMessageToClient(EClientMessage, ENetReliability, ITransportConnection, ClientWriteHandler)
                RegisterOne(harmony, netMessagesType, "SendMessageToClient",
                    new System.Type[] { typeof(EClientMessage), typeof(ENetReliability),
                        typeof(ITransportConnection), clientWriteHandlerType },
                    nameof(SendMessageToClient_Prefix), nameof(SendMessageToClient_Finalizer));

                // SendMessageToClients(EClientMessage, ENetReliability, List<ITransportConnection>, ClientWriteHandler)
                System.Type listOfTransportType = typeof(System.Collections.Generic.List<ITransportConnection>);
                RegisterOne(harmony, netMessagesType, "SendMessageToClients",
                    new System.Type[] { typeof(EClientMessage), typeof(ENetReliability),
                        listOfTransportType, clientWriteHandlerType },
                    nameof(SendMessageToClients_List_Prefix), nameof(SendMessageToClients_List_Finalizer));
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[ManualPatch] NetMessages 注册失败: {ex}");
            }
        }

        private static void RegisterOne(Harmony harmony, System.Type targetType, string methodName,
            System.Type[] paramTypes, string prefixName, string finalizerName)
        {
            try
            {
                MethodInfo original = AccessTools.Method(targetType, methodName, paramTypes);
                if (original == null)
                {
                    RoleLogger.Warn("[Shared]",
                        $"[ManualPatch] !!! {targetType.Name}.{methodName}({paramTypes.Length} args): method not found");
                    return;
                }

                HarmonyMethod prefix = null;
                HarmonyMethod finalizer = null;

                if (!string.IsNullOrEmpty(prefixName))
                {
                    MethodInfo p = AccessTools.Method(typeof(NetMessagesSendDiagnosticPatch), prefixName);
                    if (p != null) prefix = new HarmonyMethod(p);
                }
                if (!string.IsNullOrEmpty(finalizerName))
                {
                    MethodInfo f = AccessTools.Method(typeof(NetMessagesSendDiagnosticPatch), finalizerName);
                    if (f != null) finalizer = new HarmonyMethod(f);
                }

                harmony.Patch(original, prefix: prefix, finalizer: finalizer);

                HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
                int prefixCount = info?.Prefixes?.Count ?? 0;
                int finalizerCount = info?.Finalizers?.Count ?? 0;
                RoleLogger.Info("[Shared]",
                    $"[ManualPatch] OK {targetType.Name}.{methodName} 已登记 " +
                    $"(prefixes={prefixCount}, finalizers={finalizerCount})");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[ManualPatch] !!! {targetType.Name}.{methodName} 注册异常: {ex}");
            }
        }
    }
}
