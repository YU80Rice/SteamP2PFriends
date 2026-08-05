using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System;
using System.Reflection;

namespace SteamP2PFriends.Patches.P0EZombieLifecycle
{
    /// <summary>
    /// v0.2.3.39 Zombie 生命周期 v6.6 owner 自检（Codex 第五十三次审计 §3 P0-1 返修）：
    ///
    /// 对 `ZombieManager.onBoundUpdated(Player, byte, byte)` 的三种 Patch（Prefix/Postfix/Finalizer）
    /// 执行精确 owner + MethodInfo identity + count 自检。
    ///
    /// v6.6 返修要点（Codex 第五十三次审计 §3.2）：
    ///   - 旧实现仅检查 `methodMatched=true`，重复登记两个相同 Hook 时仍可能通过
    ///   - 新实现改为 count-based 裁决：`exactCount == 1 && priorityMatchCount == 1`
    ///   - 实现 `IsSameMethodInfo` 精确比较 MethodInfo identity
    ///   - 输出 `exact=1/1 priorityMatch=1/1 sameOwnerOtherMethod foreignOwnerCount duplicateExpected`
    ///
    /// 自检规则（Codex 第二十四次审计 §Harmony 多 Prefix 同 owner 共存规则 + 第五十三次 §3.2 exact-count）：
    ///   - 同 owner (Harmony ID) + 同 vanilla 方法 + 不同 PatchMethod 的多个 Prefix/Postfix/Finalizer 是合法共存
    ///   - `exactCount == 1`：expected MethodInfo 必须恰好出现 1 次（拒绝重复登记）
    ///   - `priorityMatchCount == 1`：expected MethodInfo + 期望 Priority 必须恰好出现 1 次
    ///   - `sameOwnerOtherMethodCount`：同 owner 但不同 MethodInfo 的数量（合法共存，仅信息输出）
    ///   - `foreignOwnerCount`：非本 owner 的数量（仅观测）
    ///   - `duplicateExpected`：expected MethodInfo 出现超过 1 次（重复登记标志）
    ///
    /// Priority 校验：
    ///   - Prefix 期望 Priority.VeryLow（Harmony 2.x 实际值 = 100）
    ///   - Postfix 期望 Priority.High（Harmony 2.x 实际值 = 600）
    ///   - Finalizer 期望 Priority.High（Harmony 2.x 实际值 = 600）
    /// </summary>
    internal static class ZombieLifecycleOwnerVerify
    {
        private const string HarmonyId = SteamP2PFriendsPlugin.HARMONY_ID;

        /// <summary>
        /// 对三种 Patch（Prefix/Postfix/Finalizer）逐一执行 owner exact-count 自检。
        /// 任一类型失败返回 false。
        /// </summary>
        public static bool VerifyAllPatches(
            MethodInfo originalMethod,
            MethodInfo expectedPrefix,
            MethodInfo expectedPostfix,
            MethodInfo expectedFinalizer)
        {
            if (originalMethod == null)
            {
                RoleLogger.Error("[Shared]", "[P0-E-Zombie-v6.6/OwnerVerify] originalMethod=null");
                return false;
            }

            bool preOk = VerifyOnePatch(originalMethod, expectedPrefix, HarmonyPatchType.Prefix, Priority.VeryLow, "Prefix");
            bool postOk = VerifyOnePatch(originalMethod, expectedPostfix, HarmonyPatchType.Postfix, Priority.High, "Postfix");
            bool finOk = VerifyOnePatch(originalMethod, expectedFinalizer, HarmonyPatchType.Finalizer, Priority.High, "Finalizer");

            bool allOk = preOk && postOk && finOk;
            RoleLogger.Info("[Shared]",
                $"[P0-E-Zombie-v6.6/OwnerVerify] 汇总: Prefix={preOk} Postfix={postOk} Finalizer={finOk} all={allOk}");
            return allOk;
        }

