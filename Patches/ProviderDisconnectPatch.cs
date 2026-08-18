using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using System;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// Provider.disconnect 清理 patch（对齐原版 HarmonyPatches.cs:62-86）。
    ///
    ///   Postfix：房主模式下调 HostManager.StopP2PServer 清理状态
    ///   Finalizer：disconnect 抛异常时标记观察器失败，原样返回异常（不吞异常）
    ///
    ///   门 9 不变：函数所有控制流均 return __exception
    ///
    /// vanilla client-host disconnect 会拆 transport 但不关闭 Steam GameServer API，
    /// 故需 StopP2PServer 补做动力泵停止 + HasCheatsGuard 停止 + Rich Presence 清理。
    /// </summary>
    [HarmonyPatch(typeof(Provider), "disconnect")]
    public static class ProviderDisconnectPatch
    {
        //   不允许以 IsP2PServerActive 代替，因为 LAN 路径也会将该标志设为 true。
        [HarmonyPrefix]
        private static void Prefix()
        {
            if (!HostManager.IsStage6ANativeSaveObservationActive)
                return;

            try
            {
                HostManager.TryArmStage6ANativeSaveObservation();
            }
            catch (Exception observerException)
            {
                // Prefix 的观测失败不得阻断 vanilla disconnect/save。
                try
                {
                    RoleLogger.Error("[Host]",
                        "[Stage6A-Save] prefix observer failure: " + observerException.GetType().Name);
                }
                catch
                {
                }
            }
        }

        [HarmonyPostfix]
        private static void Postfix()
        {
            if (HostManager.IsP2PServerActive)
            {
                HostManager.StopP2PServer();
            }

            // 任何 disconnect 场景都清理 Rich Presence
            try { SteamRuntime.ClearAllRichPresence(); }
            catch { }
        }

        //   1. __exception == null 直接返回 null（vanilla 正常路径）
        //   4. 所有控制流 return __exception（不替换、不吞、不包装）
        [HarmonyFinalizer]
        private static Exception Finalizer(Exception __exception)
        {
            if (__exception == null)
                return null;

            if (HostManager.IsStage6ANativeSaveObservationActive)
            {
                try { HostManager.MarkStage6ANativeSaveObservationFailure(__exception); }
                catch (Exception observerException)
                {
                    try { RoleLogger.Error("[Host]", "[Stage6A-Save] finalizer observer failure: " + observerException.GetType().Name); }
                    catch { }
                }
            }

            try
            {
                if (HostManager.IsStage6BCurrentP2PExitEligible)
                    HostManager.TryCleanupStage6BForDisconnectFinalizer("Provider.disconnect.Finalizer");
            }
            catch { }

            return __exception;
        }
    }
}
