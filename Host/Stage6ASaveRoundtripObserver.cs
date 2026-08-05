using SDG.Unturned;
using SteamP2PFriends.Shared;
using System;

namespace SteamP2PFriends.Host
{
    // v0.2.3.40 Stage 6A-1（Codex 83rd §3.1）：保存观察器
    //   仅观察原版 shutdown 保存；绝不调用 SaveManager.save()，不操作玩家身份或文件系统。
    //   所有公开入口仅在 Unity/Unturned 游戏主线程调用（ThreadUtil.assertIsGameThread）。
    //   蓝图：Codex-Blueprint-Stage6A-P2P-U3DSParity-v1.3-20260801.md §3.1
    internal enum EStage6ASaveObservationState
    {
        Inactive,
        Hosted,
        AwaitingNativeSave,
        SaveObserved,
        NativeDisconnectFailed,
        Closed,
    }

    internal static class Stage6ASaveRoundtripObserver
    {
        private static readonly object _gate = new object();
        private static EStage6ASaveObservationState _state = EStage6ASaveObservationState.Inactive;
        private static string _sessionId;
        private static int _slot = -1;
        private static bool _subscribed;

        internal static void Begin(string sessionId, int cachedSlot)
        {
            ThreadUtil.assertIsGameThread();
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session id is required.", nameof(sessionId));
            if (cachedSlot < 0 || cachedSlot > 4)
                throw new ArgumentOutOfRangeException(nameof(cachedSlot));

            lock (_gate)
            {
                if (_state != EStage6ASaveObservationState.Inactive &&
                    _state != EStage6ASaveObservationState.Closed)
                    throw new InvalidOperationException("Previous Stage 6A observer was not closed.");

                _sessionId = sessionId;
                _slot = cachedSlot;
                _state = EStage6ASaveObservationState.Hosted;
                if (!_subscribed)
                {
                    SaveManager.onPostSave += OnNativePostSave;
                    _subscribed = true;
                }
            }
        }

        internal static bool ArmForNativeShutdown()
        {
            ThreadUtil.assertIsGameThread();
            lock (_gate)
            {
                if (_state != EStage6ASaveObservationState.Hosted ||
                    !Provider.isServer ||
                    !Level.isLoaded ||
                    Level.info == null ||
                    Level.info.type != ELevelType.SURVIVAL)
                    return false;

                _state = EStage6ASaveObservationState.AwaitingNativeSave;
                RoleLogger.Info("[Host]",
                    $"[Stage6A-Save] armed-native-shutdown session={_sessionId} slot={_slot} levelType={Level.info.type}");
                return true;
            }
        }

        private static void OnNativePostSave()
        {
            try
            {
                ThreadUtil.assertIsGameThread();
                lock (_gate)
                {
                    if (_state != EStage6ASaveObservationState.AwaitingNativeSave)
                        return;

                    _state = EStage6ASaveObservationState.SaveObserved;
                    RoleLogger.Info("[Host]",
                        $"[Stage6A-Save] observed-native-post-save session={_sessionId} slot={_slot}");
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    if (_state == EStage6ASaveObservationState.AwaitingNativeSave)
                        _state = EStage6ASaveObservationState.NativeDisconnectFailed;
                }
                RoleLogger.Error("[Host]", "[Stage6A-Save] observer failure: " + ex.GetType().Name);
            }
        }

        internal static void MarkNativeDisconnectFailed(Exception exception)
        {
            ThreadUtil.assertIsGameThread();
            if (exception == null) return;
            lock (_gate)
            {
                if (_state != EStage6ASaveObservationState.AwaitingNativeSave)
                    return;

                _state = EStage6ASaveObservationState.NativeDisconnectFailed;
                RoleLogger.Error("[Host]",
                    $"[Stage6A-Save] native-disconnect-failed session={_sessionId} error={exception.GetType().Name}");
            }
        }

        internal static void Complete(string reason)
        {
            ThreadUtil.assertIsGameThread();
            if (string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException("Reason is required.", nameof(reason));

            lock (_gate)
            {
                RoleLogger.Info("[Host]",
                    $"[Stage6A-Save] close session={_sessionId ?? "none"} state={_state} reason={reason}");
                if (_subscribed)
                {
                    SaveManager.onPostSave -= OnNativePostSave;
                    _subscribed = false;
                }
                _sessionId = null;
                _slot = -1;
                _state = EStage6ASaveObservationState.Closed;
            }
        }
    }
}
