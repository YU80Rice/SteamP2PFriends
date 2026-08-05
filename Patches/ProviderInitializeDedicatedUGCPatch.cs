using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// 跳过 Provider.initializeDedicatedUGC（对齐原版 SteamP2PFriends HarmonyPatches.cs:12-26）。
    ///
    /// 原版策略：仅 return false 跳过整个方法，不手动 Invoke onDedicatedUGCInstalled。
    /// 地图加载由 OnServerHosted -> LoadClientHostedLevel -> Level.load 完成，与 UGC 链路完全解耦。
    ///
    /// 历史教训（v0.1.0 首版偏离原版）：
    ///   - 手动 Invoke onDedicatedUGCInstalled -> NRE（DedicatedUGC.installed 未初始化）
    ///   - RegisterLocalWorkshopItemsForServer + RestoreClientToHostRealSteamId 画蛇添足
    ///   - 客机进度条卡死根因：UGC NRE 让 Level.load 永不被调
    /// </summary>
    [HarmonyPatch(typeof(Provider), "initializeDedicatedUGC")]
    public static class ProviderInitializeDedicatedUGCPatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            if (HostManager.IsStarting && !Dedicator.IsDedicatedServer)
            {
                RoleLogger.Info("[Shared]", "[P2P-UGC] Skipping DedicatedUGC for client-hosted server");
                return false;
            }

            return true;
        }
    }
}
