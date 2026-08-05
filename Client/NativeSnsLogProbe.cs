using SteamP2PFriends.Shared;
using Steamworks;
using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;

namespace SteamP2PFriends.Client
{
    /// <summary>
    /// v0.2.3.6 P0-5/P1：受控原生 SNS 调试输出（Codex 第六次审计返修）。
    ///
    /// v0.2.3.6 修订（审计 v0.2.3.5 验收报告 P0-A/Medium-2/P1）：
    ///   - 原生 SNS 回调可能在 Steam 内部线程触发；v0.2.3.5 直接在回调内执行正则 + 写日志，
    ///     无线程隔离/限流/丢弃计数。v0.2.3.6 改为有界队列 + 主线程 Update 批量脱敏落盘。
    ///   - 队列容量上限：MaxQueueEntries=1024；单帧 drain 上限：MaxDrainPerFrame=64。
    ///   - 队列满时丢弃新条目并累加 dropped 计数；每 10s 输出一次 dropped 总数。
    ///   - 脱敏入口统一为 SnsDiagnosticUtil.RedactSensitiveNetworkData（P0-A 共享入口）。
    ///   - 新增 IsEnabled/EnableFailed 公开属性供 P0-C 启动自检阻断门使用。
    ///
    /// 设计目标（审计第五次审计 P0-5）：
    ///   - 通过 SteamNetworkingUtils.SetDebugOutputFunction 注册 C# 委托，接收 SNS 原生日志回调。
    ///   - 仅诊断构建/受控测试启用，由配置开关控制。
    ///   - 不修改 ICE/SDR/认证配置。
    ///
    /// 严格禁止：
    ///   - 在正式服或公开服启用
    ///   - 落盘未脱敏的 IP/候选地址
    ///   - 修改任何 SNS 状态
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

        /// <summary>Enable() 是否曾抛非"Steamworks 未初始化"异常（用于 P0-C 阻断门）。</summary>
        public static bool EnableFailed => _enableFailed;

        /// <summary>v0.2.3.9 新增：是否正在等待 Steamworks 初始化后重试 Enable。</summary>
        public static bool WaitingForSteamworks => _waitingForSteamworks;

        /// <summary>
        /// 启用受控 SNS 原生日志。仅在 RouteDiagnostics && VerboseLog 同时为 true 时启用。
        /// v0.2.3.9 修复：BepInEx 插件 Awake 时 Steamworks 可能尚未初始化（SteamAPI.Init 由
        ///   vanilla Provider.Initialize 在场景加载时调用）。若 SetDebugOutputFunction 抛
        ///   "Steamworks is not initialized" 异常，不视为永久失败，而是标记 _waitingForSteamworks=true，
        ///   由 Plugin.Update 调用 RetryEnableIfSteamworksReady() 在 Steamworks 初始化后重试。
        /// </summary>
        public static void Enable(bool routeDiagnostics, bool verboseLog)
        {
            if (!routeDiagnostics || !verboseLog)
            {
                RoleLogger.Info("[Shared]",
                    $"[Diag] [D-NativeSns] NativeSnsLogProbe NOT enabled (routeDiagnostics={routeDiagnostics} verboseLog={verboseLog})");
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
                // v0.2.3.9 修复：检测 "Steamworks is not initialized" 异常，标记为等待重试
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
        /// v0.2.3.9 新增：由 Plugin.Update 调用，在 Steamworks 初始化后重试 Enable。
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
        /// v0.2.3.6 P1：回调内只做最小复制 + 入队；脱敏/落盘由主线程 Tick 负责。
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
