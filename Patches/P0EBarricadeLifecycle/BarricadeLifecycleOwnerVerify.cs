using HarmonyLib;
using SteamP2PFriends.Shared;
using System.Reflection;

namespace SteamP2PFriends.Patches.P0EBarricadeLifecycle
{
    /// <summary>
    /// v0.2.3.39 5B-1B v2.5（Codex 第五十九次审计 🟢 放行编码）：
    /// Barricade equip/checkClaims Transpiler 的 owner + priority + exact count 自检。
    ///
    /// 设计依据：.audit/v0.2.3.39-stage5B-1B-v2.5-design-20260727/barricade-fix-design-v2.5-20260727.md §4 + §8
    ///
    /// count-based 裁决（v2.4 §4.2 + v2.5 §6 VerifyAll）：
    ///   - exact == 1            （本插件精确 Transpiler 出现 1 次）
    ///   - priorityMatch == 1    （本插件精确 Transpiler + Priority.Normal 出现 1 次）
    ///   - ownerMatch == 1       （本 owner 在此方法上仅 1 个 Transpiler，无同 owner 其他 Transpiler 共存）
    ///   - duplicateExpected == false （exact 不超过 1，无重复登记）
    ///   - noForeign             （无外部 owner Transpiler）
    ///
    /// C4 硬约束（Codex 59th §2.4）：
    ///   foreign Transpiler 触发 fail-closed 时，日志写 action=will_rollback_this_plugin_patches，
    ///   不能在 VerifyAll 阶段提前声称已经完成回滚。真正的 rollbackClean 结果由随后 RollbackBoth 日志输出。
    /// </summary>
    public static class BarricadeLifecycleOwnerVerify
    {
        /// <summary>
        /// 验证指定 original 方法上的 Transpiler owner / priority / exact count。
        /// 返回 true 仅当 5 个条件全部满足。summaryOut 输出详细计数摘要。
        /// </summary>
        public static bool VerifyOwnerAndPriority(
            MethodInfo original,
            MethodInfo expectedTranspiler,
            out string summaryOut)
        {
            summaryOut = "<unverified>";

            if (original == null)
            {
                summaryOut = "original=null";
                return false;
            }
            if (expectedTranspiler == null)
            {
                summaryOut = "expectedTranspiler=null";
                return false;
            }

            try
            {
                HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
                System.Collections.ICollection patches = info?.Transpilers;
                if (patches == null || patches.Count == 0)
                {
                    summaryOut = "transpilers count=0";
                    return false;
                }

                int exactCount = 0;            // our owner + our exact method
                int priorityMatch = 0;         // our owner + our exact method + Priority.Normal
                int ownerMatch = 0;            // our owner (any transpiler)
                int foreignCount = 0;          // foreign owner
                string firstForeignOwner = null;
                string firstForeignMethod = null;

                foreach (Patch p in patches)
                {
                    bool isOurOwner = (p.owner == SteamP2PFriendsPlugin.HARMONY_ID);
                    bool isExactMethod = BarricadeLifecycleILMatcher.MethodIdentityEqual(
                        p.PatchMethod, expectedTranspiler);

                    if (isOurOwner)
                    {
                        ownerMatch++;

                        if (isExactMethod)
                        {
                            exactCount++;

                            // Priority.Normal == 500（Harmony 2.x 默认值）
                            if (p.priority == Priority.Normal)
                            {
                                priorityMatch++;
                            }
                        }
                    }
                    else
                    {
                        foreignCount++;
                        if (firstForeignOwner == null)
                        {
                            firstForeignOwner = p.owner ?? "<null>";
                            string foreignTypeName = p.PatchMethod?.DeclaringType?.FullName ?? "<null>";
                            string foreignMethodName = p.PatchMethod?.Name ?? "<null>";
                            firstForeignMethod = $"{foreignTypeName}.{foreignMethodName}";
                        }
                    }
                }

                bool duplicateExpected = exactCount > 1;
                bool noForeign = foreignCount == 0;
                bool exactOk = exactCount == 1;
                bool priorityOk = priorityMatch == 1;
                bool ownerOk = ownerMatch == 1;

                summaryOut = $"exact={exactCount} priorityMatch={priorityMatch} " +
                    $"ownerMatch={ownerMatch} duplicateExpected={duplicateExpected} " +
                    $"foreign={foreignCount} foreignOwner={firstForeignOwner ?? "<none>"} " +
                    $"foreignMethod={firstForeignMethod ?? "<none>"} " +
                    $"total={patches.Count}";

                // v2.5 §8 + C4：foreign 触发时明确标为 COMPATIBILITY_GUARD + "will_rollback"
                if (!noForeign)
                {
                    RoleLogger.Error("[Shared]",
                        $"[5B-1B/VerifyOwner/{original.Name}] COMPATIBILITY_GUARD foreign Transpiler detected " +
                        $"foreign={foreignCount} firstForeignOwner={firstForeignOwner} " +
                        $"method={original.Name} " +
                        $"action=will_rollback_this_plugin_patches " +
                        $"reason=compatibility_protection_not_gameplay_logic " +
                        $"message=另一个模组在 {original.Name} 上登记了 Transpiler，" +
                        $"本插件为避免 IL 冲突将触发回滚，这不是游戏逻辑异常");
                }

                if (duplicateExpected)
                {
                    RoleLogger.Error("[Shared]",
                        $"[5B-1B/VerifyOwner/{original.Name}] duplicate detected " +
                        $"exact={exactCount} expected=1 method={original.Name}");
                }

                if (!exactOk)
                {
                    RoleLogger.Error("[Shared]",
                        $"[5B-1B/VerifyOwner/{original.Name}] exact mismatch " +
                        $"exact={exactCount} expected=1");
                }

                if (!priorityOk)
                {
                    RoleLogger.Error("[Shared]",
                        $"[5B-1B/VerifyOwner/{original.Name}] priority mismatch " +
                        $"priorityMatch={priorityMatch} expected=1 " +
                        $"(Priority.Normal={Priority.Normal})");
                }

                if (!ownerOk)
                {
                    RoleLogger.Error("[Shared]",
                        $"[5B-1B/VerifyOwner/{original.Name}] owner count mismatch " +
                        $"ownerMatch={ownerMatch} expected=1 " +
                        "(同 owner 其他 Transpiler 共存不被允许，因 equip/checkClaims 应仅由本插件 Transpile)");
                }

                bool verdict = exactOk && priorityOk && ownerOk && !duplicateExpected && noForeign;
                return verdict;
            }
            catch (System.Exception ex)
            {
                summaryOut = $"exception: {ex.Message}";
                RoleLogger.Error("[Shared]",
                    $"[5B-1B/VerifyOwner/{original.Name}] 异常: {ex.Message}");
                return false;
            }
        }
    }
}
