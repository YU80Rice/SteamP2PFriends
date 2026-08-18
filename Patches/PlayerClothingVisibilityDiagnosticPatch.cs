using HarmonyLib;
using SDG.NetPak;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    ///
    /// D-Vis-1：PlayerClothing.ReceiveClothingState Prefix（双端）
    ///   - U3-SDK 路径：PlayerClothing.cs:1318
    ///   - 签名：public void ReceiveClothingState(in ClientInvocationContext context)
    ///   - 目的：验证 H1 假设（客机->主机应用层同步失效）
    ///         （避免双重 patch 导致日志混乱）
    ///
    /// D-Vis-2：PlayerEquipment.ReceiveSlot / ReceiveUpdateState / ReceiveEquip Prefix（双端）
    ///   - U3-SDK 路径：PlayerEquipment.cs:1093 / 1132 / 1278
    ///   - 目的：验证 H1 是否扩展到 Equipment 状态
    ///
    /// D-Vis-7：PlayerClothing.sendUpdateShirtQuality Prefix（客机端）
    ///   - U3-SDK 路径：PlayerClothing.cs:187
    ///   - 签名：public void sendUpdateShirtQuality()
    ///   - 目的：验证客机端是否真的调用了 sendUpdateShirtQuality
    ///   - shirtQuality 是 public 字段（L117），直接访问无需反射
    ///
    /// 严格禁止：
    ///   - 修改原方法参数或返回值
    ///   - 修改 vanilla 网络栈
    ///   - 修改 Renderer 状态
    /// </summary>
    public static class PlayerClothingVisibilityDiagnosticPatch
    {
        public static bool DVis1Registered { get; private set; }
        public static bool DVis2Registered { get; private set; }
        public static bool DVis7Registered { get; private set; }

        public static bool AllRegistrationsSucceeded => DVis1Registered && DVis2Registered && DVis7Registered;

        public static bool RegisterManual(Harmony harmony)
        {
            DVis1Registered = RegisterDVis1(harmony);
            DVis2Registered = RegisterDVis2(harmony);
            DVis7Registered = RegisterDVis7(harmony);

            RoleLogger.Info("[Shared]",
                $"[D-Vis] PlayerClothingVisibilityDiagnosticPatch 汇总: " +
                $"D-Vis-1={DVis1Registered} D-Vis-2={DVis2Registered} D-Vis-7={DVis7Registered}");

            return AllRegistrationsSucceeded;
        }

        // ---------- D-Vis-1: PlayerClothing.ReceiveClothingState ----------
        // 本方法仅返回 true 占位，实际 D-Vis-1 日志由 InitialStateReceiveDiagnosticPatch 输出
        private static bool RegisterDVis1(Harmony harmony)
        {
            const string Label = "D-Vis-1 PlayerClothing.ReceiveClothingState";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(PlayerClothing), "ReceiveClothingState");
                if (original == null)
                {
                    RoleLogger.Warn("[Shared]", $"[D-Vis-1] {Label} 反射失败（v0.2.3.17 已登记，跳过）");
                    return false;
                }
                RoleLogger.Info("[Shared]",
                    $"[D-Vis-1] {Label} 已存在 v0.2.3.17 patch（InitialStateReceiveDiagnosticPatch），不重复登记");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[D-Vis-1] {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        // ---------- D-Vis-2: PlayerEquipment.ReceiveSlot / ReceiveUpdateState / ReceiveEquip ----------
        private static bool RegisterDVis2(Harmony harmony)
        {
            bool r1 = RegisterDVis2ReceiveSlot(harmony);
            bool r2 = RegisterDVis2ReceiveUpdateState(harmony);
            bool r3 = RegisterDVis2ReceiveEquip(harmony);
            RoleLogger.Info("[Shared]",
                $"[D-Vis-2] PlayerEquipment 登记汇总: ReceiveSlot={r1} ReceiveUpdateState={r2} ReceiveEquip={r3}");
            return r1 && r2 && r3;
        }

        private static bool RegisterDVis2ReceiveSlot(Harmony harmony)
        {
            const string Label = "D-Vis-2 PlayerEquipment.ReceiveSlot";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(PlayerEquipment), "ReceiveSlot");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[D-Vis-2] !!! {Label} 反射失败");
                    return false;
                }
                MethodInfo prefix = typeof(PlayerEquipmentHooks).GetMethod(nameof(PlayerEquipmentHooks.ReceiveSlotPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, prefix: new HarmonyMethod(prefix));
                RoleLogger.Info("[Shared]", $"[D-Vis-2] OK {Label} 已登记 (Prefix)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[D-Vis-2] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        private static bool RegisterDVis2ReceiveUpdateState(Harmony harmony)
        {
            const string Label = "D-Vis-2 PlayerEquipment.ReceiveUpdateState";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(PlayerEquipment), "ReceiveUpdateState");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[D-Vis-2] !!! {Label} 反射失败");
                    return false;
                }
                MethodInfo prefix = typeof(PlayerEquipmentHooks).GetMethod(nameof(PlayerEquipmentHooks.ReceiveUpdateStatePrefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, prefix: new HarmonyMethod(prefix));
                RoleLogger.Info("[Shared]", $"[D-Vis-2] OK {Label} 已登记 (Prefix)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[D-Vis-2] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        private static bool RegisterDVis2ReceiveEquip(Harmony harmony)
        {
            const string Label = "D-Vis-2 PlayerEquipment.ReceiveEquip";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(PlayerEquipment), "ReceiveEquip");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[D-Vis-2] !!! {Label} 反射失败");
                    return false;
                }
                MethodInfo prefix = typeof(PlayerEquipmentHooks).GetMethod(nameof(PlayerEquipmentHooks.ReceiveEquipPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, prefix: new HarmonyMethod(prefix));
                RoleLogger.Info("[Shared]", $"[D-Vis-2] OK {Label} 已登记 (Prefix)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[D-Vis-2] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        // ---------- D-Vis-7: PlayerClothing.sendUpdateShirtQuality ----------
        private static bool RegisterDVis7(Harmony harmony)
        {
            const string Label = "D-Vis-7 PlayerClothing.sendUpdateShirtQuality";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(PlayerClothing), "sendUpdateShirtQuality");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[D-Vis-7] !!! {Label} 反射失败");
                    return false;
                }
                MethodInfo prefix = typeof(PlayerClothingHooks).GetMethod(nameof(PlayerClothingHooks.SendUpdateShirtQualityPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, prefix: new HarmonyMethod(prefix));
                RoleLogger.Info("[Shared]", $"[D-Vis-7] OK {Label} 已登记 (Prefix)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[D-Vis-7] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        // ====================== Hooks ======================

        private static class PlayerEquipmentHooks
        {
            internal static void ReceiveSlotPrefix(PlayerEquipment __instance, byte slot, ushort id, byte[] state)
            {
                try
                {
                    if (!ShouldLogDVis()) return;
                    Player player = __instance?.player;
                    if (player == null) return;
                    SteamPlayer sp = player.channel?.owner;
                    if (sp == null) return;
                    ulong receiverSteamId = sp.playerID?.steamID.m_SteamID ?? 0UL;
                    bool isLocalPlayer = player.channel?.IsLocalPlayer ?? false;
                    int stateLen = state?.Length ?? 0;
                    RoleLogger.Info("[Shared]",
                        $"[D-Vis-2] ReceiveSlot receiver={DiagnosticMaskUtil.MaskSteamId(receiverSteamId)} " +
                        $"isLocalPlayer={isLocalPlayer} slot={slot} id={id} stateLen={stateLen}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Shared]", $"[D-Vis-2] ReceiveSlot 异常（不阻断）: {ex.Message}");
                }
            }

            internal static void ReceiveUpdateStatePrefix(PlayerEquipment __instance, byte page, byte index, byte[] newState)
            {
                try
                {
                    if (!ShouldLogDVis()) return;
                    Player player = __instance?.player;
                    if (player == null) return;
                    SteamPlayer sp = player.channel?.owner;
                    if (sp == null) return;
                    ulong receiverSteamId = sp.playerID?.steamID.m_SteamID ?? 0UL;
                    bool isLocalPlayer = player.channel?.IsLocalPlayer ?? false;
                    int stateLen = newState?.Length ?? 0;
                    RoleLogger.Info("[Shared]",
                        $"[D-Vis-2] ReceiveUpdateState receiver={DiagnosticMaskUtil.MaskSteamId(receiverSteamId)} " +
                        $"isLocalPlayer={isLocalPlayer} page={page} index={index} newStateLen={stateLen}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Shared]", $"[D-Vis-2] ReceiveUpdateState 异常（不阻断）: {ex.Message}");
                }
            }

            internal static void ReceiveEquipPrefix(PlayerEquipment __instance, byte page, byte x, byte y,
                System.Guid newAssetGuid, byte newQuality, byte[] newState, NetId useableNetId)
            {
                try
                {
                    if (!ShouldLogDVis()) return;
                    Player player = __instance?.player;
                    if (player == null) return;
                    SteamPlayer sp = player.channel?.owner;
                    if (sp == null) return;
                    ulong receiverSteamId = sp.playerID?.steamID.m_SteamID ?? 0UL;
                    bool isLocalPlayer = player.channel?.IsLocalPlayer ?? false;
                    int stateLen = newState?.Length ?? 0;
                    RoleLogger.Info("[Shared]",
                        $"[D-Vis-2] ReceiveEquip receiver={DiagnosticMaskUtil.MaskSteamId(receiverSteamId)} " +
                        $"isLocalPlayer={isLocalPlayer} page={page} x={x} y={y} " +
                        $"assetGuid={newAssetGuid.ToString().Substring(0, 8)}... quality={newQuality} " +
                        $"newStateLen={stateLen} useableNetId={useableNetId.id}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Shared]", $"[D-Vis-2] ReceiveEquip 异常（不阻断）: {ex.Message}");
                }
            }
        }

        private static class PlayerClothingHooks
        {
            // D-Vis-7: sendUpdateShirtQuality 是客机端调用的公开方法（PlayerClothing.cs:187）
            // shirtQuality 是 public 字段（L117），直接访问
            // channel.GetOwnerTransportConnection() 是 public 方法
            internal static void SendUpdateShirtQualityPrefix(PlayerClothing __instance)
            {
                try
                {
                    if (!ShouldLogDVis()) return;
                    Player player = __instance?.player;
                    if (player == null) return;
                    SteamPlayer sp = player.channel?.owner;
                    if (sp == null) return;
                    ulong callerSteamId = sp.playerID?.steamID.m_SteamID ?? 0UL;
                    byte shirtQuality = __instance.shirtQuality;
                    string typeName = "unknown";
                    try
                    {
                        var tc = player.channel?.GetOwnerTransportConnection();
                        typeName = tc?.GetType().FullName ?? "null";
                    }
                    catch { /* ignore */ }
                    RoleLogger.Info("[Client]",
                        $"[D-Vis-7] sendUpdateShirtQuality caller={DiagnosticMaskUtil.MaskSteamId(callerSteamId)} " +
                        $"shirtQuality={shirtQuality} transportType={typeName}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Client]", $"[D-Vis-7] 异常（不阻断）: {ex.Message}");
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
    }
}
