using HarmonyLib;
using SDG.NetTransport;
using SDG.NetTransport.Loopback;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using SteamP2PFriends.Shared.Enums;
using Steamworks;
using System.Reflection;
using UnityEngine;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// Listen-host 模式下的 IsLocalServerHost 修正补丁。
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
    ///   - 本 patch 同时调用 PlayerInitializationTracker.MarkConstructed(player)。
    ///
    /// 使用显式 RegisterManual 登记，以便启动自检验证实际目标与所有者。
    /// </summary>
    public static class SteamPlayerIsLocalServerHostPatch
    {
        public const bool Enabled = true;

        private static FieldInfo _isLocalServerHostField;
        private static bool _registered;
        private static readonly System.Type[] SteamPlayerConstructorParameterTypes =
        {
            typeof(ITransportConnection), typeof(NetId), typeof(SteamPlayerID), typeof(Transform),
            typeof(bool), typeof(bool), typeof(int), typeof(byte), typeof(byte), typeof(byte),
            typeof(Color), typeof(Color), typeof(Color), typeof(Color), typeof(bool),
            typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int),
            typeof(int[]), typeof(string[]), typeof(string[]),
            typeof(EPlayerSkillset), typeof(string), typeof(CSteamID), typeof(EClientPlatform)
        };

        static SteamPlayerIsLocalServerHostPatch()
        {
            _isLocalServerHostField = typeof(SteamPlayer)
                .GetField("<IsLocalServerHost>k__BackingField",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        /// <summary>
        /// 手动登记入口。由插件启动过程调用。
        /// </summary>
        public static void RegisterManual(Harmony harmony)
        {
            if (_registered) return;
            _registered = true;

            // ABI 门控：禁止按构造器列表顺序猜测目标，游戏更新后必须显式失效。
            ConstructorInfo ctor = typeof(SteamPlayer).GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, SteamPlayerConstructorParameterTypes, null);
            if (ctor == null)
            {
                RoleLogger.Error("[Shared]",
                    "[Host] SteamPlayer 目标构造器签名不匹配，拒绝登记 IsLocalServerHost 修正补丁。");
                return;
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
