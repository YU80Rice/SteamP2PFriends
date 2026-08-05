using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.34 P0-B-3 远端客机区域物品预生成（Codex 第二十二次双机测试外部审计 §4.2 授权实施）：
    ///
    /// 根因（U3-SDK ItemManager.cs:920-952 onLevelLoaded + 1028-1031 onRegionUpdated step 5）：
    ///
    /// onLevelLoaded L941:
    ///   if (Dedicator.IsDedicatedServer)  <-- 阻断点
    ///     for x, y in WORLD_SIZE:
    ///       generateItems(x, y)  <-- 全地图预生成地图物品
    ///
    /// onRegionUpdated step 5 L1028-1031:
    ///   if (player.channel.IsLocalPlayer)
    ///     generateItems(x, y)  <-- 懒加载：仅本地玩家进入新区域时生成
    ///
    /// 问题：
    ///   - listen host 下 Dedicator.IsDedicatedServer=false，onLevelLoaded 跳过全地图预生成
    ///   - 远端客机进入新区域时 player.channel.IsLocalPlayer=false，onRegionUpdated 不触发 generateItems
    ///   - 结果：regions[x,y].items 始终为空，askItems 发送空包，客机看不到地面物品
    ///
    /// 第二十二次双机测试决定性证据：
    ///   - 主机日志：askItems #1-20/20 isLoopback=False（发送入口已调用）
    ///   - 客机日志：ReceiveItems #1-20/20（客机收到空包）
    ///   - 用户反馈：客机在非主机城镇地面物品不刷新
    ///
    /// 修复方案（Codex §4.2 方案 A 授权实施）：
    ///   Transpiler 替换 L941 的 Dedicator.IsDedicatedServer 调用
    ///   为 ListenRegionSyncEligibility.IsDedicatedOrP2PHost() 调用。
    ///
    ///   替换后语义：
    ///     - dedicated server: IsDedicatedOrP2PHost=true（vanilla 行为不变）
    ///     - listen host: IsDedicatedOrP2PHost=true（新增，onLevelLoaded 时全地图预生成）
    ///     - 普通单机/客机/菜单: IsDedicatedOrP2PHost=false（跳过预生成，保持懒加载）
    ///
    ///   预生成后，远端客机进入任意区域时 regions[x,y].items 已有地图生成物品，
    ///   askItems 发送非空包，客机正常看到地面物品。
    ///
    /// 栈平衡：
    ///   原版：call get_IsDedicatedServer()（无参数，返回 bool i4）=> 栈净变化 +1
    ///   替换：call IsDedicatedOrP2PHost()（无参数，返回 bool i4）=> 栈净变化 +1
    ///   一致。
    ///
    /// replacement count 必须精确等于 1（onLevelLoaded 中 L941 是唯一一处 IsDedicatedServer 调用）。
    ///
    /// 安全性：
    ///   - 不全局伪造 Dedicator.IsDedicatedServer
    ///   - 不修改 generateItems 实现
    ///   - 不修改 onRegionUpdated step 5 的懒加载逻辑（IsLocalPlayer 门控保留）
    ///   - 与 ItemManagerRegionSyncPatch（P0-B step 5 askItems 门控）共存无冲突
    ///   - 与 ItemManagerWorldSyncDiagnosticPatch 共存无冲突
    ///
    /// 性能影响：
    ///   - onLevelLoaded 时全地图预生成（64x64=4096 区域），与 dedicated server 行为一致
    ///   - PEI 地图实测 dedicated server 加载时间可接受，listen host 应同等
    ///   - 内存开销与 dedicated server 一致
    ///
    /// 禁止项：
    ///   - 不修改 onRegionUpdated step 5 的 generateItems 门控（IsLocalPlayer 保留）
    ///   - 不修改 L325 askItem 距离检查门控
    ///   - 不修改 L1203 OnUpdate despawn/respawn 门控（!IsDedicatedServer 提前 return 保留）
    /// </summary>
    public static class ItemManagerP0B3PreGeneratePatch
    {
        public static bool AllRegistrationsSucceeded { get; private set; }
        public static string RegistrationSummary { get; private set; } = "未登记";
        public static int ReplacementCount { get; private set; } = -1;
        public static int TotalDedicatedCalls { get; private set; } = -1;
        public static bool SignatureResolved { get; private set; }
        public static string SignatureSummary { get; private set; } = "未自检";

        public static bool TranspilerOwnerVerified { get; private set; }
        public static string TranspilerOwnerSummary { get; private set; } = "未自检";

        public static bool PrefixRegistered { get; private set; }
        public static bool PostfixRegistered { get; private set; }

        private const string HarmonyId = SteamP2PFriendsPlugin.HARMONY_ID;
        private const string TargetMethodName = "onLevelLoaded";
        private const string PatchTranspilerName = nameof(OnLevelLoaded_Transpiler);

        public static bool RegisterManual(Harmony harmony)
        {
            RoleLogger.Info("[Shared]", "[P0-B-3/Item] === 手动登记 Transpiler（v0.2.3.34 P0-B-3 远端客机区域物品预生成）===");

            if (harmony == null)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = "harmony=null";
                RoleLogger.Error("[Shared]", $"[P0-B-3/Item] !!! {RegistrationSummary}");
                return false;
            }

            bool sigOk = VerifyTargetSignature();
            if (!sigOk)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = $"onLevelLoaded 签名自检失败 ({SignatureSummary})";
                RoleLogger.Error("[Shared]", $"[P0-B-3/Item] !!! {RegistrationSummary}");
                return false;
            }

            bool transpilerOk = RegisterTranspiler(harmony);
            if (!transpilerOk)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = $"Transpiler 登记失败 (replacement={ReplacementCount})";
                RoleLogger.Error("[Shared]", $"[P0-B-3/Item] !!! {RegistrationSummary}");
                return false;
            }

            bool prefixPostfixOk = RegisterPrefixPostfix(harmony);
            if (!prefixPostfixOk)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = $"Prefix/Postfix 登记失败 (prefix={PrefixRegistered}, postfix={PostfixRegistered})";
                RoleLogger.Error("[Shared]", $"[P0-B-3/Item] !!! {RegistrationSummary}");
                return false;
            }

            AllRegistrationsSucceeded = true;
            RegistrationSummary = $"signature={SignatureResolved}, replacement=1/1, totalDedicatedCalls={TotalDedicatedCalls}, transpilerOwner={TranspilerOwnerVerified}, prefix={PrefixRegistered}, postfix={PostfixRegistered}";
            RoleLogger.Info("[Shared]",
                $"[P0-B-3/Item] OK 手动登记成功 summary={RegistrationSummary}");
            return true;
        }

        private static bool RegisterPrefixPostfix(Harmony harmony)
        {
            try
            {
                MethodInfo original = AccessTools.Method(typeof(ItemManager), TargetMethodName);
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", "[P0-B-3/Item] !!! RegisterPrefixPostfix: onLevelLoaded 返回 null");
                    return false;
                }

                MethodInfo prefix = AccessTools.Method(typeof(ItemManagerP0B3PreGeneratePatch), nameof(OnLevelLoaded_Prefix));
                MethodInfo postfix = AccessTools.Method(typeof(ItemManagerP0B3PreGeneratePatch), nameof(OnLevelLoaded_Postfix));

                if (prefix == null || postfix == null)
                {
                    RoleLogger.Error("[Shared]", "[P0-B-3/Item] !!! Prefix/Postfix 方法未找到");
                    return false;
                }

                harmony.Patch(original,
                    prefix: new HarmonyMethod(prefix),
                    postfix: new HarmonyMethod(postfix));

                PrefixRegistered = true;
                PostfixRegistered = true;
                RoleLogger.Info("[Shared]",
                    "[P0-B-3/Item] OK Prefix/Postfix 已登记 (v0.2.3.35 P0-B-4 诊断日志)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P0-B-3/Item] !!! RegisterPrefixPostfix 异常: {ex}");
                return false;
            }
        }

        private static bool RegisterTranspiler(Harmony harmony)
        {
            try
            {
                MethodInfo original = AccessTools.Method(typeof(ItemManager), TargetMethodName);
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", "[P0-B-3/Item] !!! onLevelLoaded AccessTools.Method 返回 null");
                    return false;
                }

                MethodInfo transpiler = AccessTools.Method(typeof(ItemManagerP0B3PreGeneratePatch), PatchTranspilerName);
                if (transpiler == null)
                {
                    RoleLogger.Error("[Shared]", "[P0-B-3/Item] !!! Transpiler 方法未找到");
                    return false;
                }

                harmony.Patch(original, transpiler: new HarmonyMethod(transpiler));

                if (ReplacementCount != 1)
                {
                    RoleLogger.Error("[Shared]",
                        $"[P0-B-3/Item] !!! DIAGNOSTIC BUILD INVALID: replacement count={ReplacementCount} 期望=1 (totalDedicatedCalls={TotalDedicatedCalls})");
                    return false;
                }

                bool ownerOk = VerifyPatchOwner(original);
                if (!ownerOk)
                {
                    RoleLogger.Error("[Shared]",
                        $"[P0-B-3/Item] !!! DIAGNOSTIC BUILD INVALID: Transpiler owner 自检失败 summary={TranspilerOwnerSummary}");
                    return false;
                }

                RoleLogger.Info("[Shared]",
                    $"[P0-B-3/Item] OK Transpiler 已登记 (replacement=1/1, totalDedicatedCalls={TotalDedicatedCalls}, owner={TranspilerOwnerVerified})");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P0-B-3/Item] !!! RegisterTranspiler 异常: {ex}");
                return false;
            }
        }

        private static bool VerifyPatchOwner(MethodInfo original)
        {
            try
            {
                HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
                System.Collections.ICollection patches = info?.Transpilers as System.Collections.ICollection;

                if (patches == null || patches.Count == 0)
                {
                    TranspilerOwnerVerified = false;
                    TranspilerOwnerSummary = "transpilers count=0";
                    return false;
                }

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
                            && patchMethod.DeclaringType == typeof(ItemManagerP0B3PreGeneratePatch)
                            && patchMethod.Name == PatchTranspilerName)
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
                    TranspilerOwnerVerified = false;
                    TranspilerOwnerSummary = summary;
                    return false;
                }

                TranspilerOwnerVerified = true;
                TranspilerOwnerSummary = summary;
                return true;
            }
            catch (System.Exception ex)
            {
                TranspilerOwnerVerified = false;
                TranspilerOwnerSummary = $"异常: {ex.Message}";
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
                if (ps.Length != 1)
                {
                    SignatureResolved = false;
                    SignatureSummary = $"paramCount={ps.Length} 期望=1";
                    return false;
                }

                if (ps[0].ParameterType != typeof(int))
                {
                    SignatureResolved = false;
                    SignatureSummary = $"param0={ps[0].ParameterType.Name} 期望=Int32";
                    return false;
                }

                if (method.ReturnType != typeof(void))
                {
                    SignatureResolved = false;
                    SignatureSummary = $"ReturnType={method.ReturnType.Name} 期望=void";
                    return false;
                }

                SignatureResolved = true;
                SignatureSummary = "private instance void onLevelLoaded(int level)";
                RoleLogger.Info("[Shared]",
                    $"[P0-B-3/Item] OK 签名自检通过: {SignatureSummary}");
                return true;
            }
            catch (System.Exception ex)
            {
                SignatureResolved = false;
                SignatureSummary = $"异常: {ex.Message}";
                RoleLogger.Error("[Shared]", $"[P0-B-3/Item] !!! 签名自检异常: {ex}");
                return false;
            }
        }

        /// <summary>
        /// v0.2.3.34 P0-B-3 远端客机区域物品预生成 Transpiler 主实现。
        /// 替换 vanilla onLevelLoaded 中的 Dedicator.get_IsDedicatedServer() 调用
        /// 为 ListenRegionSyncEligibility.IsDedicatedOrP2PHost()。
        ///
        /// 栈平衡：
        ///   原版：call get_IsDedicatedServer()（无参数，返回 bool i4）=> 栈净变化 +1
        ///   替换：call IsDedicatedOrP2PHost()（无参数，返回 bool i4）=> 栈净变化 +1
        ///   一致。
        ///
        /// replacement count 必须精确等于 1（L941 是 onLevelLoaded 中唯一一处 IsDedicatedServer 调用）。
        /// totalDedicatedCalls 记录全方法 IsDedicatedServer 调用总数，便于未来 vanilla 升级时审计。
        /// </summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(ItemManager), TargetMethodName)]
        public static IEnumerable<CodeInstruction> OnLevelLoaded_Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var codes = new List<CodeInstruction>(instructions);

            MethodInfo dedicatedGetter = AccessTools.PropertyGetter(typeof(Dedicator), nameof(Dedicator.IsDedicatedServer));
            MethodInfo eligibilityMethod = AccessTools.Method(
                typeof(ListenRegionSyncEligibility),
                nameof(ListenRegionSyncEligibility.IsDedicatedOrP2PHost),
                System.Type.EmptyTypes);

            if (dedicatedGetter == null)
            {
                ReplacementCount = -1;
                TotalDedicatedCalls = -1;
                throw new System.InvalidOperationException(
                    "ItemManagerP0B3PreGeneratePatch: Dedicator.get_IsDedicatedServer not found");
            }

            if (eligibilityMethod == null)
            {
                ReplacementCount = -1;
                TotalDedicatedCalls = -1;
                throw new System.InvalidOperationException(
                    "ItemManagerP0B3PreGeneratePatch: IsDedicatedOrP2PHost not found");
            }

            int replacementCount = 0;
            int totalDedicatedCalls = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                CodeInstruction instr = codes[i];
                if (instr == null) continue;

                if (instr.Calls(dedicatedGetter))
                {
                    totalDedicatedCalls++;
                    instr.opcode = OpCodes.Call;
                    instr.operand = eligibilityMethod;
                    replacementCount++;
                }
            }

            ReplacementCount = replacementCount;
            TotalDedicatedCalls = totalDedicatedCalls;

            if (replacementCount != 1)
            {
                throw new System.InvalidOperationException(
                    $"ItemManagerP0B3PreGeneratePatch: replacement count={replacementCount} expected=1 (totalDedicatedCalls={totalDedicatedCalls})");
            }

            RoleLogger.Info("[Shared]",
                $"[P0-B-3/Item] OK Transpiler replacement=1/1，IL 修改已应用（L941 IsDedicatedServer -> IsDedicatedOrP2PHost，totalDedicatedCalls={totalDedicatedCalls}）");
            return codes;
        }

        /// <summary>
        /// v0.2.3.35 P0-B-4 诊断日志（Codex 第二十三次双机测试外部审计 §4.2 授权实施）：
        ///
        /// 在 vanilla onLevelLoaded 返回后输出诊断信息，用于判断：
        ///   1. onLevelLoaded 是否被触发
        ///   2. IsDedicatedOrP2PHost() 在 onLevelLoaded 时的返回值
        ///   3. LevelItems.spawns.Count（generateItems 的输入数据是否就绪）
        ///   4. ItemManager.regions 是否已初始化
        ///
        /// Codex §4.2 原话：
        ///   "在 ItemManager.onLevelLoaded 方法入口或 Transpiler 中增加强制日志：
        ///    [P0-B-3/Item] onLevelLoaded invoked level={level} IsDedicatedOrP2PHost={ListenRegionSyncEligibility.IsDedicatedOrP2PHost()}；
        ///    在 generateItems 循环前后记录 LevelItems.spawns.Count 与耗时。"
        ///
        /// U3-SDK 溯源：
        ///   - D:/Agent-工作目录/U3-SDK/Assets/Runtime/Assembly-CSharp/Unturned/Managers/ItemManager.cs:920-952 onLevelLoaded
        ///   - D:/Agent-工作目录/U3-SDK/Assets/Runtime/Assembly-CSharp/Unturned/Managers/ItemManager.cs:847-908 generateItems
        ///
        /// 注意：Postfix 在 vanilla onLevelLoaded 返回后调用，此时 generateItems 循环已执行完毕。
        /// 通过记录 _postfixStopwatchStart（在 Prefix 中捕获）可计算 onLevelLoaded 全方法耗时。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ItemManager), TargetMethodName)]
        public static void OnLevelLoaded_Postfix(int level)
        {
            try
            {
                bool eligible = ListenRegionSyncEligibility.IsDedicatedOrP2PHost();
                // U3-SDK: LevelItems.spawns 是 List<ItemSpawnpoint>[,]（二维数组）
                //   D:/Agent-工作目录/U3-SDK/Assets/Runtime/Assembly-CSharp/Unturned/Level/LevelItems.cs:38-39
                int spawnsDim0 = -1, spawnsDim1 = -1;
                try
                {
                    if (LevelItems.spawns != null)
                    {
                        spawnsDim0 = LevelItems.spawns.GetLength(0);
                        spawnsDim1 = LevelItems.spawns.GetLength(1);
                    }
                }
                catch { /* 边界访问异常时保持 -1 */ }
                int regionsDim0 = (ItemManager.regions != null) ? ItemManager.regions.GetLength(0) : -1;
                int regionsDim1 = (ItemManager.regions != null) ? ItemManager.regions.GetLength(1) : -1;

                float elapsed = (_postfixStopwatchStart > 0f)
                    ? Time.realtimeSinceStartup - _postfixStopwatchStart
                    : -1f;

                RoleLogger.Info("[Host]",
                    $"[P0-B-3/Item] onLevelLoaded postfix level={level} eligible={eligible} " +
                    $"LevelItems.spawns={spawnsDim0}x{spawnsDim1} " +
                    $"regions={regionsDim0}x{regionsDim1} " +
                    $"elapsed={elapsed:F2}s");

                _postfixStopwatchStart = 0f;

                // v0.2.3.37 P0-B-6：在 onLevelLoaded Postfix 中尝试触发全地图 generateItems
                //   Codex 第二十五次审计 §4.1 方案 A 授权实施
                //   25th 测试证明 P0-B-5 在 OnServerHosted 时机过早（LevelItems.spawns=null）
                //   P0-B-6 在 onLevelLoaded Postfix 中检测 spawns 就绪后触发，绕过时序问题
                try
                {
                    ItemManagerP0B6RegenerateOnLevelLoadedPatch.TryRegenerateOnLevelLoaded(level);
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Error("[Shared]", $"[P0-B-6] TryRegenerateOnLevelLoaded 调用异常（不阻断）: {ex}");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P0-B-3/Item] OnLevelLoaded_Postfix 异常: {ex}");
            }
        }

        /// <summary>
        /// Prefix 在 vanilla onLevelLoaded 入口捕获时间戳，供 Postfix 计算耗时。
        /// 不修改 vanilla 逻辑（return true）。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ItemManager), TargetMethodName)]
        public static void OnLevelLoaded_Prefix(int level)
        {
            _postfixStopwatchStart = Time.realtimeSinceStartup;
            RoleLogger.Info("[Host]",
                $"[P0-B-3/Item] onLevelLoaded invoked level={level} IsDedicatedOrP2PHost={ListenRegionSyncEligibility.IsDedicatedOrP2PHost()}");
        }

        private static float _postfixStopwatchStart = 0f;
    }
}
