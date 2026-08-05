using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.18 D-Vis-5 诊断 patch（客机模型可见性差异诊断 - 传输层）。
    /// v0.2.3.19 扩展：覆盖所有 9 个 SteamChannel.send 重载。
    ///
    /// 9 个 send 重载（U3-SDK 源码验证）：
    ///   L494:  send(string name, CSteamID steamID, ESteamPacket type, params object[] arguments) - 单播
    ///   L528:  send(ESteamCall mode, byte bound, ESteamPacket type, int size, byte[] packet) - 区域广播(size)
    ///   L636:  send(string name, ESteamCall mode, byte bound, ESteamPacket type, params object[] arguments)
    ///   L653:  send(ESteamCall mode, byte x, byte y, byte area, ESteamPacket type, int size, byte[] packet)
    ///   L761:  send(string name, ESteamCall mode, byte x, byte y, byte area, ESteamPacket type, params object[] arguments)
    ///   L778:  send(ESteamCall mode, ESteamPacket type, int size, byte[] packet) - 全广播(size)
    ///   L893:  send(string name, ESteamCall mode, ESteamPacket type, params object[] arguments) - 全广播
    ///   L933:  send(ESteamCall mode, Vector3 point, float radius, ESteamPacket type, int size, byte[] packet)
    ///   L1043: send(string name, ESteamCall mode, Vector3 point, float radius, ESteamPacket type, params object[] arguments)
    ///
    /// 诊断目标：
    ///   - 定位客机端输入发送走哪个重载
    ///   - 捕获发送频率与参数
    ///
    /// 节流：1 条/秒/调用方（按目标 CSteamID 或 ESteamCall 去重）
    ///
    /// 严格禁止：
    ///   - 修改原方法参数或返回值
    ///   - 修改 vanilla 网络栈
    /// </summary>
    public static class SteamChannelSendDiagnosticPatch
    {
        public static bool DVis5_L494_Registered { get; private set; }
        public static bool DVis5_L528_Registered { get; private set; }
        public static bool DVis5_L636_Registered { get; private set; }
        public static bool DVis5_L653_Registered { get; private set; }
        public static bool DVis5_L761_Registered { get; private set; }
        public static bool DVis5_L778_Registered { get; private set; }
        public static bool DVis5_L893_Registered { get; private set; }
        public static bool DVis5_L933_Registered { get; private set; }
        public static bool DVis5_L1043_Registered { get; private set; }

        public static bool AllRegistrationsSucceeded =>
            DVis5_L494_Registered && DVis5_L528_Registered && DVis5_L636_Registered
            && DVis5_L653_Registered && DVis5_L761_Registered && DVis5_L778_Registered
            && DVis5_L893_Registered && DVis5_L933_Registered && DVis5_L1043_Registered;

        // 节流状态：1 条/秒/调用方（按 key 去重，key=目标 CSteamID 或 ESteamCall.GetHashCode）
        private static readonly Dictionary<ulong, float> _lastLogTime = new Dictionary<ulong, float>();
        private const float THROTTLE_SECONDS = 1.0f;

        public static bool RegisterManual(Harmony harmony)
        {
            DVis5_L494_Registered = RegisterDVis5_L494(harmony);
            DVis5_L528_Registered = RegisterDVis5_L528(harmony);
            DVis5_L636_Registered = RegisterDVis5_L636(harmony);
            DVis5_L653_Registered = RegisterDVis5_L653(harmony);
            DVis5_L761_Registered = RegisterDVis5_L761(harmony);
            DVis5_L778_Registered = RegisterDVis5_L778(harmony);
            DVis5_L893_Registered = RegisterDVis5_L893(harmony);
            DVis5_L933_Registered = RegisterDVis5_L933(harmony);
            DVis5_L1043_Registered = RegisterDVis5_L1043(harmony);

            RoleLogger.Info("[Shared]",
                $"[D-Vis] SteamChannelSendDiagnosticPatch 汇总: " +
                $"L494={DVis5_L494_Registered} L528={DVis5_L528_Registered} " +
                $"L636={DVis5_L636_Registered} L653={DVis5_L653_Registered} " +
                $"L761={DVis5_L761_Registered} L778={DVis5_L778_Registered} " +
                $"L893={DVis5_L893_Registered} L933={DVis5_L933_Registered} " +
                $"L1043={DVis5_L1043_Registered}");
            return AllRegistrationsSucceeded;
        }

        // ---------- L494: send(string, CSteamID, ESteamPacket, object[]) ----------
        private static bool RegisterDVis5_L494(Harmony harmony)
        {
            const string Label = "D-Vis-5 L494 SteamChannel.send(name,steamID,type,args)";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(SteamChannel), "send",
                    new[] { typeof(string), typeof(Steamworks.CSteamID), typeof(ESteamPacket), typeof(object[]) });
                if (original == null) { RoleLogger.Error("[Shared]", $"[D-Vis-5] !!! {Label} 反射失败"); return false; }
                MethodInfo prefix = typeof(Hooks).GetMethod(nameof(Hooks.L494Prefix), BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, prefix: new HarmonyMethod(prefix));
                RoleLogger.Info("[Shared]", $"[D-Vis-5] OK {Label} 已登记");
                return true;
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[D-Vis-5] !!! {Label} 异常: {ex.Message}"); return false; }
        }

        // ---------- L528: send(ESteamCall, byte, ESteamPacket, int, byte[]) ----------
        private static bool RegisterDVis5_L528(Harmony harmony)
        {
            const string Label = "D-Vis-5 L528 SteamChannel.send(mode,bound,type,size,packet)";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(SteamChannel), "send",
                    new[] { typeof(ESteamCall), typeof(byte), typeof(ESteamPacket), typeof(int), typeof(byte[]) });
                if (original == null) { RoleLogger.Error("[Shared]", $"[D-Vis-5] !!! {Label} 反射失败"); return false; }
                MethodInfo prefix = typeof(Hooks).GetMethod(nameof(Hooks.L528Prefix), BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, prefix: new HarmonyMethod(prefix));
                RoleLogger.Info("[Shared]", $"[D-Vis-5] OK {Label} 已登记");
                return true;
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[D-Vis-5] !!! {Label} 异常: {ex.Message}"); return false; }
        }

        // ---------- L636: send(string, ESteamCall, byte, ESteamPacket, object[]) ----------
        private static bool RegisterDVis5_L636(Harmony harmony)
        {
            const string Label = "D-Vis-5 L636 SteamChannel.send(name,mode,bound,type,args)";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(SteamChannel), "send",
                    new[] { typeof(string), typeof(ESteamCall), typeof(byte), typeof(ESteamPacket), typeof(object[]) });
                if (original == null) { RoleLogger.Error("[Shared]", $"[D-Vis-5] !!! {Label} 反射失败"); return false; }
                MethodInfo prefix = typeof(Hooks).GetMethod(nameof(Hooks.L636Prefix), BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, prefix: new HarmonyMethod(prefix));
                RoleLogger.Info("[Shared]", $"[D-Vis-5] OK {Label} 已登记");
                return true;
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[D-Vis-5] !!! {Label} 异常: {ex.Message}"); return false; }
        }

        // ---------- L653: send(ESteamCall, byte, byte, byte, ESteamPacket, int, byte[]) ----------
        private static bool RegisterDVis5_L653(Harmony harmony)
        {
            const string Label = "D-Vis-5 L653 SteamChannel.send(mode,x,y,area,type,size,packet)";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(SteamChannel), "send",
                    new[] { typeof(ESteamCall), typeof(byte), typeof(byte), typeof(byte), typeof(ESteamPacket), typeof(int), typeof(byte[]) });
                if (original == null) { RoleLogger.Error("[Shared]", $"[D-Vis-5] !!! {Label} 反射失败"); return false; }
                MethodInfo prefix = typeof(Hooks).GetMethod(nameof(Hooks.L653Prefix), BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, prefix: new HarmonyMethod(prefix));
                RoleLogger.Info("[Shared]", $"[D-Vis-5] OK {Label} 已登记");
                return true;
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[D-Vis-5] !!! {Label} 异常: {ex.Message}"); return false; }
        }

        // ---------- L761: send(string, ESteamCall, byte, byte, byte, ESteamPacket, object[]) ----------
        private static bool RegisterDVis5_L761(Harmony harmony)
        {
            const string Label = "D-Vis-5 L761 SteamChannel.send(name,mode,x,y,area,type,args)";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(SteamChannel), "send",
                    new[] { typeof(string), typeof(ESteamCall), typeof(byte), typeof(byte), typeof(byte), typeof(ESteamPacket), typeof(object[]) });
                if (original == null) { RoleLogger.Error("[Shared]", $"[D-Vis-5] !!! {Label} 反射失败"); return false; }
                MethodInfo prefix = typeof(Hooks).GetMethod(nameof(Hooks.L761Prefix), BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, prefix: new HarmonyMethod(prefix));
                RoleLogger.Info("[Shared]", $"[D-Vis-5] OK {Label} 已登记");
                return true;
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[D-Vis-5] !!! {Label} 异常: {ex.Message}"); return false; }
        }

        // ---------- L778: send(ESteamCall, ESteamPacket, int, byte[]) ----------
        private static bool RegisterDVis5_L778(Harmony harmony)
        {
            const string Label = "D-Vis-5 L778 SteamChannel.send(mode,type,size,packet)";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(SteamChannel), "send",
                    new[] { typeof(ESteamCall), typeof(ESteamPacket), typeof(int), typeof(byte[]) });
                if (original == null) { RoleLogger.Error("[Shared]", $"[D-Vis-5] !!! {Label} 反射失败"); return false; }
                MethodInfo prefix = typeof(Hooks).GetMethod(nameof(Hooks.L778Prefix), BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, prefix: new HarmonyMethod(prefix));
                RoleLogger.Info("[Shared]", $"[D-Vis-5] OK {Label} 已登记");
                return true;
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[D-Vis-5] !!! {Label} 异常: {ex.Message}"); return false; }
        }

        // ---------- L893: send(string, ESteamCall, ESteamPacket, object[]) ----------
        private static bool RegisterDVis5_L893(Harmony harmony)
        {
            const string Label = "D-Vis-5 L893 SteamChannel.send(name,mode,type,args)";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(SteamChannel), "send",
                    new[] { typeof(string), typeof(ESteamCall), typeof(ESteamPacket), typeof(object[]) });
                if (original == null) { RoleLogger.Error("[Shared]", $"[D-Vis-5] !!! {Label} 反射失败"); return false; }
                MethodInfo prefix = typeof(Hooks).GetMethod(nameof(Hooks.L893Prefix), BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, prefix: new HarmonyMethod(prefix));
                RoleLogger.Info("[Shared]", $"[D-Vis-5] OK {Label} 已登记");
                return true;
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[D-Vis-5] !!! {Label} 异常: {ex.Message}"); return false; }
        }

        // ---------- L933: send(ESteamCall, Vector3, float, ESteamPacket, int, byte[]) ----------
        private static bool RegisterDVis5_L933(Harmony harmony)
        {
            const string Label = "D-Vis-5 L933 SteamChannel.send(mode,point,radius,type,size,packet)";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(SteamChannel), "send",
                    new[] { typeof(ESteamCall), typeof(Vector3), typeof(float), typeof(ESteamPacket), typeof(int), typeof(byte[]) });
                if (original == null) { RoleLogger.Error("[Shared]", $"[D-Vis-5] !!! {Label} 反射失败"); return false; }
                MethodInfo prefix = typeof(Hooks).GetMethod(nameof(Hooks.L933Prefix), BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, prefix: new HarmonyMethod(prefix));
                RoleLogger.Info("[Shared]", $"[D-Vis-5] OK {Label} 已登记");
                return true;
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[D-Vis-5] !!! {Label} 异常: {ex.Message}"); return false; }
        }

        // ---------- L1043: send(string, ESteamCall, Vector3, float, ESteamPacket, object[]) ----------
        private static bool RegisterDVis5_L1043(Harmony harmony)
        {
            const string Label = "D-Vis-5 L1043 SteamChannel.send(name,mode,point,radius,type,args)";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(SteamChannel), "send",
                    new[] { typeof(string), typeof(ESteamCall), typeof(Vector3), typeof(float), typeof(ESteamPacket), typeof(object[]) });
                if (original == null) { RoleLogger.Error("[Shared]", $"[D-Vis-5] !!! {Label} 反射失败"); return false; }
                MethodInfo prefix = typeof(Hooks).GetMethod(nameof(Hooks.L1043Prefix), BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, prefix: new HarmonyMethod(prefix));
                RoleLogger.Info("[Shared]", $"[D-Vis-5] OK {Label} 已登记");
                return true;
            }
            catch (System.Exception ex) { RoleLogger.Error("[Shared]", $"[D-Vis-5] !!! {Label} 异常: {ex.Message}"); return false; }
        }

        // ====================== Hooks ======================

        private static class Hooks
        {
            // L494: send(string name, CSteamID steamID, ESteamPacket type, params object[] arguments)
            internal static void L494Prefix(string name, Steamworks.CSteamID steamID, ESteamPacket type, object[] arguments)
            {
                try
                {
                    if (!ShouldLogDVis()) return;
                    ulong key = steamID.m_SteamID;
                    if (!ShouldEmit(key)) return;
                    string caller = DiagnosticMaskUtil.MaskSteamId(Provider.user.m_SteamID);
                    string target = DiagnosticMaskUtil.MaskSteamId(steamID.m_SteamID);
                    string summary = arguments != null ? $"{arguments.Length}args" : "null";
                    string hex = BuildHexSummary(arguments);
                    RoleLogger.Info("[Client]",
                        $"[D-Vis-5 L494] send caller={caller} target={target} name={name} type={type} args={summary} hex={hex}");
                }
                catch (System.Exception ex) { RoleLogger.Warn("[Client]", $"[D-Vis-5 L494] 异常: {ex.Message}"); }
            }

            // L528: send(ESteamCall mode, byte bound, ESteamPacket type, int size, byte[] packet)
            internal static void L528Prefix(ESteamCall mode, byte bound, ESteamPacket type, int size, byte[] packet)
            {
                try
                {
                    if (!ShouldLogDVis()) return;
                    ulong key = (ulong)mode + 1000U;
                    if (!ShouldEmit(key)) return;
                    RoleLogger.Info("[Client]",
                        $"[D-Vis-5 L528] send mode={mode} bound={bound} type={type} size={size} packetLen={packet?.Length ?? 0}");
                }
                catch (System.Exception ex) { RoleLogger.Warn("[Client]", $"[D-Vis-5 L528] 异常: {ex.Message}"); }
            }

            // L636: send(string name, ESteamCall mode, byte bound, ESteamPacket type, params object[] arguments)
            internal static void L636Prefix(string name, ESteamCall mode, byte bound, ESteamPacket type, object[] arguments)
            {
                try
                {
                    if (!ShouldLogDVis()) return;
                    ulong key = (ulong)mode + 2000U + bound;
                    if (!ShouldEmit(key)) return;
                    string summary = arguments != null ? $"{arguments.Length}args" : "null";
                    RoleLogger.Info("[Client]",
                        $"[D-Vis-5 L636] send name={name} mode={mode} bound={bound} type={type} args={summary}");
                }
                catch (System.Exception ex) { RoleLogger.Warn("[Client]", $"[D-Vis-5 L636] 异常: {ex.Message}"); }
            }

            // L653: send(ESteamCall mode, byte x, byte y, byte area, ESteamPacket type, int size, byte[] packet)
            internal static void L653Prefix(ESteamCall mode, byte x, byte y, byte area, ESteamPacket type, int size, byte[] packet)
            {
                try
                {
                    if (!ShouldLogDVis()) return;
                    ulong key = (ulong)mode + 3000U;
                    if (!ShouldEmit(key)) return;
                    RoleLogger.Info("[Client]",
                        $"[D-Vis-5 L653] send mode={mode} x={x} y={y} area={area} type={type} size={size} packetLen={packet?.Length ?? 0}");
                }
                catch (System.Exception ex) { RoleLogger.Warn("[Client]", $"[D-Vis-5 L653] 异常: {ex.Message}"); }
            }

            // L761: send(string name, ESteamCall mode, byte x, byte y, byte area, ESteamPacket type, params object[] arguments)
            internal static void L761Prefix(string name, ESteamCall mode, byte x, byte y, byte area, ESteamPacket type, object[] arguments)
            {
                try
                {
                    if (!ShouldLogDVis()) return;
                    ulong key = (ulong)mode + 4000U;
                    if (!ShouldEmit(key)) return;
                    string summary = arguments != null ? $"{arguments.Length}args" : "null";
                    RoleLogger.Info("[Client]",
                        $"[D-Vis-5 L761] send name={name} mode={mode} x={x} y={y} area={area} type={type} args={summary}");
                }
                catch (System.Exception ex) { RoleLogger.Warn("[Client]", $"[D-Vis-5 L761] 异常: {ex.Message}"); }
            }

            // L778: send(ESteamCall mode, ESteamPacket type, int size, byte[] packet)
            internal static void L778Prefix(ESteamCall mode, ESteamPacket type, int size, byte[] packet)
            {
                try
                {
                    if (!ShouldLogDVis()) return;
                    ulong key = (ulong)mode + 5000U;
                    if (!ShouldEmit(key)) return;
                    RoleLogger.Info("[Client]",
                        $"[D-Vis-5 L778] send mode={mode} type={type} size={size} packetLen={packet?.Length ?? 0}");
                }
                catch (System.Exception ex) { RoleLogger.Warn("[Client]", $"[D-Vis-5 L778] 异常: {ex.Message}"); }
            }

            // L893: send(string name, ESteamCall mode, ESteamPacket type, params object[] arguments)
            internal static void L893Prefix(string name, ESteamCall mode, ESteamPacket type, object[] arguments)
            {
                try
                {
                    if (!ShouldLogDVis()) return;
                    ulong key = (ulong)mode + 6000U;
                    if (!ShouldEmit(key)) return;
                    string summary = arguments != null ? $"{arguments.Length}args" : "null";
                    string hex = BuildHexSummary(arguments);
                    RoleLogger.Info("[Client]",
                        $"[D-Vis-5 L893] send name={name} mode={mode} type={type} args={summary} hex={hex}");
                }
                catch (System.Exception ex) { RoleLogger.Warn("[Client]", $"[D-Vis-5 L893] 异常: {ex.Message}"); }
            }

            // L933: send(ESteamCall mode, Vector3 point, float radius, ESteamPacket type, int size, byte[] packet)
            internal static void L933Prefix(ESteamCall mode, Vector3 point, float radius, ESteamPacket type, int size, byte[] packet)
            {
                try
                {
                    if (!ShouldLogDVis()) return;
                    ulong key = (ulong)mode + 7000U;
                    if (!ShouldEmit(key)) return;
                    string ptStr = $"({point.x:F2},{point.y:F2},{point.z:F2})";
                    RoleLogger.Info("[Client]",
                        $"[D-Vis-5 L933] send mode={mode} point={ptStr} radius={radius:F2} type={type} size={size} packetLen={packet?.Length ?? 0}");
                }
                catch (System.Exception ex) { RoleLogger.Warn("[Client]", $"[D-Vis-5 L933] 异常: {ex.Message}"); }
            }

            // L1043: send(string name, ESteamCall mode, Vector3 point, float radius, ESteamPacket type, params object[] arguments)
            internal static void L1043Prefix(string name, ESteamCall mode, Vector3 point, float radius, ESteamPacket type, object[] arguments)
            {
                try
                {
                    if (!ShouldLogDVis()) return;
                    ulong key = (ulong)mode + 8000U;
                    if (!ShouldEmit(key)) return;
                    string ptStr = $"({point.x:F2},{point.y:F2},{point.z:F2})";
                    string summary = arguments != null ? $"{arguments.Length}args" : "null";
                    RoleLogger.Info("[Client]",
                        $"[D-Vis-5 L1043] send name={name} mode={mode} point={ptStr} radius={radius:F2} type={type} args={summary}");
                }
                catch (System.Exception ex) { RoleLogger.Warn("[Client]", $"[D-Vis-5 L1043] 异常: {ex.Message}"); }
            }
        }

        // ====================== Helpers ======================

        private static bool ShouldEmit(ulong key)
        {
            float now = Time.realtimeSinceStartup;
            if (_lastLogTime.TryGetValue(key, out float t) && now - t < THROTTLE_SECONDS) return false;
            _lastLogTime[key] = now;
            return true;
        }

        private static string BuildHexSummary(object[] arguments)
        {
            if (arguments == null || arguments.Length == 0 || arguments[0] == null) return "n/a";
            try
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(arguments[0].ToString());
                string fullHex = System.BitConverter.ToString(bytes).Replace("-", "");
                return fullHex.Length > 64 ? fullHex.Substring(0, 64) : fullHex;
            }
            catch { return "err"; }
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
            catch { return false; }
        }

        /// <summary>
        /// v0.2.3.18 P1：客机断开时清除节流状态。
        /// </summary>
        public static void OnClientDisconnected()
        {
            try { _lastLogTime.Clear(); } catch { /* ignore */ }
        }
    }
}
