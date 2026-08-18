using SteamP2PFriends.Shared;
using System.Threading;

namespace SteamP2PFriends.Patches.P0EBarricadeLifecycle
{
    /// <summary>
    /// Barricade 生命周期诊断日志的受限配额工具。
    ///
    ///
    /// 职责（严格）：
    ///   - 提供 Interlocked 计数 + 上限判定
    ///   - 不依赖 WorldSyncDiagnosticCore 的锁（避免与主诊断链路竞争）
    ///   - 仅用于 BarricadeLifecycle 自身的低频事件（启动登记、回滚等）
    ///   - 命中日志的每会话配额由 BarricadeLifecycleHelper 内部维护（不在此处）
    ///
    /// 注意：
    ///   - 不在 ResetHitLogs 中重置（启动事件只发生一次，无需重置）
    ///   - 仅在 BarricadeLifecycle 命名空间内使用
    /// </summary>
    internal static class BarricadeLifecycleDiagQuota
    {
        /// <summary>
        /// 尝试获取一次配额。返回 true 时表示未超上限，可以输出日志。
        /// 线程安全：使用 Interlocked.Increment。
        /// </summary>
        internal static bool TryAcquire(ref int counter, int limit)
        {
            int current = Interlocked.Increment(ref counter);
            return current <= limit;
        }
    }
}
