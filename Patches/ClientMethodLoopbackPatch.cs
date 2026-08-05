using HarmonyLib;
using SDG.NetPak;
using SDG.NetTransport;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// ClientMethodHandle loopback 优化 3 合 1（对齐原版 HarmonyPatches.cs:189-273）。
    ///
    /// 三个 patch 都是 Prefix + return false 完全替换 vanilla 实现：
    ///   1. SendAndLoopbackIfLocal - 单发：loopback 连接走 InvokeLoopback，非 loopback 走 Send
    ///   2. SendAndLoopbackIfAnyAreLocal - 广播部分 loopback：遍历连接，loopback 标记不发送，循环结束统一 InvokeLoopback
    ///   3. SendAndLoopback - 强制广播：遍历连接跳过 loopback Send，循环结束无条件 InvokeLoopback
    ///
    /// v0.2.3.10 修复（Codex 第七次审计 P0-A/B/C）：
    ///   - 三个 Prefix 改为 RegisterManual 显式手动登记
    ///   - AllRegistrationsSucceeded + RegistrationSummary 暴露给 VerifyCriticalPatches 阻断门
    ///   - 三个 Prefix 各自增加限次分支证据日志
    ///
    /// v0.2.3.11 修复（Codex 第八次测试放行外部审计 Critical-1 + High-1 + High-2）：
    ///   - Critical-1：InvokeLoopback 改用 AccessTools.DeclaredMethod 从 ClientMethodHandle 声明类型精确解析
    ///                 基类 private 方法不会被派生类型 GetMethod 查出，旧 ReflectionUtil.InvokeInstance 必失败
    ///   - High-1：VerifyClientMethodLoopbackPrefix 收紧为精确 1/1（totalPrefixCount==1 + exactMatchCount==1）
    ///   - High-2：日志计数器分离 local/passthrough 与 remote，远程分支保留独立额度
    ///
    /// v0.2.3.12 修复（Codex v0.2.3.11 静态验收 Medium-1）：
    ///   - 远程调用引入 correlation 序号（per-call），同调用的 attempt / send-success / loopback-success 共享序号
    ///   - 一次远程调用无论产生多少阶段日志，仅消耗 1 次额度（旧实现 attempt/success 各消耗 1 次，5 次额度仅覆盖 2 次完整调用）
    ///   - 两个广播 Prefix 补齐 attempt（含 plannedRemote）+ send-success + 可选 loopback-success 三阶段独立记录
    ///   - 若 Send 或 InvokeLoopback 抛异常，attempt 已先写日志，可在远程日志中定位失败阶段
    /// </summary>
    public static class ClientMethodLoopbackPatch
    {
        // v0.2.3.10：手动登记状态（VerifyCriticalPatches 阻断门读取）
        public static bool AllRegistrationsSucceeded { get; private set; }
        public static string RegistrationSummary { get; private set; } = "未登记";

        // v0.2.3.11 Critical-1：InvokeLoopback 基类 MethodInfo 缓存
        //   AccessTools.DeclaredMethod 只在声明类型上查找，不递归派生类型
        //   基类 private 方法不会被派生类型 GetMethod 查出，必须从 ClientMethodHandle 声明类型精确解析
        private static readonly MethodInfo InvokeLoopbackMethod = AccessTools.DeclaredMethod(
            typeof(ClientMethodHandle),
            "InvokeLoopback",
            new System.Type[] { typeof(NetPakWriter) });

        // v0.2.3.11 Critical-1：InvokeLoopback 自检结果（VerifyCriticalPatches 阻断门读取）
        public static bool InvokeLoopbackResolved { get; private set; }
        public static string InvokeLoopbackSummary { get; private set; } = "未自检";

        // v0.2.3.11 High-2 + v0.2.3.12 Medium-1：日志计数器分离 local/passthrough 与 remote
        //   - local/passthrough：1 次调用 1 条日志，前 5 次
        //   - remote：1 次调用 1 个 correlation 序号，attempt/send-success/loopback-success 共享序号，前 5 次调用
        private const int BranchLogLimit = 5;
        private static int _ifLocalLocalCount;
        private static int _ifLocalRemoteCallSeq;
        private static int _ifAnyAreLocalLocalCount;
        private static int _ifAnyAreLocalRemoteCallSeq;
        private static int _sendAndLoopbackLocalCount;
        private static int _sendAndLoopbackRemoteCallSeq;

        // v0.2.3.10：精确 patch 方法名常量（自检对照）
        public const string PrefixIfLocalName = nameof(SendAndLoopbackIfLocal_Prefix);
        public const string PrefixIfAnyAreLocalName = nameof(SendAndLoopbackIfAnyAreLocal_Prefix);
        public const string PrefixSendAndLoopbackName = nameof(SendAndLoopback_Prefix);

        /// <summary>
        /// v0.2.3.11 Critical-1：InvokeLoopback 自检。
        /// 验证 InvokeLoopbackMethod 的声明类型、方法名、参数、返回类型。
        /// 任一不满足返回 false，由 VerifyCriticalPatches 聚合到 DiagnosticBuildValid 阻断门。
        /// </summary>
        private static bool VerifyInvokeLoopbackMethod()
        {
            try
            {
                if (InvokeLoopbackMethod == null)
                {
                    InvokeLoopbackResolved = false;
                    InvokeLoopbackSummary = "AccessTools.DeclaredMethod 返回 null";
                    RoleLogger.Error("[Shared]",
                        $"[ClientMethodLoopback/InvokeLoopback] !!! 自检失败: {InvokeLoopbackSummary}");
                    return false;
                }

                if (InvokeLoopbackMethod.DeclaringType != typeof(ClientMethodHandle))
                {
                    InvokeLoopbackResolved = false;
                    InvokeLoopbackSummary = $"DeclaringType={InvokeLoopbackMethod.DeclaringType?.FullName} 期望={typeof(ClientMethodHandle).FullName}";
                    RoleLogger.Error("[Shared]",
                        $"[ClientMethodLoopback/InvokeLoopback] !!! 自检失败: {InvokeLoopbackSummary}");
                    return false;
                }

                if (InvokeLoopbackMethod.Name != "InvokeLoopback")
                {
                    InvokeLoopbackResolved = false;
                    InvokeLoopbackSummary = $"Name={InvokeLoopbackMethod.Name} 期望=InvokeLoopback";
                    RoleLogger.Error("[Shared]",
                        $"[ClientMethodLoopback/InvokeLoopback] !!! 自检失败: {InvokeLoopbackSummary}");
                    return false;
                }

                ParameterInfo[] ps = InvokeLoopbackMethod.GetParameters();
                if (ps.Length != 1 || ps[0].ParameterType != typeof(NetPakWriter))
                {
                    InvokeLoopbackResolved = false;
                    InvokeLoopbackSummary = $"参数签名不匹配 (paramCount={ps.Length})";
                    RoleLogger.Error("[Shared]",
                        $"[ClientMethodLoopback/InvokeLoopback] !!! 自检失败: {InvokeLoopbackSummary}");
                    return false;
                }

                if (InvokeLoopbackMethod.ReturnType != typeof(void))
                {
                    InvokeLoopbackResolved = false;
                    InvokeLoopbackSummary = $"ReturnType={InvokeLoopbackMethod.ReturnType.FullName} 期望=System.Void";
                    RoleLogger.Error("[Shared]",
                        $"[ClientMethodLoopback/InvokeLoopback] !!! 自检失败: {InvokeLoopbackSummary}");
                    return false;
                }

                InvokeLoopbackResolved = true;
                InvokeLoopbackSummary = $"DeclaringType={InvokeLoopbackMethod.DeclaringType.Name} Name={InvokeLoopbackMethod.Name} IsPrivate={InvokeLoopbackMethod.IsPrivate}";
                RoleLogger.Info("[Shared]",
                    $"[ClientMethodLoopback/InvokeLoopback] OK 自检通过: {InvokeLoopbackSummary}");
                return true;
            }
            catch (System.Exception ex)
            {
                InvokeLoopbackResolved = false;
                InvokeLoopbackSummary = $"异常: {ex.Message}";
                RoleLogger.Error("[Shared]",
                    $"[ClientMethodLoopback/InvokeLoopback] !!! 自检异常: {ex}");
                return false;
            }
        }

        /// <summary>
        /// v0.2.3.11 Critical-1：从基类 MethodInfo 调用 InvokeLoopback。
        /// 基类 private 方法可以被基类 MethodInfo + 派生实例调用（.NET 反射语义允许）。
        /// </summary>
        private static void InvokeLoopback(ClientMethodHandle instance, NetPakWriter writer)
        {
            if (InvokeLoopbackMethod == null)
            {
                throw new MissingMethodException(
                    typeof(ClientMethodHandle).FullName,
                    "InvokeLoopback(NetPakWriter)");
            }
            InvokeLoopbackMethod.Invoke(instance, new object[] { writer });
        }

        /// <summary>
        /// v0.2.3.10：手动登记三个 Prefix（Codex 第七次审计 P0-A）。
        /// v0.2.3.11 Critical-1：登记前先自检 InvokeLoopbackMethod，失败则 fail-closed。
        /// v0.2.3.11 High-1：登记后用精确 1/1 规则验证（totalPrefixCount==1 + exactMatchCount==1）。
        /// </summary>
        public static bool RegisterManual(Harmony harmony)
        {
            RoleLogger.Info("[Shared]", "[ClientMethodLoopback] === 手动登记 3 个 Prefix（P0-A）===");

            if (harmony == null)
            {
                RoleLogger.Error("[Shared]", "[ClientMethodLoopback] harmony=null，无法登记");
                AllRegistrationsSucceeded = false;
                RegistrationSummary = "harmony=null";
                return false;
            }

            // v0.2.3.11 Critical-1：先自检 InvokeLoopbackMethod，失败则 fail-closed
            bool invokeLoopbackOk = VerifyInvokeLoopbackMethod();
            if (!invokeLoopbackOk)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = $"InvokeLoopback 自检失败 ({InvokeLoopbackSummary})";
                RoleLogger.Error("[Shared]",
                    $"[ClientMethodLoopback] !!! InvokeLoopback 自检失败，fail-closed 不登记 Prefix summary={RegistrationSummary}");
                return false;
            }

            bool ok1 = RegisterOnePrefix(harmony,
                typeof(ClientMethodHandle), "SendAndLoopbackIfLocal",
                new System.Type[] {
                    typeof(ENetReliability),
                    typeof(ITransportConnection),
                    typeof(NetPakWriter)
                },
                PrefixIfLocalName,
                "SendAndLoopbackIfLocal");

            bool ok2 = RegisterOnePrefix(harmony,
                typeof(ClientMethodHandle), "SendAndLoopbackIfAnyAreLocal",
                new System.Type[] {
                    typeof(ENetReliability),
                    typeof(List<ITransportConnection>),
                    typeof(NetPakWriter)
                },
                PrefixIfAnyAreLocalName,
                "SendAndLoopbackIfAnyAreLocal");

            bool ok3 = RegisterOnePrefix(harmony,
                typeof(ClientMethodHandle), "SendAndLoopback",
                new System.Type[] {
                    typeof(ENetReliability),
                    typeof(List<ITransportConnection>),
                    typeof(NetPakWriter)
                },
                PrefixSendAndLoopbackName,
                "SendAndLoopback");

            bool all = ok1 && ok2 && ok3;
            AllRegistrationsSucceeded = all;
            RegistrationSummary = $"IfLocal={ok1}, IfAnyAreLocal={ok2}, SendAndLoopback={ok3}, InvokeLoopback={invokeLoopbackOk}";

            if (all)
            {
                RoleLogger.Info("[Shared]",
                    $"[ClientMethodLoopback] OK 3/3 手动登记成功 summary={RegistrationSummary}");
            }
            else
            {
                RoleLogger.Error("[Shared]",
                    $"[ClientMethodLoopback] !!! 手动登记未全成功 summary={RegistrationSummary}");
            }

            return all;
        }

        private static bool RegisterOnePrefix(Harmony harmony,
            System.Type targetType, string targetMethodName,
            System.Type[] targetParamTypes,
            string prefixMethodName, string label)
        {
            string tag = $"[ClientMethodLoopback/{label}]";
            try
            {
                if (targetType == null)
                {
                    RoleLogger.Error("[Shared]", $"{tag} targetType=null");
                    return false;
                }

                MethodInfo original = AccessTools.Method(targetType, targetMethodName, targetParamTypes);
                if (original == null)
                {
                    RoleLogger.Error("[Shared]",
                        $"{tag} AccessTools.Method 返回 null type={targetType.FullName} method={targetMethodName} argCount={targetParamTypes?.Length ?? 0}");
                    return false;
                }

                // 检查当前 Harmony owner 下精确 patch method 是否已存在
                HarmonyLib.Patches existing = Harmony.GetPatchInfo(original);
                int existingTotal = existing?.Prefixes?.Count ?? 0;
                int existingExact = CountExactPrefix(existing?.Prefixes, prefixMethodName);

                if (existingExact >= 1)
                {
                    // v0.2.3.11 High-1：精确方法已存在视为成功（幂等），但额外检查 total 是否恰好 1
                    if (existingTotal == 1)
                    {
                        RoleLogger.Info("[Shared]",
                            $"{tag} SKIP 已存在本 owner 下的精确 Prefix 1/1 (total={existingTotal})");
                        return true;
                    }
                    else
                    {
                        RoleLogger.Error("[Shared]",
                            $"{tag} !!! 精确 Prefix 已存在但 total={existingTotal} > 1，存在重复或冲突 Prefix");
                        return false;
                    }
                }

                MethodInfo prefix = AccessTools.Method(typeof(ClientMethodLoopbackPatch), prefixMethodName);
                if (prefix == null)
                {
                    RoleLogger.Error("[Shared]",
                        $"{tag} Prefix 方法未找到 {typeof(ClientMethodLoopbackPatch).FullName}.{prefixMethodName}");
                    return false;
                }

                harmony.Patch(original, prefix: new HarmonyMethod(prefix));

                // v0.2.3.11 High-1：登记后用精确 1/1 规则验证
                HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
                int totalPrefixCount = info?.Prefixes?.Count ?? 0;
                int exactMatchCount = CountExactPrefix(info?.Prefixes, prefixMethodName);

                if (totalPrefixCount != 1)
                {
                    RoleLogger.Error("[Shared]",
                        $"{tag} !!! 登记后 totalPrefixCount={totalPrefixCount} 期望=1");
                    return false;
                }
                if (exactMatchCount != 1)
                {
                    RoleLogger.Error("[Shared]",
                        $"{tag} !!! 登记后 exactMatchCount={exactMatchCount} 期望=1");
                    return false;
                }

                RoleLogger.Info("[Shared]",
                    $"{tag} OK 手动登记成功 total=1 exact=1");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"{tag} 异常: {ex}");
                return false;
            }
        }

        /// <summary>
        /// v0.2.3.11 High-1：统计当前 Harmony owner + DeclaringType==ClientMethodLoopbackPatch + Name 匹配的 Prefix 数量。
        /// </summary>
        private static int CountExactPrefix(IList<HarmonyLib.Patch> list, string expectedPrefixName)
        {
            if (list == null || list.Count == 0) return 0;
            int count = 0;
            foreach (HarmonyLib.Patch p in list)
            {
                if (p.owner != SteamP2PFriendsPlugin.HARMONY_ID) continue;
                MethodInfo pm = p.PatchMethod;
                if (ReferenceEquals(pm, null)) continue;
                if (pm.DeclaringType == typeof(ClientMethodLoopbackPatch)
                    && pm.Name == expectedPrefixName)
                {
                    count++;
                }
            }
            return count;
        }

        [HarmonyPatch(typeof(ClientMethodHandle), "SendAndLoopbackIfLocal")]
        [HarmonyPrefix]
        public static bool SendAndLoopbackIfLocal_Prefix(ClientMethodHandle __instance, ENetReliability reliability,
            ITransportConnection transportConnection, NetPakWriter writer)
        {
            bool shouldProcess = HostManager.ShouldProcessClientHostListen();

            if (!shouldProcess)
            {
                LogLocalOnce(nameof(SendAndLoopbackIfLocal_Prefix),
                    shouldProcess, "transport=n/a", "vanilla passthrough",
                    reliability, -1, 0, 0, false);
                return true;
            }

            writer.Flush();

            int payloadBytes = writer.writeByteIndex;
            bool isLoopback = transportConnection is SDG.NetTransport.Loopback.TransportConnection_Loopback;
            string transportDesc = DescribeTransport(transportConnection);

            if (isLoopback)
            {
                LogLocalOnce(nameof(SendAndLoopbackIfLocal_Prefix),
                    shouldProcess, transportDesc, "InvokeLoopback",
                    reliability, payloadBytes, 0, 0, true);
                InvokeLoopback(__instance, writer);
                return false;
            }

            // v0.2.3.12 Medium-1：远程分支 1 次调用 1 个 correlation 序号
            //   attempt + send-success 共享 seq，仅消耗 1 次额度
            int seq = NextRemoteCallSeq(nameof(SendAndLoopbackIfLocal_Prefix));
            LogRemoteStage(seq, nameof(SendAndLoopbackIfLocal_Prefix), "attempt",
                shouldProcess, transportDesc, reliability, payloadBytes, 1, 0, false);
            transportConnection.Send(writer.buffer, writer.writeByteIndex, reliability);
            LogRemoteStage(seq, nameof(SendAndLoopbackIfLocal_Prefix), "send-success",
                shouldProcess, transportDesc, reliability, payloadBytes, 1, 1, false);
            return false;
        }

        [HarmonyPatch(typeof(ClientMethodHandle), "SendAndLoopbackIfAnyAreLocal")]
        [HarmonyPrefix]
        public static bool SendAndLoopbackIfAnyAreLocal_Prefix(ClientMethodHandle __instance, ENetReliability reliability,
            List<ITransportConnection> transportConnections, NetPakWriter writer)
        {
            bool shouldProcess = HostManager.ShouldProcessClientHostListen();

            if (!shouldProcess)
            {
                LogLocalOnce(nameof(SendAndLoopbackIfAnyAreLocal_Prefix),
                    shouldProcess, $"listCount={transportConnections?.Count ?? 0}", "vanilla passthrough",
                    reliability, -1, 0, 0, false);
                return true;
            }

            writer.Flush();

            int payloadBytes = writer.writeByteIndex;

            // v0.2.3.12 Medium-1：先扫描规划远程连接数与 loopback 标记，供 attempt 日志提前输出
            int plannedRemote = 0;
            bool shouldLoopback = false;
            if (transportConnections != null)
            {
                foreach (ITransportConnection tc in transportConnections)
                {
                    if (tc is SDG.NetTransport.Loopback.TransportConnection_Loopback)
                    {
                        shouldLoopback = true;
                        continue;
                    }
                    plannedRemote++;
                }
            }

            bool isRemote = plannedRemote > 0;
            int seq = isRemote ? NextRemoteCallSeq(nameof(SendAndLoopbackIfAnyAreLocal_Prefix)) : 0;
            string transportDesc = $"listCount={transportConnections?.Count ?? 0} plannedRemote={plannedRemote} loopback={shouldLoopback}";

            // 阶段 1：attempt（含 plannedRemote），仅远程调用记录
            if (isRemote)
            {
                LogRemoteStage(seq, nameof(SendAndLoopbackIfAnyAreLocal_Prefix), "attempt",
                    shouldProcess, transportDesc, reliability, payloadBytes, plannedRemote, 0, shouldLoopback);
            }

            // 阶段 2：执行远程发送
            int attemptedRemote = 0;
            int sentRemote = 0;
            if (transportConnections != null)
            {
                foreach (ITransportConnection tc in transportConnections)
                {
                    if (tc is SDG.NetTransport.Loopback.TransportConnection_Loopback) continue;
                    attemptedRemote++;
                    tc.Send(writer.buffer, writer.writeByteIndex, reliability);
                    sentRemote++;
                }
            }

            // 阶段 3：send-success（仅远程调用记录）
            if (isRemote)
            {
                LogRemoteStage(seq, nameof(SendAndLoopbackIfAnyAreLocal_Prefix), "send-success",
                    shouldProcess, transportDesc, reliability, payloadBytes, attemptedRemote, sentRemote, shouldLoopback);
            }

            // 阶段 4：本机 InvokeLoopback（仅当存在 loopback 连接）
            if (shouldLoopback)
            {
                InvokeLoopback(__instance, writer);

                // 阶段 5：loopback-success（仅远程调用记录，本地 only 走 LogLocalOnce）
                if (isRemote)
                {
                    LogRemoteStage(seq, nameof(SendAndLoopbackIfAnyAreLocal_Prefix), "loopback-success",
                        shouldProcess, transportDesc, reliability, payloadBytes, attemptedRemote, sentRemote, shouldLoopback);
                }
                else
                {
                    LogLocalOnce(nameof(SendAndLoopbackIfAnyAreLocal_Prefix),
                        shouldProcess, transportDesc, "InvokeLoopback only",
                        reliability, payloadBytes, 0, 0, true);
                }
            }
            else if (!isRemote)
            {
                // 无远程无 loopback：no-op，记 local 额度
                LogLocalOnce(nameof(SendAndLoopbackIfAnyAreLocal_Prefix),
                    shouldProcess, transportDesc, "no-op",
                    reliability, payloadBytes, 0, 0, false);
            }

            return false;
        }

        [HarmonyPatch(typeof(ClientMethodHandle), "SendAndLoopback")]
        [HarmonyPrefix]
        public static bool SendAndLoopback_Prefix(ClientMethodHandle __instance, ENetReliability reliability,
            List<ITransportConnection> transportConnections, NetPakWriter writer)
        {
            bool shouldProcess = HostManager.ShouldProcessClientHostListen();

            if (!shouldProcess)
            {
                LogLocalOnce(nameof(SendAndLoopback_Prefix),
                    shouldProcess, $"listCount={transportConnections?.Count ?? 0}", "vanilla passthrough",
                    reliability, -1, 0, 0, false);
                return true;
            }

            writer.Flush();

            int payloadBytes = writer.writeByteIndex;

            // v0.2.3.12 Medium-1：先扫描规划远程连接数，SendAndLoopback 无条件 InvokeLoopback
            int plannedRemote = 0;
            if (transportConnections != null)
            {
                foreach (ITransportConnection tc in transportConnections)
                {
                    if (tc is SDG.NetTransport.Loopback.TransportConnection_Loopback) continue;
                    plannedRemote++;
                }
            }

            bool isRemote = plannedRemote > 0;
            int seq = isRemote ? NextRemoteCallSeq(nameof(SendAndLoopback_Prefix)) : 0;
            string transportDesc = $"listCount={transportConnections?.Count ?? 0} plannedRemote={plannedRemote} loopback=always";

            // 阶段 1：attempt（含 plannedRemote）
            if (isRemote)
            {
                LogRemoteStage(seq, nameof(SendAndLoopback_Prefix), "attempt",
                    shouldProcess, transportDesc, reliability, payloadBytes, plannedRemote, 0, true);
            }

            // 阶段 2：执行远程发送
            int attemptedRemote = 0;
            int sentRemote = 0;
            if (transportConnections != null)
            {
                foreach (ITransportConnection tc in transportConnections)
                {
                    if (tc is SDG.NetTransport.Loopback.TransportConnection_Loopback) continue;
                    attemptedRemote++;
                    tc.Send(writer.buffer, writer.writeByteIndex, reliability);
                    sentRemote++;
                }
            }

            // 阶段 3：send-success
            if (isRemote)
            {
                LogRemoteStage(seq, nameof(SendAndLoopback_Prefix), "send-success",
                    shouldProcess, transportDesc, reliability, payloadBytes, attemptedRemote, sentRemote, true);
            }

            // 阶段 4：SendAndLoopback 无条件 InvokeLoopback
            InvokeLoopback(__instance, writer);

            // 阶段 5：loopback-success（仅远程调用记录，本地 only 走 LogLocalOnce）
            if (isRemote)
            {
                LogRemoteStage(seq, nameof(SendAndLoopback_Prefix), "loopback-success",
                    shouldProcess, transportDesc, reliability, payloadBytes, attemptedRemote, sentRemote, true);
            }
            else
            {
                LogLocalOnce(nameof(SendAndLoopback_Prefix),
                    shouldProcess, transportDesc, "InvokeLoopback only",
                    reliability, payloadBytes, 0, 0, true);
            }

            return false;
        }

        /// <summary>
        /// v0.2.3.12 Medium-1：为指定 Prefix 分配下一次远程调用的 correlation 序号。
        ///   一次远程调用无论产生多少阶段日志，仅分配 1 个序号、消耗 1 次额度。
        /// </summary>
        private static int NextRemoteCallSeq(string prefixName)
        {
            switch (prefixName)
            {
                case nameof(SendAndLoopbackIfLocal_Prefix):
                    return ++_ifLocalRemoteCallSeq;
                case nameof(SendAndLoopbackIfAnyAreLocal_Prefix):
                    return ++_ifAnyAreLocalRemoteCallSeq;
                case nameof(SendAndLoopback_Prefix):
                    return ++_sendAndLoopbackRemoteCallSeq;
                default:
                    return int.MaxValue;
            }
        }

        /// <summary>
        /// v0.2.3.12 Medium-1：远程分支阶段日志（attempt / send-success / loopback-success）。
        ///   同一 correlation 序号可多次调用，仅当 seq <= BranchLogLimit 时输出。
        ///   不递增计数器——计数器在 NextRemoteCallSeq 中递增。
        /// </summary>
        private static void LogRemoteStage(int seq, string prefixName, string stage,
            bool shouldProcess, string transportDesc, ENetReliability reliability,
            int payloadBytes, int attemptedRemote, int sentRemote, bool loopback)
        {
            if (seq > BranchLogLimit) return;
            RoleLogger.Info("[Host]",
                $"[ClientMethodLoopback/branch] remote #{seq}/{BranchLogLimit} prefix={prefixName} stage={stage} " +
                $"ShouldProcessClientHostListen={shouldProcess} transport={transportDesc} " +
                $"reliability={reliability} payloadBytes={payloadBytes} " +
                $"attemptedRemote={attemptedRemote} sentRemote={sentRemote} loopback={loopback}");
        }

        /// <summary>
        /// v0.2.3.12 Medium-1：local/passthrough 分支单次日志（1 次调用 1 条日志，消耗 local 额度）。
        ///   用于 vanilla passthrough、loopback-only、no-op 三类非远程分支。
        /// </summary>
        private static void LogLocalOnce(string prefixName, bool shouldProcess,
            string transportDesc, string branch, ENetReliability reliability,
            int payloadBytes, int attemptedRemote, int sentRemote, bool loopback)
        {
            int count;
            switch (prefixName)
            {
                case nameof(SendAndLoopbackIfLocal_Prefix):
                    count = ++_ifLocalLocalCount;
                    break;
                case nameof(SendAndLoopbackIfAnyAreLocal_Prefix):
                    count = ++_ifAnyAreLocalLocalCount;
                    break;
                case nameof(SendAndLoopback_Prefix):
                    count = ++_sendAndLoopbackLocalCount;
                    break;
                default:
                    return;
            }

            if (count > BranchLogLimit) return;

            RoleLogger.Info("[Host]",
                $"[ClientMethodLoopback/branch] local #{count}/{BranchLogLimit} prefix={prefixName} " +
                $"ShouldProcessClientHostListen={shouldProcess} transport={transportDesc} " +
                $"branch={branch} reliability={reliability} payloadBytes={payloadBytes} " +
                $"attemptedRemote={attemptedRemote} sentRemote={sentRemote} loopback={loopback}");
        }

        private static string DescribeTransport(ITransportConnection transport)
        {
            if (transport == null) return "null";
            return transport.GetType().Name;
        }
    }
}
