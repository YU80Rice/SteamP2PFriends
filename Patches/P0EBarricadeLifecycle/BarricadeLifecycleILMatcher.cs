using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace SteamP2PFriends.Patches.P0EBarricadeLifecycle
{
    /// <summary>
    /// Barricade equip/checkClaims Transpiler 的 IL 匹配器。
    ///
    /// 设计依据：
    ///
    ///   - 原始 Assembly-CSharp IL 编码：checkClaims 使用 brfalse.s，equip 使用 brfalse
    ///   - Harmony ILManipulator.NormalizeInstructions 通过 ShortToLongMap 将 Brfalse_S 规范化为 Brfalse
    ///   - PatchProcessor.GetCurrentInstructions 与 Transpiler 收到的 CodeInstruction 均为规范化后的形式（未应用 Transpiler 的快照仍是规范化后表示）
    ///   - 因此验证不得按方法名强制 Brfalse 或 Brfalse_S，应按语义验证 false-branch
    ///
    ///   - MatchDedicatorBranch 保留：getter 后紧邻 Brfalse 或 Brfalse_S
    ///   - ValidateFalseBranchOpcode：仅当 actual 既不是 Brfalse 也不是 Brfalse_S 时失败
    ///   - 不再按方法名区分 equip/checkClaims 的分支编码
    /// </summary>
    public static class BarricadeLifecycleILMatcher
    {
        /// <summary>
        /// Dedicator.get_IsDedicatedServer 的 MethodInfo 缓存（启动时一次性反射）。
        /// </summary>
        private static readonly MethodInfo _dedicatorIsDedicatedServerGetter =
            AccessTools.PropertyGetter(typeof(Dedicator), nameof(Dedicator.IsDedicatedServer));

        /// <summary>
        /// 在 IL 中匹配 call get_IsDedicatedServer() 后紧邻 Brfalse 或 Brfalse_S 的位置。
        /// 返回所有匹配位置的索引列表（不修改 IL）。
        /// </summary>
        public static List<int> MatchDedicatorBranch(IList<CodeInstruction> codes)
        {
            var matches = new List<int>();
            if (codes == null) return matches;

            MethodInfo getter = _dedicatorIsDedicatedServerGetter;
            if (getter == null) return matches;

            for (int i = 0; i < codes.Count; i++)
            {
                CodeInstruction instr = codes[i];
                if (instr == null) continue;

                // 使用 CodeInstruction.Calls 扩展方法
                if (!instr.Calls(getter)) continue;

                int nextIdx = i + 1;
                if (nextIdx >= codes.Count) continue;

                OpCode nextOp = codes[nextIdx].opcode;
                if (nextOp != OpCodes.Brfalse && nextOp != OpCodes.Brfalse_S) continue;

                matches.Add(i);
            }

            return matches;
        }

        /// <summary>
        /// 验证匹配结果恰好为 1 个，并输出调用点索引供 Transpiler 在 callIdx+1 处插入。
        /// 返回 false 时 callIdx=-1，调用方应 throw 拒绝部分应用。
        /// </summary>
        public static bool ValidateSingleMatch(List<int> matches, MethodInfo original, out int callIdx)
        {
            callIdx = -1;

            if (matches == null || matches.Count == 0)
            {
                RoleLogger.Error("[Shared]",
                    $"[5B-1B/Matcher] FAIL zero matches method={original?.Name ?? "null"} " +
                    $"dedicatorGetterCached={_dedicatorIsDedicatedServerGetter != null}");
                return false;
            }

            if (matches.Count > 1)
            {
                RoleLogger.Error("[Shared]",
                    $"[5B-1B/Matcher] FAIL multiple matches method={original?.Name ?? "null"} " +
                    $"count={matches.Count} expected=1");
                return false;
            }

            callIdx = matches[0];
            RoleLogger.Info("[Shared]",
                $"[5B-1B/Matcher] OK single match method={original?.Name ?? "null"} callIdx={callIdx}");
            return true;
        }

        /// <summary>
        /// 验证指定 callIdx 后紧邻的分支指令是否为 false-branch 语义（Brfalse 或 Brfalse_S 任一）。
        /// Harmony NormalizeInstructions 会把 Brfalse_S 规范化为 Brfalse，因此 CodeInstruction 层
        /// 看到的可能是任一形式（取决于 Harmony 版本与规范化时机）。
        /// 输出 actual 供调用方记录日志，不参与通过/失败判定（除非 actual 既非 Brfalse 也非 Brfalse_S）。
        /// </summary>
        public static bool ValidateFalseBranchOpcode(
            IList<CodeInstruction> codes, int callIdx, string label, out OpCode actual)
        {
            actual = OpCodes.Nop;

            if (codes == null || callIdx < 0 || callIdx + 1 >= codes.Count)
            {
                RoleLogger.Error("[Shared]",
                    $"[5B-1B/Matcher/{label}] FAIL invalid callIdx or codes for branch validation");
                return false;
            }

            actual = codes[callIdx + 1].opcode;
            if (actual != OpCodes.Brfalse && actual != OpCodes.Brfalse_S)
            {
                RoleLogger.Error("[Shared]",
                    $"[5B-1B/Matcher/{label}] FAIL branch opcode not false-branch " +
                    $"actual={actual} callIdx={callIdx} " +
                    $"accepted=Brfalse|Brfalse_S");
                return false;
            }

            return true;
        }

        /// <summary>
        /// internal：MethodInfo 身份相等比较（Name + DeclaringType + ReturnType + Parameters）。
        /// 供 Registration.VerifyOwnerAndPriority 使用，不依赖 MetadataToken（避免动态方法异常）。
        /// </summary>
        internal static bool MethodIdentityEqual(MethodInfo a, MethodInfo b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;

            if (a.Name != b.Name) return false;
            if (!ReferenceEquals(a.DeclaringType, b.DeclaringType)) return false;
            if (a.ReturnType != b.ReturnType) return false;

            ParameterInfo[] pa = a.GetParameters();
            ParameterInfo[] pb = b.GetParameters();
            if (pa.Length != pb.Length) return false;

            for (int i = 0; i < pa.Length; i++)
            {
                if (pa[i].ParameterType != pb[i].ParameterType) return false;
            }

            return true;
        }
    }
}
