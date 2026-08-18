using SteamP2PFriends.Shared;
using Steamworks;
using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;

namespace SteamP2PFriends.Client
{
    /// <summary>
    /// 可选的原生 Steam Networking Sockets 诊断输出。
    /// 回调线程只做有界入队；主线程负责脱敏和写日志。该探针从不改变网络配置或 P2P 功能门控。
    /// </summary>
    public static class NativeSnsLogProbe
    {
        private const int MaxQueueEntries = 1024;
        private const int MaxDrainPerFrame = 64;
        private const float DroppedReportIntervalSeconds = 10f;

        private static bool _enabled;
        private static bool _enableFailed;
        private static bool _waitingForSteamworks;
        private static float _lastRetryTime;
        private const float RetryIntervalSeconds = 1f;
        private const int MaxRetryAttempts = 60; // 最多重试 60 次（约 60 秒）
        private static int _retryAttempts;
        private static FSteamNetworkingSocketsDebugOutput _delegate;

        private static readonly ConcurrentQueue<NativeLogEntry> _queue = new ConcurrentQueue<NativeLogEntry>();
        private static int _droppedCount;
        private static int _drainedTotal;
        private static float _lastDroppedReportTime;

        /// <summary>当前是否已成功启用原生 SNS 日志回调。</summary>
        public static bool IsEnabled => _enabled;

        /// <summary>Enable() 是否曾抛出非"Steamworks 未初始化"异常。</summary>
        public static bool EnableFailed => _enableFailed;

        public static bool WaitingForSteamworks => _waitingForSteamworks;

