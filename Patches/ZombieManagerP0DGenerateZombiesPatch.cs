using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using System;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.32 P0-D 修复（Codex 第二十次双机测试外部审计 §5.2 方案 A 授权实施）：
    ///
    /// 根因（U3-SDK ZombieManager.cs:1448-1494 onBoundUpdated）：
    ///   - L1460 外层门控 if (Provider.isServer)
    ///   - L1473 if (LevelNavigation.checkSafe(newBound))
    ///   - L1475 if (!player.movement.loadedBounds[newBound].isZombiesLoaded)
    ///   - L1477 if (player.channel.IsLocalPlayer)
    ///       L1479 generateZombies(newBound);
    ///       L1481 regions[newBound].isNetworked = true;
    ///     else
    ///       L1485 SendZombiesToPlayer(player.channel.owner.transportConnection, newBound);
    ///     L1488 player.movement.loadedBounds[newBound].isZombiesLoaded = true;
    ///   - L1491 regions[newBound].PlayerCountInRegion += 1;
    ///
    /// 问题：listen host 模式下，房主（IsLocalPlayer=true）进入 bound 时 generateZombies 正常调用；
    /// 但远端客机（IsLocalPlayer=false）进入 bound 时仅调用 SendZombiesToPlayer，不调用 generateZombies。
    /// 若该 bound 房主从未进入（regions[newBound].isNetworked=false，zombies 列表为空），
    /// SendZombiesToPlayer 发送 zombieCount=0 -> 客机看到空城镇
    /// （U3-SDK ZombieManager.cs:679 SendZombies_Write: count = region.zombies.Count = 0）。
    ///
    /// 第二十次双机测试决定性证据：
    ///   - LogOutput-host.log:1138 SendZombiesToPlayer bound=10 zombieCount_in_bound=0
    ///   - LogOutput-host.log:1139 SendZombies_Write #1/20 bound=10 regionZombies=0
    ///   - LogOutput-client.log:1151-1152 ReceiveZombies totalZombies_before=0 delta=0
    ///
    /// 修复方案（Codex §5.2 方案 A，Prefix supplement 模式）：
    ///   在 vanilla onBoundUpdated Prefix 阶段，若满足以下全部条件：
    ///     1. Provider.isServer（listen host 侧）
    ///     2. !player.channel.IsLocalPlayer（远端客机进入）
    ///     3. HostManager.ShouldProcessClientHostListen()（P2P listen host 模式激活）
    ///     4. LevelNavigation.checkSafe(newBound)（bound 索引安全）
    ///     5. !player.movement.loadedBounds[newBound].isZombiesLoaded（客机首次进入该 bound）
    ///     6. !ZombieManager.regions[newBound].isNetworked（房主从未进入或已离开）
    ///   则：
    ///     - 调用 ZombieManager.instance.generateZombies(newBound) 补充生成僵尸
    ///     - 设置 ZombieManager.regions[newBound].isNetworked = true 防止重复生成
    ///     - 日志记录补充生成前后的 region.zombies.Count 与 PlayerCountInRegion
    ///   不返回 false，让 vanilla 继续 SendZombiesToPlayer（此时 region.zombies 已填充，客机收到真实僵尸列表）。
    ///
    /// 安全性：
    ///   - 条件 6 确保：房主当前在该 bound 时（isNetworked=true）不重复生成；
    ///   - 条件 5 确保：客机重入同一 bound 时不重复生成（loadedBounds 已由 vanilla L1488 标记）；
    ///   - 不修改 vanilla IL，不替换 vanilla 方法，仅在 Prefix 补充调用；
    ///   - 不干预 PlayerCountInRegion 计数（vanilla L1491 自行 +=1）；
    ///   - 不干预 loadedBounds[newBound].isZombiesLoaded 标记（vanilla L1488 自行设置）。
    ///
    /// Harmony 优先级：
    ///   - 本 Prefix 标记 [HarmonyPriority(Priority.Low)]，确保在 ZombieManagerWorldSyncDiagnosticPatch
    ///     .OnBoundUpdated_Prefix（默认 Priority.Normal）之后执行。
    ///   - 诊断 patch 先记录 pre-supplement 状态（zombieCount_before=0），
    ///     本 patch 后记录 supplement 动作（zombies_before=0 zombies_after=N），
    ///     日志语义清晰。
    /// </summary>
    public static class ZombieManagerP0DGenerateZombiesPatch
    {
        private const string PointPrefix = "[P0-D/Zombie]";

        // vanilla ZombieManager.onBoundUpdated private method 完整参数类型表
        // U3-SDK ZombieManager.cs:1448 private void onBoundUpdated(Player player, byte oldBound, byte newBound)
        private static readonly System.Type[] VanillaOnBoundUpdatedParamTypes =
        {
            typeof(Player),
            typeof(byte), typeof(byte)
        };

        public static bool OnBoundUpdatedPrefixRegistered { get; private set; }
        public static bool AllRegistrationsSucceeded => OnBoundUpdatedPrefixRegistered;

        /// <summary>
        /// v0.2.3.32 P0-D 手动登记：identity-based 防重复登记。
        /// </summary>
        public static bool RegisterManual(Harmony harmony)
        {
            RoleLogger.Info("[Shared]", $"{PointPrefix} === 手动登记 1 个 hook（v0.2.3.32 P0-D onBoundUpdated Prefix supplement）===");

            var patchType = typeof(ZombieManagerP0DGenerateZombiesPatch);

            bool r1;
            try
            {
                r1 = WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, typeof(ZombieManager), "onBoundUpdated", VanillaOnBoundUpdatedParamTypes,
                    AccessTools.Method(patchType, "OnBoundUpdated_Prefix"),
                    HarmonyPatchType.Prefix, "P0-D.Zombie.onBoundUpdated.Pre");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"{PointPrefix} onBoundUpdated.Pre 登记异常: {ex}");
                r1 = false;
            }

            bool all = r1;
            RoleLogger.Info("[Shared]",
                $"{PointPrefix} RegisterManual 结果: onBoundUpdated.Pre={r1} all={all}");
            return all;
        }

        public static bool VerifyRegistration()
        {
            try
            {
                var patchType = typeof(ZombieManagerP0DGenerateZombiesPatch);
                MethodInfo onBoundUpdatedPre = AccessTools.Method(patchType, "OnBoundUpdated_Prefix");

                OnBoundUpdatedPrefixRegistered = WorldSyncDiagnosticCore.IsPatchRegistered(
                    typeof(ZombieManager), "onBoundUpdated", onBoundUpdatedPre, HarmonyPatchType.Prefix, VanillaOnBoundUpdatedParamTypes);

                if (!AllRegistrationsSucceeded)
                {
                    RoleLogger.Error("[Shared]",
                        $"{PointPrefix} !!! 注册验证失败: onBoundUpdated.Pre={OnBoundUpdatedPrefixRegistered} " +
                        $"(owner={SteamP2PFriendsPlugin.HARMONY_ID}, identity-based)");
                    return false;
                }

                RoleLogger.Info("[Shared]",
                    $"{PointPrefix} OK 1 个 hook 已注册 (owner={SteamP2PFriendsPlugin.HARMONY_ID}, identity-based)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"{PointPrefix} VerifyRegistration 异常: {ex.Message}");
                OnBoundUpdatedPrefixRegistered = false;
                return false;
            }
        }

        // ============= onBoundUpdated Prefix =============
        // U3-SDK ZombieManager.cs:1448 private void onBoundUpdated(Player player, byte oldBound, byte newBound)
        // [HarmonyPriority(Priority.Low)] 确保在 ZombieManagerWorldSyncDiagnosticPatch.OnBoundUpdated_Prefix 之后执行
        [HarmonyPriority(Priority.Low)]
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ZombieManager), "onBoundUpdated")]
        public static void OnBoundUpdated_Prefix(Player player, byte oldBound, byte newBound)
        {
            try
            {
                // v0.2.3.32-P0-D返修（Codex Medium）：前置 null 断言，避免依赖 catch-all 兜底隐藏诊断信息。
                //   - player 为 null：Unity 生命周期异常场景（如 teardown 期间 onBoundUpdated 仍触发），
                //     若无前置断言，player?.channel?.IsLocalPlayer 返回 false 会误判为远端客机进入，
                //     继续走六条件守门并可能调用 generateZombies，语义错误。
                //   - ZombieManager.instance 为 null：服务器关闭过程中 manager 已销毁但 onBoundUpdated 仍触发，
                //     调用 instance.generateZombies 会抛 NRE，catch-all 兜底仅记录异常不阻止流程，但隐藏根因。
                // 前置断言将这些异常场景提前返回，保持诊断日志的语义纯净。
                if (player == null) return;
                if (ZombieManager.instance == null) return;

                // 条件 1: Provider.isServer（listen host 侧）
                if (!Provider.isServer) return;

                // 条件 2: !player.channel.IsLocalPlayer（远端客机进入）
                bool isLocalPlayer = false;
                try { isLocalPlayer = player?.channel?.IsLocalPlayer ?? false; } catch { }
                if (isLocalPlayer) return;

                // 条件 3: HostManager.ShouldProcessClientHostListen()（P2P listen host 模式激活）
                if (!HostManager.ShouldProcessClientHostListen()) return;

                // 条件 4: LevelNavigation.checkSafe(newBound)（bound 索引安全）
                bool safeBound = ReadLevelNavigationCheckSafe(newBound);
                if (!safeBound) return;

                // 条件 5: !player.movement.loadedBounds[newBound].isZombiesLoaded（客机首次进入该 bound）
                bool isZombiesLoaded = ReadLoadedBoundIsZombiesLoaded(player, newBound);
                if (isZombiesLoaded) return;

                // 条件 6: !ZombieManager.regions[newBound].isNetworked（房主从未进入或已离开）
                bool isNetworked = ReadRegionIsNetworked(newBound);
                if (isNetworked) return;

                // 全部条件满足，补充生成僵尸
                int? zombieCountBeforeOpt = ReadZombieCountInBound(newBound);
                string zombieCountBefore = zombieCountBeforeOpt.HasValue ? zombieCountBeforeOpt.Value.ToString() : "unknown";

                int playerCountBefore = ReadPlayerCountInRegion(newBound);

                // 调用 generateZombies(newBound) 补充生成
                // U3-SDK ZombieManager.cs:1149 public void generateZombies(byte bound)
                ZombieManager.instance.generateZombies(newBound);

                // 设置 isNetworked = true 防止重复生成
                // U3-SDK ZombieRegion.cs:38 public bool isNetworked;
                SetRegionIsNetworked(newBound, true);

                int? zombieCountAfterOpt = ReadZombieCountInBound(newBound);
                string zombieCountAfter = zombieCountAfterOpt.HasValue ? zombieCountAfterOpt.Value.ToString() : "unknown";

                int playerCountAfter = ReadPlayerCountInRegion(newBound);

                ulong steamId = 0UL;
                try { steamId = player?.channel?.owner?.playerID?.steamID.m_SteamID ?? 0UL; } catch { }
                string maskedId = WorldSyncDiagnosticCore.MaskSteamId(steamId);

                RoleLogger.Info("[Host]",
                    $"{PointPrefix} supplement generateZombies player={maskedId} oldBound={oldBound} newBound={newBound} " +
                    $"zombies_before={zombieCountBefore} zombies_after={zombieCountAfter} " +
                    $"playerCount_before={playerCountBefore} playerCount_after={playerCountAfter} " +
                    $"(vanilla IsLocalPlayer=false 分支仅 SendZombiesToPlayer，P0-D 补充 generateZombies 后由 vanilla 继续发送)");
            }
            catch (System.Exception ex)
            {
                try { RoleLogger.Error("[Shared]", $"{PointPrefix} OnBoundUpdated Prefix 异常: {ex.Message}"); } catch { }
            }
        }

        // ============= 安全读取辅助 =============

        private static bool ReadLevelNavigationCheckSafe(byte bound)
        {
            try { return LevelNavigation.checkSafe(bound); }
            catch { return false; }
        }

        private static bool ReadLoadedBoundIsZombiesLoaded(Player player, byte bound)
        {
            try
            {
                if (player == null) return false;
                var movement = player.movement;
                if (movement == null) return false;

                // U3-SDK PlayerMovement.cs:356 public LoadedBound[] loadedBounds => _loadedBounds;
                var loadedBounds = movement.loadedBounds;
                if (loadedBounds == null) return false;
                if (bound < 0 || bound >= loadedBounds.Length) return false;

                var lb = loadedBounds[bound];
                if (lb == null) return false;
                return lb.isZombiesLoaded;
            }
            catch { return false; }
        }

        private static bool ReadRegionIsNetworked(byte bound)
        {
            try
            {
                // U3-SDK ZombieManager.cs:35 public static ZombieRegion[] regions => _regions;
                var regions = ZombieManager.regions;
                if (regions == null) return false;
                if (bound >= regions.Length) return false;
                var region = regions[bound];
                if (region == null) return false;
                // U3-SDK ZombieRegion.cs:38 public bool isNetworked;
                return region.isNetworked;
            }
            catch { return false; }
        }

        private static void SetRegionIsNetworked(byte bound, bool value)
        {
            try
            {
                var regions = ZombieManager.regions;
                if (regions == null) return;
                if (bound >= regions.Length) return;
                var region = regions[bound];
                if (region == null) return;
                region.isNetworked = value;
            }
            catch { }
        }

        private static int? ReadZombieCountInBound(byte bound)
        {
            try
            {
                var regions = ZombieManager.regions;
                if (regions == null) return null;
                if (bound >= regions.Length) return null;
                var region = regions[bound];
                if (region == null) return null;
                // U3-SDK ZombieRegion.cs:19 public List<Zombie> zombies => _zombies;
                var zombies = region.zombies;
                if (zombies == null) return null;
                return zombies.Count;
            }
            catch { return null; }
        }

        private static int ReadPlayerCountInRegion(byte bound)
        {
            try
            {
                var regions = ZombieManager.regions;
                if (regions == null) return -1;
                if (bound >= regions.Length) return -1;
                var region = regions[bound];
                if (region == null) return -1;
                // U3-SDK ZombieRegion.cs:358 public int PlayerCountInRegion { get; internal set; }
                return region.PlayerCountInRegion;
            }
            catch { return -1; }
        }
    }
}
