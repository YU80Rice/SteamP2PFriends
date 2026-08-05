using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.18 D-Vis-3/4 诊断 patch 合集（客机模型可见性差异诊断）。
    ///
    /// D-Vis-3：PlayerInput.ReceiveSimulateMispredictedInputs Prefix（双端）
    ///   - U3-SDK 路径：PlayerInput.cs:1353
    ///   - 签名：public void ReceiveSimulateMispredictedInputs(uint frameNumber, EPlayerStance stance,
    ///     Vector3 position, Vector3 velocity, byte stamina, int lastTireOffset, int lastRestOffset)
    ///   - 目的：验证 H2 假设（客机端 PlayerInput 是否正常发送至主机）
    ///   - 注：U3-SDK PlayerLook 类没有 SendLookAngle/ReceiveLookAngle 方法，
    ///         Look 角度通过 PlayerMovement_NetMethods.cs 自动生成的代码处理，
    ///         PlayerInput.ReceiveSimulateMispredictedInputs 携带 position/velocity 可间接验证
    ///
    /// D-Vis-4：PlayerAnimator.ReceiveLean / ReceiveGesture Prefix（双端）
    ///   - U3-SDK 路径：PlayerAnimator.cs:674 / 687
    ///   - 签名：public void ReceiveLean(byte newLean) / public void ReceiveGesture(EPlayerGesture newGesture)
    ///   - 目的：验证客机端 Animator 状态同步方向性
    ///
    /// 严格禁止：
    ///   - 修改原方法参数或返回值
    ///   - 修改 vanilla 网络栈
    /// </summary>
    public static class PlayerLookAnimatorDiagnosticPatch
    {
        public static bool DVis3Registered { get; private set; }
        public static bool DVis4Registered { get; private set; }

        public static bool AllRegistrationsSucceeded => DVis3Registered && DVis4Registered;

        public static bool RegisterManual(Harmony harmony)
        {
            DVis3Registered = RegisterDVis3(harmony);
            DVis4Registered = RegisterDVis4(harmony);

            RoleLogger.Info("[Shared]",
                $"[D-Vis] PlayerLookAnimatorDiagnosticPatch 汇总: " +
                $"D-Vis-3={DVis3Registered} D-Vis-4={DVis4Registered}");

            return AllRegistrationsSucceeded;
        }

        // ---------- D-Vis-3: PlayerInput.ReceiveSimulateMispredictedInputs ----------
        private static bool RegisterDVis3(Harmony harmony)
        {
            const string Label = "D-Vis-3 PlayerInput.ReceiveSimulateMispredictedInputs";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(PlayerInput), "ReceiveSimulateMispredictedInputs");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[D-Vis-3] !!! {Label} 反射失败");
                    return false;
                }
                MethodInfo prefix = typeof(PlayerInputHooks).GetMethod(nameof(PlayerInputHooks.Prefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, prefix: new HarmonyMethod(prefix));
                RoleLogger.Info("[Shared]", $"[D-Vis-3] OK {Label} 已登记 (Prefix)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[D-Vis-3] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        // ---------- D-Vis-4: PlayerAnimator.ReceiveLean / ReceiveGesture ----------
        private static bool RegisterDVis4(Harmony harmony)
        {
            bool r1 = RegisterDVis4ReceiveLean(harmony);
            bool r2 = RegisterDVis4ReceiveGesture(harmony);
            RoleLogger.Info("[Shared]",
                $"[D-Vis-4] PlayerAnimator 登记汇总: ReceiveLean={r1} ReceiveGesture={r2}");
            return r1 && r2;
        }

        private static bool RegisterDVis4ReceiveLean(Harmony harmony)
        {
            const string Label = "D-Vis-4 PlayerAnimator.ReceiveLean";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(PlayerAnimator), "ReceiveLean");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[D-Vis-4] !!! {Label} 反射失败");
                    return false;
                }
                MethodInfo prefix = typeof(PlayerAnimatorHooks).GetMethod(nameof(PlayerAnimatorHooks.ReceiveLeanPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, prefix: new HarmonyMethod(prefix));
                RoleLogger.Info("[Shared]", $"[D-Vis-4] OK {Label} 已登记 (Prefix)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[D-Vis-4] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        private static bool RegisterDVis4ReceiveGesture(Harmony harmony)
        {
            const string Label = "D-Vis-4 PlayerAnimator.ReceiveGesture";
            try
            {
                MethodInfo original = AccessTools.Method(typeof(PlayerAnimator), "ReceiveGesture");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", $"[D-Vis-4] !!! {Label} 反射失败");
                    return false;
                }
                MethodInfo prefix = typeof(PlayerAnimatorHooks).GetMethod(nameof(PlayerAnimatorHooks.ReceiveGesturePrefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(original, prefix: new HarmonyMethod(prefix));
                RoleLogger.Info("[Shared]", $"[D-Vis-4] OK {Label} 已登记 (Prefix)");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[D-Vis-4] !!! {Label} 登记异常: {ex.Message}");
                return false;
            }
        }

        // ====================== Hooks ======================

        private static class PlayerInputHooks
        {
            internal static void Prefix(PlayerInput __instance, uint frameNumber, EPlayerStance stance,
                UnityEngine.Vector3 position, UnityEngine.Vector3 velocity, byte stamina,
                int lastTireOffset, int lastRestOffset)
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
                    RoleLogger.Info("[Shared]",
                        $"[D-Vis-3] ReceiveSimulateMispredictedInputs receiver={DiagnosticMaskUtil.MaskSteamId(receiverSteamId)} " +
                        $"isLocalPlayer={isLocalPlayer} frame={frameNumber} stance={stance} " +
                        $"pos=({position.x:F2},{position.y:F2},{position.z:F2}) stamina={stamina}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Shared]", $"[D-Vis-3] 异常（不阻断）: {ex.Message}");
                }
            }
        }

        private static class PlayerAnimatorHooks
        {
            internal static void ReceiveLeanPrefix(PlayerAnimator __instance, byte newLean)
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
                    RoleLogger.Info("[Shared]",
                        $"[D-Vis-4] ReceiveLean receiver={DiagnosticMaskUtil.MaskSteamId(receiverSteamId)} " +
                        $"isLocalPlayer={isLocalPlayer} newLean={newLean}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Shared]", $"[D-Vis-4] ReceiveLean 异常（不阻断）: {ex.Message}");
                }
            }

            internal static void ReceiveGesturePrefix(PlayerAnimator __instance, EPlayerGesture newGesture)
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
                    RoleLogger.Info("[Shared]",
                        $"[D-Vis-4] ReceiveGesture receiver={DiagnosticMaskUtil.MaskSteamId(receiverSteamId)} " +
                        $"isLocalPlayer={isLocalPlayer} newGesture={newGesture}");
                }
                catch (System.Exception ex)
                {
                    RoleLogger.Warn("[Shared]", $"[D-Vis-4] ReceiveGesture 异常（不阻断）: {ex.Message}");
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
