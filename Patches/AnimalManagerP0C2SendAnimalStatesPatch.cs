using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.33 P0-C-2 动物周期性状态广播（Codex 第二十一次双机测试外部审计 §6.2 裁决事项 3 授权实施）：
    ///
    /// 根因（U3-SDK AnimalManager.cs:999-1067 Update）：
    ///   L1001 if (!Provider.isServer || !Level.isLoaded) return;
    ///   L1019 if (Dedicator.IsDedicatedServer)  <-- time-slice 门控（Codex 禁止项：不得替换）
    ///     L1021-1034 time-slice 逻辑（tickIndex 管理，每次 tick 25 只）
    ///   L1035 else  <-- listen host 走此分支（全量 tick，保留）
    ///     L1037-1038 start=0, end=tickingAnimals.Count
    ///   L1042-1054 tick 循环（animal.tick()）
    ///   L1057 if (Dedicator.IsDedicatedServer && Time.realtimeSinceStartup - lastTick > Provider.UPDATE_TIME)  <-- 阻断点
    ///     L1059 lastTick += Provider.UPDATE_TIME
    ///     L1065 sendAnimalStates()
    ///
    /// 问题：listen host 模式下 Dedicator.IsDedicatedServer=false，L1057 条件不满足，
    ///   sendAnimalStates 不调用，客机永远收不到动物周期性状态更新。
    ///
    /// 第二十一次双机测试决定性证据：
    ///   - 主机日志：wouldSendAnimalStates=False（全程）
    ///   - 客机日志：0 次 ReceiveAnimalStates
    ///
    /// 修复方案（Codex §6.2 裁决事项 3 授权实施）：
    ///   Transpiler 替换 AnimalManager.Update 中第 2 处 Dedicator.IsDedicatedServer 调用（L1057）
    ///   为 ListenRegionSyncEligibility.IsDedicatedOrP2PHost()。
    ///
    ///   第 1 处（L1019）保留不动（time-slice 门控，listen host 走 else 分支全量 tick）。
    ///
    ///   替换后语义：
    ///     - dedicated server: IsDedicatedOrP2PHost=true（vanilla 行为不变）
    ///     - listen host: IsDedicatedOrP2PHost=true（新增，L1057 条件满足时调用 sendAnimalStates）
    ///     - 普通单机/客机/菜单: IsDedicatedOrP2PHost=false（L1057 条件不满足，跳过）
    ///
    ///   sendAnimalStates 内部遍历 Provider.clients，对每个 client 发送 SendAnimalStates.Invoke，
    ///   目标由 Provider.clients 决定（已排除 loopback 玩家），listen host 下只会发往远端客机。
    ///
    /// 动物 isUpdated 标记链路（Codex §3.4 验证）：
    ///   - AnimalManager.cs:1035-1039 else 分支：listen host 下 start=0, end=tickingAnimals.Count（全量 tick）
    ///   - AnimalManager.cs:1052 animal.tick() 调用
    ///   - Animal.cs:959-961 animal.tick() 内部设置 isUpdated=true
    ///   - AnimalManager.cs:925-944 sendAnimalStates 遍历 animals, 检查 isUpdated, 发送
    ///   - 链路完整：listen host 下 animal.tick() 设置 isUpdated -> sendAnimalStates 发送
    ///
    /// 栈平衡：
    ///   原版：call get_IsDedicatedServer()（无参数，返回 bool i4）=> 栈净变化 +1
    ///   替换：call IsDedicatedOrP2PHost()（无参数，返回 bool i4）=> 栈净变化 +1
    ///   一致。
    ///
    /// 安全性：
    ///   - 不全局伪造 Dedicator.IsDedicatedServer
    ///   - 不替换 L1019（time-slice 门控保留，listen host 走 else 分支全量 tick）
    ///   - 不修改 sendAnimalStates 实现
    ///   - 不干预 animal.tick() 的 isUpdated 标记逻辑
    ///   - 不替换 AnimalManager.tickAnimal L1019（Codex 禁止项）
    ///   - 不替换 AnimalManager.addAnimal L523（Codex 禁止项）
    ///
    /// 禁止项（Codex §6.2 裁决事项 5）：
    ///   - 不夹带 P0-E / ItemManager.dropItem 修改
    ///   - 不替换 VehicleManager.Update L2853
    ///   - 不替换 AnimalManager.tickAnimal L1019
    ///   - 不替换 AnimalManager.addAnimal L523
    /// </summary>
    public static class AnimalManagerP0C2SendAnimalStatesPatch
    {
        public static bool AllRegistrationsSucceeded { get; private set; }
        public static string RegistrationSummary { get; private set; } = "未登记";
        public static int ReplacementCount { get; private set; } = -1;
        public static bool SignatureResolved { get; private set; }
        public static string SignatureSummary { get; private set; } = "未自检";

        public static bool TranspilerOwnerVerified { get; private set; }
        public static string TranspilerOwnerSummary { get; private set; } = "未自检";

        private const string HarmonyId = SteamP2PFriendsPlugin.HARMONY_ID;
        private const string TargetMethodName = "Update";
        private const string PatchTranspilerName = nameof(Update_Transpiler);

        public static bool RegisterManual(Harmony harmony)
        {
            RoleLogger.Info("[Shared]", "[P0-C-2/Animal] === 手动登记 Transpiler（v0.2.3.33 P0-C-2 动物周期性状态广播）===");

            if (harmony == null)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = "harmony=null";
                RoleLogger.Error("[Shared]", $"[P0-C-2/Animal] !!! {RegistrationSummary}");
                return false;
            }

            bool sigOk = VerifyTargetSignature();
            if (!sigOk)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = $"Update 签名自检失败 ({SignatureSummary})";
                RoleLogger.Error("[Shared]", $"[P0-C-2/Animal] !!! {RegistrationSummary}");
                return false;
            }

            bool transpilerOk = RegisterTranspiler(harmony);
            if (!transpilerOk)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = $"Transpiler 登记失败 (replacement={ReplacementCount})";
                RoleLogger.Error("[Shared]", $"[P0-C-2/Animal] !!! {RegistrationSummary}");
                return false;
            }

            AllRegistrationsSucceeded = true;
            RegistrationSummary = $"signature={SignatureResolved}, replacement=1/1, transpilerOwner={TranspilerOwnerVerified}";
            RoleLogger.Info("[Shared]",
                $"[P0-C-2/Animal] OK 手动登记成功 summary={RegistrationSummary}");
            return true;
        }

        private static bool RegisterTranspiler(Harmony harmony)
        {
            try
            {
                MethodInfo original = AccessTools.Method(typeof(AnimalManager), TargetMethodName);
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", "[P0-C-2/Animal] !!! Update AccessTools.Method 返回 null");
                    return false;
                }

                MethodInfo transpiler = AccessTools.Method(typeof(AnimalManagerP0C2SendAnimalStatesPatch), PatchTranspilerName);
                if (transpiler == null)
                {
                    RoleLogger.Error("[Shared]", "[P0-C-2/Animal] !!! Transpiler 方法未找到");
                    return false;
                }

                harmony.Patch(original, transpiler: new HarmonyMethod(transpiler));

                if (ReplacementCount != 1)
                {
                    RoleLogger.Error("[Shared]",
                        $"[P0-C-2/Animal] !!! DIAGNOSTIC BUILD INVALID: replacement count={ReplacementCount} 期望=1");
                    return false;
                }

                bool ownerOk = VerifyPatchOwner(original);
                if (!ownerOk)
                {
                    RoleLogger.Error("[Shared]",
                        $"[P0-C-2/Animal] !!! DIAGNOSTIC BUILD INVALID: Transpiler owner 自检失败 summary={TranspilerOwnerSummary}");
                    return false;
                }

                RoleLogger.Info("[Shared]",
                    $"[P0-C-2/Animal] OK Transpiler 已登记 (replacement=1/1, owner={TranspilerOwnerVerified})");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P0-C-2/Animal] !!! RegisterTranspiler 异常: {ex}");
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
                            && patchMethod.DeclaringType == typeof(AnimalManagerP0C2SendAnimalStatesPatch)
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
                MethodInfo method = AccessTools.Method(typeof(AnimalManager), TargetMethodName);
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
                SignatureSummary = "private instance void Update()";
                RoleLogger.Info("[Shared]",
                    $"[P0-C-2/Animal] OK 签名自检通过: {SignatureSummary}");
                return true;
            }
            catch (System.Exception ex)
            {
                SignatureResolved = false;
                SignatureSummary = $"异常: {ex.Message}";
                RoleLogger.Error("[Shared]", $"[P0-C-2/Animal] !!! 签名自检异常: {ex}");
                return false;
            }
        }

        /// <summary>
        /// v0.2.3.33 P0-C-2 动物 Transpiler 主实现。
        /// 替换 vanilla AnimalManager.Update 中第 2 处 Dedicator.get_IsDedicatedServer() 调用（L1057）
        /// 为 ListenRegionSyncEligibility.IsDedicatedOrP2PHost()。
        ///
        /// 第 1 处（L1019）保留不动（time-slice 门控，listen host 走 else 分支全量 tick）。
        ///
        /// 栈平衡：
        ///   原版：call get_IsDedicatedServer()（无参数，返回 bool i4）=> 栈净变化 +1
        ///   替换：call IsDedicatedOrP2PHost()（无参数，返回 bool i4）=> 栈净变化 +1
        ///   一致。
        ///
        /// replacement count 必须精确等于 1（仅替换第 2 处）。
        /// </summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(AnimalManager), TargetMethodName)]
        public static IEnumerable<CodeInstruction> Update_Transpiler(
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
                    "AnimalManagerP0C2SendAnimalStatesPatch: Dedicator.get_IsDedicatedServer not found");
            }

            if (eligibilityMethod == null)
            {
                ReplacementCount = -1;
                throw new System.InvalidOperationException(
                    "AnimalManagerP0C2SendAnimalStatesPatch: IsDedicatedOrP2PHost not found");
            }

            // 第一遍：统计 IsDedicatedServer 调用总数，验证期望=2（L1019 + L1057）
            int totalDedicatedCalls = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                CodeInstruction instr = codes[i];
                if (instr != null && instr.Calls(dedicatedGetter))
                {
                    totalDedicatedCalls++;
                }
            }

            if (totalDedicatedCalls != 2)
            {
                ReplacementCount = -1;
                throw new System.InvalidOperationException(
                    $"AnimalManagerP0C2SendAnimalStatesPatch: IsDedicatedServer 调用总数={totalDedicatedCalls} 期望=2 (L1019 + L1057)");
            }

            // 第二遍：替换第 2 处（L1057）
            int dedicatedCallIndex = 0;
            int replacementCount = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                CodeInstruction instr = codes[i];
                if (instr == null) continue;

                if (instr.Calls(dedicatedGetter))
                {
                    dedicatedCallIndex++;
                    if (dedicatedCallIndex == 2) // 第二处是 L1057
                    {
                        instr.opcode = OpCodes.Call;
                        instr.operand = eligibilityMethod;
                        replacementCount++;
                    }
                }
            }

            ReplacementCount = replacementCount;

            if (replacementCount != 1)
            {
                throw new System.InvalidOperationException(
                    $"AnimalManagerP0C2SendAnimalStatesPatch: replacement count={replacementCount} expected=1");
            }

            RoleLogger.Info("[Shared]",
                $"[P0-C-2/Animal] OK Transpiler replacement=1/1，IL 修改已应用（L1057 IsDedicatedServer -> IsDedicatedOrP2PHost，L1019 保留）");
            return codes;
        }
    }
}
