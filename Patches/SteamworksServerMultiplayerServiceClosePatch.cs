using HarmonyLib;
using SDG.SteamworksProvider.Services.Multiplayer.Server;
using SDG.Unturned;
using SteamP2PFriends.Shared;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    ///
    /// 策略：绝不调 LogOff/Shutdown，仅 SetAdvertiseServerActive(false) + isHosting=false。
    ///
    /// 根因：Steamworks GameServer API 每个进程只能 Init 一次，对应的 Shutdown 也只能调一次。
    /// 退回主菜单时若 vanilla close() 调 LogOff/Shutdown，第二局再开服时 GameServer.Init 会失败。
    /// 会话复用依赖 GameServer 持续存活，故 close() 仅做最小清理。
    /// </summary>
    [HarmonyPatch(typeof(SteamworksServerMultiplayerService), "close")]
    public static class SteamworksServerMultiplayerServiceClosePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(SteamworksServerMultiplayerService __instance)
        {
            if (Dedicator.IsDedicatedServer)
                return true;

            RoleLogger.Info("[Shared]", "[SessionReuse] close() Prefix - 仅 SetAdvertiseServerActive(false) + isHosting=false，不调 LogOff/Shutdown");

            try { SteamRuntime.SetAdvertiseServerActive(false); }
            catch (System.Exception ex) { RoleLogger.Warn("[Shared]", $"SetAdvertiseServerActive(false) 异常: {ex.Message}"); }

            try
            {
                var isHostingProp = AccessTools.Property(typeof(SteamworksServerMultiplayerService), "isHosting");
                var setter = isHostingProp?.GetSetMethod(true);
                if (setter != null)
                {
                    setter.Invoke(__instance, new object[] { false });
                }
            }
            catch (System.Exception ex) { RoleLogger.Warn("[Shared]", $"isHosting=false 异常: {ex.Message}"); }

            return false;
        }
    }
}
