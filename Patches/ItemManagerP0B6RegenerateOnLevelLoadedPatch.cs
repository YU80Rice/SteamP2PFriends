using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    ///
    /// 背景：
    ///   25th 双机测试失败：OnServerHosted 时 LevelItems.spawns=null，防御检查失败跳过预生成。
    ///
    ///   25th 主机日志证据：
    ///     L530-L535 onLevelLoaded level=1/8/6 LevelItems.spawns=-1x-1 IsDedicatedOrP2PHost=False
    ///     L579      Provider.onServerHosted 回调触发
    ///     L695-L696 onLevelLoaded level=2 LevelItems.spawns=64x64 eligible=False
    ///
    ///   关键洞察：LevelItems.spawns 在 onLevelLoaded level=2（OnServerHosted 之后）时才就绪。
    ///
    ///   入口检查：
    ///     1. level > Level.BUILD_INDEX_SETUP（与 vanilla onLevelLoaded 门控一致）
    ///     2. _p0B6RegenerationDone == false（确保只执行一次）
    ///     3. HostManager.IsP2PServerActive == true
    ///     4. LevelItems.spawns != null + 维度 == Regions.WORLD_SIZE × Regions.WORLD_SIZE
    ///     5. ItemManager.regions != null + 维度 == Regions.WORLD_SIZE × Regions.WORLD_SIZE
    ///     6. ItemManager.manager 实例（反射 private static 字段）不为 null
    ///     7. generateItems 方法（反射 private instance）找到
    ///   单区域 try-catch（单区域失败不影响其他区域）。
    ///
    ///   _p0B6RegenerationDone 需在以下场景重置为 false：
    ///     - HostManager.ResetHostSession()（断线重连 / 返回主菜单）
    ///     - HostManager.AbortHostStart()（启动失败回滚）
    ///
    /// U3-SDK 溯源：
    ///   - D:/Agent-工作目录/U3-SDK/Assets/Runtime/Assembly-CSharp/Unturned/Level/Level.cs:31
    ///     public static readonly int BUILD_INDEX_SETUP = 0
    ///   - D:/Agent-工作目录/U3-SDK/Assets/Runtime/Assembly-CSharp/Unturned/Level/Level.cs:33
    ///     public static readonly int BUILD_INDEX_GAME = 2
    ///   - D:/Agent-工作目录/U3-SDK/Assets/Runtime/Assembly-CSharp/Unturned/Managers/ItemManager.cs:847-908
    ///     generateItems（private instance void(byte x, byte y)）
    ///   - D:/Agent-工作目录/U3-SDK/Assets/Runtime/Assembly-CSharp/Unturned/Managers/ItemManager.cs:52
    ///     private static ItemManager manager
    ///   - D:/Agent-工作目录/U3-SDK/Assets/Runtime/Assembly-CSharp/Unturned/Managers/ItemManager.cs:59
    ///     public static ItemRegion[,] regions
    ///   - D:/Agent-工作目录/U3-SDK/Assets/Runtime/Assembly-CSharp/Unturned/Level/LevelItems.cs:38-39
    ///     public static List<ItemSpawnpoint>[,] spawns
    ///   - D:/Agent-工作目录/U3-SDK/Assets/Runtime/Assembly-CSharp/Unturned/Regions/Regions.cs:34
    ///     public static readonly byte WORLD_SIZE = 64
    ///   - D:/Agent-工作目录/U3-SDK/Assets/Runtime/Assembly-CSharp/Unturned/Managers/ItemManager.cs:922-924
    ///     onLevelLoaded 仅在 level > BUILD_INDEX_SETUP 时创建 regions
    ///
    /// 安全性：
    ///   - 不全局伪造 Dedicator.IsDedicatedServer
    ///   - 不修改 vanilla IL
    ///   - 仅在 onLevelLoaded Postfix 中显式调用 vanilla private 方法
    ///   - 不引入自定义 RPC
    ///   - 反射调用包裹 try-catch，单区域失败不影响其他区域
    ///
    /// FACT.md 合规：
    ///   ✅ 未触碰 Dedicator.IsDedicatedServer
    ///   ✅ 未修改 vanilla IL
    ///   ✅ 仅在 listen host 已就绪 + spawns 已就绪时执行
    /// </summary>
    public static class ItemManagerP0B6RegenerateOnLevelLoadedPatch
    {
        /// <summary>
        /// 在 ResetHostSession/AbortHostStart 中通过 ResetRegenerationFlag() 重置。
        /// </summary>
        private static bool _p0B6RegenerationDone = false;

        /// <summary>
        /// </summary>
        public static bool RegenerationDone => _p0B6RegenerationDone;

        /// <summary>
        /// </summary>
        public static string RegenerationSummary { get; private set; } = "未执行";

        /// <summary>
        /// </summary>
        public static void ResetRegenerationFlag()
        {
            if (_p0B6RegenerationDone)
            {
                RoleLogger.Info("[Host]",
                    "[P0-B-6] 重置 _p0B6RegenerationDone=false（ResetHostSession/AbortHostStart 触发）");
            }
            _p0B6RegenerationDone = false;
            RegenerationSummary = "未执行（已重置）";
        }

        /// <summary>
        /// 检查 spawns 就绪 + IsP2PServerActive + 未执行过时，调用 generateItems 全地图循环。
        ///
        /// 与 vanilla onLevelLoaded 门控一致：level > Level.BUILD_INDEX_SETUP 才执行。
        ///   U3-SDK: Level.cs:31 BUILD_INDEX_SETUP=0
        ///   U3-SDK: ItemManager.cs:922-924 onLevelLoaded 仅在 level > BUILD_INDEX_SETUP 时创建 regions
        /// </summary>
        public static void TryRegenerateOnLevelLoaded(int level)
        {
            // 门控 1：level > BUILD_INDEX_SETUP（与 vanilla 一致）
            //   level=0 是菜单/设置，level=2 是游戏关卡
            //   level=1/8/6 是加载中间状态（spawns 未就绪，防御检查会兜底）
            if (level <= Level.BUILD_INDEX_SETUP)
            {
                return;
            }

            // 门控 2：已执行过则跳过（确保只执行一次）
            if (_p0B6RegenerationDone)
            {
                return;
            }

            // 门控 3：必须是 P2P listen host
            if (!Host.HostManager.IsP2PServerActive)
            {
                return;
            }

            RoleLogger.Info("[Host]",
                $"[P0-B-6] === onLevelLoaded Postfix 检测到 level={level}，尝试触发全地图 generateItems（v0.2.3.37 P0-B-6）===");

            RegenerateAllItems();
        }

        /// <summary>
        /// 显式调用 ItemManager.generateItems 全地图循环（64x64=4096 区域）。
        /// 绕过 IsDedicatedOrP2PHost() 检查，仅检查 LevelItems.spawns / ItemManager.regions / ItemManager.manager 实例。
        /// </summary>
        private static void RegenerateAllItems()
        {
            try
            {
                // 防御检查 1：LevelItems.spawns 必须就绪
                //   U3-SDK: LevelItems.cs:38-39 public static List<ItemSpawnpoint>[,] spawns
                if (LevelItems.spawns == null)
                {
                    RegenerationSummary = "LevelItems.spawns=null，跳过预生成";
                    RoleLogger.Warn("[Host]", $"[P0-B-6] !!! {RegenerationSummary}");
                    return;
                }

                int spawnsDim0 = LevelItems.spawns.GetLength(0);
                int spawnsDim1 = LevelItems.spawns.GetLength(1);

                if (spawnsDim0 != Regions.WORLD_SIZE || spawnsDim1 != Regions.WORLD_SIZE)
                {
                    RegenerationSummary = $"LevelItems.spawns 维度异常={spawnsDim0}x{spawnsDim1}，期望={Regions.WORLD_SIZE}x{Regions.WORLD_SIZE}";
                    RoleLogger.Warn("[Host]", $"[P0-B-6] !!! {RegenerationSummary}");
                    return;
                }

                // 防御检查 2：ItemManager.regions 必须就绪
                //   U3-SDK: ItemManager.cs:59 public static ItemRegion[,] regions
                if (ItemManager.regions == null)
                {
                    RegenerationSummary = "ItemManager.regions=null，跳过预生成";
                    RoleLogger.Warn("[Host]", $"[P0-B-6] !!! {RegenerationSummary}");
                    return;
                }

                int regionsDim0 = ItemManager.regions.GetLength(0);
                int regionsDim1 = ItemManager.regions.GetLength(1);

                if (regionsDim0 != Regions.WORLD_SIZE || regionsDim1 != Regions.WORLD_SIZE)
                {
                    RegenerationSummary = $"ItemManager.regions 维度异常={regionsDim0}x{regionsDim1}，期望={Regions.WORLD_SIZE}x{Regions.WORLD_SIZE}";
                    RoleLogger.Warn("[Host]", $"[P0-B-6] !!! {RegenerationSummary}");
                    return;
                }

                // 防御检查 3：ItemManager.manager 实例（private static 字段）
                //   U3-SDK: ItemManager.cs:52 private static ItemManager manager
                FieldInfo managerField = typeof(ItemManager).GetField(
                    "manager", BindingFlags.NonPublic | BindingFlags.Static);
                if (managerField == null)
                {
                    RegenerationSummary = "无法找到 ItemManager.manager 字段";
                    RoleLogger.Error("[Host]", $"[P0-B-6] !!! {RegenerationSummary}");
                    return;
                }

                ItemManager managerInstance = managerField.GetValue(null) as ItemManager;
                if (managerInstance == null)
                {
                    RegenerationSummary = "ItemManager.manager 实例为 null";
                    RoleLogger.Error("[Host]", $"[P0-B-6] !!! {RegenerationSummary}");
                    return;
                }

                // 防御检查 4：generateItems 方法（private instance void(byte x, byte y)）
                //   U3-SDK: ItemManager.cs:847-908
                MethodInfo generateItemsMethod = typeof(ItemManager).GetMethod(
                    "generateItems", BindingFlags.NonPublic | BindingFlags.Instance);
                if (generateItemsMethod == null)
                {
                    RegenerationSummary = "无法找到 ItemManager.generateItems 方法";
                    RoleLogger.Error("[Host]", $"[P0-B-6] !!! {RegenerationSummary}");
                    return;
                }

                // 全地图 generateItems 循环（64x64=4096 区域）
                RoleLogger.Info("[Host]",
                    $"[P0-B-6] 开始全地图 generateItems regions={regionsDim0}x{regionsDim1} spawns={spawnsDim0}x{spawnsDim1}");

                int successCount = 0;
                int failCount = 0;
                System.DateTime startTime = System.DateTime.UtcNow;

                for (byte x = 0; x < Regions.WORLD_SIZE; x++)
                {
                    for (byte y = 0; y < Regions.WORLD_SIZE; y++)
                    {
                        try
                        {
                            generateItemsMethod.Invoke(managerInstance, new object[] { x, y });
                            successCount++;
                        }
                        catch (System.Exception ex)
                        {
                            failCount++;
                            if (failCount <= 3)
                            {
                                RoleLogger.Warn("[Host]",
                                    $"[P0-B-6] generateItems({x},{y}) 异常: {ex.InnerException?.Message ?? ex.Message}");
                            }
                        }
                    }
                }

                System.TimeSpan elapsed = System.DateTime.UtcNow - startTime;
                _p0B6RegenerationDone = true;
                RegenerationSummary = $"success={successCount} fail={failCount} elapsed={elapsed.TotalSeconds:F2}s";

                RoleLogger.Info("[Host]",
                    $"[P0-B-6] OK 全地图 generateItems 完成 summary={RegenerationSummary}");

                // 输出预生成后的 items 总数采样（取对角线 5 个区域）
                try
                {
                    int totalSampleItems = 0;
                    for (int i = 0; i < 5; i++)
                    {
                        byte sx = (byte)(i * 12);
                        byte sy = (byte)(i * 12);
                        if (sx < Regions.WORLD_SIZE && sy < Regions.WORLD_SIZE)
                        {
                            ItemRegion region = ItemManager.regions[sx, sy];
                            if (region != null && region.items != null)
                            {
                                totalSampleItems += region.items.Count;
                            }
                        }
                    }
                    RoleLogger.Info("[Host]",
                        $"[P0-B-6] 预生成后采样 5 个对角线区域 items 总数={totalSampleItems}（验证 generateItems 已填充）");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Host]", $"[P0-B-6] 采样验证异常（不阻断）: {ex.Message}");
                }
            }
            catch (System.Exception ex)
            {
                RegenerationSummary = $"异常: {ex.Message}";
                RoleLogger.Error("[Host]", $"[P0-B-6] !!! RegenerateAllItems 异常: {ex}");
            }
        }
    }
}