        /// <summary>
        /// 单一 Patch 类型 exact-count 自检：在指定 Patch 集合中精确统计 expected MethodInfo 出现次数。
        /// 成功条件：`exactCount == 1 && priorityMatchCount == 1`。
        /// </summary>
        private static bool VerifyOnePatch(
            MethodInfo originalMethod,
            MethodInfo expectedPatchMethod,
            HarmonyPatchType patchType,
            int expectedPriority,
            string label)
        {
            if (expectedPatchMethod == null)
            {
                RoleLogger.Error("[Shared]",
                    $"[P0-E-Zombie-v6.6/OwnerVerify] {label} expectedPatchMethod=null");
                return false;
            }

            HarmonyLib.Patches info = Harmony.GetPatchInfo(originalMethod);
            if (info == null)
            {
                RoleLogger.Error("[Shared]",
                    $"[P0-E-Zombie-v6.6/OwnerVerify] {label} GetPatchInfo=null (original={originalMethod.Name})");
                return false;
            }

            System.Collections.ICollection patches;
            switch (patchType)
            {
                case HarmonyPatchType.Prefix: patches = info.Prefixes; break;
                case HarmonyPatchType.Postfix: patches = info.Postfixes; break;
                case HarmonyPatchType.Finalizer: patches = info.Finalizers; break;
                default:
                    RoleLogger.Error("[Shared]",
                        $"[P0-E-Zombie-v6.6/OwnerVerify] {label} unsupported patchType={patchType}");
                    return false;
            }

            int exactCount = 0;
            int priorityMatchCount = 0;
            int sameOwnerOtherMethodCount = 0;
            int foreignOwnerCount = 0;
            bool duplicateExpected = false;
            string firstForeignOwner = null;

            foreach (Patch p in patches)
            {
                if (p.owner != HarmonyId)
                {
                    foreignOwnerCount++;
                    if (firstForeignOwner == null)
                    {
                        firstForeignOwner = p.owner;
                    }
                    continue;
                }

                if (IsSameMethodInfo(p.PatchMethod, expectedPatchMethod))
                {
                    exactCount++;
                    if (p.priority == expectedPriority)
                    {
                        priorityMatchCount++;
                    }
                    if (exactCount > 1)
                    {
                        duplicateExpected = true;
                    }
                }
                else
                {
                    // 同 owner 但不同 MethodInfo（合法共存，如 P0-D Prefix）
                    sameOwnerOtherMethodCount++;
                }
            }

            // Codex §3.2：成功条件为 exactCount == 1 && priorityMatchCount == 1
            bool ok = exactCount == 1 && priorityMatchCount == 1;

            string summary =
                $"exact={exactCount}/1 priorityMatch={priorityMatchCount}/1 " +
                $"sameOwnerOtherMethod={sameOwnerOtherMethodCount} " +
                $"foreignOwnerCount={foreignOwnerCount} firstForeignOwner={firstForeignOwner ?? "none"} " +
                $"duplicateExpected={duplicateExpected}";

            if (!ok)
            {
                string failReason;
                if (exactCount == 0)
                {
                    failReason = "expected MethodInfo NOT FOUND in same-owner patches";
                }
                else if (exactCount > 1)
                {
                    failReason = $"expected MethodInfo DUPLICATED (exactCount={exactCount}, duplicateExpected=true)";
                }
                else if (priorityMatchCount == 0)
                {
                    failReason = $"Priority MISMATCH (expected={expectedPriority} not found on the single matched MethodInfo)";
                }
                else
                {
                    failReason = $"unexpected state (exactCount={exactCount} priorityMatchCount={priorityMatchCount})";
                }

                RoleLogger.Error("[Shared]",
                    $"[P0-E-Zombie-v6.6/OwnerVerify] {label} FAIL: {failReason} | {summary}");
                return false;
            }

            // exactCount == 1 && priorityMatchCount == 1 -> 通过
            // sameOwnerOtherMethodCount 与 foreignOwnerCount 仅信息输出
            RoleLogger.Info("[Shared]",
                $"[P0-E-Zombie-v6.6/OwnerVerify] {label} OK: {summary}");
            return true;
        }

        /// <summary>
        /// 精确比较两个 MethodInfo identity（Codex 第五十三次审计 §3.2）。
        ///
        /// 比较顺序（短路 OR）：
        ///   1. ReferenceEquals(a, b) -> true（同一引用）
        ///   2. Module 相同 && MetadataToken 相同 -> true（同一模块同一 token）
        ///   3. DeclaringType + Name + ReturnType + 完整参数类型序列相同 -> true（兜底）
        /// </summary>
        private static bool IsSameMethodInfo(MethodInfo a, MethodInfo b)
        {
            if (a == null || b == null) return false;

            // 第 1 级：引用相同
            if (ReferenceEquals(a, b)) return true;

            // 第 2 级：Module + MetadataToken
            try
            {
                if (a.Module == b.Module && a.MetadataToken == b.MetadataToken)
                {
                    return true;
                }
            }
            catch
            {
                // MetadataToken 在某些动态方法上可能抛异常，降级到第 3 级
            }

            // 第 3 级：DeclaringType + Name + ReturnType + 完整参数类型序列
            try
            {
                if (a.DeclaringType != b.DeclaringType) return false;
                if (a.Name != b.Name) return false;
                if (a.ReturnType != b.ReturnType) return false;

                ParameterInfo[] paramsA = a.GetParameters();
                ParameterInfo[] paramsB = b.GetParameters();
                if (paramsA.Length != paramsB.Length) return false;

                for (int i = 0; i < paramsA.Length; i++)
                {
                    if (paramsA[i].ParameterType != paramsB[i].ParameterType) return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
