using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace SteamP2PFriends.Patches.P0EZombieLifecycle
{
    /// <summary>
    /// v0.2.3.39 Zombie 生命周期 v6.6（Codex 第五十二次审计 §5 放行编码）：
    ///
    /// 根因（U3-SDK ZombieManager.cs:1448-1494 onBoundUpdated）：
    ///   - L1452 if (LevelNavigation.checkSafe(oldBound) && regions[oldBound].isNetworked)
    ///       L1454 regions[oldBound].destroy();
    ///       L1455 regions[oldBound].isNetworked = false;
    ///     本地主机离开 old bound 时，只检查 regions[oldBound].isNetworked，不检查远端玩家是否仍占用。
    ///   - L1475 if (!player.movement.loadedBounds[newBound].isZombiesLoaded)
    ///       L1477 if (player.channel.IsLocalPlayer)
    ///         L1479 generateZombies(newBound);
    ///         L1481 regions[newBound].isNetworked = true;
    ///       L1488 player.movement.loadedBounds[newBound].isZombiesLoaded = true;
    ///     本地主机进入 new bound 时，若 loadedBounds[newBound].isZombiesLoaded=false，会调用 generateZombies
    ///     （即使该 bound 已由 P0-D 为远端客机 generate 过，仍会重复 generate）。
    ///
    /// 修复方案（v6.6，Codex 第五十二次审计 §5 放行编码）：
    ///   - Prefix（VeryLow）：在 vanilla L1452/L1475 之前，临时修改两个标志位：
    ///     * TryProtectOldBound：当远端客机仍在 old bound 时，临时设置 regions[oldBound].isNetworked=false
    ///       （让 vanilla L1452 跳过 destroy）。Postfix 与 Finalizer 无条件幂等恢复。
    ///     * TryProcessNewBound：当 newRegion.isNetworked=true（房主或 P0-D 已 generate 过）且
    ///       newLoadedBound.isZombiesLoaded=false（本地主机首次进入该 bound）时，临时设置
    ///       loadedBounds[newBound].isZombiesLoaded=true（让 vanilla L1475 跳过 generateZombies 分支）。
    ///       Finalizer 仅在异常时回滚。
    ///   - Postfix（High）：乐观路径，原方法成功后恢复 old isNetworked。
    ///   - Finalizer（High）：兜底无条件恢复 old isNetworked；仅在异常时回滚 new loaded；始终返回 __exception。
    ///
    /// v6.6 关键修订（Codex 第五十一次审计 §3-§4）：
    ///   1. old/new 控制流完全独立：TryProtectOldBound + TryProcessNewBound 两条独立 try-catch 路径，互不阻断。
    ///   2. Finalizer 无条件幂等恢复 old（无论 __exception 是否为空）。
    ///   3. new-bound 仅在原方法异常时回滚。
    ///   4. Finalizer 始终原样返回 __exception（不吞异常）。
    ///
    /// Harmony Priority 设计：
    ///   - Prefix=VeryLow (-200)：让诊断 Prefix (Normal=0) 与 P0-D Prefix (Low=-100) 先执行。
    ///   - Postfix=High (100)：让诊断 Postfix (Normal=0) 先记录 vanilla 后状态，然后功能 Postfix 恢复。
    ///   - Finalizer=High (100)：让诊断 Finalizer (Normal=0) 先记录异常，然后功能 Finalizer 兜底恢复。
    ///
    /// 与 P0-D / P0-C1 的职责边界：
    ///   - v6.6 处理本地主机（IsLocalPlayer=true）切换 bound 的生命周期。
    ///   - P0-D 处理远端客机（IsLocalPlayer=false）进入 bound 的 generateZombies 补充。
    ///   - P0-C1 处理 updateRegionsAndSendZombieStates 的周期状态广播。
    ///   - v6.6 与 P0-D 玩家角色互斥（IsLocalPlayer=true vs false），同一调用中只会其中之一实际介入。
    ///   - v6.6 与 P0-C1 通过不同的原方法（onBoundUpdated vs updateRegionsAndSendZombieStates）隔离。
    /// </summary>
    public static class ZombieLifecyclePatch
    {
        private const string PointPrefix = "[P0-E-Zombie-v6.6]";

        // vanilla ZombieManager.onBoundUpdated private method 完整参数类型表
        // U3-SDK ZombieManager.cs:1448 private void onBoundUpdated(Player player, byte oldBound, byte newBound)
        private static readonly Type[] VanillaOnBoundUpdatedParamTypes =
        {
            typeof(Player),
            typeof(byte), typeof(byte)
        };

        public static bool PrefixRegistered { get; private set; }
        public static bool PostfixRegistered { get; private set; }
        public static bool FinalizerRegistered { get; private set; }
        public static bool AllRegistrationsSucceeded =>
            PrefixRegistered && PostfixRegistered && FinalizerRegistered;

        /// <summary>
        /// v0.2.3.39 v6.6 手动登记：identity-based 防重复登记。
        /// 三种 Patch 类型（Prefix/Postfix/Finalizer）分别登记。
        /// </summary>
        public static bool RegisterManual(Harmony harmony)
        {
            RoleLogger.Info("[Shared]", $"{PointPrefix} === 手动登记 3 个 hook（Prefix VeryLow + Postfix High + Finalizer High）===");

            var patchType = typeof(ZombieLifecyclePatch);

            bool r1, r2, r3;
            try
            {
                r1 = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(ZombieManager), "onBoundUpdated", VanillaOnBoundUpdatedParamTypes,
                    AccessTools.Method(patchType, "OnBoundUpdated_Prefix"),
                    HarmonyPatchType.Prefix, "P0-E-Zombie-v6.6.onBoundUpdated.Pre");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"{PointPrefix} onBoundUpdated.Pre 登记异常: {ex}");
                r1 = false;
            }

            try
            {
                r2 = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(ZombieManager), "onBoundUpdated", VanillaOnBoundUpdatedParamTypes,
                    AccessTools.Method(patchType, "OnBoundUpdated_Postfix"),
                    HarmonyPatchType.Postfix, "P0-E-Zombie-v6.6.onBoundUpdated.Post");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"{PointPrefix} onBoundUpdated.Post 登记异常: {ex}");
                r2 = false;
            }

            try
            {
                r3 = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(ZombieManager), "onBoundUpdated", VanillaOnBoundUpdatedParamTypes,
                    AccessTools.Method(patchType, "OnBoundUpdated_Finalizer"),
                    HarmonyPatchType.Finalizer, "P0-E-Zombie-v6.6.onBoundUpdated.Final");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"{PointPrefix} onBoundUpdated.Final 登记异常: {ex}");
                r3 = false;
            }

            PrefixRegistered = r1;
            PostfixRegistered = r2;
            FinalizerRegistered = r3;

            bool all = r1 && r2 && r3;
            RoleLogger.Info("[Shared]",
                $"{PointPrefix} RegisterManual 结果: Pre={r1} Post={r2} Final={r3} all={all}");
            return all;
        }

        public static bool VerifyRegistration()
        {
            try
            {
                var patchType = typeof(ZombieLifecyclePatch);
                MethodInfo pre = AccessTools.Method(patchType, "OnBoundUpdated_Prefix");
                MethodInfo post = AccessTools.Method(patchType, "OnBoundUpdated_Postfix");
                MethodInfo fin = AccessTools.Method(patchType, "OnBoundUpdated_Finalizer");

                PrefixRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(ZombieManager), "onBoundUpdated", pre, HarmonyPatchType.Prefix, VanillaOnBoundUpdatedParamTypes);
                PostfixRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(ZombieManager), "onBoundUpdated", post, HarmonyPatchType.Postfix, VanillaOnBoundUpdatedParamTypes);
                FinalizerRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(ZombieManager), "onBoundUpdated", fin, HarmonyPatchType.Finalizer, VanillaOnBoundUpdatedParamTypes);

                // v6.6 owner 自检：使用 ZombieLifecycleOwnerVerify 进行精确比较
                // sameOwnerOtherCount 仅信息输出，不作为失败条件
                bool ownerVerifyOk = ZombieLifecycleOwnerVerify.VerifyAllPatches(
                    AccessTools.Method(typeof(ZombieManager), "onBoundUpdated", VanillaOnBoundUpdatedParamTypes),
                    pre, post, fin);

                bool allOk = AllRegistrationsSucceeded && ownerVerifyOk;

                if (!allOk)
                {
                    RoleLogger.Error("[Shared]",
                        $"{PointPrefix} !!! 注册验证失败: Pre={PrefixRegistered} Post={PostfixRegistered} " +
                        $"Final={FinalizerRegistered} ownerVerify={ownerVerifyOk} " +
                        $"(owner={SteamP2PFriendsPlugin.HARMONY_ID}, identity-based)");
                    return false;
                }

                RoleLogger.Info("[Shared]",
                    $"{PointPrefix} OK 3 个 hook 已注册 (owner={SteamP2PFriendsPlugin.HARMONY_ID}, identity-based)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"{PointPrefix} VerifyRegistration 异常: {ex.Message}");
                PrefixRegistered = false;
                PostfixRegistered = false;
                FinalizerRegistered = false;
                return false;
            }
        }

        // ============= onBoundUpdated Prefix（v6.6 修订：两条独立路径） =============
        // U3-SDK ZombieManager.cs:1448 private void onBoundUpdated(Player player, byte oldBound, byte newBound)
        // [HarmonyPriority(Priority.VeryLow)] 确保在诊断 Prefix (Normal) + P0-D Prefix (Low) 之后执行
        [HarmonyPriority(Priority.VeryLow)]
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ZombieManager), "onBoundUpdated")]
        public static void OnBoundUpdated_Prefix(
            Player player, byte oldBound, byte newBound,
            ref ZombieLifecycleState __state)
        {
            __state = default(ZombieLifecycleState);

            // CommonGuard 是 old/new 共享的前置条件
            if (!CommonGuard(player)) return;

            // v6.6 关键修订：两条独立路径，互不阻断
            // old 失败不得阻止 new；new 失败不得撤销或阻止 old
            TryProtectOldBound(player, oldBound, ref __state);
            TryProcessNewBound(player, newBound, ref __state);
        }

        // ============= onBoundUpdated Postfix（乐观路径） =============
        [HarmonyPriority(Priority.High)]
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ZombieManager), "onBoundUpdated")]
        public static void OnBoundUpdated_Postfix(
            Player player, byte oldBound, byte newBound,
            ZombieLifecycleState __state)
        {
            // 乐观路径：原方法成功，Postfix 恢复 old isNetworked
            // 若 Postfix 自身异常，Finalizer 会兜底无条件恢复
            if (__state.oldWasModified)
            {
                try
                {
                    RestoreOldIsNetworked(ref __state);
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"{PointPrefix} Postfix 恢复 old isNetworked 异常: {ex.Message}");
                    // Postfix 异常不抛出，让 Finalizer 兜底
                }
            }
        }

        // ============= onBoundUpdated Finalizer（v6.6 修订：无条件恢复 old） =============
        [HarmonyPriority(Priority.High)]
        [HarmonyFinalizer]
        [HarmonyPatch(typeof(ZombieManager), "onBoundUpdated")]
        public static System.Exception OnBoundUpdated_Finalizer(
            Player player, byte oldBound, byte newBound,
            ZombieLifecycleState __state,
            System.Exception __exception)
        {
            // v6.6 关键修订：无条件执行 old-bound 幂等恢复
            // 理由：Postfix 恢复过程自身异常时，old Region 可能永久停留在 isNetworked=false
            if (__state.oldWasModified)
            {
                try
                {
                    RestoreOldIsNetworked(ref __state);
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"{PointPrefix} Finalizer 无条件恢复 old isNetworked 异常: {ex.Message}");
                    // 不吞异常：仅记录，继续返回原 __exception
                }
            }

            // new-bound 仅在原方法异常时回滚
            if (__exception != null && __state.newWasModified)
            {
                try
                {
                    RollbackNewIsZombiesLoaded(player, ref __state);
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"{PointPrefix} Finalizer 回滚 new loaded 异常: {ex.Message}");
                }
            }

            // 始终原样返回异常（不吞异常）
            return __exception;
        }

        // ============= 两条独立路径实现 =============

        /// <summary>
        /// TryProtectOldBound：当远端客机仍在 old bound 时，临时设置 regions[oldBound].isNetworked=false。
        /// 让 vanilla L1452 跳过 destroy。Postfix 与 Finalizer 无条件幂等恢复。
        /// </summary>
        private static void TryProtectOldBound(Player player, byte oldBound, ref ZombieLifecycleState __state)
        {
            try
            {
                // old-bound 守门（6 项）
                if (!OldBoundGuards(oldBound, out ZombieRegion oldRegion)) return;

                // 远端占用检查：是否仍有其他客机在该 bound
                if (!HasRemoteClientInBound(oldBound, player)) return;

                // 保存原值并临时写入 false
                __state.oldOriginalIsNetworked = oldRegion.isNetworked;
                __state.oldBound = oldBound;

                // 临时写入 false，让 vanilla L1452 跳过 destroy
                oldRegion.isNetworked = false;
                __state.oldWasModified = true;

                // 受限日志配额
                if (WorldSyncDiagnosticCore.TryAcquireQuota("P0-E-Zombie-v6.6-TryProtectOldBound", out _))
                {
                    RoleLogger.Info("[Host]",
                        $"{PointPrefix} TryProtectOldBound oldBound={oldBound} " +
                        $"originalIsNetworked={__state.oldOriginalIsNetworked} " +
                        $"(vanilla L1452 将跳过 destroy，Postfix/Finalizer 无条件恢复)");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"{PointPrefix} TryProtectOldBound 异常: {ex.Message}");
                // 异常不阻断 new-bound 处理
            }
        }

        /// <summary>
        /// TryProcessNewBound：当 newRegion.isNetworked=true（房主或 P0-D 已 generate 过）且
        /// newLoadedBound.isZombiesLoaded=false（本地主机首次进入该 bound）时，
        /// 临时设置 loadedBounds[newBound].isZombiesLoaded=true。让 vanilla L1475 跳过 generateZombies 分支。
        /// Finalizer 仅在异常时回滚。
        /// </summary>
        private static void TryProcessNewBound(Player player, byte newBound, ref ZombieLifecycleState __state)
        {
            try
            {
                // new-bound 守门（6 项）
                if (!NewBoundGuards(player, newBound, out ZombieRegion newRegion, out LoadedBound newLoadedBound)) return;

                // 仅当 newRegion.isNetworked=true（房主或 P0-D 已 generate 过）才介入
                if (!newRegion.isNetworked) return;

                // 仅当 newLoadedBound.isZombiesLoaded=false（本地主机首次进入该 bound）才介入
                if (newLoadedBound.isZombiesLoaded) return;

                // 保存原值并临时写入 true
                __state.newOriginalIsZombiesLoaded = newLoadedBound.isZombiesLoaded;
                __state.newBound = newBound;

                // 临时写入 true，让 vanilla L1475 跳过 generateZombies 分支
                newLoadedBound.isZombiesLoaded = true;
                __state.newWasModified = true;

                // 受限日志配额
                if (WorldSyncDiagnosticCore.TryAcquireQuota("P0-E-Zombie-v6.6-TryProcessNewBound", out _))
                {
                    RoleLogger.Info("[Host]",
                        $"{PointPrefix} TryProcessNewBound newBound={newBound} " +
                        $"originalIsZombiesLoaded={__state.newOriginalIsZombiesLoaded} " +
                        $"(vanilla L1475 将跳过 generateZombies，Finalizer 仅在异常时回滚)");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"{PointPrefix} TryProcessNewBound 异常: {ex.Message}");
                // 异常不阻断 old-bound 处理
            }
        }

        // ============= CommonGuard 与守门 =============

        /// <summary>
        /// CommonGuard 是 old/new 共享的前置条件（7 项 null 守门）。
        /// </summary>
        private static bool CommonGuard(Player player)
        {
            // 守门 1: player 非 null
            if (player == null) return false;

            // 守门 2: player.channel 非 null
            var channel = player.channel;
            if (channel == null) return false;

            // 守门 3: player.movement 非 null
            var movement = player.movement;
            if (movement == null) return false;

            // 守门 4: movement.loadedBounds 非 null
            var loadedBounds = movement.loadedBounds;
            if (loadedBounds == null) return false;

            // 守门 5: ZombieManager.regions 非 null
            if (ZombieManager.regions == null) return false;

            // 守门 6: HostManager.ShouldProcessClientHostListen()（统一 Listen Host 判定，含 !Dedicator.IsDedicatedServer）
            if (!HostManager.ShouldProcessClientHostListen()) return false;

            // 守门 7: channel.IsLocalPlayer（v6.6 只处理本地主机切换）
            if (!channel.IsLocalPlayer) return false;

            return true;
        }

        /// <summary>old-bound 守门（6 项）</summary>
        private static bool OldBoundGuards(byte oldBound, out ZombieRegion oldRegion)
        {
            oldRegion = null;
            // 守门 1: LevelNavigation.checkSafe(oldBound)
            if (!LevelNavigation.checkSafe(oldBound)) return false;
            // 守门 2: ZombieManager.regions 非 null
            var regions = ZombieManager.regions;
            if (regions == null) return false;
            // 守门 3: oldBound < regions.Length
            if (oldBound >= regions.Length) return false;
            // 守门 4: regions[oldBound] 非 null
            oldRegion = regions[oldBound];
            if (oldRegion == null) return false;
            // 守门 5: oldRegion.isNetworked（仅当已 networked 才需要介入）
            if (!oldRegion.isNetworked) return false;
            return true;
        }

        /// <summary>new-bound 守门（6 项）</summary>
        private static bool NewBoundGuards(Player player, byte newBound,
            out ZombieRegion newRegion, out LoadedBound newLoadedBound)
        {
            newRegion = null;
            newLoadedBound = null;
            // 守门 1: LevelNavigation.checkSafe(newBound)
            if (!LevelNavigation.checkSafe(newBound)) return false;
            // 守门 2: movement 非 null（CommonGuard 已验证，但独立守门仍检查）
            var movement = player.movement;
            if (movement == null) return false;
            // 守门 3: loadedBounds 非 null
            var loadedBounds = movement.loadedBounds;
            if (loadedBounds == null) return false;
            // 守门 4: newBound < loadedBounds.Length
            if (newBound >= loadedBounds.Length) return false;
            // 守门 5: ZombieManager.regions 非 null + newBound < regions.Length
            var regions = ZombieManager.regions;
            if (regions == null) return false;
            if (newBound >= regions.Length) return false;
            // 守门 6: regions[newBound] 非 null + loadedBounds[newBound] 非 null
            newRegion = regions[newBound];
            if (newRegion == null) return false;
            newLoadedBound = loadedBounds[newBound];
            if (newLoadedBound == null) return false;
            return true;
        }

        // ============= Provider.clients 有界快照检查 =============

        /// <summary>
        /// 远端占用检查：是否仍有其他客机在该 bound（排除当前正在切换 bound 的本地主机玩家）。
        /// Provider.clients 快照边界：Count 快照 + 索引有界遍历 + 单项异常捕获。
        /// </summary>
        private static bool HasRemoteClientInBound(byte bound, Player excludingPlayer)
        {
            try
            {
                var clients = Provider.clients;
                if (clients == null) return false;
                int count = clients.Count;
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        var client = clients[i];
                        if (client == null) continue;
                        // 排除当前正在切换 bound 的本地主机玩家
                        if (client.player == excludingPlayer) continue;
                        var clientPlayer = client.player;
                        if (clientPlayer == null) continue;
                        if (clientPlayer.movement == null) continue;
                        byte clientBound = clientPlayer.movement.bound;
                        if (clientBound == bound) return true;
                    }
                    catch
                    {
                        // 单项生命周期异常：跳过该项继续遍历
                        continue;
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        // ============= 恢复/回滚辅助 =============

        /// <summary>
        /// 幂等恢复 old isNetworked（Postfix 与 Finalizer 共用）。
        /// 多次调用安全：强制写回原值，不依赖当前值。
        /// </summary>
        private static void RestoreOldIsNetworked(ref ZombieLifecycleState __state)
        {
            var regions = ZombieManager.regions;
            if (regions == null) return;
            if (__state.oldBound >= regions.Length) return;
            var region = regions[__state.oldBound];
            if (region == null) return;
            region.isNetworked = __state.oldOriginalIsNetworked;
        }

        /// <summary>
        /// 幂等回滚 new isZombiesLoaded（仅 Finalizer 异常时调用）。
        /// </summary>
        private static void RollbackNewIsZombiesLoaded(Player player, ref ZombieLifecycleState __state)
        {
            var movement = player?.movement;
            if (movement == null) return;
            var loadedBounds = movement.loadedBounds;
            if (loadedBounds == null) return;
            if (__state.newBound >= loadedBounds.Length) return;
            var lb = loadedBounds[__state.newBound];
            if (lb == null) return;
            lb.isZombiesLoaded = __state.newOriginalIsZombiesLoaded;
        }
    }
}
