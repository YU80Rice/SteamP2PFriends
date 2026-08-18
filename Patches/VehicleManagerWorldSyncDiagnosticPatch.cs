using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using Steamworks;
using System.Reflection;
using UnityEngine;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// VehicleManager 世界同步链路五段证据诊断。
    ///
    /// 五段证据闭环：
    ///   1. 源事件：spawnVehicleInternal（载具生成）
    ///   2. 资格/目标：vehicles.Count + lastTick + UPDATE_TIME
    ///   3. 发送入口：sendVehicleStates()（vanilla private instance 方法）
    ///   4. 客机 Receive 入口：ReceiveMultipleVehicles（初始全量）+ ReceiveVehicleStates（周期状态）
    ///   5. Receive 后状态：客机 vehicles.Count
    ///
    ///     容忍 InitialStateReceiveDiagnosticPatch 在 ReceiveMultipleVehicles 上同 owner 共存）
    ///
    ///     RegisterManual 与 VerifyRegistration 共用
    ///
    /// vanilla 源码（U3-SDK VehicleManager.cs）：
    ///   - Update: L2918 `if (vehicles.Count > 0 && Dedicator.IsDedicatedServer && Time.realtimeSinceStartup - lastTick > Provider.UPDATE_TIME)`
    ///   - sendVehicleStates(): L2696 `private void sendVehicleStates()` (instance)
    ///   - spawnVehicleInternal: L378 `internal static InteractableVehicle spawnVehicleInternal(...)`
    ///   - ReceiveMultipleVehicles: L1164 (static)
    ///   - ReceiveVehicleStates: L782 (static, 对应 SendVehicleStates ClientStaticMethod 字段)
    ///   - lastTick: L79 `private static float lastTick`
    /// </summary>
    public static class VehicleManagerWorldSyncDiagnosticPatch
    {
        private const string PointPrefix = "[WorldSyncDiag/Vehicle]";
        private const float UpdateLogInterval = 5.0f;
        private static float _lastUpdateLogTime = -100f;

        // 由 RegisterManual 与 VerifyRegistration 共用。
        //   - Update() 无参数
        //   - sendVehicleStates() 无参数（private instance）
        //   - spawnVehicleInternal(Asset, Vector3, Quaternion, CSteamID, CSteamID, Color32?)
        //     Color32? 是 System.Nullable<Color32>
        //   - ReceiveMultipleVehicles(in ClientInvocationContext) - 与 InitialStateReceiveDiagnosticPatch 同 owner 共存
        //   - ReceiveVehicleStates(in ClientInvocationContext)
        private static readonly System.Type[] VanillaUpdateParamTypes = System.Type.EmptyTypes;
        private static readonly System.Type[] VanillaSendVehicleStatesParamTypes = System.Type.EmptyTypes;
        private static readonly System.Type[] VanillaSpawnVehicleInternalParamTypes =
        {
            typeof(Asset),
            typeof(Vector3),
            typeof(Quaternion),
            typeof(CSteamID),
            typeof(CSteamID),
            typeof(System.Nullable<Color32>)
        };
        private static readonly System.Type[] VanillaReceiveMultipleVehiclesParamTypes =
        {
            typeof(SDG.Unturned.ClientInvocationContext).MakeByRefType()
        };
        private static readonly System.Type[] VanillaReceiveVehicleStatesParamTypes =
        {
            typeof(SDG.Unturned.ClientInvocationContext).MakeByRefType()
        };

        static VehicleManagerWorldSyncDiagnosticPatch()
        {
            WorldSyncDiagnosticCore.RegisterSessionResetCallback(() => _lastUpdateLogTime = -100f);
        }

        public static bool UpdatePrefixRegistered { get; private set; }
        public static bool SendVehicleStatesPrefixRegistered { get; private set; }
        public static bool SpawnVehicleInternalPrefixRegistered { get; private set; }
        public static bool ReceiveMultipleVehiclesPrefixRegistered { get; private set; }
        public static bool ReceiveVehicleStatesPrefixRegistered { get; private set; }
        public static bool AllRegistrationsSucceeded =>
            UpdatePrefixRegistered && SendVehicleStatesPrefixRegistered
            && SpawnVehicleInternalPrefixRegistered && ReceiveMultipleVehiclesPrefixRegistered
            && ReceiveVehicleStatesPrefixRegistered;

        /// <summary>
        /// 5 个 hook 精确、幂等的 identity-based 手动登记。
        ///
        /// </summary>
        public static bool RegisterManual(Harmony harmony)
        {
            RoleLogger.Info("[Shared]", "[WorldSyncDiag/Vehicle] === 手动登记 5 个 hook（P0-R1～R8 identity-based 幂等）===");

            var patchType = typeof(VehicleManagerWorldSyncDiagnosticPatch);

            bool r1, r2, r3, r4, r5;
            try
            {
                r1 = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(VehicleManager), "Update", VanillaUpdateParamTypes,
                    AccessTools.Method(patchType, "Update_Prefix"),
                    HarmonyPatchType.Prefix, "Vehicle.Update.Pre");
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Vehicle] Update.Pre 登记异常: {ex}"); r1 = false; }

            try
            {
                r2 = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(VehicleManager), "sendVehicleStates", VanillaSendVehicleStatesParamTypes,
                    AccessTools.Method(patchType, "SendVehicleStates_Prefix"),
                    HarmonyPatchType.Prefix, "Vehicle.sendVehicleStates.Pre");
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Vehicle] sendVehicleStates.Pre 登记异常: {ex}"); r2 = false; }

            try
            {
                r3 = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(VehicleManager), "spawnVehicleInternal", VanillaSpawnVehicleInternalParamTypes,
                    AccessTools.Method(patchType, "SpawnVehicleInternal_Prefix"),
                    HarmonyPatchType.Prefix, "Vehicle.spawnVehicleInternal.Pre");
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Vehicle] spawnVehicleInternal.Pre 登记异常: {ex}"); r3 = false; }

            try
            {
                r4 = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(VehicleManager), "ReceiveMultipleVehicles", VanillaReceiveMultipleVehiclesParamTypes,
                    AccessTools.Method(patchType, "ReceiveMultipleVehicles_Prefix"),
                    HarmonyPatchType.Prefix, "Vehicle.ReceiveMultipleVehicles.Pre");
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Vehicle] ReceiveMultipleVehicles.Pre 登记异常: {ex}"); r4 = false; }

            try
            {
                r5 = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(VehicleManager), "ReceiveVehicleStates", VanillaReceiveVehicleStatesParamTypes,
                    AccessTools.Method(patchType, "ReceiveVehicleStates_Prefix"),
                    HarmonyPatchType.Prefix, "Vehicle.ReceiveVehicleStates.Pre");
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Vehicle] ReceiveVehicleStates.Pre 登记异常: {ex}"); r5 = false; }

            bool all = r1 && r2 && r3 && r4 && r5;
            RoleLogger.Info("[Shared]",
                $"[WorldSyncDiag/Vehicle] RegisterManual 结果: Update.Pre={r1} sendVehicleStates.Pre={r2} " +
                $"spawnVehicleInternal.Pre={r3} ReceiveMultipleVehicles.Pre={r4} ReceiveVehicleStates.Pre={r5} all={all}");
            return all;
        }

        /// <summary>
        /// 与 RegisterManual 使用同一套 Type[]。
        /// </summary>
        public static bool VerifyRegistration()
        {
            try
            {
                var patchType = typeof(VehicleManagerWorldSyncDiagnosticPatch);

                MethodInfo updatePre = AccessTools.Method(patchType, "Update_Prefix");
                MethodInfo sendVehicleStatesPre = AccessTools.Method(patchType, "SendVehicleStates_Prefix");
                MethodInfo spawnVehicleInternalPre = AccessTools.Method(patchType, "SpawnVehicleInternal_Prefix");
                MethodInfo receiveMultipleVehiclesPre = AccessTools.Method(patchType, "ReceiveMultipleVehicles_Prefix");
                MethodInfo receiveVehicleStatesPre = AccessTools.Method(patchType, "ReceiveVehicleStates_Prefix");

                UpdatePrefixRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(VehicleManager), "Update", updatePre, HarmonyPatchType.Prefix, VanillaUpdateParamTypes);
                SendVehicleStatesPrefixRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(VehicleManager), "sendVehicleStates", sendVehicleStatesPre, HarmonyPatchType.Prefix, VanillaSendVehicleStatesParamTypes);
                SpawnVehicleInternalPrefixRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(VehicleManager), "spawnVehicleInternal", spawnVehicleInternalPre, HarmonyPatchType.Prefix, VanillaSpawnVehicleInternalParamTypes);
                // InitialStateReceiveDiagnosticPatch.Prefix，旧版 CountPatches 会得到 2，
                // 按 expected=1 直接 FAIL。改为 identity-based 后只要我们自己的 MethodInfo
                // 在 patches 列表中即视为注册成功。
                ReceiveMultipleVehiclesPrefixRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(VehicleManager), "ReceiveMultipleVehicles", receiveMultipleVehiclesPre, HarmonyPatchType.Prefix, VanillaReceiveMultipleVehiclesParamTypes);
                ReceiveVehicleStatesPrefixRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(VehicleManager), "ReceiveVehicleStates", receiveVehicleStatesPre, HarmonyPatchType.Prefix, VanillaReceiveVehicleStatesParamTypes);

                if (!AllRegistrationsSucceeded)
                {
                    RoleLogger.Error("[Shared]",
                        $"[WorldSyncDiag/Vehicle] !!! 注册验证失败: " +
                        $"Update.Pre={UpdatePrefixRegistered} " +
                        $"sendVehicleStates.Pre={SendVehicleStatesPrefixRegistered} " +
                        $"spawnVehicleInternal.Pre={SpawnVehicleInternalPrefixRegistered} " +
                        $"ReceiveMultipleVehicles.Pre={ReceiveMultipleVehiclesPrefixRegistered} " +
                        $"ReceiveVehicleStates.Pre={ReceiveVehicleStatesPrefixRegistered} " +
                        $"(owner={SteamP2PFriendsPlugin.HARMONY_ID}, identity-based, 共用 VanillaXxxParamTypes)");
                    return false;
                }

                RoleLogger.Info("[Shared]",
                    $"[WorldSyncDiag/Vehicle] OK 5 个 hook 均已注册 (owner={SteamP2PFriendsPlugin.HARMONY_ID}, identity-based, 共用 VanillaXxxParamTypes)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Vehicle] VerifyRegistration 异常: {ex.Message}");
                UpdatePrefixRegistered = SendVehicleStatesPrefixRegistered = false;
                SpawnVehicleInternalPrefixRegistered = ReceiveMultipleVehiclesPrefixRegistered = false;
                ReceiveVehicleStatesPrefixRegistered = false;
                return false;
            }
        }

        // ============= 1. 资格/目标：Update 条件评估 =============
        [HarmonyPrefix]
        [HarmonyPatch(typeof(VehicleManager), "Update")]
        public static void Update_Prefix()
        {
            try
            {
                if (VehicleManager.vehicles == null || VehicleManager.vehicles.Count == 0) return;

                float now = Time.realtimeSinceStartup;
                if (now - _lastUpdateLogTime < UpdateLogInterval) return;
                _lastUpdateLogTime = now;

                if (!WorldSyncDiagnosticCore.TryAcquireQuota("Vehicle.Update", out int count))
                {
                    return;
                }

                bool isDedicated = Dedicator.IsDedicatedServer;
                float lastTick = ReadLastTick();
                float timeSinceLastTick = now - lastTick;
                bool wouldSend = isDedicated && timeSinceLastTick > Provider.UPDATE_TIME;

                RoleLogger.Info("[Host]",
                    $"{PointPrefix} Update #{count}/{WorldSyncDiagnosticCore.PerPointLimit} " +
                    $"vehicles={VehicleManager.vehicles.Count} isDedicated={isDedicated} " +
                    $"timeSinceLastTick={timeSinceLastTick:F2}s UPDATE_TIME={Provider.UPDATE_TIME}s " +
                    $"wouldSendVehicleStates={wouldSend} " +
                    $"(vanilla: sendVehicleStates 仅在 isDedicated=true 时调用)");
            }
            catch (System.Exception ex)
            {
                try { RoleLogger.Error("[Shared]", $"{PointPrefix} Update Prefix 异常: {ex.Message}"); } catch { }
            }
        }

        // ============= 2. 发送入口：sendVehicleStates() =============
        [HarmonyPrefix]
        [HarmonyPatch(typeof(VehicleManager), "sendVehicleStates")]
        public static void SendVehicleStates_Prefix()
        {
            try
            {
                if (!WorldSyncDiagnosticCore.TryAcquireQuota("Vehicle.sendVehicleStates", out int count))
                {
                    return;
                }

                int vehicleCount = VehicleManager.vehicles?.Count ?? 0;
                RoleLogger.Info("[Host]",
                    $"{PointPrefix} sendVehicleStates #{count}/{WorldSyncDiagnosticCore.PerPointLimit} " +
                    $"vehicles={vehicleCount} isDedicated={Dedicator.IsDedicatedServer} " +
                    $"(真实发送入口已调用 - 周期载具状态包)");
            }
            catch (System.Exception ex)
            {
                try { RoleLogger.Error("[Shared]", $"{PointPrefix} sendVehicleStates Prefix 异常: {ex.Message}"); } catch { }
            }
        }

        // ============= 3. 源事件：spawnVehicleInternal =============
        [HarmonyPrefix]
        [HarmonyPatch(typeof(VehicleManager), "spawnVehicleInternal")]
        public static void SpawnVehicleInternal_Prefix(
            Asset asset,
            Vector3 point,
            Quaternion angle,
            CSteamID owner,
            CSteamID groupId,
            Color32? preferredColor)
        {
            try
            {
                string assetGuid = asset?.GUID.ToString() ?? "null";
                string assetName = asset?.FriendlyName ?? "null";
                string maskedOwner = WorldSyncDiagnosticCore.MaskSteamId(owner);
                string maskedGroup = WorldSyncDiagnosticCore.MaskSteamId(groupId);

                if (!WorldSyncDiagnosticCore.TryAcquireQuota("Vehicle.spawnVehicleInternal", out int count))
                {
                    return;
                }

                RoleLogger.Info("[Host]",
                    $"{PointPrefix} spawnVehicleInternal #{count}/{WorldSyncDiagnosticCore.PerPointLimit} " +
                    $"asset={assetName}({assetGuid}) " +
                    $"point=({point.x:F1},{point.y:F1},{point.z:F1}) " +
                    $"owner={maskedOwner} group={maskedGroup} " +
                    $"isDedicated={Dedicator.IsDedicatedServer}");
            }
            catch (System.Exception ex)
            {
                try { RoleLogger.Error("[Shared]", $"{PointPrefix} spawnVehicleInternal Prefix 异常: {ex.Message}"); } catch { }
            }
        }

        // ============= 4. 客机 Receive 入口：ReceiveMultipleVehicles（初始全量） =============
        [HarmonyPrefix]
        [HarmonyPatch(typeof(VehicleManager), "ReceiveMultipleVehicles")]
        public static void ReceiveMultipleVehicles_Prefix()
        {
            try
            {
                if (!WorldSyncDiagnosticCore.TryAcquireQuota("Vehicle.ReceiveMultipleVehicles", out int count))
                {
                    return;
                }

                RoleLogger.Info("[Client]",
                    $"{PointPrefix} ReceiveMultipleVehicles #{count}/{WorldSyncDiagnosticCore.PerPointLimit} " +
                    $"(初始全量载具包 / 运行时增量载具包)");
            }
            catch (System.Exception ex)
            {
                try { RoleLogger.Error("[Shared]", $"{PointPrefix} ReceiveMultipleVehicles Prefix 异常: {ex.Message}"); } catch { }
            }
        }

        // ============= 5. 客机 Receive 入口：ReceiveVehicleStates（周期状态） =============
        [HarmonyPrefix]
        [HarmonyPatch(typeof(VehicleManager), "ReceiveVehicleStates")]
        public static void ReceiveVehicleStates_Prefix()
        {
            try
            {
                if (!WorldSyncDiagnosticCore.TryAcquireQuota("Vehicle.ReceiveVehicleStates", out int count))
                {
                    return;
                }

                int vehicleCount = VehicleManager.vehicles?.Count ?? 0;
                RoleLogger.Info("[Client]",
                    $"{PointPrefix} ReceiveVehicleStates #{count}/{WorldSyncDiagnosticCore.PerPointLimit} " +
                    $"vehicles={vehicleCount} " +
                    $"(周期载具状态包 - 客机收到)");
            }
            catch (System.Exception ex)
            {
                try { RoleLogger.Error("[Shared]", $"{PointPrefix} ReceiveVehicleStates Prefix 异常: {ex.Message}"); } catch { }
            }
        }

        // ============= 安全读取辅助 =============

        private static float ReadLastTick()
        {
            try
            {
                var field = typeof(VehicleManager).GetField("lastTick",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (field != null)
                {
                    return (float)field.GetValue(null);
                }
            }
            catch { }
            return 0f;
        }
    }
}
