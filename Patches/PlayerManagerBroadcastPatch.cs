using HarmonyLib;
using SDG.NetTransport;
using SDG.NetTransport.Loopback;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using Steamworks;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.22 P0-S1/S2 修复（外部审计报告-Codex §5 + v0.2.3.21 单机冒烟前静态外部审计返修）。
    ///
    /// P0-S1: PlayerManager.Update Dedicator.IsDedicatedServer 守卫替换
    ///   - 原版 PlayerManager.Update（PlayerManager.cs:354-378）节拍逻辑：
    ///       if (Dedicator.IsDedicatedServer && Time.realtimeSinceStartup - lastTick > Provider.UPDATE_TIME)
    ///   - listen server 模式下 Dedicator.IsDedicatedServer=false，导致 sendPlayerStates 从不被调用
    ///   - Transpiler 替换 Dedicator.IsDedicatedServer 为 IsDedicatedOrP2PHost（原版 dedicated || P2P host）
    ///   - 保留原版 Provider.UPDATE_TIME、Time.realtimeSinceStartup、lastTick、sendPlayerStates 逻辑
    ///   - 命中数验证恰好为 1
    ///
    /// P0-S2: PlayerManager.sendPlayerStates Prefix 注入房主本地快照（v0.2.3.22 强化）
    ///   - 原版本地玩家不填充 movement.updates（PlayerMovement.simulate 跳过 IsLocalPlayer）
    ///   - Prefix 在 sendPlayerStates 调用前为房主本地玩家注入一个 PlayerStateUpdate
    ///   - 根据位置/角度变化阈值或低频 keepalive 决定是否注入
    ///   - 让原版 sendPlayerStates 完成对所有远端客户端的写包和统一清队列
    ///   - v0.2.3.22 Critical-1 强化：房主候选必须同时满足四重身份条件
    ///     (1) sp.IsLocalServerHost == true
    ///     (2) sp.player.channel.IsLocalPlayer == true
    ///     (3) sp.playerID.steamID == Provider.user
    ///     (4) sp.transportConnection is TransportConnection_Loopback
    ///     候选数必须恰好为 1，0 或多个时不注入（fail-safe）。
    ///     remoteClientCount 只统计 transport 非 loopback 且非本地玩家的有效远端对象。
    ///     反射字段失配时不注入（fail-safe）。
    ///
    /// 审计授权：外部审计报告-Codex §5 P0-S1/S2 + v0.2.3.21 单机冒烟前静态外部审计返修 Critical-1。
    ///
    /// 严格禁止（审计 §8 + v0.2.3.22 返修）：
    ///   - 全局伪造 Dedicator.IsDedicatedServer=true
    ///   - 在客机直接写房主 Transform
    ///   - 手工调用 tellState
    ///   - 复制 NetMessages 原始包
    ///   - 绕过原版 seq/culling
    ///   - P0-S2 单一身份字段判定（必须四重条件 + 候选=1）
    /// </summary>
    public static class PlayerManagerBroadcastPatch
    {
        private const string TargetUpdateMethod = "Update";
        private const string TargetSendMethod = "sendPlayerStates";
        private const string HarmonyId = SteamP2PFriendsPlugin.HARMONY_ID;

        public static bool P0S1_Registered { get; private set; }
        public static bool P0S2_Registered { get; private set; }
        public static int P0S1_ReplacementCount { get; private set; } = -1;

        // v0.2.3.23 P0-C1：精确 MethodInfo owner 自检（审计报告-Codex §3 P0-C1 修订）
        //   - exactExpectedCount：期望的精确 PatchMethod 出现次数（应为 1）
        //   - sameOwnerOtherCount：同一 HARMONY_ID 但 PatchMethod 不同的 patch 数（允许 >=0）
        //   - foreignOwnerCount：其他 owner 的 patch 数（允许 >=0）
        //   - total：该 original 方法上该类型 patch 的总数
        //   判定条件：exactExpectedCount == 1 即通过
        public static bool P0S1_TranspilerOwnerVerified { get; private set; }
        public static bool P0S2_PrefixOwnerVerified { get; private set; }
        public static string P0S1_TranspilerOwnerSummary { get; private set; } = "<unverified>";
        public static string P0S2_PrefixOwnerSummary { get; private set; } = "<unverified>";

        // v0.2.3.22 自检：P0-S2 反射字段完整性
        public static bool P0S2_ReflectionComplete { get; private set; }

        public static bool AllRegistrationsSucceeded =>
            P0S1_Registered && P0S2_Registered
            && P0S1_TranspilerOwnerVerified && P0S2_PrefixOwnerVerified
            && P0S1_ReplacementCount == 1
            && P0S2_ReflectionComplete;

        // P0-S2: 房主本地快照注入状态
        private static Vector3 _lastInjectedPos;
        private static byte _lastInjectedAngle;
        private static byte _lastInjectedRot;
        private static bool _lastInjectedValid;
        private static float _lastInjectTime;
        private const float KeepaliveInterval = 0.4f;
        private const float PositionThresholdSqr = 0.01f * 0.01f;

        // v0.2.3.22 Critical-1: 候选数错误限频日志
        private static float _lastHostCandidateErrorTime;
        private const float HostCandidateErrorInterval = 5.0f;

        // v0.2.3.26 sender 诊断（Codex §5.3）：有界记录编码 yaw byte 变化
        // - baseline 首次注入记录一次
        // - 仅在编码 yaw byte 变化时记录，上限 SenderChangeLogLimit 次
        // - 客机断线/新会话时由 OnClientDisconnected 重置
        private static bool _senderBaselineLogged;
        private static byte _lastLoggedEncodedRot;
        private static int _senderChangeLogCount;
        private const int SenderChangeLogLimit = 12;

        // 反射缓存：PlayerMovement 的 internal 字段
        private static FieldInfo _mostRecentlyAddedUpdateField;
        private static FieldInfo _hasMostRecentlyAddedUpdateField;
        private static bool _reflectionCached;

        /// <summary>
        /// P0-S1 helper：判定是否应启动玩家状态广播。
        /// 原版 dedicated server 或 P2P listen-host 模式。
        /// </summary>
        public static bool IsDedicatedOrP2PHost()
        {
            try
            {
                if (Dedicator.IsDedicatedServer) return true;
                return HostManager.IsP2PHostMode;
            }
            catch
            {
                return false;
            }
        }

        public static bool RegisterManual(Harmony harmony)
        {
            CacheReflection();

            P0S1_Registered = RegisterP0S1(harmony);
            P0S2_Registered = RegisterP0S2(harmony);

            // v0.2.3.23 P0-C1：RegisterManual 阶段仅做初步自检；
            // Plugin.VerifyCriticalPatches 会在所有 patch 登记完成后调用 ReverifyOwnersAfterAllRegistrations
            // 进行最终的精确 MethodInfo 自检（实时读取 Harmony 元数据）。
            VerifyPatchOwners(harmony);

            RoleLogger.Info("[Shared]",
                $"[P0-S1/S2] PlayerManagerBroadcastPatch 汇总: " +
                $"P0-S1={P0S1_Registered} P0-S2={P0S2_Registered} " +
                $"replacementCount={P0S1_ReplacementCount} " +
                $"P0S1_owner={P0S1_TranspilerOwnerSummary} " +
                $"P0S2_owner={P0S2_PrefixOwnerSummary} " +
                $"P0S2_reflectionComplete={P0S2_ReflectionComplete} " +
                $"reflectionMostRecentField={(_mostRecentlyAddedUpdateField != null)} " +
                $"reflectionHasMostRecentField={(_hasMostRecentlyAddedUpdateField != null)} " +
                $"allOk={AllRegistrationsSucceeded}");
            return AllRegistrationsSucceeded;
        }

        /// <summary>
        /// v0.2.3.23 P0-C1：在所有手工 patch 登记完成后由 Plugin.VerifyCriticalPatches 调用，
        /// 重新实时读取 Harmony 元数据进行精确 MethodInfo 自检。
        /// 解决 P0-S2 RegisterManual 阶段读取时 P1-S5 Prefix 尚未登记导致元数据不完整的问题。
        /// </summary>
        public static void ReverifyOwnersAfterAllRegistrations(Harmony harmony)
        {
            VerifyPatchOwners(harmony);

            RoleLogger.Info("[Shared]",
                $"[P0-S1/S2] ReverifyOwnersAfterAllRegistrations: " +
                $"P0S1_owner={P0S1_TranspilerOwnerVerified} ({P0S1_TranspilerOwnerSummary}) " +
                $"P0S2_owner={P0S2_PrefixOwnerVerified} ({P0S2_PrefixOwnerSummary}) " +
                $"allOk={AllRegistrationsSucceeded}");
        }

        private static void CacheReflection()
        {
            if (_reflectionCached) return;
            _reflectionCached = true;
            try
            {
                _mostRecentlyAddedUpdateField = AccessTools.Field(typeof(PlayerMovement), "mostRecentlyAddedUpdate");
                _hasMostRecentlyAddedUpdateField = AccessTools.Field(typeof(PlayerMovement), "hasMostRecentlyAddedUpdate");

                // v0.2.3.22 Critical-1: 反射字段必须全部就绪，否则 P0-S2 fail-safe
                P0S2_ReflectionComplete = (_mostRecentlyAddedUpdateField != null)
                    && (_hasMostRecentlyAddedUpdateField != null);

                if (_mostRecentlyAddedUpdateField == null)
                {
                    RoleLogger.Error("[Shared]",
                        "[P0-S2] !!! PlayerMovement.mostRecentlyAddedUpdate 反射失败，P0-S2 将 fail-safe 不注入（Critical-1）");
                }
                if (_hasMostRecentlyAddedUpdateField == null)
                {
                    RoleLogger.Error("[Shared]",
                        "[P0-S2] !!! PlayerMovement.hasMostRecentlyAddedUpdate 反射失败，P0-S2 将 fail-safe 不注入（Critical-1）");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P0-S2] 反射缓存异常: {ex.Message}");
                P0S2_ReflectionComplete = false;
            }
        }

        private static bool RegisterP0S1(Harmony harmony)
        {
            const string Label = "P0-S1 PlayerManager.Update Transpiler";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(PlayerManager), TargetUpdateMethod);
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[P0-S1] !!! {Label} 反射失败");
                    return false;
                }
                MethodInfo transpiler = typeof(Hooks).GetMethod(nameof(Hooks.UpdateTranspiler),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, transpiler: new HarmonyMethod(transpiler));

                if (P0S1_ReplacementCount != 1)
                {
                    RoleLogger.Error("[Shared]",
                        $"[P0-S1] !!! DIAGNOSTIC BUILD INVALID: replacement count={P0S1_ReplacementCount} 期望=1");
                    return false;
                }

                RoleLogger.Info("[Shared]", $"[P0-S1] OK {Label} 已登记 (replacement=1/1)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P0-S1] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        private static bool RegisterP0S2(Harmony harmony)
        {
            const string Label = "P0-S2 PlayerManager.sendPlayerStates Prefix";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(PlayerManager), TargetSendMethod);
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[P0-S2] !!! {Label} 反射失败");
                    return false;
                }
                MethodInfo prefix = typeof(Hooks).GetMethod(nameof(Hooks.SendPlayerStatesPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, prefix: new HarmonyMethod(prefix));

                RoleLogger.Info("[Shared]", $"[P0-S2] OK {Label} 已登记");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P0-S2] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// v0.2.3.23 P0-C1：验证 P0-S1 Transpiler + P0-S2 Prefix owner 精确 MethodInfo 出现 1 次。
        /// 审计报告-Codex §3 P0-C1 修订：
        ///   - 使用精确 MethodInfo 比较（不用 ReflectedType 猜测，不用 DeclaringType.DeclaringType）
        ///   - 日志输出 exactExpectedCount / sameOwnerOtherCount / foreignOwnerCount / total
        ///   - 判定条件：exactExpectedCount == 1（允许同 owner 其他 patch 共存）
        /// </summary>
        private static void VerifyPatchOwners(Harmony harmony)
        {
            // P0-S1 Transpiler owner 自检
            try
            {
                MethodInfo original = AccessTools.Method(typeof(PlayerManager), TargetUpdateMethod);
                MethodInfo expectedTranspiler = AccessTools.Method(typeof(Hooks), nameof(Hooks.UpdateTranspiler));
                P0S1_TranspilerOwnerVerified = VerifyPatchOwnerExact(original, isTranspiler: true,
                    expectedMethod: expectedTranspiler,
                    summaryOut: out string tSummary);
                P0S1_TranspilerOwnerSummary = tSummary;
                if (!P0S1_TranspilerOwnerVerified)
                {
                    RoleLogger.Error("[Shared]", $"[P0-S1] !!! Owner 自检失败: {tSummary}");
                }
            }
            catch (System.Exception ex)
            {
                P0S1_TranspilerOwnerVerified = false;
                P0S1_TranspilerOwnerSummary = $"exception: {ex.Message}";
                RoleLogger.Error("[Shared]", $"[P0-S1] Owner 自检异常: {ex.Message}");
            }

            // P0-S2 Prefix owner 自检
            try
            {
                MethodInfo original = AccessTools.Method(typeof(PlayerManager), TargetSendMethod);
                MethodInfo expectedPrefix = AccessTools.Method(typeof(Hooks), nameof(Hooks.SendPlayerStatesPrefix));
                P0S2_PrefixOwnerVerified = VerifyPatchOwnerExact(original, isTranspiler: false,
                    expectedMethod: expectedPrefix,
                    summaryOut: out string pSummary);
                P0S2_PrefixOwnerSummary = pSummary;
                if (!P0S2_PrefixOwnerVerified)
                {
                    RoleLogger.Error("[Shared]", $"[P0-S2] !!! Owner 自检失败: {pSummary}");
                }
            }
            catch (System.Exception ex)
            {
                P0S2_PrefixOwnerVerified = false;
                P0S2_PrefixOwnerSummary = $"exception: {ex.Message}";
                RoleLogger.Error("[Shared]", $"[P0-S2] Owner 自检异常: {ex.Message}");
            }
        }

        /// <summary>
        /// v0.2.3.23 P0-C1：精确 MethodInfo owner 自检核心逻辑。
        /// 审计要求：比较 PatchMethod 与期望 MethodInfo 的引用相等性；
        /// 若运行时引用比较不稳定，回退到 Module + MetadataToken 等价比较。
        /// 日志输出 exact/same-owner-other/foreign/total 四项元数据。
        /// </summary>
        private static bool VerifyPatchOwnerExact(MethodInfo original, bool isTranspiler,
            MethodInfo expectedMethod, out string summaryOut)
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
            System.Collections.ICollection patches = isTranspiler
                ? info?.Transpilers
                : info?.Prefixes;

            if (patches == null || patches.Count == 0)
            {
                summaryOut = $"patches=0 (isTranspiler={isTranspiler})";
                return false;
            }

            int exactExpectedCount = 0;
            int sameOwnerOtherCount = 0;
            int foreignOwnerCount = 0;
            string firstForeignOwner = null;

            foreach (Patch p in patches)
            {
                bool isOurOwner = (p.owner == HarmonyId);
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

        /// <summary>
        /// v0.2.3.23 P0-C1：MethodInfo 等价比较。
        /// 优先用引用相等；若引用不等，回退到 Module + MetadataToken 比较（同一运行时同一方法应产生相同 token）。
        /// </summary>
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
                // 某些动态方法可能不支持 MetadataToken
            }
            return false;
        }

        /// <summary>
        /// 客机断开时重置注入状态（避免下次开服残留）。
        /// </summary>
        public static void OnClientDisconnected()
        {
            _lastInjectedValid = false;
            _lastInjectTime = 0f;
            _lastHostCandidateErrorTime = 0f;
            // v0.2.3.26 sender 诊断重置
            _senderBaselineLogged = false;
            _lastLoggedEncodedRot = 0;
            _senderChangeLogCount = 0;
        }

        /// <summary>
        /// v0.2.3.26 sender 诊断（Codex §5.3）：有界记录编码 yaw byte 变化。
        /// - 首次注入记录 baseline 一次
        /// - 仅在编码 yaw byte 相对上次变化时记录，上限 SenderChangeLogLimit 次
        /// - 每条包含 lookPitch/lookYaw 浮点、新编码 byte、旧 look.angle/rot（stale 对照）、model yaw
        /// - 纯只读诊断，不干预任何字段
        /// 设计目的：直接证明"新 byte 随主机转动而变化，旧 byte 仍 stale"，
        /// 且日志额度不会在玩家真正转向之前被 First-N 耗尽。
        /// </summary>
        private static void LogSenderDiagnostic(Player hostPlayer, float pitchF, float yawF,
                                                byte encodedAngle, byte encodedRot)
        {
            try
            {
                if (hostPlayer == null) return;

                float modelYaw = 0f;
                try
                {
                    // model 是 SteamPlayer 的属性（SteamPlayer.cs:39），不是 Player 的。
                    // 通过 hostPlayer.channel.owner 取得 SteamPlayer 再读 model。
                    SteamChannel ch = hostPlayer.channel;
                    if (!ReferenceEquals(ch, null))
                    {
                        SteamPlayer ownerSp = ch.owner;
                        if (!ReferenceEquals(ownerSp, null) && !ReferenceEquals(ownerSp.model, null))
                        {
                            modelYaw = ownerSp.model.transform.rotation.eulerAngles.y;
                        }
                    }
                }
                catch { }

                byte staleByteAngle = 0;
                byte staleByteRot = 0;
                try
                {
                    staleByteAngle = hostPlayer.look.angle;
                    staleByteRot = hostPlayer.look.rot;
                }
                catch { }

                // baseline：首次注入记录一次
                if (!_senderBaselineLogged)
                {
                    _senderBaselineLogged = true;
                    _lastLoggedEncodedRot = encodedRot;
                    RoleLogger.Info("[Host]",
                        $"[P0-S2] inject baseline pitch(f={pitchF:F2},b={encodedAngle}) " +
                        $"yaw(f={yawF:F2},b={encodedRot}) " +
                        $"staleByteAngle={staleByteAngle} staleByteRot={staleByteRot} " +
                        $"modelYaw={modelYaw:F2}");
                    return;
                }

                // 仅在编码 yaw byte 变化时记录，上限 SenderChangeLogLimit 次
                if (encodedRot != _lastLoggedEncodedRot && _senderChangeLogCount < SenderChangeLogLimit)
                {
                    _senderChangeLogCount++;
                    _lastLoggedEncodedRot = encodedRot;
                    RoleLogger.Info("[Host]",
                        $"[P0-S2] inject change#{_senderChangeLogCount} pitch(f={pitchF:F2},b={encodedAngle}) " +
                        $"yaw(f={yawF:F2},b={encodedRot}) " +
                        $"staleByteAngle={staleByteAngle} staleByteRot={staleByteRot} " +
                        $"modelYaw={modelYaw:F2}");
                }
            }
            catch
            {
                // 诊断内部异常不能影响注入路径
            }
        }

        private static class Hooks
        {
            /// <summary>
            /// P0-S1: PlayerManager.Update Transpiler。
            /// 替换 Dedicator.IsDedicatedServer 调用为 IsDedicatedOrP2PHost。
            /// 栈平衡：原 call get_IsDedicatedServer()（+1 bool）替换为 call IsDedicatedOrP2PHost()（+1 bool），一致。
            /// </summary>
            internal static IEnumerable<CodeInstruction> UpdateTranspiler(
                IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            {
                var codes = new List<CodeInstruction>(instructions);

                MethodInfo dedicatedGetter = AccessTools.PropertyGetter(typeof(Dedicator), nameof(Dedicator.IsDedicatedServer));
                MethodInfo helperMethod = AccessTools.Method(typeof(PlayerManagerBroadcastPatch),
                    nameof(IsDedicatedOrP2PHost), System.Type.EmptyTypes);

                if (dedicatedGetter == null)
                {
                    P0S1_ReplacementCount = -1;
                    throw new System.InvalidOperationException(
                        "PlayerManagerBroadcastPatch: Dedicator.get_IsDedicatedServer not found");
                }
                if (helperMethod == null)
                {
                    P0S1_ReplacementCount = -1;
                    throw new System.InvalidOperationException(
                        "PlayerManagerBroadcastPatch: IsDedicatedOrP2PHost not found");
                }

                int replacementCount = 0;
                for (int i = 0; i < codes.Count; i++)
                {
                    CodeInstruction instr = codes[i];
                    if (instr == null) continue;

                    if (instr.Calls(dedicatedGetter))
                    {
                        instr.opcode = OpCodes.Call;
                        instr.operand = helperMethod;
                        replacementCount++;
                    }
                }

                P0S1_ReplacementCount = replacementCount;

                if (replacementCount != 1)
                {
                    throw new System.InvalidOperationException(
                        $"PlayerManagerBroadcastPatch: replacement count={replacementCount} expected=1");
                }

                RoleLogger.Info("[Shared]",
                    $"[P0-S1] Transpiler replacement=1/1，IL 修改已应用（Dedicator.IsDedicatedServer -> IsDedicatedOrP2PHost）");
                return codes;
            }

            /// <summary>
            /// P0-S2: PlayerManager.sendPlayerStates Prefix（v0.2.3.22 Critical-1 强化）。
            /// 在原版 sendPlayerStates 调用前，为房主本地玩家注入一个 PlayerStateUpdate。
            /// 房主候选必须满足四重身份条件，候选数恰好为 1，否则 fail-safe 不注入。
            /// </summary>
            internal static void SendPlayerStatesPrefix(PlayerManager __instance)
            {
                try
                {
                    if (!HostManager.IsP2PHostMode) return;
                    if (!Provider.isServer) return;
                    if (Provider.clients == null || Provider.clients.Count < 2) return;

                    // v0.2.3.22 Critical-1: 反射字段必须完整，否则 fail-safe
                    if (!P0S2_ReflectionComplete)
                    {
                        // 限频错误日志（每 5 秒最多一条）
                        float now0 = Time.realtimeSinceStartup;
                        if (now0 - _lastHostCandidateErrorTime > HostCandidateErrorInterval)
                        {
                            _lastHostCandidateErrorTime = now0;
                            RoleLogger.Warn("[Host]",
                                "[P0-S2] 反射字段未就绪，fail-safe 不注入（Critical-1）");
                        }
                        return;
                    }

                    CSteamID providerUser = Provider.user;

                    Player hostPlayer = null;
                    int hostCandidateCount = 0;
                    int remoteClientCount = 0;

                    foreach (SteamPlayer sp in Provider.clients)
                    {
                        if (sp == null || sp.player == null) continue;

                        // 四重身份条件
                        bool isLocalServerHost = sp.IsLocalServerHost;
                        bool isLocalPlayer = sp.player.channel != null && sp.player.channel.IsLocalPlayer;
                        bool steamIdMatches = false;
                        try
                        {
                            if (!ReferenceEquals(sp.playerID, null) && sp.playerID.steamID == providerUser)
                            {
                                steamIdMatches = true;
                            }
                        }
                        catch { }

                        bool transportIsLoopback = false;
                        try
                        {
                            ITransportConnection tc = sp.transportConnection;
                            if (!ReferenceEquals(tc, null) && tc is TransportConnection_Loopback)
                            {
                                transportIsLoopback = true;
                            }
                        }
                        catch { }

                        if (isLocalServerHost && isLocalPlayer && steamIdMatches && transportIsLoopback)
                        {
                            hostPlayer = sp.player;
                            hostCandidateCount++;
                        }
                        else if (!isLocalPlayer && !transportIsLoopback)
                        {
                            // 仅统计 transport 非 loopback 且非本地玩家的有效远端对象
                            remoteClientCount++;
                        }
                    }

                    // v0.2.3.22 Critical-1: 候选数必须恰好为 1
                    if (hostCandidateCount != 1)
                    {
                        float now = Time.realtimeSinceStartup;
                        if (now - _lastHostCandidateErrorTime > HostCandidateErrorInterval)
                        {
                            _lastHostCandidateErrorTime = now;
                            RoleLogger.Warn("[Host]",
                                $"[P0-S2] 房主候选数={hostCandidateCount} != 1（期望=1），fail-safe 不注入。 " +
                                $"remoteClientCount={remoteClientCount}（Critical-1 四重身份条件未满足）");
                        }
                        return;
                    }

                    if (hostPlayer == null || remoteClientCount == 0) return;
                    if (hostPlayer.movement == null || hostPlayer.movement.updates == null) return;

                    Vector3 currentPos = hostPlayer.transform.position;
                    // v0.2.3.26 修复（Codex §5.1）：PlayerLook.Update()（PlayerLook.cs:1131-1657）在本地玩家鼠标路径
                    // 仅更新 _yaw/_pitch 浮点字段与 transform.localRotation，不调 updateRot()。
                    // 因此 look.angle/rot 字节字段自 InitializePlayer 后保持 stale，P0-S2 读取它们导致
                    // 客机收到 yaw=104 固化（v0.2.3.25 第十六次双机转动不同步根因）。
                    // 修复方案：从 look.pitch/yaw 浮点权威值实时计算发送用字节。
                    // 纯读路径，不调 updateRot()，不修改 look.angle/rot。
                    // pitch 逐分支复刻 vanilla updateRot()（PlayerLook.cs:549-562）。
                    // yaw 使用 MeasurementTool.angleToByte（与 PlayerLook.cs:564 一致）。
                    float pitchF = hostPlayer.look.pitch;
                    float yawF = hostPlayer.look.yaw;
                    byte currentAngle;
                    if (pitchF < 0f)
                        currentAngle = 0;
                    else if (pitchF > 180f)
                        currentAngle = 180;
                    else
                        currentAngle = (byte)pitchF;
                    byte currentRot = MeasurementTool.angleToByte(yawF);

                    float now2 = Time.realtimeSinceStartup;
                    bool shouldInject;
                    if (!_lastInjectedValid)
                    {
                        shouldInject = true;
                    }
                    else if ((currentPos - _lastInjectedPos).sqrMagnitude > PositionThresholdSqr)
                    {
                        shouldInject = true;
                    }
                    else if (currentAngle != _lastInjectedAngle || currentRot != _lastInjectedRot)
                    {
                        shouldInject = true;
                    }
                    else if (now2 - _lastInjectTime > KeepaliveInterval)
                    {
                        shouldInject = true;
                    }
                    else
                    {
                        shouldInject = false;
                    }

                    if (!shouldInject) return;

                    var update = new PlayerStateUpdate(currentPos, currentAngle, currentRot);
                    hostPlayer.movement.updates.Add(update);

                    // P0S2_ReflectionComplete 已校验，字段非空
                    _mostRecentlyAddedUpdateField.SetValue(hostPlayer.movement, update);
                    _hasMostRecentlyAddedUpdateField.SetValue(hostPlayer.movement, true);

                    _lastInjectedPos = currentPos;
                    _lastInjectedAngle = currentAngle;
                    _lastInjectedRot = currentRot;
                    _lastInjectedValid = true;
                    _lastInjectTime = now2;

                    // v0.2.3.26 sender 诊断（Codex §5.3）：有界记录编码 yaw byte 变化
                    LogSenderDiagnostic(hostPlayer, pitchF, yawF, currentAngle, currentRot);
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Host]", $"[P0-S2] SendPlayerStatesPrefix 异常（不阻断）: {ex.Message}");
                }
            }
        }
    }
}
