using HarmonyLib;
using SDG.NetPak;
using SDG.Unturned;
using SteamP2PFriends.Client;
using SteamP2PFriends.Shared;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    ///
    ///       Prefix/Postfix/Finalizer 之间的状态通道；`__ownerInfo` 既不是 Harmony 特殊参数，
    ///       也不是 vanilla 原方法参数，运行时会抛 "IL Compile Error: Parameter __ownerInfo
    ///       does not contain a valid index"。改为在每个阶段通过 `__instance` 重新计算 owner
    ///       信息。
    ///
    ///       后续 hook 尝试。
    ///
    ///       后才为 true。VerifyCriticalPatches 必须同时检查此属性，不能仅依赖 Harmony
    ///       元数据数量与 owner。
    ///
    /// 覆盖方法：
    ///   1. LightingManager.ReceiveInitialLightingState (static, 9 个原始参数) -> Level.isLoadingLighting
    ///   2. VehicleManager.ReceiveMultipleVehicles (static, in ClientInvocationContext) -> Level.isLoadingVehicles
    ///   3. BarricadeManager.ReceiveMultipleBarricades (static, in ClientInvocationContext) -> Level.isLoadingBarricades
    ///   4. StructureManager.ReceiveMultipleStructures (static, in ClientInvocationContext) -> Level.isLoadingStructures
    ///   5. PlayerInventory.ReceiveInventory (instance, in ClientInvocationContext) -> Player.isLoadingInventory
    ///   6. PlayerLife.ReceiveLifeStats (instance, 7 个原始参数) -> Player.isLoadingLife
    ///   7. PlayerClothing.ReceiveClothingState (instance, in ClientInvocationContext) -> Player.isLoadingClothing
    /// </summary>
    public static class InitialStateReceiveDiagnosticPatch
    {
        /// <summary>
        /// 任一 RegisterXxx 抛异常或反射失败置 false，且 DiagnosticBuildValid=false。
        /// </summary>
        public static bool AllRegistrationsSucceeded { get; private set; }

        /// <summary>
        /// </summary>
        public static string RegistrationSummary { get; private set; } = "尚未登记";

        /// <summary>
        /// 返回 true 仅当 7 个 hook 全部登记成功。
        /// </summary>
        public static bool RegisterManual(Harmony harmony)
        {
            bool r1 = RegisterLightingManager(harmony);
            bool r2 = RegisterVehicleManager(harmony);
            bool r3 = RegisterBarricadeManager(harmony);
            bool r4 = RegisterStructureManager(harmony);
            bool r5 = RegisterPlayerInventory(harmony);
            bool r6 = RegisterPlayerLife(harmony);
            bool r7 = RegisterPlayerClothing(harmony);

            int success = (r1 ? 1 : 0) + (r2 ? 1 : 0) + (r3 ? 1 : 0) + (r4 ? 1 : 0)
                        + (r5 ? 1 : 0) + (r6 ? 1 : 0) + (r7 ? 1 : 0);
            AllRegistrationsSucceeded = (success == 7);
            RegistrationSummary = $"{{Lighting={r1},Vehicles={r2},Barricades={r3},Structures={r4}," +
                                  $"Inventory={r5},Life={r6},Clothing={r7}}} ({success}/7)";

            RoleLogger.Info("[Shared]",
                $"[P0-C] RegisterManual 汇总: {RegistrationSummary} " +
                $"AllRegistrationsSucceeded={AllRegistrationsSucceeded}");

            return AllRegistrationsSucceeded;
        }

        // ---------- 1. LightingManager.ReceiveInitialLightingState ----------
        private static bool RegisterLightingManager(Harmony harmony)
        {
            const string Label = "LightingManager.ReceiveInitialLightingState";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(LightingManager), "ReceiveInitialLightingState",
                    new System.Type[] {
                        typeof(uint), typeof(uint), typeof(uint), typeof(byte), typeof(byte),
                        typeof(System.Guid), typeof(float), typeof(NetId), typeof(int)
                    });
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[P0-C] !!! {Label} 反射失败");
                    return false;
                }

                MethodInfo prefix = typeof(LightingManagerHooks).GetMethod(nameof(LightingManagerHooks.Prefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo postfix = typeof(LightingManagerHooks).GetMethod(nameof(LightingManagerHooks.Postfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo finalizer = typeof(LightingManagerHooks).GetMethod(nameof(LightingManagerHooks.Finalizer),
                    BindingFlags.Static | BindingFlags.NonPublic);

                harmony.Patch(original,
                    prefix: new HarmonyMethod(prefix),
                    postfix: new HarmonyMethod(postfix),
                    finalizer: new HarmonyMethod(finalizer));
                RoleLogger.Info("[Shared]", $"[P0-C] OK {Label} 已登记 (Prefix/Postfix/Finalizer)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]",
                    $"[P0-C] !!! {Label} 登记异常: {ex.GetType().Name}: {ex.Message}\n" +
                    $"inner={(ex.InnerException == null ? "null" : ex.InnerException.GetType().Name + ": " + ex.InnerException.Message)}\n" +
                    $"stack:\n{ex.StackTrace}");
                CleanupFailedPatch(harmony, typeof(LightingManager), "ReceiveInitialLightingState");
                return false;
            }
        }

        // ---------- 2. VehicleManager.ReceiveMultipleVehicles ----------
        private static bool RegisterVehicleManager(Harmony harmony)
        {
            const string Label = "VehicleManager.ReceiveMultipleVehicles";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(VehicleManager), "ReceiveMultipleVehicles");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[P0-C] !!! {Label} 反射失败");
                    return false;
                }

                MethodInfo prefix = typeof(VehicleManagerHooks).GetMethod(nameof(VehicleManagerHooks.Prefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo postfix = typeof(VehicleManagerHooks).GetMethod(nameof(VehicleManagerHooks.Postfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo finalizer = typeof(VehicleManagerHooks).GetMethod(nameof(VehicleManagerHooks.Finalizer),
                    BindingFlags.Static | BindingFlags.NonPublic);

                harmony.Patch(original,
                    prefix: new HarmonyMethod(prefix),
                    postfix: new HarmonyMethod(postfix),
                    finalizer: new HarmonyMethod(finalizer));
                RoleLogger.Info("[Shared]", $"[P0-C] OK {Label} 已登记 (Prefix/Postfix/Finalizer)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]",
                    $"[P0-C] !!! {Label} 登记异常: {ex.GetType().Name}: {ex.Message}\n" +
                    $"inner={(ex.InnerException == null ? "null" : ex.InnerException.GetType().Name + ": " + ex.InnerException.Message)}\n" +
                    $"stack:\n{ex.StackTrace}");
                CleanupFailedPatch(harmony, typeof(VehicleManager), "ReceiveMultipleVehicles");
                return false;
            }
        }

        // ---------- 3. BarricadeManager.ReceiveMultipleBarricades ----------
        private static bool RegisterBarricadeManager(Harmony harmony)
        {
            const string Label = "BarricadeManager.ReceiveMultipleBarricades";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(BarricadeManager), "ReceiveMultipleBarricades");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[P0-C] !!! {Label} 反射失败");
                    return false;
                }

                MethodInfo prefix = typeof(BarricadeManagerHooks).GetMethod(nameof(BarricadeManagerHooks.Prefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo postfix = typeof(BarricadeManagerHooks).GetMethod(nameof(BarricadeManagerHooks.Postfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo finalizer = typeof(BarricadeManagerHooks).GetMethod(nameof(BarricadeManagerHooks.Finalizer),
                    BindingFlags.Static | BindingFlags.NonPublic);

                harmony.Patch(original,
                    prefix: new HarmonyMethod(prefix),
                    postfix: new HarmonyMethod(postfix),
                    finalizer: new HarmonyMethod(finalizer));
                RoleLogger.Info("[Shared]", $"[P0-C] OK {Label} 已登记 (Prefix/Postfix/Finalizer)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]",
                    $"[P0-C] !!! {Label} 登记异常: {ex.GetType().Name}: {ex.Message}\n" +
                    $"inner={(ex.InnerException == null ? "null" : ex.InnerException.GetType().Name + ": " + ex.InnerException.Message)}\n" +
                    $"stack:\n{ex.StackTrace}");
                CleanupFailedPatch(harmony, typeof(BarricadeManager), "ReceiveMultipleBarricades");
                return false;
            }
        }

        // ---------- 4. StructureManager.ReceiveMultipleStructures ----------
        private static bool RegisterStructureManager(Harmony harmony)
        {
            const string Label = "StructureManager.ReceiveMultipleStructures";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(StructureManager), "ReceiveMultipleStructures");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[P0-C] !!! {Label} 反射失败");
                    return false;
                }

                MethodInfo prefix = typeof(StructureManagerHooks).GetMethod(nameof(StructureManagerHooks.Prefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo postfix = typeof(StructureManagerHooks).GetMethod(nameof(StructureManagerHooks.Postfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo finalizer = typeof(StructureManagerHooks).GetMethod(nameof(StructureManagerHooks.Finalizer),
                    BindingFlags.Static | BindingFlags.NonPublic);

                harmony.Patch(original,
                    prefix: new HarmonyMethod(prefix),
                    postfix: new HarmonyMethod(postfix),
                    finalizer: new HarmonyMethod(finalizer));
                RoleLogger.Info("[Shared]", $"[P0-C] OK {Label} 已登记 (Prefix/Postfix/Finalizer)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]",
                    $"[P0-C] !!! {Label} 登记异常: {ex.GetType().Name}: {ex.Message}\n" +
                    $"inner={(ex.InnerException == null ? "null" : ex.InnerException.GetType().Name + ": " + ex.InnerException.Message)}\n" +
                    $"stack:\n{ex.StackTrace}");
                CleanupFailedPatch(harmony, typeof(StructureManager), "ReceiveMultipleStructures");
                return false;
            }
        }

        // ---------- 5. PlayerInventory.ReceiveInventory ----------
        private static bool RegisterPlayerInventory(Harmony harmony)
        {
            const string Label = "PlayerInventory.ReceiveInventory";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(PlayerInventory), "ReceiveInventory");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[P0-C] !!! {Label} 反射失败");
                    return false;
                }

                MethodInfo prefix = typeof(PlayerInventoryHooks).GetMethod(nameof(PlayerInventoryHooks.Prefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo postfix = typeof(PlayerInventoryHooks).GetMethod(nameof(PlayerInventoryHooks.Postfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo finalizer = typeof(PlayerInventoryHooks).GetMethod(nameof(PlayerInventoryHooks.Finalizer),
                    BindingFlags.Static | BindingFlags.NonPublic);

                harmony.Patch(original,
                    prefix: new HarmonyMethod(prefix),
                    postfix: new HarmonyMethod(postfix),
                    finalizer: new HarmonyMethod(finalizer));
                RoleLogger.Info("[Shared]", $"[P0-C] OK {Label} 已登记 (Prefix/Postfix/Finalizer)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]",
                    $"[P0-C] !!! {Label} 登记异常: {ex.GetType().Name}: {ex.Message}\n" +
                    $"inner={(ex.InnerException == null ? "null" : ex.InnerException.GetType().Name + ": " + ex.InnerException.Message)}\n" +
                    $"stack:\n{ex.StackTrace}");
                CleanupFailedPatch(harmony, typeof(PlayerInventory), "ReceiveInventory");
                return false;
            }
        }

        // ---------- 6. PlayerLife.ReceiveLifeStats ----------
        private static bool RegisterPlayerLife(Harmony harmony)
        {
            const string Label = "PlayerLife.ReceiveLifeStats";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(PlayerLife), "ReceiveLifeStats",
                    new System.Type[] {
                        typeof(byte), typeof(byte), typeof(byte), typeof(byte), typeof(byte), typeof(bool), typeof(bool)
                    });
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[P0-C] !!! {Label} 反射失败");
                    return false;
                }

                MethodInfo prefix = typeof(PlayerLifeHooks).GetMethod(nameof(PlayerLifeHooks.Prefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo postfix = typeof(PlayerLifeHooks).GetMethod(nameof(PlayerLifeHooks.Postfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo finalizer = typeof(PlayerLifeHooks).GetMethod(nameof(PlayerLifeHooks.Finalizer),
                    BindingFlags.Static | BindingFlags.NonPublic);

                harmony.Patch(original,
                    prefix: new HarmonyMethod(prefix),
                    postfix: new HarmonyMethod(postfix),
                    finalizer: new HarmonyMethod(finalizer));
                RoleLogger.Info("[Shared]", $"[P0-C] OK {Label} 已登记 (Prefix/Postfix/Finalizer)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]",
                    $"[P0-C] !!! {Label} 登记异常: {ex.GetType().Name}: {ex.Message}\n" +
                    $"inner={(ex.InnerException == null ? "null" : ex.InnerException.GetType().Name + ": " + ex.InnerException.Message)}\n" +
                    $"stack:\n{ex.StackTrace}");
                CleanupFailedPatch(harmony, typeof(PlayerLife), "ReceiveLifeStats");
                return false;
            }
        }

        // ---------- 7. PlayerClothing.ReceiveClothingState ----------
        private static bool RegisterPlayerClothing(Harmony harmony)
        {
            const string Label = "PlayerClothing.ReceiveClothingState";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(PlayerClothing), "ReceiveClothingState");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[P0-C] !!! {Label} 反射失败");
                    return false;
                }

                MethodInfo prefix = typeof(PlayerClothingHooks).GetMethod(nameof(PlayerClothingHooks.Prefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo postfix = typeof(PlayerClothingHooks).GetMethod(nameof(PlayerClothingHooks.Postfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo finalizer = typeof(PlayerClothingHooks).GetMethod(nameof(PlayerClothingHooks.Finalizer),
                    BindingFlags.Static | BindingFlags.NonPublic);

                harmony.Patch(original,
                    prefix: new HarmonyMethod(prefix),
                    postfix: new HarmonyMethod(postfix),
                    finalizer: new HarmonyMethod(finalizer));
                RoleLogger.Info("[Shared]", $"[P0-C] OK {Label} 已登记 (Prefix/Postfix/Finalizer)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]",
                    $"[P0-C] !!! {Label} 登记异常: {ex.GetType().Name}: {ex.Message}\n" +
                    $"inner={(ex.InnerException == null ? "null" : ex.InnerException.GetType().Name + ": " + ex.InnerException.Message)}\n" +
                    $"stack:\n{ex.StackTrace}");
                CleanupFailedPatch(harmony, typeof(PlayerClothing), "ReceiveClothingState");
                return false;
            }
        }

        /// <summary>
        /// 使用 HarmonyPatchType.All 因 Harmony 2.9 的 Unpatch(MethodBase, string) 重载不可用。
        /// 这些 vanilla 接收方法（ReceiveInventory 等）通常不被其他插件 patch，All 范围可接受。
        /// </summary>
        private static void CleanupFailedPatch(Harmony harmony, System.Type targetType, string methodName)
        {
            try
            {
                MethodInfo original = AccessTools.Method(targetType, methodName);
                if (original != null)
                {
                    harmony.Unpatch(original, HarmonyPatchType.All);
                    RoleLogger.Warn("[Shared]",
                        $"[P0-C] CleanupFailedPatch: 已清理 {targetType.Name}.{methodName} 上的残留元数据 (HarmonyPatchType.All)");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Shared]",
                    $"[P0-C] CleanupFailedPatch 异常（不阻断）: {ex.Message}");
            }
        }

        // ====================== Hooks ======================

        private static class LightingManagerHooks
        {
            private const string Label = "LightingManager.ReceiveInitialLightingState";

            internal static void Prefix(ref bool __state)
            {
                __state = Level.isLoadingLighting;
                RoleLogger.Info("[Client]",
                    $"[P0-C] {Label} ENTER before_loadingLighting={__state} isClient={Provider.isClient} isServer={Provider.isServer}");
            }

            internal static void Postfix(bool __state, bool __runOriginal)
            {
                bool after = Level.isLoadingLighting;
                RoleLogger.Info("[Client]",
                    $"[P0-C] {Label} RETURNED after_loadingLighting={after} before={__state} runOriginal={__runOriginal} " +
                    $"(cleared={__state && !after})");
                if (__state && !after)
                {
                    NativeLoadingGateDumper.Dump($"{Label}-cleared-loadingLighting");
                }
            }

            internal static System.Exception Finalizer(bool __state, System.Exception __exception)
            {
                if (__exception != null)
                {
                    RoleLogger.Error("[Client]",
                        $"[P0-C] {Label} THREW before_loadingLighting={__state} " +
                        $"exType={__exception.GetType().Name} exMsg={__exception.Message}");
                }
                return __exception;
            }
        }

        private static class VehicleManagerHooks
        {
            private const string Label = "VehicleManager.ReceiveMultipleVehicles";

            internal static void Prefix(ref bool __state)
            {
                __state = Level.isLoadingVehicles;
                RoleLogger.Info("[Client]",
                    $"[P0-C] {Label} ENTER before_loadingVehicles={__state} isClient={Provider.isClient} isServer={Provider.isServer}");
            }

            internal static void Postfix(bool __state, bool __runOriginal)
            {
                bool after = Level.isLoadingVehicles;
                RoleLogger.Info("[Client]",
                    $"[P0-C] {Label} RETURNED after_loadingVehicles={after} before={__state} runOriginal={__runOriginal} " +
                    $"(cleared={__state && !after})");
                if (__state && !after)
                {
                    NativeLoadingGateDumper.Dump($"{Label}-cleared-loadingVehicles");
                }
            }

            internal static System.Exception Finalizer(bool __state, System.Exception __exception)
            {
                if (__exception != null)
                {
                    RoleLogger.Error("[Client]",
                        $"[P0-C] {Label} THREW before_loadingVehicles={__state} " +
                        $"exType={__exception.GetType().Name} exMsg={__exception.Message}");
                }
                return __exception;
            }
        }

        private static class BarricadeManagerHooks
        {
            private const string Label = "BarricadeManager.ReceiveMultipleBarricades";

            internal static void Prefix(ref bool __state)
            {
                __state = Level.isLoadingBarricades;
                RoleLogger.Info("[Client]",
                    $"[P0-C] {Label} ENTER before_loadingBarricades={__state} isClient={Provider.isClient} isServer={Provider.isServer}");
            }

            internal static void Postfix(bool __state, bool __runOriginal)
            {
                bool after = Level.isLoadingBarricades;
                RoleLogger.Info("[Client]",
                    $"[P0-C] {Label} RETURNED after_loadingBarricades={after} before={__state} runOriginal={__runOriginal} " +
                    $"(cleared={__state && !after})");
                if (__state && !after)
                {
                    NativeLoadingGateDumper.Dump($"{Label}-cleared-loadingBarricades");
                }
            }

            internal static System.Exception Finalizer(bool __state, System.Exception __exception)
            {
                if (__exception != null)
                {
                    RoleLogger.Error("[Client]",
                        $"[P0-C] {Label} THREW before_loadingBarricades={__state} " +
                        $"exType={__exception.GetType().Name} exMsg={__exception.Message}");
                }
                return __exception;
            }
        }

        private static class StructureManagerHooks
        {
            private const string Label = "StructureManager.ReceiveMultipleStructures";

            internal static void Prefix(ref bool __state)
            {
                __state = Level.isLoadingStructures;
                RoleLogger.Info("[Client]",
                    $"[P0-C] {Label} ENTER before_loadingStructures={__state} isClient={Provider.isClient} isServer={Provider.isServer}");
            }

            internal static void Postfix(bool __state, bool __runOriginal)
            {
                bool after = Level.isLoadingStructures;
                RoleLogger.Info("[Client]",
                    $"[P0-C] {Label} RETURNED after_loadingStructures={after} before={__state} runOriginal={__runOriginal} " +
                    $"(cleared={__state && !after})");
                if (__state && !after)
                {
                    NativeLoadingGateDumper.Dump($"{Label}-cleared-loadingStructures");
                }
            }

            internal static System.Exception Finalizer(bool __state, System.Exception __exception)
            {
                if (__exception != null)
                {
                    RoleLogger.Error("[Client]",
                        $"[P0-C] {Label} THREW before_loadingStructures={__state} " +
                        $"exType={__exception.GetType().Name} exMsg={__exception.Message}");
                }
                return __exception;
            }
        }

        private static class PlayerInventoryHooks
        {
            private const string Label = "PlayerInventory.ReceiveInventory";

            internal static void Prefix(PlayerInventory __instance, ref bool __state)
            {
                __state = Player.isLoadingInventory;
                string ownerInfo = ExtractInstanceInfo(__instance);
                RoleLogger.Info("[Client]",
                    $"[P0-C] {Label} ENTER before_loadingInventory={__state} isClient={Provider.isClient} info=[{ownerInfo}]");
            }

            internal static void Postfix(PlayerInventory __instance, bool __state, bool __runOriginal)
            {
                bool after = Player.isLoadingInventory;
                string ownerInfo = ExtractInstanceInfo(__instance);
                RoleLogger.Info("[Client]",
                    $"[P0-C] {Label} RETURNED after_loadingInventory={after} before={__state} runOriginal={__runOriginal} " +
                    $"(cleared={__state && !after}) info=[{ownerInfo}]");
                if (__state && !after)
                {
                    NativeLoadingGateDumper.Dump($"{Label}-cleared-loadingInventory");
                }
            }

            internal static System.Exception Finalizer(PlayerInventory __instance, bool __state, System.Exception __exception)
            {
                if (__exception != null)
                {
                    string ownerInfo = ExtractInstanceInfo(__instance);
                    RoleLogger.Error("[Client]",
                        $"[P0-C] {Label} THREW before_loadingInventory={__state} " +
                        $"exType={__exception.GetType().Name} exMsg={__exception.Message} info=[{ownerInfo}]");
                }
                return __exception;
            }
        }

        private static class PlayerLifeHooks
        {
            private const string Label = "PlayerLife.ReceiveLifeStats";

            internal static void Prefix(PlayerLife __instance, ref bool __state)
            {
                __state = Player.isLoadingLife;
                string ownerInfo = ExtractInstanceInfo(__instance);
                RoleLogger.Info("[Client]",
                    $"[P0-C] {Label} ENTER before_loadingLife={__state} isClient={Provider.isClient} info=[{ownerInfo}]");
            }

            internal static void Postfix(PlayerLife __instance, bool __state, bool __runOriginal)
            {
                bool after = Player.isLoadingLife;
                string ownerInfo = ExtractInstanceInfo(__instance);
                RoleLogger.Info("[Client]",
                    $"[P0-C] {Label} RETURNED after_loadingLife={after} before={__state} runOriginal={__runOriginal} " +
                    $"(cleared={__state && !after}) info=[{ownerInfo}]");
                if (__state && !after)
                {
                    NativeLoadingGateDumper.Dump($"{Label}-cleared-loadingLife");
                }
            }

            internal static System.Exception Finalizer(PlayerLife __instance, bool __state, System.Exception __exception)
            {
                if (__exception != null)
                {
                    string ownerInfo = ExtractInstanceInfo(__instance);
                    RoleLogger.Error("[Client]",
                        $"[P0-C] {Label} THREW before_loadingLife={__state} " +
                        $"exType={__exception.GetType().Name} exMsg={__exception.Message} info=[{ownerInfo}]");
                }
                return __exception;
            }
        }

        private static class PlayerClothingHooks
        {
            private const string Label = "PlayerClothing.ReceiveClothingState";

            internal static void Prefix(PlayerClothing __instance, ref bool __state)
            {
                __state = Player.isLoadingClothing;
                string ownerInfo = ExtractInstanceInfo(__instance);
                RoleLogger.Info("[Client]",
                    $"[P0-C] {Label} ENTER before_loadingClothing={__state} isClient={Provider.isClient} info=[{ownerInfo}]");
            }

            internal static void Postfix(PlayerClothing __instance, bool __state, bool __runOriginal)
            {
                bool after = Player.isLoadingClothing;
                string ownerInfo = ExtractInstanceInfo(__instance);
                RoleLogger.Info("[Client]",
                    $"[P0-C] {Label} RETURNED after_loadingClothing={after} before={__state} runOriginal={__runOriginal} " +
                    $"(cleared={__state && !after}) info=[{ownerInfo}]");
                if (__state && !after)
                {
                    NativeLoadingGateDumper.Dump($"{Label}-cleared-loadingClothing");
                }
            }

            internal static System.Exception Finalizer(PlayerClothing __instance, bool __state, System.Exception __exception)
            {
                if (__exception != null)
                {
                    string ownerInfo = ExtractInstanceInfo(__instance);
                    RoleLogger.Error("[Client]",
                        $"[P0-C] {Label} THREW before_loadingClothing={__state} " +
                        $"exType={__exception.GetType().Name} exMsg={__exception.Message} info=[{ownerInfo}]");
                }
                return __exception;
            }
        }

        // ====================== Helpers ======================

        /// <summary>
        /// 提取实例方法的 owner 信息：channel.IsLocalPlayer / owner SteamID / NetId / Player instanceId。
        /// </summary>
        private static string ExtractInstanceInfo(object __instance)
        {
            try
            {
                if (__instance == null) return "instance=null";

                Player player = null;
                if (__instance is PlayerInventory inv) player = inv.player;
                else if (__instance is PlayerLife life) player = life.player;
                else if (__instance is PlayerClothing cloth) player = cloth.player;

                if (ReferenceEquals(player, null)) return "player=null";

                ulong steamId = 0;
                uint netId = 0;
                int instanceId = player.GetInstanceID();
                bool isLocalPlayer = false;

                try
                {
                    if (player.channel?.owner?.playerID?.steamID != null)
                    {
                        steamId = player.channel.owner.playerID.steamID.m_SteamID;
                    }
                    isLocalPlayer = player.channel?.IsLocalPlayer ?? false;
                    try { netId = player.channel?.owner?.GetNetId().id ?? 0; } catch { }
                }
                catch { /* ignore */ }

                return $"steamId={steamId} netId={netId} instanceId={instanceId} isLocalPlayer={isLocalPlayer}";
            }
            catch (System.Exception ex)
            {
                return $"extract-failed: {ex.Message}";
            }
        }
    }
}
