using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    ///
    /// 根因（U3-SDK ZombieManager.cs:1653-1684 updateRegionsAndSendZombieStates）：
    ///   L1655-1658 遍历 regions，调用 region.UpdateRegion()
    ///   L1660 if (region.updates > 0)
    ///   L1662 if (Dedicator.IsDedicatedServer)  <-- 阻断点
    ///     L1664 seq++
    ///     L1665 SendZombieStates.Invoke(Unreliable, GatherRemoteClientConnections(regionIndex),
    ///                                  SendZombieStates_Write, regionIndex)
    ///     L1668 region.updates = 0
    ///   L1670 else
    ///     L1672-1678 foreach zombies: isUpdated=false（仅清空，不发送）
    ///     L1680 region.updates = 0
    ///
    /// 问题：listen host 模式下 Dedicator.IsDedicatedServer=false，走 else 分支，
    ///   僵尸 isUpdated 被清空但不发送 SendZombieStates，客机永远收不到僵尸周期性状态更新。
    ///   客机表现：僵尸在原地挠空气（位置不更新），但客机被"不可见"的真实僵尸攻击掉血。
    ///
    /// 第二十一次双机测试决定性证据：
    ///   - 主机日志：wouldSendZombieStates=False（15+ 次）
    ///   - 客机日志：0 次 ReceiveZombieStates
    ///
    ///   Transpiler 替换 L1662 的 Dedicator.IsDedicatedServer 调用
    ///   为 ListenRegionSyncEligibility.IsDedicatedOrP2PHost() 调用。
    ///
    ///   替换后语义：
    ///     - dedicated server: IsDedicatedOrP2PHost=true（vanilla 行为不变）
    ///     - listen host: IsDedicatedOrP2PHost=true（新增，走 dedicated 分支发送 SendZombieStates）
    ///     - 普通单机/客机/菜单: IsDedicatedOrP2PHost=false（走 else 分支，仅清空）
    ///
    ///   GatherRemoteClientConnections(regionIndex) 是 public static method（ZombieManager.cs:1936），
    ///   内部已排除 loopback 玩家（vanilla L1936-1952），listen host 下 SendZombieStates
    ///   只会发往远端客机，不会发回主机本地玩家。
    ///
    /// 栈平衡：
    ///   原版：call get_IsDedicatedServer()（无参数，返回 bool i4）=> 栈净变化 +1
    ///   替换：call IsDedicatedOrP2PHost()（无参数，返回 bool i4）=> 栈净变化 +1
    ///   一致。
    ///
    /// 安全性：
    ///   - 不全局伪造 Dedicator.IsDedicatedServer
    ///   - 不替换 vanilla IL 之外的内容（仅替换 1 处 callvirt/call）
    ///   - 不修改 SendZombieStates_Write 实现
    ///   - 不干预 GatherRemoteClientConnections 的 loopback 排除逻辑
    ///   - 与 ZombieManagerWorldSyncDiagnosticPatch 的 updateRegionsAndSendZombieStates Prefix 共存
    ///     （Prefix 在 Transpiler 修改后的方法上仍会执行，诊断日志不受影响）
    ///
    ///   - 不替换 AnimalManager.tickAnimal L1019
    ///   - 不替换 AnimalManager.addAnimal L523
    ///   - 不替换 VehicleManager.Update L2853
    /// </summary>
    public static class ZombieManagerP0C1SendZombieStatesPatch
    {
        public static bool AllRegistrationsSucceeded { get; private set; }
        public static string RegistrationSummary { get; private set; } = "未登记";
        public static int ReplacementCount { get; private set; } = -1;
        public static bool SignatureResolved { get; private set; }
        public static string SignatureSummary { get; private set; } = "未自检";

        public static bool TranspilerOwnerVerified { get; private set; }
        public static string TranspilerOwnerSummary { get; private set; } = "未自检";

        private const string HarmonyId = SteamP2PFriendsPlugin.HARMONY_ID;
        private const string TargetMethodName = "updateRegionsAndSendZombieStates";
        private const string PatchTranspilerName = nameof(UpdateRegionsAndSendZombieStates_Transpiler);

        public static bool RegisterManual(Harmony harmony)
        {
            RoleLogger.Info("[Shared]", "[P0-C-1/Zombie] === 手动登记 Transpiler（v0.2.3.33 P0-C-1 僵尸周期性状态广播）===");

            if (harmony == null)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = "harmony=null";
                RoleLogger.Error("[Shared]", $"[P0-C-1/Zombie] !!! {RegistrationSummary}");
                return false;
            }

            bool sigOk = VerifyTargetSignature();
            if (!sigOk)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = $"updateRegionsAndSendZombieStates 签名自检失败 ({SignatureSummary})";
                RoleLogger.Error("[Shared]", $"[P0-C-1/Zombie] !!! {RegistrationSummary}");
                return false;
            }

            bool transpilerOk = RegisterTranspiler(harmony);
            if (!transpilerOk)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = $"Transpiler 登记失败 (replacement={ReplacementCount})";
                RoleLogger.Error("[Shared]", $"[P0-C-1/Zombie] !!! {RegistrationSummary}");
                return false;
            }

            AllRegistrationsSucceeded = true;
            RegistrationSummary = $"signature={SignatureResolved}, replacement=1/1, transpilerOwner={TranspilerOwnerVerified}";
            RoleLogger.Info("[Shared]",
                $"[P0-C-1/Zombie] OK 手动登记成功 summary={RegistrationSummary}");
            return true;
        }

        private static bool RegisterTranspiler(Harmony harmony)
        {
            try
            {
                MethodInfo original = AccessTools.Method(typeof(ZombieManager), TargetMethodName);
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", "[P0-C-1/Zombie] !!! updateRegionsAndSendZombieStates AccessTools.Method 返回 null");
                    return false;
                }

                MethodInfo transpiler = AccessTools.Method(typeof(ZombieManagerP0C1SendZombieStatesPatch), PatchTranspilerName);
                if (transpiler == null)
                {
                    RoleLogger.Error("[Shared]", "[P0-C-1/Zombie] !!! Transpiler 方法未找到");
                    return false;
                }

                harmony.Patch(original, transpiler: new HarmonyMethod(transpiler));

                if (ReplacementCount != 1)
                {
                    RoleLogger.Error("[Shared]",
                        $"[P0-C-1/Zombie] !!! DIAGNOSTIC BUILD INVALID: replacement count={ReplacementCount} 期望=1");
                    return false;
                }

                bool ownerOk = VerifyPatchOwner(original);
                if (!ownerOk)
                {
                    RoleLogger.Error("[Shared]",
                        $"[P0-C-1/Zombie] !!! DIAGNOSTIC BUILD INVALID: Transpiler owner 自检失败 summary={TranspilerOwnerSummary}");
                    return false;
                }

                RoleLogger.Info("[Shared]",
                    $"[P0-C-1/Zombie] OK Transpiler 已登记 (replacement=1/1, owner={TranspilerOwnerVerified})");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P0-C-1/Zombie] !!! RegisterTranspiler 异常: {ex}");
                return false;
            }
        }

        private static bool VerifyPatchOwner(MethodInfo original)
        {
            try
            {
                HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
                System.Collections.ICollection patches = info?.Transpilers as System.Collections.ICollection;

                if (patches == null || patches.Count == 0)
                {
                    TranspilerOwnerVerified = false;
                    TranspilerOwnerSummary = "transpilers count=0";
                    return false;
                }

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
                            && patchMethod.DeclaringType == typeof(ZombieManagerP0C1SendZombieStatesPatch)
                            && patchMethod.Name == PatchTranspilerName)
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
                    TranspilerOwnerVerified = false;
                    TranspilerOwnerSummary = summary;
                    return false;
                }

                TranspilerOwnerVerified = true;
                TranspilerOwnerSummary = summary;
                return true;
            }
            catch (System.Exception ex)
            {
                TranspilerOwnerVerified = false;
                TranspilerOwnerSummary = $"异常: {ex.Message}";
                return false;
            }
        }

        private static bool VerifyTargetSignature()
        {
            try
            {
                MethodInfo method = AccessTools.Method(typeof(ZombieManager), TargetMethodName);
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
                if (ps.Length != 0)
                {
                    SignatureResolved = false;
                    SignatureSummary = $"paramCount={ps.Length} 期望=0";
                    return false;
                }

                if (method.ReturnType != typeof(void))
                {
                    SignatureResolved = false;
                    SignatureSummary = $"ReturnType={method.ReturnType.Name} 期望=void";
                    return false;
                }

                SignatureResolved = true;
                SignatureSummary = "private instance void updateRegionsAndSendZombieStates()";
                RoleLogger.Info("[Shared]",
                    $"[P0-C-1/Zombie] OK 签名自检通过: {SignatureSummary}");
                return true;
            }
            catch (System.Exception ex)
            {
                SignatureResolved = false;
                SignatureSummary = $"异常: {ex.Message}";
                RoleLogger.Error("[Shared]", $"[P0-C-1/Zombie] !!! 签名自检异常: {ex}");
                return false;
            }
        }

        /// <summary>
        /// 替换 vanilla updateRegionsAndSendZombieStates 中的 Dedicator.get_IsDedicatedServer() 调用
        /// 为 ListenRegionSyncEligibility.IsDedicatedOrP2PHost()。
        ///
        /// 栈平衡：
        ///   原版：call get_IsDedicatedServer()（无参数，返回 bool i4）=> 栈净变化 +1
        ///   替换：call IsDedicatedOrP2PHost()（无参数，返回 bool i4）=> 栈净变化 +1
        ///   一致。
        ///
        /// replacement count 必须精确等于 1（L1662 是全方法唯一一处 IsDedicatedServer 调用）。
        /// </summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(ZombieManager), TargetMethodName)]
        public static IEnumerable<CodeInstruction> UpdateRegionsAndSendZombieStates_Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var codes = new List<CodeInstruction>(instructions);

            MethodInfo dedicatedGetter = AccessTools.PropertyGetter(typeof(Dedicator), nameof(Dedicator.IsDedicatedServer));
            MethodInfo eligibilityMethod = AccessTools.Method(
                typeof(ListenRegionSyncEligibility),
                nameof(ListenRegionSyncEligibility.IsDedicatedOrP2PHost),
                System.Type.EmptyTypes);

            if (dedicatedGetter == null)
            {
                ReplacementCount = -1;
                throw new System.InvalidOperationException(
                    "ZombieManagerP0C1SendZombieStatesPatch: Dedicator.get_IsDedicatedServer not found");
            }

            if (eligibilityMethod == null)
            {
                ReplacementCount = -1;
                throw new System.InvalidOperationException(
                    "ZombieManagerP0C1SendZombieStatesPatch: IsDedicatedOrP2PHost not found");
            }

            int replacementCount = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                CodeInstruction instr = codes[i];
                if (instr == null) continue;

                if (instr.Calls(dedicatedGetter))
                {
                    instr.opcode = OpCodes.Call;
                    instr.operand = eligibilityMethod;
                    replacementCount++;
                }
            }

            ReplacementCount = replacementCount;

            if (replacementCount != 1)
            {
                throw new System.InvalidOperationException(
                    $"ZombieManagerP0C1SendZombieStatesPatch: replacement count={replacementCount} expected=1");
            }

            RoleLogger.Info("[Shared]",
                $"[P0-C-1/Zombie] OK Transpiler replacement=1/1，IL 修改已应用（L1662 IsDedicatedServer -> IsDedicatedOrP2PHost）");
            return codes;
        }
    }
}
