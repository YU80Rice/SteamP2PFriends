using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;

namespace SteamP2PFriends.Patches.P0EBarricadeLifecycle
{
    /// <summary>
    /// v0.2.3.39 5B-1B v2.5.1（Codex 第六十二次审计返修）：
    /// Barricade equip/checkClaims Transpiler 的原子登记中心。
    ///
    /// 设计依据：
    ///   .audit/v0.2.3.39-stage5B-1B-v2.5-smoke-20260728/Codex第六十二次Barricade-v2.5冒烟失败审计与指导报告-20260728.md §4.2
    ///   .audit/v0.2.3.39-stage5B-1B-v2.5-design-20260727/barricade-fix-design-v2.5-20260727.md §2 + §7
    ///
    /// 职责：
    ///   - 启动时一次性缓存 6 个 MethodInfo（2 原方法 + 2 Transpiler + 2 Helper）
    ///   - Precheck 真正读取 IL（PatchProcessor.GetCurrentInstructions maxTranspilers=0）
    ///   - Precheck 验证 false-branch 语义（Brfalse 或 Brfalse_S 任一，不按方法名区分）
    ///   - 原子登记两个 Transpiler（任一失败则全部回滚）
    ///   - VerifyAll owner + priority + ReplacementApplied 自检
    ///   - 启动时缓存 DiagnosticBuildValid，运行时只读
    ///
    /// Codex 62nd §2 真实根因纠正：
    ///   - 原始 Assembly-CSharp IL 编码：checkClaims=brfalse.s，equip=brfalse（编码形式不同）
    ///   - Harmony ILManipulator.NormalizeInstructions 通过 ShortToLongMap 将 Brfalse_S 规范化为 Brfalse
    ///   - PatchProcessor.GetCurrentInstructions 返回的 CodeInstruction 是规范化后的形式（maxTranspilers=0 仅表示未应用 Transpiler，不恢复原始短分支编码）
    ///   - 因此 Precheck 不得按方法名强制 Brfalse 或 Brfalse_S，应按语义验证 false-branch
    ///
    /// Codex 62nd §4.2 v2.5.1 修复：
    ///   - 删除 equip -> expected Brfalse、checkClaims -> expected Brfalse_S 的方法名硬约束
    ///   - 两者统一调用 ValidateFalseBranchOpcode，接受 Brfalse 或 Brfalse_S 任一
    ///   - 日志输出 actualBranch（Harmony 规范化后的实际 opcode），不再声明方法名对应的期望编码
    ///
    /// Codex 60th P0-3：真正的 Precheck 必须读取 IL
    ///   - 使用 PatchProcessor.GetCurrentInstructions(original, out _, maxTranspilers: 0) 读取未应用 Transpiler 的 Harmony 规范化 CodeInstruction 快照
    ///   - 调用 matcher 验证精确单匹配
    ///   - 验证 false-branch 语义（Brfalse 或 Brfalse_S 任一）
    ///
    /// Codex 60th P0-5：RollbackBoth 必须验证回滚结果
    ///   - 缓存 _harmony = harmony
    ///   - 对两个目标执行精确 Unpatch（遍历所有匹配，不 break）
    ///   - 回滚后通过 Harmony.GetPatchInfo 验证 exact count 均为 0
    ///   - _rollbackClean 只能来自上述验证结果
    ///   - 回滚后 Volatile.Write(...ReplacementApplied, 0)
    ///
    /// Codex 60th P0-6：重复登记不再次 Patch
    ///   - 发现本插件目标 Transpiler 已存在时 fail-closed 返回 false
    ///   - 不再次调用 harmony.Patch
    ///
    /// Codex 60th P0-7：ReplacementApplied 在新登记开始和回滚时清零
    ///   - RegisterAtomically 入口清零两个 ReplacementApplied 字段
    ///   - RollbackBoth 末尾清零两个 ReplacementApplied 字段
    ///
    /// Codex 60th P1-2：Precheck foreign 日志时序
    ///   - Precheck 阶段 foreign 日志写 action=abort_before_patch（不是 will_rollback）
    ///   - VerifyAll 阶段 foreign 日志写 action=will_rollback_this_plugin_patches
    ///
    /// Codex 60th P1-3：Reset 回调状态纳入构建有效性
    ///   - _resetCallbackRegistered 状态字段
    ///   - DiagnosticBuildValid 综合考虑 resetCallbackRegistered
    ///
    /// C1 硬约束（Codex 59th §2.1）：
    ///   - Helper MethodInfo 必须按真实签名解析：new[] { typeof(UseableBarricade) }
    ///   - equip/checkClaims 原方法使用 Type.EmptyTypes
    ///
    /// C2 硬约束（Codex 59th §2.2）：
    ///   - Mark 方法统一命名：MarkEquipReplacementApplied / MarkCheckClaimsReplacementApplied
    ///
    /// C3 硬约束（Codex 59th §2.3）：
    ///   - RegisterAtomically 入口必须清除 4 个状态字段 + 2 个 ReplacementApplied 字段（Codex 60th P0-7 扩展）
    ///
    /// C4 硬约束（Codex 59th §2.4）：
    ///   - foreign 日志不得提前声称"已执行回滚"
    ///   - 由 OwnerVerify 输出 action=will_rollback_this_plugin_patches
    ///   - 真正的 rollbackClean 结果由本类 RollbackBoth 日志输出
    /// </summary>
    public static class BarricadeLifecycleRegistration
    {
        // ===== v2.5 P0-1：private 缓存字段（保持 private，不改为 public） =====
        private static MethodInfo _equipMethod;
        private static MethodInfo _checkClaimsMethod;
        private static MethodInfo _equipTranspiler;
        private static MethodInfo _checkClaimsTranspiler;
        private static MethodInfo _equipHelperMethod;
        private static MethodInfo _checkClaimsHelperMethod;

