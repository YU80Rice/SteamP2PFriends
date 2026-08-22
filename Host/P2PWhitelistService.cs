using SDG.Unturned;
using SteamP2PFriends.Shared;
using Steamworks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace SteamP2PFriends.Host
{
    // =====================================================================
    // =====================================================================
    // 三个接口（seam three-piece set）：
    //   IWhitelistStore / IWhitelistRuntimeContext / IWhitelistDisconnectGateway
    // 三个生产实现：
    //   NativeWhitelistStore / NativeWhitelistRuntimeContext / NativeWhitelistDisconnectGateway
    // 一个进程内唯一 static 类：
    //   P2PWhitelistService
    //
    //   - SteamWhitelist.* 原生调用仅在 NativeWhitelistStore
    //   - 零处访问 SteamWhitelist._list
    //   - Provider.disconnect() 仅在 NativeWhitelistDisconnectGateway
    //   - 无 new P2PWhitelistService()
    // =====================================================================

    internal interface IWhitelistStore
    {
        void Load();
        void Save();
        bool Contains(CSteamID steamId);
        void AddOrUpdate(CSteamID steamId, string tag, CSteamID judgeId);
        bool Remove(CSteamID steamId);
        List<SteamWhitelistID> Snapshot();
        void Restore(List<SteamWhitelistID> snapshot);
    }

    internal interface IWhitelistRuntimeContext
    {
        void AssertGameThread();
        bool IsActiveP2PHost { get; }
        CSteamID LocalUser { get; }
        void SetWhitelisted(bool value);
    }

    internal interface IWhitelistDisconnectGateway
    {
        void DisconnectCurrentP2PHost();
    }

    // =====================================================================
    // 生产实现：NativeWhitelistStore
    // =====================================================================

    internal sealed class NativeWhitelistStore : IWhitelistStore
    {
        public void Load() => SteamWhitelist.load();
        public void Save() => SteamWhitelist.save();
        /// <summary>
        /// Returns physical membership from the native list, rather than calling
        /// SteamWhitelist.checkWhitelisted. Route B patches that method only for the
        /// handshake pipeline, so it must never decide persistence or quarantine.
        /// </summary>
        public bool Contains(CSteamID steamId)
        {
            List<SteamWhitelistID> list = SteamWhitelist.list;
            if (list == null) throw new InvalidOperationException("SteamWhitelist.list is null");
            return ContainsRaw(list, steamId);
        }

        internal static bool ContainsRawForTest(List<SteamWhitelistID> list, CSteamID steamId)
        {
            return ContainsRaw(list, steamId);
        }

        private static bool ContainsRaw(List<SteamWhitelistID> list, CSteamID steamId)
        {
            if (list == null) throw new ArgumentNullException(nameof(list));
            for (int index = 0; index < list.Count; index++)
            {
                if (list[index].steamID == steamId) return true;
            }
            return false;
        }

        public void AddOrUpdate(CSteamID steamId, string tag, CSteamID judgeId) =>
            SteamWhitelist.whitelist(steamId, tag, judgeId);

        /// <summary>
        /// Removes only the in-memory whitelist entry. Do not call SteamWhitelist.unwhitelist:
        /// that vanilla helper kicks the guest before this service can prove Save/Load succeeded.
        /// </summary>
        public bool Remove(CSteamID steamId)
        {
            List<SteamWhitelistID> list = SteamWhitelist.list;
            if (list == null) throw new InvalidOperationException("SteamWhitelist.list is null");
            for (int index = 0; index < list.Count; index++)
            {
                if (list[index].steamID != steamId) continue;
                list.RemoveAt(index);
                return true;
            }
            return false;
        }

        // 零处访问 private SteamWhitelist._list
        public List<SteamWhitelistID> Snapshot() => SteamWhitelist.list
            .Select(x => new SteamWhitelistID(x.steamID, x.tag, x.judgeID))
            .ToList();

        public void Restore(List<SteamWhitelistID> snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            SteamWhitelist.list.Clear();
            foreach (SteamWhitelistID entry in snapshot)
            {
                SteamWhitelist.list.Add(
                    new SteamWhitelistID(entry.steamID, entry.tag, entry.judgeID));
            }
        }
    }

    // =====================================================================
    // 生产实现：NativeWhitelistRuntimeContext
    // =====================================================================

    internal sealed class NativeWhitelistRuntimeContext : IWhitelistRuntimeContext
    {
        public void AssertGameThread() => ThreadUtil.assertIsGameThread();

        // 三重 host 守卫：P2P 房主模式 + Provider.isServer + Provider.isWhitelisted
        public bool IsActiveP2PHost =>
            HostManager.IsP2PHostMode && Provider.isServer && Provider.isWhitelisted;

        public CSteamID LocalUser => Provider.user;

        // 经 runtime 统一 Provider.isWhitelisted 写入点（TryBootstrap 唯一调用）
        public void SetWhitelisted(bool value) => Provider.isWhitelisted = value;
    }

    // =====================================================================
    // 生产实现：NativeWhitelistDisconnectGateway
    // =====================================================================

    internal sealed class NativeWhitelistDisconnectGateway : IWhitelistDisconnectGateway
    {
        public void DisconnectCurrentP2PHost()
        {
            // 主线程断言（蓝图 §2.1 强制）
            ThreadUtil.assertIsGameThread();
            // 原版 Provider.disconnect() -> ProviderDisconnectPatch.Postfix -> HostManager.StopP2PServer()
            Provider.disconnect();
        }
    }

    // =====================================================================
    // =====================================================================

    internal static class P2PWhitelistService
    {
        private static readonly object WhitelistSync = new object();
        private static IWhitelistStore _store = new NativeWhitelistStore();
        private static IWhitelistRuntimeContext _runtime = new NativeWhitelistRuntimeContext();
        private static IWhitelistDisconnectGateway _disconnect =
            new NativeWhitelistDisconnectGateway();
        private static bool _persistenceFaulted;
        private const int MaxTagLength = 64;

        // ===== 生命周期 =====

        /// <summary>
        /// P2P 开房前置：清 service 运行时 fault 状态。
        /// 仅在主线程调用；不改/不保存 native list（蓝图 §2.1）。
        /// </summary>
        internal static void ResetForP2PStart()
        {
            _runtime.AssertGameThread();
            lock (WhitelistSync)
            {
                _persistenceFaulted = false;
            }
            RoleLogger.Info("[Host]", "[P2P-WL] ResetForP2PStart: persistenceFaulted=false");
        }

        /// <summary>
        /// P2P 退出后清理：清 service 运行时状态。
        /// 不清空/不保存 native list（蓝图 §2.1 + §4.5）。
        /// </summary>
        internal static void ResetAfterP2PExit()
        {
            _runtime.AssertGameThread();
            lock (WhitelistSync)
            {
                _persistenceFaulted = false;
            }
            RoleLogger.Info("[Host]", "[P2P-WL] ResetAfterP2PExit: persistenceFaulted=false");
        }

        // ===== Bootstrap（蓝图 §2.1 + 设计 §4.1）=====

        /// <summary>
        /// P2P 房主白名单 bootstrap：
        ///   Load -> 验证 hostId/LocalUser 有效且相等 -> AddOrUpdate(host,"P2P_HOST",host) -> Save -> Load -> Contains(host)
        /// 仅全过才 Provider.isWhitelisted=true；失败置 false 并返回 false，不调 disconnect（蓝图 §2.1）。
        /// 失败由既有 StartP2PServer 外层 catch -> AbortHostStart 收敛。
        /// </summary>
        internal static bool TryBootstrap(CSteamID hostId, out string failure)
        {
            _runtime.AssertGameThread();
            failure = "";

            if (hostId == CSteamID.Nil || !hostId.IsValid())
            {
                failure = "hostId is Nil or invalid";
                _runtime.SetWhitelisted(false);
                RoleLogger.Error("[Host]", "[P2P-WL] bootstrap rejected: " + failure);
                return false;
            }

            CSteamID localUser = _runtime.LocalUser;
            if (localUser != hostId)
            {
                failure = $"hostId mismatch: hostId={hostId.m_SteamID} localUser={localUser.m_SteamID}";
                _runtime.SetWhitelisted(false);
                RoleLogger.Error("[Host]", "[P2P-WL] bootstrap rejected: " + failure);
                return false;
            }

            lock (WhitelistSync)
            {
                _runtime.SetWhitelisted(false);
                try
                {
                    _store.Load();
                    _store.AddOrUpdate(hostId, "P2P_HOST", hostId);
                    _store.Save();
                    _store.Load();
                    if (!_store.Contains(hostId))
                    {
                        failure = "postcondition Contains(host)==false after bootstrap";
                        RecordWhitelistFailure("bootstrap", hostId, null);
                        RoleLogger.Error("[Host]", "[P2P-WL] bootstrap failed: " + failure);
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    failure = "bootstrap exception: " + ex.GetType().Name + ": " + ex.Message;
                    RecordWhitelistFailure("bootstrap", hostId, ex);
                    RoleLogger.Error("[Host]", "[P2P-WL] bootstrap failed: " + failure);
                    return false;
                }

                _runtime.SetWhitelisted(true);
                RoleLogger.Info("[Host]",
                    $"[P2P-WL] bootstrap success: host={hostId.m_SteamID} isWhitelisted=true");
                return true;
            }
        }

        // ===== UI 入口 =====

        /// <summary>
        /// UI 操作许可判定：仅当 !_persistenceFaulted && _runtime.IsActiveP2PHost 时返回 true。
        /// </summary>
        internal static bool CanManage()
        {
            _runtime.AssertGameThread();
            if (_persistenceFaulted) return false;
            return _runtime.IsActiveP2PHost;
        }

        /// <summary>
        /// 添加客机到白名单（蓝图 §2.1）：
        ///   仅活动 P2P 房主；拒绝 Nil/无效/self/空或超 64 tag；
        ///   Snapshot -> AddOrUpdate(target,tag,LocalUser) -> Save -> Load -> Contains(target)
        ///   失败走 shouldDisconnect 模板：restore snapshot -> fault latching -> 锁外 disconnect。
        /// </summary>
        internal static bool TryAdd(CSteamID target, string tag, out string feedback)
        {
            _runtime.AssertGameThread();
            feedback = "";

            if (!CanManage())
            {
                feedback = "当前不是活动 P2P 房主或故障锁存中";
                RoleLogger.Warn("[Host]", "[P2P-WL] TryAdd rejected: " + feedback);
                return false;
            }

            if (target == CSteamID.Nil || !target.IsValid())
            {
                feedback = "SteamID 无效";
                RoleLogger.Warn("[Host]", "[P2P-WL] TryAdd rejected: " + feedback);
                return false;
            }

            CSteamID localUser = _runtime.LocalUser;
            if (localUser == CSteamID.Nil || !localUser.IsValid())
            {
                feedback = "房主 SteamID 无效";
                RoleLogger.Warn("[Host]", "[P2P-WL] TryAdd rejected: " + feedback);
                return false;
            }

            if (target == localUser)
            {
                feedback = "不能添加房主自身（已在 bootstrap 时加入）";
                RoleLogger.Warn("[Host]", "[P2P-WL] TryAdd rejected: " + feedback);
                return false;
            }

            if (string.IsNullOrEmpty(tag) || tag.Length > MaxTagLength)
            {
                feedback = $"tag 为空或超过 {MaxTagLength} 字符";
                RoleLogger.Warn("[Host]", "[P2P-WL] TryAdd rejected: " + feedback);
                return false;
            }

            bool shouldDisconnect = false;
            lock (WhitelistSync)
            {
                if (!CanManage())
                {
                    feedback = "锁内二次校验失败";
                    RoleLogger.Warn("[Host]", "[P2P-WL] TryAdd rejected: " + feedback);
                    return false;
                }

                // 失败走 Restore（若 snapshot 非 null）+ fault-latch + 锁外 disconnect
                List<SteamWhitelistID> snapshot = null;
                try
                {
                    snapshot = _store.Snapshot();
                    _store.AddOrUpdate(target, tag, localUser);
                    _store.Save();
                    _store.Load();
                    if (!_store.Contains(target))
                    {
                        throw new InvalidOperationException(
                            "postcondition Contains(target)==false after Add");
                    }
                }
                catch (Exception ex)
                {
                    if (snapshot != null)
                    {
                        try { _store.Restore(snapshot); }
                        catch (Exception restoreEx)
                        {
                            SafeLogRestoreFailure("Add", restoreEx);
                        }
                    }
                    _persistenceFaulted = true;
                    RecordWhitelistFailure("Add", target, ex);
                    feedback = "添加失败，已断开 P2P 会话以保护存档：" + ex.GetType().Name;
                    shouldDisconnect = true;
                }
            }

            if (shouldDisconnect)
            {
                // 蓝图 §2.1：disconnect 必须锁外、且仅一次
                RoleLogger.Warn("[Host]",
                    "[P2P-WL] TryAdd convergence: invoking DisconnectCurrentP2PHost");
                _disconnect.DisconnectCurrentP2PHost();
                return false;
            }

            feedback = "添加成功";
            RoleLogger.Info("[Host]",
                $"[P2P-WL] TryAdd success: target={target.m_SteamID} tag={tag}");
            return true;
        }

        /// <summary>
        ///   拒绝 Nil/无效/self；
        ///   Snapshot -> Remove：Remove=false 是 no-op（不 Save、不 disconnect）；
        ///   Remove=true 后执行 Save -> Load -> !Contains(target)
        ///   失败走 shouldDisconnect 模板。
        /// </summary>
        internal static bool TryRemove(CSteamID target, out string feedback)
        {
            _runtime.AssertGameThread();
            feedback = "";

            if (!CanManage())
            {
                feedback = "当前不是活动 P2P 房主或故障锁存中";
                RoleLogger.Warn("[Host]", "[P2P-WL] TryRemove rejected: " + feedback);
                return false;
            }

            if (target == CSteamID.Nil || !target.IsValid())
            {
                feedback = "SteamID 无效";
                RoleLogger.Warn("[Host]", "[P2P-WL] TryRemove rejected: " + feedback);
                return false;
            }

            CSteamID localUser = _runtime.LocalUser;
            if (localUser == CSteamID.Nil || !localUser.IsValid())
            {
                feedback = "房主 SteamID 无效";
                RoleLogger.Warn("[Host]", "[P2P-WL] TryRemove rejected: " + feedback);
                return false;
            }

            if (target == localUser)
            {
                feedback = "不能移除房主自身";
                RoleLogger.Warn("[Host]", "[P2P-WL] TryRemove rejected: " + feedback);
                return false;
            }

            bool shouldDisconnect = false;
            lock (WhitelistSync)
            {
                if (!CanManage())
                {
                    feedback = "锁内二次校验失败";
                    RoleLogger.Warn("[Host]", "[P2P-WL] TryRemove rejected: " + feedback);
                    return false;
                }

                // 失败走 Restore（若 snapshot 非 null）+ fault-latch + 锁外 disconnect
                List<SteamWhitelistID> snapshot = null;
                try
                {
                    snapshot = _store.Snapshot();
                    bool removed = _store.Remove(target);
                    if (!removed)
                    {
                        feedback = "条目已不在白名单中";
                        RoleLogger.Info("[Host]",
                            $"[P2P-WL] TryRemove no-op: target={target.m_SteamID} not in list");
                        return false;
                    }

                    _store.Save();
                    _store.Load();
                    if (_store.Contains(target))
                    {
                        throw new InvalidOperationException(
                            "postcondition Contains(target)==true after Remove");
                    }
                }
                catch (Exception ex)
                {
                    if (snapshot != null)
                    {
                        try { _store.Restore(snapshot); }
                        catch (Exception restoreEx)
                        {
                            SafeLogRestoreFailure("Remove", restoreEx);
                        }
                    }
                    _persistenceFaulted = true;
                    RecordWhitelistFailure("Remove", target, ex);
                    feedback = "移除失败，已断开 P2P 会话以保护存档：" + ex.GetType().Name;
                    shouldDisconnect = true;
                }
            }

            if (shouldDisconnect)
            {
                RoleLogger.Warn("[Host]",
                    "[P2P-WL] TryRemove convergence: invoking DisconnectCurrentP2PHost");
                _disconnect.DisconnectCurrentP2PHost();
                return false;
            }

            feedback = "移除成功";
            RoleLogger.Info("[Host]",
                $"[P2P-WL] TryRemove success: target={target.m_SteamID}");
            return true;
        }

        /// <summary>
        /// Route B revoke transaction. It has the same Snapshot -> Remove -> Save -> Load ->
        /// !Contains proof as TryRemove, but never disconnects any player on persistence failure.
        /// P2PApprovalManager issues the one targeted kick only after this method returns true.
        /// </summary>
        internal static bool TryRemoveForApprovalRevoke(CSteamID target, out string feedback)
        {
            _runtime.AssertGameThread();
            feedback = "";
            if (!CanManage())
            {
                feedback = "当前不是活动 P2P 房主或故障锁存中";
                return false;
            }
            if (target == CSteamID.Nil || !target.IsValid())
            {
                feedback = "SteamID 无效";
                return false;
            }
            CSteamID localUser = _runtime.LocalUser;
            if (localUser == CSteamID.Nil || !localUser.IsValid() || target == localUser)
            {
                feedback = "不能移除房主自身";
                return false;
            }

            lock (WhitelistSync)
            {
                if (!CanManage())
                {
                    feedback = "锁内二次校验失败";
                    return false;
                }

                List<SteamWhitelistID> snapshot = null;
                try
                {
                    snapshot = _store.Snapshot();
                    if (!_store.Remove(target))
                    {
                        feedback = "条目已不在白名单中";
                        return false;
                    }
                    _store.Save();
                    _store.Load();
                    if (_store.Contains(target))
                        throw new InvalidOperationException("postcondition Contains(target)==true after Remove");
                }
                catch (Exception ex)
                {
                    if (snapshot != null)
                    {
                        try { _store.Restore(snapshot); }
                        catch (Exception restoreEx) { SafeLogRestoreFailure("ApprovalRevoke", restoreEx); }
                    }
                    _persistenceFaulted = true;
                    RecordWhitelistFailure("ApprovalRevoke", target, ex);
                    feedback = "移除失败，客户端保持连接：" + ex.GetType().Name;
                    RoleLogger.Error("[Host]", "[P2P-WL] approval revoke persistence failed; no guest kick: " + ex.GetType().Name);
                    return false;
                }
            }

            feedback = "移除成功";
            RoleLogger.Info("[Host]", "[P2P-WL] approval revoke committed: target=" + target.m_SteamID);
            return true;
        }

        /// <summary>
        /// UI 列表快照：仅在 CanManage() 时返回深拷贝；否则返回空只读列表（蓝图 §2.1）。
        /// </summary>
        internal static IReadOnlyList<SteamWhitelistID> SnapshotForUi()
        {
            _runtime.AssertGameThread();
            if (!CanManage())
            {
                return new List<SteamWhitelistID>(0);
            }

            lock (WhitelistSync)
            {
                if (!CanManage())
                {
                    return new List<SteamWhitelistID>(0);
                }
                return _store.Snapshot();
            }
        }

        /// <summary>
        /// Physical whitelist membership for the world-entry state machine and P-key UI.
        /// This deliberately bypasses the Route B handshake permit postfix.
        /// </summary>
        internal static bool ContainsForUi(CSteamID target)
        {
            _runtime.AssertGameThread();
            if (target == CSteamID.Nil || !target.IsValid() || !CanManage()) return false;
            lock (WhitelistSync)
            {
                return CanManage() && _store.Contains(target);
            }
        }

        // ===== 文件证据记录（蓝图 §2.1 + 设计 §3.9）=====

        /// <summary>
        /// 记录 Whitelist.dat / Whitelist.dat~ 的 exists/length/SHA-256 + 操作 + 目标 ID + 异常类型。
        /// 仅记录证据，不实现自动恢复；记录失败不得替换业务异常。
        /// </summary>
        private static void RecordWhitelistFailure(string operation, CSteamID target, Exception ex)
        {
            try
            {
                string primaryPath = ResolveWhitelistDiskPath();
                string backupPath = primaryPath == null ? null : (primaryPath + "~");

                string primaryEvidence = DescribeFile(primaryPath);
                string backupEvidence = DescribeFile(backupPath);

                RoleLogger.Error("[Host]",
                    $"[P2P-WL] persistence-failure" +
                    $" operation={operation}" +
                    $" target={(target == CSteamID.Nil ? "Nil" : target.m_SteamID.ToString())}" +
                    $" exceptionType={(ex == null ? "null" : ex.GetType().FullName)}" +
                    $" whitelistDat={primaryEvidence}" +
                    $" whitelistDatTilde={backupEvidence}");
            }
            catch (Exception logEx)
            {
                // 记录失败不得替换业务异常
                RoleLogger.Error("[Host]",
                    "[P2P-WL] RecordWhitelistFailure itself failed: " + logEx.GetType().Name);
            }
        }

        private static void SafeLogRestoreFailure(string operation, Exception restoreEx)
        {
            // restore snapshot 失败只能安全记录，不能替换原异常（蓝图 §2.1）
            RoleLogger.Error("[Host]",
                $"[P2P-WL] restore-snapshot-failed operation={operation}" +
                $" exceptionType={restoreEx.GetType().FullName}" +
                $" message={restoreEx.Message}");
        }

        private static string ResolveWhitelistDiskPath()
        {
            try
            {
                // 对齐 ServerSavedata.transformPath: directory + "/" + Provider.serverID + path
                // ServerSavedata.directory 在非 Dedicated 进程下为 "/Worlds"
                string relative = ServerSavedata.directory + "/" + Provider.serverID + "/Server/Whitelist.dat";
                return Path.Combine(ReadWrite.PATH, relative.TrimStart('/'));
            }
            catch
            {
                return null;
            }
        }

        private static string DescribeFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return "path=null";
            try
            {
                if (!File.Exists(path)) return "exists=false";
                FileInfo fi = new FileInfo(path);
                string sha = ComputeSha256(path);
                return $"exists=true length={fi.Length} sha256={sha}";
            }
            catch (Exception ex)
            {
                return "describe-failed:" + ex.GetType().Name;
            }
        }

        private static string ComputeSha256(string path)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(path))
                {
                    byte[] hash = sha.ComputeHash(stream);
                    return BitConverter.ToString(hash).Replace("-", "").ToUpperInvariant();
                }
            }
            catch (Exception ex)
            {
                return "sha-failed:" + ex.GetType().Name;
            }
        }

        // ===== 测试 hook（蓝图 §2.1 + §3 授权）=====
        // internal、返回 IDisposable；一次性替换 store/runtime/disconnect 三个依赖；
        // Dispose/try-finally 恢复生产默认依赖和 _persistenceFaulted=false。
        // 生产代码无 public setter。

        internal static IDisposable InstallTestDependencies(
            IWhitelistStore store,
            IWhitelistRuntimeContext runtime,
            IWhitelistDisconnectGateway disconnect)
        {
            return new TestDependencyScope(store, runtime, disconnect);
        }

        private sealed class TestDependencyScope : IDisposable
        {
            private readonly IWhitelistStore _prevStore;
            private readonly IWhitelistRuntimeContext _prevRuntime;
            private readonly IWhitelistDisconnectGateway _prevDisconnect;
            private bool _disposed;

            internal TestDependencyScope(
                IWhitelistStore store,
                IWhitelistRuntimeContext runtime,
                IWhitelistDisconnectGateway disconnect)
            {
                _prevStore = _store;
                _prevRuntime = _runtime;
                _prevDisconnect = _disconnect;
                _store = store;
                _runtime = runtime;
                _disconnect = disconnect;
                _persistenceFaulted = false;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _store = _prevStore ?? new NativeWhitelistStore();
                _runtime = _prevRuntime ?? new NativeWhitelistRuntimeContext();
                _disconnect = _prevDisconnect ?? new NativeWhitelistDisconnectGateway();
                _persistenceFaulted = false;
            }
        }
    }
}
