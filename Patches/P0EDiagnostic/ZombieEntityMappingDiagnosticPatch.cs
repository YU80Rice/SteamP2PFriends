using HarmonyLib;
using SDG.NetPak;
using SDG.NetTransport;
using SDG.Unturned;
using SteamP2PFriends.Patches;
using SteamP2PFriends.Shared;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SteamP2PFriends.Patches.P0EDiagnostic
{
    /// <summary>
    ///
    ///
    /// 返修后 7 个诊断点（DP-1..DP-7）：
    ///   DP-1 SendZombies_Write Postfix：主机写包后采样
    ///   DP-2 ReceiveZombies Postfix：客机落地后采样
    ///   DP-3 SendZombieStates_Write Postfix：主机周期包采样
    ///   DP-4 ReceiveZombieStates Postfix：客机周期包采样
    ///   DP-5 onBoundUpdated Prefix+Postfix：struct __state（oldBound/newBound/oldCount/oldPlayerCount/oldIsNet/remoteOccupants/playerDesc）
    ///   DP-6 sendZombieDead + sendZombieAlive Prefix：主机发起事件
    ///   DP-7 ReceiveZombieDead + ReceiveZombieAlive Prefix：客机接收事件
    ///
    ///   sendZombieAlive(Zombie, byte, byte, byte, byte, byte, byte, Vector3, byte) - 9 参数
    ///   ReceiveZombieAlive(byte, ushort, byte, byte, byte, byte, byte, byte, Vector3, byte) - 10 参数
    ///
    /// </summary>
    public static class ZombieEntityMappingDiagnosticPatch
    {
        private const string Label = "[P0-E-1-Diag/Zombie]";

        // ===== Registration state (7 DPs + DP-8.7) =====
        public static bool DP1_SendZombiesWrite_Registered { get; private set; }
        public static bool DP2_ReceiveZombies_Registered { get; private set; }
        public static bool DP3_SendZombieStatesWrite_Registered { get; private set; }
        public static bool DP4_ReceiveZombieStates_Registered { get; private set; }
        public static bool DP5_OnBoundUpdated_Registered { get; private set; }
        public static bool DP6_SendZombieDead_Registered { get; private set; }
        public static bool DP7_ReceiveZombieDead_Registered { get; private set; }

        // 登记与 owner 精确自检状态分离，二者任一失败都 fail-closed。
        public static bool DP8_7_Destroy_Registered { get; private set; }
        public static bool DP8_7_Destroy_OwnerVerified { get; private set; }
        public static string DP8_7_Destroy_OwnerSummary { get; private set; } = "<unverified>";

        // Plugin 不得再次反射读取 _reflectionFailed，只能读取此属性。
        public static bool ReflectionFailed => _reflectionFailed;

        // - 登记成功不等于 owner 精确自检成功
        // - 反射失败时所有 Zombie DP（含 DP-8.7）不登记
        // - 任一登记或 owner 自检失败都令 DiagnosticBuildValid=false
        public static bool AllRegistrationsSucceeded =>
            DP1_SendZombiesWrite_Registered && DP2_ReceiveZombies_Registered
            && DP3_SendZombieStatesWrite_Registered && DP4_ReceiveZombieStates_Registered
            && DP5_OnBoundUpdated_Registered && DP6_SendZombieDead_Registered
            && DP7_ReceiveZombieDead_Registered
            && DP8_7_Destroy_Registered && DP8_7_Destroy_OwnerVerified
            && !_reflectionFailed;

        private const int MAX_SAMPLE_PER_BOUND = 10;
        private const float PERIODIC_THROTTLE = 5.0f;

        private static readonly Dictionary<(int dp, byte bound), float> _lastPeriodicLog
            = new Dictionary<(int dp, byte bound), float>();

        private static int _sessionId = 0;
        public static int CurrentSessionId => _sessionId;

        // ===== Per-bound sample index cache =====
        private static readonly Dictionary<byte, int[]> _sampleIndices = new Dictionary<byte, int[]>();

        // 不是 field。原 AccessTools.Field 返回 null 导致 CacheReflection fail-closed，
        // 7 个 DP 全部未登记 -> DiagnosticBuildValid=false -> P2P 入口被阻断。
        // U3-SDK 证据：U3-SDK/Assets/Runtime/Assembly-CSharp/Unturned/Managers/ZombieRegion.cs:358-387
        //   public int PlayerCountInRegion { get => _playerCountInRegion; internal set { ... } }
        private static FieldInfo _isNetworkedField;
        private static PropertyInfo _playerCountInRegionProperty;
        private static bool _reflectionCached;
        private static bool _reflectionFailed;

        static ZombieEntityMappingDiagnosticPatch()
        {
            WorldSyncDiagnosticCore.RegisterSessionResetCallback(OnSessionReset);
        }

        private static void OnSessionReset()
        {
            int oldSession = _sessionId;
            _sessionId++;
            _lastPeriodicLog.Clear();
            _sampleIndices.Clear();
            RoleLogger.Info("[Shared]",
                $"{Label} RESET oldSession={oldSession} newSession={_sessionId} reason=WorldSyncDiagnosticCore.ResetAll");
        }

        private static void CacheReflection()
        {
            if (_reflectionCached) return;
            _reflectionCached = true;
            try
            {
                _isNetworkedField = AccessTools.Field(typeof(ZombieRegion), "isNetworked");
                _playerCountInRegionProperty = AccessTools.Property(typeof(ZombieRegion), "PlayerCountInRegion");
                if (_isNetworkedField == null || _playerCountInRegionProperty == null)
                {
                    RoleLogger.Error("[Shared]",
                        $"{Label} !!! CacheReflection 失败：isNetworked={_isNetworkedField != null} "
                        + $"PlayerCountInRegion={_playerCountInRegionProperty != null}");
                    _reflectionFailed = true;
                }
                else
                {
                    RoleLogger.Info("[Shared]", $"{Label} CacheReflection OK：所有字段已缓存");
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

            RoleLogger.Info("[Shared]", $"{Label} === 阶段 2 返修后诊断补丁登记开始 (7 DPs, identity-based) ===");

            if (harmony == null)
            {
                RoleLogger.Error("[Shared]", $"{Label} !!! harmony=null");
                return false;
            }

            if (_reflectionFailed)
            {
                RoleLogger.Error("[Shared]",
                    $"{Label} !!! reflectionFailed=true，按 fail-closed 不登记任何 DP（含 DP-8.7）");
                DP1_SendZombiesWrite_Registered = DP2_ReceiveZombies_Registered
                    = DP3_SendZombieStatesWrite_Registered = DP4_ReceiveZombieStates_Registered
                    = DP5_OnBoundUpdated_Registered = DP6_SendZombieDead_Registered
                    = DP7_ReceiveZombieDead_Registered = false;
                DP8_7_Destroy_Registered = false;
                DP8_7_Destroy_OwnerVerified = false;
                return false;
            }

            // DP-1 SendZombies_Write Postfix
            System.Type[] sendZombiesWriteParams = { typeof(NetPakWriter), typeof(byte) };
            DP1_SendZombiesWrite_Registered = RegisterOne(harmony, "SendZombies_Write",
                sendZombiesWriteParams, nameof(Hooks.SendZombiesWritePostfix),
                HarmonyPatchType.Postfix, "DP-1-SendZombies_Write-Postfix");

            // DP-2 ReceiveZombies Postfix
            System.Type[] receiveZombiesParams = { typeof(ClientInvocationContext).MakeByRefType() };
            DP2_ReceiveZombies_Registered = RegisterOne(harmony, "ReceiveZombies",
                receiveZombiesParams, nameof(Hooks.ReceiveZombiesPostfix),
                HarmonyPatchType.Postfix, "DP-2-ReceiveZombies-Postfix");

            // DP-3 SendZombieStates_Write Postfix
            System.Type[] sendZombieStatesWriteParams = { typeof(NetPakWriter), typeof(byte) };
            DP3_SendZombieStatesWrite_Registered = RegisterOne(harmony, "SendZombieStates_Write",
                sendZombieStatesWriteParams, nameof(Hooks.SendZombieStatesWritePostfix),
                HarmonyPatchType.Postfix, "DP-3-SendZombieStates_Write-Postfix");

            // DP-4 ReceiveZombieStates Postfix
            System.Type[] receiveZombieStatesParams = { typeof(ClientInvocationContext).MakeByRefType() };
            DP4_ReceiveZombieStates_Registered = RegisterOne(harmony, "ReceiveZombieStates",
                receiveZombieStatesParams, nameof(Hooks.ReceiveZombieStatesPostfix),
                HarmonyPatchType.Postfix, "DP-4-ReceiveZombieStates-Postfix");

            // DP-5 onBoundUpdated Prefix + Postfix
            System.Type[] onBoundUpdatedParams = { typeof(Player), typeof(byte), typeof(byte) };
            DP5_OnBoundUpdated_Registered = RegisterOne(harmony, "onBoundUpdated",
                onBoundUpdatedParams, nameof(Hooks.OnBoundUpdatedPrefix),
                HarmonyPatchType.Prefix, "DP-5-onBoundUpdated-Prefix")
                && RegisterOne(harmony, "onBoundUpdated",
                onBoundUpdatedParams, nameof(Hooks.OnBoundUpdatedPostfix),
                HarmonyPatchType.Postfix, "DP-5-onBoundUpdated-Postfix");

            // DP-6 sendZombieDead + sendZombieAlive Prefix
            System.Type[] sendZombieDeadParams = { typeof(Zombie), typeof(Vector3), typeof(ERagdollEffect) };
            System.Type[] sendZombieAliveParams = {
                typeof(Zombie), typeof(byte), typeof(byte), typeof(byte),
                typeof(byte), typeof(byte), typeof(byte), typeof(Vector3), typeof(byte)
            };
            DP6_SendZombieDead_Registered = RegisterOne(harmony, "sendZombieDead",
                sendZombieDeadParams, nameof(Hooks.SendZombieDeadPrefix),
                HarmonyPatchType.Prefix, "DP-6-sendZombieDead-Prefix")
                && RegisterOne(harmony, "sendZombieAlive",
                sendZombieAliveParams, nameof(Hooks.SendZombieAlivePrefix),
                HarmonyPatchType.Prefix, "DP-6-sendZombieAlive-Prefix");

            // DP-7 ReceiveZombieDead + ReceiveZombieAlive Prefix
            System.Type[] receiveZombieDeadParams = { typeof(byte), typeof(ushort), typeof(Vector3), typeof(ERagdollEffect) };
            System.Type[] receiveZombieAliveParams = {
                typeof(byte), typeof(ushort), typeof(byte), typeof(byte),
                typeof(byte), typeof(byte), typeof(byte), typeof(byte),
                typeof(Vector3), typeof(byte)
            };
            DP7_ReceiveZombieDead_Registered = RegisterOne(harmony, "ReceiveZombieDead",
                receiveZombieDeadParams, nameof(Hooks.ReceiveZombieDeadPrefix),
                HarmonyPatchType.Prefix, "DP-7-ReceiveZombieDead-Prefix")
                && RegisterOne(harmony, "ReceiveZombieAlive",
                receiveZombieAliveParams, nameof(Hooks.ReceiveZombieAlivePrefix),
                HarmonyPatchType.Prefix, "DP-7-ReceiveZombieAlive-Prefix");

            //   DP-8.7 ZombieRegion.destroy Prefix - 销毁前快照（zombies.Count/nav/isNetworked/PlayerCountInRegion/remoteOccupants）
            //   唯一新增 Hook，仅读取销毁前状态，不修改 Region 生命周期、Zombie 列表、PlayerCount 或 isNetworked。
            //   C1：MethodInfo 必须从实际嵌套 Hooks 类解析（不得照抄外层类型示例）。
            //   F1：登记成功 != owner 精确自检成功；二者任一失败都 fail-closed。
            //   F4：所需成员均为 ZombieRegion 公开成员（zombies/nav/isNetworked/PlayerCountInRegion），不新增反射。
            System.Type[] destroyParams = System.Type.EmptyTypes;
            DP8_7_Destroy_Registered = RegisterOneForZombieRegion(harmony, "destroy",
                destroyParams, nameof(Hooks.DP8_7_Destroy_Prefix),
                HarmonyPatchType.Prefix, "DP-8.7-ZombieRegion.destroy-Prefix");
            VerifyDP8_7Owner(harmony);

            bool ok = AllRegistrationsSucceeded;
            RoleLogger.Info("[Shared]",
                $"{Label} === 阶段 4B 诊断补丁登记完成 ok={ok} "
                + $"DP1={DP1_SendZombiesWrite_Registered} DP2={DP2_ReceiveZombies_Registered} "
                + $"DP3={DP3_SendZombieStatesWrite_Registered} DP4={DP4_ReceiveZombieStates_Registered} "
                + $"DP5={DP5_OnBoundUpdated_Registered} DP6={DP6_SendZombieDead_Registered} "
                + $"DP7={DP7_ReceiveZombieDead_Registered} "
                + $"DP8_7={DP8_7_Destroy_Registered} owner8_7={DP8_7_Destroy_OwnerVerified} "
                + $"reflectionFailed={_reflectionFailed} ===");
            return ok;
        }

        /// <summary>
        /// 目标类型为 ZombieRegion（非 ZombieManager），故不复用 RegisterOne。
        /// MethodInfo 必须从实际嵌套 Hooks 类解析，不得照抄外层类型示例。
        /// </summary>
        private static bool RegisterOneForZombieRegion(Harmony harmony, string methodName, System.Type[] paramTypes,
            string hookName, HarmonyPatchType patchType, string label)
        {
            try
            {
                MethodInfo hook = typeof(Hooks).GetMethod(hookName, BindingFlags.Static | BindingFlags.NonPublic);
                if (hook == null)
                {
                    RoleLogger.Error("[Shared]", $"{Label} !!! {label} hook MethodInfo 未找到: {hookName}");
                    return false;
                }
                return WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(ZombieRegion), methodName, paramTypes, hook, patchType, label);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"{Label} !!! {label} 登记异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 使用精确 MethodInfo 比较，允许同一 owner 的其他合法 Prefix 共存。
        /// owner 自检失败必然令 AllRegistrationsSucceeded=false。
        /// </summary>
        private static void VerifyDP8_7Owner(Harmony harmony)
        {
            try
            {
                MethodInfo original = AccessTools.Method(typeof(ZombieRegion), "destroy");
                MethodInfo expectedPrefix = typeof(Hooks).GetMethod(
                    nameof(Hooks.DP8_7_Destroy_Prefix), BindingFlags.Static | BindingFlags.NonPublic);
                DP8_7_Destroy_OwnerVerified = VerifyPatchOwnerExact(original, expectedPrefix, out string summary);
                DP8_7_Destroy_OwnerSummary = summary;
                if (!DP8_7_Destroy_OwnerVerified)
                {
                    RoleLogger.Error("[Shared]", $"{Label} !!! DP-8.7 owner 自检失败: {summary}");
                }
                else
                {
                    RoleLogger.Info("[Shared]", $"{Label} DP-8.7 owner 自检 OK: {summary}");
                }
            }
            catch (System.Exception ex)
            {
                DP8_7_Destroy_OwnerVerified = false;
                DP8_7_Destroy_OwnerSummary = $"exception: {ex.Message}";
                RoleLogger.Error("[Shared]", $"{Label} DP-8.7 owner 自检异常: {ex.Message}");
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
            System.Collections.ICollection patches = info?.Prefixes;
            if (patches == null || patches.Count == 0)
            {
                summaryOut = "prefixes=0";
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
            string hookName, HarmonyPatchType patchType, string label)
        {
            try
            {
                MethodInfo hook = typeof(Hooks).GetMethod(hookName, BindingFlags.Static | BindingFlags.NonPublic);
                if (hook == null)
                {
                    RoleLogger.Error("[Shared]", $"{Label} !!! {label} hook MethodInfo 未找到: {hookName}");
                    return false;
                }
                return WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(ZombieManager), methodName, paramTypes, hook, patchType, label);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"{Label} !!! {label} 登记异常: {ex.Message}");
                return false;
            }
        }

        // ====================== Helpers ======================

        private static string ProcessRole()
        {
            if (Provider.isServer) return "Host";
            if (Provider.isClient) return "Client";
            return "Shared";
        }

        private static int[] GetSampleIndices(byte bound, int totalCount)
        {
            if (totalCount <= 0) return System.Array.Empty<int>();
            if (_sampleIndices.TryGetValue(bound, out int[] cached) && cached != null && cached.Length > 0)
            {
                if (cached[cached.Length - 1] == totalCount - 1) return cached;
            }
            var list = new List<int>();
            for (int i = 0; i < 5 && i < totalCount; i++) list.Add(i);
            if (totalCount > 5)
            {
                list.Add(totalCount - 1);
                if (totalCount > 6) list.Add(totalCount / 2);
            }
            int[] arr = list.ToArray();
            _sampleIndices[bound] = arr;
            return arr;
        }

        private static string FormatEntitySignature(Zombie z)
        {
            if (z == null) return "null";
            try
            {
                Vector3 p = z.transform.position;
                // 支持 4C "机长服装 vs 平民服装" 分析。
                // U3-SDK Zombie.cs:67-74 公开字段：id(ushort)/type(byte)/speciality(EZombieSpeciality)/
                //   shirt(byte)/pants(byte)/hat(byte)/gear(byte)；:362 isDead(bool)
                return $"id={z.id} type={z.type} spec={z.speciality} dead={z.isDead} "
                    + $"pos=({p.x:F1},{p.y:F1},{p.z:F1}) "
                    + $"shirt={z.shirt} pants={z.pants} hat={z.hat} gear={z.gear}";
            }
            catch
            {
                return $"id={z?.id ?? 0} <error>";
            }
        }

        /// <summary>
        /// 复用 GetSampleIndices 与 FormatEntitySignature，不新增反射、不新增 Tick。
        /// </summary>
        private static string FormatRegionEntitySnapshot(byte bound, int totalCount, List<Zombie> zombies, int maxSamples = 10)
        {
            if (totalCount <= 0 || zombies == null) return "count=0 samples=[]";
            int[] indices = GetSampleIndices(bound, totalCount);
            var sb = new System.Text.StringBuilder();
            sb.Append($"samples=[");
            int written = 0;
            for (int i = 0; i < indices.Length && written < maxSamples; i++)
            {
                int idx = indices[i];
                if (idx >= 0 && idx < totalCount && idx < zombies.Count)
                {
                    if (written > 0) sb.Append("; ");
                    sb.Append($"[{idx}] {FormatEntitySignature(zombies[idx])}");
                    written++;
                }
            }
            sb.Append("]");
            return $"count={totalCount} {sb}";
        }

        private static bool ShouldLogPeriodic(int dpId, byte bound)
        {
            float now = Time.realtimeSinceStartup;
            var key = (dpId, bound);
            if (_lastPeriodicLog.TryGetValue(key, out float t) && now - t < PERIODIC_THROTTLE) return false;
            _lastPeriodicLog[key] = now;
            return true;
        }

        private static bool ReadRegionIsNetworked(ZombieRegion region)
        {
            if (region == null || _isNetworkedField == null) return false;
            try { return (bool)_isNetworkedField.GetValue(region); } catch { return false; }
        }

        private static int ReadPlayerCountInRegion(ZombieRegion region)
        {
            if (region == null || _playerCountInRegionProperty == null) return -1;
            try { return (int)_playerCountInRegionProperty.GetValue(region, null); } catch { return -1; }
        }


        private struct OnBoundUpdatedState
        {
            public byte oldBound;
            public byte newBound;
            public int oldCount;
            public int oldPlayerCount;
            public bool oldIsNet;
            public int remoteOccupants;
            public string playerDesc;
        }

        // ====================== Hooks ======================

        private static class Hooks
        {
            // DP-1: SendZombies_Write Postfix - 主机端写入后采样
            // 签名：private static void SendZombies_Write(NetPakWriter writer, byte bound)
            internal static void SendZombiesWritePostfix(NetPakWriter writer, byte bound)
            {
                try
                {
                    if (!Provider.isServer) return;
                    if (!ShouldLogPeriodic(1, bound)) return;

                    ZombieRegion region = null;
                    int count = 0;
                    try
                    {
                        if (ZombieManager.regions != null && bound < ZombieManager.regions.Length)
                        {
                            region = ZombieManager.regions[bound];
                            count = region?.zombies?.Count ?? 0;
                        }
                    }
                    catch { }

                    int[] indices = GetSampleIndices(bound, count);
                    var sigs = new System.Text.StringBuilder();
                    for (int i = 0; i < indices.Length; i++)
                    {
                        int idx = indices[i];
                        if (idx >= 0 && idx < count)
                        {
                            sigs.Append($"[{idx}] {FormatEntitySignature(region.zombies[idx])}; ");
                        }
                    }

                    RoleLogger.Info("[Shared]",
                        $"{Label} DP-1 SendZombies_Write session={CurrentSessionId} role={ProcessRole()} "
                        + $"bound={bound} count={count} samples={sigs} "
                        + $"sessionQuota={WorldSyncDiagnosticCore.SessionTotalCount}/{WorldSyncDiagnosticCore.SessionTotalLimit}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"{Label} DP-1 Postfix 异常: {ex.Message}");
                }
            }

            // DP-2: ReceiveZombies Postfix - 客机端 addZombie 后采样
            // 签名：public static void ReceiveZombies(in ClientInvocationContext context)
            internal static void ReceiveZombiesPostfix()
            {
                try
                {
                    if (Provider.isServer) return;
                    if (ZombieManager.regions == null) return;

                    for (byte bound = 0; bound < ZombieManager.regions.Length; bound++)
                    {
                        ZombieRegion region = ZombieManager.regions[bound];
                        if (region == null) continue;
                        try
                        {
                            bool isNet = ReadRegionIsNetworked(region);
                            if (!isNet) continue;

                            int count = region.zombies?.Count ?? 0;
                            if (count == 0) continue;

                            if (!ShouldLogPeriodic(2, bound)) continue;

                            int[] indices = GetSampleIndices(bound, count);
                            var sigs = new System.Text.StringBuilder();
                            for (int i = 0; i < indices.Length; i++)
                            {
                                int idx = indices[i];
                                if (idx >= 0 && idx < count)
                                {
                                    sigs.Append($"[{idx}] {FormatEntitySignature(region.zombies[idx])}; ");
                                }
                            }

                            RoleLogger.Info("[Shared]",
                                $"{Label} DP-2 ReceiveZombies session={CurrentSessionId} role={ProcessRole()} "
                                + $"bound={bound} count={count} samples={sigs} "
                                + $"sessionQuota={WorldSyncDiagnosticCore.SessionTotalCount}/{WorldSyncDiagnosticCore.SessionTotalLimit}");
                        }
                        catch { }
                    }
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"{Label} DP-2 Postfix 异常: {ex.Message}");
                }
            }

            // DP-3: SendZombieStates_Write Postfix - 主机端写入后采样已更新僵尸
            // 签名：private void SendZombieStates_Write(NetPakWriter writer, byte regionIndex)
            internal static void SendZombieStatesWritePostfix(byte regionIndex)
            {
                try
                {
                    if (!Provider.isServer) return;
                    if (!ShouldLogPeriodic(3, regionIndex)) return;

                    ZombieRegion region = null;
                    int count = 0;
                    try
                    {
                        if (ZombieManager.regions != null && regionIndex < ZombieManager.regions.Length)
                        {
                            region = ZombieManager.regions[regionIndex];
                            count = region?.zombies?.Count ?? 0;
                        }
                    }
                    catch { }

                    var sigs = new System.Text.StringBuilder();
                    int sampled = 0;
                    if (region != null && region.zombies != null)
                    {
                        for (int i = 0; i < count && sampled < MAX_SAMPLE_PER_BOUND; i++)
                        {
                            Zombie z = region.zombies[i];
                            if (z != null && z.isUpdated)
                            {
                                sigs.Append($"[{i}] {FormatEntitySignature(z)}; ");
                                sampled++;
                            }
                        }
                    }

                    RoleLogger.Info("[Shared]",
                        $"{Label} DP-3 SendZombieStates_Write session={CurrentSessionId} role={ProcessRole()} "
                        + $"bound={regionIndex} count={count} updatedSampled={sampled} samples={sigs}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"{Label} DP-3 Postfix 异常: {ex.Message}");
                }
            }

            // DP-4: ReceiveZombieStates Postfix - 客机端落地后采样
            // 签名：public static void ReceiveZombieStates(in ClientInvocationContext context)
            internal static void ReceiveZombieStatesPostfix()
            {
                try
                {
                    if (Provider.isServer) return;
                    if (ZombieManager.regions == null) return;

                    for (byte bound = 0; bound < ZombieManager.regions.Length; bound++)
                    {
                        ZombieRegion region = ZombieManager.regions[bound];
                        if (region == null) continue;
                        try
                        {
                            bool isNet = ReadRegionIsNetworked(region);
                            if (!isNet) continue;

                            int count = region.zombies?.Count ?? 0;
                            if (count == 0) continue;

                            if (!ShouldLogPeriodic(4, bound)) continue;

                            int[] indices = GetSampleIndices(bound, count);
                            var sigs = new System.Text.StringBuilder();
                            for (int i = 0; i < indices.Length; i++)
                            {
                                int idx = indices[i];
                                if (idx >= 0 && idx < count)
                                {
                                    sigs.Append($"[{idx}] {FormatEntitySignature(region.zombies[idx])}; ");
                                }
                            }

                            RoleLogger.Info("[Shared]",
                                $"{Label} DP-4 ReceiveZombieStates session={CurrentSessionId} role={ProcessRole()} "
                                + $"bound={bound} count={count} samples={sigs}");
                        }
                        catch { }
                    }
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"{Label} DP-4 Postfix 异常: {ex.Message}");
                }
            }

            // 签名：private void onBoundUpdated(Player player, byte oldBound, byte newBound)
            internal static void OnBoundUpdatedPrefix(Player player, byte oldBound, byte newBound,
                out OnBoundUpdatedState __state)
            {
                __state = new OnBoundUpdatedState();
                if (_reflectionFailed) return;
                try
                {
                    __state.oldBound = oldBound;
                    __state.newBound = newBound;

                    // before 状态
                    __state.oldCount = -1;
                    __state.oldPlayerCount = -1;
                    __state.oldIsNet = false;
                    try
                    {
                        if (ZombieManager.regions != null && oldBound < ZombieManager.regions.Length)
                        {
                            ZombieRegion r = ZombieManager.regions[oldBound];
                            if (r != null)
                            {
                                __state.oldCount = r.zombies?.Count ?? -1;
                                __state.oldPlayerCount = ReadPlayerCountInRegion(r);
                                __state.oldIsNet = ReadRegionIsNetworked(r);
                            }
                        }
                    }
                    catch { }

                    // 远端占用情况
                    __state.remoteOccupants = 0;
                    try
                    {
                        if (Provider.isServer && Provider.clients != null)
                        {
                            foreach (SteamPlayer sp in Provider.clients)
                            {
                                if (sp == null || sp.player == null || sp.player.movement == null) continue;
                                if (sp.player.channel?.IsLocalPlayer ?? false) continue;
                                if (sp.player.movement.bound == oldBound)
                                {
                                    __state.remoteOccupants++;
                                }
                            }
                        }
                    }
                    catch { }

                    // 玩家描述
                    __state.playerDesc = "unknown";
                    try
                    {
                        if (player != null)
                        {
                            bool isLocal = player.channel?.IsLocalPlayer ?? false;
                            ulong sid = player.channel?.owner?.playerID?.steamID.m_SteamID ?? 0UL;
                            __state.playerDesc = $"isLocal={isLocal} steamId={DiagnosticMaskUtil.MaskSteamId(sid)}";
                        }
                    }
                    catch { }

                    // 复用 GetSampleIndices + FormatEntitySignature，不新增 Tick/反射。
                    string oldBoundSnapshot = "<unavailable>";
                    try
                    {
                        if (ZombieManager.regions != null && oldBound < ZombieManager.regions.Length)
                        {
                            ZombieRegion r = ZombieManager.regions[oldBound];
                            if (r != null && r.zombies != null)
                            {
                                oldBoundSnapshot = FormatRegionEntitySnapshot(oldBound, r.zombies.Count, r.zombies, 10);
                            }
                        }
                    }
                    catch { }

                    RoleLogger.Info("[Shared]",
                        $"{Label} DP-5 onBoundUpdated PRE session={CurrentSessionId} role={ProcessRole()} "
                        + $"player={__state.playerDesc} oldBound={oldBound} newBound={newBound} "
                        + $"oldRegion(count={__state.oldCount}, playerCount={__state.oldPlayerCount}, isNet={__state.oldIsNet}) "
                        + $"remoteOccupantsInOldBound={__state.remoteOccupants} "
                        + $"oldBoundEntitySnapshot={oldBoundSnapshot} "
                        + $"sessionQuota={WorldSyncDiagnosticCore.SessionTotalCount}/{WorldSyncDiagnosticCore.SessionTotalLimit}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"{Label} DP-5 Prefix 异常: {ex.Message}");
                }
            }

            internal static void OnBoundUpdatedPostfix(Player player, byte oldBound, byte newBound,
                OnBoundUpdatedState __state)
            {
                try
                {
                    int newCount = -1, newPlayerCount = -1;
                    bool newIsNet = false;
                    try
                    {
                        if (ZombieManager.regions != null && newBound < ZombieManager.regions.Length)
                        {
                            ZombieRegion r = ZombieManager.regions[newBound];
                            if (r != null)
                            {
                                newCount = r.zombies?.Count ?? -1;
                                newPlayerCount = ReadPlayerCountInRegion(r);
                                newIsNet = ReadRegionIsNetworked(r);
                            }
                        }
                    }
                    catch { }

                    // 也读取 oldBound 的 after 状态（关键：房主离区后是否 destroy）
                    int oldCountAfter = -1, oldPlayerCountAfter = -1;
                    bool oldIsNetAfter = false;
                    try
                    {
                        if (ZombieManager.regions != null && oldBound < ZombieManager.regions.Length)
                        {
                            ZombieRegion r = ZombieManager.regions[oldBound];
                            if (r != null)
                            {
                                oldCountAfter = r.zombies?.Count ?? -1;
                                oldPlayerCountAfter = ReadPlayerCountInRegion(r);
                                oldIsNetAfter = ReadRegionIsNetworked(r);
                            }
                        }
                    }
                    catch { }

                    string newBoundSnapshot = "<unavailable>";
                    try
                    {
                        if (ZombieManager.regions != null && newBound < ZombieManager.regions.Length)
                        {
                            ZombieRegion r = ZombieManager.regions[newBound];
                            if (r != null && r.zombies != null)
                            {
                                newBoundSnapshot = FormatRegionEntitySnapshot(newBound, r.zombies.Count, r.zombies, 10);
                            }
                        }
                    }
                    catch { }

                    RoleLogger.Info("[Shared]",
                        $"{Label} DP-5 onBoundUpdated POST session={CurrentSessionId} role={ProcessRole()} "
                        + $"player={__state.playerDesc} oldBound={oldBound} newBound={newBound} "
                        + $"oldRegion(before:count={__state.oldCount},playerCount={__state.oldPlayerCount},isNet={__state.oldIsNet}; "
                        + $"after:count={oldCountAfter},playerCount={oldPlayerCountAfter},isNet={oldIsNetAfter}) "
                        + $"newRegion(after:count={newCount},playerCount={newPlayerCount},isNet={newIsNet}) "
                        + $"remoteOccupantsInOldBound(before={__state.remoteOccupants}) "
                        + $"newBoundEntitySnapshot={newBoundSnapshot} "
                        + $"sessionQuota={WorldSyncDiagnosticCore.SessionTotalCount}/{WorldSyncDiagnosticCore.SessionTotalLimit}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"{Label} DP-5 Postfix 异常: {ex.Message}");
                }
            }

            // DP-6: sendZombieDead Prefix - 主机端发起事件
            // 签名：public static void sendZombieDead(Zombie zombie, Vector3 newRagdoll, ERagdollEffect newRagdollEffect)
            internal static void SendZombieDeadPrefix(Zombie zombie, Vector3 newRagdoll, ERagdollEffect newRagdollEffect)
            {
                try
                {
                    if (!Provider.isServer) return;
                    if (zombie == null) return;

                    Vector3 p = zombie.transform.position;
                    RoleLogger.Info("[Shared]",
                        $"{Label} DP-6 sendZombieDead session={CurrentSessionId} role={ProcessRole()} "
                        + $"bound={zombie.bound} id={zombie.id} dead={zombie.isDead} "
                        + $"pos=({p.x:F1},{p.y:F1},{p.z:F1}) ragdoll=({newRagdoll.x:F1},{newRagdoll.y:F1},{newRagdoll.z:F1}) effect={newRagdollEffect}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"{Label} DP-6 sendZombieDead Prefix 异常: {ex.Message}");
                }
            }

            // DP-6: sendZombieAlive Prefix - 主机端发起事件
            internal static void SendZombieAlivePrefix(Zombie zombie, byte newType, byte newSpeciality,
                byte newShirt, byte newPants, byte newHat, byte newGear,
                Vector3 newPosition, byte newAngle)
            {
                try
                {
                    if (!Provider.isServer) return;
                    if (zombie == null) return;

                    Vector3 p = zombie.transform.position;
                    RoleLogger.Info("[Shared]",
                        $"{Label} DP-6 sendZombieAlive session={CurrentSessionId} role={ProcessRole()} "
                        + $"bound={zombie.bound} id={zombie.id} dead={zombie.isDead} "
                        + $"pos=({p.x:F1},{p.y:F1},{p.z:F1}) newPos=({newPosition.x:F1},{newPosition.y:F1},{newPosition.z:F1}) "
                        + $"type={newType} spec={newSpeciality} angle={newAngle}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"{Label} DP-6 sendZombieAlive Prefix 异常: {ex.Message}");
                }
            }

            // DP-7: ReceiveZombieDead Prefix - 客机端接收事件
            // 签名：public static void ReceiveZombieDead(byte reference, ushort id, Vector3 newRagdoll, ERagdollEffect newRagdollEffect)
            internal static void ReceiveZombieDeadPrefix(byte reference, ushort id, Vector3 newRagdoll, ERagdollEffect newRagdollEffect)
            {
                try
                {
                    if (Provider.isServer) return;

                    int count = -1;
                    Vector3 currentPos = Vector3.zero;
                    bool currentDead = false;
                    try
                    {
                        if (ZombieManager.regions != null && reference < ZombieManager.regions.Length)
                        {
                            ZombieRegion r = ZombieManager.regions[reference];
                            count = r?.zombies?.Count ?? -1;
                            if (r != null && r.zombies != null && id < r.zombies.Count)
                            {
                                Zombie z = r.zombies[id];
                                if (z != null)
                                {
                                    currentPos = z.transform.position;
                                    currentDead = z.isDead;
                                }
                            }
                        }
                    }
                    catch { }

                    bool idValid = id < count;

                    RoleLogger.Info("[Shared]",
                        $"{Label} DP-7 ReceiveZombieDead session={CurrentSessionId} role={ProcessRole()} "
                        + $"bound={reference} id={id} count={count} idValid={idValid} "
                        + $"currentPos=({currentPos.x:F1},{currentPos.y:F1},{currentPos.z:F1}) currentDead={currentDead} "
                        + $"newRagdoll=({newRagdoll.x:F1},{newRagdoll.y:F1},{newRagdoll.z:F1}) effect={newRagdollEffect}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"{Label} DP-7 ReceiveZombieDead Prefix 异常: {ex.Message}");
                }
            }

            // DP-7: ReceiveZombieAlive Prefix - 客机端接收事件
            internal static void ReceiveZombieAlivePrefix(byte reference, ushort id, byte newType, byte newSpeciality,
                byte newShirt, byte newPants, byte newHat, byte newGear,
                Vector3 newPosition, byte newAngle)
            {
                try
                {
                    if (Provider.isServer) return;

                    int count = -1;
                    Vector3 currentPos = Vector3.zero;
                    bool currentDead = false;
                    try
                    {
                        if (ZombieManager.regions != null && reference < ZombieManager.regions.Length)
                        {
                            ZombieRegion r = ZombieManager.regions[reference];
                            count = r?.zombies?.Count ?? -1;
                            if (r != null && r.zombies != null && id < r.zombies.Count)
                            {
                                Zombie z = r.zombies[id];
                                if (z != null)
                                {
                                    currentPos = z.transform.position;
                                    currentDead = z.isDead;
                                }
                            }
                        }
                    }
                    catch { }

                    bool idValid = id < count;

                    RoleLogger.Info("[Shared]",
                        $"{Label} DP-7 ReceiveZombieAlive session={CurrentSessionId} role={ProcessRole()} "
                        + $"bound={reference} id={id} count={count} idValid={idValid} "
                        + $"currentPos=({currentPos.x:F1},{currentPos.y:F1},{currentPos.z:F1}) currentDead={currentDead} "
                        + $"newPos=({newPosition.x:F1},{newPosition.y:F1},{newPosition.z:F1}) type={newType} spec={newSpeciality} angle={newAngle}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"{Label} DP-7 ReceiveZombieAlive Prefix 异常: {ex.Message}");
                }
            }

            //   DP-8.7 ZombieRegion.destroy Prefix - 销毁前快照
            // 签名：public void destroy()，无参数（instance method on ZombieRegion）
            // 严格只读（F4 + R6：不新增反射，公开成员直接读取）：
            //   - zombies.Count：public getter，U3-SDK ZombieRegion.cs:18-20
            //   - nav：public byte 字段（值类型），:21-25 -- R5 直接记录数值，不与 null 比较
            //   - isNetworked：public bool 字段，:38 -- R6 直接读 __instance.isNetworked，不用反射读取器
            //   - PlayerCountInRegion：public int getter，:357-370 -- R6 直接读 __instance.PlayerCountInRegion
            //   - remoteOccupantsInBound：扫描 Provider.clients，不依赖反射
            //   - 销毁前稳定索引实体快照（最多 10 个）：R3
            // 严禁：修改 Region 生命周期、Zombie 列表、PlayerCount 或 isNetworked
            internal static void DP8_7_Destroy_Prefix(ZombieRegion __instance)
            {
                try
                {
                    if (__instance == null) return;

                    // R6：直接读取公开成员，不调用 ReadRegionIsNetworked/ReadPlayerCountInRegion 反射读取器
                    int zombiesCount = -1;
                    byte nav = 0;
                    bool isNetworked = false;
                    int playerCountInRegion = -1;
                    int bound = -1;
                    List<Zombie> zombiesList = null;

                    try
                    {
                        zombiesList = __instance.zombies;
                        zombiesCount = zombiesList?.Count ?? -1;
                        // R5：nav 是 byte 值类型，直接读取数值，不与 null 比较（修复 CS0472）
                        nav = __instance.nav;
                        isNetworked = __instance.isNetworked;
                        playerCountInRegion = __instance.PlayerCountInRegion;

                        // 通过扫描 ZombieManager.regions 找到当前 bound 索引（ReferenceEquals 同实例）
                        if (ZombieManager.regions != null)
                        {
                            for (int i = 0; i < ZombieManager.regions.Length; i++)
                            {
                                if (ReferenceEquals(ZombieManager.regions[i], __instance))
                                {
                                    bound = i;
                                    break;
                                }
                            }
                        }
                    }
                    catch { }

                    // 计算 remoteOccupantsInBound（与 DP-5 一致：扫描 Provider.clients 排除 IsLocalPlayer）
                    int remoteOccupants = 0;
                    try
                    {
                        if (Provider.isServer && Provider.clients != null && bound >= 0)
                        {
                            foreach (SteamPlayer sp in Provider.clients)
                            {
                                if (sp == null || sp.player == null || sp.player.movement == null) continue;
                                if (sp.player.channel?.IsLocalPlayer ?? false) continue;
                                if (sp.player.movement.bound == bound)
                                {
                                    remoteOccupants++;
                                }
                            }
                        }
                    }
                    catch { }

                    // R3：销毁前稳定索引实体快照（最多 10 个）
                    string entitySnapshot = "<unavailable>";
                    try
                    {
                        if (bound >= 0 && zombiesList != null)
                        {
                            entitySnapshot = FormatRegionEntitySnapshot((byte)bound, zombiesCount, zombiesList, 10);
                        }
                    }
                    catch { }

                    RoleLogger.Info("[Shared]",
                        $"{Label} DP-8.7 ZombieRegion.destroy PRE session={CurrentSessionId} role={ProcessRole()} "
                        + $"bound={bound} zombiesCount={zombiesCount} nav={nav} "
                        + $"isNetworked={isNetworked} playerCountInRegion={playerCountInRegion} "
                        + $"remoteOccupantsInBound={remoteOccupants} "
                        + $"entitySnapshot={entitySnapshot} "
                        + $"sessionQuota={WorldSyncDiagnosticCore.SessionTotalCount}/{WorldSyncDiagnosticCore.SessionTotalLimit}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"{Label} DP-8.7 ZombieRegion.destroy Prefix 异常: {ex.Message}");
                }
            }
        }
    }
}
