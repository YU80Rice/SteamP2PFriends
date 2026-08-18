using HarmonyLib;
using SDG.NetTransport;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using Steamworks;
using System.Collections.Generic;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    ///
    ///   - 补齐 PhysicsMaterialNetTable.Send（真实调用链第一项，Provider.cs:4751）。
    ///   - 补齐 AddClientToThirdpartyAntiCheat（partial method，运行时反射）。
    ///   - 所有 Finalizer 在成功时也输出 OK（原仅异常时记录）。
    /// </summary>
    public static class ProviderAcceptStageDiagnosticPatch
    {
        public static void RegisterManual(Harmony harmony)
        {
            try
            {
                // Provider.SendInitialGlobalState (private static, Provider.cs:4749)
                RegisterOne(harmony, typeof(Provider), "SendInitialGlobalState",
                    new System.Type[] { typeof(SteamPlayer) },
                    nameof(SendInitialGlobalState_Prefix), nameof(SendInitialGlobalState_Finalizer));

                // PhysicsMaterialNetTable.Send (Provider.cs:4751 调用)
                RegisterOne(harmony, typeof(PhysicsMaterialNetTable), "Send",
                    new System.Type[] { typeof(ITransportConnection) },
                    nameof(PhysicsMaterial_Send_Prefix), nameof(PhysicsMaterial_Send_Finalizer));

                // LightingManager.SendInitialGlobalState
                RegisterOne(harmony, typeof(LightingManager), "SendInitialGlobalState",
                    new System.Type[] { typeof(SteamPlayer) },
                    nameof(Lighting_SendInitial_Prefix), nameof(Lighting_SendInitial_Finalizer));

                // VehicleManager.SendInitialGlobalState
                RegisterOne(harmony, typeof(VehicleManager), "SendInitialGlobalState",
                    new System.Type[] { typeof(SteamPlayer) },
                    nameof(Vehicle_SendInitial_Prefix), nameof(Vehicle_SendInitial_Finalizer));

                // AnimalManager.SendInitialGlobalState
                RegisterOne(harmony, typeof(AnimalManager), "SendInitialGlobalState",
                    new System.Type[] { typeof(ITransportConnection) },
                    nameof(Animal_SendInitial_Prefix), nameof(Animal_SendInitial_Finalizer));

                // LevelManager.SendInitialGlobalState
                RegisterOne(harmony, typeof(LevelManager), "SendInitialGlobalState",
                    new System.Type[] { typeof(SteamPlayer) },
                    nameof(Level_SendInitial_Prefix), nameof(Level_SendInitial_Finalizer));

                // ZombieManager.SendInitialGlobalState
                RegisterOne(harmony, typeof(ZombieManager), "SendInitialGlobalState",
                    new System.Type[] { typeof(SteamPlayer) },
                    nameof(Zombie_SendInitial_Prefix), nameof(Zombie_SendInitial_Finalizer));

                // Player.SendInitialPlayerState(SteamPlayer)
                RegisterOne(harmony, typeof(Player), "SendInitialPlayerState",
                    new System.Type[] { typeof(SteamPlayer) },
                    nameof(Player_SendInitialState_Prefix), nameof(Player_SendInitialState_Finalizer));

                // Player.SendInitialPlayerState(List<ITransportConnection>)
                RegisterOne(harmony, typeof(Player), "SendInitialPlayerState",
                    new System.Type[] { typeof(List<ITransportConnection>) },
                    nameof(Player_SendInitialState_List_Prefix), nameof(Player_SendInitialState_List_Finalizer));

                // AddClientToThirdpartyAntiCheat (partial method, Provider.cs:334)
                RegisterOne(harmony, typeof(Provider), "AddClientToThirdpartyAntiCheat",
                    new System.Type[] { typeof(ITransportConnection), typeof(SteamPlayerID), typeof(SteamPlayer) },
                    nameof(AddAntiCheat_Prefix), nameof(AddAntiCheat_Finalizer));
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[ManualPatch] ProviderAcceptStageDiagnosticPatch.RegisterManual 失败: {ex}");
            }
        }

        private static void RegisterOne(Harmony harmony, System.Type targetType, string methodName,
            System.Type[] paramTypes, string prefixName, string finalizerName)
        {
            try
            {
                System.Reflection.MethodInfo original = AccessTools.Method(targetType, methodName, paramTypes);
                if (original == null)
                {
                    RoleLogger.Warn("[Shared]",
                        $"[ManualPatch] !!! {targetType.Name}.{methodName}({paramTypes.Length} args): method not found");
                    return;
                }

                HarmonyMethod prefix = null;
                HarmonyMethod finalizer = null;
                if (!string.IsNullOrEmpty(prefixName))
                {
                    System.Reflection.MethodInfo p = AccessTools.Method(typeof(ProviderAcceptStageDiagnosticPatch), prefixName);
                    if (p != null) prefix = new HarmonyMethod(p);
                }
                if (!string.IsNullOrEmpty(finalizerName))
                {
                    System.Reflection.MethodInfo f = AccessTools.Method(typeof(ProviderAcceptStageDiagnosticPatch), finalizerName);
                    if (f != null) finalizer = new HarmonyMethod(f);
                }

                harmony.Patch(original, prefix: prefix, finalizer: finalizer);

                HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
                RoleLogger.Info("[Shared]",
                    $"[ManualPatch] OK {targetType.Name}.{methodName}({paramTypes.Length} args) 已登记 " +
                    $"(prefixes={info?.Prefixes?.Count ?? 0}, finalizers={info?.Finalizers?.Count ?? 0})");
            }
            catch (System.Exception ex)
            {
                RoleLogger.Error("[Shared]", $"[ManualPatch] !!! {targetType.Name}.{methodName} 注册异常: {ex}");
            }
        }

        // ===== Provider.SendInitialGlobalState =====
        public static void SendInitialGlobalState_Prefix(SteamPlayer client)
        {
            ulong steamId = ExtractSteamId(client);
            RoleLogger.Info("[Host]",
                $"{DiagnosticContext.FormatPrefixFor(steamId, "Provider.SendInitialGlobalState ENTER")}");
        }

        public static void SendInitialGlobalState_Finalizer(SteamPlayer client, System.Exception __exception)
        {
            ulong steamId = ExtractSteamId(client);
            if (__exception != null)
            {
                RoleLogger.Error("[Host]",
                    $"{DiagnosticContext.FormatPrefixFor(steamId, "Provider.SendInitialGlobalState THREW")} " +
                    $"exceptionType={__exception.GetType().Name} message={__exception.Message}");
            }
            else
            {
                RoleLogger.Info("[Host]",
                    $"{DiagnosticContext.FormatPrefixFor(steamId, "Provider.SendInitialGlobalState OK")}");
            }
        }

        // ===== PhysicsMaterialNetTable.Send =====
        public static void PhysicsMaterial_Send_Prefix(ITransportConnection transportConnection)
        {
            string t = transportConnection?.GetType().Name ?? "null";
            RoleLogger.Info("[Host]",
                $"{DiagnosticContext.FormatPrefix("PhysicsMaterialNetTable.Send ENTER")} transport={t}");
        }

        public static void PhysicsMaterial_Send_Finalizer(ITransportConnection transportConnection, System.Exception __exception)
        {
            string t = transportConnection?.GetType().Name ?? "null";
            if (__exception != null)
            {
                RoleLogger.Error("[Host]",
                    $"{DiagnosticContext.FormatPrefix("PhysicsMaterialNetTable.Send THREW")} transport={t} " +
                    $"exceptionType={__exception.GetType().Name} message={__exception.Message}");
            }
            else
            {
                RoleLogger.Info("[Host]",
                    $"{DiagnosticContext.FormatPrefix("PhysicsMaterialNetTable.Send OK")} transport={t}");
            }
        }

        // ===== LightingManager.SendInitialGlobalState =====
        public static void Lighting_SendInitial_Prefix(SteamPlayer client)
        {
            ulong steamId = ExtractSteamId(client);
            RoleLogger.Info("[Host]",
                $"{DiagnosticContext.FormatPrefixFor(steamId, "LightingManager.SendInitialGlobalState ENTER")}");
        }

        public static void Lighting_SendInitial_Finalizer(SteamPlayer client, System.Exception __exception)
        {
            ulong steamId = ExtractSteamId(client);
            if (__exception != null)
            {
                RoleLogger.Error("[Host]",
                    $"{DiagnosticContext.FormatPrefixFor(steamId, "LightingManager.SendInitialGlobalState THREW")} " +
                    $"exceptionType={__exception.GetType().Name} message={__exception.Message}");
            }
            else
            {
                RoleLogger.Info("[Host]",
                    $"{DiagnosticContext.FormatPrefixFor(steamId, "LightingManager.SendInitialGlobalState OK")}");
            }
        }

        // ===== VehicleManager.SendInitialGlobalState =====
        public static void Vehicle_SendInitial_Prefix(SteamPlayer client)
        {
            ulong steamId = ExtractSteamId(client);
            RoleLogger.Info("[Host]",
                $"{DiagnosticContext.FormatPrefixFor(steamId, "VehicleManager.SendInitialGlobalState ENTER")}");
        }

        public static void Vehicle_SendInitial_Finalizer(SteamPlayer client, System.Exception __exception)
        {
            ulong steamId = ExtractSteamId(client);
            if (__exception != null)
            {
                RoleLogger.Error("[Host]",
                    $"{DiagnosticContext.FormatPrefixFor(steamId, "VehicleManager.SendInitialGlobalState THREW")} " +
                    $"exceptionType={__exception.GetType().Name} message={__exception.Message}");
            }
            else
            {
                RoleLogger.Info("[Host]",
                    $"{DiagnosticContext.FormatPrefixFor(steamId, "VehicleManager.SendInitialGlobalState OK")}");
            }
        }

        // ===== AnimalManager.SendInitialGlobalState =====
        public static void Animal_SendInitial_Prefix(ITransportConnection transportConnection)
        {
            string t = transportConnection?.GetType().Name ?? "null";
            RoleLogger.Info("[Host]",
                $"{DiagnosticContext.FormatPrefix("AnimalManager.SendInitialGlobalState ENTER")} transport={t}");
        }

        public static void Animal_SendInitial_Finalizer(ITransportConnection transportConnection, System.Exception __exception)
        {
            string t = transportConnection?.GetType().Name ?? "null";
            if (__exception != null)
            {
                RoleLogger.Error("[Host]",
                    $"{DiagnosticContext.FormatPrefix("AnimalManager.SendInitialGlobalState THREW")} transport={t} " +
                    $"exceptionType={__exception.GetType().Name} message={__exception.Message}");
            }
            else
            {
                RoleLogger.Info("[Host]",
                    $"{DiagnosticContext.FormatPrefix("AnimalManager.SendInitialGlobalState OK")} transport={t}");
            }
        }

        // ===== LevelManager.SendInitialGlobalState =====
        public static void Level_SendInitial_Prefix(SteamPlayer client)
        {
            ulong steamId = ExtractSteamId(client);
            RoleLogger.Info("[Host]",
                $"{DiagnosticContext.FormatPrefixFor(steamId, "LevelManager.SendInitialGlobalState ENTER")}");
        }

        public static void Level_SendInitial_Finalizer(SteamPlayer client, System.Exception __exception)
        {
            ulong steamId = ExtractSteamId(client);
            if (__exception != null)
            {
                RoleLogger.Error("[Host]",
                    $"{DiagnosticContext.FormatPrefixFor(steamId, "LevelManager.SendInitialGlobalState THREW")} " +
                    $"exceptionType={__exception.GetType().Name} message={__exception.Message}");
            }
            else
            {
                RoleLogger.Info("[Host]",
                    $"{DiagnosticContext.FormatPrefixFor(steamId, "LevelManager.SendInitialGlobalState OK")}");
            }
        }

        // ===== ZombieManager.SendInitialGlobalState =====
        public static void Zombie_SendInitial_Prefix(SteamPlayer client)
        {
            ulong steamId = ExtractSteamId(client);
            RoleLogger.Info("[Host]",
                $"{DiagnosticContext.FormatPrefixFor(steamId, "ZombieManager.SendInitialGlobalState ENTER")}");
        }

        public static void Zombie_SendInitial_Finalizer(SteamPlayer client, System.Exception __exception)
        {
            ulong steamId = ExtractSteamId(client);
            if (__exception != null)
            {
                RoleLogger.Error("[Host]",
                    $"{DiagnosticContext.FormatPrefixFor(steamId, "ZombieManager.SendInitialGlobalState THREW")} " +
                    $"exceptionType={__exception.GetType().Name} message={__exception.Message}");
            }
            else
            {
                RoleLogger.Info("[Host]",
                    $"{DiagnosticContext.FormatPrefixFor(steamId, "ZombieManager.SendInitialGlobalState OK")}");
            }
        }

        // ===== Player.SendInitialPlayerState(SteamPlayer) =====
        public static void Player_SendInitialState_Prefix(Player __instance, SteamPlayer client)
        {
            ulong targetSteamId = ExtractSteamId(client);
            ulong senderSteamId = ExtractSenderSteamId(__instance);
            RoleLogger.Info("[Host]",
                $"{DiagnosticContext.FormatPrefixFor(targetSteamId, "Player.SendInitialPlayerState(SteamPlayer) ENTER")} " +
                $"sender={senderSteamId} target={targetSteamId}");
        }

        public static void Player_SendInitialState_Finalizer(Player __instance, SteamPlayer client, System.Exception __exception)
        {
            ulong targetSteamId = ExtractSteamId(client);
            if (__exception != null)
            {
                RoleLogger.Error("[Host]",
                    $"{DiagnosticContext.FormatPrefixFor(targetSteamId, "Player.SendInitialPlayerState(SteamPlayer) THREW")} " +
                    $"exceptionType={__exception.GetType().Name} message={__exception.Message}");
            }
            else
            {
                RoleLogger.Info("[Host]",
                    $"{DiagnosticContext.FormatPrefixFor(targetSteamId, "Player.SendInitialPlayerState(SteamPlayer) OK")}");
            }
        }

        // ===== Player.SendInitialPlayerState(List<ITransportConnection>) =====
        public static void Player_SendInitialState_List_Prefix(Player __instance, List<ITransportConnection> transportConnections)
        {
            int count = transportConnections?.Count ?? -1;
            ulong senderSteamId = ExtractSenderSteamId(__instance);

            string transportTypes = "<none>";
            if (transportConnections != null && transportConnections.Count > 0)
            {
                var types = new List<string>(transportConnections.Count);
                foreach (var tc in transportConnections)
                {
                    types.Add(tc?.GetType().Name ?? "null");
                }
                transportTypes = string.Join(",", types);
            }

            RoleLogger.Info("[Host]",
                $"{DiagnosticContext.FormatPrefix("Player.SendInitialPlayerState(List) ENTER")} " +
                $"sender={senderSteamId} target_count={count} transports=[{transportTypes}]");
        }

        public static void Player_SendInitialState_List_Finalizer(Player __instance, List<ITransportConnection> transportConnections, System.Exception __exception)
        {
            if (__exception != null)
            {
                RoleLogger.Error("[Host]",
                    $"{DiagnosticContext.FormatPrefix("Player.SendInitialPlayerState(List) THREW")} " +
                    $"exceptionType={__exception.GetType().Name} message={__exception.Message}");
            }
            else
            {
                int count = transportConnections?.Count ?? -1;
                RoleLogger.Info("[Host]",
                    $"{DiagnosticContext.FormatPrefix("Player.SendInitialPlayerState(List) OK")} target_count={count}");
            }
        }

        // ===== AddClientToThirdpartyAntiCheat =====
        public static void AddAntiCheat_Prefix(ITransportConnection clientId, SteamPlayerID playerID, SteamPlayer newClient)
        {
            ulong steamId = playerID?.steamID.m_SteamID ?? 0;
            string t = clientId?.GetType().Name ?? "null";
            RoleLogger.Info("[Host]",
                $"{DiagnosticContext.FormatPrefixFor(steamId, "Provider.AddClientToThirdpartyAntiCheat ENTER")} " +
                $"steamId={steamId} transport={t}");
        }

        public static void AddAntiCheat_Finalizer(ITransportConnection clientId, SteamPlayerID playerID, SteamPlayer newClient, System.Exception __exception)
        {
            ulong steamId = playerID?.steamID.m_SteamID ?? 0;
            if (__exception != null)
            {
                RoleLogger.Error("[Host]",
                    $"{DiagnosticContext.FormatPrefixFor(steamId, "Provider.AddClientToThirdpartyAntiCheat THREW")} " +
                    $"exceptionType={__exception.GetType().Name} message={__exception.Message}");
            }
            else
            {
                RoleLogger.Info("[Host]",
                    $"{DiagnosticContext.FormatPrefixFor(steamId, "Provider.AddClientToThirdpartyAntiCheat OK")}");
            }
        }

        // ===== 辅助方法 =====
        private static ulong ExtractSteamId(SteamPlayer client)
        {
            if (ReferenceEquals(client, null) || ReferenceEquals(client.playerID, null)) return 0;
            return client.playerID.steamID.m_SteamID;
        }

        private static ulong ExtractSenderSteamId(Player player)
        {
            if (ReferenceEquals(player, null)) return 0;
            if (ReferenceEquals(player.channel?.owner?.playerID, null)) return 0;
            return player.channel.owner.playerID.steamID.m_SteamID;
        }
    }
}
