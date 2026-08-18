using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    ///
    /// 背景：
    ///   24th 双机测试用户反馈"客机同样无法登上载具"。
    ///   未定位登车失败具体环节（请求未发送 / 服务器未接收 / 验证失败）。
    ///
    ///
    /// U3-SDK 溯源：
    ///   - D:/Agent-工作目录/U3-SDK/Assets/Runtime/Assembly-CSharp/Unturned/Managers/VehicleManager.cs:418-423
    ///     public static void enterVehicle(InteractableVehicle vehicle)
    ///     -> SendEnterVehicleRequest.Invoke(ENetReliability.Unreliable, vehicle.instanceID, vehicle.asset.hash, physicsProfileHash, (byte)vehicle.asset.engine);
    ///     客机端调用入口
    ///
    ///   - D:/Agent-工作目录/U3-SDK/Assets/Runtime/Assembly-CSharp/Unturned/Managers/VehicleManager.cs:1625
    ///     private static readonly ServerStaticMethod&lt;uint, byte[], byte[], byte&gt; SendEnterVehicleRequest = ...Get(ReceiveEnterVehicleRequest);
    ///
    ///   - D:/Agent-工作目录/U3-SDK/Assets/Runtime/Assembly-CSharp/Unturned/Managers/VehicleManager.cs:1627-1795
    ///     [SteamCall(ESteamCallValidation.SERVERSIDE, ratelimitHz = 2, legacyName = nameof(askEnterVehicle))]
    ///     public static void ReceiveEnterVehicleRequest(in ServerInvocationContext context, uint instanceID, byte[] hash, byte[] physicsProfileHash, byte engine)
    ///     服务端处理登车请求，验证项：null player / dead / equipment busy / arena / vehicle seated / hasValidUseable / IsEquipAnimationFinished / 等
    ///     任何一项失败都会 context.LogWarning 并静默返回
    ///
    ///   1. Prefix patch VehicleManager.enterVehicle（客机端调用入口）
    ///      记录请求发送：instanceID / vehicle asset name
    ///   2. Prefix patch VehicleManager.ReceiveEnterVehicleRequest（服务端处理入口）
    ///      记录请求接收：player name / instanceID / engine
    ///      不修改 vanilla 验证逻辑（return true）
    ///
    ///   通过双机日志对比判断：
    ///     - 客机日志有 enterVehicle 但主机日志无 ReceiveEnterVehicleRequest -> 请求未发送（客户端交互层问题）
    ///     - 主机日志有 ReceiveEnterVehicleRequest 但后续 LogWarning -> 验证失败（具体失败原因在 LogWarning）
    ///     - 主机日志有 ReceiveEnterVehicleRequest 无 LogWarning 但客机无 ReceiveEnterVehicle 广播 -> 服务端未广播
    ///
    /// 安全性：
    ///   - 不修改 vanilla 验证逻辑（Prefix return true）
    ///   - 不引入新的 RPC
    ///   - 仅日志记录
    ///
    /// FACT.md 合规：
    ///   ✅ 未触碰 Dedicator.IsDedicatedServer
    ///   ✅ 未修改 vanilla IL
    ///   ✅ 仅诊断日志
    /// </summary>
    public static class VehicleEnterDiagnosticPatch
    {
        public static bool AllRegistrationsSucceeded { get; private set; }
        public static string RegistrationSummary { get; private set; } = "未登记";
        public static bool EnterVehiclePrefixRegistered { get; private set; }
        public static bool ReceiveEnterVehicleRequestPrefixRegistered { get; private set; }

        private const string HarmonyId = SteamP2PFriendsPlugin.HARMONY_ID;

        public static bool RegisterManual(Harmony harmony)
        {
            RoleLogger.Info("[Shared]",
                "[P0-C-1-V-a] === 手动登记 Prefix（v0.2.3.36 P0-C-1-V-a 客机载具登车诊断）===");

            if (harmony == null)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = "harmony=null";
                RoleLogger.Error("[Shared]", $"[P0-C-1-V-a] !!! {RegistrationSummary}");
                return false;
            }

            try
            {
                bool ok1 = RegisterEnterVehiclePrefix(harmony);
                bool ok2 = RegisterReceiveEnterVehicleRequestPrefix(harmony);

                EnterVehiclePrefixRegistered = ok1;
                ReceiveEnterVehicleRequestPrefixRegistered = ok2;

                if (!ok1 || !ok2)
                {
                    AllRegistrationsSucceeded = false;
                    RegistrationSummary = $"enterVehicle={ok1} receiveEnterVehicleRequest={ok2}";
                    RoleLogger.Error("[Shared]",
                        $"[P0-C-1-V-a] !!! DIAGNOSTIC BUILD INVALID: {RegistrationSummary}");
                    return false;
                }

                AllRegistrationsSucceeded = true;
                RegistrationSummary = $"enterVehicle={EnterVehiclePrefixRegistered} receiveEnterVehicleRequest={ReceiveEnterVehicleRequestPrefixRegistered}";
                RoleLogger.Info("[Shared]",
                    $"[P0-C-1-V-a] OK 手动登记成功 summary={RegistrationSummary}");
                return true;
            }
            catch (System.Exception ex)
            {
                AllRegistrationsSucceeded = false;
                RegistrationSummary = $"异常: {ex.Message}";
                RoleLogger.Error("[Shared]", $"[P0-C-1-V-a] !!! RegisterManual 异常: {ex}");
                return false;
            }
        }

        private static bool RegisterEnterVehiclePrefix(Harmony harmony)
        {
            try
            {
                // VehicleManager.enterVehicle 是 public static void(InteractableVehicle vehicle)
                //   U3-SDK: VehicleManager.cs:418-423
                MethodInfo original = AccessTools.Method(typeof(VehicleManager), "enterVehicle", new System.Type[] { typeof(InteractableVehicle) });
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", "[P0-C-1-V-a] !!! enterVehicle AccessTools.Method 返回 null");
                    return false;
                }

                MethodInfo prefix = AccessTools.Method(typeof(VehicleEnterDiagnosticPatch), nameof(EnterVehicle_Prefix));
                if (prefix == null)
                {
                    RoleLogger.Error("[Shared]", "[P0-C-1-V-a] !!! EnterVehicle_Prefix 方法未找到");
                    return false;
                }

                harmony.Patch(original, prefix: new HarmonyMethod(prefix));
                RoleLogger.Info("[Shared]", "[P0-C-1-V-a] OK enterVehicle Prefix 已登记");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P0-C-1-V-a] !!! RegisterEnterVehiclePrefix 异常: {ex}");
                return false;
            }
        }

        private static bool RegisterReceiveEnterVehicleRequestPrefix(Harmony harmony)
        {
            try
            {
                // VehicleManager.ReceiveEnterVehicleRequest 是 public static void(in ServerInvocationContext, uint, byte[], byte[], byte)
                //   U3-SDK: VehicleManager.cs:1627
                //   注意：in 参数是 ByRef，Harmony 参数注入按名字匹配
                MethodInfo original = AccessTools.Method(typeof(VehicleManager), "ReceiveEnterVehicleRequest");
                if (original == null)
                {
                    RoleLogger.Error("[Shared]", "[P0-C-1-V-a] !!! ReceiveEnterVehicleRequest AccessTools.Method 返回 null");
                    return false;
                }

                MethodInfo prefix = AccessTools.Method(typeof(VehicleEnterDiagnosticPatch), nameof(ReceiveEnterVehicleRequest_Prefix));
                if (prefix == null)
                {
                    RoleLogger.Error("[Shared]", "[P0-C-1-V-a] !!! ReceiveEnterVehicleRequest_Prefix 方法未找到");
                    return false;
                }

                harmony.Patch(original, prefix: new HarmonyMethod(prefix));
                RoleLogger.Info("[Shared]", "[P0-C-1-V-a] OK ReceiveEnterVehicleRequest Prefix 已登记");
                return true;
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[P0-C-1-V-a] !!! RegisterReceiveEnterVehicleRequestPrefix 异常: {ex}");
                return false;
            }
        }

        /// <summary>
        /// 客机端调用入口：VehicleManager.enterVehicle(InteractableVehicle vehicle)
        ///   U3-SDK: VehicleManager.cs:418-423
        ///   public static void enterVehicle(InteractableVehicle vehicle)
        ///   {
        ///       VehiclePhysicsProfileAsset physicsProfile = vehicle.asset.physicsProfileRef.Find();
        ///       byte[] physicsProfileHash = physicsProfile != null ? physicsProfile.hash : new byte[0];
        ///       SendEnterVehicleRequest.Invoke(ENetReliability.Unreliable, vehicle.instanceID, vehicle.asset.hash, physicsProfileHash, (byte) vehicle.asset.engine);
        ///   }
        ///
        /// Prefix 参数按名字注入：vehicle（匹配 vanilla 参数名）。
        /// return true 不修改 vanilla 逻辑。
        /// </summary>
        public static void EnterVehicle_Prefix(InteractableVehicle vehicle)
        {
            try
            {
                if (vehicle == null)
                {
                    RoleLogger.Info("[Client]", "[P0-C-1-V-a] enterVehicle 调用 vehicle=null");
                    return;
                }

                string vehicleName = vehicle.asset?.vehicleName ?? vehicle.asset?.name ?? "unknown";
                RoleLogger.Info("[Client]",
                    $"[P0-C-1-V-a] enterVehicle 请求发送 instanceID={vehicle.instanceID} vehicleName={vehicleName} engine={vehicle.asset?.engine}");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Shared]", $"[P0-C-1-V-a] EnterVehicle_Prefix 异常（不阻断）: {ex.Message}");
            }
        }

        /// <summary>
        /// 服务端处理入口：VehicleManager.ReceiveEnterVehicleRequest(in ServerInvocationContext context, uint instanceID, byte[] hash, byte[] physicsProfileHash, byte engine)
        ///   U3-SDK: VehicleManager.cs:1627-1795
        ///   [SteamCall(ESteamCallValidation.SERVERSIDE, ratelimitHz = 2, legacyName = nameof(askEnterVehicle))]
        ///   public static void ReceiveEnterVehicleRequest(in ServerInvocationContext context, uint instanceID, byte[] hash, byte[] physicsProfileHash, byte engine)
        ///
        /// Prefix 参数按名字注入：context + instanceID + engine（只声明需要的）。
        /// return true 不修改 vanilla 验证逻辑。
        /// </summary>
        public static void ReceiveEnterVehicleRequest_Prefix(
            ref SDG.Unturned.ServerInvocationContext context,
            uint instanceID,
            byte engine)
        {
            try
            {
                Player player = context.GetPlayer();
                string playerName = player?.name ?? "null";
                ulong steamId = player != null ? player.channel.owner.playerID.steamID.m_SteamID : 0UL;

                RoleLogger.Info("[Host]",
                    $"[P0-C-1-V-a] ReceiveEnterVehicleRequest 接收 player={playerName} steamId={steamId} " +
                    $"instanceID={instanceID} engine={engine}");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Warn("[Shared]", $"[P0-C-1-V-a] ReceiveEnterVehicleRequest_Prefix 异常（不阻断）: {ex.Message}");
            }
        }
    }
}
