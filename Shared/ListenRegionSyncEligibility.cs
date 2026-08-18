using SDG.NetTransport;
using SDG.NetTransport.Loopback;
using SDG.Unturned;
using SteamP2PFriends.Host;

namespace SteamP2PFriends.Shared
{
    /// <summary>
    /// listen-host 远程区域同步资格 helper。
    ///
    /// 仅对以下情况返回 true：
    ///   1. Dedicator.IsDedicatedServer=true（vanilla dedicated server 路径保持原版行为）
    ///   2. listen host 模式（HostManager.ShouldProcessClientHostListen=true）
    ///      + 玩家非 null
    ///      + 玩家 channel/owner/transportConnection 非空
    ///      + 玩家不是本地玩家（!player.channel.IsLocalPlayer）
    ///      + 传输连接不是 TransportConnection_Loopback（房主自连不重发，避免重复实例化）
    ///
    /// 其他情况返回 false：
    ///   - 普通单机
    ///   - 普通客机
    ///   - 菜单阶段
    ///   - listen 房主的本地 loopback 玩家
    ///
    /// 使用场景：
    ///   - BarricadeManager.onRegionUpdated step 2 的 SendRegion 资格门控
    ///   - StructureManager.onRegionUpdated step 1 的 askStructures 资格门控
    ///
    ///   - 不全局伪造 Dedicator.IsDedicatedServer
    ///   - 不让客机调用 BarricadeManager.load() / StructureManager.load()
    ///   - 不直接设置 loading flag
    ///   - 不强制关闭 LoadingUI
    /// </summary>
    public static class ListenRegionSyncEligibility
    {
        /// <summary>
        /// 判断是否应向指定玩家发送 listen-host 远程区域同步 RPC。
        ///
        /// 此方法被两个 Transpiler 注入到 vanilla onRegionUpdated 中，
        /// 替换 Dedicator.IsDedicatedServer() 调用点。
        /// 返回 true 时 vanilla 进入 SendRegion/askStructures 分支，
        /// 返回 false 时 vanilla 跳过该分支（等同 listen server 普通玩家）。
        /// </summary>
        public static bool IsDedicatedOrP2PRemoteRecipient(Player player)
        {
            // vanilla dedicated server：保持原版 true 行为
            if (Dedicator.IsDedicatedServer)
            {
                return true;
            }

            // 非 listen host 场景（普通单机/客机/菜单）：返回 false
            if (!HostManager.ShouldProcessClientHostListen())
            {
                return false;
            }

            // listen host 模式下必须严格检查目标玩家
            if (player == null)
            {
                return false;
            }

            // player.channel 可能为 null（玩家正在销毁）
            if (player.channel == null)
            {
                return false;
            }

            // 本地玩家（房主自连的 loopback 玩家）不重发，避免重复实例化
            // vanilla onRegionUpdated 在 dedicated 路径下不会触发 loopback 玩家，
            // 但 listen host 模式下房主自连会触发，必须显式排除。
            if (player.channel.IsLocalPlayer)
            {
                return false;
            }

            // owner 可能为 null（SteamPlayer 未完全初始化）
            SteamPlayer owner = player.channel.owner;
            if (owner == null)
            {
                return false;
            }

            // transportConnection 可能为 null
            ITransportConnection connection = owner.transportConnection;
            if (connection == null)
            {
                return false;
            }

            // loopback 连接（房主自连的 transport）：不重发
            // listen host 模式下房主自己的 transport 是 TransportConnection_Loopback，
            // 不应进入 SendRegion 分支（会重复实例化已加载的静态 region）。
            if (connection is TransportConnection_Loopback)
            {
                return false;
            }

            // 远程非 loopback 玩家：可以发送 RPC
            return true;
        }

        /// <summary>
        /// 判断当前是否为"dedicated server 或 listen host"。
        ///
        /// 此方法被 Transpiler 注入到 vanilla 的周期性状态广播门控中，
        /// 替换 Dedicator.IsDedicatedServer() 调用点（无 Player 参数场景）。
        ///
        /// 使用场景：
        ///   - ZombieManager.updateRegionsAndSendZombieStates L1662（SendZombieStates 门控）
        ///   - VehicleManager.Update L2918（sendVehicleStates 门控）
        ///   - AnimalManager.Update L1057（sendAnimalStates 门控，L1019 保留）
        ///
        /// 返回 true 时 vanilla 进入 dedicated 分支（发送状态广播），
        /// 返回 false 时 vanilla 走 else 分支或跳过（等同 listen server 普通路径）。
        ///
        /// 与 IsDedicatedOrP2PRemoteRecipient(Player) 的区别：
        ///   - IsDedicatedOrP2PRemoteRecipient 适用于"向特定玩家发送 RPC"的门控（带 Player 参数）
        ///   - IsDedicatedOrP2PHost 适用于"周期性广播状态"的门控（无 Player 参数，广播目标由 GatherRemoteClientConnections 决定）
        ///
        /// 安全性：
        ///   - 不全局伪造 Dedicator.IsDedicatedServer
        ///   - GatherRemoteClientConnections 内部已排除 loopback 玩家（vanilla L1936-1952）
        ///   - listen host 下 SendZombieStates/SendAnimalStates 只会发往远端客机，不会发回主机本地玩家
        /// </summary>
        public static bool IsDedicatedOrP2PHost()
        {
            return Dedicator.IsDedicatedServer || HostManager.ShouldProcessClientHostListen();
        }

        /// <summary>
        /// 仅在 IsDedicatedOrP2PRemoteRecipient 返回 true 时由 patch 调用，
        /// 输出一次有限日志记录决定性证据。
        /// </summary>
        public static string DescribeEligibility(Player player)
        {
            if (Dedicator.IsDedicatedServer)
            {
                return "dedicated=true";
            }

            if (player == null)
            {
                return "player=null";
            }

            if (player.channel == null)
            {
                return "channel=null";
            }

            if (player.channel.IsLocalPlayer)
            {
                return "localPlayer";
            }

            SteamPlayer owner = player.channel.owner;
            if (owner == null)
            {
                return "owner=null";
            }

            ITransportConnection connection = owner.transportConnection;
            if (connection == null)
            {
                return "transport=null";
            }

            string transportType = connection.GetType().Name;
            if (connection is TransportConnection_Loopback)
            {
                return $"loopback({transportType})";
            }

            ulong steamId = owner.playerID?.steamID.m_SteamID ?? 0UL;
            return $"remote(steamId={steamId},transport={transportType})";
        }
    }
}
