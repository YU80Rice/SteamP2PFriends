using HarmonyLib;
using SDG.NetPak;
using SDG.NetTransport;
using SDG.Unturned;
using SteamP2PFriends.Patches;
using SteamP2PFriends.Shared;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SteamP2PFriends.Patches.P0EDiagnostic
{
    /// <summary>
    ///
    /// 三个 P0EDiagnostic 补丁统一使用 WorldSyncDiagnosticCore.RegisterIdentityPatch
    /// 以确保 identity-based 验证（original + owner + PatchMethod + patchType）。
    ///
    /// Finding 5：ResetCounters 已存在但无调用点；需接入 RegisterSessionResetCallback。
    ///
    /// 返修后 3 个诊断点（DP-1..DP-3）：
    ///   DP-1 SendPlayerStates_Write Prefix：主机端写入前快照 forClient.culledPlayers + playersToSend + updateCount
    ///   DP-2 ReceivePlayerStates Postfix：客机端接收后聚合 seq + count + 调用次数
    ///   DP-3 PlayerMovement.tellState Prefix：客机端每实体落地前快照 position + isSentinel + before transform.position
    ///
    /// </summary>
    public static class PlayerManagerCullingDiagnosticPatch
    {
        private const string HarmonyId = SteamP2PFriendsPlugin.HARMONY_ID;
        private const string Label = "[P0-E-1-Diag/Culling]";

        public static bool DP1_SendPlayerStatesWritePrefix_Registered { get; private set; }
        public static bool DP2_ReceivePlayerStatesPostfix_Registered { get; private set; }
        public static bool DP3_TellStatePrefix_Registered { get; private set; }

        public static bool AllRegistrationsSucceeded =>
            DP1_SendPlayerStatesWritePrefix_Registered
            && DP2_ReceivePlayerStatesPostfix_Registered
            && DP3_TellStatePrefix_Registered;

        // 日志限频：周期聚合每 5 秒最多 1 条
        private const float AggregateInterval = 5.0f;
        // 逐包详细日志：前 10 包逐包记录
        private const int FirstNVerbose = 10;

        // DP-1 主机端：SendPlayerStates_Write 调用计数 + 节流
        private static long _dp1CallCount;
        private static long _dp1VerboseLogged;
        private static float _dp1LastAggregateTime;
        private static long _dp1AggregateCalls;
        private static long _dp1AggregateCulledTotal;
        private static long _dp1AggregatePlayersToSendTotal;
        private static long _dp1AggregateUpdateCountTotal;
        private static long _dp1AggregateSentinelWritesTotal;

        // DP-2 客机端：ReceivePlayerStates 调用计数 + 节流
        private static long _dp2CallCount;
        private static long _dp2VerboseLogged;
        private static float _dp2LastAggregateTime;
        private static long _dp2AggregateCalls;
        private static long _dp2AggregateTotalCount;

        // DP-3 客机端：tellState 调用计数 + 哨兵统计
        private static long _dp3CallCount;
        private static long _dp3VerboseLogged;
        private static float _dp3LastAggregateTime;
        private static long _dp3AggregateCalls;
        private static long _dp3AggregateSentinelCount;
        private static long _dp3AggregateNonSentinelCount;
        private static long _dp3AggregateLargeDeltaCount;

        // 反射缓存
        private static FieldInfo _seqField;
        private static FieldInfo _culledPlayersField;
        private static FieldInfo _playersToSendField;
        private static bool _reflectionCached;
        private static bool _reflectionFailed;

        private static int _sessionId = 0;
        public static int CurrentSessionId => _sessionId;

        static PlayerManagerCullingDiagnosticPatch()
        {
            WorldSyncDiagnosticCore.RegisterSessionResetCallback(OnSessionReset);
        }

        private static void OnSessionReset()
        {
            int oldSession = _sessionId;
            _sessionId++;
            ResetCounters();
            RoleLogger.Info("[Shared]",
                $"{Label} RESET oldSession={oldSession} newSession={_sessionId} reason=WorldSyncDiagnosticCore.ResetAll");
        }

        private static void CacheReflection()
        {
            if (_reflectionCached) return;
            _reflectionCached = true;
            try
            {
                _seqField = AccessTools.Field(typeof(PlayerManager), "seq");
                _culledPlayersField = AccessTools.Field(typeof(SteamPlayer), "culledPlayers");
                _playersToSendField = AccessTools.Field(typeof(PlayerManager), "playersToSend");
                if (_seqField == null || _culledPlayersField == null || _playersToSendField == null)
                {
                    RoleLogger.Error("[Shared]",
                        $"{Label} !!! CacheReflection 失败：seq={_seqField != null} "
                        + $"culledPlayers={_culledPlayersField != null} playersToSend={_playersToSendField != null}");
                    _reflectionFailed = true;
                }
                else
                {
                    RoleLogger.Info("[Shared]", $"{Label} CacheReflection OK：所有字段已缓存");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"{Label} !!! CacheReflection 异常: {ex.Message}");
                _reflectionFailed = true;
            }
        }

        /// <summary>
        /// </summary>
        public static bool RegisterManual(Harmony harmony)
        {
            CacheReflection();

            RoleLogger.Info("[Shared]", $"{Label} === 阶段 2 返修后诊断补丁登记开始 (3 DPs, identity-based) ===");

            if (harmony == null)
            {
                RoleLogger.Error("[Shared]", $"{Label} !!! harmony=null");
                return false;
            }

            if (_reflectionFailed)
            {
                RoleLogger.Error("[Shared]",
                    $"{Label} !!! reflectionFailed=true，按 fail-closed 不登记任何 DP");
                DP1_SendPlayerStatesWritePrefix_Registered = DP2_ReceivePlayerStatesPostfix_Registered
                    = DP3_TellStatePrefix_Registered = false;
                return false;
            }

            // DP-1 PlayerManager.SendPlayerStates_Write Prefix
            System.Type[] dp1Params = { typeof(NetPakWriter), typeof(ushort), typeof(SteamPlayer) };
            DP1_SendPlayerStatesWritePrefix_Registered = RegisterOne(harmony, typeof(PlayerManager),
                "SendPlayerStates_Write", dp1Params,
                nameof(DP1_SendPlayerStatesWrite_Prefix), HarmonyPatchType.Prefix, "DP-1-SendPlayerStates_Write-Prefix");

            // DP-2 PlayerManager.ReceivePlayerStates Postfix
            System.Type[] dp2Params = { typeof(ClientInvocationContext).MakeByRefType() };
            DP2_ReceivePlayerStatesPostfix_Registered = RegisterOne(harmony, typeof(PlayerManager),
                "ReceivePlayerStates", dp2Params,
                nameof(DP2_ReceivePlayerStates_Postfix), HarmonyPatchType.Postfix, "DP-2-ReceivePlayerStates-Postfix");

            // DP-3 PlayerMovement.tellState Prefix
            System.Type[] dp3Params = { typeof(Vector3), typeof(byte), typeof(byte) };
            DP3_TellStatePrefix_Registered = RegisterOne(harmony, typeof(PlayerMovement),
                "tellState", dp3Params,
                nameof(DP3_TellState_Prefix), HarmonyPatchType.Prefix, "DP-3-tellState-Prefix");

            bool ok = AllRegistrationsSucceeded;
            RoleLogger.Info("[Shared]",
                $"{Label} === 阶段 2 返修后诊断补丁登记完成 ok={ok} "
                + $"DP1={DP1_SendPlayerStatesWritePrefix_Registered} "
                + $"DP2={DP2_ReceivePlayerStatesPostfix_Registered} "
                + $"DP3={DP3_TellStatePrefix_Registered} ===");
            return ok;
        }

        private static bool RegisterOne(Harmony harmony, System.Type targetType, string methodName,
            System.Type[] paramTypes, string hookName, HarmonyPatchType patchType, string label)
        {
            try
            {
                MethodInfo hook = typeof(PlayerManagerCullingDiagnosticPatch)
                    .GetMethod(hookName, BindingFlags.NonPublic | BindingFlags.Static);
                if (hook == null)
                {
                    RoleLogger.Error("[Shared]", $"{Label} !!! {label} hook MethodInfo 未找到: {hookName}");
                    return false;
                }
                return WorldSyncDiagnosticCore.RegisterIdentityPatch(
                    harmony, targetType, methodName, paramTypes, hook, patchType, label);
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"{Label} !!! {label} 登记异常: {ex.Message}");
                return false;
            }
        }

        // ========================================================================
        // DP-1 PlayerManager.SendPlayerStates_Write Prefix
        // 主机端：写入前快照 forClient.culledPlayers + playersToSend + updateCount + 哨兵预期写入数
        // ========================================================================
        private static void DP1_SendPlayerStatesWrite_Prefix(
            PlayerManager __instance,
            NetPakWriter writer,
            ushort updateCount,
            SteamPlayer forClient)
        {
            try
            {
                _dp1CallCount++;

                // 反射读取 forClient.culledPlayers
                int culledCount = -1;
                int sentinelWritesExpected = 0;
                if (_culledPlayersField != null && forClient != null)
                {
                    try
                    {
                        var set = _culledPlayersField.GetValue(forClient) as HashSet<Steamworks.CSteamID>;
                        if (set != null)
                        {
                            culledCount = set.Count;
                        }
                    }
                    catch { culledCount = -2; }
                }

                // 反射读取 __instance.playersToSend
                int playersToSendCount = -1;
                if (_playersToSendField != null && __instance != null)
                {
                    try
                    {
                        var list = _playersToSendField.GetValue(__instance) as List<SteamPlayer>;
                        if (list != null)
                        {
                            playersToSendCount = list.Count;
                            if (_culledPlayersField != null && forClient != null)
                            {
                                var set = _culledPlayersField.GetValue(forClient) as HashSet<Steamworks.CSteamID>;
                                if (set != null)
                                {
                                    foreach (var sp in list)
                                    {
                                        if (sp == null) continue;
                                        try
                                        {
                                            if (set.Contains(sp.playerID.steamID)) sentinelWritesExpected++;
                                        }
                                        catch { }
                                    }
                                }
                            }
                        }
                    }
                    catch { playersToSendCount = -2; }
                }

                string forClientPosStr = "n/a";
                string forClientMaskedId = "n/a";
                try
                {
                    if (forClient != null)
                    {
                        forClientMaskedId = DiagnosticMaskUtil.MaskSteamId(forClient.playerID.steamID);
                        if (forClient.model != null && forClient.model.transform != null)
                        {
                            Vector3 p = forClient.model.transform.position;
                            forClientPosStr = $"({p.x:F1},{p.y:F1},{p.z:F1})";
                        }
                    }
                }
                catch { }

                if (_dp1VerboseLogged < FirstNVerbose)
                {
                    _dp1VerboseLogged++;
                    RoleLogger.Info("[Host]",
                        $"{Label} DP-1 SendWrite VERBOSE session={_sessionId} #{_dp1CallCount} " +
                        $"forClient={forClientMaskedId} pos={forClientPosStr} " +
                        $"updateCount={updateCount} playersToSend={playersToSendCount} " +
                        $"culledCount={culledCount} sentinelWritesExpected={sentinelWritesExpected}");
                }
                else
                {
                    float now = Time.realtimeSinceStartup;
                    if (now - _dp1LastAggregateTime >= AggregateInterval)
                    {
                        _dp1LastAggregateTime = now;
                        RoleLogger.Info("[Host]",
                            $"{Label} DP-1 SendWrite AGGREGATE session={_sessionId} (last {AggregateInterval:F0}s) " +
                            $"calls={_dp1AggregateCalls} " +
                            $"totalCulled={_dp1AggregateCulledTotal} " +
                            $"totalPlayersToSend={_dp1AggregatePlayersToSendTotal} " +
                            $"totalUpdateCount={_dp1AggregateUpdateCountTotal} " +
                            $"totalSentinelWritesExpected={_dp1AggregateSentinelWritesTotal} " +
                            $"totalCalls={_dp1CallCount}");
                        _dp1AggregateCalls = 0;
                        _dp1AggregateCulledTotal = 0;
                        _dp1AggregatePlayersToSendTotal = 0;
                        _dp1AggregateUpdateCountTotal = 0;
                        _dp1AggregateSentinelWritesTotal = 0;
                    }
                }

                _dp1AggregateCalls++;
                if (culledCount > 0) _dp1AggregateCulledTotal += culledCount;
                if (playersToSendCount > 0) _dp1AggregatePlayersToSendTotal += playersToSendCount;
                _dp1AggregateUpdateCountTotal += updateCount;
                _dp1AggregateSentinelWritesTotal += sentinelWritesExpected;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Host]", $"{Label} DP-1 Prefix 异常（不阻断）: {ex.Message}");
            }
        }

        // ========================================================================
        // DP-2 PlayerManager.ReceivePlayerStates Postfix
        // 客机端：接收后聚合 seq + count + 调用次数
        // ========================================================================
        private static void DP2_ReceivePlayerStates_Postfix(in ClientInvocationContext context)
        {
            try
            {
                _dp2CallCount++;

                uint seq = 0;
                if (_seqField != null)
                {
                    try { seq = (uint)_seqField.GetValue(null); } catch { }
                }

                if (_dp2VerboseLogged < FirstNVerbose)
                {
                    _dp2VerboseLogged++;
                    RoleLogger.Info("[Client]",
                        $"{Label} DP-2 Receive VERBOSE session={_sessionId} #{_dp2CallCount} seq={seq}");
                }
                else
                {
                    float now = Time.realtimeSinceStartup;
                    if (now - _dp2LastAggregateTime >= AggregateInterval)
                    {
                        _dp2LastAggregateTime = now;
                        RoleLogger.Info("[Client]",
                            $"{Label} DP-2 Receive AGGREGATE session={_sessionId} (last {AggregateInterval:F0}s) " +
                            $"calls={_dp2AggregateCalls} " +
                            $"totalTellStateCalls={_dp2AggregateTotalCount} " +
                            $"totalCalls={_dp2CallCount}");
                        _dp2AggregateCalls = 0;
                        _dp2AggregateTotalCount = 0;
                    }
                }

                _dp2AggregateCalls++;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Client]", $"{Label} DP-2 Postfix 异常（不阻断）: {ex.Message}");
            }
        }

        // ========================================================================
        // DP-3 PlayerMovement.tellState Prefix
        // 客机端：每实体落地前快照 position + isSentinel + before transform.position
        // ========================================================================
        private static void DP3_TellState_Prefix(
            PlayerMovement __instance,
            Vector3 newPosition,
            byte newPitch,
            byte newYaw)
        {
            try
            {
                if (__instance != null && __instance.channel != null && __instance.channel.IsLocalPlayer)
                {
                    return;
                }

                _dp3CallCount++;
                _dp2AggregateTotalCount++;

                Vector3 sentinel = PlayerManager.CulledPosition;
                bool isSentinel = newPosition.x == sentinel.x
                    && newPosition.y == sentinel.y
                    && newPosition.z == sentinel.z;

                string beforePosStr = "n/a";
                bool isLargeDelta = false;
                try
                {
                    if (__instance != null && __instance.transform != null)
                    {
                        Vector3 before = __instance.transform.position;
                        beforePosStr = $"({before.x:F1},{before.y:F1},{before.z:F1})";
                        float sqrDelta = (newPosition - before).sqrMagnitude;
                        isLargeDelta = sqrDelta > 16f * 16f;
                    }
                }
                catch { }

                string maskedId = "n/a";
                try
                {
                    if (__instance?.channel?.owner != null)
                    {
                        maskedId = DiagnosticMaskUtil.MaskSteamId(__instance.channel.owner.playerID.steamID);
                    }
                }
                catch { }

                if (_dp3VerboseLogged < FirstNVerbose)
                {
                    _dp3VerboseLogged++;
                    RoleLogger.Info("[Client]",
                        $"{Label} DP-3 tellState VERBOSE session={_sessionId} #{_dp3CallCount} " +
                        $"player={maskedId} " +
                        $"newPos=({newPosition.x:F1},{newPosition.y:F1},{newPosition.z:F1}) " +
                        $"pitch={newPitch} yaw={newYaw} " +
                        $"isSentinel={isSentinel} beforePos={beforePosStr} isLargeDelta={isLargeDelta}");
                }
                else
                {
                    float now = Time.realtimeSinceStartup;
                    if (now - _dp3LastAggregateTime >= AggregateInterval)
                    {
                        _dp3LastAggregateTime = now;
                        long total = _dp3AggregateSentinelCount + _dp3AggregateNonSentinelCount;
                        double sentinelRatio = total > 0
                            ? (double)_dp3AggregateSentinelCount / total * 100.0
                            : 0.0;
                        RoleLogger.Info("[Client]",
                            $"{Label} DP-3 tellState AGGREGATE session={_sessionId} (last {AggregateInterval:F0}s) " +
                            $"calls={_dp3AggregateCalls} " +
                            $"sentinel={_dp3AggregateSentinelCount} nonSentinel={_dp3AggregateNonSentinelCount} " +
                            $"sentinelRatio={sentinelRatio:F1}% largeDelta={_dp3AggregateLargeDeltaCount} " +
                            $"totalCalls={_dp3CallCount}");
                        _dp3AggregateCalls = 0;
                        _dp3AggregateSentinelCount = 0;
                        _dp3AggregateNonSentinelCount = 0;
                        _dp3AggregateLargeDeltaCount = 0;
                    }
                }

                _dp3AggregateCalls++;
                if (isSentinel) _dp3AggregateSentinelCount++;
                else _dp3AggregateNonSentinelCount++;
                if (isLargeDelta) _dp3AggregateLargeDeltaCount++;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Client]", $"{Label} DP-3 Prefix 异常（不阻断）: {ex.Message}");
            }
        }

        // ========================================================================
        // 对外查询接口
        // ========================================================================
        public static long TotalSendWriteCalls => _dp1CallCount;
        public static long TotalReceiveCalls => _dp2CallCount;
        public static long TotalTellStateCalls => _dp3CallCount;

        public static void ResetCounters()
        {
            _dp1CallCount = 0; _dp1VerboseLogged = 0; _dp1LastAggregateTime = 0;
            _dp1AggregateCalls = 0; _dp1AggregateCulledTotal = 0;
            _dp1AggregatePlayersToSendTotal = 0; _dp1AggregateUpdateCountTotal = 0;
            _dp1AggregateSentinelWritesTotal = 0;

            _dp2CallCount = 0; _dp2VerboseLogged = 0; _dp2LastAggregateTime = 0;
            _dp2AggregateCalls = 0; _dp2AggregateTotalCount = 0;

            _dp3CallCount = 0; _dp3VerboseLogged = 0; _dp3LastAggregateTime = 0;
            _dp3AggregateCalls = 0; _dp3AggregateSentinelCount = 0;
            _dp3AggregateNonSentinelCount = 0; _dp3AggregateLargeDeltaCount = 0;
        }
    }
}