        // ===== v2.5 P1-2：replacement 状态字段（改名 ReplacementApplied） =====
        private static int _equipReplacementApplied;
        private static int _checkClaimsReplacementApplied;

        // ===== v2.5 P1-3：DiagnosticBuildValid 缓存 =====
        private static bool _diagnosticBuildValid;

        // ===== v2.5 P1-5：状态字段（沿用 v2.4） =====
        private static bool _rollbackAttempted;
        private static bool _rollbackClean;
        private static bool _registrationSucceeded;

        // ===== Codex 60th P0-5：缓存 Harmony 实例（避免临时创建） =====
        private static Harmony _harmonyInstance;

        // ===== Codex 60th P1-3：Reset 回调登记状态 =====
        private static bool _resetCallbackRegistered;

        // ===== v2.5 C1：Helper 参数类型数组（Helper 真实签名是单参数 UseableBarricade） =====
        private static readonly System.Type[] HelperParamTypes = { typeof(UseableBarricade) };

        // ===== v2.5 P0-1：internal 只读访问器（Transpiler 跨类访问） =====
        internal static MethodInfo EquipMethod => _equipMethod;
        internal static MethodInfo CheckClaimsMethod => _checkClaimsMethod;
        internal static MethodInfo EquipHelperMethod => _equipHelperMethod;
        internal static MethodInfo CheckClaimsHelperMethod => _checkClaimsHelperMethod;

        // ===== v2.5 P0-1 + C2：internal Mark 方法（replacement 状态写入） =====
        internal static void MarkEquipReplacementApplied()
            => Volatile.Write(ref _equipReplacementApplied, 1);

        internal static void MarkCheckClaimsReplacementApplied()
            => Volatile.Write(ref _checkClaimsReplacementApplied, 1);

        // ===== v2.5 P1-3 + P1-5：公开只读属性 =====
        public static bool DiagnosticBuildValid => _diagnosticBuildValid;
        public static bool IsRegistrationSucceeded => _registrationSucceeded;
        public static bool WasRollbackAttempted => _rollbackAttempted;
        public static bool IsRollbackClean => _rollbackClean;

