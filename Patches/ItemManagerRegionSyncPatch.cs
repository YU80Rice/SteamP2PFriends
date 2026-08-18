using HarmonyLib;
using SDG.NetTransport;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// ItemManager.onRegionUpdated step 5 远程区域同步资格 patch + askItems 决定性日志。
    ///
    ///     1. ItemManager.onRegionUpdated 中仅替换直接控制 askItems/本地分支的一个 dedicated getter。"
    ///
    /// 目标：解除 listen server 模式下"主机不向远程客机发送 Items RPC"的诅咒。
    /// vanilla 源码（U3-SDK ItemManager.cs L980-1074，step 5 @ L1015）：
    ///   if (step == 5)
    ///   {
    ///       if (Dedicator.IsDedicatedServer)   <-- Transpiler 替换此调用（L1036，全方法唯一一处）
    ///       {
    ///           // 遍历玩家周围 ITEM_REGIONS 范围，调 askItems 发送（L1038）
    ///       }
    ///   }
    ///
    ///   原生 dedicated，或 Provider.isServer && HostManager.IsP2PHostMode && recipient 为真实远端非 loopback 玩家。
    ///   在主机本地玩家、普通单机、客机进程、非 P2P 模式一律返回原生值（false）。
    ///   ListenRegionSyncEligibility.IsDedicatedOrP2PRemoteRecipient(Player) 已实现此语义。
    ///
    ///   - onRegionUpdated 签名精确解析（private instance, 7 args: Player, byte x5, ref bool）
    ///   - askItems 签名精确解析（internal instance, 4 args: ITransportConnection, byte, byte, float）
    ///   - Transpiler replacement count 必须精确等于 1
    ///   - askItems Prefix 必须登记成功
    ///   - Transpiler owner 为 com.yu80rice.steamp2pfriends，patch method 为 ItemManagerRegionSyncPatch.OnRegionUpdated_Transpiler，count=1
    ///   - askItems Prefix owner 为 com.yu80rice.steamp2pfriends，patch method 为 ItemManagerRegionSyncPatch.AskItems_Prefix，count=1
    ///   任一失败并入 DiagnosticBuildValid fail-closed。
    ///
    ///   - 不手动写 loaded flag
    ///   - 不在 Postfix 重复调用发送方法
    ///   - 不伪造全局 dedicated 状态
    ///   - 不修改 load/generate/client cleanup 分支
    /// </summary>
    public static class ItemManagerRegionSyncPatch
    {
        public static bool AllRegistrationsSucceeded { get; private set; }
        public static string RegistrationSummary { get; private set; } = "未登记";
        public static int ReplacementCount { get; private set; } = -1;
        public static bool SignatureResolved { get; private set; }
        public static string SignatureSummary { get; private set; } = "未自检";
        public static bool AskItemsPrefixRegistered { get; private set; }

        public static bool TranspilerOwnerVerified { get; private set; }
        public static bool PrefixOwnerVerified { get; private set; }
        public static string TranspilerOwnerSummary { get; private set; } = "未自检";
        public static string PrefixOwnerSummary { get; private set; } = "未自检";

        private const int EligibilityLogLimit = 5;
        private static readonly Dictionary<ulong, int> _eligibilityLogCounts = new Dictionary<ulong, int>();

        private const string HarmonyId = SteamP2PFriendsPlugin.HARMONY_ID;
        private const string TargetMethodName = "onRegionUpdated";
        private const string AskItemsMethodName = "askItems";
        private const string PatchTranspilerName = nameof(OnRegionUpdated_Transpiler);
        private const string PatchAskItemsPrefixName = nameof(AskItems_Prefix);

        public static bool RegisterManual(Harmony harmony)
        {
            RoleLogger.Info("[Shared]", "[ItemRegionSync] === 手动登记 Transpiler + askItems Prefix（v0.2.3.29 P0-B）===");

            if (harmony == null)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = "harmony=null";
                RoleLogger.Error("[Shared]", $"[ItemRegionSync] !!! {RegistrationSummary}");
                return false;
            }

            bool sigOk = VerifyTargetSignature();
            if (!sigOk)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = $"onRegionUpdated 签名自检失败 ({SignatureSummary})";
                RoleLogger.Error("[Shared]", $"[ItemRegionSync] !!! {RegistrationSummary}");
                return false;
            }

            bool transpilerOk = RegisterTranspiler(harmony);
            if (!transpilerOk)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = $"Transpiler 登记失败 (replacement={ReplacementCount})";
                RoleLogger.Error("[Shared]", $"[ItemRegionSync] !!! {RegistrationSummary}");
                return false;
            }

            bool prefixOk = RegisterAskItemsPrefix(harmony);
            AskItemsPrefixRegistered = prefixOk;
            if (!prefixOk)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = "askItems Prefix 登记失败";
                RoleLogger.Error("[Shared]", $"[ItemRegionSync] !!! {RegistrationSummary}");
                return false;
            }

            AllRegistrationsSucceeded = true;
            RegistrationSummary = $"signature={SignatureResolved}, replacement=1/1, askItemsPrefix=true, " +
                $"transpilerOwner={TranspilerOwnerVerified}, prefixOwner={PrefixOwnerVerified}";
            RoleLogger.Info("[Shared]",
                $"[ItemRegionSync] OK 手动登记成功 summary={RegistrationSummary}");
            return true;
        }

        private static bool RegisterTranspiler(Harmony harmony)
        {
            try
            {
                MethodInfo original = AccessTools.Method(typeof(ItemManager), TargetMethodName);
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", "[ItemRegionSync] !!! onRegionUpdated AccessTools.Method 返回 null");
                    return false;
                }

                MethodInfo transpiler = AccessTools.Method(typeof(ItemManagerRegionSyncPatch), PatchTranspilerName);
                if (transpiler == null)
                {
                    RoleLogger.Error("[Shared]", "[ItemRegionSync] !!! Transpiler 方法未找到");
                    return false;
                }

                harmony.Patch(original, transpiler: new HarmonyMethod(transpiler));

                if (ReplacementCount != 1)
                {
                    RoleLogger.Error("[Shared]",
                        $"[ItemRegionSync] !!! DIAGNOSTIC BUILD INVALID: replacement count={ReplacementCount} 期望=1");
                    return false;
                }

                bool ownerOk = VerifyPatchOwner(original, isTranspiler: true);
                if (!ownerOk)
                {
                    RoleLogger.Error("[Shared]",
                        $"[ItemRegionSync] !!! DIAGNOSTIC BUILD INVALID: Transpiler owner 自检失败 summary={TranspilerOwnerSummary}");
                    return false;
                }

                RoleLogger.Info("[Shared]",
                    $"[ItemRegionSync] OK Transpiler 已登记 (replacement=1/1, owner={TranspilerOwnerVerified})");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[ItemRegionSync] !!! RegisterTranspiler 异常: {ex}");
                return false;
            }
        }

        private static bool RegisterAskItemsPrefix(Harmony harmony)
        {
            try
            {
                // askItems internal 方法签名：void askItems(ITransportConnection, byte, byte, float)
                MethodInfo original = AccessTools.Method(typeof(ItemManager), AskItemsMethodName,
                    new System.Type[] { typeof(ITransportConnection), typeof(byte), typeof(byte), typeof(float) });
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", "[ItemRegionSync] !!! askItems(4 args) AccessTools.Method 返回 null");
                    return false;
                }

                MethodInfo prefix = AccessTools.Method(typeof(ItemManagerRegionSyncPatch), PatchAskItemsPrefixName);
                if (prefix == null)
                {
                    RoleLogger.Error("[Shared]", "[ItemRegionSync] !!! askItems Prefix 方法未找到");
                    return false;
                }

                harmony.Patch(original, prefix: new HarmonyMethod(prefix));

                bool ownerOk = VerifyPatchOwner(original, isTranspiler: false);
                if (!ownerOk)
                {
                    RoleLogger.Error("[Shared]",
                        $"[ItemRegionSync] !!! DIAGNOSTIC BUILD INVALID: askItems Prefix owner 自检失败 summary={PrefixOwnerSummary}");
                    return false;
                }

                RoleLogger.Info("[Shared]",
                    $"[ItemRegionSync] OK askItems Prefix 已登记 (owner={PrefixOwnerVerified})");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[ItemRegionSync] !!! RegisterAskItemsPrefix 异常: {ex}");
                return false;
            }
        }

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

                string expectedMethodName = isTranspiler ? PatchTranspilerName : PatchAskItemsPrefixName;
                System.Type expectedDeclaringType = typeof(ItemManagerRegionSyncPatch);

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

        private static bool VerifyTargetSignature()
        {
            try
            {
                MethodInfo method = AccessTools.Method(typeof(ItemManager), TargetMethodName);
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
                    $"[ItemRegionSync] OK 签名自检通过: {SignatureSummary}");
                return true;
            }
            catch (System.Exception ex)
            {
                SignatureResolved = false;
                SignatureSummary = $"异常: {ex.Message}";
                RoleLogger.Error("[Shared]", $"[ItemRegionSync] !!! 签名自检异常: {ex}");
                return false;
            }
        }

        /// <summary>
        /// 替换 vanilla onRegionUpdated step 5 中的 Dedicator.get_IsDedicatedServer() 调用
        /// 为 ListenRegionSyncEligibility.IsDedicatedOrP2PRemoteRecipient(player)。
        ///
        /// 栈平衡：
        ///   原版：call get_IsDedicatedServer()（无参数，返回 bool i4）=> 栈净变化 +1
        ///   替换：ldarg.1（压入 Player）+ call IsDedicatedOrP2PRemoteRecipient(Player)（消费 1，返回 bool i4）=> 栈净变化 +1
        ///   一致。
        ///
        /// 严格禁止：不可误伤 ItemManager.onRegionUpdated 中其他 Dedicator.IsDedicatedServer 调用
        /// （U3-SDK 确认本方法仅 1 处调用，但若未来版本新增必须靠 replacementCount=1 自检拦截）。
        /// </summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(ItemManager), TargetMethodName)]
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
                    "ItemManagerRegionSyncPatch: Dedicator.get_IsDedicatedServer not found");
            }

            if (eligibilityMethod == null)
            {
                ReplacementCount = -1;
                throw new System.InvalidOperationException(
                    "ItemManagerRegionSyncPatch: IsDedicatedOrP2PRemoteRecipient not found");
            }

            int replacementCount = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                CodeInstruction instr = codes[i];
                if (instr == null) continue;

                if (instr.Calls(dedicatedGetter))
                {
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
                    $"ItemManagerRegionSyncPatch: replacement count={replacementCount} expected=1");
            }

            RoleLogger.Info("[Shared]",
                $"[ItemRegionSync] OK Transpiler replacement=1/1，IL 修改已应用（原地修改，保留 labels/blocks）");
            return codes;
        }

        /// <summary>
        /// 仅记录日志，不影响原方法行为。
        /// askItems 签名：void askItems(ITransportConnection, byte, byte, float)
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ItemManager), AskItemsMethodName)]
        public static void AskItems_Prefix(ITransportConnection transportConnection, byte x, byte y)
        {
            if (transportConnection == null) return;

            ulong steamId = 0UL;
            foreach (SteamPlayer sp in Provider.clients)
            {
                if (sp != null && sp.transportConnection == transportConnection)
                {
                    steamId = sp.playerID?.steamID.m_SteamID ?? 0UL;
                    break;
                }
            }

            if (steamId == 0UL) return;

            int count;
            if (!_eligibilityLogCounts.TryGetValue(steamId, out count))
            {
                count = 0;
            }
            count++;
            _eligibilityLogCounts[steamId] = count;

            if (count > EligibilityLogLimit) return;

            string transportDesc = transportConnection.GetType().Name;

            string escPrefix = SteamP2PFriends.Host.HostManager.EscPauseDetectorEnabled
                ? $"escPaused={SteamP2PFriends.Host.HostManager.IsEscPausedCurrent} "
                : "";

            RoleLogger.Info("[Host]",
                $"[ListenRegionSync/Item] send #{count}/{EligibilityLogLimit} " +
                $"{escPrefix}steamId={steamId} transport={transportDesc} step=5 region=({x},{y})");
        }

        public static void OnClientDisconnected()
        {
            try
            {
                if (Provider.clients == null) return;

                var activeSteamIds = new HashSet<ulong>();
                foreach (SteamPlayer sp in Provider.clients)
                {
                    if (sp == null) continue;
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
                        $"[ItemRegionSync] OnClientDisconnected 清除断线玩家计数 ({keysToRemove.Count} 个 steamId)");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[ItemRegionSync] OnClientDisconnected 异常: {ex}");
            }
        }

        public static void ResetAll()
        {
            int cleared = _eligibilityLogCounts.Count;
            _eligibilityLogCounts.Clear();
            RoleLogger.Info("[Shared]",
                $"[ItemRegionSync] ResetAll 清空所有计数 ({cleared} 个 steamId)");
        }
    }
}
