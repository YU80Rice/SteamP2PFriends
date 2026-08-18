using HarmonyLib;
using SDG.NetPak;
using SDG.NetTransport;
using SDG.NetTransport.Loopback;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    ///
    /// 补全真正的发送端诊断，替换 D-Vis-15 的主要用途。
    ///
    ///   - sendPlayerStates 改为 Prefix + Postfix + Finalizer 三 Hook
    ///   - Prefix 采集广播前快照（seq-before、房主 updates、远程 updates、玩家数和 transport）
    ///   - Postfix 采集广播后快照（seq-after、队列是否按预期清零）
    ///   - Finalizer 记录原方法异常（防止 Postfix 未执行误呈现为"未调用"）
    ///   - 高频日志节流：前 10 包逐包记录，之后每秒聚合一次
    ///
    /// 诊断点：
    ///   1. PlayerManager.Update Postfix：每秒聚合一次 gate 状态与是否跨过发送节拍
    ///   2. PlayerManager.sendPlayerStates Prefix：广播前 host-local 与 remote 的 movement.updates.Count
    ///   3. PlayerManager.sendPlayerStates Postfix：调用完成、seq 递增、队列清零验证
    ///   4. PlayerManager.sendPlayerStates Finalizer：原方法异常捕获
    ///   5. 客机 PlayerManager.ReceivePlayerStates Postfix：包 seq、调用次数（节流）
    ///
    ///
    /// 严格禁止（审计 §8）：
    ///   - 把 SendMessageToClient Postfix 返回称为网络投递成功（D-Vis-16 文案已改）
    ///   - 修改原方法参数或返回值
    ///   - 吞异常
    /// </summary>
    public static class PlayerManagerBroadcastDiagnosticPatch
    {
        private const string HarmonyId = SteamP2PFriendsPlugin.HARMONY_ID;

        public static bool UpdatePostfixRegistered { get; private set; }
        public static bool SendPrefixRegistered { get; private set; }
        public static bool SendPostfixRegistered { get; private set; }
        public static bool SendFinalizerRegistered { get; private set; }
        public static bool ReceivePostfixRegistered { get; private set; }

        public static bool P1S5_Registered =>
            UpdatePostfixRegistered && SendPrefixRegistered && SendPostfixRegistered
            && SendFinalizerRegistered && ReceivePostfixRegistered;

        public static bool AllRegistrationsSucceeded => P1S5_Registered;

        // 诊断计数器
        private static long _updateTickCount;
        private static long _sendPlayerStatesCount;
        private static long _receivePlayerStatesCount;
        private static float _lastGateSnapshotTime;
        private const float GateSnapshotInterval = 1.0f;

        private const int FirstNVerbose = 10;
        private static long _verboseLoggedCount;
        private static float _lastSendAggregateTime;
        private const float SendAggregateInterval = 1.0f;
        private static long _aggregateCallCount;
        private static long _aggregateQueueClearedCount;
        private static long _aggregateQueueNotClearedCount;
        private static long _aggregateHostExceptionCount;
        private static uint _aggregateSeqMin;
        private static uint _aggregateSeqMax;
        private static long _aggregateHostUpdatesTotal;
        private static long _aggregateRemoteUpdatesTotal;

        private const int FirstNReceiveVerbose = 10;
        private static long _receiveVerboseLoggedCount;
        private static float _lastReceiveAggregateTime;
        private const float ReceiveAggregateInterval = 1.0f;
        private static long _receiveAggregateCallCount;
        private static uint _receiveAggregateSeqMin;
        private static uint _receiveAggregateSeqMax;

        // 反射缓存
        private static FieldInfo _seqField;
        private static FieldInfo _lastTickField;
        private static bool _reflectionCached;

        private class SendSnapshot
        {
            public uint SeqBefore;
            public int HostLocalUpdatesCount;
            public int RemoteClientsCount;
            public int TotalPlayersWithUpdates;
            public int TotalUpdates;
            public int RemoteClientCount;
            public string TransportSummary;
            public float CaptureTime;
        }

        // 单帧快照（Unity 主线程同步，无需线程安全）
        private static SendSnapshot _currentSnapshot;

        public static bool RegisterManual(Harmony harmony)
        {
            CacheReflection();

            UpdatePostfixRegistered = RegisterUpdatePostfix(harmony);
            SendPrefixRegistered = RegisterSendPlayerStatesPrefix(harmony);
            SendPostfixRegistered = RegisterSendPlayerStatesPostfix(harmony);
            SendFinalizerRegistered = RegisterSendPlayerStatesFinalizer(harmony);
            ReceivePostfixRegistered = RegisterReceivePlayerStatesPostfix(harmony);

            RoleLogger.Info("[Shared]",
                $"[P1-S5] PlayerManagerBroadcastDiagnosticPatch 汇总: P1-S5={P1S5_Registered} " +
                $"updatePost={UpdatePostfixRegistered} " +
                $"sendPre={SendPrefixRegistered} sendPost={SendPostfixRegistered} sendFinal={SendFinalizerRegistered} " +
                $"receivePost={ReceivePostfixRegistered} " +
                $"reflectionSeq={(_seqField != null)} reflectionLastTick={(_lastTickField != null)}");
            return AllRegistrationsSucceeded;
        }

        private static void CacheReflection()
        {
            if (_reflectionCached) return;
            _reflectionCached = true;
            try
            {
                _seqField = AccessTools.Field(typeof(PlayerManager), "seq");
                _lastTickField = AccessTools.Field(typeof(PlayerManager), "lastTick");

                if (_seqField == null)
                {
                    RoleLogger.Warn("[Shared]", "[P1-S5] PlayerManager.seq 反射失败（private static 字段）");
                }
                if (_lastTickField == null)
                {
                    RoleLogger.Warn("[Shared]", "[P1-S5] PlayerManager.lastTick 反射失败（private static 字段）");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Shared]", $"[P1-S5] 反射缓存异常: {ex.Message}");
            }
        }

        private static bool RegisterUpdatePostfix(Harmony harmony)
        {
            const string Label = "P1-S5 PlayerManager.Update Postfix";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(PlayerManager), "Update");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[P1-S5] !!! {Label} 反射失败");
                    return false;
                }
                MethodInfo postfix = typeof(Hooks).GetMethod(nameof(Hooks.UpdatePostfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, postfix: new HarmonyMethod(postfix));
                RoleLogger.Info("[Shared]", $"[P1-S5] OK {Label} 已登记");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P1-S5] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        private static bool RegisterSendPlayerStatesPrefix(Harmony harmony)
        {
            const string Label = "P1-S5 PlayerManager.sendPlayerStates Prefix";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(PlayerManager), "sendPlayerStates");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[P1-S5] !!! {Label} 反射失败");
                    return false;
                }
                MethodInfo prefix = typeof(Hooks).GetMethod(nameof(Hooks.SendPlayerStatesPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, prefix: new HarmonyMethod(prefix));
                RoleLogger.Info("[Shared]", $"[P1-S5] OK {Label} 已登记");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P1-S5] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        private static bool RegisterSendPlayerStatesPostfix(Harmony harmony)
        {
            const string Label = "P1-S5 PlayerManager.sendPlayerStates Postfix";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(PlayerManager), "sendPlayerStates");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[P1-S5] !!! {Label} 反射失败");
                    return false;
                }
                MethodInfo postfix = typeof(Hooks).GetMethod(nameof(Hooks.SendPlayerStatesPostfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, postfix: new HarmonyMethod(postfix));
                RoleLogger.Info("[Shared]", $"[P1-S5] OK {Label} 已登记");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P1-S5] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        private static bool RegisterSendPlayerStatesFinalizer(Harmony harmony)
        {
            const string Label = "P1-S5 PlayerManager.sendPlayerStates Finalizer";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(PlayerManager), "sendPlayerStates");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[P1-S5] !!! {Label} 反射失败");
                    return false;
                }
                MethodInfo finalizer = typeof(Hooks).GetMethod(nameof(Hooks.SendPlayerStatesFinalizer),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, finalizer: new HarmonyMethod(finalizer));
                RoleLogger.Info("[Shared]", $"[P1-S5] OK {Label} 已登记");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P1-S5] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        private static bool RegisterReceivePlayerStatesPostfix(Harmony harmony)
        {
            const string Label = "P1-S5 PlayerManager.ReceivePlayerStates Postfix";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(PlayerManager), "ReceivePlayerStates");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[P1-S5] !!! {Label} 反射失败");
                    return false;
                }
                MethodInfo postfix = typeof(Hooks).GetMethod(nameof(Hooks.ReceivePlayerStatesPostfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, postfix: new HarmonyMethod(postfix));
                RoleLogger.Info("[Shared]", $"[P1-S5] OK {Label} 已登记");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P1-S5] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        public static void OnClientDisconnected()
        {
            _updateTickCount = 0;
            _sendPlayerStatesCount = 0;
            _receivePlayerStatesCount = 0;
            _lastGateSnapshotTime = 0f;
            _verboseLoggedCount = 0;
            _lastSendAggregateTime = 0f;
            _aggregateCallCount = 0;
            _aggregateQueueClearedCount = 0;
            _aggregateQueueNotClearedCount = 0;
            _aggregateHostExceptionCount = 0;
            _aggregateSeqMin = 0;
            _aggregateSeqMax = 0;
            _aggregateHostUpdatesTotal = 0;
            _aggregateRemoteUpdatesTotal = 0;
            _receiveVerboseLoggedCount = 0;
            _lastReceiveAggregateTime = 0f;
            _receiveAggregateCallCount = 0;
            _receiveAggregateSeqMin = 0;
            _receiveAggregateSeqMax = 0;
            _currentSnapshot = null;
        }

        private static bool ShouldLogVerbose()
        {
            try
            {
                return SteamP2PFriendsPlugin.VerboseLog != null
                    && SteamP2PFriendsPlugin.VerboseLog.Value
                    && SteamP2PFriendsPlugin.RouteDiagnostics != null
                    && SteamP2PFriendsPlugin.RouteDiagnostics.Value;
            }
            catch
            {
                return false;
            }
        }

        private static class Hooks
        {
            /// <summary>
            /// 每秒聚合一次 gate 状态与是否跨过发送节拍。
            /// </summary>
            internal static void UpdatePostfix(PlayerManager __instance)
            {
                try
                {
                    _updateTickCount++;

                    float now = Time.realtimeSinceStartup;
                    if (now - _lastGateSnapshotTime < GateSnapshotInterval) return;
                    _lastGateSnapshotTime = now;

                    bool isServer = Provider.isServer;
                    bool levelLoaded = Level.isLoaded;
                    bool isDedicated = Dedicator.IsDedicatedServer;
                    bool isP2PHost = HostManager.IsP2PHostMode;
                    bool broadcastEligible = PlayerManagerBroadcastPatch.IsDedicatedOrP2PHost();

                    float lastTick = 0f;
                    if (_lastTickField != null)
                    {
                        try { lastTick = (float)_lastTickField.GetValue(null); } catch { }
                    }
                    float timeSinceLastTick = now - lastTick;
                    float updateTime = Provider.UPDATE_TIME;
                    bool tickDue = timeSinceLastTick > updateTime;

                    RoleLogger.Info("[Host]",
                        $"[P1-S5] Update gate snapshot t={now:F2}s " +
                        $"isServer={isServer} levelLoaded={levelLoaded} " +
                        $"isDedicated={isDedicated} isP2PHost={isP2PHost} broadcastEligible={broadcastEligible} " +
                        $"timeSinceLastTick={timeSinceLastTick:F3}s updateTime={updateTime:F3}s tickDue={tickDue} " +
                        $"updateTickCount={_updateTickCount} sendPlayerStatesCount={_sendPlayerStatesCount}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Host]", $"[P1-S5] UpdatePostfix 异常（不阻断）: {ex.Message}");
                }
            }

            /// <summary>
            /// 采集广播前快照：seq-before、房主 updates、远程 updates、玩家数和 transport。
            /// 快照存入 _currentSnapshot 供 Postfix/Finalizer 读取。
            /// </summary>
            internal static void SendPlayerStatesPrefix(PlayerManager __instance)
            {
                try
                {
                    _sendPlayerStatesCount++;

                    uint seq = 0;
                    if (_seqField != null)
                    {
                        try { seq = (uint)_seqField.GetValue(null); } catch { }
                    }

                    int hostLocalUpdatesCount = 0;
                    int remoteClientsCount = 0;
                    int totalPlayersWithUpdates = 0;
                    int totalUpdates = 0;
                    int remoteClientCount = 0;
                    int loopbackCount = 0;
                    int snsCount = 0;
                    int otherTransportCount = 0;

                    if (Provider.clients != null)
                    {
                        foreach (SteamPlayer sp in Provider.clients)
                        {
                            if (sp == null || sp.player == null || sp.player.movement == null
                                || sp.player.movement.updates == null) continue;

                            int updatesCount = sp.player.movement.updates.Count;
                            totalUpdates += updatesCount;
                            if (updatesCount > 0) totalPlayersWithUpdates++;

                            if (sp.IsLocalServerHost)
                            {
                                hostLocalUpdatesCount = updatesCount;
                            }
                            else
                            {
                                remoteClientsCount++;
                            }

                            // 统计 transport 类型
                            try
                            {
                                ITransportConnection tc = sp.transportConnection;
                                if (ReferenceEquals(tc, null))
                                {
                                    otherTransportCount++;
                                }
                                else if (tc is TransportConnection_Loopback)
                                {
                                    loopbackCount++;
                                }
                                else
                                {
                                    snsCount++;
                                    remoteClientCount++;
                                }
                            }
                            catch
                            {
                                otherTransportCount++;
                            }
                        }
                    }

                    _currentSnapshot = new SendSnapshot
                    {
                        SeqBefore = seq,
                        HostLocalUpdatesCount = hostLocalUpdatesCount,
                        RemoteClientsCount = remoteClientsCount,
                        TotalPlayersWithUpdates = totalPlayersWithUpdates,
                        TotalUpdates = totalUpdates,
                        RemoteClientCount = remoteClientCount,
                        TransportSummary = $"loopback={loopbackCount} sns={snsCount} other={otherTransportCount}",
                        CaptureTime = Time.realtimeSinceStartup,
                    };

                    // 节流：前 10 包逐包，之后每秒聚合
                    if (ShouldLogVerbose() && _verboseLoggedCount < FirstNVerbose)
                    {
                        _verboseLoggedCount++;
                        RoleLogger.Info("[Host]",
                            $"[P1-S5] sendPlayerStates PREFIX seq-before={seq} " +
                            $"callCount={_sendPlayerStatesCount} " +
                            $"hostLocalUpdates={hostLocalUpdatesCount} " +
                            $"remoteClientsWithUpdates={remoteClientsCount} " +
                            $"totalPlayersWithUpdates={totalPlayersWithUpdates} " +
                            $"totalUpdates={totalUpdates} " +
                            $"transport: {_currentSnapshot.TransportSummary}");
                    }
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Host]", $"[P1-S5] SendPlayerStatesPrefix 异常（不阻断）: {ex.Message}");
                }
            }

            /// <summary>
            /// 采集广播后快照：seq-after、队列是否按预期清零。
            /// 原版 sendPlayerStates 返回前会清空所有玩家 movement.updates，因此 Postfix 中应全部为 0。
            /// </summary>
            internal static void SendPlayerStatesPostfix(PlayerManager __instance)
            {
                try
                {
                    SendSnapshot snap = _currentSnapshot;
                    _currentSnapshot = null;

                    if (snap == null)
                    {
                        // Prefix 未执行或异常，跳过
                        return;
                    }

                    uint seqAfter = 0;
                    if (_seqField != null)
                    {
                        try { seqAfter = (uint)_seqField.GetValue(null); } catch { }
                    }

                    // 验证队列是否按预期清零
                    int hostLocalUpdatesAfter = 0;
                    int remoteUpdatesAfter = 0;
                    int playersWithRemainingUpdates = 0;
                    if (Provider.clients != null)
                    {
                        foreach (SteamPlayer sp in Provider.clients)
                        {
                            if (sp == null || sp.player == null || sp.player.movement == null
                                || sp.player.movement.updates == null) continue;
                            int count = sp.player.movement.updates.Count;
                            if (count > 0)
                            {
                                playersWithRemainingUpdates++;
                                if (sp.IsLocalServerHost)
                                {
                                    hostLocalUpdatesAfter = count;
                                }
                                else
                                {
                                    remoteUpdatesAfter += count;
                                }
                            }
                        }
                    }

                    bool queueCleared = (hostLocalUpdatesAfter == 0) && (remoteUpdatesAfter == 0);

                    // 聚合计数
                    _aggregateCallCount++;
                    if (queueCleared) _aggregateQueueClearedCount++;
                    else _aggregateQueueNotClearedCount++;
                    if (_aggregateCallCount == 1 || snap.SeqBefore < _aggregateSeqMin) _aggregateSeqMin = snap.SeqBefore;
                    if (snap.SeqBefore > _aggregateSeqMax) _aggregateSeqMax = snap.SeqBefore;
                    _aggregateHostUpdatesTotal += snap.HostLocalUpdatesCount;
                    _aggregateRemoteUpdatesTotal += snap.TotalUpdates - snap.HostLocalUpdatesCount;

                    // 节流：前 10 包逐包，之后每秒聚合
                    if (ShouldLogVerbose() && _verboseLoggedCount < FirstNVerbose)
                    {
                        _verboseLoggedCount++;
                        RoleLogger.Info("[Host]",
                            $"[P1-S5] sendPlayerStates POSTFIX seq-before={snap.SeqBefore} seq-after={seqAfter} " +
                            $"queueCleared={queueCleared} " +
                            $"hostLocalUpdatesBefore={snap.HostLocalUpdatesCount} hostLocalUpdatesAfter={hostLocalUpdatesAfter} " +
                            $"remoteUpdatesBefore={snap.TotalUpdates - snap.HostLocalUpdatesCount} remoteUpdatesAfter={remoteUpdatesAfter} " +
                            $"playersWithRemainingUpdates={playersWithRemainingUpdates} " +
                            $"callCount={_sendPlayerStatesCount}");
                    }
                    else
                    {
                        // 每秒聚合一次
                        float now = Time.realtimeSinceStartup;
                        if (now - _lastSendAggregateTime >= SendAggregateInterval)
                        {
                            _lastSendAggregateTime = now;
                            RoleLogger.Info("[Host]",
                                $"[P1-S5] sendPlayerStates AGGREGATE (last {SendAggregateInterval:F1}s) " +
                                $"callCount={_aggregateCallCount} " +
                                $"seqRange=[{_aggregateSeqMin},{_aggregateSeqMax}] " +
                                $"queueCleared={_aggregateQueueClearedCount} queueNotCleared={_aggregateQueueNotClearedCount} " +
                                $"hostUpdatesTotal={_aggregateHostUpdatesTotal} remoteUpdatesTotal={_aggregateRemoteUpdatesTotal} " +
                                $"hostException={_aggregateHostExceptionCount} " +
                                $"totalCallCount={_sendPlayerStatesCount}");
                            // 重置聚合窗口
                            _aggregateCallCount = 0;
                            _aggregateQueueClearedCount = 0;
                            _aggregateQueueNotClearedCount = 0;
                            _aggregateHostExceptionCount = 0;
                            _aggregateSeqMin = 0;
                            _aggregateSeqMax = 0;
                            _aggregateHostUpdatesTotal = 0;
                            _aggregateRemoteUpdatesTotal = 0;
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Host]", $"[P1-S5] SendPlayerStatesPostfix 异常（不阻断）: {ex.Message}");
                }
            }

            /// <summary>
            /// 捕获原方法异常，防止 Postfix 未执行时误呈现为"未调用"。
            /// </summary>
            internal static System.Exception SendPlayerStatesFinalizer(System.Exception __exception)
            {
                try
                {
                    if (__exception != null)
                    {
                        _aggregateHostExceptionCount++;
                        SendSnapshot snap = _currentSnapshot;
                        _currentSnapshot = null;
                        RoleLogger.Error("[Host]",
                            $"[P1-S5] sendPlayerStates FINALIZER 原方法抛异常: {__exception.GetType().Name}: {__exception.Message} " +
                            $"seq-before={snap?.SeqBefore ?? 0} callCount={_sendPlayerStatesCount}");
                    }
                }
                catch
                {
                    // Finalizer 内部异常不能影响原异常传播
                }
                // 不吞异常：返回原异常让 Harmony 重新抛出
                return __exception;
            }

            /// <summary>
            /// 记录包 seq、调用次数。
            /// </summary>
            internal static void ReceivePlayerStatesPostfix(in ClientInvocationContext context)
            {
                try
                {
                    _receivePlayerStatesCount++;

                    uint seq = 0;
                    if (_seqField != null)
                    {
                        try { seq = (uint)_seqField.GetValue(null); } catch { }
                    }

                    // 节流：前 10 包逐包，之后每秒聚合
                    if (ShouldLogVerbose() && _receiveVerboseLoggedCount < FirstNReceiveVerbose)
                    {
                        _receiveVerboseLoggedCount++;
                        RoleLogger.Info("[Client]",
                            $"[P1-S5] ReceivePlayerStates CALLED seq={seq} " +
                            $"callCount={_receivePlayerStatesCount}");
                    }
                    else
                    {
                        // 每秒聚合
                        float now = Time.realtimeSinceStartup;
                        if (now - _lastReceiveAggregateTime >= ReceiveAggregateInterval)
                        {
                            _lastReceiveAggregateTime = now;
                            RoleLogger.Info("[Client]",
                                $"[P1-S5] ReceivePlayerStates AGGREGATE (last {ReceiveAggregateInterval:F1}s) " +
                                $"callCount={_receiveAggregateCallCount} " +
                                $"seqRange=[{_receiveAggregateSeqMin},{_receiveAggregateSeqMax}] " +
                                $"totalCallCount={_receivePlayerStatesCount}");
                            _receiveAggregateCallCount = 0;
                            _receiveAggregateSeqMin = 0;
                            _receiveAggregateSeqMax = 0;
                        }
                    }

                    _receiveAggregateCallCount++;
                    if (_receiveAggregateCallCount == 1 || seq < _receiveAggregateSeqMin) _receiveAggregateSeqMin = seq;
                    if (seq > _receiveAggregateSeqMax) _receiveAggregateSeqMax = seq;
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Client]", $"[P1-S5] ReceivePlayerStatesPostfix 异常（不阻断）: {ex.Message}");
                }
            }
        }
    }
}
