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
    /// v0.2.3.40 Stage 6A-1（Codex 83rd §3.3）：
    ///   Prefix：调 HostManager.TryArmStage6ANativeSaveObservation 设置观察器为 AwaitingNativeSave
    ///   Postfix：房主模式下调 HostManager.StopP2PServer 清理状态
    ///   Finalizer：disconnect 抛异常时标记观察器失败，原样返回异常（不吞异常）
    ///
    /// v0.2.3.40 Stage 6A-1（Codex 84th v1.4 [指令 B/C]）：
    ///   P0-OBS-01：Finalizer 用 try/catch 包裹观察器调用，确保观察器自身异常不会替换 __exception
    ///   P1-OBS-01：Prefix 和 Finalizer 以 IsStage6ANativeSaveObservationActive 为第一道环境隔离
    ///   门 9 不变：函数所有控制流均 return __exception
    ///
    /// vanilla client-host disconnect 会拆 transport 但不关闭 Steam GameServer API，
    /// 故需 StopP2PServer 补做动力泵停止 + HasCheatsGuard 停止 + Rich Presence 清理。
    /// </summary>
    [HarmonyPatch(typeof(Provider), "disconnect")]
    public static class ProviderDisconnectPatch
    {
        // v0.2.3.40 Stage 6A-1（Codex 84th v1.4 [指令 C]）：Prefix 精确环境隔离 + 观测失败不阻断 vanilla。
        //   P1-OBS-01：第一道环境隔离为 IsStage6ANativeSaveObservationActive，避免 LAN/单人/U3DS 进入观察器路径。
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

        // Codex 103rd 全流程接管蓝图 §6：唯一 Finalizer，整合 Stage6A 观察器转发 + Stage6B P2P 双门清理。
        //   1. __exception == null 直接返回 null（vanilla 正常路径）
        //   2. Stage6A 观察器转发保持原有 try/catch 语义，不吞异常
        //   3. Stage6B 经 HostManager.IsStage6BCurrentP2PExitEligible 双门，仅 P2P 异常时清理
        //   4. 所有控制流 return __exception（不替换、不吞、不包装）
        [HarmonyFinalizer]
        private static Exception Finalizer(Exception __exception)
        {
            if (__exception == null)
                return null;

            // Existing Stage6A semantics remain first.
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
