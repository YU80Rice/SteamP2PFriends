using SDG.Unturned;
using SteamP2PFriends.Shared;
using Steamworks;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.27 P0-A 决定性诊断核心（Codex 第 7 节 P0-A）：
    /// 为 Item/Zombie/Vehicle/Animal/Object/Resource 六条世界同步链路提供
    /// 统一的限频、脱敏、重置基础设施。
    ///
    /// v0.2.3.27-P0-A 返修（Codex 静态审计 NO-GO 修正）：
    ///   - OnClientDisconnected 字典枚举与删除同锁
    ///   - 新增 RegisterSessionResetCallback / ResetForNewSession：第二局开服时清空所有计数 +
    ///     调用各 patch 注册的计时重置回调（_lastUpdateLogTime = -100f）
    ///   - TryAcquirePlayerQuota 真实使用引导：onRegionUpdated/onBoundUpdated 必须使用此方法
    ///
    /// 严格 diag-only（Codex P0-A 硬门槛）：
    ///   - 不改变方法返回值、参数、连接列表、writer 或 isNetworked
    ///   - 不增加任何 tc.Send、InvokeLoopback 或自定义 RPC
    ///   - 不改变 dedicated/listen 条件
    ///   - 诊断异常不得中断 vanilla 方法（所有 LogXxx 均 try-catch）
    ///   - 按具体接收方法独立限频，并设置会话总上限
    ///   - 断线、退出、第二局开服时重置诊断计数
    ///   - SteamID 只能散列/尾号化（复用 DiagnosticMaskUtil）
    /// </summary>
    public static class WorldSyncDiagnosticCore
    {
        public const int PerPointLimit = 20;
        public const int PerPlayerPointLimit = 10;
        public const int SessionTotalLimit = 500;

        private static int _sessionTotalCount;
        private static readonly object _lock = new object();

        private static readonly Dictionary<string, int> _pointCounts = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> _playerPointCounts = new Dictionary<string, int>();

        /// <summary>
        /// 各 patch 注册的"计时状态重置回调"（如 _lastUpdateLogTime = -100f）。
        /// 由 ResetAll 调用，确保第二局开服时所有时间间隔限频状态归零。
        /// </summary>
        private static readonly List<System.Action> _sessionResetCallbacks = new List<System.Action>();

        /// <summary>
        /// 注册一个会话重置回调（用于重置 patch 内的 _lastUpdateLogTime 等时间间隔限频状态）。
        /// 必须在 patch 类静态构造或 Plugin.Awake 中调用一次。
        /// </summary>
        public static void RegisterSessionResetCallback(System.Action callback)
        {
            if (callback == null) return;
            lock (_lock)
            {
                _sessionResetCallbacks.Add(callback);
            }
        }

        public static bool TryAcquireQuota(string pointId, out int currentCount)
        {
            currentCount = 0;
            try
            {
                lock (_lock)
                {
                    if (_sessionTotalCount >= SessionTotalLimit)
                    {
                        return false;
                    }

                    _pointCounts.TryGetValue(pointId, out currentCount);
                    if (currentCount >= PerPointLimit)
                    {
                        return false;
                    }

                    currentCount++;
                    _pointCounts[pointId] = currentCount;
                    _sessionTotalCount++;
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 玩家级配额：onRegionUpdated/onBoundUpdated 等玩家相关诊断点必须使用此方法，
        /// 否则 _playerPointCounts 始终为空，OnClientDisconnected 清理逻辑无效。
        /// </summary>
        public static bool TryAcquirePlayerQuota(ulong steamId, string pointId, int perPlayerLimit, out int currentCount)
        {
            currentCount = 0;
            try
            {
                if (steamId == 0UL)
                {
                    return false;
                }

                string key = $"{steamId}:{pointId}";
                lock (_lock)
                {
                    if (_sessionTotalCount >= SessionTotalLimit)
                    {
                        return false;
                    }

                    _playerPointCounts.TryGetValue(key, out currentCount);
                    if (currentCount >= perPlayerLimit)
                    {
                        return false;
                    }

                    currentCount++;
                    _playerPointCounts[key] = currentCount;
                    _sessionTotalCount++;
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public static string MaskSteamId(ulong steamId)
        {
            try
            {
                return DiagnosticMaskUtil.MaskSteamId(steamId);
            }
            catch
            {
                return "mask-error";
            }
        }

        public static string MaskSteamId(CSteamID steamId)
        {
            try
            {
                return DiagnosticMaskUtil.MaskSteamId(steamId);
            }
            catch
            {
                return "mask-error";
            }
        }

        /// <summary>
        /// v0.2.3.27-P0-A 返修：断线时清除已不在 Provider.clients 中的玩家计数。
        /// 字典枚举与删除必须置于同一 _lock 中（Codex P1-1）。
        /// </summary>
        public static void OnClientDisconnected()
        {
            try
            {
                if (Provider.clients == null) return;

                var activeSteamIds = new HashSet<ulong>();
                foreach (SteamPlayer sp in Provider.clients)
                {
                    if (sp == null) continue;
                    ulong steamId = 0UL;
                    try
                    {
                        steamId = sp.playerID?.steamID.m_SteamID ?? 0UL;
                    }
                    catch { }
                    if (steamId != 0UL)
                    {
                        activeSteamIds.Add(steamId);
                    }
                }

                var keysToRemove = new List<string>();
                lock (_lock)
                {
                    // 同锁内枚举 + 收集
                    foreach (var key in _playerPointCounts.Keys)
                    {
                        int colonIdx = key.IndexOf(':');
                        if (colonIdx <= 0) continue;
                        string steamIdStr = key.Substring(0, colonIdx);
                        if (ulong.TryParse(steamIdStr, out ulong parsed) && !activeSteamIds.Contains(parsed))
                        {
                            keysToRemove.Add(key);
                        }
                    }
                    // 同锁内删除
                    foreach (var key in keysToRemove)
                    {
                        _playerPointCounts.Remove(key);
                    }
                }

                if (keysToRemove.Count > 0)
                {
                    RoleLogger.Info("[Shared]",
                        $"[WorldSyncDiag] OnClientDisconnected 清除断线玩家计数 ({keysToRemove.Count} 条)");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[WorldSyncDiag] OnClientDisconnected 异常: {ex}");
            }
        }

        /// <summary>
        /// v0.2.3.27-P0-A 返修：开新服/停服时清除所有计数 + 调用各 patch 注册的计时重置回调。
        /// 由 HostManager.StartP2PServer / Plugin.OnDestroy 调用。
        /// </summary>
        public static void ResetAll()
        {
            try
            {
                int pointCleared, playerCleared, callbackCount;
                List<System.Action> callbacksCopy;
                lock (_lock)
                {
                    pointCleared = _pointCounts.Count;
                    playerCleared = _playerPointCounts.Count;
                    _pointCounts.Clear();
                    _playerPointCounts.Clear();
                    _sessionTotalCount = 0;
                    callbacksCopy = new List<System.Action>(_sessionResetCallbacks);
                    callbackCount = callbacksCopy.Count;
                }

                // 在锁外调用回调，避免回调内再次获取锁导致死锁
                foreach (var cb in callbacksCopy)
                {
                    try { cb(); } catch { }
                }

                RoleLogger.Info("[Shared]",
                    $"[WorldSyncDiag] ResetAll 清空所有计数 (points={pointCleared} playerPoints={playerCleared} sessionTotal=0 resetCallbacks={callbackCount})");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[WorldSyncDiag] ResetAll 异常: {ex}");
            }
        }

        public static int SessionTotalCount
        {
            get
            {
                lock (_lock)
                {
                    return _sessionTotalCount;
                }
            }
        }

        /// <summary>
        /// v0.2.3.27-P0-A 返修（Codex TC-S6）：精确 Patch MethodInfo 身份验证。
        /// 不再统计同 owner 数量（InitialStateReceiveDiagnosticPatch 等其他 patch 会使
        /// ReceiveMultipleVehicles 等 owner 计数 &gt;=2），改为检查
        /// "我们自己的 Prefix/Postfix MethodInfo 是否在 patches 列表中"。
        ///
        /// v0.2.3.27-P0-A 冒烟中止返修（Codex P0-R7）：新增 logWhenMissing 参数，
        /// 将"查询身份"与"报告验证失败"拆开。
        ///   - logWhenMissing=false：登记前预检查使用，缺失是预期状态（待登记），不输出 Error
        ///   - logWhenMissing=true（默认）：登记后验证/最终 VerifyRegistration 使用，缺失才输出 Error
        /// </summary>
        public static bool IsPatchRegistered(
            System.Type targetType,
            string methodName,
            MethodInfo patchMethod,
            HarmonyPatchType patchType,
            System.Type[] parameterTypes = null,
            bool logWhenMissing = true)
        {
            if (patchMethod == null)
            {
                if (logWhenMissing)
                {
                    RoleLogger.Error("[Shared]",
                        $"[WorldSyncDiag] IsPatchRegistered patchMethod=null for {targetType.Name}.{methodName}");
                }
                return false;
            }

            MethodInfo original = parameterTypes != null
                ? AccessTools.Method(targetType, methodName, parameterTypes)
                : AccessTools.Method(targetType, methodName);
            if (original == null)
            {
                if (logWhenMissing)
                {
                    RoleLogger.Error("[Shared]",
                        $"[WorldSyncDiag] IsPatchRegistered original not found: {targetType.Name}.{methodName}");
                }
                return false;
            }

            HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
            if (info == null) return false;

            System.Collections.ICollection patches;
            if (patchType == HarmonyPatchType.Prefix) patches = info.Prefixes;
            else if (patchType == HarmonyPatchType.Postfix) patches = info.Postfixes;
            else if (patchType == HarmonyPatchType.Finalizer) patches = info.Finalizers;
            else
            {
                if (logWhenMissing)
                {
                    RoleLogger.Error("[Shared]",
                        $"[WorldSyncDiag] IsPatchRegistered unsupported patchType={patchType}");
                }
                return false;
            }

            int ownerCount = 0;
            foreach (Patch p in patches)
            {
                if (p.owner == SteamP2PFriendsPlugin.HARMONY_ID)
                {
                    ownerCount++;
                    if (p.PatchMethod == patchMethod) return true;
                }
            }

            if (logWhenMissing)
            {
                RoleLogger.Error("[Shared]",
                    $"[WorldSyncDiag] IsPatchRegistered NOT FOUND: {targetType.Name}.{methodName} " +
                    $"{patchType} owner={SteamP2PFriendsPlugin.HARMONY_ID} ownerCount={ownerCount} " +
                    $"expected patch={patchMethod.DeclaringType.Name}.{patchMethod.Name}");
            }
            return false;
        }

        /// <summary>
        /// v0.2.3.27-P0-A 手动登记（Codex 外部审计裁决 P0-R1～R6）：
        /// identity-based 幂等登记。检查"同一 original + 同一 owner + 同一 Patch MethodInfo + 同一 Prefix/Postfix 类型"
        /// 是否已存在：
        ///   - 已存在：记录 SKIP already registered，返回 true（幂等）
        ///   - 不存在：执行 harmony.Patch，然后立即用相同 identity 再验证一次
        ///
        /// P0-R1：参数类型必须完整指定，包括 in ClientInvocationContext -&gt; MakeByRefType()，
        ///        ref bool -&gt; typeof(bool).MakeByRefType()，所有重载必须区分。
        /// P0-R2：identity-based 幂等，不使用"只要该目标上存在任何 Prefix/Postfix 就跳过"模式，
        ///        以免 InitialStateReceiveDiagnosticPatch 等其他同 owner patch 误判。
        /// P0-R3：每个 hook 独立 try/catch，一个失败不阻止其他。
        /// P0-R4：Prefix/Postfix 分别核验，不互相替代。
        /// </summary>
        public static bool RegisterIdentityPatch(
            Harmony harmony,
            System.Type targetType,
            string targetMethodName,
            System.Type[] targetParamTypes,
            MethodInfo patchMethod,
            HarmonyPatchType patchType,
            string label)
        {
            string tag = $"[WorldSyncDiag/Register/{label}]";
            try
            {
                if (harmony == null)
                {
                    RoleLogger.Error("[Shared]", $"{tag} harmony=null");
                    return false;
                }
                if (targetType == null)
                {
                    RoleLogger.Error("[Shared]", $"{tag} targetType=null");
                    return false;
                }
                if (patchMethod == null)
                {
                    RoleLogger.Error("[Shared]", $"{tag} patchMethod=null");
                    return false;
                }

                // P0-R1：完整参数类型解析
                MethodInfo original = targetParamTypes != null
                    ? AccessTools.Method(targetType, targetMethodName, targetParamTypes)
                    : AccessTools.Method(targetType, targetMethodName);
                if (original == null)
                {
                    RoleLogger.Error("[Shared]",
                        $"{tag} original not found: {targetType.FullName}.{targetMethodName} argCount={targetParamTypes?.Length ?? 0}");
                    return false;
                }

                // P0-R1：记录目标的 DeclaringType、Name、返回类型和完整参数类型
                string paramSig = FormatParameterSignature(original);
                RoleLogger.Info("[Shared]",
                    $"{tag} resolved original: {original.DeclaringType.Name}.{original.Name} returnType={original.ReturnType.Name} params=[{paramSig}]");

                // P0-R2 + P0-R7：identity-based 登记前检查（静默，缺失是预期状态，不输出 Error）
                bool alreadyRegistered = IsPatchRegistered(targetType, targetMethodName, patchMethod, patchType, targetParamTypes, logWhenMissing: false);
                if (alreadyRegistered)
                {
                    RoleLogger.Info("[Shared]", $"{tag} SKIP already registered (identity-based)");
                    return true;
                }

                // 执行登记
                if (patchType == HarmonyPatchType.Prefix)
                {
                    harmony.Patch(original, prefix: new HarmonyMethod(patchMethod));
                }
                else if (patchType == HarmonyPatchType.Postfix)
                {
                    harmony.Patch(original, postfix: new HarmonyMethod(patchMethod));
                }
                else if (patchType == HarmonyPatchType.Finalizer)
                {
                    harmony.Patch(original, finalizer: new HarmonyMethod(patchMethod));
                }
                else
                {
                    RoleLogger.Error("[Shared]", $"{tag} 不支持的 patchType={patchType}");
                    return false;
                }

                // P0-R2 + P0-R7：登记后立即用相同 identity 再验证一次（此时缺失才是错误，输出 Error）
                bool verified = IsPatchRegistered(targetType, targetMethodName, patchMethod, patchType, targetParamTypes, logWhenMissing: true);
                if (!verified)
                {
                    RoleLogger.Error("[Shared]",
                        $"{tag} !!! 登记后 identity-based 验证失败");
                    return false;
                }

                RoleLogger.Info("[Shared]", $"{tag} OK 手动登记成功 (identity-based verified)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"{tag} 异常: {ex}");
                return false;
            }
        }

        private static string FormatParameterSignature(MethodInfo method)
        {
            if (method == null) return "null";
            ParameterInfo[] ps = method.GetParameters();
            if (ps.Length == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < ps.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(ps[i].ParameterType.FullName);
            }
            return sb.ToString();
        }
    }
}
