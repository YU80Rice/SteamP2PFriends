using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.13 新增（Codex 第八次审计 P0-B + P0-C + P0-D）：
    /// BarricadeManager.onRegionUpdated step 2 远程区域同步资格 patch + SendRegion 决定性日志。
    ///
    /// v0.2.3.13 返修（Codex v0.2.3.13 外部审计报告 P0-2 + P0-3 + P1-1）：
    ///   - P0-2：Transpiler 改为原地修改 CodeInstruction（保留 labels/blocks）+ 使用 CodeInstruction.Calls(dedicatedGetter)。
    ///   - P0-3：新增 OnClientDisconnected + ResetAll，按断线/新会话清除 _eligibilityLogCounts。
    ///   - P1-1：Transpiler 和 SendRegion Prefix 增加精确 owner + patch method + exact 1/1 自检。
    ///
    /// 目标：解除 listen server 模式下"主机不向远程客机发送 Barricades RPC"的诅咒。
    /// vanilla 源码（U3-SDK BarricadeManager.cs:2886-2908）：
    ///   if (step == 2)
    ///   {
    ///       if (Dedicator.IsDedicatedServer)   <-- Transpiler 替换此调用
    ///       {
    ///           // 遍历玩家周围 BARRICADE_REGIONS 范围，调 SendRegion 发送
    ///       }
    ///   }
    ///
    /// 严格自检（P0-C fail-closed + P1-1 精确 owner）：
    ///   - onRegionUpdated 签名精确解析（private instance, 7 args）
    ///   - SendRegion 签名精确解析（internal instance, 6 args）
    ///   - Transpiler replacement count 必须精确等于 1
    ///   - SendRegion Prefix 必须登记成功
    ///   - Transpiler owner 为 com.yu80rice.steamp2pfriends，patch method 为 BarricadeManagerRegionSyncPatch.OnRegionUpdated_Transpiler，count=1
    ///   - SendRegion Prefix owner 为 com.yu80rice.steamp2pfriends，patch method 为 BarricadeManagerRegionSyncPatch.SendRegion_Prefix，count=1
    ///
    /// 严格禁止（P0-E）：
    ///   - 不全局伪造 Dedicator.IsDedicatedServer
    ///   - 不修改 BarricadeManager.onLevelLoaded 中的 if (Provider.isServer) load() 守卫
    ///   - 不引入自定义 RPC（继续使用原生 SendRegion / ReceiveMultipleBarricades）
    /// </summary>
    public static class BarricadeManagerRegionSyncPatch
    {
        // P0-C：手动登记状态
        public static bool AllRegistrationsSucceeded { get; private set; }
        public static string RegistrationSummary { get; private set; } = "未登记";
        public static int ReplacementCount { get; private set; } = -1;
        public static bool SignatureResolved { get; private set; }
        public static string SignatureSummary { get; private set; } = "未自检";
        public static bool SendRegionPrefixRegistered { get; private set; }

        // P1-1：精确 owner 自检状态
        public static bool TranspilerOwnerVerified { get; private set; }
        public static bool PrefixOwnerVerified { get; private set; }
        public static string TranspilerOwnerSummary { get; private set; } = "未自检";
        public static string PrefixOwnerSummary { get; private set; } = "未自检";

        // P0-D：决定性日志限次（每个玩家前 5 次资格判定输出一次）
        private const int EligibilityLogLimit = 5;
        private static readonly Dictionary<ulong, int> _eligibilityLogCounts = new Dictionary<ulong, int>();

        private const string HarmonyId = SteamP2PFriendsPlugin.HARMONY_ID;
        private const string TargetMethodName = "onRegionUpdated";
        private const string SendRegionMethodName = "SendRegion";
        private const string PatchTranspilerName = nameof(OnRegionUpdated_Transpiler);
        private const string PatchSendRegionPrefixName = nameof(SendRegion_Prefix);

        /// <summary>
        /// 手动登记两个 patch（P0-C fail-closed + P1-1 精确 owner）。
        /// </summary>
        public static bool RegisterManual(Harmony harmony)
        {
            RoleLogger.Info("[Shared]", "[BarricadeRegionSync] === 手动登记 Transpiler + SendRegion Prefix（P0-B + P0-D + P1-1）===");

            if (harmony == null)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = "harmony=null";
                RoleLogger.Error("[Shared]", $"[BarricadeRegionSync] !!! {RegistrationSummary}");
                return false;
            }

            // P0-C step 1：自检 onRegionUpdated 签名
            bool sigOk = VerifyTargetSignature();
            if (!sigOk)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = $"onRegionUpdated 签名自检失败 ({SignatureSummary})";
                RoleLogger.Error("[Shared]", $"[BarricadeRegionSync] !!! {RegistrationSummary}");
                return false;
            }

            // P0-C step 2：登记 Transpiler
            bool transpilerOk = RegisterTranspiler(harmony);
            if (!transpilerOk)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = $"Transpiler 登记失败 (replacement={ReplacementCount})";
                RoleLogger.Error("[Shared]", $"[BarricadeRegionSync] !!! {RegistrationSummary}");
                return false;
            }

            // P0-C step 3：登记 SendRegion Prefix
            bool prefixOk = RegisterSendRegionPrefix(harmony);
            SendRegionPrefixRegistered = prefixOk;
            if (!prefixOk)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = "SendRegion Prefix 登记失败";
                RoleLogger.Error("[Shared]", $"[BarricadeRegionSync] !!! {RegistrationSummary}");
                return false;
            }

            AllRegistrationsSucceeded = true;
            RegistrationSummary = $"signature={SignatureResolved}, replacement=1/1, sendRegionPrefix=true, " +
                $"transpilerOwner={TranspilerOwnerVerified}, prefixOwner={PrefixOwnerVerified}";
            RoleLogger.Info("[Shared]",
                $"[BarricadeRegionSync] OK 手动登记成功 summary={RegistrationSummary}");
            return true;
        }

        private static bool RegisterTranspiler(Harmony harmony)
        {
            try
            {
                MethodInfo original = AccessTools.Method(typeof(BarricadeManager), TargetMethodName);
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", "[BarricadeRegionSync] !!! onRegionUpdated AccessTools.Method 返回 null");
                    return false;
                }

                MethodInfo transpiler = AccessTools.Method(typeof(BarricadeManagerRegionSyncPatch), PatchTranspilerName);
                if (transpiler == null)
                {
                    RoleLogger.Error("[Shared]", "[BarricadeRegionSync] !!! Transpiler 方法未找到");
                    return false;
                }

                harmony.Patch(original, transpiler: new HarmonyMethod(transpiler));

                if (ReplacementCount != 1)
                {
                    RoleLogger.Error("[Shared]",
                        $"[BarricadeRegionSync] !!! DIAGNOSTIC BUILD INVALID: replacement count={ReplacementCount} 期望=1");
                    return false;
                }

                // P1-1：精确 owner 自检
                bool ownerOk = VerifyPatchOwner(original, isTranspiler: true);
                if (!ownerOk)
                {
                    RoleLogger.Error("[Shared]",
                        $"[BarricadeRegionSync] !!! DIAGNOSTIC BUILD INVALID: Transpiler owner 自检失败 summary={TranspilerOwnerSummary}");
                    return false;
                }

                RoleLogger.Info("[Shared]",
                    $"[BarricadeRegionSync] OK Transpiler 已登记 (replacement=1/1, owner={TranspilerOwnerVerified})");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[BarricadeRegionSync] !!! RegisterTranspiler 异常: {ex}");
                return false;
            }
        }

        private static bool RegisterSendRegionPrefix(Harmony harmony)
        {
            try
            {
                // SendRegion 是 internal 方法，AccessTools 能访问到
                MethodInfo original = AccessTools.Method(typeof(BarricadeManager), SendRegionMethodName);
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", "[BarricadeRegionSync] !!! SendRegion AccessTools.Method 返回 null");
                    return false;
                }

                MethodInfo prefix = AccessTools.Method(typeof(BarricadeManagerRegionSyncPatch), PatchSendRegionPrefixName);
                if (prefix == null)
                {
                    RoleLogger.Error("[Shared]", "[BarricadeRegionSync] !!! SendRegion Prefix 方法未找到");
                    return false;
                }

                harmony.Patch(original, prefix: new HarmonyMethod(prefix));

                // P1-1：精确 owner 自检
                bool ownerOk = VerifyPatchOwner(original, isTranspiler: false);
                if (!ownerOk)
                {
                    RoleLogger.Error("[Shared]",
                        $"[BarricadeRegionSync] !!! DIAGNOSTIC BUILD INVALID: SendRegion Prefix owner 自检失败 summary={PrefixOwnerSummary}");
                    return false;
                }

                RoleLogger.Info("[Shared]",
                    $"[BarricadeRegionSync] OK SendRegion Prefix 已登记 (owner={PrefixOwnerVerified})");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[BarricadeRegionSync] !!! RegisterSendRegionPrefix 异常: {ex}");
                return false;
            }
        }

        /// <summary>
        /// P1-1：精确 owner + patch method + exact 1/1 自检。
        /// 验证目标方法上 owner=com.yu80rice.steamp2pfriends 的 patch 精确为 1 个，
        /// 且 patch method 的 DeclaringType 和 Name 与本类期望一致。
        /// </summary>
        private static bool VerifyPatchOwner(MethodInfo original, bool isTranspiler)
        {
            try
            {
                HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
                System.Collections.ICollection patches = isTranspiler
                    ? (info?.Transpilers as System.Collections.ICollection)
                    : (info?.Prefixes as System.Collections.ICollection);

                if (patches == null || patches.Count == 0)
                {
                    if (isTranspiler)
                    {
                        TranspilerOwnerVerified = false;
                        TranspilerOwnerSummary = "transpilers count=0";
                    }
                    else
                    {
                        PrefixOwnerVerified = false;
                        PrefixOwnerSummary = "prefixes count=0";
                    }
                    return false;
                }

                string expectedMethodName = isTranspiler ? PatchTranspilerName : PatchSendRegionPrefixName;
                System.Type expectedDeclaringType = typeof(BarricadeManagerRegionSyncPatch);

                int ownCount = 0;
                bool methodMatched = false;
                string firstForeignOwner = null;

                foreach (Patch p in patches)
                {
                    if (p.owner == HarmonyId)
                    {
                        ownCount++;
                        MethodInfo patchMethod = p.PatchMethod;
                        if (patchMethod != null
                            && patchMethod.DeclaringType == expectedDeclaringType
                            && patchMethod.Name == expectedMethodName)
                        {
                            methodMatched = true;
                        }
                    }
                    else if (firstForeignOwner == null)
                    {
                        firstForeignOwner = p.owner;
                    }
                }

                string summary = $"ownCount={ownCount} methodMatched={methodMatched} foreignOwner={firstForeignOwner ?? "none"}";

                if (ownCount != 1 || !methodMatched)
                {
                    if (isTranspiler)
                    {
                        TranspilerOwnerVerified = false;
                        TranspilerOwnerSummary = summary;
                    }
                    else
                    {
                        PrefixOwnerVerified = false;
                        PrefixOwnerSummary = summary;
                    }
                    return false;
                }

                if (isTranspiler)
                {
                    TranspilerOwnerVerified = true;
                    TranspilerOwnerSummary = summary;
                }
                else
                {
                    PrefixOwnerVerified = true;
                    PrefixOwnerSummary = summary;
                }
                return true;
            }
            catch (System.Exception ex)
            {
                if (isTranspiler)
                {
                    TranspilerOwnerVerified = false;
                    TranspilerOwnerSummary = $"异常: {ex.Message}";
                }
                else
                {
                    PrefixOwnerVerified = false;
                    PrefixOwnerSummary = $"异常: {ex.Message}";
                }
                return false;
            }
        }

        /// <summary>
        /// P0-C：自检 onRegionUpdated 签名。
        /// 期望：private instance method，7 个参数 (Player, byte, byte, byte, byte, byte, ref bool)
        /// </summary>
        private static bool VerifyTargetSignature()
        {
            try
            {
                MethodInfo method = AccessTools.Method(typeof(BarricadeManager), TargetMethodName);
                if (method == null)
                {
                    SignatureResolved = false;
                    SignatureSummary = "AccessTools.Method 返回 null";
                    return false;
                }

                if (method.Name != TargetMethodName)
                {
                    SignatureResolved = false;
                    SignatureSummary = $"Name={method.Name} 期望={TargetMethodName}";
                    return false;
                }

                if (method.IsStatic)
                {
                    SignatureResolved = false;
                    SignatureSummary = "IsStatic=true 期望=false";
                    return false;
                }

                ParameterInfo[] ps = method.GetParameters();
                if (ps.Length != 7)
                {
                    SignatureResolved = false;
                    SignatureSummary = $"paramCount={ps.Length} 期望=7";
                    return false;
                }

                if (ps[0].ParameterType != typeof(Player))
                {
                    SignatureResolved = false;
                    SignatureSummary = $"param[0]={ps[0].ParameterType.Name} 期望=Player";
                    return false;
                }

                for (int i = 1; i <= 5; i++)
                {
                    if (ps[i].ParameterType != typeof(byte))
                    {
                        SignatureResolved = false;
                        SignatureSummary = $"param[{i}]={ps[i].ParameterType.Name} 期望=byte";
                        return false;
                    }
                }

                if (ps[6].ParameterType != typeof(bool).MakeByRefType())
                {
                    SignatureResolved = false;
                    SignatureSummary = $"param[6]={ps[6].ParameterType.Name} 期望=bool&";
                    return false;
                }

                if (method.ReturnType != typeof(void))
                {
                    SignatureResolved = false;
                    SignatureSummary = $"ReturnType={method.ReturnType.Name} 期望=void";
                    return false;
                }

                SignatureResolved = true;
                SignatureSummary = "private instance void onRegionUpdated(Player,byte,byte,byte,byte,byte,ref bool)";
                RoleLogger.Info("[Shared]",
                    $"[BarricadeRegionSync] OK 签名自检通过: {SignatureSummary}");
                return true;
            }
            catch (System.Exception ex)
            {
                SignatureResolved = false;
                SignatureSummary = $"异常: {ex.Message}";
                RoleLogger.Error("[Shared]", $"[BarricadeRegionSync] !!! 签名自检异常: {ex}");
                return false;
            }
        }

        /// <summary>
        /// P0-B + P0-2 返修：Transpiler 主实现。
        /// 替换 vanilla onRegionUpdated 中的 Dedicator.get_IsDedicatedServer() 调用
        /// 为 ListenRegionSyncEligibility.IsDedicatedOrP2PRemoteRecipient(player)。
        ///
        /// v0.2.3.13 返修（Codex P0-2）：
        ///   - 改用 CodeInstruction.Calls(dedicatedGetter) 替代 ReferenceEquals（不依赖 MethodInfo 对象引用）
        ///   - 原地修改 codes[i].opcode/operand（保留 labels/blocks），避免未来游戏更新或 Harmony 组合 patch 后控制流元数据被破坏
        ///
        /// 栈平衡验证：
        ///   原版：call get_IsDedicatedServer()（无参数，返回 bool i4）=> 栈净变化 +1
        ///   替换：ldarg.1（压入 Player）+ call IsDedicatedOrP2PRemoteRecipient(Player)（消费 1，返回 bool i4）=> 栈净变化 +1
        ///   一致。
        /// </summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(BarricadeManager), TargetMethodName)]
        public static IEnumerable<CodeInstruction> OnRegionUpdated_Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var codes = new List<CodeInstruction>(instructions);

            MethodInfo dedicatedGetter = AccessTools.PropertyGetter(typeof(Dedicator), nameof(Dedicator.IsDedicatedServer));
            MethodInfo eligibilityMethod = AccessTools.Method(
                typeof(ListenRegionSyncEligibility),
                nameof(ListenRegionSyncEligibility.IsDedicatedOrP2PRemoteRecipient),
                new System.Type[] { typeof(Player) });

            if (dedicatedGetter == null)
            {
                ReplacementCount = -1;
                throw new System.InvalidOperationException(
                    "BarricadeManagerRegionSyncPatch: Dedicator.get_IsDedicatedServer not found");
            }

            if (eligibilityMethod == null)
            {
                ReplacementCount = -1;
                throw new System.InvalidOperationException(
                    "BarricadeManagerRegionSyncPatch: IsDedicatedOrP2PRemoteRecipient not found");
            }

            int replacementCount = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                CodeInstruction instr = codes[i];
                if (instr == null) continue;

                // P0-2 返修：使用 CodeInstruction.Calls(dedicatedGetter)
                if (instr.Calls(dedicatedGetter))
                {
                    // P0-2 返修：原地修改 opcode/operand，保留 labels/blocks
                    instr.opcode = OpCodes.Ldarg_1;
                    instr.operand = null;
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, eligibilityMethod));
                    replacementCount++;
                    i++;
                }
            }

            ReplacementCount = replacementCount;

            if (replacementCount != 1)
            {
                throw new System.InvalidOperationException(
                    $"BarricadeManagerRegionSyncPatch: replacement count={replacementCount} expected=1");
            }

            RoleLogger.Info("[Shared]",
                $"[BarricadeRegionSync] OK Transpiler replacement=1/1，IL 修改已应用（原地修改，保留 labels/blocks）");
            return codes;
        }

        /// <summary>
        /// v0.2.3.13 P0-D：SendRegion Prefix 决定性日志。
        /// 仅记录日志，不影响原方法行为（不返回 false，不修改参数）。
        /// 形成完整链路证据：主机 eligible/send -> ClientMethod remote attempt/send-success -> 客机 Receive。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BarricadeManager), SendRegionMethodName)]
        public static void SendRegion_Prefix(SteamPlayer client, byte x, byte y)
        {
            if (client == null) return;

            // v0.2.3.13 NRE 修复：SteamPlayerID 重载 != 但未判空，用 ?. 绕开
            ulong steamId = client.playerID?.steamID.m_SteamID ?? 0UL;
            if (steamId == 0UL) return;

            int count;
            if (!_eligibilityLogCounts.TryGetValue(steamId, out count))
            {
                count = 0;
            }
            count++;
            _eligibilityLogCounts[steamId] = count;

            if (count > EligibilityLogLimit) return;

            string transportDesc = client.transportConnection != null
                ? client.transportConnection.GetType().Name
                : "null";

            // v0.2.3.15 P0-D：send 日志附加 escPaused 前缀（供外部审计定位 RegionSync 延迟根因）
            string escPrefix = SteamP2PFriends.Host.HostManager.EscPauseDetectorEnabled
                ? $"escPaused={SteamP2PFriends.Host.HostManager.IsEscPausedCurrent} "
                : "";

            RoleLogger.Info("[Host]",
                $"[ListenRegionSync/Barricade] send #{count}/{EligibilityLogLimit} " +
                $"{escPrefix}steamId={steamId} transport={transportDesc} step=2 region=({x},{y})");
        }

        /// <summary>
        /// v0.2.3.13 返修 P0-3：Provider.onClientDisconnected 回调。
        /// 断线时清除已不在 Provider.clients 中的 SteamID 计数，避免同一 SteamID 重连后丢失诊断日志。
        /// </summary>
        public static void OnClientDisconnected()
        {
            try
            {
                // v0.2.3.13 NRE 修复：Provider.clients 在 shutdown 期间可能为 null
                if (Provider.clients == null) return;

                var activeSteamIds = new HashSet<ulong>();
                foreach (SteamPlayer sp in Provider.clients)
                {
                    if (sp == null) continue;

                    // v0.2.3.13 NRE 修复：SteamPlayerID 重载 != 但未判空，用 ?. 绕开
                    ulong steamId = sp.playerID?.steamID.m_SteamID ?? 0UL;
                    if (steamId != 0UL)
                    {
                        activeSteamIds.Add(steamId);
                    }
                }

                var keysToRemove = new List<ulong>();
                foreach (var key in _eligibilityLogCounts.Keys)
                {
                    if (!activeSteamIds.Contains(key))
                    {
                        keysToRemove.Add(key);
                    }
                }

                foreach (var key in keysToRemove)
                {
                    _eligibilityLogCounts.Remove(key);
                }

                if (keysToRemove.Count > 0)
                {
                    RoleLogger.Info("[Shared]",
                        $"[BarricadeRegionSync] OnClientDisconnected 清除断线玩家计数 ({keysToRemove.Count} 个 steamId)");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[BarricadeRegionSync] OnClientDisconnected 异常: {ex}");
            }
        }

        /// <summary>
        /// v0.2.3.13 返修 P0-3：开新服/停服时清除所有计数。
        /// 由 HostManager.StartP2PServer / Plugin.OnDestroy 调用。
        /// </summary>
        public static void ResetAll()
        {
            int cleared = _eligibilityLogCounts.Count;
            _eligibilityLogCounts.Clear();
            RoleLogger.Info("[Shared]",
                $"[BarricadeRegionSync] ResetAll 清空所有计数 ({cleared} 个 steamId)");
        }
    }
}
