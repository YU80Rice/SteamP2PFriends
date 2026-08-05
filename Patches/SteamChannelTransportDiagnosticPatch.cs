using HarmonyLib;
using SDG.NetTransport;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.18 D-Vis-8 诊断 patch（客机模型可见性差异诊断 - 传输层）。
    ///
    /// D-Vis-8：SteamChannel.GetOwnerTransportConnection Postfix（双端，含节流）
    ///   - U3-SDK 路径：SteamChannel.cs:111
    ///   - 签名：public ITransportConnection GetOwnerTransportConnection()
    ///   - 审计 7.1.1 修正：SteamChannel 类只有 owner 字段（L93），没有 player 字段
    ///   - 节流：每 steamId 首次触发 + 每 10 秒采样一次
    ///   - 目的：直接验证 H2 假设（TransportConnection 类型差异）
    ///
    /// 严格禁止：
    ///   - 修改原方法参数或返回值
    ///   - 修改 vanilla 网络栈
    /// </summary>
    public static class SteamChannelTransportDiagnosticPatch
    {
        public static bool DVis8Registered { get; private set; }

        public static bool AllRegistrationsSucceeded => DVis8Registered;

        // 节流状态：每 steamId 首次触发 + 每 10 秒采样一次
        private static readonly Dictionary<ulong, float> _lastLogTime = new Dictionary<ulong, float>();
        private const float THROTTLE_SECONDS = 10.0f;

        public static bool RegisterManual(Harmony harmony)
        {
            DVis8Registered = RegisterDVis8(harmony);
            RoleLogger.Info("[Shared]",
                $"[D-Vis] SteamChannelTransportDiagnosticPatch 汇总: D-Vis-8={DVis8Registered}");
            return AllRegistrationsSucceeded;
        }

        private static bool RegisterDVis8(Harmony harmony)
        {
            const string Label = "D-Vis-8 SteamChannel.GetOwnerTransportConnection";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(SteamChannel), "GetOwnerTransportConnection");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[D-Vis-8] !!! {Label} 反射失败");
                    return false;
                }
                MethodInfo postfix = typeof(SteamChannelTransportHooks).GetMethod(nameof(SteamChannelTransportHooks.Postfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, postfix: new HarmonyMethod(postfix));
                RoleLogger.Info("[Shared]", $"[D-Vis-8] OK {Label} 已登记 (Postfix)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[D-Vis-8] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        // ====================== Hooks ======================

        private static class SteamChannelTransportHooks
        {
            // 审计 7.1.1 修正：SteamChannel 类只有 owner 字段（L93），没有 player 字段
            internal static void Postfix(SteamChannel __instance, ITransportConnection __result)
            {
                try
                {
                    if (!ShouldLogDVis()) return;
                    // SteamChannel.owner 是 SteamPlayer 类型
                    if (__instance?.owner == null) return;
                    SteamPlayer sp = __instance.owner;
                    ulong steamId = sp.playerID?.steamID.m_SteamID ?? 0UL;

                    // 节流：首次 + 每 10 秒采样
                    float now = Time.realtimeSinceStartup;
                    if (_lastLogTime.TryGetValue(steamId, out float t) && now - t < THROTTLE_SECONDS) return;
                    _lastLogTime[steamId] = now;

                    string typeName = __result?.GetType().FullName ?? "null";
                    RoleLogger.Info("[Shared]",
                        $"[D-Vis-8] SteamChannel.GetOwnerTransportConnection steamId={DiagnosticMaskUtil.MaskSteamId(steamId)} type={typeName}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Shared]", $"[D-Vis-8] 异常（不阻断）: {ex.Message}");
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
        /// v0.2.3.18 P1：客机断开时清除节流状态。
        /// </summary>
        public static void OnClientDisconnected()
        {
            try
            {
                _lastLogTime.Clear();
            }
            catch { /* ignore */ }
        }
    }
}
