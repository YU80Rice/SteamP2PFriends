using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    ///
    /// 目标：追踪服务器->客机位置同步 RPC 调用，定位"客机端位置始终不变"根因。
    ///
    /// U3-SDK 源码：
    ///   - 文件：PlayerMovement.cs
    ///   - 方法：public void tellState(Vector3 newPosition, byte newPitch, byte newYaw) (L671)
    ///   - 语义：服务器告诉客机新位置（服务器->客机 RPC）
    ///
    /// 诊断目标：
    ///   - 验证客机端是否收到服务器 tellState 调用
    ///   - 若 tellState 未触发 -> 服务器未发送位置包
    ///   - 若 tellState 触发但位置不变 -> 服务器发送的位置本身不变
    ///
    /// 节流：1 条/秒/steamId（与 D-Vis-5 一致）
    ///
    /// 严格禁止：
    ///   - 修改原方法参数或返回值
    ///   - 修改 vanilla 位置同步
    /// </summary>
    public static class PlayerMovementTellStateDiagnosticPatch
    {
        public static bool DVis9_Registered { get; private set; }

        public static bool AllRegistrationsSucceeded => DVis9_Registered;

        // 节流状态：1 条/秒/steamId
        private static readonly Dictionary<ulong, float> _lastLogTime = new Dictionary<ulong, float>();
        private const float THROTTLE_SECONDS = 1.0f;

        // 位置变化检测：即使节流未到，位置变化也记录
        private static readonly Dictionary<ulong, Vector3> _lastPositions = new Dictionary<ulong, Vector3>();

        public static bool RegisterManual(Harmony harmony)
        {
            DVis9_Registered = RegisterDVis9(harmony);
            RoleLogger.Info("[Shared]",
                $"[D-Vis] PlayerMovementTellStateDiagnosticPatch 汇总: D-Vis-9={DVis9_Registered}");
            return AllRegistrationsSucceeded;
        }

        private static bool RegisterDVis9(Harmony harmony)
        {
            const string Label = "D-Vis-9 PlayerMovement.tellState";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(PlayerMovement), "tellState");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[D-Vis-9] !!! {Label} 反射失败");
                    return false;
                }
                MethodInfo prefix = typeof(Hooks).GetMethod(nameof(Hooks.TellStatePrefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, prefix: new HarmonyMethod(prefix));
                RoleLogger.Info("[Shared]", $"[D-Vis-9] OK {Label} 已登记 (Prefix)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[D-Vis-9] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        // ====================== Hooks ======================

        private static class Hooks
        {
            // 参数名（按 vanilla 签名匹配）：newPosition / newPitch / newYaw
            internal static void TellStatePrefix(PlayerMovement __instance, Vector3 newPosition, byte newPitch, byte newYaw)
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

                    // 节流：每 steamId 每秒最多 1 条
                    float now = Time.realtimeSinceStartup;
                    bool positionChanged = false;
                    if (_lastPositions.TryGetValue(steamId, out Vector3 lastPos))
                    {
                        float dx = newPosition.x - lastPos.x;
                        float dy = newPosition.y - lastPos.y;
                        float dz = newPosition.z - lastPos.z;
                        float sqDist = dx * dx + dy * dy + dz * dz;
                        positionChanged = sqDist > 0.01f; // 0.1m 阈值
                    }
                    else
                    {
                        positionChanged = true;
                    }
                    _lastPositions[steamId] = newPosition;

                    if (!positionChanged)
                    {
                        // 位置未变，仍按节流记录
                        if (_lastLogTime.TryGetValue(steamId, out float t) && now - t < THROTTLE_SECONDS) return;
                    }
                    _lastLogTime[steamId] = now;

                    string posStr = $"({newPosition.x:F2},{newPosition.y:F2},{newPosition.z:F2})";
                    RoleLogger.Info("[Shared]",
                        $"[D-Vis-9] PlayerMovement.tellState receiver={DiagnosticMaskUtil.MaskSteamId(steamId)} " +
                        $"isLocalPlayer={isLocalPlayer} pos={posStr} pitch={newPitch} yaw={newYaw} " +
                        $"posChanged={positionChanged}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Shared]", $"[D-Vis-9] tellState 异常（不阻断）: {ex.Message}");
                }
            }
        }

        // ====================== Helpers ======================

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
        /// </summary>
        public static void OnClientDisconnected()
        {
            try
            {
                _lastLogTime.Clear();
                _lastPositions.Clear();
            }
            catch { /* ignore */ }
        }
    }
}
