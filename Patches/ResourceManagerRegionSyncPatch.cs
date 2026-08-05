using HarmonyLib;
using SDG.NetPak;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.29 新增（Codex 第十八次审计 P0-B 授权实施）：
    /// ResourceManager.onRegionUpdated step 3 远程区域同步资格 patch + SendResources_Write 决定性日志。
    ///
    /// 授权来源：第十八次双机测试外部审计裁决-Codex §5.1
    ///   "P0-B 允许修改：
    ///     2. ResourceManager.onRegionUpdated 中仅替换 step 3 外层的一个 dedicated getter。"
    ///
    /// 目标：解除 listen server 模式下"主机不向远程客机发送 Resources RPC"的诅咒。
    /// vanilla 源码（U3-SDK ResourceManager.cs L713-780，step 3 @ L755）：
    ///   if (step == 3)
    ///   {
    ///       if (Dedicator.IsDedicatedServer)   <-- Transpiler 替换此调用（L757，全方法唯一一处）
    ///       {
    ///           // 遍历玩家周围 RESOURCE_REGIONS 范围，调 SendResources.Invoke 发送（L772）
    ///       }
    ///   }
    ///
    /// 发送方法说明：
    ///   SendResources 是 ClientStaticMethod 字段（L477），无独立 ask 方法。
    ///   实际写入由 SendResources_Write(NetPakWriter, byte, byte)（L530）完成。
    ///   本 patch 对 SendResources_Write 加 Prefix 决定性日志，证明 SendResources 真实触发。
    ///
    /// helper 语义（Codex §5.1.4-5.5）：
    ///   原生 dedicated，或 Provider.isServer && HostManager.IsP2PHostMode && recipient 为真实远端非 loopback 玩家。
    ///   ListenRegionSyncEligibility.IsDedicatedOrP2PRemoteRecipient(Player) 已实现此语义。
    ///
    /// 严格自检（Codex §5.1.7 + §6 静态阶段）：
    ///   - onRegionUpdated 签名精确解析（private instance, 7 args: Player, byte x5, ref bool）
    ///   - SendResources_Write 签名精确解析（private static, 3 args: NetPakWriter, byte, byte）
    ///   - Transpiler replacement count 必须精确等于 1
    ///   - SendResources_Write Prefix 必须登记成功
    ///   - Transpiler owner 为 com.yu80rice.steamp2pfriends，patch method 为 ResourceManagerRegionSyncPatch.OnRegionUpdated_Transpiler，count=1
    ///   - SendResources_Write Prefix owner 为 com.yu80rice.steamp2pfriends，patch method 为 ResourceManagerRegionSyncPatch.SendResources_Write_Prefix，count=1
    ///   任一失败并入 DiagnosticBuildValid fail-closed。
    ///
    /// 严格禁止（Codex §5.1.6）：
    ///   - 不手动写 loaded flag
    ///   - 不在 Postfix 重复调用发送方法
    ///   - 不伪造全局 dedicated 状态
    ///   - 不修改 load/generate/client cleanup 分支
    /// </summary>
    public static class ResourceManagerRegionSyncPatch
    {
        public static bool AllRegistrationsSucceeded { get; private set; }
        public static string RegistrationSummary { get; private set; } = "未登记";
        public static int ReplacementCount { get; private set; } = -1;
        public static bool SignatureResolved { get; private set; }
        public static string SignatureSummary { get; private set; } = "未自检";
        public static bool SendResourcesWritePrefixRegistered { get; private set; }

        public static bool TranspilerOwnerVerified { get; private set; }
        public static bool PrefixOwnerVerified { get; private set; }
        public static string TranspilerOwnerSummary { get; private set; } = "未自检";
        public static string PrefixOwnerSummary { get; private set; } = "未自检";

        private const int WriteLogLimit = 12;
        private static int _writeLogCount;

        private const string HarmonyId = SteamP2PFriendsPlugin.HARMONY_ID;
        private const string TargetMethodName = "onRegionUpdated";
        private const string SendResourcesWriteMethodName = "SendResources_Write";
        private const string PatchTranspilerName = nameof(OnRegionUpdated_Transpiler);
        private const string PatchSendResourcesWritePrefixName = nameof(SendResources_Write_Prefix);

        public static bool RegisterManual(Harmony harmony)
        {
            RoleLogger.Info("[Shared]", "[ResourceRegionSync] === 手动登记 Transpiler + SendResources_Write Prefix（v0.2.3.29 P0-B）===");

            if (harmony == null)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = "harmony=null";
                RoleLogger.Error("[Shared]", $"[ResourceRegionSync] !!! {RegistrationSummary}");
                return false;
            }

            bool sigOk = VerifyTargetSignature();
            if (!sigOk)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = $"onRegionUpdated 签名自检失败 ({SignatureSummary})";
                RoleLogger.Error("[Shared]", $"[ResourceRegionSync] !!! {RegistrationSummary}");
                return false;
            }

            bool transpilerOk = RegisterTranspiler(harmony);
            if (!transpilerOk)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = $"Transpiler 登记失败 (replacement={ReplacementCount})";
                RoleLogger.Error("[Shared]", $"[ResourceRegionSync] !!! {RegistrationSummary}");
                return false;
            }

            bool prefixOk = RegisterSendResourcesWritePrefix(harmony);
            SendResourcesWritePrefixRegistered = prefixOk;
            if (!prefixOk)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = "SendResources_Write Prefix 登记失败";
                RoleLogger.Error("[Shared]", $"[ResourceRegionSync] !!! {RegistrationSummary}");
                return false;
            }

            AllRegistrationsSucceeded = true;
            RegistrationSummary = $"signature={SignatureResolved}, replacement=1/1, sendResourcesWritePrefix=true, " +
                $"transpilerOwner={TranspilerOwnerVerified}, prefixOwner={PrefixOwnerVerified}";
            RoleLogger.Info("[Shared]",
                $"[ResourceRegionSync] OK 手动登记成功 summary={RegistrationSummary}");
            return true;
        }

        private static bool RegisterTranspiler(Harmony harmony)
        {
            try
            {
                MethodInfo original = AccessTools.Method(typeof(ResourceManager), TargetMethodName);
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", "[ResourceRegionSync] !!! onRegionUpdated AccessTools.Method 返回 null");
                    return false;
                }

                MethodInfo transpiler = AccessTools.Method(typeof(ResourceManagerRegionSyncPatch), PatchTranspilerName);
                if (transpiler == null)
                {
                    RoleLogger.Error("[Shared]", "[ResourceRegionSync] !!! Transpiler 方法未找到");
                    return false;
                }

                harmony.Patch(original, transpiler: new HarmonyMethod(transpiler));

                if (ReplacementCount != 1)
                {
                    RoleLogger.Error("[Shared]",
                        $"[ResourceRegionSync] !!! DIAGNOSTIC BUILD INVALID: replacement count={ReplacementCount} 期望=1");
                    return false;
                }

                bool ownerOk = VerifyPatchOwner(original, isTranspiler: true);
                if (!ownerOk)
                {
                    RoleLogger.Error("[Shared]",
                        $"[ResourceRegionSync] !!! DIAGNOSTIC BUILD INVALID: Transpiler owner 自检失败 summary={TranspilerOwnerSummary}");
                    return false;
                }

                RoleLogger.Info("[Shared]",
                    $"[ResourceRegionSync] OK Transpiler 已登记 (replacement=1/1, owner={TranspilerOwnerVerified})");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[ResourceRegionSync] !!! RegisterTranspiler 异常: {ex}");
                return false;
            }
        }

        private static bool RegisterSendResourcesWritePrefix(Harmony harmony)
        {
            try
            {
                // SendResources_Write private static 方法签名：void SendResources_Write(NetPakWriter, byte, byte)
                MethodInfo original = AccessTools.Method(typeof(ResourceManager), SendResourcesWriteMethodName,
                    new System.Type[] { typeof(NetPakWriter), typeof(byte), typeof(byte) });
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", "[ResourceRegionSync] !!! SendResources_Write(3 args) AccessTools.Method 返回 null");
                    return false;
                }

                MethodInfo prefix = AccessTools.Method(typeof(ResourceManagerRegionSyncPatch), PatchSendResourcesWritePrefixName);
                if (prefix == null)
                {
                    RoleLogger.Error("[Shared]", "[ResourceRegionSync] !!! SendResources_Write Prefix 方法未找到");
                    return false;
                }

                harmony.Patch(original, prefix: new HarmonyMethod(prefix));

                bool ownerOk = VerifyPatchOwner(original, isTranspiler: false);
                if (!ownerOk)
                {
                    RoleLogger.Error("[Shared]",
                        $"[ResourceRegionSync] !!! DIAGNOSTIC BUILD INVALID: SendResources_Write Prefix owner 自检失败 summary={PrefixOwnerSummary}");
                    return false;
                }

                RoleLogger.Info("[Shared]",
                    $"[ResourceRegionSync] OK SendResources_Write Prefix 已登记 (owner={PrefixOwnerVerified})");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[ResourceRegionSync] !!! RegisterSendResourcesWritePrefix 异常: {ex}");
                return false;
            }
        }

        private static bool VerifyPatchOwner(MethodInfo original, bool isTranspiler)
        {
            try
            {
                HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
                System.Collections.ICollection patches = isTranspiler
                    ? (info?.Transpilers as System.Collections.ICollection)
                    : (info?.Prefixes as System.Collections.ICollection);

                if (patches == null || patches.Count == 0)
                {
                    if (isTranspiler)
                    {
                        TranspilerOwnerVerified = false;
                        TranspilerOwnerSummary = "transpilers count=0";
                    }
                    else
                    {
                        PrefixOwnerVerified = false;
                        PrefixOwnerSummary = "prefixes count=0";
                    }
                    return false;
                }

                string expectedMethodName = isTranspiler ? PatchTranspilerName : PatchSendResourcesWritePrefixName;
                System.Type expectedDeclaringType = typeof(ResourceManagerRegionSyncPatch);

                int ownCount = 0;
                bool methodMatched = false;
                string firstForeignOwner = null;

                foreach (Patch p in patches)
                {
                    if (p.owner == HarmonyId)
                    {
                        ownCount++;
                        MethodInfo patchMethod = p.PatchMethod;
                        if (patchMethod != null
                            && patchMethod.DeclaringType == expectedDeclaringType
                            && patchMethod.Name == expectedMethodName)
                        {
                            methodMatched = true;
                        }
                    }
                    else if (firstForeignOwner == null)
                    {
                        firstForeignOwner = p.owner;
                    }
                }

                string summary = $"ownCount={ownCount} methodMatched={methodMatched} foreignOwner={firstForeignOwner ?? "none"}";

                if (ownCount != 1 || !methodMatched)
                {
                    if (isTranspiler)
                    {
                        TranspilerOwnerVerified = false;
                        TranspilerOwnerSummary = summary;
                    }
                    else
                    {
                        PrefixOwnerVerified = false;
                        PrefixOwnerSummary = summary;
                    }
                    return false;
                }

                if (isTranspiler)
                {
                    TranspilerOwnerVerified = true;
                    TranspilerOwnerSummary = summary;
                }
                else
                {
                    PrefixOwnerVerified = true;
                    PrefixOwnerSummary = summary;
                }
                return true;
            }
            catch (System.Exception ex)
            {
                if (isTranspiler)
                {
                    TranspilerOwnerVerified = false;
                    TranspilerOwnerSummary = $"异常: {ex.Message}";
                }
                else
                {
                    PrefixOwnerVerified = false;
                    PrefixOwnerSummary = $"异常: {ex.Message}";
                }
                return false;
            }
        }

        private static bool VerifyTargetSignature()
        {
            try
            {
                MethodInfo method = AccessTools.Method(typeof(ResourceManager), TargetMethodName);
                if (method == null)
                {
                    SignatureResolved = false;
                    SignatureSummary = "AccessTools.Method 返回 null";
                    return false;
                }

                if (method.Name != TargetMethodName)
                {
                    SignatureResolved = false;
                    SignatureSummary = $"Name={method.Name} 期望={TargetMethodName}";
                    return false;
                }

                if (method.IsStatic)
                {
                    SignatureResolved = false;
                    SignatureSummary = "IsStatic=true 期望=false";
                    return false;
                }

                ParameterInfo[] ps = method.GetParameters();
                if (ps.Length != 7)
                {
                    SignatureResolved = false;
                    SignatureSummary = $"paramCount={ps.Length} 期望=7";
                    return false;
                }

                if (ps[0].ParameterType != typeof(Player))
                {
                    SignatureResolved = false;
                    SignatureSummary = $"param[0]={ps[0].ParameterType.Name} 期望=Player";
                    return false;
                }

                for (int i = 1; i <= 5; i++)
                {
                    if (ps[i].ParameterType != typeof(byte))
                    {
                        SignatureResolved = false;
                        SignatureSummary = $"param[{i}]={ps[i].ParameterType.Name} 期望=byte";
                        return false;
                    }
                }

                if (ps[6].ParameterType != typeof(bool).MakeByRefType())
                {
                    SignatureResolved = false;
                    SignatureSummary = $"param[6]={ps[6].ParameterType.Name} 期望=bool&";
                    return false;
                }

                if (method.ReturnType != typeof(void))
                {
                    SignatureResolved = false;
                    SignatureSummary = $"ReturnType={method.ReturnType.Name} 期望=void";
                    return false;
                }

                SignatureResolved = true;
                SignatureSummary = "private instance void onRegionUpdated(Player,byte,byte,byte,byte,byte,ref bool)";
                RoleLogger.Info("[Shared]",
                    $"[ResourceRegionSync] OK 签名自检通过: {SignatureSummary}");
                return true;
            }
            catch (System.Exception ex)
            {
                SignatureResolved = false;
                SignatureSummary = $"异常: {ex.Message}";
                RoleLogger.Error("[Shared]", $"[ResourceRegionSync] !!! 签名自检异常: {ex}");
                return false;
            }
        }

        /// <summary>
        /// v0.2.3.29 P0-B Transpiler 主实现。
        /// 替换 vanilla onRegionUpdated step 3 中的 Dedicator.get_IsDedicatedServer() 调用
        /// 为 ListenRegionSyncEligibility.IsDedicatedOrP2PRemoteRecipient(player)。
        ///
        /// 栈平衡：
        ///   原版：call get_IsDedicatedServer()（无参数，返回 bool i4）=> 栈净变化 +1
        ///   替换：ldarg.1（压入 Player）+ call IsDedicatedOrP2PRemoteRecipient(Player)（消费 1，返回 bool i4）=> 栈净变化 +1
        ///   一致。
        /// </summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(ResourceManager), TargetMethodName)]
        public static IEnumerable<CodeInstruction> OnRegionUpdated_Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var codes = new List<CodeInstruction>(instructions);

            MethodInfo dedicatedGetter = AccessTools.PropertyGetter(typeof(Dedicator), nameof(Dedicator.IsDedicatedServer));
            MethodInfo eligibilityMethod = AccessTools.Method(
                typeof(ListenRegionSyncEligibility),
                nameof(ListenRegionSyncEligibility.IsDedicatedOrP2PRemoteRecipient),
                new System.Type[] { typeof(Player) });

            if (dedicatedGetter == null)
            {
                ReplacementCount = -1;
                throw new System.InvalidOperationException(
                    "ResourceManagerRegionSyncPatch: Dedicator.get_IsDedicatedServer not found");
            }

            if (eligibilityMethod == null)
            {
                ReplacementCount = -1;
                throw new System.InvalidOperationException(
                    "ResourceManagerRegionSyncPatch: IsDedicatedOrP2PRemoteRecipient not found");
            }

            int replacementCount = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                CodeInstruction instr = codes[i];
                if (instr == null) continue;

                if (instr.Calls(dedicatedGetter))
                {
                    instr.opcode = OpCodes.Ldarg_1;
                    instr.operand = null;
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, eligibilityMethod));
                    replacementCount++;
                    i++;
                }
            }

            ReplacementCount = replacementCount;

            if (replacementCount != 1)
            {
                throw new System.InvalidOperationException(
                    $"ResourceManagerRegionSyncPatch: replacement count={replacementCount} expected=1");
            }

            RoleLogger.Info("[Shared]",
                $"[ResourceRegionSync] OK Transpiler replacement=1/1，IL 修改已应用（原地修改，保留 labels/blocks）");
            return codes;
        }

        /// <summary>
        /// v0.2.3.29 P0-B：SendResources_Write Prefix 决定性日志。
        /// 仅记录日志，不影响原方法行为。
        /// SendResources_Write 签名：void SendResources_Write(NetPakWriter, byte, byte)
        /// 该方法由 ClientStaticMethod.Invoke 内部对每个目标连接调用一次，
        /// 故本 Prefix 触发次数 = 实际写入次数 = 真实发送目标数。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ResourceManager), SendResourcesWriteMethodName)]
        public static void SendResources_Write_Prefix(byte x, byte y)
        {
            int count = ++_writeLogCount;
            if (count > WriteLogLimit) return;

            string escPrefix = SteamP2PFriends.Host.HostManager.EscPauseDetectorEnabled
                ? $"escPaused={SteamP2PFriends.Host.HostManager.IsEscPausedCurrent} "
                : "";

            RoleLogger.Info("[Host]",
                $"[ListenRegionSync/Resource] write #{count}/{WriteLogLimit} " +
                $"{escPrefix}step=3 region=({x},{y})");
        }

        public static void OnClientDisconnected()
        {
            // SendResources_Write 使用全局计数，不按玩家区分，无需按断线清理
        }

        public static void ResetAll()
        {
            int cleared = _writeLogCount;
            _writeLogCount = 0;
            RoleLogger.Info("[Shared]",
                $"[ResourceRegionSync] ResetAll 清空 write 计数 (was={cleared})");
        }
    }
}