        /// <summary>
        /// v2.5 P1-2：ReplacementApplied 只读属性（用于 VerifyAll 与启动日志）
        /// </summary>
        public static bool EquipReplacementApplied => Volatile.Read(ref _equipReplacementApplied) == 1;
        public static bool CheckClaimsReplacementApplied => Volatile.Read(ref _checkClaimsReplacementApplied) == 1;

        /// <summary>
        /// Codex 60th P1-3：Reset 回调登记状态（纳入 DiagnosticBuildValid）
        /// </summary>
        public static bool ResetCallbackRegistered => _resetCallbackRegistered;

        /// <summary>
        /// Codex 60th P1-3：由 Plugin 调用，标记 Reset 回调已成功登记。
        /// </summary>
        public static void MarkResetCallbackRegistered()
        {
            _resetCallbackRegistered = true;
            RoleLogger.Info("[Shared]",
                "[5B-1B/MarkResetCallbackRegistered] resetCallbackRegistered=true");
        }

        /// <summary>
        /// v0.2.3.39 5B-1B v2.5：原子登记两个 Transpiler。
        /// 任一阶段失败则全部回滚并返回 false。
        /// </summary>
        public static bool RegisterAtomically(Harmony harmony)
        {
            // v2.5 C3 + Codex 60th P0-7：入口清除 4 个状态字段 + 2 个 ReplacementApplied 字段
            _diagnosticBuildValid = false;
            _registrationSucceeded = false;
            _rollbackAttempted = false;
            _rollbackClean = false;
            Volatile.Write(ref _equipReplacementApplied, 0);
            Volatile.Write(ref _checkClaimsReplacementApplied, 0);

            // Codex 60th P0-5：缓存 Harmony 实例
            _harmonyInstance = harmony;

            RoleLogger.Info("[Shared]", "[5B-1B/Register] === 原子登记开始（Codex 60th 返修） ===");

            if (harmony == null)
            {
                RoleLogger.Error("[Shared]", "[5B-1B/Register] !!! harmony=null");
                CacheDiagnosticBuildValid(false);
                return false;
            }

            // 阶段 1：缓存 6 个 MethodInfo
            if (!CacheAllMethodInfos())
            {
                RoleLogger.Error("[Shared]", "[5B-1B/Register] !!! CacheAllMethodInfos 失败");
                // 无登记可回滚，直接缓存失败结果
                CacheDiagnosticBuildValid(false);
                return false;
            }

            // 阶段 2：真正的 Precheck（Codex 60th P0-3：读取未应用 Transpiler 的 Harmony 规范化 CodeInstruction 快照 + 验证 false-branch 语义）
            if (!PrecheckBothPatternsWithIL())
            {
                RoleLogger.Error("[Shared]", "[5B-1B/Register] !!! PrecheckBothPatternsWithIL 失败");
                CacheDiagnosticBuildValid(false);
                return false;
            }

            // 阶段 3：登记两个 Transpiler（任一失败则回滚已登记的）
            // Codex 60th P0-6：发现本插件目标 Transpiler 已存在时 fail-closed，不再次 Patch
            // 已在 PrecheckOnePattern 中检测并 fail-closed

            try
            {
                harmony.Patch(_equipMethod, transpiler: new HarmonyMethod(_equipTranspiler, Priority.Normal));
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]",
                    $"[5B-1B/Register] !!! equip Transpiler 登记异常: {ex}");
                RollbackBoth("equip Transpiler 登记异常");
                CacheDiagnosticBuildValid(false);
                return false;
            }

