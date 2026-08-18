using HarmonyLib;
using SDG.Provider.Services.Multiplayer;
using SDG.Provider.Services.Multiplayer.Server;
using SDG.SteamworksProvider.Services.Multiplayer.Server;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using SteamP2PFriends.Shared.Enums;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    ///
    /// 根因：Steamworks 的 GameServer.Init 每个进程只能成功调用一次。退回主菜单后再开第二局时，
    /// 底层 GameServer API 仍处于存活状态（未 LogOff/Shutdown），但 isHosting 在 disconnect 后
    /// 可能为 false，导致 open() 再次调用 GameServer.Init -> 失败或异常。
    ///
    /// 本补丁通过 GameServer.GetHSteamPipe() 探测会话是否仍存活：
    ///   - 无存活会话（首局）：放行原始 open()，正常 Init + LogOn。
    ///   - 有存活会话（第二局+）：跳过 Init/LogOn，仅重新启用广告、置 isHosting=true。
    ///
    /// </summary>
    [HarmonyPatch(typeof(SteamworksServerMultiplayerService), "open", new[] { typeof(uint), typeof(ushort), typeof(ESecurityMode) })]
    public static class SteamworksServerMultiplayerServiceOpenPatch
    {
        public static bool Prefix(SteamworksServerMultiplayerService __instance)
        {
            try
            {
                ESteamServerVisibility expected;
                string branch;
                expected = ESteamServerVisibility.LAN;
                branch = HostManager.HostMode == EHostMode.P2P
                    ? "P2P-only（SteamUser identity）"
                    : "LAN/非 P2P（GSLT 已移除）";

                ESteamServerVisibility before = Dedicator.serverVisibility;
                if (before != expected)
                {
                    RoleLogger.Warn("[Shared]",
                        $"[P2P-Vis] open() Prefix 检测 serverVisibility={before}（与期望 {expected} 不符），已强制矫正（{branch}）");
                    Dedicator.serverVisibility = expected;
                }
                else
                {
                    RoleLogger.Info("[Shared]",
                        $"[P2P-Vis] open() Prefix serverVisibility 校验通过：{before}（{branch}）");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Shared]", $"[P2P-Vis] open() Prefix 守卫异常（不阻断 open）: {ex.Message}");
            }

            // 专用服务器走原生生命周期，不干预
            if (Dedicator.IsDedicatedServer)
                return true;

            if (!SteamRuntime.IsGameServerAlive())
            {
                RoleLogger.Info("[Shared]", "[SessionReuse] 无存活 GameServer，按原生流程初始化。");
                return true;
            }

            // 会话复用路径
            RoleLogger.Info("[Shared]", "[SessionReuse] 检测到 Steam GameServer 仍存活，仅复用会话（isHosting=true；P2P 模式不启用 GS 广告）");

            SteamRuntime.SetAdvertiseServerActive(HostManager.HostMode != EHostMode.P2P);

            PropertyInfo isHostingProp = AccessTools.Property(typeof(SteamworksServerMultiplayerService), "isHosting");
            MethodInfo setter = isHostingProp?.GetSetMethod(true);
            if (setter != null)
            {
                setter.Invoke(__instance, new object[] { true });
            }
            else
            {
                RoleLogger.Warn("[Shared]", "[SessionReuse] 无法获取 isHosting setter，服务状态可能不一致。");
            }

            return false;
        }

        public static void Postfix(SteamworksServerMultiplayerService __instance)
        {
            try
            {
                bool gsAlive = SteamRuntime.IsGameServerAlive();
                ESteamServerVisibility vis = Dedicator.serverVisibility;
                bool isHosting = __instance.isHosting;
                string actualLoginToken = Provider.configData?.Browser?.Login_Token?.Trim() ?? "";
                string gsltStatus = string.IsNullOrEmpty(actualLoginToken) ? "空-LogOnAnonymous" : $"已配置-LogOn(token, 长度={actualLoginToken.Length})";

                RoleLogger.Info("[Shared]",
                    $"[P2P-Audit] open() Postfix - GameServer.Init() 已执行 | gsAlive={gsAlive} | " +
                    $"serverVisibility={vis} | isHosting={isHosting} | GSLT={gsltStatus}");
                if (gsAlive)
                {
                    try
                    {
                        string idStr = SteamRuntime.GetGameServerSteamIDString();
                        RoleLogger.Info("[Shared]",
                            $"[P2P-Audit] GameServer 已存活，SteamID={idStr ?? "<未登录>"}（异步登录未完成时可能为 0）");
                    }
                    catch (System.Exception ex)
                    {
                        RoleLogger.Warn("[Shared]",
                            $"[P2P-Audit] Postfix 取 SteamGameServer.GetSteamID 异常（不阻断）: {ex.Message}");
                    }
                }
                else
                {
                    RoleLogger.Error("[Shared]",
                        "[P2P-Audit] !!! GameServer.Init() 失败或未执行 - gsAlive=false，SteamGameServer 后端未初始化 !!!");
                }
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Shared]", $"[P2P-Audit] open() Postfix 异常（不阻断）: {ex.Message}");
            }
        }
    }
}
