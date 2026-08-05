using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.33 P0-C-1 车辆周期性状态广播（Codex 第二十一次双机测试外部审计 §6.2 裁决事项 2 + §4.1 路线 A 授权实施）：
    ///
    /// 根因（U3-SDK VehicleManager.cs:2844-2930 Update + InteractableVehicle.cs:3908-3950 OnUpdate）：
    ///
    /// VehicleManager.Update L2844:
    ///   L2853 if (Dedicator.IsDedicatedServer)  <-- 外层 OnUpdate 路径门控（保留，不改）
    ///     L2855-2870 foreach vehicles: vehicle.OnUpdate(deltaTime)（dedicated 全量更新）
    ///   L2872 else  <-- listen host 走此分支（time-slice 更新，保留）
    ///     L2880-2908 foreach vehicles: time-slice + vehicle.OnUpdate(vehicleDeltaTime)
    ///   L2918 if (vehicles.Count > 0 && Dedicator.IsDedicatedServer && Time.realtimeSinceStartup - lastTick > Provider.UPDATE_TIME)  <-- 阻断点 1
    ///     L2926 sendVehicleStates()
    ///
    /// InteractableVehicle.OnUpdate L3908:
    ///   L3929 if (Provider.isServer && !needsReplicationUpdate && updates != null && updates.Count > 0)  <-- 兼容旧路径
    ///     L3932 updates.Clear()
    ///     L3934 MarkForReplicationUpdate()
    ///   L3937 if (Dedicator.IsDedicatedServer)  <-- 阻断点 2（位移检测 + MarkForReplicationUpdate）
    ///     L3939 if (isPhysical)
    ///       L3941 if (!needsReplicationUpdate)
    ///         L3943 if (|lastUpdatedPos - transform.position| > UPDATE_DISTANCE)
    ///           L3945 lastUpdatedPos = transform.position
    ///           L3946 MarkForReplicationUpdate()
    ///   L3951 else  <-- 客机端动画插值（listen host 房主需要此分支）
    ///     L3953-3963 AnimatedSteeringAngle / AnimatedForwardVelocity 等插值
    ///
    /// 问题（Codex §3.3 验证）：
    ///   1. L2918 被 IsDedicatedServer 门控，listen host 下 sendVehicleStates 不调用
    ///   2. L3929-3935 的 updates.Count > 0 条件永远为 false（updates 字段已废弃，无 Add 调用，
    ///      InteractableVehicle.cs:857-860 注释明确说明 obsolete）
    ///   3. L3937-3950 被 IsDedicatedServer 门控，listen host 下位移检测不触发
    ///   4. 综合结果：vehiclesNeedingReplicationUpdate 始终为空，sendVehicleStates 无车可发
    ///
    /// 第二十一次双机测试决定性证据：
    ///   - 主机日志：wouldSendVehicleStates=False（全程）
    ///   - 客机日志：0 次 ReceiveVehicleStates
    ///
    /// 修复方案（Codex §4.1 路线 A 授权实施）：
    ///
    /// 子任务 1：Transpiler 修改 VehicleManager.Update L2918（第 2 处 IsDedicatedServer 调用）
    ///   替换为 ListenRegionSyncEligibility.IsDedicatedOrP2PHost()
    ///   - L2853 第 1 处保留（time-slice 路径保留，性能更优）
    ///   - L2918 第 2 处替换（放开 sendVehicleStates 门控）
    ///   - replacement count 必须精确等于 1
    ///
    /// 子任务 2：Postfix InteractableVehicle.OnUpdate 补充位移检测 + MarkForReplicationUpdate
    ///   守门：Provider.isServer && !Dedicator.IsDedicatedServer && HostManager.ShouldProcessClientHostListen()
    ///   逻辑（与 vanilla L3937-3950 等价）：
    ///     if (isPhysical && !needsReplicationUpdate &&
    ///         |lastUpdatedPos - transform.position| > UPDATE_DISTANCE)
    ///       lastUpdatedPos = transform.position
    ///       MarkForReplicationUpdate()
    ///   反射访问 internal/private 字段：
    ///     - hasUnityCalledStart（internal, InteractableVehicle.cs:3895）
    ///     - needsReplicationUpdate（internal, InteractableVehicle.cs:638）
    ///     - isPhysical（private, InteractableVehicle.cs:730）
    ///     - lastUpdatedPos（private, InteractableVehicle.cs:711）
    ///   FieldInfo 缓存到静态字段，避免每次 AccessTools.Field 开销
    ///
    /// 为什么不用 Transpiler 替换 L3937？
    ///   - L3937 的 else 分支（L3951-3963）是客机端动画插值，listen host 房主需要此分支
    ///   - 若替换 L3937 为 IsDedicatedOrP2PHost，listen host 下会跳过 else 分支，
    ///     房主本地车辆动画插值失效（AnimatedSteeringAngle/AnimatedForwardVelocity 不更新）
    ///   - Postfix 模式不影响 vanilla 的 if/else 分支选择，只在 OnUpdate 后补充位移检测
    ///
    /// 栈平衡（Transpiler）：
    ///   原版：call get_IsDedicatedServer()（无参数，返回 bool i4）=> 栈净变化 +1
    ///   替换：call IsDedicatedOrP2PHost()（无参数，返回 bool i4）=> 栈净变化 +1
    ///   一致。
    ///
    /// 安全性：
    ///   - 不全局伪造 Dedicator.IsDedicatedServer
    ///   - 不替换 L2853（time-slice 路径保留）
    ///   - 不修改 InteractableVehicle.OnUpdate 的 if/else 分支（Postfix 不影响分支选择）
    ///   - 不修改 sendVehicleStates 实现
    ///   - 反射访问仅读取/写入 4 个字段，不调用 vanilla private 方法（MarkForReplicationUpdate 是 public）
    ///   - Postfix 守门条件确保只在 listen host 下执行（客机端不执行）
    ///
    /// 禁止项（Codex §6.2 裁决事项 5）：
    ///   - 不替换 VehicleManager.Update L2853
    ///   - 不夹带 P0-E / ItemManager.dropItem 修改
    ///   - 不替换 AnimalManager.tickAnimal L1019
    ///   - 不替换 AnimalManager.addAnimal L523
    /// </summary>
    public static class VehicleManagerP0C1ReplicationPatch
    {
        public static bool AllRegistrationsSucceeded { get; private set; }
        public static string RegistrationSummary { get; private set; } = "未登记";
        public static int TranspilerReplacementCount { get; private set; } = -1;
        public static bool UpdateSignatureResolved { get; private set; }
        public static string UpdateSignatureSummary { get; private set; } = "未自检";
        public static bool OnUpdatePostfixRegistered { get; private set; }
        public static bool TranspilerOwnerVerified { get; private set; }
        public static string TranspilerOwnerSummary { get; private set; } = "未自检";

        private const string HarmonyId = SteamP2PFriendsPlugin.HARMONY_ID;
        private const string TargetUpdateMethodName = "Update";
        private const string TargetOnUpdateMethodName = "OnUpdate";
        private const string PatchTranspilerName = nameof(Update_Transpiler);
        private const string PatchOnUpdatePostfixName = nameof(OnUpdate_Postfix);

        // 反射字段访问委托（InteractableVehicle internal/private 字段）
        // v0.2.3.34 Medium 优化：FieldInfo.GetValue/SetValue -> AccessTools.FieldRefAccess
        // 消除每次反射调用的 boxing/方法调用开销（Codex 第二十二次审计 §2 Medium 授权实施）
        private static readonly AccessTools.FieldRef<InteractableVehicle, bool> _hasUnityCalledStartRef =
            AccessTools.FieldRefAccess<InteractableVehicle, bool>("hasUnityCalledStart");
        private static readonly AccessTools.FieldRef<InteractableVehicle, bool> _needsReplicationUpdateRef =
            AccessTools.FieldRefAccess<InteractableVehicle, bool>("needsReplicationUpdate");
        private static readonly AccessTools.FieldRef<InteractableVehicle, bool> _isPhysicalRef =
            AccessTools.FieldRefAccess<InteractableVehicle, bool>("isPhysical");
        private static readonly AccessTools.FieldRef<InteractableVehicle, Vector3> _lastUpdatedPosRef =
            AccessTools.FieldRefAccess<InteractableVehicle, Vector3>("lastUpdatedPos");

        // 日志限流（避免每帧刷屏）
        private const int SupplementLogLimit = 30;
        private static int _supplementLogCount;

        public static bool RegisterManual(Harmony harmony)
        {
            RoleLogger.Info("[Shared]", "[P0-C-1/Vehicle] === 手动登记 Transpiler + OnUpdate Postfix（v0.2.3.33 P0-C-1 车辆周期性状态广播）===");

            if (harmony == null)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = "harmony=null";
                RoleLogger.Error("[Shared]", $"[P0-C-1/Vehicle] !!! {RegistrationSummary}");
                return false;
            }

            // v0.2.3.34 Medium 优化后，FieldRefAccess 在首次访问时若字段不存在会抛异常
            // （静态字段初始化时即抛 TypeLoadException 或返回 null 委托），
            // 此处改为运行时 null 检查（委托调用结果不可预判，但委托本身非 null）
            // 保留守门逻辑：若任一委托为 null，登记失败
            if (_hasUnityCalledStartRef == null || _needsReplicationUpdateRef == null
                || _isPhysicalRef == null || _lastUpdatedPosRef == null)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = "反射字段委托初始化失败";
                RoleLogger.Error("[Shared]", $"[P0-C-1/Vehicle] !!! {RegistrationSummary} " +
                    $"hasUnityCalledStart={_hasUnityCalledStartRef != null} " +
                    $"needsReplicationUpdate={_needsReplicationUpdateRef != null} " +
                    $"isPhysical={_isPhysicalRef != null} " +
                    $"lastUpdatedPos={_lastUpdatedPosRef != null}");
                return false;
            }

            bool sigOk = VerifyUpdateSignature();
            if (!sigOk)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = $"Update 签名自检失败 ({UpdateSignatureSummary})";
                RoleLogger.Error("[Shared]", $"[P0-C-1/Vehicle] !!! {RegistrationSummary}");
                return false;
            }

            bool transpilerOk = RegisterTranspiler(harmony);
            if (!transpilerOk)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = $"Transpiler 登记失败 (replacement={TranspilerReplacementCount})";
                RoleLogger.Error("[Shared]", $"[P0-C-1/Vehicle] !!! {RegistrationSummary}");
                return false;
            }

            bool postfixOk = RegisterOnUpdatePostfix(harmony);
            OnUpdatePostfixRegistered = postfixOk;
            if (!postfixOk)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = "OnUpdate Postfix 登记失败";
                RoleLogger.Error("[Shared]", $"[P0-C-1/Vehicle] !!! {RegistrationSummary}");
                return false;
            }

            AllRegistrationsSucceeded = true;
            RegistrationSummary = $"signature={UpdateSignatureResolved}, transpilerReplacement=1/1, " +
                $"onUpdatePostfix={OnUpdatePostfixRegistered}, transpilerOwner={TranspilerOwnerVerified}";
            RoleLogger.Info("[Shared]",
                $"[P0-C-1/Vehicle] OK 手动登记成功 summary={RegistrationSummary}");
            return true;
        }

        private static bool RegisterTranspiler(Harmony harmony)
        {
            try
            {
                MethodInfo original = AccessTools.Method(typeof(VehicleManager), TargetUpdateMethodName);
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", "[P0-C-1/Vehicle] !!! Update AccessTools.Method 返回 null");
                    return false;
                }

                MethodInfo transpiler = AccessTools.Method(typeof(VehicleManagerP0C1ReplicationPatch), PatchTranspilerName);
                if (transpiler == null)
                {
                    RoleLogger.Error("[Shared]", "[P0-C-1/Vehicle] !!! Transpiler 方法未找到");
                    return false;
                }

                harmony.Patch(original, transpiler: new HarmonyMethod(transpiler));

                if (TranspilerReplacementCount != 1)
                {
                    RoleLogger.Error("[Shared]",
                        $"[P0-C-1/Vehicle] !!! DIAGNOSTIC BUILD INVALID: replacement count={TranspilerReplacementCount} 期望=1");
                    return false;
                }

                bool ownerOk = VerifyPatchOwner(original);
                if (!ownerOk)
                {
                    RoleLogger.Error("[Shared]",
                        $"[P0-C-1/Vehicle] !!! DIAGNOSTIC BUILD INVALID: Transpiler owner 自检失败 summary={TranspilerOwnerSummary}");
                    return false;
                }

                RoleLogger.Info("[Shared]",
                    $"[P0-C-1/Vehicle] OK Transpiler 已登记 (replacement=1/1, owner={TranspilerOwnerVerified})");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P0-C-1/Vehicle] !!! RegisterTranspiler 异常: {ex}");
                return false;
            }
        }

        private static bool RegisterOnUpdatePostfix(Harmony harmony)
        {
            try
            {
                // InteractableVehicle.OnUpdate 是 internal 方法
                MethodInfo original = AccessTools.Method(typeof(InteractableVehicle), TargetOnUpdateMethodName,
                    new System.Type[] { typeof(float) });
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", "[P0-C-1/Vehicle] !!! OnUpdate(float) AccessTools.Method 返回 null");
                    return false;
                }

                MethodInfo postfix = AccessTools.Method(typeof(VehicleManagerP0C1ReplicationPatch), PatchOnUpdatePostfixName);
                if (postfix == null)
                {
                    RoleLogger.Error("[Shared]", "[P0-C-1/Vehicle] !!! OnUpdate Postfix 方法未找到");
                    return false;
                }

                harmony.Patch(original, postfix: new HarmonyMethod(postfix));

                RoleLogger.Info("[Shared]",
                    $"[P0-C-1/Vehicle] OK OnUpdate Postfix 已登记");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P0-C-1/Vehicle] !!! RegisterOnUpdatePostfix 异常: {ex}");
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
                            && patchMethod.DeclaringType == typeof(VehicleManagerP0C1ReplicationPatch)
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

        private static bool VerifyUpdateSignature()
        {
            try
            {
                MethodInfo method = AccessTools.Method(typeof(VehicleManager), TargetUpdateMethodName);
                if (method == null)
                {
                    UpdateSignatureResolved = false;
                    UpdateSignatureSummary = "AccessTools.Method 返回 null";
                    return false;
                }

                if (method.Name != TargetUpdateMethodName)
                {
                    UpdateSignatureResolved = false;
                    UpdateSignatureSummary = $"Name={method.Name} 期望={TargetUpdateMethodName}";
                    return false;
                }

                if (method.IsStatic)
                {
                    UpdateSignatureResolved = false;
                    UpdateSignatureSummary = "IsStatic=true 期望=false";
                    return false;
                }

                ParameterInfo[] ps = method.GetParameters();
                if (ps.Length != 0)
                {
                    UpdateSignatureResolved = false;
                    UpdateSignatureSummary = $"paramCount={ps.Length} 期望=0";
                    return false;
                }

                if (method.ReturnType != typeof(void))
                {
                    UpdateSignatureResolved = false;
                    UpdateSignatureSummary = $"ReturnType={method.ReturnType.Name} 期望=void";
                    return false;
                }

                UpdateSignatureResolved = true;
                UpdateSignatureSummary = "private instance void Update()";
                RoleLogger.Info("[Shared]",
                    $"[P0-C-1/Vehicle] OK 签名自检通过: {UpdateSignatureSummary}");
                return true;
            }
            catch (System.Exception ex)
            {
                UpdateSignatureResolved = false;
                UpdateSignatureSummary = $"异常: {ex.Message}";
                RoleLogger.Error("[Shared]", $"[P0-C-1/Vehicle] !!! 签名自检异常: {ex}");
                return false;
            }
        }

        /// <summary>
        /// v0.2.3.33 P0-C-1 车辆 Transpiler 主实现。
        /// 替换 vanilla VehicleManager.Update 中第 2 处 Dedicator.get_IsDedicatedServer() 调用（L2918）
        /// 为 ListenRegionSyncEligibility.IsDedicatedOrP2PHost()。
        ///
        /// 第 1 处（L2853）保留不动（time-slice 路径门控，listen host 走 else 分支性能更优）。
        ///
        /// 栈平衡：
        ///   原版：call get_IsDedicatedServer()（无参数，返回 bool i4）=> 栈净变化 +1
        ///   替换：call IsDedicatedOrP2PHost()（无参数，返回 bool i4）=> 栈净变化 +1
        ///   一致。
        ///
        /// replacement count 必须精确等于 1（仅替换第 2 处）。
        /// </summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(VehicleManager), TargetUpdateMethodName)]
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
                TranspilerReplacementCount = -1;
                throw new System.InvalidOperationException(
                    "VehicleManagerP0C1ReplicationPatch: Dedicator.get_IsDedicatedServer not found");
            }

            if (eligibilityMethod == null)
            {
                TranspilerReplacementCount = -1;
                throw new System.InvalidOperationException(
                    "VehicleManagerP0C1ReplicationPatch: IsDedicatedOrP2PHost not found");
            }

            // 第一遍：统计 IsDedicatedServer 调用总数，验证期望=2（L2853 + L2918）
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
                TranspilerReplacementCount = -1;
                throw new System.InvalidOperationException(
                    $"VehicleManagerP0C1ReplicationPatch: IsDedicatedServer 调用总数={totalDedicatedCalls} 期望=2 (L2853 + L2918)");
            }

            // 第二遍：替换第 2 处（L2918）
            int dedicatedCallIndex = 0;
            int replacementCount = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                CodeInstruction instr = codes[i];
                if (instr == null) continue;

                if (instr.Calls(dedicatedGetter))
                {
                    dedicatedCallIndex++;
                    if (dedicatedCallIndex == 2) // 第二处是 L2918
                    {
                        instr.opcode = OpCodes.Call;
                        instr.operand = eligibilityMethod;
                        replacementCount++;
                    }
                }
            }

            TranspilerReplacementCount = replacementCount;

            if (replacementCount != 1)
            {
                throw new System.InvalidOperationException(
                    $"VehicleManagerP0C1ReplicationPatch: replacement count={replacementCount} expected=1");
            }

            RoleLogger.Info("[Shared]",
                $"[P0-C-1/Vehicle] OK Transpiler replacement=1/1，IL 修改已应用（L2918 IsDedicatedServer -> IsDedicatedOrP2PHost，L2853 保留，totalDedicatedCalls={totalDedicatedCalls}）");
            return codes;
        }

        /// <summary>
        /// v0.2.3.33 P0-C-1 车辆 OnUpdate Postfix：listen host 下补充位移检测 + MarkForReplicationUpdate。
        ///
        /// 守门条件：
        ///   - Provider.isServer（listen host 侧）
        ///   - !Dedicator.IsDedicatedServer（dedicated server 走 vanilla L3937-3950，无需补充）
        ///   - HostManager.ShouldProcessClientHostListen()（P2P listen host 模式激活）
        ///
        /// 逻辑（与 vanilla L3937-3950 等价）：
        ///   if (isPhysical && !needsReplicationUpdate &&
        ///       |lastUpdatedPos - transform.position| > UPDATE_DISTANCE)
        ///     lastUpdatedPos = transform.position
        ///     MarkForReplicationUpdate()
        ///
        /// 反射访问 internal/private 字段（FieldInfo 缓存到静态字段）：
        ///   - hasUnityCalledStart（internal, InteractableVehicle.cs:3895）
        ///   - needsReplicationUpdate（internal, InteractableVehicle.cs:638）
        ///   - isPhysical（private, InteractableVehicle.cs:730）
        ///   - lastUpdatedPos（private, InteractableVehicle.cs:711）
        ///
        /// 为什么用 Postfix 而非 Transpiler 替换 L3937？
        ///   - L3937 的 else 分支（L3951-3963）是客机端动画插值，listen host 房主需要此分支
        ///   - 若替换 L3937 为 IsDedicatedOrP2PHost，listen host 下会跳过 else 分支，
        ///     房主本地车辆动画插值失效
        ///   - Postfix 模式不影响 vanilla 的 if/else 分支选择，只在 OnUpdate 后补充位移检测
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(InteractableVehicle), TargetOnUpdateMethodName)]
        public static void OnUpdate_Postfix(InteractableVehicle __instance, float deltaTime)
        {
            // 守门条件
            if (!Provider.isServer) return;
            if (Dedicator.IsDedicatedServer) return;
            if (!HostManager.ShouldProcessClientHostListen()) return;
            // v0.2.3.34 Low-1：VehicleManager.instance 空值守卫
            // teardown 阶段 VehicleManager.instance 可能已销毁但 OnUpdate 仍被调用一次，
            // 导致 NRE 被 catch-all 捕获隐藏诊断（Codex 第二十一次审计 §2 Low-1 授权实施）
            if (VehicleManager.instance == null) return;

            try
            {
                if (__instance == null) return;

                // hasUnityCalledStart 检查（与 vanilla L2857/L2882 一致）
                // v0.2.3.34 Medium：FieldRefAccess 替代 FieldInfo.GetValue
                bool hasUnityCalledStart = _hasUnityCalledStartRef(__instance);
                if (!hasUnityCalledStart) return;

                // needsReplicationUpdate 检查（与 vanilla L3941 一致）
                bool needsReplicationUpdate = _needsReplicationUpdateRef(__instance);
                if (needsReplicationUpdate) return;

                // isPhysical 检查（与 vanilla L3939 一致）
                bool isPhysical = _isPhysicalRef(__instance);
                if (!isPhysical) return;

                // 位移检测（与 vanilla L3943 一致）
                Vector3 lastUpdatedPos = _lastUpdatedPosRef(__instance);
                Vector3 curPos = __instance.transform.position;
                if (Mathf.Abs(lastUpdatedPos.x - curPos.x) > Provider.UPDATE_DISTANCE ||
                    Mathf.Abs(lastUpdatedPos.y - curPos.y) > Provider.UPDATE_DISTANCE ||
                    Mathf.Abs(lastUpdatedPos.z - curPos.z) > Provider.UPDATE_DISTANCE)
                {
                    // 更新 lastUpdatedPos（与 vanilla L3945 一致）
                    // v0.2.3.34 Medium：FieldRefAccess 替代 FieldInfo.SetValue
                    _lastUpdatedPosRef(__instance) = curPos;

                    // 调用 MarkForReplicationUpdate（public 方法，InteractableVehicle.cs:862）
                    __instance.MarkForReplicationUpdate();

                    // 有限日志记录
                    int count = System.Threading.Interlocked.Increment(ref _supplementLogCount);
                    if (count <= SupplementLogLimit)
                    {
                        RoleLogger.Info("[Host]",
                            $"[P0-C-1/Vehicle] supplement markForReplication #{count}/{SupplementLogLimit} " +
                            $"vehicle={__instance.asset?.FriendlyName ?? "unknown"} " +
                            $"deltaPos=({curPos.x - lastUpdatedPos.x:F2},{curPos.y - lastUpdatedPos.y:F2},{curPos.z - lastUpdatedPos.z:F2})");
                    }
                }
            }
            catch (System.Exception ex)
            {
                try { RoleLogger.Error("[Shared]", $"[P0-C-1/Vehicle] OnUpdate Postfix 异常: {ex.Message}"); } catch { }
            }
        }

        public static void OnClientDisconnected()
        {
            // 按车清理不必要，supplement 日志使用全局计数，不按玩家区分
        }

        public static void ResetAll()
        {
            int cleared = _supplementLogCount;
            _supplementLogCount = 0;
            RoleLogger.Info("[Shared]",
                $"[P0-C-1/Vehicle] ResetAll 清空 supplement 计数 (was={cleared})");
        }
    }
}
