using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.20 D-Vis-15 诊断 patch（PlayerMovement.tellState 调用方追踪）。
    ///
    /// 审计授权：外部审计报告-第十三次双机测试-v0.2.3.19-修订版验收 §9.2 授权实施。
    ///
    /// 目标（审计要求）：
    ///   - Postfix PlayerMovement.tellState，记录 new StackTrace().ToString() 前 5 帧
    ///   - 定位是谁在调用 tellState（或确认根本没人调用）
    ///   - 验证议题 A/B 独立性：
    ///     * 若 0 triggers -> 支持假设 H1（主机未调用 tellState 给客机，议题 B 降级为 R2 下游症状）
    ///     * 若有 triggers -> 支持假设 H2（主机调用了但通过其他 API，议题 B 独立）
    ///
    /// U3-SDK 源码：
    ///   - 文件：PlayerMovement.cs
    ///   - 方法：public void tellState(Vector3 newPosition, byte newPitch, byte newYaw) (L671)
    ///   - 语义：服务器告诉客机新位置（[SteamCall] RPC，由网络层接收时调用）
    ///
    /// 与 D-Vis-9 的关系：
    ///   - D-Vis-9 是 Prefix，记录进入时的 receiver/位置/姿态信息
    ///   - D-Vis-15 是 Postfix，专门记录调用栈前 5 帧
    ///   - 两个 patch 共存于同一 original 方法，互不干扰
    ///
    /// 节流：1 条/秒/steamId（与 D-Vis-9 一致），避免高频调用栈采集影响性能
    ///
    /// 严格禁止：
    ///   - 修改原方法参数或返回值
    ///   - 修改 vanilla 位置同步
    ///   - 在 StackTrace 采集中抛异常影响原方法
    /// </summary>
    public static class PlayerMovementTellStateCallerDiagnosticPatch
    {
        public static bool DVis15_Registered { get; private set; }

        public static bool AllRegistrationsSucceeded => DVis15_Registered;

        // 节流状态：1 条/秒/steamId（与 D-Vis-9 一致）
        private static readonly Dictionary<ulong, float> _lastTraceTime = new Dictionary<ulong, float>();
        private const float THROTTLE_SECONDS = 1.0f;

        public static bool RegisterManual(Harmony harmony)
        {
            DVis15_Registered = RegisterDVis15(harmony);
            RoleLogger.Info("[Shared]",
                $"[D-Vis] PlayerMovementTellStateCallerDiagnosticPatch 汇总: D-Vis-15={DVis15_Registered}");
            return AllRegistrationsSucceeded;
        }

        private static bool RegisterDVis15(Harmony harmony)
        {
            const string Label = "D-Vis-15 PlayerMovement.tellState Caller Trace";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(PlayerMovement), "tellState");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[D-Vis-15] !!! {Label} 反射失败");
                    return false;
                }
                MethodInfo postfix = typeof(Hooks).GetMethod(nameof(Hooks.TellStateCallerPostfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, postfix: new HarmonyMethod(postfix));

                // 验证与 D-Vis-9 共存
                HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
                int prefixCount = info?.Prefixes?.Count ?? 0;
                int postfixCount = info?.Postfixes?.Count ?? 0;
                RoleLogger.Info("[Shared]",
                    $"[D-Vis-15] OK {Label} 已登记 (Postfix)。当前 tellState patches: " +
                    $"prefixes={prefixCount}(含 D-Vis-9), postfixes={postfixCount}(含 D-Vis-15)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[D-Vis-15] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        // ====================== Hooks ======================

        private static class Hooks
        {
            // 参数名（按 vanilla 签名匹配）：newPosition / newPitch / newYaw
            internal static void TellStateCallerPostfix(PlayerMovement __instance, Vector3 newPosition, byte newPitch, byte newYaw)
            {
                try
                {
                    if (!ShouldLogDVis()) return;

                    Player player = __instance?.player;
                    if (player == null) return;
                    SteamPlayer sp = player.channel?.owner;
                    if (sp == null) return;
                    ulong steamId = sp.playerID?.steamID.m_SteamID ?? 0UL;
                    bool isLocalPlayer = player.channel?.IsLocalPlayer ?? false;

                    // 节流：每 steamId 每秒最多 1 条调用栈
                    float now = Time.realtimeSinceStartup;
                    if (_lastTraceTime.TryGetValue(steamId, out float t) && now - t < THROTTLE_SECONDS) return;
                    _lastTraceTime[steamId] = now;

                    // 采集调用栈前 5 帧（跳过当前 Postfix 本身）
                    string stackTrace = GetTopFrames(5);

                    string posStr = $"({newPosition.x:F2},{newPosition.y:F2},{newPosition.z:F2})";
                    RoleLogger.Info("[Shared]",
                        $"[D-Vis-15] PlayerMovement.tellState CALLER receiver={DiagnosticMaskUtil.MaskSteamId(steamId)} " +
                        $"isLocalPlayer={isLocalPlayer} pos={posStr} pitch={newPitch} yaw={newYaw}");
                    RoleLogger.Info("[Shared]",
                        $"[D-Vis-15]   stack={stackTrace}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Shared]", $"[D-Vis-15] 调用方追踪异常（不阻断）: {ex.Message}");
                }
            }
        }

        // ====================== Helpers ======================

        /// <summary>
        /// 采集调用栈前 N 帧，格式化为单行字符串（便于日志检索）。
        /// 跳过当前 Postfix 方法本身（GetTopFrames + TellStateCallerPostfix）。
        /// </summary>
        private static string GetTopFrames(int frameCount)
        {
            try
            {
                var st = new StackTrace(2, false); // 跳过本方法 + Postfix 调用者
                var frames = st.GetFrames();
                if (frames == null || frames.Length == 0) return "<no-frames>";

                var parts = new List<string>(frameCount);
                int count = System.Math.Min(frameCount, frames.Length);
                for (int i = 0; i < count; i++)
                {
                    var f = frames[i];
                    var mb = f.GetMethod();
                    if (mb == null)
                    {
                        parts.Add($"#{i}=<unknown>");
                        continue;
                    }
                    string declType = mb.DeclaringType?.Name ?? "<unknown-type>";
                    string methodName = mb.Name;
                    parts.Add($"#{i}={declType}.{methodName}");
                }
                return string.Join(" | ", parts);
            }
            catch (System.Exception ex)
            {
                return $"<stack-trace-error: {ex.Message}>";
            }
        }

        private static bool ShouldLogDVis()
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

        /// <summary>
        /// v0.2.3.20 P1：客机断开时清除节流状态。
        /// </summary>
        public static void OnClientDisconnected()
        {
            try
            {
                _lastTraceTime.Clear();
            }
            catch { /* ignore */ }
        }
    }
}
