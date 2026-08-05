using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using UnityEngine;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// v0.2.3.2 P0-J 方案 A 修正版（审计 §4.1）：
    ///   patch 点 = PlayerMovement.InitializePlayer Prefix（在 NRE 抛出前介入）。
    ///
    /// 根因（v2 §1.2 + §八 证据 7）：
    ///   - vanilla GameMode.getPlayerGameObject 为远程客机返回 Player_Client prefab。
    ///   - Player_Client prefab 无 CharacterController（class ID 143 缺失）。
    ///   - vanilla PlayerMovement.InitializePlayer line 2000-2018：
    ///       if (Provider.isServer || channel.IsLocalPlayer)
    ///           controller = GetComponent&lt;CharacterController&gt;();  // 返回 null
    ///           controller.enableOverlapRecovery = ...;                  // NRE
    ///   - 在 P2P listen server 模式下，主机端为远程客机创建 Player 对象，
    ///     Provider.isServer=true 触发该路径，controller 为 null 抛 NRE。
    ///
    /// 修正策略：
    ///   - Prefix 在 vanilla InitializePlayer 之前调用，若 controller 不存在则 AddComponent。
    ///   - 参数从 Resources.Load("Characters/Player_Server") prefab 复制，避免硬编码。
    ///   - 禁用 Player_Client 已有的非 trigger BoxCollider（已验证全为 trigger，分支不触发）。
    ///   - 不直接反射设置 controller 字段，让 vanilla line 2000 的 GetComponent 自然找到我们添加的组件。
    ///
    /// 门控：
    ///   - 仅 P2P 主机模式启用（HostManager.IsP2PHostMode）。
    ///   - 仅 Provider.isServer 时执行。
    ///   - 排除本地房主 Player（channel.IsLocalPlayer）。
    /// </summary>
    [HarmonyPatch(typeof(PlayerMovement), "InitializePlayer")]
    public static class PlayerMovementInitializePlayerPrefixPatch
    {
        public const bool Enabled = true;

        private static GameObject _cachedServerPrefab;
        private static CharacterController _cachedTemplate;
        private static bool _templateResolved;
        private static bool _templateLoadFailed;

        [HarmonyPriority(Priority.High)]
        static void Prefix(PlayerMovement __instance)
        {
            // 门控 1：仅 P2P 主机模式
            if (!HostManager.IsP2PHostMode) return;

            // 门控 2：仅主机端执行
            if (!Provider.isServer) return;

            // 门控 3：排除本地房主 Player
            Player player = __instance.player;
            if (ReferenceEquals(player, null)) return;
            bool isLocalPlayer = false;
            try
            {
                isLocalPlayer = player.channel?.IsLocalPlayer ?? false;
            }
            catch
            {
                // channel 可能在极早期为 null，此时不处理，让 vanilla 走原始路径
                return;
            }
            if (isLocalPlayer) return;

            // 检查是否已有 CharacterController
            CharacterController existing = __instance.GetComponent<CharacterController>();
            if (existing != null)
            {
                // 已有 controller（Player_Server prefab 或二次调用），放行 vanilla
                return;
            }

            // 加载 Player_Server prefab 模板（缓存）
            CharacterController template = ResolveServerPrefabTemplate();
            if (template == null)
            {
                RoleLogger.Warn("[Host]",
                    "[P0-J] Player_Server 模板不可用，跳过 AddComponent。" +
                    "vanilla InitializePlayer 将抛 NRE，由 D-11 Unity bridge 捕获。");
                return;
            }

            // 禁用 Player_Client 已有的非 trigger BoxCollider（防止物理冲突）
            // 已验证 Player_Client 的 6 个 BoxCollider 全为 trigger，本分支不会触发
            BoxCollider[] existingBoxColliders = __instance.GetComponents<BoxCollider>();
            int disabledCount = 0;
            foreach (var bc in existingBoxColliders)
            {
                if (bc != null && !bc.isTrigger)
                {
                    RoleLogger.Info("[Host]",
                        $"[P0-J] 禁用非 trigger BoxCollider: name={bc.gameObject.name} size={bc.size} center={bc.center}");
                    bc.enabled = false;
                    disabledCount++;
                }
            }

            // 添加 CharacterController 并复制 Player_Server 的参数
            CharacterController newCC = __instance.gameObject.AddComponent<CharacterController>();
            newCC.height = template.height;
            newCC.radius = template.radius;
            newCC.center = template.center;
            newCC.slopeLimit = template.slopeLimit;
            newCC.stepOffset = template.stepOffset;
            newCC.skinWidth = template.skinWidth;
            newCC.minMoveDistance = template.minMoveDistance;

            // 读取 steamId 用于日志（Player 类无 playerID，需通过 channel.owner.playerID）
            string steamIdStr = "unknown";
            try
            {
                var owner = player.channel?.owner;
                if (owner != null)
                {
                    var playerID = owner.playerID;
                    if (!ReferenceEquals(playerID, null))
                    {
                        steamIdStr = playerID.steamID.m_SteamID.ToString();
                    }
                }
            }
            catch
            {
                // 早期阶段 channel/owner/playerID 可能未赋值，忽略
            }

            RoleLogger.Info("[Host]",
                $"[P0-J] 为远程玩家动态添加 CharacterController: " +
                $"steamId={steamIdStr} instanceId={__instance.GetInstanceID()} " +
                $"height={newCC.height} radius={newCC.radius} center={newCC.center} " +
                $"slopeLimit={newCC.slopeLimit} stepOffset={newCC.stepOffset} " +
                $"skinWidth={newCC.skinWidth} minMoveDistance={newCC.minMoveDistance} " +
                $"disabledBoxColliders={disabledCount}");

            // 不在此处赋值 __instance.controller 字段。
            // vanilla InitializePlayer line 2000 会执行 controller = GetComponent<CharacterController>()，
            // 此时 GetComponent 会找到我们刚添加的 CharacterController，正常赋值。
        }

        private static CharacterController ResolveServerPrefabTemplate()
        {
            if (_templateResolved) return _cachedTemplate;
            if (_templateLoadFailed) return null;

            try
            {
                _cachedServerPrefab = Resources.Load<GameObject>("Characters/Player_Server");
                if (_cachedServerPrefab == null)
                {
                    RoleLogger.Warn("[Host]", "[P0-J] Resources.Load('Characters/Player_Server') 返回 null");
                    _templateLoadFailed = true;
                    return null;
                }

                _cachedTemplate = _cachedServerPrefab.GetComponent<CharacterController>();
                if (_cachedTemplate == null)
                {
                    RoleLogger.Warn("[Host]", "[P0-J] Player_Server prefab 无 CharacterController 组件");
                    _templateLoadFailed = true;
                    return null;
                }

                _templateResolved = true;
                RoleLogger.Info("[Host]",
                    $"[P0-J] Player_Server 模板已缓存: height={_cachedTemplate.height} " +
                    $"radius={_cachedTemplate.radius} center={_cachedTemplate.center} " +
                    $"slopeLimit={_cachedTemplate.slopeLimit} stepOffset={_cachedTemplate.stepOffset} " +
                    $"skinWidth={_cachedTemplate.skinWidth} minMoveDistance={_cachedTemplate.minMoveDistance}");
                return _cachedTemplate;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Host]", $"[P0-J] 加载 Player_Server 模板异常: {ex}");
                _templateLoadFailed = true;
                return null;
            }
        }

        public static void RegisterManual(Harmony harmony)
        {
            // HarmonyAnnotation 已通过 [HarmonyPatch] 声明，PatchAll 自动登记。
            // 此方法仅用于启动日志输出。
            RoleLogger.Info("[Shared]",
                $"[P0-J] PlayerMovementInitializePlayerPrefixPatch Enabled={Enabled} " +
                $"(patch 点=PlayerMovement.InitializePlayer Prefix, P2P 门控+IsLocalPlayer 排除)");
        }
    }
}
