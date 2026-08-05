using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Patches;
using SteamP2PFriends.Shared;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SteamP2PFriends.Patches.P0EDiagnostic
{
    /// <summary>
    /// v0.2.3.38 P0-E-2 阶段 2 诊断补丁（Codex 阶段 2 外部审计 P0-R1~R7 返修版）：
    ///
    /// v0.2.3.38 阶段 2 第一版被 Codex 外部审计 NO-GO，7 项 Findings：
    ///   P0-R1: 多重 __state_x 参数无效，Harmony 仅识别单一 __state
    ///   P0-R3: isBusy 不存在（应读 player.equipment.isBusy），isUseable 是属性非字段
    ///   P0-R4: 实施报告所称 identity 自检未实现（仅 PlayerManagerCullingDiagnosticPatch 实现）
    ///   P0-R5: sessionId 从未变化，跨会话诊断无法关联
    ///   P1-R7: 缺少 build/dropBarricade 权威创建点 Hook
    ///
    /// 返修后 8 个诊断点（DP-1..DP-8）：
    ///   DP-1 startPrimary Prefix+Postfix：struct __state（isBusy/isValid/wasAsked/instanceId）
    ///   DP-2 check Postfix：__result
    ///   DP-3 checkSpace Postfix：__result、hit.point 只读、MainCamera.forward、player.look.aim.forward
    ///   DP-4 checkClaims Postfix：__result
    ///   DP-5 ReceiveBarricadeNone Prefix+Postfix：struct __state（wasAskedBefore/instanceId）
    ///   DP-6 simulate Postfix：isUsing/isUseable（属性反射）/isBusy（player.equipment.isBusy 直读）
    ///   DP-7 build Prefix+Postfix（新增 P1-R7）：struct __state（isUsing/isBuilding/startedUse/instanceId）
    ///   DP-8 BarricadeManager.dropBarricade Prefix+Postfix（新增 P1-R7）：asset.id/hit.name/point/owner/__result null
    ///
    /// 严格约束（零副作用）：
    ///   - 仅 Prefix/Postfix，禁止 Transpiler
    ///   - 不主动调用 check/checkSpace/checkClaims/startPrimary/build/simulate/dropBarricade
    ///   - 仅读取原版实际调用的 __result 与字段
    ///   - 反射启动时一次性缓存，失败 fail-closed（DiagnosticBuildValid=false）
    ///
    /// owner 自检（P0-R4）：所有 DP 使用 WorldSyncDiagnosticCore.RegisterIdentityPatch
    /// 会话重置（P0-R5）：静态构造注册 RegisterSessionResetCallback，递增 _sessionId + 清空节流缓存
    /// SteamID 脱敏：使用 DiagnosticMaskUtil.MaskSteamId
    /// </summary>
    public static class UseableBarricadeDiagnosticPatch
    {
        private const string Label = "[P0-E-2-Diag/Barricade]";

        // ===== Registration state (8 DPs + DP-5 Finalizer) =====
        public static bool DP1_StartPrimary_Registered { get; private set; }
        public static bool DP2_Check_Registered { get; private set; }
        public static bool DP3_CheckSpace_Registered { get; private set; }
        public static bool DP4_CheckClaims_Registered { get; private set; }
        public static bool DP5_ReceiveBarricadeNone_Registered { get; private set; }
        // v0.2.3.39 5B-0（Codex 第四十二次审计 §5 授权）：DP-5 Finalizer 异常诊断
        public static bool DP5_Finalizer_Registered { get; private set; }
        // v0.2.3.39 F1（Codex 第四十三次审计 §3 P0-F1 返修）：DP-5 Finalizer owner 精确自检
        // 登记成功不等于 owner 精确自检成功；任一失败都 fail-closed
        public static bool DP5_Finalizer_OwnerVerified { get; private set; }
        public static string DP5_Finalizer_OwnerSummary { get; private set; } = "<unverified>";
        public static bool DP6_Simulate_Registered { get; private set; }
        public static bool DP7_Build_Registered { get; private set; }
        public static bool DP8_DropBarricade_Registered { get; private set; }

        public static bool AllRegistrationsSucceeded =>
            DP1_StartPrimary_Registered && DP2_Check_Registered && DP3_CheckSpace_Registered
            && DP4_CheckClaims_Registered && DP5_ReceiveBarricadeNone_Registered
            && DP5_Finalizer_Registered && DP5_Finalizer_OwnerVerified
            && DP6_Simulate_Registered && DP7_Build_Registered && DP8_DropBarricade_Registered;

        // ===== Reflection cache (P0-R3) =====
        private static FieldInfo _isValidField;
        private static FieldInfo _wasAskedField;
        private static FieldInfo _isUsingField;
        private static FieldInfo _isBuildingField;
        private static FieldInfo _startedUseField;
        private static FieldInfo _pendingBuildHandleField;
        private static FieldInfo _hitField;
        private static PropertyInfo _isUseableProperty;
        // v0.2.3.39 5B-1A（Codex 第四十六次审计 §6 授权）：私有 help 字段启动时一次性缓存
        // 用于 Finalizer 异常快照确认 Listen Host 远端实例 help=null 候选（L553 boundsRotation = help.rotation）
        private static FieldInfo _helpField;
        private static bool _reflectionCached;
        private static bool _reflectionFailed;

        // ===== Throttle (per DP + instance) =====
        private static readonly Dictionary<(int dp, int instanceId), float> _lastLogTime
            = new Dictionary<(int dp, int instanceId), float>();
        private const float THROTTLE_SECONDS = 1.0f;

        // ===== Event counters (DP-5/DP-7/DP-8 always log, count tracks totals) =====
        private static long _dp5EventCount;
        private static long _dp5ExceptionCount; // v0.2.3.39 5B-0：DP-5 Finalizer 异常计数
        private static long _dp7EventCount;
        private static long _dp8EventCount;

        // ===== Session state (P0-R5) =====
        private static int _sessionId = 0;
        public static int CurrentSessionId => _sessionId;

        static UseableBarricadeDiagnosticPatch()
        {
            WorldSyncDiagnosticCore.RegisterSessionResetCallback(OnSessionReset);
        }

        private static void OnSessionReset()
        {
            int oldSession = _sessionId;
            _sessionId++;
            _lastLogTime.Clear();
            _dp5EventCount = 0;
            _dp5ExceptionCount = 0; // v0.2.3.39 5B-0：重置异常计数器
            _dp7EventCount = 0;
            _dp8EventCount = 0;
            RoleLogger.Info("[Shared]",
                $"{Label} RESET oldSession={oldSession} newSession={_sessionId} reason=WorldSyncDiagnosticCore.ResetAll");
        }

        // ===== Reflection caching (P0-R3: fail-closed) =====
        private static void CacheReflection()
        {
            if (_reflectionCached) return;
            _reflectionCached = true;
            try
            {
                _isValidField = AccessTools.Field(typeof(UseableBarricade), "isValid");
                _wasAskedField = AccessTools.Field(typeof(UseableBarricade), "wasAsked");
                _isUsingField = AccessTools.Field(typeof(UseableBarricade), "isUsing");
                _isBuildingField = AccessTools.Field(typeof(UseableBarricade), "isBuilding");
                _startedUseField = AccessTools.Field(typeof(UseableBarricade), "startedUse");
                _pendingBuildHandleField = AccessTools.Field(typeof(UseableBarricade), "pendingBuildHandle");
                _hitField = AccessTools.Field(typeof(UseableBarricade), "hit");
                _isUseableProperty = AccessTools.Property(typeof(UseableBarricade), "isUseable");
                // v0.2.3.39 5B-1A（Codex 第四十六次审计 §6）：私有 help 字段一次性缓存
                _helpField = AccessTools.Field(typeof(UseableBarricade), "help");

                if (_isValidField == null || _wasAskedField == null || _isUsingField == null
                    || _isBuildingField == null || _startedUseField == null
                    || _pendingBuildHandleField == null || _hitField == null
                    || _isUseableProperty == null || _helpField == null)
                {
                    RoleLogger.Error("[Shared]",
                        $"{Label} !!! CacheReflection 失败：isValid={_isValidField != null} wasAsked={_wasAskedField != null} "
                        + $"isUsing={_isUsingField != null} isBuilding={_isBuildingField != null} "
                        + $"startedUse={_startedUseField != null} pendingBuildHandle={_pendingBuildHandleField != null} "
                        + $"hit={_hitField != null} isUseable={_isUseableProperty != null} help={_helpField != null}");
                    _reflectionFailed = true;
                }
                else
                {
                    RoleLogger.Info("[Shared]", $"{Label} CacheReflection OK：所有字段/属性已缓存（含 5B-1A help）");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"{Label} !!! CacheReflection 异常: {ex.Message}");
                _reflectionFailed = true;
            }
        }

        public static bool RegisterManual(Harmony harmony)
        {
            CacheReflection();

            RoleLogger.Info("[Shared]", $"{Label} === 阶段 2 返修后诊断补丁登记开始 (8 DPs, identity-based) ===");

            if (harmony == null)
            {
                RoleLogger.Error("[Shared]", $"{Label} !!! harmony=null");
                return false;
            }

            if (_reflectionFailed)
            {
                RoleLogger.Error("[Shared]",
                    $"{Label} !!! reflectionFailed=true，按 P0-R3 fail-closed 不登记任何 DP（DiagnosticBuildValid 将为 false）");
                DP1_StartPrimary_Registered = DP2_Check_Registered = DP3_CheckSpace_Registered
                    = DP4_CheckClaims_Registered = DP5_ReceiveBarricadeNone_Registered
                    = DP5_Finalizer_Registered = DP5_Finalizer_OwnerVerified
                    = DP6_Simulate_Registered = DP7_Build_Registered = DP8_DropBarricade_Registered = false;
                DP5_Finalizer_OwnerSummary = "reflectionFailed";
                return false;
            }

            // DP-1 startPrimary Prefix + Postfix
            DP1_StartPrimary_Registered = RegisterOne(harmony, "startPrimary", null,
                nameof(Hooks.StartPrimaryPrefix), HarmonyPatchType.Prefix, "DP-1-startPrimary-Prefix")
                && RegisterOne(harmony, "startPrimary", null,
                nameof(Hooks.StartPrimaryPostfix), HarmonyPatchType.Postfix, "DP-1-startPrimary-Postfix");

            // DP-2 check Postfix
            DP2_Check_Registered = RegisterOne(harmony, "check", null,
                nameof(Hooks.CheckPostfix), HarmonyPatchType.Postfix, "DP-2-check-Postfix");

            // DP-3 checkSpace Postfix
            DP3_CheckSpace_Registered = RegisterOne(harmony, "checkSpace", null,
                nameof(Hooks.CheckSpacePostfix), HarmonyPatchType.Postfix, "DP-3-checkSpace-Postfix");

            // DP-4 checkClaims Postfix
            DP4_CheckClaims_Registered = RegisterOne(harmony, "checkClaims", null,
                nameof(Hooks.CheckClaimsPostfix), HarmonyPatchType.Postfix, "DP-4-checkClaims-Postfix");

            // DP-5 ReceiveBarricadeNone Prefix + Postfix + Finalizer
            // v0.2.3.39 5B-0（Codex 第四十二次审计 §5 授权）：新增 Finalizer 异常诊断
            System.Type[] receiveBarricadeNoneParams = {
                typeof(ServerInvocationContext).MakeByRefType(),
                typeof(Vector3), typeof(float), typeof(float), typeof(float)
            };
            DP5_ReceiveBarricadeNone_Registered = RegisterOne(harmony, "ReceiveBarricadeNone",
                receiveBarricadeNoneParams,
                nameof(Hooks.ReceiveBarricadeNonePrefix), HarmonyPatchType.Prefix, "DP-5-ReceiveBarricadeNone-Prefix")
                && RegisterOne(harmony, "ReceiveBarricadeNone",
                receiveBarricadeNoneParams,
                nameof(Hooks.ReceiveBarricadeNonePostfix), HarmonyPatchType.Postfix, "DP-5-ReceiveBarricadeNone-Postfix");
            // Finalizer 独立登记，不阻塞 Prefix/Postfix 的成功状态
            DP5_Finalizer_Registered = RegisterOne(harmony, "ReceiveBarricadeNone",
                receiveBarricadeNoneParams,
                nameof(Hooks.ReceiveBarricadeNoneFinalizer), HarmonyPatchType.Finalizer, "DP-5-ReceiveBarricadeNone-Finalizer");

            // DP-6 simulate Postfix
            System.Type[] simulateParams = { typeof(uint), typeof(bool) };
            DP6_Simulate_Registered = RegisterOne(harmony, "simulate", simulateParams,
                nameof(Hooks.SimulatePostfix), HarmonyPatchType.Postfix, "DP-6-simulate-Postfix");

            // DP-7 build Prefix + Postfix (NEW P1-R7)
            DP7_Build_Registered = RegisterOne(harmony, "build", null,
                nameof(Hooks.BuildPrefix), HarmonyPatchType.Prefix, "DP-7-build-Prefix")
                && RegisterOne(harmony, "build", null,
                nameof(Hooks.BuildPostfix), HarmonyPatchType.Postfix, "DP-7-build-Postfix");

            // DP-8 BarricadeManager.dropBarricade Prefix + Postfix (NEW P1-R7)
            System.Type[] dropBarricadeParams = {
                typeof(Barricade), typeof(Transform), typeof(Vector3),
                typeof(float), typeof(float), typeof(float),
                typeof(ulong), typeof(ulong)
            };
            DP8_DropBarricade_Registered = RegisterOne(harmony, "dropBarricade", dropBarricadeParams,
                nameof(Hooks.DropBarricadePrefix), HarmonyPatchType.Prefix, "DP-8-dropBarricade-Prefix",
                targetType: typeof(BarricadeManager))
                && RegisterOne(harmony, "dropBarricade", dropBarricadeParams,
                nameof(Hooks.DropBarricadePostfix), HarmonyPatchType.Postfix, "DP-8-dropBarricade-Postfix",
                targetType: typeof(BarricadeManager));

            // v0.2.3.39 F1（Codex 第四十三次审计 §3 P0-F1 返修）：DP-5 Finalizer owner 精确自检
            // 登记成功不等于 owner 精确自检成功；任一失败都 fail-closed
            VerifyDP5FinalizerOwner(harmony);

            bool ok = AllRegistrationsSucceeded;
            RoleLogger.Info("[Shared]",
                $"{Label} === 阶段 2 返修后诊断补丁登记完成 ok={ok} "
                + $"DP1={DP1_StartPrimary_Registered} DP2={DP2_Check_Registered} "
                + $"DP3={DP3_CheckSpace_Registered} DP4={DP4_CheckClaims_Registered} "
                + $"DP5={DP5_ReceiveBarricadeNone_Registered} DP5Finalizer={DP5_Finalizer_Registered} "
                + $"owner5Finalizer={DP5_Finalizer_OwnerVerified} ownerSummary=\"{DP5_Finalizer_OwnerSummary}\" "
                + $"DP6={DP6_Simulate_Registered} "
                + $"DP7={DP7_Build_Registered} DP8={DP8_DropBarricade_Registered} ===");
            return ok;
        }

        /// <summary>
        /// v0.2.3.39 F1（Codex 第四十三次审计 §3 P0-F1 返修）：DP-5 Finalizer owner 精确自检。
        /// 使用精确 MethodInfo 比较，要求 exactExpectedCount == 1。
        /// 同 owner 的其他合法 Prefix/Postfix 不影响此验证（仅检查 Finalizers 集合）。
        /// owner 自检失败必然令 AllRegistrationsSucceeded=false。
        /// </summary>
        private static void VerifyDP5FinalizerOwner(Harmony harmony)
        {
            try
            {
                System.Type[] receiveBarricadeNoneParams = {
                    typeof(ServerInvocationContext).MakeByRefType(),
                    typeof(Vector3), typeof(float), typeof(float), typeof(float)
                };
                MethodInfo original = AccessTools.Method(typeof(UseableBarricade), "ReceiveBarricadeNone",
                    receiveBarricadeNoneParams);
                MethodInfo expectedFinalizer = typeof(Hooks).GetMethod(
                    nameof(Hooks.ReceiveBarricadeNoneFinalizer), BindingFlags.Static | BindingFlags.NonPublic);
                DP5_Finalizer_OwnerVerified = VerifyPatchOwnerExact(original, expectedFinalizer, out string summary);
                DP5_Finalizer_OwnerSummary = summary;
                if (!DP5_Finalizer_OwnerVerified)
                {
                    RoleLogger.Error("[Shared]", $"{Label} !!! DP-5 Finalizer owner 自检失败: {summary}");
                }
                else
                {
                    RoleLogger.Info("[Shared]", $"{Label} DP-5 Finalizer owner 自检 OK: {summary}");
                }
            }
            catch (System.Exception ex)
            {
                DP5_Finalizer_OwnerVerified = false;
                DP5_Finalizer_OwnerSummary = $"exception: {ex.Message}";
                RoleLogger.Error("[Shared]", $"{Label} DP-5 Finalizer owner 自检异常: {ex.Message}");
            }
        }

        private static bool VerifyPatchOwnerExact(MethodInfo original, MethodInfo expectedMethod, out string summaryOut)
        {
            if (original == null)
            {
                summaryOut = "original=null";
                return false;
            }
            if (expectedMethod == null)
            {
                summaryOut = "expectedMethod=null";
                return false;
            }

            HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
            System.Collections.ICollection patches = info?.Finalizers;
            if (patches == null || patches.Count == 0)
            {
                summaryOut = "finalizers=0";
                return false;
            }

            int exactExpectedCount = 0;
            int sameOwnerOtherCount = 0;
            int foreignOwnerCount = 0;
            string firstForeignOwner = null;

            foreach (Patch p in patches)
            {
                bool isOurOwner = (p.owner == SteamP2PFriendsPlugin.HARMONY_ID);
                bool isExactMethod = IsSameMethodInfo(p.PatchMethod, expectedMethod);

                if (isOurOwner && isExactMethod)
                {
                    exactExpectedCount++;
                }
                else if (isOurOwner)
                {
                    sameOwnerOtherCount++;
                }
                else
                {
                    foreignOwnerCount++;
                    if (firstForeignOwner == null)
                    {
                        firstForeignOwner = $"{p.owner}/{p.PatchMethod?.DeclaringType?.Name}.{p.PatchMethod?.Name}";
                    }
                }
            }

            int total = patches.Count;
            summaryOut = $"exact={exactExpectedCount} sameOwnerOther={sameOwnerOtherCount} foreign={foreignOwnerCount} total={total} foreignOwner={firstForeignOwner ?? "<none>"}";
            return exactExpectedCount == 1;
        }

        private static bool IsSameMethodInfo(MethodInfo a, MethodInfo b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a.Name != b.Name) return false;
            if (!ReferenceEquals(a.DeclaringType, b.DeclaringType)) return false;
            if (!ReferenceEquals(a.Module, b.Module)) return false;
            try
            {
                if (a.MetadataToken != 0 && b.MetadataToken != 0
                    && a.MetadataToken == b.MetadataToken)
                {
                    return true;
                }
            }
            catch
            {
            }
            return false;
        }

        private static bool RegisterOne(Harmony harmony, string methodName, System.Type[] paramTypes,
            string hookName, HarmonyPatchType patchType, string label, System.Type targetType = null)
        {
            try
            {
                if (targetType == null) targetType = typeof(UseableBarricade);
                MethodInfo hook = typeof(Hooks).GetMethod(hookName, BindingFlags.Static | BindingFlags.NonPublic);
                if (hook == null)
                {
                    RoleLogger.Error("[Shared]", $"{Label} !!! {label} hook MethodInfo 未找到: {hookName}");
                    return false;
                }
                return WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, targetType, methodName, paramTypes, hook, patchType, label);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"{Label} !!! {label} 登记异常: {ex.Message}");
                return false;
            }
        }

        // ====================== Helpers ======================

        private static bool ShouldLog(int dpId, int instanceId)
        {
            float now = Time.realtimeSinceStartup;
            var key = (dpId, instanceId);
            if (_lastLogTime.TryGetValue(key, out float t) && now - t < THROTTLE_SECONDS) return false;
            _lastLogTime[key] = now;
            return true;
        }

        private static string FormatPos(Vector3 v) => $"({v.x:F2},{v.y:F2},{v.z:F2})";

        private static string ResolveInstanceRole(UseableBarricade instance)
        {
            try
            {
                if (instance == null) return "null";
                bool isLocalPlayer = instance.player?.channel?.IsLocalPlayer ?? false;
                bool isServer = Provider.isServer;
                if (isServer && !isLocalPlayer) return "HostRemoteClient";
                if (isServer && isLocalPlayer) return "HostLocal";
                if (!isServer && isLocalPlayer) return "ClientLocal";
                return "Other";
            }
            catch { return "error"; }
        }

        private static string GetMaskedSteamId(UseableBarricade instance)
        {
            try
            {
                ulong sid = instance?.player?.channel?.owner?.playerID?.steamID.m_SteamID ?? 0UL;
                return DiagnosticMaskUtil.MaskSteamId(sid);
            }
            catch { return "error"; }
        }

        // ====================== State Structs (P0-R1: single struct per Prefix/Postfix pair) ======================

        private struct StartPrimaryState
        {
            public bool isBusy;
            public bool isValid;
            public bool wasAsked;
            public int instanceId;
            // v0.2.3.38 4B 编码 R4：pendingBuildHandle 必须在 Prefix 读取 before，
            // Postfix 读取 after。Postfix 读取值不可标为 before。
            public int pendingBuildHandle;
        }

        private struct ReceiveBarricadeNoneState
        {
            public bool wasAskedBefore;
            public int instanceId;
        }

        private struct BuildState
        {
            public bool isUsing;
            public bool isBuilding;
            public float startedUse;
            public int instanceId;
        }

        // ====================== Hooks ======================

        private static class Hooks
        {
            // DP-1: startPrimary Prefix - 保存 before 状态（P0-R1: 单一 struct __state）
            // 签名：public override bool startPrimary()，无参数
            internal static void StartPrimaryPrefix(UseableBarricade __instance, out StartPrimaryState __state)
            {
                __state = new StartPrimaryState();
                if (_reflectionFailed) return;
                try
                {
                    // P0-R3: isBusy 直读 player.equipment.isBusy（公共字段链）
                    __state.isBusy = __instance?.player?.equipment?.isBusy ?? false;
                    __state.isValid = _isValidField != null && (bool)_isValidField.GetValue(__instance);
                    __state.wasAsked = _wasAskedField != null && (bool)_wasAskedField.GetValue(__instance);
                    __state.instanceId = __instance.GetInstanceID();
                    // v0.2.3.38 4B 编码 R4：pendingBuildHandle(before) 必须在 Prefix 读取，
                    // 不得在 Postfix 读取后标为 before。
                    __state.pendingBuildHandle = -1;
                    if (_pendingBuildHandleField != null)
                    {
                        try { __state.pendingBuildHandle = (int)_pendingBuildHandleField.GetValue(__instance); } catch { }
                    }
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"{Label} DP-1 Prefix 反射异常: {ex.Message}");
                }
            }

            // DP-1: startPrimary Postfix - 读取 __result + after 状态
            internal static void StartPrimaryPostfix(UseableBarricade __instance, bool __result, StartPrimaryState __state)
            {
                try
                {
                    if (!ShouldLog(1, __state.instanceId)) return;

                    bool isBusyAfter = __instance?.player?.equipment?.isBusy ?? false;
                    bool isValidAfter = _isValidField != null && (bool)_isValidField.GetValue(__instance);
                    bool wasAskedAfter = _wasAskedField != null && (bool)_wasAskedField.GetValue(__instance);

                    bool isLocalPlayer = __instance?.player?.channel?.IsLocalPlayer ?? false;
                    bool isServer = Provider.isServer;
                    bool isDedicated = Dedicator.IsDedicatedServer;

                    // v0.2.3.38 4B 编码 R4：pendingBuildHandle(after) 在 Postfix 读取，
                    // 与 Prefix 的 before 值一并输出 (before=X,after=Y)。
                    int pendingBuildHandleAfter = -1;
                    if (_pendingBuildHandleField != null)
                    {
                        try { pendingBuildHandleAfter = (int)_pendingBuildHandleField.GetValue(__instance); } catch { }
                    }

                    RoleLogger.Info("[Shared]",
                        $"{Label} DP-1 startPrimary session={_sessionId} role={ResolveInstanceRole(__instance)} "
                        + $"instance={__state.instanceId} result={__result} "
                        + $"isLocalPlayer={isLocalPlayer} isServer={isServer} isDedicated={isDedicated} "
                        + $"steamId={GetMaskedSteamId(__instance)} "
                        + $"isBusy(before={__state.isBusy},after={isBusyAfter}) "
                        + $"isValid(before={__state.isValid},after={isValidAfter}) "
                        + $"wasAsked(before={__state.wasAsked},after={wasAskedAfter}) "
                        + $"pendingBuildHandle(before={__state.pendingBuildHandle},after={pendingBuildHandleAfter}) "
                        + $"sessionQuota={WorldSyncDiagnosticCore.SessionTotalCount}/{WorldSyncDiagnosticCore.SessionTotalLimit}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"{Label} DP-1 Postfix 异常: {ex.Message}");
                }
            }

            // DP-2: check Postfix - 仅读 __result
            internal static void CheckPostfix(UseableBarricade __instance, bool __result)
            {
                try
                {
                    int instanceId = __instance.GetInstanceID();
                    if (!ShouldLog(2, instanceId)) return;

                    RoleLogger.Info("[Shared]",
                        $"{Label} DP-2 check session={_sessionId} role={ResolveInstanceRole(__instance)} "
                        + $"instance={instanceId} result={__result} "
                        + $"steamId={GetMaskedSteamId(__instance)}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"{Label} DP-2 Postfix 异常: {ex.Message}");
                }
            }

            // DP-3: checkSpace Postfix - __result + hit.point 只读快照 + MainCamera.forward + aim.forward
            internal static void CheckSpacePostfix(UseableBarricade __instance, bool __result)
            {
                try
                {
                    int instanceId = __instance.GetInstanceID();
                    if (!ShouldLog(3, instanceId)) return;

                    string hitPointStr = "unknown";
                    if (_hitField != null)
                    {
                        try
                        {
                            object hitObj = _hitField.GetValue(__instance);
                            if (hitObj is RaycastHit hit)
                            {
                                hitPointStr = FormatPos(hit.point);
                            }
                        }
                        catch { }
                    }

                    string mainCamFwdStr = "unknown";
                    try
                    {
                        if (MainCamera.instance != null)
                        {
                            Vector3 fwd = MainCamera.instance.transform.forward;
                            mainCamFwdStr = $"({fwd.x:F2},{fwd.y:F2},{fwd.z:F2})";
                        }
                    }
                    catch { }

                    string aimFwdStr = "unknown";
                    try
                    {
                        if (__instance?.player?.look?.aim != null)
                        {
                            Vector3 fwd = __instance.player.look.aim.forward;
                            aimFwdStr = $"({fwd.x:F2},{fwd.y:F2},{fwd.z:F2})";
                        }
                    }
                    catch { }

                    RoleLogger.Info("[Shared]",
                        $"{Label} DP-3 checkSpace session={_sessionId} role={ResolveInstanceRole(__instance)} "
                        + $"instance={instanceId} result={__result} "
                        + $"steamId={GetMaskedSteamId(__instance)} "
                        + $"hitPoint={hitPointStr} mainCamFwd={mainCamFwdStr} aimFwd={aimFwdStr}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"{Label} DP-3 Postfix 异常: {ex.Message}");
                }
            }

            // DP-4: checkClaims Postfix
            internal static void CheckClaimsPostfix(UseableBarricade __instance, bool __result)
            {
                try
                {
                    int instanceId = __instance.GetInstanceID();
                    if (!ShouldLog(4, instanceId)) return;

                    RoleLogger.Info("[Shared]",
                        $"{Label} DP-4 checkClaims session={_sessionId} role={ResolveInstanceRole(__instance)} "
                        + $"instance={instanceId} result={__result} "
                        + $"steamId={GetMaskedSteamId(__instance)}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"{Label} DP-4 Postfix 异常: {ex.Message}");
                }
            }

            // DP-5: ReceiveBarricadeNone Prefix - 保存 before 状态（P0-R1: 单一 struct __state）
            // 签名：public void ReceiveBarricadeNone(in ServerInvocationContext context, Vector3 newPoint, float newAngle_X, float newAngle_Y, float newAngle_Z)
            internal static void ReceiveBarricadeNonePrefix(UseableBarricade __instance,
                Vector3 newPoint,
                out ReceiveBarricadeNoneState __state)
            {
                __state = new ReceiveBarricadeNoneState();
                if (_reflectionFailed) return;
                try
                {
                    __state.instanceId = __instance.GetInstanceID();
                    __state.wasAskedBefore = _wasAskedField != null && (bool)_wasAskedField.GetValue(__instance);

                    Vector3 aimPos = Vector3.zero;
                    try { aimPos = __instance.player.look.aim.position; } catch { }
                    float sqrDist = (newPoint - aimPos).sqrMagnitude;

                    bool isLocalPlayer = __instance?.player?.channel?.IsLocalPlayer ?? false;
                    string maskedId = GetMaskedSteamId(__instance);

                    RoleLogger.Info("[Shared]",
                        $"{Label} DP-5 ReceiveBarricadeNone PRE session={_sessionId} role={ResolveInstanceRole(__instance)} "
                        + $"instance={__state.instanceId} "
                        + $"isLocalPlayer={isLocalPlayer} steamId={maskedId} "
                        + $"newPoint={FormatPos(newPoint)} aimPos={FormatPos(aimPos)} "
                        + $"sqrDist={sqrDist:F2} (<256 pass) wasAsked(before={__state.wasAskedBefore})");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"{Label} DP-5 Prefix 异常: {ex.Message}");
                }
            }

            // DP-5: ReceiveBarricadeNone Postfix - 读取 after 状态
            internal static void ReceiveBarricadeNonePostfix(UseableBarricade __instance,
                Vector3 newPoint,
                ReceiveBarricadeNoneState __state)
            {
                try
                {
                    bool wasAskedAfter = false, isValidAfter = false;
                    int pendingHandleAfter = -1;
                    if (_wasAskedField != null) try { wasAskedAfter = (bool)_wasAskedField.GetValue(__instance); } catch { }
                    if (_isValidField != null) try { isValidAfter = (bool)_isValidField.GetValue(__instance); } catch { }
                    if (_pendingBuildHandleField != null) try { pendingHandleAfter = (int)_pendingBuildHandleField.GetValue(__instance); } catch { }

                    _dp5EventCount++;
                    RoleLogger.Info("[Shared]",
                        $"{Label} DP-5 ReceiveBarricadeNone POST session={_sessionId} role={ResolveInstanceRole(__instance)} "
                        + $"instance={__state.instanceId} "
                        + $"wasAsked(before={__state.wasAskedBefore},after={wasAskedAfter}) "
                        + $"isValid(after={isValidAfter}) pendingBuildHandle(after={pendingHandleAfter}) eventCount={_dp5EventCount}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"{Label} DP-5 Postfix 异常: {ex.Message}");
                }
            }

            // DP-5: ReceiveBarricadeNone Finalizer - v0.2.3.39 5B-0（Codex 第四十二次审计 §5 授权）
            // v0.2.3.39 5B-1A（Codex 第四十六次审计 §6 授权）：扩展依赖快照
            //   - 仅在 __exception != null 时输出（Codex §6.2）
            //   - 仅只读快照，不修改任何字段
            //   - 私有 help 字段使用启动时缓存反射（不运行时查找）
            //   - 反射缓存失败已进入 Barricade reflectionFailed fail-closed
            // 不吞异常，不修改字段，不强制继续。
            // 签名与 Prefix/Postfix 一致：原方法所有参数 + __exception
            // 返回 __exception 原样（包括 null）以保持 vanilla 异常传播行为。
            internal static System.Exception ReceiveBarricadeNoneFinalizer(UseableBarricade __instance,
                Vector3 newPoint,
                System.Exception __exception)
            {
                // 正常返回（无异常）只计数，不输出堆栈
                if (__exception == null) return null;

                // Finalizer 自身使用 try/catch 保护（Codex §5.1 要求）
                try
                {
                    _dp5ExceptionCount++;

                    // 读取当前字段值（只读，不修改）
                    bool wasAsked = false, isValid = false;
                    int pendingHandle = -1;
                    if (_wasAskedField != null) try { wasAsked = (bool)_wasAskedField.GetValue(__instance); } catch { }
                    if (_isValidField != null) try { isValid = (bool)_isValidField.GetValue(__instance); } catch { }
                    if (_pendingBuildHandleField != null) try { pendingHandle = (int)_pendingBuildHandleField.GetValue(__instance); } catch { }

                    // 有界 StackTrace，限制 1024 字符避免日志爆量
                    string stack = __exception.StackTrace ?? "";
                    if (stack.Length > 1024)
                    {
                        stack = stack.Substring(0, 1024) + "...<truncated>";
                    }

                    // v0.2.3.39 5B-1A（Codex 第四十六次审计 §6）：依赖快照
                    // 仅在异常时输出，用于确认 Listen Host 远端实例的 L553 help=null 候选
                    // 所有字段读取使用 try/catch 保护，任一异常不影响其他字段
                    string depSnapshot = BuildDependencySnapshot(__instance);

                    // 异常时输出完整 Finalizer 异常记录
                    // 不记录原始 SteamID/群组 ID/敏感网络地址（GetMaskedSteamId 已脱敏）
                    RoleLogger.Error("[Shared]",
                        $"{Label} DP-5 ReceiveBarricadeNone FINALIZER EXCEPTION session={_sessionId} role={ResolveInstanceRole(__instance)} "
                        + $"instance={(__instance != null ? __instance.GetInstanceID() : -1)} "
                        + $"steamId={GetMaskedSteamId(__instance)} "
                        + $"exceptionType={__exception.GetType().Name} message={__exception.Message} "
                        + $"wasAsked={wasAsked} isValid={isValid} pendingBuildHandle={pendingHandle} "
                        + $"exceptionCount={_dp5ExceptionCount} "
                        + $"stackTrace(1k)={stack} "
                        + $"depSnapshot={depSnapshot}");
                }
                catch
                {
                    // Finalizer 自身异常时静默，绝不影响 vanilla 异常传播
                }

                // 必须原样返回 __exception，不返回 null 抑制异常
                return __exception;
            }

            /// <summary>
            /// v0.2.3.39 5B-1A v2（Codex 第四十七次审计 §4.1 授权返修）：
            ///   - 逐字段独立 try/catch：一个字段异常不得阻断其他字段输出
            ///   - 三态输出：true/false/unknown(errorType)，不得用默认 false 冒充成功读取
            ///   - help 4 属性：helpFieldCached / helpClrNull / helpUnityNull / helpType
            ///   - 保留关键依赖：player、movement、isSafe、isSafeInfo、asset/build、
            ///     channel、owner、playerID、quests、help、dedicated、localPlayer
            ///   - 不新增 Harmony 入口、Tick 或运行时反射查找
            ///   - _helpField 仅在启动时缓存
            ///   - 仅在 DP-5 Finalizer 且 __exception != null 时调用（低频异常路径）
            /// </summary>
            private static string BuildDependencySnapshot(UseableBarricade instance)
            {
                var parts = new List<string>(20);

                // ===== instance Unity null =====
                try
                {
                    bool instanceUnityNull = instance == null;
                    parts.Add($"instanceUnityNull={(instanceUnityNull ? "true" : "false")}");
                    if (instanceUnityNull) return string.Join(" ", parts);
                }
                catch (System.Exception ex)
                {
                    parts.Add($"instanceUnityNull=unknown({ex.GetType().Name})");
                    return string.Join(" ", parts);
                }

                // ===== player =====
                Player player = null;
                bool playerReadOk = false;
                try { player = instance.player; playerReadOk = true; }
                catch (System.Exception ex) { parts.Add($"playerNull=unknown({ex.GetType().Name})"); }
                if (playerReadOk)
                {
                    bool playerNull = player == null;
                    parts.Add($"playerNull={(playerNull ? "true" : "false")}");
                }

                // ===== movement =====
                PlayerMovement movement = null;
                bool movementReadOk = false;
                if (playerReadOk && player != null)
                {
                    try { movement = player.movement; movementReadOk = true; }
                    catch (System.Exception ex) { parts.Add($"movementNull=unknown({ex.GetType().Name})"); }
                }
                if (movementReadOk)
                {
                    bool movementNull = movement == null;
                    parts.Add($"movementNull={(movementNull ? "true" : "false")}");
                }

                // ===== isSafe =====
                if (movementReadOk && movement != null)
                {
                    try
                    {
                        bool isSafe = movement.isSafe;
                        parts.Add($"isSafe={(isSafe ? "true" : "false")}");
                    }
                    catch (System.Exception ex) { parts.Add($"isSafe=unknown({ex.GetType().Name})"); }
                }
                else
                {
                    parts.Add("isSafe=unknown(notRead)");
                }

                // ===== isSafeInfoNull =====
                if (movementReadOk && movement != null)
                {
                    try
                    {
                        bool isSafeInfoNull = movement.isSafeInfo == null;
                        parts.Add($"isSafeInfoNull={(isSafeInfoNull ? "true" : "false")}");
                    }
                    catch (System.Exception ex) { parts.Add($"isSafeInfoNull=unknown({ex.GetType().Name})"); }
                }
                else
                {
                    parts.Add("isSafeInfoNull=unknown(notRead)");
                }

                // ===== asset =====
                ItemBarricadeAsset asset = null;
                bool assetReadOk = false;
                if (playerReadOk && player != null)
                {
                    try { asset = player.equipment?.asset as ItemBarricadeAsset; assetReadOk = true; }
                    catch (System.Exception ex) { parts.Add($"assetNull=unknown({ex.GetType().Name})"); }
                }
                if (assetReadOk)
                {
                    bool assetNull = asset == null;
                    parts.Add($"assetNull={(assetNull ? "true" : "false")}");
                }

                // ===== assetBuild =====
                if (assetReadOk && asset != null)
                {
                    try
                    {
                        string buildStr = asset.build.ToString();
                        parts.Add($"assetBuild={buildStr}");
                    }
                    catch (System.Exception ex) { parts.Add($"assetBuild=unknown({ex.GetType().Name})"); }
                }
                else
                {
                    parts.Add("assetBuild=n/a");
                }

                // ===== channel =====
                SteamChannel channel = null;
                bool channelReadOk = false;
                if (playerReadOk && player != null)
                {
                    try { channel = player.channel; channelReadOk = true; }
                    catch (System.Exception ex) { parts.Add($"channelNull=unknown({ex.GetType().Name})"); }
                }
                if (channelReadOk)
                {
                    bool channelNull = channel == null;
                    parts.Add($"channelNull={(channelNull ? "true" : "false")}");
                }

                // ===== owner =====
                SteamPlayer owner = null;
                bool ownerReadOk = false;
                if (channelReadOk && channel != null)
                {
                    try { owner = channel.owner; ownerReadOk = true; }
                    catch (System.Exception ex) { parts.Add($"ownerNull=unknown({ex.GetType().Name})"); }
                }
                if (ownerReadOk)
                {
                    bool ownerNull = owner == null;
                    parts.Add($"ownerNull={(ownerNull ? "true" : "false")}");
                }

                // ===== playerIdNull =====
                if (ownerReadOk && owner != null)
                {
                    try
                    {
                        bool playerIdNull = owner.playerID == null;
                        parts.Add($"playerIdNull={(playerIdNull ? "true" : "false")}");
                    }
                    catch (System.Exception ex) { parts.Add($"playerIdNull=unknown({ex.GetType().Name})"); }
                }
                else
                {
                    parts.Add("playerIdNull=unknown(notRead)");
                }

                // ===== questsNull =====
                if (playerReadOk && player != null)
                {
                    try
                    {
                        bool questsNull = player.quests == null;
                        parts.Add($"questsNull={(questsNull ? "true" : "false")}");
                    }
                    catch (System.Exception ex) { parts.Add($"questsNull=unknown({ex.GetType().Name})"); }
                }
                else
                {
                    parts.Add("questsNull=unknown(notRead)");
                }

                // ===== help 4 属性（Codex §4.1.2） =====
                // helpFieldCached：启动时反射缓存是否成功
                // helpClrNull：C# null check（ReferenceEquals，避免 == 重载干扰）
                // helpUnityNull：Unity Object == 重载 null check（Transform 是 Unity Object）
                // helpType：实际运行时类型名
                parts.Add($"helpFieldCached={(_helpField != null ? "true" : "false")}");
                if (_helpField != null)
                {
                    object helpVal = null;
                    bool helpReadOk = false;
                    try { helpVal = _helpField.GetValue(instance); helpReadOk = true; }
                    catch (System.Exception ex)
                    {
                        parts.Add($"helpClrNull=unknown({ex.GetType().Name})");
                        parts.Add($"helpUnityNull=unknown({ex.GetType().Name})");
                        parts.Add($"helpType=unknown({ex.GetType().Name})");
                    }
                    if (helpReadOk)
                    {
                        // C# null check（ReferenceEquals 避免 == 重载干扰）
                        bool helpClrNull = object.ReferenceEquals(helpVal, null);
                        parts.Add($"helpClrNull={(helpClrNull ? "true" : "false")}");

                        // Unity null check（Unity Object == 重载）
                        try
                        {
                            bool helpUnityNull;
                            if (helpVal is UnityEngine.Object unityObj)
                            {
                                helpUnityNull = unityObj == null;
                            }
                            else
                            {
                                helpUnityNull = helpClrNull;
                            }
                            parts.Add($"helpUnityNull={(helpUnityNull ? "true" : "false")}");
                        }
                        catch (System.Exception ex) { parts.Add($"helpUnityNull=unknown({ex.GetType().Name})"); }

                        // 类型名
                        try
                        {
                            string typeName = helpClrNull ? "null" : helpVal.GetType().Name;
                            parts.Add($"helpType={typeName}");
                        }
                        catch (System.Exception ex) { parts.Add($"helpType=unknown({ex.GetType().Name})"); }
                    }
                }
                else
                {
                    parts.Add("helpClrNull=unknown(noCache)");
                    parts.Add("helpUnityNull=unknown(noCache)");
                    parts.Add("helpType=unknown(noCache)");
                }

                // ===== dedicated =====
                try
                {
                    bool dedicated = Dedicator.IsDedicatedServer;
                    parts.Add($"dedicated={(dedicated ? "true" : "false")}");
                }
                catch (System.Exception ex) { parts.Add($"dedicated=unknown({ex.GetType().Name})"); }

                // ===== localPlayer =====
                if (channelReadOk && channel != null)
                {
                    try
                    {
                        bool localPlayer = channel.IsLocalPlayer;
                        parts.Add($"localPlayer={(localPlayer ? "true" : "false")}");
                    }
                    catch (System.Exception ex) { parts.Add($"localPlayer=unknown({ex.GetType().Name})"); }
                }
                else
                {
                    parts.Add("localPlayer=unknown(notRead)");
                }

                return string.Join(" ", parts);
            }

            // DP-6: simulate Postfix - isUseable 用属性反射（P0-R3）
            // 签名：public override void simulate(uint simulation, bool inputSteady)
            internal static void SimulatePostfix(UseableBarricade __instance, uint simulation, bool inputSteady)
            {
                try
                {
                    int instanceId = __instance.GetInstanceID();
                    if (!ShouldLog(6, instanceId)) return;

                    bool isUsing = false, isUseable = false;
                    if (_isUsingField != null) try { isUsing = (bool)_isUsingField.GetValue(__instance); } catch { }
                    if (_isUseableProperty != null) try { isUseable = (bool)_isUseableProperty.GetValue(__instance, null); } catch { }
                    // P0-R3: isBusy 直读 player.equipment.isBusy
                    bool isBusy = __instance?.player?.equipment?.isBusy ?? false;

                    bool isLocalPlayer = __instance?.player?.channel?.IsLocalPlayer ?? false;
                    bool isServer = Provider.isServer;
                    bool isDedicated = Dedicator.IsDedicatedServer;

                    // v0.2.3.38 4B 编码：扩展 DP-6 字段用于 4C 离线关联
                    //   - isBuilding/startedUse/pendingBuildHandle：与 DP-7 build 关联，证明 simulate 期间建造状态
                    //   - sessionQuota：证明日志缺席不是配额耗尽导致
                    bool isBuilding = false;
                    float startedUse = 0f;
                    int pendingBuildHandle = -1;
                    if (_isBuildingField != null) try { isBuilding = (bool)_isBuildingField.GetValue(__instance); } catch { }
                    if (_startedUseField != null) try { startedUse = (float)_startedUseField.GetValue(__instance); } catch { }
                    if (_pendingBuildHandleField != null) try { pendingBuildHandle = (int)_pendingBuildHandleField.GetValue(__instance); } catch { }

                    RoleLogger.Info("[Shared]",
                        $"{Label} DP-6 simulate session={_sessionId} role={ResolveInstanceRole(__instance)} "
                        + $"instance={instanceId} sim={simulation} steady={inputSteady} "
                        + $"isLocalPlayer={isLocalPlayer} isServer={isServer} isDedicated={isDedicated} "
                        + $"steamId={GetMaskedSteamId(__instance)} "
                        + $"isUsing={isUsing} isUseable={isUseable} isBusy={isBusy} "
                        + $"isBuilding={isBuilding} startedUse={startedUse:F2} pendingBuildHandle={pendingBuildHandle} "
                        + $"sessionQuota={WorldSyncDiagnosticCore.SessionTotalCount}/{WorldSyncDiagnosticCore.SessionTotalLimit}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"{Label} DP-6 Postfix 异常: {ex.Message}");
                }
            }

            // DP-7: build Prefix+Postfix（P1-R7 新增）
            // 签名：private void build()，无参数
            internal static void BuildPrefix(UseableBarricade __instance, out BuildState __state)
            {
                __state = new BuildState();
                if (_reflectionFailed) return;
                try
                {
                    __state.instanceId = __instance.GetInstanceID();
                    if (_isUsingField != null) try { __state.isUsing = (bool)_isUsingField.GetValue(__instance); } catch { }
                    if (_isBuildingField != null) try { __state.isBuilding = (bool)_isBuildingField.GetValue(__instance); } catch { }
                    if (_startedUseField != null) try { __state.startedUse = (float)_startedUseField.GetValue(__instance); } catch { }

                    RoleLogger.Info("[Shared]",
                        $"{Label} DP-7 build PRE session={_sessionId} role={ResolveInstanceRole(__instance)} "
                        + $"instance={__state.instanceId} "
                        + $"isUsing(before={__state.isUsing}) isBuilding(before={__state.isBuilding}) startedUse={__state.startedUse:F2} "
                        + $"steamId={GetMaskedSteamId(__instance)}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"{Label} DP-7 Prefix 异常: {ex.Message}");
                }
            }

            internal static void BuildPostfix(UseableBarricade __instance, BuildState __state)
            {
                try
                {
                    bool isUsingAfter = false, isBuildingAfter = false;
                    float startedUseAfter = 0f;
                    if (_isUsingField != null) try { isUsingAfter = (bool)_isUsingField.GetValue(__instance); } catch { }
                    if (_isBuildingField != null) try { isBuildingAfter = (bool)_isBuildingField.GetValue(__instance); } catch { }
                    if (_startedUseField != null) try { startedUseAfter = (float)_startedUseField.GetValue(__instance); } catch { }

                    _dp7EventCount++;
                    RoleLogger.Info("[Shared]",
                        $"{Label} DP-7 build POST session={_sessionId} role={ResolveInstanceRole(__instance)} "
                        + $"instance={__state.instanceId} "
                        + $"isUsing(before={__state.isUsing},after={isUsingAfter}) "
                        + $"isBuilding(before={__state.isBuilding},after={isBuildingAfter}) "
                        + $"startedUse(before={__state.startedUse:F2},after={startedUseAfter:F2}) "
                        + $"eventCount={_dp7EventCount}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"{Label} DP-7 Postfix 异常: {ex.Message}");
                }
            }

            // DP-8: BarricadeManager.dropBarricade Prefix+Postfix（P1-R7 新增）
            // 签名：public static Transform dropBarricade(Barricade barricade, Transform hit, Vector3 point, float angle_x, float angle_y, float angle_z, ulong owner, ulong group)
            internal static void DropBarricadePrefix(Barricade barricade, Transform hit, Vector3 point,
                float angle_x, float angle_y, float angle_z, ulong owner, ulong group)
            {
                try
                {
                    _dp8EventCount++;
                    string assetId = "n/a";
                    try
                    {
                        if (barricade != null && barricade.asset != null)
                            assetId = barricade.asset.id.ToString();
                    }
                    catch { }
                    string hitName = hit != null ? hit.name : "null";
                    string ownerMasked = DiagnosticMaskUtil.MaskSteamId(owner);
                    // P0-R8: group 同样是 64 位 Steam 标识符，必须脱敏（0 时输出 0，非 0 时输出脱敏值）
                    string groupMasked = group == 0UL ? "0" : DiagnosticMaskUtil.MaskSteamId(group);
                    string role = Provider.isServer ? "Host" : "Client";

                    RoleLogger.Info("[Shared]",
                        $"{Label} DP-8 dropBarricade PRE session={_sessionId} role={role} "
                        + $"assetId={assetId} hitName={hitName} point={FormatPos(point)} "
                        + $"owner={ownerMasked} group={groupMasked} eventCount={_dp8EventCount}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"{Label} DP-8 Prefix 异常: {ex.Message}");
                }
            }

            internal static void DropBarricadePostfix(Barricade barricade, Transform hit, Vector3 point,
                float angle_x, float angle_y, float angle_z, ulong owner, ulong group,
                Transform __result)
            {
                try
                {
                    string resultStr = __result == null ? "NULL(FAILED)" : $"transform={__result.name}";
                    string role = Provider.isServer ? "Host" : "Client";
                    RoleLogger.Info("[Shared]",
                        $"{Label} DP-8 dropBarricade POST session={_sessionId} role={role} result={resultStr}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"{Label} DP-8 Postfix 异常: {ex.Message}");
                }
            }
        }
    }
}
