using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.27 P0-A 决定性诊断（Codex 第 7 节 P0-A + 静态审计返修）：
    /// ObjectManager 世界同步链路五段证据诊断。
    ///
    /// 五段证据闭环：
    ///   1. 源事件：onRegionUpdated step=4
    ///   2. 资格/目标：player.movement.loadedRegions[x,y].isObjectsLoaded + Regions.checkSafe
    ///   3. 发送入口：askObjects(ITransportConnection, byte, byte) 实际调用
    ///   4. 客机 Receive 入口：ReceiveObjects
    ///   5. Receive 后状态/拒绝门控：ObjectManager.regions[x,y].isNetworked
    ///
    /// v0.2.3.27-P0-A 返修：
    ///   - P0-2：新增 VerifyRegistration（identity-based，owner + MethodInfo 双重验证）
    ///   - P0-3：补齐 askObjects Prefix（真实发送入口）
    ///   - P0-4：改读 player.movement.loadedRegions[x,y].isObjectsLoaded
    ///   - P1-1：onRegionUpdated 使用 TryAcquirePlayerQuota
    ///
    /// vanilla 源码（U3-SDK ObjectManager.cs）：
    ///   - onRegionUpdated: L955（step 4 askObjects 调用点 L1021）
    ///   - askObjects internal: L792 `internal void askObjects(ITransportConnection, byte, byte)`
    ///   - ReceiveObjects: L740（静态，in ClientInvocationContext）
    /// </summary>
    public static class ObjectManagerWorldSyncDiagnosticPatch
    {
        private const string PointPrefix = "[WorldSyncDiag/Object]";
        private const string LoopbackTransportFullName = "SDG.NetTransport.Loopback.TransportConnection_Loopback";

        // v0.2.3.27-P0-A 冒烟中止返修（Codex P0-R8）：vanilla 目标完整参数类型表，
        // 由 RegisterManual 与 VerifyRegistration 共用。
        //   - onRegionUpdated(Player, byte, byte, byte, byte, byte, ref bool)
        //   - askObjects(ITransportConnection, byte, byte) - internal 重载，与 public askObjects(CSteamID, byte, byte) 区分
        //   - ReceiveObjects(in ClientInvocationContext)
        private static readonly System.Type[] VanillaOnRegionUpdatedParamTypes =
        {
            typeof(Player),
            typeof(byte), typeof(byte),
            typeof(byte), typeof(byte),
            typeof(byte),
            typeof(bool).MakeByRefType()
        };
        private static readonly System.Type[] VanillaAskObjectsParamTypes =
        {
            typeof(SDG.NetTransport.ITransportConnection),
            typeof(byte), typeof(byte)
        };
        private static readonly System.Type[] VanillaReceiveObjectsParamTypes =
        {
            typeof(SDG.Unturned.ClientInvocationContext).MakeByRefType()
        };

        public static bool OnRegionUpdatedPrefixRegistered { get; private set; }
        public static bool AskObjectsPrefixRegistered { get; private set; }
        public static bool ReceiveObjectsPrefixRegistered { get; private set; }
        public static bool ReceiveObjectsPostfixRegistered { get; private set; }
        public static bool AllRegistrationsSucceeded =>
            OnRegionUpdatedPrefixRegistered && AskObjectsPrefixRegistered
            && ReceiveObjectsPrefixRegistered && ReceiveObjectsPostfixRegistered;

        /// <summary>
        /// v0.2.3.27-P0-A 手动登记（Codex 外部审计裁决 P0-R1～R8）：
        /// 4 个 hook 精确、幂等的 identity-based 手动登记。
        ///
        /// P0-R1：所有 vanilla 目标使用完整参数类型解析（类级别 VanillaXxxParamTypes）。
        /// P0-R2：identity-based 幂等。
        /// P0-R3：每个 hook 独立 try/catch。
        /// P0-R4：ReceiveObjects 的 Prefix 和 Postfix 分别精确登记、分别核验。
        /// P0-R7：登记前预检查静默。
        /// P0-R8：RegisterManual 与 VerifyRegistration 共用 VanillaXxxParamTypes。
        /// </summary>
        public static bool RegisterManual(Harmony harmony)
        {
            RoleLogger.Info("[Shared]", "[WorldSyncDiag/Object] === 手动登记 4 个 hook（P0-R1～R8 identity-based 幂等）===");

            var patchType = typeof(ObjectManagerWorldSyncDiagnosticPatch);

            bool r1, r2, r3, r4;
            try
            {
                r1 = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(ObjectManager), "onRegionUpdated", VanillaOnRegionUpdatedParamTypes,
                    AccessTools.Method(patchType, "OnRegionUpdated_Prefix"),
                    HarmonyPatchType.Prefix, "Object.onRegionUpdated.Pre");
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Object] onRegionUpdated.Pre 登记异常: {ex}"); r1 = false; }

            try
            {
                r2 = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(ObjectManager), "askObjects", VanillaAskObjectsParamTypes,
                    AccessTools.Method(patchType, "AskObjects_Prefix"),
                    HarmonyPatchType.Prefix, "Object.askObjects.Pre");
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Object] askObjects.Pre 登记异常: {ex}"); r2 = false; }

            try
            {
                r3 = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(ObjectManager), "ReceiveObjects", VanillaReceiveObjectsParamTypes,
                    AccessTools.Method(patchType, "ReceiveObjects_Prefix"),
                    HarmonyPatchType.Prefix, "Object.ReceiveObjects.Pre");
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Object] ReceiveObjects.Pre 登记异常: {ex}"); r3 = false; }

            try
            {
                r4 = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(ObjectManager), "ReceiveObjects", VanillaReceiveObjectsParamTypes,
                    AccessTools.Method(patchType, "ReceiveObjects_Postfix"),
                    HarmonyPatchType.Postfix, "Object.ReceiveObjects.Post");
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Object] ReceiveObjects.Post 登记异常: {ex}"); r4 = false; }

            bool all = r1 && r2 && r3 && r4;
            RoleLogger.Info("[Shared]",
                $"[WorldSyncDiag/Object] RegisterManual 结果: onRegionUpdated.Pre={r1} askObjects.Pre={r2} " +
                $"ReceiveObjects.Pre={r3} ReceiveObjects.Post={r4} all={all}");
            return all;
        }

        /// <summary>
        /// v0.2.3.27-P0-A 冒烟中止返修（Codex P0-R8）：复用类级别 VanillaXxxParamTypes 完整参数表。
        /// </summary>
        public static bool VerifyRegistration()
        {
            try
            {
                var patchType = typeof(ObjectManagerWorldSyncDiagnosticPatch);

                MethodInfo onRegionUpdatedPre = AccessTools.Method(patchType, "OnRegionUpdated_Prefix");
                MethodInfo askObjectsPre = AccessTools.Method(patchType, "AskObjects_Prefix");
                MethodInfo receiveObjectsPre = AccessTools.Method(patchType, "ReceiveObjects_Prefix");
                MethodInfo receiveObjectsPost = AccessTools.Method(patchType, "ReceiveObjects_Postfix");

                OnRegionUpdatedPrefixRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(ObjectManager), "onRegionUpdated", onRegionUpdatedPre, HarmonyPatchType.Prefix, VanillaOnRegionUpdatedParamTypes);
                AskObjectsPrefixRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(ObjectManager), "askObjects", askObjectsPre, HarmonyPatchType.Prefix, VanillaAskObjectsParamTypes);
                ReceiveObjectsPrefixRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(ObjectManager), "ReceiveObjects", receiveObjectsPre, HarmonyPatchType.Prefix, VanillaReceiveObjectsParamTypes);
                ReceiveObjectsPostfixRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(ObjectManager), "ReceiveObjects", receiveObjectsPost, HarmonyPatchType.Postfix, VanillaReceiveObjectsParamTypes);

                if (!AllRegistrationsSucceeded)
                {
                    RoleLogger.Error("[Shared]",
                        $"[WorldSyncDiag/Object] !!! 注册验证失败: " +
                        $"onRegionUpdated.Pre={OnRegionUpdatedPrefixRegistered} " +
                        $"askObjects.Pre={AskObjectsPrefixRegistered} " +
                        $"ReceiveObjects.Pre={ReceiveObjectsPrefixRegistered} " +
                        $"ReceiveObjects.Post={ReceiveObjectsPostfixRegistered} " +
                        $"(owner={SteamP2PFriendsPlugin.HARMONY_ID}, identity-based, 共用 VanillaXxxParamTypes)");
                    return false;
                }

                RoleLogger.Info("[Shared]",
                    $"[WorldSyncDiag/Object] OK 4 个 hook 均已注册 (owner={SteamP2PFriendsPlugin.HARMONY_ID}, identity-based, 共用 VanillaXxxParamTypes)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Object] VerifyRegistration 异常: {ex.Message}");
                OnRegionUpdatedPrefixRegistered = AskObjectsPrefixRegistered = false;
                ReceiveObjectsPrefixRegistered = ReceiveObjectsPostfixRegistered = false;
                return false;
            }
        }

        // ============= 1. 源事件：onRegionUpdated step=4 =============
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ObjectManager), "onRegionUpdated")]
        public static void OnRegionUpdated_Prefix(
            Player player,
            byte old_x, byte old_y,
            byte new_x, byte new_y,
            byte step,
            ref bool canIncrementIndex)
        {
            try
            {
                if (step != 4) return;

                ulong steamId = 0UL;
                try { steamId = player?.channel?.owner?.playerID?.steamID.m_SteamID ?? 0UL; } catch { }
                string maskedId = WorldSyncDiagnosticCore.MaskSteamId(steamId);

                bool isDedicated = Dedicator.IsDedicatedServer;
                string isObjectsLoadedStr = ReadRegionObjectsLoaded(player, new_x, new_y);
                bool checkSafe = ReadRegionsCheckSafe(new_x, new_y);

                if (!WorldSyncDiagnosticCore.TryAcquirePlayerQuota(steamId, "Object.onRegionUpdated.step4",
                    WorldSyncDiagnosticCore.PerPlayerPointLimit, out int count))
                {
                    return;
                }

                RoleLogger.Info("[Host]",
                    $"{PointPrefix} onRegionUpdated #{count}/{WorldSyncDiagnosticCore.PerPlayerPointLimit} " +
                    $"step=4 player={maskedId} region=({new_x},{new_y}) " +
                    $"isDedicated={isDedicated} checkSafe={checkSafe} " +
                    $"isObjectsLoaded={isObjectsLoadedStr} " +
                    $"(vanilla: askObjects 仅在 isDedicated=true 时调用)");
            }
            catch (System.Exception ex)
            {
                try { RoleLogger.Error("[Shared]", $"{PointPrefix} onRegionUpdated Prefix 异常: {ex.Message}"); } catch { }
            }
        }

        // ============= 2. 发送入口：askObjects(ITransportConnection, byte, byte) =============
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ObjectManager), "askObjects",
            new[] { typeof(SDG.NetTransport.ITransportConnection), typeof(byte), typeof(byte) })]
        public static void AskObjects_Prefix(
            SDG.NetTransport.ITransportConnection transportConnection,
            byte x, byte y)
        {
            try
            {
                string transportType = transportConnection?.GetType().FullName ?? "null";
                bool isLoopback = string.Equals(transportType, LoopbackTransportFullName, System.StringComparison.Ordinal);

                if (!WorldSyncDiagnosticCore.TryAcquireQuota("Object.askObjects", out int count))
                {
                    return;
                }

                RoleLogger.Info("[Host]",
                    $"{PointPrefix} askObjects #{count}/{WorldSyncDiagnosticCore.PerPointLimit} " +
                    $"region=({x},{y}) transport={transportType} isLoopback={isLoopback} " +
                    $"isDedicated={Dedicator.IsDedicatedServer} " +
                    $"(真实发送入口已调用)");
            }
            catch (System.Exception ex)
            {
                try { RoleLogger.Error("[Shared]", $"{PointPrefix} askObjects Prefix 异常: {ex.Message}"); } catch { }
            }
        }

        // ============= 3. 客机 Receive 入口：ReceiveObjects =============
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ObjectManager), "ReceiveObjects")]
        public static void ReceiveObjects_Prefix()
        {
            try
            {
                if (!WorldSyncDiagnosticCore.TryAcquireQuota("Object.ReceiveObjects", out int count))
                {
                    return;
                }

                RoleLogger.Info("[Client]",
                    $"{PointPrefix} ReceiveObjects #{count}/{WorldSyncDiagnosticCore.PerPointLimit} " +
                    $"(初始区域物件包 - 客机收到)");
            }
            catch (System.Exception ex)
            {
                try { RoleLogger.Error("[Shared]", $"{PointPrefix} ReceiveObjects Prefix 异常: {ex.Message}"); } catch { }
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ObjectManager), "ReceiveObjects")]
        public static void ReceiveObjects_Postfix()
        {
            try
            {
                if (!WorldSyncDiagnosticCore.TryAcquireQuota("Object.ReceiveObjects.Postfix", out int count))
                {
                    return;
                }

                int networkedCount = CountNetworkedObjectRegions();

                RoleLogger.Info("[Client]",
                    $"{PointPrefix} ReceiveObjects.Postfix #{count}/{WorldSyncDiagnosticCore.PerPointLimit} " +
                    $"totalNetworkedRegions={networkedCount} " +
                    $"(客机已 networked 的物件区域总数)");
            }
            catch (System.Exception ex)
            {
                try { RoleLogger.Error("[Shared]", $"{PointPrefix} ReceiveObjects Postfix 异常: {ex.Message}"); } catch { }
            }
        }

        /// <summary>
        /// 反射读取 ObjectManager.regions 字段并统计 isNetworked=true 的区域数。
        /// ObjectManager.regions 是 internal/private static，无法直接访问。
        /// </summary>
        private static int CountNetworkedObjectRegions()
        {
            try
            {
                var field = typeof(ObjectManager).GetField("regions",
                    BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
                if (field == null) return -1;
                var regions = field.GetValue(null) as Array;
                if (regions == null) return -1;

                int total = 0;
                int len0 = regions.GetLength(0);
                int len1 = regions.GetLength(1);
                for (int i = 0; i < len0; i++)
                {
                    for (int j = 0; j < len1; j++)
                    {
                        var region = regions.GetValue(i, j);
                        if (region == null) continue;
                        var isNetworkedField = region.GetType().GetField("isNetworked",
                            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                        if (isNetworkedField != null && (bool)isNetworkedField.GetValue(region))
                        {
                            total++;
                        }
                    }
                }
                return total;
            }
            catch { return -1; }
        }

        // ============= 安全读取辅助 =============

        private static string ReadRegionObjectsLoaded(Player player, byte x, byte y)
        {
            try
            {
                if (player == null) return "unknown(player=null)";
                var movement = player.movement;
                if (movement == null) return "unknown(movement=null)";

                var field = typeof(PlayerMovement).GetField("_loadedRegions",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (field == null) return "unknown(_loadedRegions field not found)";

                var loadedRegions = field.GetValue(movement) as LoadedRegion[,];
                if (loadedRegions == null) return "unknown(loadedRegions=null)";

                if (x < 0 || x >= loadedRegions.GetLength(0) || y < 0 || y >= loadedRegions.GetLength(1))
                    return $"unknown(out_of_range x={x} y={y})";

                var region = loadedRegions[x, y];
                if (region == null) return "unknown(region=null)";

                return region.isObjectsLoaded.ToString().ToLowerInvariant();
            }
            catch (System.Exception ex)
            {
                return $"unknown(read-failed: {ex.GetType().Name})";
            }
        }

        private static bool ReadRegionsCheckSafe(byte x, byte y)
        {
            try { return Regions.checkSafe((int)x, (int)y); }
            catch { return false; }
        }
    }
}
