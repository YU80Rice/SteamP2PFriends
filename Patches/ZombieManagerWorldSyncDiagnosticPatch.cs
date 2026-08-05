using HarmonyLib;
using SDG.NetPak;
using SDG.NetTransport;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System;
using System.Reflection;
using UnityEngine;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.27 P0-A 决定性诊断（Codex 第 7 节 P0-A + 静态审计返修）：
    /// ZombieManager 世界同步链路五段证据诊断。
    ///
    /// v0.2.3.29 返修（Codex 第十八次审计 §5.1 僵尸诊断返修授权）：
    ///   1. OnBoundUpdated 日志先判断 Provider.isServer，客机输出 skip(not server)。
    ///   2. 使用真实的一维 ZombieManager.regions[bound] 安全读取精确 count
    ///      （vanilla L35: public static ZombieRegion[] regions => _regions; 1D 索引）。
    ///   3. 未知值必须输出 unknown，禁止参与 delta 算术（int? null 语义）。
    ///   4. 新增 SendZombies_Write(NetPakWriter, byte) 只读探针，记录真正写入包的 bound
    ///      和精确 regions[bound].zombies.Count（vanilla L674-695，count=region.zombies.Count L679）。
    ///   5. ReceiveZombies 若只能统计全局总数，日志明确 totalCount（不能声称是该 bound 精确落地数）。
    ///
    /// 五段证据闭环：
    ///   1. 源事件：onBoundUpdated（玩家进入新 bound）
    ///   2. 资格/目标：LevelNavigation.checkSafe(newBound) + loadedBounds[newBound].isZombiesLoaded
    ///      + region.zombies.Count before
    ///   3. 发送入口：SendZombiesToPlayer(ITransportConnection, byte) + snapshot count
    ///   4. 客机 Receive 入口：ReceiveZombies（初始 bound 包）+ ReceiveZombieStates（周期状态）
    ///   5. Receive 后状态：客机 loadedBounds / zombies count
    ///
    /// vanilla 源码（U3-SDK ZombieManager.cs）：
    ///   - updateRegionsAndSendZombieStates: L1653（SendZombieStates.Invoke L1665，条件 L1662 IsDedicatedServer）
    ///   - onBoundUpdated: L1448 `private void onBoundUpdated(Player player, byte oldBound, byte newBound)`
    ///     - L1473 `if (LevelNavigation.checkSafe(newBound))`
    ///     - L1475 `if (!player.movement.loadedBounds[newBound].isZombiesLoaded)`
    ///     - L1477 `if (player.channel.IsLocalPlayer)` -> L1479 `generateZombies(newBound);`
    ///     - 否则 L1485 `SendZombiesToPlayer(player.channel.owner.transportConnection, newBound);`
    ///     - L1488 `player.movement.loadedBounds[newBound].isZombiesLoaded = true;`
    ///     - 外层门控：L1471 `if (Provider.isServer)`（v0.2.3.29 返修：必须纳入 vanillaBranch 判定）
    ///   - SendZombiesToPlayer: L669 `private void SendZombiesToPlayer(ITransportConnection transportConnection, byte bound)`
    ///   - SendZombies_Write: L674 `private static void SendZombies_Write(NetPakWriter writer, byte bound)`，
    ///     count = `region.zombies.Count`（L679）
    ///   - ReceiveZombies: L617（静态，in ClientInvocationContext）
    ///   - ReceiveZombieStates: L241（静态，对应 SendZombieStates ClientStaticMethod 字段）
    /// </summary>
    public static class ZombieManagerWorldSyncDiagnosticPatch
    {
        private const string PointPrefix = "[WorldSyncDiag/Zombie]";
        private const string LoopbackTransportFullName = "SDG.NetTransport.Loopback.TransportConnection_Loopback";
        private const float UpdateLogInterval = 5.0f;
        private static float _lastUpdateLogTime = -100f;

        // v0.2.3.27-P0-A 冒烟中止返修（Codex P0-R8）：vanilla 目标完整参数类型表
        // v0.2.3.29 返修：新增 SendZombies_Write 参数类型表
        private static readonly System.Type[] VanillaUpdateRegionsParamTypes = System.Type.EmptyTypes;
        private static readonly System.Type[] VanillaOnBoundUpdatedParamTypes =
        {
            typeof(Player),
            typeof(byte), typeof(byte)
        };
        private static readonly System.Type[] VanillaSendZombiesToPlayerParamTypes =
        {
            typeof(SDG.NetTransport.ITransportConnection),
            typeof(byte)
        };
        private static readonly System.Type[] VanillaSendZombiesWriteParamTypes =
        {
            typeof(NetPakWriter),
            typeof(byte)
        };
        private static readonly System.Type[] VanillaReceiveZombiesParamTypes =
        {
            typeof(SDG.Unturned.ClientInvocationContext).MakeByRefType()
        };
        private static readonly System.Type[] VanillaReceiveZombieStatesParamTypes =
        {
            typeof(SDG.Unturned.ClientInvocationContext).MakeByRefType()
        };

        static ZombieManagerWorldSyncDiagnosticPatch()
        {
            WorldSyncDiagnosticCore.RegisterSessionResetCallback(() => _lastUpdateLogTime = -100f);
        }

        public static bool UpdateRegionsPrefixRegistered { get; private set; }
        public static bool OnBoundUpdatedPrefixRegistered { get; private set; }
        public static bool SendZombiesToPlayerPrefixRegistered { get; private set; }
        public static bool SendZombiesWritePrefixRegistered { get; private set; }
        public static bool ReceiveZombiesPrefixRegistered { get; private set; }
        public static bool ReceiveZombiesPostfixRegistered { get; private set; }
        public static bool ReceiveZombieStatesPrefixRegistered { get; private set; }
        public static bool AllRegistrationsSucceeded =>
            UpdateRegionsPrefixRegistered && OnBoundUpdatedPrefixRegistered
            && SendZombiesToPlayerPrefixRegistered && SendZombiesWritePrefixRegistered
            && ReceiveZombiesPrefixRegistered && ReceiveZombiesPostfixRegistered
            && ReceiveZombieStatesPrefixRegistered;

        /// <summary>
        /// v0.2.3.27-P0-A 手动登记（Codex 外部审计裁决 P0-R1～R8）：
        /// v0.2.3.29 返修：7 个 hook（新增 SendZombies_Write Prefix）。
        /// </summary>
        public static bool RegisterManual(Harmony harmony)
        {
            RoleLogger.Info("[Shared]", "[WorldSyncDiag/Zombie] === 手动登记 7 个 hook（v0.2.3.29 返修：含 SendZombies_Write）===");

            var patchType = typeof(ZombieManagerWorldSyncDiagnosticPatch);

            bool r1, r2, r3, r3b, r4, r5, r6;
            try
            {
                r1 = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(ZombieManager), "updateRegionsAndSendZombieStates", VanillaUpdateRegionsParamTypes,
                    AccessTools.Method(patchType, "UpdateRegionsAndSendZombieStates_Prefix"),
                    HarmonyPatchType.Prefix, "Zombie.updateRegionsAndSendZombieStates.Pre");
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Zombie] updateRegionsAndSendZombieStates.Pre 登记异常: {ex}"); r1 = false; }

            try
            {
                r2 = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(ZombieManager), "onBoundUpdated", VanillaOnBoundUpdatedParamTypes,
                    AccessTools.Method(patchType, "OnBoundUpdated_Prefix"),
                    HarmonyPatchType.Prefix, "Zombie.onBoundUpdated.Pre");
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Zombie] onBoundUpdated.Pre 登记异常: {ex}"); r2 = false; }

            try
            {
                r3 = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(ZombieManager), "SendZombiesToPlayer", VanillaSendZombiesToPlayerParamTypes,
                    AccessTools.Method(patchType, "SendZombiesToPlayer_Prefix"),
                    HarmonyPatchType.Prefix, "Zombie.SendZombiesToPlayer.Pre");
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Zombie] SendZombiesToPlayer.Pre 登记异常: {ex}"); r3 = false; }

            // v0.2.3.29 新增：SendZombies_Write Prefix 探针（vanilla L674 private static void SendZombies_Write(NetPakWriter, byte)）
            try
            {
                r3b = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(ZombieManager), "SendZombies_Write", VanillaSendZombiesWriteParamTypes,
                    AccessTools.Method(patchType, "SendZombies_Write_Prefix"),
                    HarmonyPatchType.Prefix, "Zombie.SendZombies_Write.Pre");
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Zombie] SendZombies_Write.Pre 登记异常: {ex}"); r3b = false; }

            try
            {
                r4 = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(ZombieManager), "ReceiveZombies", VanillaReceiveZombiesParamTypes,
                    AccessTools.Method(patchType, "ReceiveZombies_Prefix"),
                    HarmonyPatchType.Prefix, "Zombie.ReceiveZombies.Pre");
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Zombie] ReceiveZombies.Pre 登记异常: {ex}"); r4 = false; }

            try
            {
                r5 = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(ZombieManager), "ReceiveZombies", VanillaReceiveZombiesParamTypes,
                    AccessTools.Method(patchType, "ReceiveZombies_Postfix"),
                    HarmonyPatchType.Postfix, "Zombie.ReceiveZombies.Post");
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Zombie] ReceiveZombies.Post 登记异常: {ex}"); r5 = false; }

            try
            {
                r6 = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(ZombieManager), "ReceiveZombieStates", VanillaReceiveZombieStatesParamTypes,
                    AccessTools.Method(patchType, "ReceiveZombieStates_Prefix"),
                    HarmonyPatchType.Prefix, "Zombie.ReceiveZombieStates.Pre");
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Zombie] ReceiveZombieStates.Pre 登记异常: {ex}"); r6 = false; }

            bool all = r1 && r2 && r3 && r3b && r4 && r5 && r6;
            RoleLogger.Info("[Shared]",
                $"[WorldSyncDiag/Zombie] RegisterManual 结果: updateRegionsAndSendZombieStates.Pre={r1} onBoundUpdated.Pre={r2} " +
                $"SendZombiesToPlayer.Pre={r3} SendZombies_Write.Pre={r3b} " +
                $"ReceiveZombies.Pre={r4} ReceiveZombies.Post={r5} ReceiveZombieStates.Pre={r6} all={all}");
            return all;
        }

        /// <summary>
        /// v0.2.3.27-P0-A 冒烟中止返修（Codex P0-R8）：复用类级别 VanillaXxxParamTypes 完整参数表。
        /// v0.2.3.29 返修：新增 SendZombies_Write VerifyRegistration。
        /// </summary>
        public static bool VerifyRegistration()
        {
            try
            {
                var patchType = typeof(ZombieManagerWorldSyncDiagnosticPatch);

                MethodInfo updateRegionsPre = AccessTools.Method(patchType, "UpdateRegionsAndSendZombieStates_Prefix");
                MethodInfo onBoundUpdatedPre = AccessTools.Method(patchType, "OnBoundUpdated_Prefix");
                MethodInfo sendZombiesToPlayerPre = AccessTools.Method(patchType, "SendZombiesToPlayer_Prefix");
                MethodInfo sendZombiesWritePre = AccessTools.Method(patchType, "SendZombies_Write_Prefix");
                MethodInfo receiveZombiesPre = AccessTools.Method(patchType, "ReceiveZombies_Prefix");
                MethodInfo receiveZombiesPost = AccessTools.Method(patchType, "ReceiveZombies_Postfix");
                MethodInfo receiveZombieStatesPre = AccessTools.Method(patchType, "ReceiveZombieStates_Prefix");

                UpdateRegionsPrefixRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(ZombieManager), "updateRegionsAndSendZombieStates", updateRegionsPre, HarmonyPatchType.Prefix, VanillaUpdateRegionsParamTypes);
                OnBoundUpdatedPrefixRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(ZombieManager), "onBoundUpdated", onBoundUpdatedPre, HarmonyPatchType.Prefix, VanillaOnBoundUpdatedParamTypes);
                SendZombiesToPlayerPrefixRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(ZombieManager), "SendZombiesToPlayer", sendZombiesToPlayerPre, HarmonyPatchType.Prefix, VanillaSendZombiesToPlayerParamTypes);
                SendZombiesWritePrefixRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(ZombieManager), "SendZombies_Write", sendZombiesWritePre, HarmonyPatchType.Prefix, VanillaSendZombiesWriteParamTypes);
                ReceiveZombiesPrefixRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(ZombieManager), "ReceiveZombies", receiveZombiesPre, HarmonyPatchType.Prefix, VanillaReceiveZombiesParamTypes);
                ReceiveZombiesPostfixRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(ZombieManager), "ReceiveZombies", receiveZombiesPost, HarmonyPatchType.Postfix, VanillaReceiveZombiesParamTypes);
                ReceiveZombieStatesPrefixRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(ZombieManager), "ReceiveZombieStates", receiveZombieStatesPre, HarmonyPatchType.Prefix, VanillaReceiveZombieStatesParamTypes);

                if (!AllRegistrationsSucceeded)
                {
                    RoleLogger.Error("[Shared]",
                        $"[WorldSyncDiag/Zombie] !!! 注册验证失败: " +
                        $"updateRegions.Pre={UpdateRegionsPrefixRegistered} " +
                        $"onBoundUpdated.Pre={OnBoundUpdatedPrefixRegistered} " +
                        $"SendZombiesToPlayer.Pre={SendZombiesToPlayerPrefixRegistered} " +
                        $"SendZombies_Write.Pre={SendZombiesWritePrefixRegistered} " +
                        $"ReceiveZombies.Pre={ReceiveZombiesPrefixRegistered} " +
                        $"ReceiveZombies.Post={ReceiveZombiesPostfixRegistered} " +
                        $"ReceiveZombieStates.Pre={ReceiveZombieStatesPrefixRegistered} " +
                        $"(owner={SteamP2PFriendsPlugin.HARMONY_ID}, identity-based, 共用 VanillaXxxParamTypes)");
                    return false;
                }

                RoleLogger.Info("[Shared]",
                    $"[WorldSyncDiag/Zombie] OK 7 个 hook 均已注册 (owner={SteamP2PFriendsPlugin.HARMONY_ID}, identity-based, 共用 VanillaXxxParamTypes)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Zombie] VerifyRegistration 异常: {ex.Message}");
                UpdateRegionsPrefixRegistered = OnBoundUpdatedPrefixRegistered = false;
                SendZombiesToPlayerPrefixRegistered = SendZombiesWritePrefixRegistered = false;
                ReceiveZombiesPrefixRegistered = ReceiveZombiesPostfixRegistered = false;
                ReceiveZombieStatesPrefixRegistered = false;
                return false;
            }
        }

        // ============= 1. 发送入口：updateRegionsAndSendZombieStates（周期） =============
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ZombieManager), "updateRegionsAndSendZombieStates")]
        public static void UpdateRegionsAndSendZombieStates_Prefix()
        {
            try
            {
                float now = Time.realtimeSinceStartup;
                if (now - _lastUpdateLogTime < UpdateLogInterval) return;
                _lastUpdateLogTime = now;

                if (!WorldSyncDiagnosticCore.TryAcquireQuota("Zombie.updateRegionsAndSendZombieStates", out int count))
                {
                    return;
                }

                bool isDedicated = Dedicator.IsDedicatedServer;
                int? zombieCount = ReadZombieCountTotal();
                string countStr = zombieCount.HasValue ? zombieCount.Value.ToString() : "unknown";

                RoleLogger.Info("[Host]",
                    $"{PointPrefix} updateRegionsAndSendZombieStates #{count}/{WorldSyncDiagnosticCore.PerPointLimit} " +
                    $"isDedicated={isDedicated} totalZombies={countStr} " +
                    $"wouldSendZombieStates={isDedicated} " +
                    $"(vanilla: SendZombieStates 仅在 isDedicated=true 时调用；listen host 仍清 isUpdated 但不发送)");
            }
            catch (System.Exception ex)
            {
                try { RoleLogger.Error("[Shared]", $"{PointPrefix} updateRegions Prefix 异常: {ex.Message}"); } catch { }
            }
        }

        // ============= 2. 源事件：onBoundUpdated（玩家进入新 bound） =============
        // v0.2.3.29 返修：先判断 Provider.isServer，客机输出 skip(not server)
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ZombieManager), "onBoundUpdated")]
        public static void OnBoundUpdated_Prefix(Player player, byte oldBound, byte newBound)
        {
            try
            {
                ulong steamId = 0UL;
                try { steamId = player?.channel?.owner?.playerID?.steamID.m_SteamID ?? 0UL; } catch { }
                string maskedId = WorldSyncDiagnosticCore.MaskSteamId(steamId);

                bool isLocalPlayer = false;
                try { isLocalPlayer = player?.channel?.IsLocalPlayer ?? false; } catch { }

                bool isDedicated = Dedicator.IsDedicatedServer;

                // v0.2.3.29 返修（Codex §5.1.1）：外层 Provider.isServer 门控
                // U3-SDK ZombieManager.onBoundUpdated L1471: if (Provider.isServer) { ... }
                // 客机进程 Provider.isServer=false，真实分支应为 skip(not server)，而非 generateZombies。
                if (!Provider.isServer)
                {
                    if (!WorldSyncDiagnosticCore.TryAcquirePlayerQuota(steamId, "Zombie.onBoundUpdated",
                        WorldSyncDiagnosticCore.PerPlayerPointLimit, out int countSkip))
                    {
                        return;
                    }

                    RoleLogger.Info("[Host]",
                        $"{PointPrefix} onBoundUpdated #{countSkip}/{WorldSyncDiagnosticCore.PerPlayerPointLimit} " +
                        $"player={maskedId} oldBound={oldBound} newBound={newBound} " +
                        $"isLocalPlayer={isLocalPlayer} isDedicated={isDedicated} " +
                        $"isServer=False vanillaBranch=skip(not server) " +
                        $"(客机进程不进入 generate/send 分支)");
                    return;
                }

                bool safeBound = ReadLevelNavigationCheckSafe(newBound);
                bool isZombiesLoadedBefore = ReadLoadedBoundIsZombiesLoaded(player, newBound);
                int? zombieCountBeforeOpt = ReadZombieCountInBound(newBound);
                string zombieCountBefore = zombieCountBeforeOpt.HasValue ? zombieCountBeforeOpt.Value.ToString() : "unknown";

                // vanilla 完整门控：isServer && safe && !loadedBefore -> (IsLocalPlayer ? generateZombies : SendZombiesToPlayer)
                bool willEnterBlock = safeBound && !isZombiesLoadedBefore;
                string vanillaBranch = !willEnterBlock
                    ? "skip(safe=false or alreadyLoaded)"
                    : (isLocalPlayer ? "generateZombies" : "SendZombiesToPlayer");

                if (!WorldSyncDiagnosticCore.TryAcquirePlayerQuota(steamId, "Zombie.onBoundUpdated",
                    WorldSyncDiagnosticCore.PerPlayerPointLimit, out int count))
                {
                    return;
                }

                RoleLogger.Info("[Host]",
                    $"{PointPrefix} onBoundUpdated #{count}/{WorldSyncDiagnosticCore.PerPlayerPointLimit} " +
                    $"player={maskedId} oldBound={oldBound} newBound={newBound} " +
                    $"isLocalPlayer={isLocalPlayer} isDedicated={isDedicated} " +
                    $"safeBound={safeBound} isZombiesLoaded_before={isZombiesLoadedBefore} " +
                    $"zombieCount_before={zombieCountBefore} " +
                    $"vanillaBranch={vanillaBranch} " +
                    $"(vanilla: isServer && safe && !loaded -> IsLocalPlayer?generateZombies:SendZombiesToPlayer)");
            }
            catch (System.Exception ex)
            {
                try { RoleLogger.Error("[Shared]", $"{PointPrefix} onBoundUpdated Prefix 异常: {ex.Message}"); } catch { }
            }
        }

        // ============= 3. 发送入口：SendZombiesToPlayer(ITransportConnection, byte) =============
        // v0.2.3.29 返修：使用 int? count，未知值输出 unknown
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ZombieManager), "SendZombiesToPlayer")]
        public static void SendZombiesToPlayer_Prefix(ITransportConnection transportConnection, byte bound)
        {
            try
            {
                string transportType = transportConnection?.GetType().FullName ?? "null";
                bool isLoopback = string.Equals(transportType, LoopbackTransportFullName, StringComparison.Ordinal);
                int? zombieCountOpt = ReadZombieCountInBound(bound);
                string zombieCount = zombieCountOpt.HasValue ? zombieCountOpt.Value.ToString() : "unknown";

                if (!WorldSyncDiagnosticCore.TryAcquireQuota("Zombie.SendZombiesToPlayer", out int count))
                {
                    return;
                }

                RoleLogger.Info("[Host]",
                    $"{PointPrefix} SendZombiesToPlayer #{count}/{WorldSyncDiagnosticCore.PerPointLimit} " +
                    $"transport={transportType} isLoopback={isLoopback} bound={bound} " +
                    $"zombieCount_in_bound={zombieCount} " +
                    $"isDedicated={Dedicator.IsDedicatedServer} " +
                    $"(vanilla: 发送 bound 区域僵尸快照，count=0 时客机看到空城镇)");
            }
            catch (System.Exception ex)
            {
                try { RoleLogger.Error("[Shared]", $"{PointPrefix} SendZombiesToPlayer Prefix 异常: {ex.Message}"); } catch { }
            }
        }

        // ============= 3b. 发送写入：SendZombies_Write(NetPakWriter, byte) =============
        // v0.2.3.29 新增（Codex §5.1.3）：只读探针，记录真正写入包的 bound 和精确 regions[bound].zombies.Count
        // vanilla L674-695: private static void SendZombies_Write(NetPakWriter writer, byte bound)
        //   L676: ZombieRegion region = regions[bound];
        //   L679: count = region.zombies.Count
        // 该方法由 ClientStaticMethod.Invoke 内部对每个目标连接调用一次，
        // 故本 Prefix 触发次数 = 实际写入次数 = 真实发送目标数。
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ZombieManager), "SendZombies_Write")]
        public static void SendZombies_Write_Prefix(byte bound)
        {
            try
            {
                int? zombieCountOpt = ReadZombieCountInBound(bound);
                string zombieCount = zombieCountOpt.HasValue ? zombieCountOpt.Value.ToString() : "unknown";

                if (!WorldSyncDiagnosticCore.TryAcquireQuota("Zombie.SendZombies_Write", out int count))
                {
                    return;
                }

                RoleLogger.Info("[Host]",
                    $"{PointPrefix} SendZombies_Write #{count}/{WorldSyncDiagnosticCore.PerPointLimit} " +
                    $"bound={bound} regionZombies={zombieCount} " +
                    $"(vanilla L679: region.zombies.Count -> 实际写入包的僵尸数)");
            }
            catch (System.Exception ex)
            {
                try { RoleLogger.Error("[Shared]", $"{PointPrefix} SendZombies_Write Prefix 异常: {ex.Message}"); } catch { }
            }
        }

        // ============= 4. 客机 Receive 入口：ReceiveZombies（初始 bound 包） =============
        // v0.2.3.29 返修：使用 int? count，未知值输出 unknown，禁止 delta 算术
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ZombieManager), "ReceiveZombies")]
        public static void ReceiveZombies_Prefix(ref int? __state)
        {
            __state = null;
            try
            {
                __state = ReadZombieCountTotal();

                if (!WorldSyncDiagnosticCore.TryAcquireQuota("Zombie.ReceiveZombies", out int count))
                {
                    return;
                }

                string beforeStr = __state.HasValue ? __state.Value.ToString() : "unknown";
                RoleLogger.Info("[Client]",
                    $"{PointPrefix} ReceiveZombies #{count}/{WorldSyncDiagnosticCore.PerPointLimit} " +
                    $"totalZombies_before={beforeStr} " +
                    $"(初始 bound 僵尸快照 - 客机收到；totalCount 为全局总数，非该 bound 精确落地数)");
            }
            catch (System.Exception ex)
            {
                try { RoleLogger.Error("[Shared]", $"{PointPrefix} ReceiveZombies Prefix 异常: {ex.Message}"); } catch { }
            }
        }

        // v0.2.3.29 返修：Postfix 使用 int? count，未知值输出 unknown，delta 禁止算术
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ZombieManager), "ReceiveZombies")]
        public static void ReceiveZombies_Postfix(int? __state)
        {
            try
            {
                int? countAfterOpt = ReadZombieCountTotal();

                if (!WorldSyncDiagnosticCore.TryAcquireQuota("Zombie.ReceiveZombies.Postfix", out int count))
                {
                    return;
                }

                string beforeStr = __state.HasValue ? __state.Value.ToString() : "unknown";
                string afterStr = countAfterOpt.HasValue ? countAfterOpt.Value.ToString() : "unknown";
                // v0.2.3.29 返修（Codex §5.1.2）：未知值禁止参与 delta 算术
                string deltaStr = (__state.HasValue && countAfterOpt.HasValue)
                    ? (countAfterOpt.Value - __state.Value).ToString()
                    : "unknown";

                RoleLogger.Info("[Client]",
                    $"{PointPrefix} ReceiveZombies.Postfix #{count}/{WorldSyncDiagnosticCore.PerPointLimit} " +
                    $"totalZombies_before={beforeStr} totalZombies_after={afterStr} " +
                    $"delta={deltaStr} " +
                    $"(落地结果: totalCount 为全局总数，delta>0 表示客机全局新增僵尸)");
            }
            catch (System.Exception ex)
            {
                try { RoleLogger.Error("[Shared]", $"{PointPrefix} ReceiveZombies Postfix 异常: {ex.Message}"); } catch { }
            }
        }

        // ============= 5. 客机 Receive 入口：ReceiveZombieStates（周期状态） =============
        // v0.2.3.29 返修：使用 int? count，未知值输出 unknown
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ZombieManager), "ReceiveZombieStates")]
        public static void ReceiveZombieStates_Prefix()
        {
            try
            {
                if (!WorldSyncDiagnosticCore.TryAcquireQuota("Zombie.ReceiveZombieStates", out int count))
                {
                    return;
                }

                int? zombieCountOpt = ReadZombieCountTotal();
                string zombieCount = zombieCountOpt.HasValue ? zombieCountOpt.Value.ToString() : "unknown";
                RoleLogger.Info("[Client]",
                    $"{PointPrefix} ReceiveZombieStates #{count}/{WorldSyncDiagnosticCore.PerPointLimit} " +
                    $"totalZombies={zombieCount} " +
                    $"(周期僵尸状态包 - 客机收到；totalCount 为全局总数)");
            }
            catch (System.Exception ex)
            {
                try { RoleLogger.Error("[Shared]", $"{PointPrefix} ReceiveZombieStates Prefix 异常: {ex.Message}"); } catch { }
            }
        }

        // ============= 安全读取辅助 =============

        private static bool ReadLevelNavigationCheckSafe(byte bound)
        {
            try { return LevelNavigation.checkSafe(bound); }
            catch { return false; }
        }

        /// <summary>
        /// 读取 player.movement.loadedBounds[bound].isZombiesLoaded。
        /// </summary>
        private static bool ReadLoadedBoundIsZombiesLoaded(Player player, byte bound)
        {
            try
            {
                if (player == null) return false;
                var movement = player.movement;
                if (movement == null) return false;

                var field = typeof(PlayerMovement).GetField("_loadedBounds",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (field == null) return false;

                var loadedBounds = field.GetValue(movement) as LoadedBound[];
                if (loadedBounds == null) return false;
                if (bound < 0 || bound >= loadedBounds.Length) return false;

                var lb = loadedBounds[bound];
                if (lb == null) return false;
                return lb.isZombiesLoaded;
            }
            catch { return false; }
        }

        /// <summary>
        /// v0.2.3.29 返修（Codex §5.1.2）：使用真实一维 ZombieManager.regions[bound] 安全读取精确 count。
        /// vanilla L35: public static ZombieRegion[] regions => _regions;
        /// vanilla L676: ZombieRegion region = regions[bound];
        /// vanilla L679: count = region.zombies.Count
        ///
        /// 返回 int?：null 表示未知（regions 字段缺失/null/索引越界/zombies 字段缺失），
        /// 调用方必须输出 "unknown"，禁止参与 delta 算术。
        /// </summary>
        private static int? ReadZombieCountInBound(byte bound)
        {
            try
            {
                // 优先读 public static 属性 regions（vanilla L35）
                var regionsProp = typeof(ZombieManager).GetProperty("regions",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (regionsProp == null)
                {
                    // 回退读 private static 字段 _regions（vanilla L34）
                    var regionsField = typeof(ZombieManager).GetField("_regions",
                        BindingFlags.NonPublic | BindingFlags.Static);
                    if (regionsField == null) return null;
                    var regionsObj = regionsField.GetValue(null) as ZombieRegion[];
                    return CountZombiesInRegionArray(regionsObj, bound);
                }

                var regions = regionsProp.GetValue(null, null) as ZombieRegion[];
                return CountZombiesInRegionArray(regions, bound);
            }
            catch
            {
                return null;
            }
        }

        private static int? CountZombiesInRegionArray(ZombieRegion[] regions, byte bound)
        {
            if (regions == null) return null;
            if (bound >= regions.Length) return null;

            var region = regions[bound];
            if (region == null) return null;

            var zombies = region.zombies;
            if (zombies == null) return null;

            return zombies.Count;
        }

        /// <summary>
        /// v0.2.3.29 返修：使用真实一维 ZombieManager.regions 迭代读取全局总数。
        /// 返回 int?：null 表示未知，调用方必须输出 "unknown"。
        /// 注意：totalCount 为全局总数，非特定 bound 精确落地数。
        /// </summary>
        private static int? ReadZombieCountTotal()
        {
            try
            {
                var regionsProp = typeof(ZombieManager).GetProperty("regions",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                ZombieRegion[] regions;
                if (regionsProp == null)
                {
                    var regionsField = typeof(ZombieManager).GetField("_regions",
                        BindingFlags.NonPublic | BindingFlags.Static);
                    if (regionsField == null) return null;
                    regions = regionsField.GetValue(null) as ZombieRegion[];
                }
                else
                {
                    regions = regionsProp.GetValue(null, null) as ZombieRegion[];
                }

                if (regions == null) return null;

                int total = 0;
                for (int i = 0; i < regions.Length; i++)
                {
                    var region = regions[i];
                    if (region == null) continue;
                    var zombies = region.zombies;
                    if (zombies == null) continue;
                    total += zombies.Count;
                }
                return total;
            }
            catch
            {
                return null;
            }
        }
    }
}