            try
            {
                harmony.Patch(_checkClaimsMethod, transpiler: new HarmonyMethod(_checkClaimsTranspiler, Priority.Normal));
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]",
                    $"[5B-1B/Register] !!! checkClaims Transpiler 登记异常: {ex}");
                RollbackBoth("checkClaims Transpiler 登记异常");
                CacheDiagnosticBuildValid(false);
                return false;
            }

            // 阶段 4：VerifyAll（owner + priority + ReplacementApplied）
            bool verifyResult = VerifyAll();
            if (!verifyResult)
            {
                RoleLogger.Error("[Shared]", "[5B-1B/Register] !!! VerifyAll 失败，执行回滚");
                RollbackBoth("VerifyAll 失败");
                CacheDiagnosticBuildValid(false);
                return false;
            }

            // v2.5 P1-5：登记成功状态
            _registrationSucceeded = true;

            // v2.5 P1-3：缓存成功结果
            CacheDiagnosticBuildValid(verifyResult);

            RoleLogger.Info("[Shared]",
                "[5B-1B/Register] OK 双方法 Transpiler 全部精确登记 + 自检通过 " +
                $"(equipReplacementApplied={EquipReplacementApplied} " +
                $"checkClaimsReplacementApplied={CheckClaimsReplacementApplied})");
            return true;
        }

        /// <summary>
        /// v2.5 P0-2 + C1：缓存 6 个 MethodInfo。
        /// - equip/checkClaims 原方法使用 Type.EmptyTypes
        /// - Helper 使用 new[] { typeof(UseableBarricade) }（C1 硬约束）
        /// </summary>
        private static bool CacheAllMethodInfos()
        {
            // v2.5 P0-2：原方法使用 Type.EmptyTypes
            _equipMethod = AccessTools.Method(typeof(UseableBarricade), "equip", System.Type.EmptyTypes);
            _checkClaimsMethod = AccessTools.Method(typeof(UseableBarricade), "checkClaims", System.Type.EmptyTypes);

            // v2.5 P0-2：Transpiler MethodInfo 通过 AccessTools.Method 解析（public static，无需 BindingFlags）
            // 与 BarricadeManagerRegionSyncPatch.cs:133 同一模式
            _equipTranspiler = AccessTools.Method(
                typeof(BarricadeLifecycleTranspiler),
                nameof(BarricadeLifecycleTranspiler.Equip_Transpiler));
            _checkClaimsTranspiler = AccessTools.Method(
                typeof(BarricadeLifecycleTranspiler),
                nameof(BarricadeLifecycleTranspiler.CheckClaims_Transpiler));

            // v2.5 C1：Helper MethodInfo 使用 new[] { typeof(UseableBarricade) }
            _equipHelperMethod = AccessTools.Method(
                typeof(BarricadeLifecycleHelper),
                nameof(BarricadeLifecycleHelper.IsListenHostRemoteEquipInstance),
                HelperParamTypes);
            _checkClaimsHelperMethod = AccessTools.Method(
                typeof(BarricadeLifecycleHelper),
                nameof(BarricadeLifecycleHelper.IsListenHostRemoteCheckClaimsInstance),
                HelperParamTypes);

            // 判空 fail-closed
            if (_equipMethod == null || _checkClaimsMethod == null
                || _equipTranspiler == null || _checkClaimsTranspiler == null
                || _equipHelperMethod == null || _checkClaimsHelperMethod == null)
            {
                RoleLogger.Error("[Shared]",
                    $"[5B-1B/CacheAllMethodInfos] FAIL equip={_equipMethod != null} " +
                    $"checkClaims={_checkClaimsMethod != null} " +
                    $"equipTranspiler={_equipTranspiler != null} " +
                    $"checkClaimsTranspiler={_checkClaimsTranspiler != null} " +
                    $"equipHelper={_equipHelperMethod != null} " +
                    $"checkClaimsHelper={_checkClaimsHelperMethod != null}");
                return false;
            }

            RoleLogger.Info("[Shared]",
                "[5B-1B/CacheAllMethodInfos] OK 全部 6 个 MethodInfo 缓存成功 " +
                $"(helperParamCount={HelperParamTypes.Length})");
            return true;
        }

        /// <summary>
        /// Codex 60th P0-3：真正的 Precheck，读取未应用 Transpiler 的 Harmony 规范化 CodeInstruction 快照并验证 false-branch 语义。
        /// 使用 PatchProcessor.GetCurrentInstructions(original, out _, maxTranspilers: 0) 读取未应用 Transpiler 的 Harmony 规范化 CodeInstruction 快照。
        /// </summary>
        private static bool PrecheckBothPatternsWithIL()
        {
            try
            {
                // 读取未应用 Transpiler 的 Harmony 规范化 CodeInstruction 快照（maxTranspilers=0 不应用任何已登记 Transpiler）
                List<CodeInstruction> equipInstructions;
                List<CodeInstruction> checkClaimsInstructions;

                try
                {
                    equipInstructions = PatchProcessor.GetCurrentInstructions(_equipMethod, out _, maxTranspilers: 0);
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]",
                        $"[5B-1B/Precheck/equip] GetCurrentInstructions 异常: {ex.Message}");
                    return false;
                }

                try
                {
                    checkClaimsInstructions = PatchProcessor.GetCurrentInstructions(_checkClaimsMethod, out _, maxTranspilers: 0);
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]",
                        $"[5B-1B/Precheck/checkClaims] GetCurrentInstructions 异常: {ex.Message}");
                    return false;
                }

                if (equipInstructions == null || checkClaimsInstructions == null)
                {
                    RoleLogger.Error("[Shared]",
                        "[5B-1B/Precheck] GetCurrentInstructions 返回 null");
                    return false;
                }

                // Codex 62nd §4.2 v2.5.1：验证 equip 精确单匹配 + false-branch 语义（Brfalse 或 Brfalse_S 任一）
                if (!PrecheckOnePatternWithIL(equipInstructions, _equipMethod, "equip"))
                {
                    return false;
                }

                // Codex 62nd §4.2 v2.5.1：验证 checkClaims 精确单匹配 + false-branch 语义（Brfalse 或 Brfalse_S 任一）
                if (!PrecheckOnePatternWithIL(checkClaimsInstructions, _checkClaimsMethod, "checkClaims"))
                {
                    return false;
                }

                // Codex 60th P0-6：检测本插件目标 Transpiler 是否已存在（fail-closed，不再次 Patch）
                if (!CheckNoExistingOwnTranspiler(_equipMethod, "equip"))
                {
                    return false;
                }
                if (!CheckNoExistingOwnTranspiler(_checkClaimsMethod, "checkClaims"))
                {
                    return false;
                }

                // Codex 60th P1-2：检测 foreign Transpiler（此时未登记任何本插件 Patch，使用 abort_before_patch）
                if (!CheckNoForeignTranspiler(_equipMethod, "equip"))
                {
                    return false;
                }
                if (!CheckNoForeignTranspiler(_checkClaimsMethod, "checkClaims"))
                {
                    return false;
                }

                RoleLogger.Info("[Shared]",
                    "[5B-1B/PrecheckBothPatternsWithIL] OK 两方法 IL 精确单匹配 + false-branch 语义验证通过");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]",
                    $"[5B-1B/PrecheckBothPatternsWithIL] 异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Codex 62nd §4.2 v2.5.1：单个方法的 IL Precheck。
        /// 验证 matcher 精确单匹配 + 紧邻分支为 false-branch 语义（Brfalse 或 Brfalse_S 任一）。
        /// 不再按方法名强制期望编码，输出 actualBranch 供日志记录。
        /// </summary>
        private static bool PrecheckOnePatternWithIL(
            IList<CodeInstruction> instructions, MethodInfo original, string label)
        {
            try
            {
                if (instructions == null || original == null)
                {
                    RoleLogger.Error("[Shared]",
                        $"[5B-1B/Precheck/{label}] FAIL instructions or original is null");
                    return false;
                }

                var matches = BarricadeLifecycleILMatcher.MatchDedicatorBranch(instructions);
                if (!BarricadeLifecycleILMatcher.ValidateSingleMatch(matches, original, out int callIdx))
                {
                    return false;
                }

                if (!BarricadeLifecycleILMatcher.ValidateFalseBranchOpcode(
                        instructions, callIdx, label, out OpCode actualBranch))
                {
                    return false;
                }

                RoleLogger.Info("[Shared]",
                    $"[5B-1B/Precheck/{label}] OK callIdx={callIdx} actualBranch={actualBranch} " +
                    $"semantic=false-branch matchCount=1 instructionCount={instructions.Count}");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]",
                    $"[5B-1B/Precheck/{label}] 异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Codex 60th P0-6：检测本插件目标 Transpiler 是否已存在。
        /// 发现本插件目标 Transpiler 已存在时 fail-closed 返回 false，不再次 Patch。
        /// </summary>
        private static bool CheckNoExistingOwnTranspiler(MethodInfo original, string label)
        {
            try
            {
                if (original == null) return false;

                HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
                if (info?.Transpilers == null || info.Transpilers.Count == 0)
                {
                    return true;
                }

                MethodInfo expectedTranspiler = label == "equip" ? _equipTranspiler : _checkClaimsTranspiler;
                if (expectedTranspiler == null)
                {
                    RoleLogger.Error("[Shared]",
                        $"[5B-1B/Precheck/{label}] expectedTranspiler 未缓存");
                    return false;
                }

                foreach (Patch p in info.Transpilers)
                {
                    if (p.owner == SteamP2PFriendsPlugin.HARMONY_ID
                        && BarricadeLifecycleILMatcher.MethodIdentityEqual(p.PatchMethod, expectedTranspiler))
                    {
                        RoleLogger.Error("[Shared]",
                            $"[5B-1B/Precheck/{label}] DUPLICATE_OWN Transpiler 已存在，fail-closed " +
                            $"method={original.Name} action=abort_before_patch " +
                            $"reason=duplicate_registration_not_idempotent");
                        return false;
                    }
                }

                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]",
                    $"[5B-1B/Precheck/{label}] CheckNoExistingOwnTranspiler 异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Codex 60th P1-2：检测 foreign Transpiler。
        /// Precheck 阶段未登记本插件 Patch，foreign 日志使用 action=abort_before_patch（不是 will_rollback）。
        /// </summary>
        private static bool CheckNoForeignTranspiler(MethodInfo original, string label)
        {
            try
            {
                if (original == null) return false;

                HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
                if (info?.Transpilers == null || info.Transpilers.Count == 0)
                {
                    return true;
                }

                foreach (Patch p in info.Transpilers)
                {
                    if (p.owner != SteamP2PFriendsPlugin.HARMONY_ID)
                    {
                        RoleLogger.Error("[Shared]",
                            $"[5B-1B/Precheck/{label}] COMPATIBILITY_GUARD foreign Transpiler detected " +
                            $"foreignOwner={p.owner} " +
                            $"foreignMethod={p.PatchMethod?.DeclaringType?.FullName}.{p.PatchMethod?.Name} " +
                            $"method={original.Name} " +
                            $"action=abort_before_patch " +
                            $"reason=compatibility_protection_not_gameplay_logic " +
                            $"message=另一个模组在 {original.Name} 上登记了 Transpiler，" +
                            $"本插件为避免 IL 冲突将中止登记（此阶段未登记任何本插件 Patch）");
                        return false;
                    }
                }

                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]",
                    $"[5B-1B/Precheck/{label}] CheckNoForeignTranspiler 异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// v2.5 P1-3：VerifyAll（owner + priority + ReplacementApplied）。
        /// 仅在启动登记阶段调用一次。
        /// </summary>
        private static bool VerifyAll()
        {
            try
            {
                // 1. owner + priority + exact count 验证
                bool equipOwnerOk = BarricadeLifecycleOwnerVerify.VerifyOwnerAndPriority(
                    _equipMethod, _equipTranspiler, out string equipSummary);
                RoleLogger.Info("[Shared]",
                    $"[5B-1B/VerifyAll/equip] owner verdict={equipOwnerOk} summary=\"{equipSummary}\"");

                bool checkClaimsOwnerOk = BarricadeLifecycleOwnerVerify.VerifyOwnerAndPriority(
                    _checkClaimsMethod, _checkClaimsTranspiler, out string checkClaimsSummary);
                RoleLogger.Info("[Shared]",
                    $"[5B-1B/VerifyAll/checkClaims] owner verdict={checkClaimsOwnerOk} summary=\"{checkClaimsSummary}\"");

                // 2. ReplacementApplied 验证（v2.5 P1-2）
                bool equipApplied = EquipReplacementApplied;
                bool checkClaimsApplied = CheckClaimsReplacementApplied;

                if (!equipApplied)
                {
                    RoleLogger.Error("[Shared]",
                        $"[5B-1B/VerifyAll/equip] ReplacementApplied=false，期望 true");
                }
                if (!checkClaimsApplied)
                {
                    RoleLogger.Error("[Shared]",
                        $"[5B-1B/VerifyAll/checkClaims] ReplacementApplied=false，期望 true");
                }

                bool verdict = equipOwnerOk && checkClaimsOwnerOk && equipApplied && checkClaimsApplied;

                RoleLogger.Info("[Shared]",
                    $"[5B-1B/VerifyAll] final verdict={verdict} " +
                    $"equip(owner={equipOwnerOk}, applied={equipApplied}) " +
                    $"checkClaims(owner={checkClaimsOwnerOk}, applied={checkClaimsApplied})");
                return verdict;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]",
                    $"[5B-1B/VerifyAll] 异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// v2.5 P1-3 + Codex 60th P1-3：缓存 DiagnosticBuildValid。
        /// 综合 verifyResult + _registrationSucceeded + rollbackNotMisjudged + _resetCallbackRegistered 四项。
        /// </summary>
        private static void CacheDiagnosticBuildValid(bool verifyResult)
        {
            bool rollbackNotMisjudged = !_rollbackAttempted || !_registrationSucceeded;
            _diagnosticBuildValid = verifyResult
                && _registrationSucceeded
                && rollbackNotMisjudged
                && _resetCallbackRegistered;

            RoleLogger.Info("[Shared]",
                $"[5B-1B/CacheDiagnosticBuildValid] verifyResult={verifyResult} " +
                $"registrationSucceeded={_registrationSucceeded} " +
                $"rollbackNotMisjudged={rollbackNotMisjudged} " +
                $"resetCallbackRegistered={_resetCallbackRegistered} " +
                $"diagnosticBuildValid={_diagnosticBuildValid}");
        }

        /// <summary>
        /// Codex 60th P0-5 + P0-7：RollbackBoth 回滚已登记的 Transpiler。
        /// - 遍历所有匹配的 Patch ID（不 break）
        /// - 通过 Harmony.GetPatchInfo 验证 exact count 均为 0
        /// - _rollbackClean 仅来自上述验证结果
        /// - 末尾清零两个 ReplacementApplied 字段
        /// </summary>
        private static void RollbackBoth(string reason)
        {
            _rollbackAttempted = true;
            _registrationSucceeded = false;

            RoleLogger.Info("[Shared]",
                $"[5B-1B/RollbackBoth] 开始 reason=\"{reason}\"");

            Harmony harmony = _harmonyInstance;
            if (harmony == null)
            {
                RoleLogger.Error("[Shared]",
                    "[5B-1B/RollbackBoth] _harmonyInstance=null，无法回滚");
                _rollbackClean = false;
                Volatile.Write(ref _equipReplacementApplied, 0);
                Volatile.Write(ref _checkClaimsReplacementApplied, 0);
                _diagnosticBuildValid = false;
                return;
            }

            // 移除 equip 上所有本插件 Transpiler
            int equipRemovedCount = RemoveAllOwnTranspilers(harmony, _equipMethod, _equipTranspiler, "equip");
            // 移除 checkClaims 上所有本插件 Transpiler
            int checkClaimsRemovedCount = RemoveAllOwnTranspilers(harmony, _checkClaimsMethod, _checkClaimsTranspiler, "checkClaims");

            // Codex 60th P0-5：通过 Harmony.GetPatchInfo 验证 exact count 均为 0
            bool equipExactZero = VerifyExactCountZero(_equipMethod, _equipTranspiler, "equip");
            bool checkClaimsExactZero = VerifyExactCountZero(_checkClaimsMethod, _checkClaimsTranspiler, "checkClaims");

            _rollbackClean = equipExactZero && checkClaimsExactZero;

            // Codex 60th P0-7：清零两个 ReplacementApplied 字段
            Volatile.Write(ref _equipReplacementApplied, 0);
            Volatile.Write(ref _checkClaimsReplacementApplied, 0);

            // v2.5 P1-3：清除 DiagnosticBuildValid 缓存
            _diagnosticBuildValid = false;

            RoleLogger.Info("[Shared]",
                $"[5B-1B/RollbackBoth] 完成 rollbackAttempted={_rollbackAttempted} " +
                $"rollbackClean={_rollbackClean} diagnosticBuildValid={_diagnosticBuildValid} " +
                $"equipRemovedCount={equipRemovedCount} checkClaimsRemovedCount={checkClaimsRemovedCount} " +
                $"equipExactZero={equipExactZero} checkClaimsExactZero={checkClaimsExactZero} " +
                $"equipReplacementApplied={EquipReplacementApplied} " +
                $"checkClaimsReplacementApplied={CheckClaimsReplacementApplied}");
        }

        /// <summary>
        /// Codex 60th P0-5：移除指定 original 上所有本插件目标 Transpiler。
        /// 遍历所有匹配的 Patch（不 break），避免重复登记时只移除一个。
        /// </summary>
        private static int RemoveAllOwnTranspilers(Harmony harmony, MethodInfo original, MethodInfo expectedTranspiler, string label)
        {
            if (original == null || expectedTranspiler == null || harmony == null)
            {
                return 0;
            }

            int removedCount = 0;
            try
            {
                HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
                if (info?.Transpilers == null) return 0;

                // 收集所有匹配的 Patch method（避免遍历时修改集合）
                var toRemove = new List<MethodInfo>();
                foreach (Patch p in info.Transpilers)
                {
                    if (p.owner == SteamP2PFriendsPlugin.HARMONY_ID
                        && BarricadeLifecycleILMatcher.MethodIdentityEqual(p.PatchMethod, expectedTranspiler))
                    {
                        toRemove.Add(p.PatchMethod);
                    }
                }

                // 移除所有匹配的 Patch
                foreach (MethodInfo patchMethod in toRemove)
                {
                    try
                    {
                        harmony.Unpatch(original, patchMethod);
                        removedCount++;
                    }
                    catch (System.Exception ex)
                    {
                        RoleLogger.Error("[Shared]",
                            $"[5B-1B/RollbackBoth/{label}] 移除 Patch 异常: {ex.Message}");
                    }
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]",
                    $"[5B-1B/RollbackBoth/{label}] RemoveAllOwnTranspilers 异常: {ex.Message}");
            }

            return removedCount;
        }

        /// <summary>
        /// Codex 60th P0-5：通过 Harmony.GetPatchInfo 验证本 owner+本 PatchMethod exact count 为 0。
        /// </summary>
        private static bool VerifyExactCountZero(MethodInfo original, MethodInfo expectedTranspiler, string label)
        {
            try
            {
                if (original == null || expectedTranspiler == null) return false;

                HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
                if (info?.Transpilers == null || info.Transpilers.Count == 0)
                {
                    return true;
                }

                int exactCount = 0;
                foreach (Patch p in info.Transpilers)
                {
                    if (p.owner == SteamP2PFriendsPlugin.HARMONY_ID
                        && BarricadeLifecycleILMatcher.MethodIdentityEqual(p.PatchMethod, expectedTranspiler))
                    {
                        exactCount++;
                    }
                }

                if (exactCount != 0)
                {
                    RoleLogger.Error("[Shared]",
                        $"[5B-1B/VerifyExactCountZero/{label}] FAIL exact={exactCount} expected=0 " +
                        $"method={original.Name}");
                    return false;
                }

                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]",
                    $"[5B-1B/VerifyExactCountZero/{label}] 异常: {ex.Message}");
                return false;
            }
        }
    }
}
