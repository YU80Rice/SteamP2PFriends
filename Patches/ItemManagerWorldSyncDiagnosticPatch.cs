using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Reflection;
using UnityEngine;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.27 P0-A 决定性诊断（Codex 第 7 节 P0-A + 静态审计返修）：
    /// ItemManager 世界同步链路五段证据诊断。
    ///
    /// 五段证据闭环：
    ///   1. 源事件：onRegionUpdated step=5（玩家进入新区域）
    ///   2. 资格/目标：player.movement.loadedRegions[x,y].isItemsLoaded + Regions.checkSafe(x,y)
    ///   3. 发送入口：askItems(ITransportConnection, byte, byte, float) 实际调用
    ///   4. 客机 Receive 入口：ReceiveItem / ReceiveItems
    ///   5. Receive 后状态/拒绝门控：ItemManager.regions[x,y].isNetworked before/after
    ///
    /// v0.2.3.27-P0-A 返修（Codex 静态审计 NO-GO）：
    ///   - P0-2：新增 VerifyRegistration，identity-based 自检（owner + MethodInfo 双重验证，
    ///     容忍同 owner 的其他 patch 共存）
    ///   - P0-3：补齐 askItems Prefix（真实发送入口）
    ///   - P0-4：改读 player.movement.loadedRegions[x,y].isItemsLoaded，不再反射不存在的 isItemsLoaded 静态字段
    ///   - P0-5：ReceiveItem Prefix 读取 Regions.checkSafe + regions[x,y].isNetworked before；
    ///           Postfix 记录 after + 落地结果
    ///   - P1-1：onRegionUpdated 使用 TryAcquirePlayerQuota（玩家级配额）
    ///   - P1-4：Loopback transport 改用精确 FullName 常量相等比较
    ///
    /// v0.2.3.27-P0-A 第二轮返修（Codex 第三轮 GO 复审非阻断修正）：
    ///   - ReceiveItem wasAccepted 语义澄清：仅证明包通过 checkSafe + isNetworked 门控并进入
    ///     pendingInstantiations 待实例化流程，不直接证明物品最终已在世界中可见（实例化由
    ///     后续 PlayerLoop/garbageCollector 阶段异步消费 pendingInstantiations 队列完成）。
    ///     wasAccepted=true -> 包未在 ReceiveItem 入口被丢弃；物品可见性需观察客机 ItemManager
    ///     .regions[x,y].items 数据或客户端 ItemDropzer 实例。
    ///
    /// vanilla 源码（U3-SDK ItemManager.cs）：
    ///   - onRegionUpdated: L980（step 5 askItems 调用点 L1036-1038）
    ///   - askItems internal: L642 `internal void askItems(ITransportConnection, byte, byte, float)`
    ///   - dropItem: L157（SendItem.Invoke L212）
    ///   - ReceiveItem: L527（静态）
    ///   - ReceiveItems: L565（静态，参数 in ClientInvocationContext）
    ///   - regions 字段: L59 `public static ItemRegion[,] regions`
    ///   - ItemRegion.isNetworked: ItemRegion.cs L17
    /// </summary>
    public static class ItemManagerWorldSyncDiagnosticPatch
    {
        private const string PointPrefix = "[WorldSyncDiag/Item]";

        // P1-4：精确 FullName 常量（Loopback transport 类型判断）
        private const string LoopbackTransportFullName = "SDG.NetTransport.Loopback.TransportConnection_Loopback";

        // v0.2.3.27-P0-A 冒烟中止返修（Codex P0-R8）：vanilla 目标完整参数类型表，
        // 由 RegisterManual 与 VerifyRegistration 共用，避免两套容易漂移的局部数组。
        //   - onRegionUpdated(Player, byte, byte, byte, byte, byte, ref bool)
        //     最后一个 canIncrementIndex 是 ref bool -&gt; typeof(bool).MakeByRefType()
        //   - dropItem(Item, Vector3, bool, bool, bool)
        //   - askItems(ITransportConnection, byte, byte, float) - internal 重载，与 public askItems(CSteamID, byte, byte) 区分
        //   - ReceiveItem(byte, byte, ushort, byte, byte, byte[], Vector3, uint, bool)
        //     vanilla 签名，__state 是 Harmony 注入参数，不计入 original 参数类型
        //   - ReceiveItems(in ClientInvocationContext) -&gt; typeof(ClientInvocationContext).MakeByRefType()
        private static readonly System.Type[] VanillaOnRegionUpdatedParamTypes =
        {
            typeof(Player),
            typeof(byte), typeof(byte),
            typeof(byte), typeof(byte),
            typeof(byte),
            typeof(bool).MakeByRefType()
        };
        private static readonly System.Type[] VanillaDropItemParamTypes =
        {
            typeof(Item),
            typeof(Vector3),
            typeof(bool), typeof(bool), typeof(bool)
        };
        private static readonly System.Type[] VanillaAskItemsParamTypes =
        {
            typeof(SDG.NetTransport.ITransportConnection),
            typeof(byte), typeof(byte),
            typeof(float)
        };
        private static readonly System.Type[] VanillaReceiveItemParamTypes =
        {
            typeof(byte), typeof(byte),
            typeof(ushort),
            typeof(byte), typeof(byte), typeof(byte[]),
            typeof(Vector3),
            typeof(uint),
            typeof(bool)
        };
        private static readonly System.Type[] VanillaReceiveItemsParamTypes =
        {
            typeof(SDG.Unturned.ClientInvocationContext).MakeByRefType()
        };

        public static bool OnRegionUpdatedPrefixRegistered { get; private set; }
        public static bool DropItemPrefixRegistered { get; private set; }
        public static bool AskItemsPrefixRegistered { get; private set; }
        public static bool ReceiveItemPrefixRegistered { get; private set; }
        public static bool ReceiveItemPostfixRegistered { get; private set; }
        public static bool ReceiveItemsPrefixRegistered { get; private set; }
        public static bool AllRegistrationsSucceeded =>
            OnRegionUpdatedPrefixRegistered && DropItemPrefixRegistered && AskItemsPrefixRegistered
            && ReceiveItemPrefixRegistered && ReceiveItemPostfixRegistered && ReceiveItemsPrefixRegistered;

        /// <summary>
        /// v0.2.3.27-P0-A 手动登记（Codex 外部审计裁决 P0-R1～R8）：
        /// 6 个 hook 精确、幂等的 identity-based 手动登记。
        ///
        /// P0-R1：所有 vanilla 目标使用完整参数类型解析（类级别静态字段 VanillaXxxParamTypes）。
        /// P0-R2：identity-based 幂等，登记前后均按 original + owner + Patch MethodInfo + Prefix/Postfix 类型精确验证。
        /// P0-R3：每个 hook 独立 try/catch（由 RegisterIdentityPatch 内部处理），一个失败不阻止其他。
        /// P0-R4：ReceiveItem 的 Prefix 和 Postfix 分别精确登记、分别核验。
        /// P0-R5：RegisterManual 返回值不绕过 VerifyRegistration，最终权威仍是 6 个 VerifyRegistration 聚合至 DiagnosticBuildValid。
        /// P0-R6：仅新增手动登记，不修改 Prefix/Postfix 诊断行为，不实施 P0-B/P0-C 功能修复。
        /// P0-R7：登记前预检查静默，登记后/最终验证失败才输出 Error。
        /// P0-R8：RegisterManual 与 VerifyRegistration 共用类级别 VanillaXxxParamTypes。
        /// </summary>
        public static bool RegisterManual(Harmony harmony)
        {
            RoleLogger.Info("[Shared]", "[WorldSyncDiag/Item] === 手动登记 6 个 hook（P0-R1～R8 identity-based 幂等）===");

            var patchType = typeof(ItemManagerWorldSyncDiagnosticPatch);

            bool r1, r2, r3, r4, r5, r6;
            try
            {
                r1 = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(ItemManager), "onRegionUpdated", VanillaOnRegionUpdatedParamTypes,
                    AccessTools.Method(patchType, "OnRegionUpdated_Prefix"),
                    HarmonyPatchType.Prefix, "Item.onRegionUpdated.Pre");
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Item] onRegionUpdated.Pre 登记异常: {ex}"); r1 = false; }

            try
            {
                r2 = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(ItemManager), "dropItem", VanillaDropItemParamTypes,
                    AccessTools.Method(patchType, "DropItem_Prefix"),
                    HarmonyPatchType.Prefix, "Item.dropItem.Pre");
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Item] dropItem.Pre 登记异常: {ex}"); r2 = false; }

            try
            {
                r3 = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(ItemManager), "askItems", VanillaAskItemsParamTypes,
                    AccessTools.Method(patchType, "AskItems_Prefix"),
                    HarmonyPatchType.Prefix, "Item.askItems.Pre");
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Item] askItems.Pre 登记异常: {ex}"); r3 = false; }

            try
            {
                r4 = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(ItemManager), "ReceiveItem", VanillaReceiveItemParamTypes,
                    AccessTools.Method(patchType, "ReceiveItem_Prefix"),
                    HarmonyPatchType.Prefix, "Item.ReceiveItem.Pre");
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Item] ReceiveItem.Pre 登记异常: {ex}"); r4 = false; }

            try
            {
                r5 = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(ItemManager), "ReceiveItem", VanillaReceiveItemParamTypes,
                    AccessTools.Method(patchType, "ReceiveItem_Postfix"),
                    HarmonyPatchType.Postfix, "Item.ReceiveItem.Post");
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Item] ReceiveItem.Post 登记异常: {ex}"); r5 = false; }

            try
            {
                r6 = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(ItemManager), "ReceiveItems", VanillaReceiveItemsParamTypes,
                    AccessTools.Method(patchType, "ReceiveItems_Prefix"),
                    HarmonyPatchType.Prefix, "Item.ReceiveItems.Pre");
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Item] ReceiveItems.Pre 登记异常: {ex}"); r6 = false; }

            bool all = r1 && r2 && r3 && r4 && r5 && r6;
            RoleLogger.Info("[Shared]",
                $"[WorldSyncDiag/Item] RegisterManual 结果: onRegionUpdated.Pre={r1} dropItem.Pre={r2} askItems.Pre={r3} " +
                $"ReceiveItem.Pre={r4} ReceiveItem.Post={r5} ReceiveItems.Pre={r6} all={all}");
            return all;
        }

        /// <summary>
        /// P0-2：精确注册自检。由 SteamP2PFriendsPlugin.VerifyCriticalPatches 调用。
        /// 失败时聚合到 DiagnosticBuildValid=false。
        ///
        /// v0.2.3.27-P0-A 返修（Codex TC-S6）：identity-based，检查"我们自己的 Prefix/Postfix MethodInfo 是否在 patches 列表中"。
        /// v0.2.3.27-P0-A 冒烟中止返修（Codex P0-R8）：复用类级别 VanillaXxxParamTypes 完整参数表，
        /// 与 RegisterManual 使用同一套 Type[]，避免两套容易漂移的局部数组。
        /// </summary>
        public static bool VerifyRegistration()
        {
            try
            {
                var patchType = typeof(ItemManagerWorldSyncDiagnosticPatch);

                MethodInfo onRegionUpdatedPre = AccessTools.Method(patchType, "OnRegionUpdated_Prefix");
                MethodInfo dropItemPre = AccessTools.Method(patchType, "DropItem_Prefix");
                MethodInfo askItemsPre = AccessTools.Method(patchType, "AskItems_Prefix");
                MethodInfo receiveItemPre = AccessTools.Method(patchType, "ReceiveItem_Prefix");
                MethodInfo receiveItemPost = AccessTools.Method(patchType, "ReceiveItem_Postfix");
                MethodInfo receiveItemsPre = AccessTools.Method(patchType, "ReceiveItems_Prefix");

                OnRegionUpdatedPrefixRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(ItemManager), "onRegionUpdated", onRegionUpdatedPre, HarmonyPatchType.Prefix, VanillaOnRegionUpdatedParamTypes);
                DropItemPrefixRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(ItemManager), "dropItem", dropItemPre, HarmonyPatchType.Prefix, VanillaDropItemParamTypes);
                AskItemsPrefixRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(ItemManager), "askItems", askItemsPre, HarmonyPatchType.Prefix, VanillaAskItemsParamTypes);
                ReceiveItemPrefixRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(ItemManager), "ReceiveItem", receiveItemPre, HarmonyPatchType.Prefix, VanillaReceiveItemParamTypes);
                ReceiveItemPostfixRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(ItemManager), "ReceiveItem", receiveItemPost, HarmonyPatchType.Postfix, VanillaReceiveItemParamTypes);
                ReceiveItemsPrefixRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(ItemManager), "ReceiveItems", receiveItemsPre, HarmonyPatchType.Prefix, VanillaReceiveItemsParamTypes);

                if (!AllRegistrationsSucceeded)
                {
                    RoleLogger.Error("[Shared]",
                        $"[WorldSyncDiag/Item] !!! 注册验证失败: " +
                        $"onRegionUpdated.Pre={OnRegionUpdatedPrefixRegistered} " +
                        $"dropItem.Pre={DropItemPrefixRegistered} " +
                        $"askItems.Pre={AskItemsPrefixRegistered} " +
                        $"ReceiveItem.Pre={ReceiveItemPrefixRegistered} " +
                        $"ReceiveItem.Post={ReceiveItemPostfixRegistered} " +
                        $"ReceiveItems.Pre={ReceiveItemsPrefixRegistered} " +
                        $"(owner={SteamP2PFriendsPlugin.HARMONY_ID}, identity-based, 共用 VanillaXxxParamTypes)");
                    return false;
                }

                RoleLogger.Info("[Shared]",
                    $"[WorldSyncDiag/Item] OK 6 个 hook 均已注册 (owner={SteamP2PFriendsPlugin.HARMONY_ID}, identity-based, 共用 VanillaXxxParamTypes)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[WorldSyncDiag/Item] VerifyRegistration 异常: {ex.Message}");
                OnRegionUpdatedPrefixRegistered = DropItemPrefixRegistered = AskItemsPrefixRegistered = false;
                ReceiveItemPrefixRegistered = ReceiveItemPostfixRegistered = ReceiveItemsPrefixRegistered = false;
                return false;
            }
        }

        // ============= 1. 源事件：onRegionUpdated step=5 =============
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ItemManager), "onRegionUpdated")]
        public static void OnRegionUpdated_Prefix(
            Player player,
            byte old_x, byte old_y,
            byte new_x, byte new_y,
            byte step,
            ref bool canIncrementIndex)
        {
            try
            {
                if (step != 5) return;

                ulong steamId = 0UL;
                try { steamId = player?.channel?.owner?.playerID?.steamID.m_SteamID ?? 0UL; } catch { }
                string maskedId = WorldSyncDiagnosticCore.MaskSteamId(steamId);

                bool isDedicated = Dedicator.IsDedicatedServer;
                string isItemsLoadedStr = ReadRegionItemsLoaded(player, new_x, new_y);
                bool checkSafe = ReadRegionsCheckSafe(new_x, new_y);

                if (!WorldSyncDiagnosticCore.TryAcquirePlayerQuota(steamId, "Item.onRegionUpdated.step5",
                    WorldSyncDiagnosticCore.PerPlayerPointLimit, out int count))
                {
                    return;
                }

                RoleLogger.Info("[Host]",
                    $"{PointPrefix} onRegionUpdated #{count}/{WorldSyncDiagnosticCore.PerPlayerPointLimit} " +
                    $"step=5 player={maskedId} region=({new_x},{new_y}) " +
                    $"isDedicated={isDedicated} checkSafe={checkSafe} " +
                    $"isItemsLoaded={isItemsLoadedStr} " +
                    $"(vanilla: askItems 仅在 isDedicated=true 时调用)");
            }
            catch (System.Exception ex)
            {
                try { RoleLogger.Error("[Shared]", $"{PointPrefix} onRegionUpdated Prefix 异常: {ex.Message}"); } catch { }
            }
        }

        // ============= 2. 发送入口：askItems(ITransportConnection, byte, byte, float) =============
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ItemManager), "askItems",
            new[] { typeof(SDG.NetTransport.ITransportConnection), typeof(byte), typeof(byte), typeof(float) })]
        public static void AskItems_Prefix(
            SDG.NetTransport.ITransportConnection transportConnection,
            byte x, byte y, float sortOrder)
        {
            try
            {
                string transportType = transportConnection?.GetType().FullName ?? "null";
                bool isLoopback = string.Equals(transportType, LoopbackTransportFullName, System.StringComparison.Ordinal);

                if (!WorldSyncDiagnosticCore.TryAcquireQuota("Item.askItems", out int count))
                {
                    return;
                }

                // v0.2.3.35 P0-B-4 诊断（Codex 第二十三次双机测试外部审计 §4.2）：
                //   输出 regions[x,y].items.Count，用于判断 P0-B-3 预生成是否生效。
                //   U3-SDK 溯源：
                //     - D:/Agent-工作目录/U3-SDK/Assets/Runtime/Assembly-CSharp/Unturned/Managers/ItemManager.cs:59 `public static ItemRegion[,] regions`
                //     - D:/Agent-工作目录/U3-SDK/Assets/Runtime/Assembly-CSharp/Unturned/Managers/ItemRegion.cs:15 `public List<ItemData> items`
                //   预期：
                //     - P0-B-3 生效：items_count > 0（预生成已填充）
                //     - P0-B-3 未生效：items_count == 0（askItems 发送空包，客机看不见地面物品）
                int itemsCount = -1;
                try
                {
                    if (ItemManager.regions != null)
                    {
                        ItemRegion region = ItemManager.regions[x, y];
                        if (region != null && region.items != null)
                        {
                            itemsCount = region.items.Count;
                        }
                    }
                }
                catch { /* 边界访问异常时保持 itemsCount=-1 */ }

                RoleLogger.Info("[Host]",
                    $"{PointPrefix} askItems #{count}/{WorldSyncDiagnosticCore.PerPointLimit} " +
                    $"region=({x},{y}) sortOrder={sortOrder:F1} " +
                    $"transport={transportType} isLoopback={isLoopback} " +
                    $"isDedicated={Dedicator.IsDedicatedServer} " +
                    $"items_count={itemsCount} " +
                    $"(真实发送入口已调用)");
            }
            catch (System.Exception ex)
            {
                try { RoleLogger.Error("[Shared]", $"{PointPrefix} askItems Prefix 异常: {ex.Message}"); } catch { }
            }
        }

        // ============= 3. 源事件：dropItem =============
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ItemManager), "dropItem")]
        public static void DropItem_Prefix(
            Item item,
            Vector3 point,
            bool playEffect,
            bool isDropped,
            bool wideSpread)
        {
            try
            {
                if (!WorldSyncDiagnosticCore.TryAcquireQuota("Item.dropItem", out int count))
                {
                    return;
                }

                RoleLogger.Info("[Host]",
                    $"{PointPrefix} dropItem #{count}/{WorldSyncDiagnosticCore.PerPointLimit} " +
                    $"id={item?.id ?? 0} amount={item?.amount ?? 0} " +
                    $"point=({point.x:F1},{point.y:F1},{point.z:F1}) " +
                    $"isDropped={isDropped} wideSpread={wideSpread} " +
                    $"isDedicated={Dedicator.IsDedicatedServer}");
            }
            catch (System.Exception ex)
            {
                try { RoleLogger.Error("[Shared]", $"{PointPrefix} dropItem Prefix 异常: {ex.Message}"); } catch { }
            }
        }

        // ============= 4. 客机 Receive 入口：ReceiveItem =============
        // P0-5：Prefix 读取 safe + isNetworked before；Postfix 读取 after + 落地结果
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ItemManager), "ReceiveItem")]
        public static void ReceiveItem_Prefix(
            byte x, byte y,
            ushort id,
            byte amount,
            byte quality,
            byte[] state,
            Vector3 point,
            uint instanceID,
            bool shouldPlayEffect,
            ref bool __state)
        {
            __state = false;
            try
            {
                bool checkSafe = ReadRegionsCheckSafe(x, y);
                bool isNetworkedBefore = ReadItemRegionIsNetworked(x, y);
                __state = isNetworkedBefore;

                if (!WorldSyncDiagnosticCore.TryAcquireQuota("Item.ReceiveItem", out int count))
                {
                    return;
                }

                RoleLogger.Info("[Client]",
                    $"{PointPrefix} ReceiveItem #{count}/{WorldSyncDiagnosticCore.PerPointLimit} " +
                    $"region=({x},{y}) id={id} amount={amount} instanceID={instanceID} " +
                    $"checkSafe={checkSafe} isNetworked_before={isNetworkedBefore} " +
                    $"(vanilla: isNetworked=false 时直接 return，包被丢弃)");
            }
            catch (System.Exception ex)
            {
                try { RoleLogger.Error("[Shared]", $"{PointPrefix} ReceiveItem Prefix 异常: {ex.Message}"); } catch { }
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ItemManager), "ReceiveItem")]
        public static void ReceiveItem_Postfix(
            byte x, byte y,
            ushort id,
            byte amount,
            byte quality,
            byte[] state,
            Vector3 point,
            uint instanceID,
            bool shouldPlayEffect,
            bool __state)
        {
            try
            {
                // v0.2.3.27-P0-A 返修（Codex TC-S6）：vanilla 逻辑为
                //   if (!Regions.checkSafe(x, y)) return;     // 拒绝
                //   if (!regions[x, y].isNetworked) return;   // 拒绝（丢弃）
                //   pendingInstantiations.Insert(...)         // 接收
                // ReceiveItem 不修改 isNetworked，所以 before == after。
                // 正确判定：
                //   wasAccepted = checkSafe && isNetworked
                //   wasDropped  = !wasAccepted
                // 原代码 `wasAccepted = isNetworkedAfter && !__state` 在 before=true after=true
                // （正常接收）时算出 false，正常接收被误记为未接受。
                bool isNetworkedAfter = ReadItemRegionIsNetworked(x, y);
                bool checkSafe = ReadRegionsCheckSafe(x, y);
                bool wasAccepted = checkSafe && isNetworkedAfter;
                bool wasDropped = !wasAccepted;

                if (!WorldSyncDiagnosticCore.TryAcquireQuota("Item.ReceiveItem.Postfix", out int count))
                {
                    return;
                }

                RoleLogger.Info("[Client]",
                    $"{PointPrefix} ReceiveItem.Postfix #{count}/{WorldSyncDiagnosticCore.PerPointLimit} " +
                    $"region=({x},{y}) id={id} " +
                    $"isNetworked_before={__state} isNetworked_after={isNetworkedAfter} " +
                    $"wasAccepted={wasAccepted} wasDropped={wasDropped} " +
                    $"(门控结果: accepted=通过 checkSafe+isNetworked 门控进入 pendingInstantiations; " +
                    $"dropped=任一门控不满足被 return 丢弃; accepted 不等于物品最终可见, 实例化异步消费)");
            }
            catch (System.Exception ex)
            {
                try { RoleLogger.Error("[Shared]", $"{PointPrefix} ReceiveItem Postfix 异常: {ex.Message}"); } catch { }
            }
        }

        // ============= 5. 客机 Receive 入口：ReceiveItems（初始区域物品包） =============
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ItemManager), "ReceiveItems")]
        public static void ReceiveItems_Prefix()
        {
            try
            {
                if (!WorldSyncDiagnosticCore.TryAcquireQuota("Item.ReceiveItems", out int count))
                {
                    return;
                }

                RoleLogger.Info("[Client]",
                    $"{PointPrefix} ReceiveItems #{count}/{WorldSyncDiagnosticCore.PerPointLimit} " +
                    $"(初始区域物品包 - 客机收到区域初始包)");
            }
            catch (System.Exception ex)
            {
                try { RoleLogger.Error("[Shared]", $"{PointPrefix} ReceiveItems Prefix 异常: {ex.Message}"); } catch { }
            }
        }

        // ============= 安全读取辅助（不修改任何状态） =============

        /// <summary>
        /// 读取 player.movement.loadedRegions[x,y].isItemsLoaded。
        /// 失败时返回 "unknown/..."，不得用 false 代替（Codex P0-4）。
        /// </summary>
        private static string ReadRegionItemsLoaded(Player player, byte x, byte y)
        {
            try
            {
                if (player == null) return "unknown(player=null)";
                var movement = player.movement;
                if (movement == null) return "unknown(movement=null)";

                var loadedRegionsField = typeof(PlayerMovement).GetField("_loadedRegions",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (loadedRegionsField == null) return "unknown(_loadedRegions field not found)";

                var loadedRegions = loadedRegionsField.GetValue(movement) as LoadedRegion[,];
                if (loadedRegions == null) return "unknown(loadedRegions=null)";

                if (x < 0 || x >= loadedRegions.GetLength(0) || y < 0 || y >= loadedRegions.GetLength(1))
                    return $"unknown(out_of_range x={x} y={y})";

                var region = loadedRegions[x, y];
                if (region == null) return "unknown(region=null)";

                return region.isItemsLoaded.ToString().ToLowerInvariant();
            }
            catch (System.Exception ex)
            {
                return $"unknown(read-failed: {ex.GetType().Name})";
            }
        }

        private static bool ReadRegionsCheckSafe(byte x, byte y)
        {
            try
            {
                return Regions.checkSafe((int)x, (int)y);
            }
            catch
            {
                return false;
            }
        }

        private static bool ReadItemRegionIsNetworked(byte x, byte y)
        {
            try
            {
                if (ItemManager.regions == null) return false;
                if (x < 0 || x >= ItemManager.regions.GetLength(0) || y < 0 || y >= ItemManager.regions.GetLength(1))
                    return false;

                var region = ItemManager.regions[x, y];
                if (region == null) return false;
                return region.isNetworked;
            }
            catch
            {
                return false;
            }
        }
    }
}
