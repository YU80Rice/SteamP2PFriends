using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace SteamP2PFriends.Patches.P0EBarricadeLifecycle
{
    /// <summary>
    /// v0.2.3.39 5B-1B v2.5.1（Codex 第六十二次审计返修）：
    /// Barricade equip() 与 checkClaims() 的 Transpiler。
    ///
    /// 设计依据：
    ///   .audit/v0.2.3.39-stage5B-1B-v2.5-smoke-20260728/Codex第六十二次Barricade-v2.5冒烟失败审计与指导报告-20260728.md §4.3
    ///   .audit/v0.2.3.39-stage5B-1B-v2.5-design-20260727/barricade-fix-design-v2.5-20260727.md §2.3
    ///
    /// IL 修改模式（v2.5.1 §4.3）：
    ///   原版 IL: call Dedicator::get_IsDedicatedServer() -> brfalse/brfalse.s
    ///   修改后:  call Dedicator::get_IsDedicatedServer()
    ///            ldarg.0
    ///            call BarricadeLifecycleHelper::IsListenHostRemoteXxxInstance(UseableBarricade)
    ///            or
    ///            brfalse/brfalse.s（原指令保留，不修改、不替换、不重建）
    ///
    /// 栈平衡：
    ///   原版：get_IsDedicatedServer()（无参，返回 bool i4） => 栈 +1
    ///   插入：ldarg.0（+1）+ call Helper（-1+1=0 净）+ or（-2+1=-1） => 净 +0
    ///   一致。
    ///
    /// Codex 62nd §4.3 v2.5.1 修复：
    ///   - 两条 Transpiler 均使用同一 false-branch 结构验证（ValidateFalseBranchOpcode）
    ///   - 不再按方法名强制 equip=Brfalse、checkClaims=Brfalse_S
    ///   - 日志记录 actualBranch（Harmony 规范化后的实际 opcode），不参与通过/失败判定
    ///   - 原分支指令保留，只在前方插入既定三条 IL（ldarg.0; call; or）
    ///
    /// 硬约束（Codex 59th §5 验收门第 11-12 项，Codex 62nd §4.3 重申）：
    ///   - 插入 IL 仅 3 条：ldarg.0; call cachedHelper; or
    ///   - 不新增 Label、Dup 或额外 branch
    ///   - 不修改原 IL 的 labels/blocks
    ///   - 不修改、替换或重新创建原 branch 指令
    ///   - 使用 Registration.EquipMethod / EquipHelperMethod 完整限定（不跨类访问 private 字段）
    ///   - 失败时 throw 拒绝部分应用
    /// </summary>
    public static class BarricadeLifecycleTranspiler
    {
        /// <summary>
        /// equip() Transpiler：在 Dedicator.IsDedicatedServer 调用后插入 OR Helper。
        /// vanilla UseableBarricade.cs:1617 if (Dedicator.IsDedicatedServer) 改为
        ///   if (Dedicator.IsDedicatedServer || IsListenHostRemoteEquipInstance(this))
        /// </summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(UseableBarricade), "equip")]
        public static IEnumerable<CodeInstruction> Equip_Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            var list = new List<CodeInstruction>(instructions);
            var matches = BarricadeLifecycleILMatcher.MatchDedicatorBranch(list);

            // v2.5 P0-1：使用 BarricadeLifecycleRegistration.EquipMethod 完整限定（不跨类访问 private 字段）
            if (!BarricadeLifecycleILMatcher.ValidateSingleMatch(
                    matches, BarricadeLifecycleRegistration.EquipMethod, out int callIdx))
            {
                throw new System.InvalidOperationException(
                    "[5B-1B/Transpiler/equip] Dedicator 分支匹配失败，拒绝部分应用");
            }

            // Codex 62nd §4.3 v2.5.1：语义验证 false-branch（Brfalse 或 Brfalse_S 任一）
            if (!BarricadeLifecycleILMatcher.ValidateFalseBranchOpcode(
                    list, callIdx, "equip", out OpCode actualBranch))
            {
                throw new System.InvalidOperationException(
                    "[5B-1B/Transpiler/equip] false-branch 语义验证失败，拒绝部分应用");
            }

            // v2.5 P0-1：使用 BarricadeLifecycleRegistration.EquipHelperMethod 完整限定
            MethodInfo helperMethod = BarricadeLifecycleRegistration.EquipHelperMethod;
            if (helperMethod == null)
            {
                throw new System.InvalidOperationException(
                    "[5B-1B/Transpiler/equip] equip Helper MethodInfo 未缓存，FAIL-CLOSED");
            }

            // v2.5 §5 验收门第 11 项：插入 3 条 IL（ldarg.0; call helper; or）
            // v2.5 §5 验收门第 12 项：不新增 Label、Dup、额外 branch
            // Codex 62nd §4.3：不修改、替换或重新创建原 branch 指令
            // InsertRange 在 callIdx+1 处插入，原指令的 labels/blocks 保留在其 CodeInstruction 对象上
            list.InsertRange(callIdx + 1, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, helperMethod),
                new CodeInstruction(OpCodes.Or),
            });

            // v2.5 P0-1 + P1-2 + C2：使用 MarkEquipReplacementApplied() 完整限定
            BarricadeLifecycleRegistration.MarkEquipReplacementApplied();

            RoleLogger.Info("[Shared]",
                $"[5B-1B/Transpiler/equip] OK insertedAt={callIdx + 1} helper={helperMethod.DeclaringType.Name}.{helperMethod.Name} actualBranch={actualBranch}");

            return list;
        }

        /// <summary>
        /// checkClaims() Transpiler：在 Dedicator.IsDedicatedServer 调用后插入 OR Helper。
        /// vanilla UseableBarricade.cs:543 if (Dedicator.IsDedicatedServer) 改为
        ///   if (Dedicator.IsDedicatedServer || IsListenHostRemoteCheckClaimsInstance(this))
        /// </summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(UseableBarricade), "checkClaims")]
        public static IEnumerable<CodeInstruction> CheckClaims_Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            var list = new List<CodeInstruction>(instructions);
            var matches = BarricadeLifecycleILMatcher.MatchDedicatorBranch(list);

            // v2.5 P0-1：使用 BarricadeLifecycleRegistration.CheckClaimsMethod 完整限定
            if (!BarricadeLifecycleILMatcher.ValidateSingleMatch(
                    matches, BarricadeLifecycleRegistration.CheckClaimsMethod, out int callIdx))
            {
                throw new System.InvalidOperationException(
                    "[5B-1B/Transpiler/checkClaims] Dedicator 分支匹配失败，拒绝部分应用");
            }

            // Codex 62nd §4.3 v2.5.1：语义验证 false-branch（Brfalse 或 Brfalse_S 任一）
            if (!BarricadeLifecycleILMatcher.ValidateFalseBranchOpcode(
                    list, callIdx, "checkClaims", out OpCode actualBranch))
            {
                throw new System.InvalidOperationException(
                    "[5B-1B/Transpiler/checkClaims] false-branch 语义验证失败，拒绝部分应用");
            }

            // v2.5 P0-1：使用 BarricadeLifecycleRegistration.CheckClaimsHelperMethod 完整限定
            MethodInfo helperMethod = BarricadeLifecycleRegistration.CheckClaimsHelperMethod;
            if (helperMethod == null)
            {
                throw new System.InvalidOperationException(
                    "[5B-1B/Transpiler/checkClaims] checkClaims Helper MethodInfo 未缓存，FAIL-CLOSED");
            }

            list.InsertRange(callIdx + 1, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, helperMethod),
                new CodeInstruction(OpCodes.Or),
            });

            // v2.5 P0-1 + P1-2 + C2：使用 MarkCheckClaimsReplacementApplied() 完整限定
            BarricadeLifecycleRegistration.MarkCheckClaimsReplacementApplied();

            RoleLogger.Info("[Shared]",
                $"[5B-1B/Transpiler/checkClaims] OK insertedAt={callIdx + 1} helper={helperMethod.DeclaringType.Name}.{helperMethod.Name} actualBranch={actualBranch}");

            return list;
        }
    }
}
