using HarmonyLib;
using SDG.NetTransport;
using SDG.NetTransport.Loopback;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using SteamP2PFriends.Shared.Enums;
using Steamworks;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.2 P0-C IsLocalServerHost 修正 patch（v2 审计放行后启用）。
    ///
    /// 根因（v2 §1.3 + §八 证据 1-3）：
    ///   - vanilla SteamPlayer.ctor line 786：
    ///       IsLocalServerHost = transportConnection != null &amp;&amp; !Dedicator.IsDedicatedServer
    ///   - listen server 模式下 Dedicator.IsDedicatedServer=false，
    ///     远程客机的 SteamPlayer 也被错判为 IsLocalServerHost=true。
    ///   - 该字段被 GatherRemoteClientConnections* 用于过滤远端连接，
    ///     错判导致远端客机收不到部分状态同步。
    ///
    /// 修正策略（v2 §3.2 line 320-340）：
    ///   - SteamPlayer.ctor Postfix 反射设置 backing field。
    ///   - backing field 名称 = &lt;IsLocalServerHost&gt;k__BackingField（auto-property 命名规则）。
    ///   - 修正逻辑：loopback transportConnection + SteamID == Provider.user -> IsLocalServerHost=true；
    ///     其他情况 -> IsLocalServerHost=false。
    ///
    /// v2 审计 §4.2 评估：
    ///   - 字段名正确，P2P 门控正确，修正逻辑正确。
    ///   - Postfix 时机有效（反编译证据：构造器 line 786 后无该字段读取）。
    ///   - 实际效果依赖 A/B 对照验证（Medium-7）。
    ///
    /// P0-E 集成：
    ///   - 本 patch 同时调用 PlayerInitializationTracker.MarkConstructed(player)。
    ///   - 配合 P0-E 状态表 Constructed -> Initializing -> Ready 转换。
    ///
    /// 二次审计 Medium-1 修复：
    ///   - 移除 [HarmonyPatch] 自动登记特性，改用 RegisterManual 手动登记。
    ///   - FullFixBuild=false 时不登记本 patch，真正禁用 P0-C（构建 A）。
    /// </summary>
    public static class SteamPlayerIsLocalServerHostPatch
    {
        public const bool Enabled = true;

        private static FieldInfo _isLocalServerHostField;
        private static bool _registered;

        static SteamPlayerIsLocalServerHostPatch()
        {
            _isLocalServerHostField = typeof(SteamPlayer)
                .GetField("<IsLocalServerHost>k__BackingField",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        /// <summary>
        /// 手动登记入口。由 Plugin.ApplyV2AuditFixPatches 在 FullFixBuild=true 时调用。
        /// </summary>
        public static void RegisterManual(Harmony harmony)
        {
            if (_registered) return;
            _registered = true;

            // 反射查找 SteamPlayer 构造器（单个 ctor，参数列表很长，用 GetConstructors 取第一个）
            ConstructorInfo ctor = typeof(SteamPlayer).GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, System.Type.EmptyTypes, null);
            if (ctor == null)
            {
                // 单 ctor 多参数情况，用 GetConstructors 取第一个
                ConstructorInfo[] ctors = typeof(SteamPlayer).GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ctors.Length == 0)
                {
                    RoleLogger.Error("[Shared]", "[P0-C] SteamPlayer 无构造器，登记失败");
                    return;
                }
                ctor = ctors[0];
            }

            MethodInfo postfix = AccessTools.Method(
                typeof(SteamPlayerIsLocalServerHostPatch), nameof(Postfix));
            harmony.Patch(ctor, postfix: new HarmonyMethod(postfix));

            RoleLogger.Info("[Shared]",
                $"[P0-C] SteamPlayerIsLocalServerHostPatch registered manually " +
                $"(SteamPlayer.ctor Postfix, backing field=<IsLocalServerHost>k__BackingField, P2P 门控)");
        }

        public static void Postfix(SteamPlayer __instance, ITransportConnection transportConnection)
        {
            // 门控：仅 P2P 主机模式
            if (!HostManager.IsP2PHostMode) return;

            if (_isLocalServerHostField == null)
            {
                RoleLogger.Warn("[Host]",
                    "[P0-C] 反射失败：找不到 <IsLocalServerHost>k__BackingField 字段。" +
                    "可能 Assembly-CSharp.dll 已更新，需重新反编译验证字段名。");
                return;
            }

            try
            {
                // 修正逻辑：loopback transportConnection + SteamID == Provider.user -> true
                // TransportConnection_Loopback 是 struct，赋值给 ITransportConnection 时装箱，is 检查正常工作
                bool isLoopback = transportConnection is TransportConnection_Loopback;
                bool isLocalSteamId = false;
                try
                {
                    var playerID = __instance.playerID;
                    if (!ReferenceEquals(playerID, null))
                    {
                        isLocalSteamId = playerID.steamID == Provider.user;
                    }
                }
                catch
                {
                    // playerID 早期可能未赋值
                }

                bool corrected = isLoopback && isLocalSteamId;
                _isLocalServerHostField.SetValue(__instance, corrected);

                RoleLogger.Info("[Host]",
                    $"[P0-C] SteamPlayer.ctor Postfix: steamId={__instance.playerID?.steamID} " +
                    $"isLoopback={isLoopback} isLocalSteamId={isLocalSteamId} " +
                    $"corrected IsLocalServerHost={corrected}");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Host]", $"[P0-C] Postfix 异常: {ex}");
            }

            // P0-E 集成：标记 Player 实例为 Constructed
            try
            {
                Player player = __instance.player;
                if (!ReferenceEquals(player, null))
                {
                    PlayerInitializationTracker.MarkConstructed(player);
                }
            }
            catch
            {
                // player 可能在 SteamPlayer.ctor 阶段还未赋值
            }
        }
    }
}
