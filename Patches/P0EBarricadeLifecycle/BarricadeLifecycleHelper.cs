using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using System.Threading;

namespace SteamP2PFriends.Patches.P0EBarricadeLifecycle
{
    /// <summary>
    /// Barricade 客机放置修复的两个薄 Helper + 共用核心判定 + 有界命中日志。
    ///
    ///
    /// 职责边界（严格）：
    ///   - 仅暴露两个 public static bool Helper：Equip / CheckClaims
    ///   - 共用 private IsListenHostRemoteInstanceCore 判定
    ///   - 不修改 vanilla 字段、不调用 vanilla 副作用方法
    ///   - 不全局伪造 Dedicator.IsDedicatedServer
    ///   - 命中日志每会话前 3 次，由 ResetHitLogs 通过 RegisterSessionResetCallback 重置
    ///
    ///   1. instance 非空
    ///   2. HostManager.ShouldProcessClientHostListen()（已含 !Dedicator + Provider.isServer + Level.isLoaded）
    ///   3. Provider.isServer（冗余安全检查）
    ///   4. player.channel 非空
    ///   5. !channel.IsLocalPlayer（排除房主本地实例）
    ///   删除：owner、transportConnection、TransportConnection_Loopback 读取（避免远端实例生命周期 NRE）
    ///
    ///   - result=false 时直接 return false，不调用 LogHitBounded
    ///   - LogHitBounded 不再需要 helperResult 字段
    ///
    ///   - 仅记录 category + branchSelected + instanceId + hitCount
    ///   - 移除 owner/playerID/SteamID 读取
    ///
    ///   - Helper 真实签名包含一个 UseableBarricade 参数
    /// </summary>
    public static class BarricadeLifecycleHelper
    {
        /// <summary>
        /// 每会话每分类命中日志上限（前 3 次输出 branchSelected）。
        /// 由 ResetHitLogs 通过 Volatile.Write 重置。
        /// </summary>
        private const int MaxHitLogsPerSession = 3;

        private static int _equipHitLogCount;
        private static int _checkClaimsHitLogCount;

        /// <summary>
        /// equip() Transpiler 注入的薄 Helper。
        /// </summary>
        public static bool IsListenHostRemoteEquipInstance(UseableBarricade instance)
        {
            bool result = IsListenHostRemoteInstanceCore(instance);
            if (!result)
            {
                return false;
            }

            LogHitBounded("equip", instance, ref _equipHitLogCount);
            return true;
        }

        /// <summary>
        /// checkClaims() Transpiler 注入的薄 Helper。
        /// </summary>
        public static bool IsListenHostRemoteCheckClaimsInstance(UseableBarricade instance)
        {
            bool result = IsListenHostRemoteInstanceCore(instance);
            if (!result)
            {
                return false;
            }

            LogHitBounded("checkClaims", instance, ref _checkClaimsHitLogCount);
            return true;
        }

        /// <summary>
        /// 仅在 listen host 模式 + 远端非 loopback 客机实例时返回 true。
        /// 不读取 owner/transportConnection/TransportConnection_Loopback（避免生命周期 NRE）。
        /// </summary>
        private static bool IsListenHostRemoteInstanceCore(UseableBarricade instance)
        {
            // 守门 1：instance 非空
            if (instance == null)
            {
                return false;
            }

            // 守门 2：listen host 模式（已包含 !Dedicator.IsDedicatedServer + Provider.isConnected + Provider.isServer + Level.isLoaded）
            if (!HostManager.ShouldProcessClientHostListen())
            {
                return false;
            }

            // 守门 3：Provider.isServer（冗余安全检查，与守门 2 共同保证 listen host 状态）
            if (!Provider.isServer)
            {
                return false;
            }

            // player 可能在销毁中
            Player player = instance.player;
            if (player == null)
            {
                return false;
            }

            // 守门 4：channel 非空（使用 var 避免类型名解析问题）
            var channel = player.channel;
            if (channel == null)
            {
                return false;
            }

            // 守门 5：非房主本地实例（channel.IsLocalPlayer 已足以排除房主自连 loopback）
            if (channel.IsLocalPlayer)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// 有界命中日志（每会话每分类前 3 次输出 branchSelected）。
        /// 使用 Interlocked.Increment 保证并发安全，与 Volatile.Write Reset 语义一致。
        /// </summary>
        private static void LogHitBounded(string category, UseableBarricade instance, ref int counter)
        {
            try
            {
                int count = Interlocked.Increment(ref counter);
                if (count > MaxHitLogsPerSession)
                {
                    return;
                }

                int instanceId = -1;
                try { instanceId = instance != null ? instance.GetInstanceID() : -1; } catch { }

                // 不再读取 owner/playerID/SteamID（避免生命周期对象访问）
                RoleLogger.Info("[Shared]",
                    $"[5B-1B/Hit/{category}] branchSelected=true " +
                    $"instance={instanceId} " +
                    $"hitCount={count}/{MaxHitLogsPerSession}");
            }
            catch
            {
                // 日志失败不影响 vanilla 行为
            }
        }

        /// <summary>
        /// 会话重置回调（由 WorldSyncDiagnosticCore.RegisterSessionResetCallback 登记一次）。
        /// 使用 Volatile.Write，与 Interlocked.Increment 并发语义一致。
        /// </summary>
        public static void ResetHitLogs()
        {
            Volatile.Write(ref _equipHitLogCount, 0);
            Volatile.Write(ref _checkClaimsHitLogCount, 0);
            RoleLogger.Info("[Shared]",
                "[5B-1B/ResetHitLogs] equipHitLogCount=0 checkClaimsHitLogCount=0 reason=sessionReset");
        }
    }
}
