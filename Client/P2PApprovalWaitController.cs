using SDG.Unturned;
using SteamP2PFriends.Shared;
using SteamP2PFriends.UI;
using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace SteamP2PFriends.Client
{
    // 标记连接发起来源：显式用户操作 vs 等待控制器自动重试
    internal enum P2PConnectOrigin
    {
        ExplicitUserAction = 0,
        ApprovalWaitRetry = 1
    }

    internal static class P2PApprovalWaitController
    {
        private const float RetrySeconds = 5f;
        private const float RateLimitCooldownSeconds = 30f;
        private const float ExpireSeconds = 120f;
        private const int MaxAttempts = 24;

        private static ulong _hostSteamId;
        private static float _expiresAt;
        private static float _nextRetryAt;
        private static int _attempts;
        private static bool _waiting;

        internal static bool _testBypassThreadAssert;
        internal static Func<float> _testTimeProvider;
        internal static bool? _testIsSafeToRetry;
        internal static Action<ulong> _testTryConnectCallback;
        internal static bool IsWaitingForTest => _waiting;
        internal static int AttemptsForTest => _attempts;
        internal static float ExpiresAtForTest => _expiresAt;
        internal static float NextRetryAtForTest => _nextRetryAt;
        internal static bool IsWaitUiVisibleForTest => P2PNativeMenuUI.IsWaitVisibleForTest;

        private static void AssertGameThread()
        {
            if (!_testBypassThreadAssert) ThreadUtil.assertIsGameThread();
        }

        private static float GetTime()
        {
            if (_testTimeProvider != null) return _testTimeProvider();
            try { return GetTimeFromUnity(); }
            catch { return 0f; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static float GetTimeFromUnity() { return Time.realtimeSinceStartup; }

        private static bool IsSafeToRetry =>
            _testIsSafeToRetry ?? P2PJoinManager.IsSafeToRetry;

        private static void TryConnect(ulong hostSteamId)
        {
            if (_testTryConnectCallback != null) _testTryConnectCallback(hostSteamId);
            else P2PJoinManager.TryConnectToHostFromApprovalWait(hostSteamId);
        }

        private static int RemainingSeconds(float now) =>
            Math.Max(0, (int)Math.Ceiling(_nextRetryAt - now));


        internal static void BeginAfterWhitelistRejected(ulong hostSteamId)
        {
            AssertGameThread();
            if (!P2PClientUiEnvironment.CanTouchClientUi() || hostSteamId == 0) return;

            // 同 host 的一次连接已再次被 WHITELISTED 拒绝：保持 attempts/expiresAt，
            // 但必须从“本次断开完成”重新计算 5 秒间隔，禁止过期时间点导致零间隔重连。
            if (_waiting && _hostSteamId == hostSteamId)
            {
                float rejectedAt = GetTime();
                _nextRetryAt = rejectedAt + RetrySeconds;
                P2PNativeMenuUI.EnsureApprovalWaitVisible(hostSteamId, RemainingSeconds(rejectedAt), Cancel);
                RoleLogger.Info("[Client]", "[P2P-Wait] WHITELISTED again; next retry in 5s without renewing budget");
                return;
            }

            // host 变更 -> 先完全收敛旧 UI/字段
            if (_waiting) StopWaitingCore();

            float now = GetTime();
            _hostSteamId = hostSteamId;
            _attempts = 0;
            _expiresAt = now + ExpireSeconds;
            _nextRetryAt = now + RetrySeconds;

            if (!P2PNativeMenuUI.EnsureApprovalWaitVisible(hostSteamId, RemainingSeconds(now), Cancel))
            {
                RoleLogger.Warn("[Client]", "[P2P-Wait] Begin: UI unavailable, not starting wait");
                _hostSteamId = 0;
                _attempts = 0;
                _expiresAt = 0f;
                _nextRetryAt = 0f;
                return; // fail-closed：不可见/不可取消 -> 不等待
            }

            _waiting = true;
            RoleLogger.Info("[Client]", "[P2P-Wait] Begin: host=" + hostSteamId + " 120s内最多24次(握手耗时计入总时限)");
        }


        internal static void NotifyConnectionAccepted()
        {
            AssertGameThread();
            if (_waiting) StopWaitingCore();
        }

        /// <summary>
        /// 路由自动重试连接的失败。返回 true 表示该失败已由等待控制器处理，
        /// 调用方不得再显示通用 SafeAlert。
        /// </summary>
        internal static bool HandleRetryFailure(ESteamConnectionFailureInfo failureInfo, ulong hostSteamId)
        {
            AssertGameThread();

            if (failureInfo == ESteamConnectionFailureInfo.WHITELISTED && hostSteamId != 0UL)
            {
                BeginAfterWhitelistRejected(hostSteamId);
                return true;
            }

            if (!_waiting || hostSteamId == 0UL || _hostSteamId != hostSteamId)
                return false;

            if (failureInfo == ESteamConnectionFailureInfo.CONNECT_RATE_LIMITING)
            {
                float now = GetTime();
                _nextRetryAt = Math.Max(_nextRetryAt, now + RateLimitCooldownSeconds);
                P2PNativeMenuUI.EnsureApprovalWaitVisible(_hostSteamId, RemainingSeconds(now), Cancel);
                RoleLogger.Warn("[Client]",
                    "[P2P-Wait] server rate-limited retry; cooling down 30s without renewing budget");
                return true;
            }

            // 认证、网络、房主退出等其他失败不属于“等待批准”语义，立即停止自动重试。
            StopWaitingCore();
            return false;
        }


        internal static void CancelForExplicitUserJoin()
        {
            AssertGameThread();
            if (_waiting) StopWaitingCore();
        }

        internal static void Tick()
        {
            if (!_waiting) return;
            AssertGameThread();
            float now = GetTime();
            if (now >= _expiresAt || _attempts >= MaxAttempts)
            {
                RoleLogger.Info("[Client]", "[P2P-Wait] 超时/上限停止: attempts=" + _attempts + " expired=" + (now >= _expiresAt));
                StopWaitingCore();
                return;
            }

            int remaining = RemainingSeconds(now);

            if (!P2PNativeMenuUI.EnsureApprovalWaitVisible(_hostSteamId, remaining, Cancel))
            {
                RoleLogger.Warn("[Client]", "[P2P-Wait] UI unavailable during Tick, stopping wait");
                StopWaitingCore();
                return;
            }

            if (now < _nextRetryAt || !IsSafeToRetry) return;
            ++_attempts;
            _nextRetryAt = now + RetrySeconds;
            RoleLogger.Info("[Client]", "[P2P-Wait] 重试 #" + _attempts + "/" + MaxAttempts + " host=" + _hostSteamId);
            TryConnect(_hostSteamId);
        }

        internal static void Cancel()
        {
            AssertGameThread();
            StopWaitingCore();
        }

        private static void StopWaitingCore()
        {
            _waiting = false;
            _hostSteamId = 0;
            _attempts = 0;
            _expiresAt = 0f;
            _nextRetryAt = 0f;
            P2PNativeMenuUI.HideApprovalWait();
        }
    }
}