        /// <summary>
        /// 启用受控 SNS 原生日志。仅在 RouteDiagnostics && VerboseLog 同时为 true 时启用。
        ///   vanilla Provider.Initialize 在场景加载时调用）。若 SetDebugOutputFunction 抛
        ///   "Steamworks is not initialized" 异常，不视为永久失败，而是标记 _waitingForSteamworks=true，
        ///   由 Plugin.Update 调用 RetryEnableIfSteamworksReady() 在 Steamworks 初始化后重试。
        /// </summary>
        public static void Enable(bool routeDiagnostics, bool verboseLog)
        {
            if (!routeDiagnostics || !verboseLog)
            {
                return;
            }

            try
            {
                if (_enabled)
                {
                    RoleLogger.Info("[Shared]", "[Diag] [D-NativeSns] 已启用，跳过重复 Enable");
                    return;
                }

                _delegate = new FSteamNetworkingSocketsDebugOutput(OnNativeSnsDebugOutput);
                SteamNetworkingUtils.SetDebugOutputFunction(
                    ESteamNetworkingSocketsDebugOutputType.k_ESteamNetworkingSocketsDebugOutputType_Msg,
                    _delegate);
                _enabled = true;
                _enableFailed = false;
                _waitingForSteamworks = false;
                RoleLogger.Info("[Shared]",
                    "[Diag] [D-NativeSns] NativeSnsLogProbe ENABLED (level=Msg, queue cap=" +
                    MaxQueueEntries + ", drain/frame cap=" + MaxDrainPerFrame +
                    ", IP/IPv6/hostname:port/STUN-TURN/ICE-SDP/ticket-cert-PEM 内容将脱敏)");
            }
            catch (Exception ex)
            {
                //   BepInEx 插件 Awake 时 Steamworks 可能尚未初始化（SteamAPI.Init 由 vanilla
                //   Provider.Initialize 在场景加载时调用）。此时不视为永久失败，由 Plugin.Update
                //   在 Steamworks 初始化后重试 Enable。
                string msg = ex.Message ?? "";
                bool isSteamworksNotInit = msg.IndexOf("Steamworks is not initialized", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("not initialized", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isSteamworksNotInit)
                {
                    _waitingForSteamworks = true;
                    _enableFailed = false; // 不视为永久失败
                    _retryAttempts = 0;
                    _lastRetryTime = Time.realtimeSinceStartup;
                    RoleLogger.Info("[Shared]",
                        $"[Diag] [D-NativeSns] NativeSnsLogProbe 等待 Steamworks 初始化后重试 (exception: {msg})。 " +
                        "Plugin.Update 会在 Steamworks 就绪后自动重试 Enable。");
                }
                else
                {
                    _enableFailed = true;
                    RoleLogger.Warn("[Shared]", $"[Diag] [D-NativeSns] Enable 异常（不阻断 Update，但 P0-C 自检会拦截）: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 仅当 _waitingForSteamworks=true 时尝试重试。重试成功后 _enabled=true。
        /// 超过 MaxRetryAttempts 次仍未成功时设 _enableFailed=true（永久失败）。
        /// </summary>
        public static void RetryEnableIfSteamworksReady()
        {
            if (!_waitingForSteamworks || _enabled || _enableFailed) return;

            float now = Time.realtimeSinceStartup;
            if (now - _lastRetryTime < RetryIntervalSeconds) return;

            _lastRetryTime = now;
            _retryAttempts++;

            try
            {
                _delegate = new FSteamNetworkingSocketsDebugOutput(OnNativeSnsDebugOutput);
                SteamNetworkingUtils.SetDebugOutputFunction(
                    ESteamNetworkingSocketsDebugOutputType.k_ESteamNetworkingSocketsDebugOutputType_Msg,
                    _delegate);
                _enabled = true;
                _waitingForSteamworks = false;
                _enableFailed = false;
                RoleLogger.Info("[Shared]",
                    $"[Diag] [D-NativeSns] NativeSnsLogProbe ENABLED (retry success, attempts={_retryAttempts}, " +
                    "level=Msg, queue cap=" + MaxQueueEntries + ", drain/frame cap=" + MaxDrainPerFrame +
                    ", IP/IPv6/hostname:port/STUN-TURN/ICE-SDP/ticket-cert-PEM 内容将脱敏)");
            }
            catch (Exception ex)
            {
                string msg = ex.Message ?? "";
                bool isSteamworksNotInit = msg.IndexOf("Steamworks is not initialized", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("not initialized", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isSteamworksNotInit)
                {
                    if (_retryAttempts >= MaxRetryAttempts)
                    {
                        // 超过最大重试次数，视为永久失败
                        _enableFailed = true;
                        _waitingForSteamworks = false;
                        RoleLogger.Warn("[Shared]",
                            $"[Diag] [D-NativeSns] NativeSnsLogProbe 重试 {MaxRetryAttempts} 次后仍未启用（Steamworks 长时间未初始化），" +
                            "P0-C 阻断门将生效。请检查 Steam 客户端是否运行。");
                    }
                    else
                    {
                        RoleLogger.Info("[Shared]",
                            $"[Diag] [D-NativeSns] NativeSnsLogProbe 重试 {_retryAttempts}/{MaxRetryAttempts}：" +
                            $"Steamworks 仍未初始化，{RetryIntervalSeconds:F0}s 后再次重试");
                    }
                }
                else
                {
                    _enableFailed = true;
                    _waitingForSteamworks = false;
                    RoleLogger.Warn("[Shared]",
                        $"[Diag] [D-NativeSns] RetryEnable 异常（非 Steamworks 未初始化，视为永久失败）: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 原生 SNS 调试输出回调（可能在 Steam 内部线程触发）。
        /// </summary>
        private static void OnNativeSnsDebugOutput(ESteamNetworkingSocketsDebugOutputType nType, IntPtr pszMsg)
        {
            try
            {
                if (pszMsg == IntPtr.Zero) return;
                // .NET Framework 4.7.2 不支持 Marshal.PtrToStringUTF8，使用 PtrToStringAnsi
                // SNS 原生日志以 ASCII 为主，PtrToStringAnsi 已足够。
                string msg = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(pszMsg);
                if (string.IsNullOrEmpty(msg)) return;

                // 入队（ bounded）；满时丢弃并累加计数（Interlocked 原子）
                int currentCount = _queue.Count;
                if (currentCount >= MaxQueueEntries)
                {
                    Interlocked.Increment(ref _droppedCount);
                    return;
                }

                _queue.Enqueue(new NativeLogEntry
                {
                    Type = nType,
                    Message = msg
                });

                // 二次检查：Enqueue 后若超过容量，从尾部丢弃不太可行（ConcurrentQueue 只有 Dequeue），
                // 这里允许短暂超容，由后续 Dequeue 自然回收。最坏情况：多入队 1-2 条，不影响安全。
            }
            catch
            {
                // 回调内部异常绝不能抛出，否则会破坏 SNS 内部线程
                Interlocked.Increment(ref _droppedCount);
            }
        }

        /// <summary>
        /// 主线程 Update 每帧调用：drain 队列，脱敏后落盘。
        /// 单帧最多处理 MaxDrainPerFrame 条，避免单帧卡顿。
        /// 每 10s 输出一次 dropped 总数。
        /// </summary>
        public static void Tick()
        {
            if (!_enabled) return;

            try
            {
                int processed = 0;
                while (processed < MaxDrainPerFrame && _queue.TryDequeue(out NativeLogEntry entry))
                {
                    EmitEntry(entry);
                    processed++;
                    _drainedTotal++;
                }

                // 每 10s 报告一次 dropped 总数
                float now = Time.realtimeSinceStartup;
                if (now - _lastDroppedReportTime >= DroppedReportIntervalSeconds)
                {
                    _lastDroppedReportTime = now;
                    int dropped = Interlocked.CompareExchange(ref _droppedCount, 0, 0);
                    if (dropped > 0)
                    {
                        RoleLogger.Warn("[Shared]",
                            $"[Diag] [D-NativeSns] Dropped entries in last {DroppedReportIntervalSeconds:F0}s: {dropped} " +
                            $"(queue cap={MaxQueueEntries}, drainedTotal={_drainedTotal})");
                    }
                }
            }
            catch (Exception ex)
            {
                // 主线程异常不应阻断 Update
                RoleLogger.Warn("[Shared]", $"[Diag] [D-NativeSns] Tick 异常（不阻断）: {ex.Message}");
            }
        }

        private static void EmitEntry(NativeLogEntry entry)
        {
            try
            {
                // 脱敏入口：与 SnsDiagnosticUtil 共享同一套
                string redacted = SnsDiagnosticUtil.RedactSensitiveNetworkData(entry.Message);

                string level = entry.Type.ToString();
                if (level.StartsWith("k_ESteamNetworkingSocketsDebugOutputType_"))
                {
                    level = level.Substring("k_ESteamNetworkingSocketsDebugOutputType_".Length);
                }

                if (entry.Type == ESteamNetworkingSocketsDebugOutputType.k_ESteamNetworkingSocketsDebugOutputType_Bug
                    || entry.Type == ESteamNetworkingSocketsDebugOutputType.k_ESteamNetworkingSocketsDebugOutputType_Error)
                {
                    RoleLogger.Error("[Shared]", $"[Diag] [D-NativeSns] [{level}] {redacted}");
                }
                else if (entry.Type == ESteamNetworkingSocketsDebugOutputType.k_ESteamNetworkingSocketsDebugOutputType_Important
                    || entry.Type == ESteamNetworkingSocketsDebugOutputType.k_ESteamNetworkingSocketsDebugOutputType_Warning)
                {
                    RoleLogger.Warn("[Shared]", $"[Diag] [D-NativeSns] [{level}] {redacted}");
                }
                else
                {
                    RoleLogger.Info("[Shared]", $"[Diag] [D-NativeSns] [{level}] {redacted}");
                }
            }
            catch
            {
                // 单条 emit 异常不影响后续
            }
        }

        /// <summary>禁用原生 SNS 日志回调（应用退出时调用）。</summary>
        public static void Disable()
        {
            try
            {
                if (!_enabled) return;
                SteamNetworkingUtils.SetDebugOutputFunction(
                    ESteamNetworkingSocketsDebugOutputType.k_ESteamNetworkingSocketsDebugOutputType_None,
                    null);
                _enabled = false;
                _delegate = null;

                // 排空残留队列
                while (_queue.TryDequeue(out _)) { }

                RoleLogger.Info("[Shared]",
                    $"[Diag] [D-NativeSns] NativeSnsLogProbe DISABLED (drainedTotal={_drainedTotal}, " +
                    $"droppedTotal={Interlocked.CompareExchange(ref _droppedCount, 0, 0)})");
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Shared]", $"[Diag] [D-NativeSns] Disable 异常（不阻断）: {ex.Message}");
            }
        }

        private struct NativeLogEntry
        {
            public ESteamNetworkingSocketsDebugOutputType Type;
            public string Message;
        }
    }
}
