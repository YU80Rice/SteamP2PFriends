using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Reflection;
using UnityEngine;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.27 P0-A 决定性诊断（Codex 第 7 节 P0-A + 静态审计返修）：
    /// AnimalManager 世界同步链路五段证据诊断。
    ///
    /// 五段证据闭环：
    ///   1. 源事件：spawnAnimal
    ///   2. 资格/目标：animals.Count + lastTick + UPDATE_TIME
    ///   3. 发送入口：sendAnimalStates()（vanilla private instance 方法）
    ///   4. 客机 Receive 入口：ReceiveMultipleAnimals（初始全量）+ ReceiveAnimalStates（周期状态）
    ///   5. Receive 后状态：客机 animals.Count
    ///
    /// v0.2.3.27-P0-A 返修：
    ///   - P0-2：新增 VerifyRegistration（identity-based，owner + MethodInfo 双重验证）
    ///   - P0-3：补齐 sendAnimalStates Prefix + ReceiveAnimalStates Prefix
    ///   - P1-2：ReceiveAnimalStates 与 ReceiveMultipleAnimals 分开记录
    ///   - P1-5：_lastUpdateLogTime 通过 RegisterSessionResetCallback 纳入 ResetAll
    ///
    /// v0.2.3.27-P0-A 第二轮返修（Codex 第三轮 GO 复审 TC-S2 阻断修正）：
    ///   - SpawnAnimal_Prefix 签名修正：vanilla `spawnAnimal(ushort id, Vector3, Quaternion)`，
    ///     首参是 ushort id 而非 AnimalAsset asset。删除 asset.GUID/FriendlyName 读取，改为只记录 id。
    ///
    /// v0.2.3.27-P0-A 冒烟中止返修（Codex P0-R7/P0-R8）：
    ///   - P0-R7：IsPatchRegistered 新增 logWhenMissing，登记前预检查静默
    ///   - P0-R8：5 个 vanilla 目标完整参数表移到类级别 VanillaXxxParamTypes，
    ///     RegisterManual 与 VerifyRegistration 共用
    ///
    /// vanilla 源码（U3-SDK AnimalManager.cs）：
    ///   - Update: L1057 `if (animals.Count > 0 && Dedicator.IsDedicatedServer && Time.realtimeSinceStartup - lastTick > Provider.UPDATE_TIME)`
    ///   - sendAnimalStates(): L901 `private void sendAnimalStates()` (instance)
    ///   - spawnAnimal: vanilla 内部
    ///   - ReceiveMultipleAnimals: L368 (static)
    ///   - ReceiveAnimalStates: L274 (static, 对应 SendAnimalStates ClientStaticMethod 字段)
    ///   - lastTick: L131 `private static float lastTick`
    /// </summary>
    public static class AnimalManagerWorldSyncDiagnosticPatch
    {
        private const string PointPrefix = "[WorldSyncDiag/Animal]";
        private const float UpdateLogInterval = 5.0f;
        private static float _lastUpdateLogTime = -100f;

        // v0.2.3.27-P0-A 冒烟中止返修（Codex P0-R8）：vanilla 目标完整参数类型表，
        // 由 RegisterManual 与 VerifyRegistration 共用。
        //   - Update() 无参数
        //   - sendAnimalStates() 无参数（private instance）
        //   - spawnAnimal(ushort, Vector3, Quaternion)
        //   - ReceiveMultipleAnimals(in ClientInvocationContext)
        //   - ReceiveAnimalStates(in ClientInvocationContext)
        private static readonly System.Type[] VanillaUpdateParamTypes = System.Type.EmptyTypes;
        private static readonly System.Type[] VanillaSendAnimalStatesParamTypes = System.Type.EmptyTypes;
        private static readonly System.Type[] VanillaSpawnAnimalParamTypes =
        {
            typeof(ushort),
            typeof(Vector3),
            typeof(Quaternion)
        };
        private static readonly System.Type[] VanillaReceiveMultipleAnimalsParamTypes =
        {
            typeof(SDG.Unturned.ClientInvocationContext).MakeByRefType()
        };
        private static readonly System.Type[] VanillaReceiveAnimalStatesParamTypes =
        {
            typeof(SDG.Unturned.ClientInvocationContext).MakeByRefType()
        };

        static AnimalManagerWorldSyncDiagnosticPatch()
        {
            WorldSyncDiagnosticCore.RegisterSessionResetCallback(() => _lastUpdateLogTime = -100f);
        }

        public static bool UpdatePrefixRegistered { get; private set; }
        public static bool SendAnimalStatesPrefixRegistered { get; private set; }
        public static bool SpawnAnimalPrefixRegistered { get; private set; }
        public static bool ReceiveMultipleAnimalsPrefixRegistered { get; private set; }
        public static bool ReceiveAnimalStatesPrefixRegistered { get; private set; }
        public static bool AllRegistrationsSucceeded =>
            UpdatePrefixRegistered && SendAnimalStatesPrefixRegistered
            && SpawnAnimalPrefixRegistered && ReceiveMultipleAnimalsPrefixRegistered
            && ReceiveAnimalStatesPrefixRegistered;

        /// <summary>
        /// v0.2.3.27-P0-A 手动登记（Codex 外部审计裁决 P0-R1～R8）：
        /// 5 个 hook 精确、幂等的 identity-based 手动登记。
        ///
        /// P0-R1：所有 vanilla 目标使用完整参数类型解析（类级别 VanillaXxxParamTypes）。
        /// P0-R2：identity-based 幂等。
        /// P0-R3：每个 hook 独立 try/catch。
        /// P0-R7：登记前预检查静默（logWhenMissing=false）。
        /// P0-R8：RegisterManual 与 VerifyRegistration 共用类级别 VanillaXxxParamTypes。
        /// </summary>
        public static bool RegisterManual(Harmony harmony)
        {
            RoleLogger.Info("[Shared]", "[WorldSyncDiag/Animal] === 手动登记 5 个 hook（P0-R1～R8 identity-based 幂等）===");

            var patchType = typeof(AnimalManagerWorldSyncDiagnosticPatch);

            bool r1, r2, r3, r4, r5;
            try
            {
                r1 = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(AnimalManager), "Update", VanillaUpdateParamTypes,
                    AccessTools.Method(patchType, "Update_Prefix"),
                    HarmonyPatchType.Prefix, "Animal.Update.Pre");
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Animal] Update.Pre 登记异常: {ex}"); r1 = false; }

            try
            {
                r2 = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(AnimalManager), "sendAnimalStates", VanillaSendAnimalStatesParamTypes,
                    AccessTools.Method(patchType, "SendAnimalStates_Prefix"),
                    HarmonyPatchType.Prefix, "Animal.sendAnimalStates.Pre");
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Animal] sendAnimalStates.Pre 登记异常: {ex}"); r2 = false; }

            try
            {
                r3 = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(AnimalManager), "spawnAnimal", VanillaSpawnAnimalParamTypes,
                    AccessTools.Method(patchType, "SpawnAnimal_Prefix"),
                    HarmonyPatchType.Prefix, "Animal.spawnAnimal.Pre");
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Animal] spawnAnimal.Pre 登记异常: {ex}"); r3 = false; }

            try
            {
                r4 = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(AnimalManager), "ReceiveMultipleAnimals", VanillaReceiveMultipleAnimalsParamTypes,
                    AccessTools.Method(patchType, "ReceiveMultipleAnimals_Prefix"),
                    HarmonyPatchType.Prefix, "Animal.ReceiveMultipleAnimals.Pre");
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Animal] ReceiveMultipleAnimals.Pre 登记异常: {ex}"); r4 = false; }

            try
            {
                r5 = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(AnimalManager), "ReceiveAnimalStates", VanillaReceiveAnimalStatesParamTypes,
                    AccessTools.Method(patchType, "ReceiveAnimalStates_Prefix"),
                    HarmonyPatchType.Prefix, "Animal.ReceiveAnimalStates.Pre");
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Animal] ReceiveAnimalStates.Pre 登记异常: {ex}"); r5 = false; }

            bool all = r1 && r2 && r3 && r4 && r5;
            RoleLogger.Info("[Shared]",
                $"[WorldSyncDiag/Animal] RegisterManual 结果: Update.Pre={r1} sendAnimalStates.Pre={r2} " +
                $"spawnAnimal.Pre={r3} ReceiveMultipleAnimals.Pre={r4} ReceiveAnimalStates.Pre={r5} all={all}");
            return all;
        }

        /// <summary>
        /// v0.2.3.27-P0-A 冒烟中止返修（Codex P0-R8）：复用类级别 VanillaXxxParamTypes 完整参数表，
        /// 与 RegisterManual 使用同一套 Type[]。
        /// </summary>
        public static bool VerifyRegistration()
        {
            try
            {
                var patchType = typeof(AnimalManagerWorldSyncDiagnosticPatch);

                MethodInfo updatePre = AccessTools.Method(patchType, "Update_Prefix");
                MethodInfo sendAnimalStatesPre = AccessTools.Method(patchType, "SendAnimalStates_Prefix");
                MethodInfo spawnAnimalPre = AccessTools.Method(patchType, "SpawnAnimal_Prefix");
                MethodInfo receiveMultipleAnimalsPre = AccessTools.Method(patchType, "ReceiveMultipleAnimals_Prefix");
                MethodInfo receiveAnimalStatesPre = AccessTools.Method(patchType, "ReceiveAnimalStates_Prefix");

                UpdatePrefixRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(AnimalManager), "Update", updatePre, HarmonyPatchType.Prefix, VanillaUpdateParamTypes);
                SendAnimalStatesPrefixRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(AnimalManager), "sendAnimalStates", sendAnimalStatesPre, HarmonyPatchType.Prefix, VanillaSendAnimalStatesParamTypes);
                SpawnAnimalPrefixRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(AnimalManager), "spawnAnimal", spawnAnimalPre, HarmonyPatchType.Prefix, VanillaSpawnAnimalParamTypes);
                ReceiveMultipleAnimalsPrefixRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(AnimalManager), "ReceiveMultipleAnimals", receiveMultipleAnimalsPre, HarmonyPatchType.Prefix, VanillaReceiveMultipleAnimalsParamTypes);
                ReceiveAnimalStatesPrefixRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(AnimalManager), "ReceiveAnimalStates", receiveAnimalStatesPre, HarmonyPatchType.Prefix, VanillaReceiveAnimalStatesParamTypes);

                if (!AllRegistrationsSucceeded)
                {
                    RoleLogger.Error("[Shared]",
                        $"[WorldSyncDiag/Animal] !!! 注册验证失败: " +
                        $"Update.Pre={UpdatePrefixRegistered} " +
                        $"sendAnimalStates.Pre={SendAnimalStatesPrefixRegistered} " +
                        $"spawnAnimal.Pre={SpawnAnimalPrefixRegistered} " +
                        $"ReceiveMultipleAnimals.Pre={ReceiveMultipleAnimalsPrefixRegistered} " +
                        $"ReceiveAnimalStates.Pre={ReceiveAnimalStatesPrefixRegistered} " +
                        $"(owner={SteamP2PFriendsPlugin.HARMONY_ID}, identity-based, 共用 VanillaXxxParamTypes)");
                    return false;
                }

                RoleLogger.Info("[Shared]",
                    $"[WorldSyncDiag/Animal] OK 5 个 hook 均已注册 (owner={SteamP2PFriendsPlugin.HARMONY_ID}, identity-based, 共用 VanillaXxxParamTypes)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Animal] VerifyRegistration 异常: {ex.Message}");
                UpdatePrefixRegistered = SendAnimalStatesPrefixRegistered = false;
                SpawnAnimalPrefixRegistered = ReceiveMultipleAnimalsPrefixRegistered = false;
                ReceiveAnimalStatesPrefixRegistered = false;
                return false;
            }
        }

        // ============= 1. 资格/目标：Update 条件评估 =============
        [HarmonyPrefix]
        [HarmonyPatch(typeof(AnimalManager), "Update")]
        public static void Update_Prefix()
        {
            try
            {
                if (AnimalManager.animals == null || AnimalManager.animals.Count == 0) return;

                float now = Time.realtimeSinceStartup;
                if (now - _lastUpdateLogTime < UpdateLogInterval) return;
                _lastUpdateLogTime = now;

                if (!WorldSyncDiagnosticCore.TryAcquireQuota("Animal.Update", out int count))
                {
                    return;
                }

                bool isDedicated = Dedicator.IsDedicatedServer;
                float lastTick = ReadLastTick();
                float timeSinceLastTick = now - lastTick;
                bool wouldSend = isDedicated && timeSinceLastTick > Provider.UPDATE_TIME;

                RoleLogger.Info("[Host]",
                    $"{PointPrefix} Update #{count}/{WorldSyncDiagnosticCore.PerPointLimit} " +
                    $"animals={AnimalManager.animals.Count} isDedicated={isDedicated} " +
                    $"timeSinceLastTick={timeSinceLastTick:F2}s UPDATE_TIME={Provider.UPDATE_TIME}s " +
                    $"wouldSendAnimalStates={wouldSend} " +
                    $"(vanilla: sendAnimalStates 仅在 isDedicated=true 时调用)");
            }
            catch (System.Exception ex)
            {
                try { RoleLogger.Error("[Shared]", $"{PointPrefix} Update Prefix 异常: {ex.Message}"); } catch { }
            }
        }

        // ============= 2. 发送入口：sendAnimalStates() =============
        [HarmonyPrefix]
        [HarmonyPatch(typeof(AnimalManager), "sendAnimalStates")]
        public static void SendAnimalStates_Prefix()
        {
            try
            {
                if (!WorldSyncDiagnosticCore.TryAcquireQuota("Animal.sendAnimalStates", out int count))
                {
                    return;
                }

                int animalCount = AnimalManager.animals?.Count ?? 0;
                RoleLogger.Info("[Host]",
                    $"{PointPrefix} sendAnimalStates #{count}/{WorldSyncDiagnosticCore.PerPointLimit} " +
                    $"animals={animalCount} isDedicated={Dedicator.IsDedicatedServer} " +
                    $"(真实发送入口已调用 - 周期动物状态包)");
            }
            catch (System.Exception ex)
            {
                try { RoleLogger.Error("[Shared]", $"{PointPrefix} sendAnimalStates Prefix 异常: {ex.Message}"); } catch { }
            }
        }

        // ============= 3. 源事件：spawnAnimal =============
        // v0.2.3.27-P0-A 返修（Codex TC-S2）：vanilla 真实签名为
        //   public static void spawnAnimal(ushort id, Vector3 point, Quaternion angle)
        // 首参是 ushort id（动物 asset ID），不是 AnimalAsset。
        [HarmonyPrefix]
        [HarmonyPatch(typeof(AnimalManager), "spawnAnimal")]
        public static void SpawnAnimal_Prefix(
            ushort id,
            Vector3 point,
            Quaternion angle)
        {
            try
            {
                if (!WorldSyncDiagnosticCore.TryAcquireQuota("Animal.spawnAnimal", out int count))
                {
                    return;
                }

                RoleLogger.Info("[Host]",
                    $"{PointPrefix} spawnAnimal #{count}/{WorldSyncDiagnosticCore.PerPointLimit} " +
                    $"id={id} " +
                    $"point=({point.x:F1},{point.y:F1},{point.z:F1}) " +
                    $"isDedicated={Dedicator.IsDedicatedServer}");
            }
            catch (System.Exception ex)
            {
                try { RoleLogger.Error("[Shared]", $"{PointPrefix} spawnAnimal Prefix 异常: {ex.Message}"); } catch { }
            }
        }

        // ============= 4. 客机 Receive 入口：ReceiveMultipleAnimals（初始全量） =============
        [HarmonyPrefix]
        [HarmonyPatch(typeof(AnimalManager), "ReceiveMultipleAnimals")]
        public static void ReceiveMultipleAnimals_Prefix()
        {
            try
            {
                if (!WorldSyncDiagnosticCore.TryAcquireQuota("Animal.ReceiveMultipleAnimals", out int count))
                {
                    return;
                }

                RoleLogger.Info("[Client]",
                    $"{PointPrefix} ReceiveMultipleAnimals #{count}/{WorldSyncDiagnosticCore.PerPointLimit} " +
                    $"(初始全量动物包 / 运行时增量动物包)");
            }
            catch (System.Exception ex)
            {
                try { RoleLogger.Error("[Shared]", $"{PointPrefix} ReceiveMultipleAnimals Prefix 异常: {ex.Message}"); } catch { }
            }
        }

        // ============= 5. 客机 Receive 入口：ReceiveAnimalStates（周期状态） =============
        [HarmonyPrefix]
        [HarmonyPatch(typeof(AnimalManager), "ReceiveAnimalStates")]
        public static void ReceiveAnimalStates_Prefix()
        {
            try
            {
                if (!WorldSyncDiagnosticCore.TryAcquireQuota("Animal.ReceiveAnimalStates", out int count))
                {
                    return;
                }

                int animalCount = AnimalManager.animals?.Count ?? 0;
                RoleLogger.Info("[Client]",
                    $"{PointPrefix} ReceiveAnimalStates #{count}/{WorldSyncDiagnosticCore.PerPointLimit} " +
                    $"animals={animalCount} " +
                    $"(周期动物状态包 - 客机收到)");
            }
            catch (System.Exception ex)
            {
                try { RoleLogger.Error("[Shared]", $"{PointPrefix} ReceiveAnimalStates Prefix 异常: {ex.Message}"); } catch { }
            }
        }

        // ============= 安全读取辅助 =============

        private static float ReadLastTick()
        {
            try
            {
                var field = typeof(AnimalManager).GetField("lastTick",
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
