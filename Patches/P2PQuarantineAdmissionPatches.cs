using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// Server-authoritative quarantine gate for gameplay mutations. Unlike the former blanket
    /// InvokeMethod gate, these targets exclude handshake and world-loading RPCs.
    /// </summary>
    internal static class P2PQuarantineActionGatePatch
    {
        internal const int MinimumExpectedContextTargetCount = 19;

        internal static bool RegistrationValid { get; private set; }

        internal static void RegisterManual(Harmony harmony)
        {
            RegistrationValid = false;
            if (harmony == null) return;
            MethodInfo contextPrefix = AccessTools.Method(typeof(P2PQuarantineActionGatePatch), nameof(ContextPrefix));
            MethodInfo ownerPrefix = AccessTools.Method(typeof(P2PQuarantineActionGatePatch), nameof(OwnerPrefix));
            var contextMethods = new HashSet<MethodInfo>();
            var ownerMethods = new HashSet<MethodInfo>();
            int installed = 0;
            Type[] types;
            try { types = typeof(Player).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }

            foreach (Type type in types)
            {
                if (type == null) continue;
                CollectTargets(type, contextMethods, ownerMethods);
            }

            // ReflectionTypeLoadException in stripped or test environments must not erase the
            // critical gameplay boundary. These types are compile-time dependencies and mandatory.
            Type[] criticalTypes =
            {
                typeof(PlayerInput), typeof(PlayerInventory), typeof(PlayerEquipment),
                typeof(ItemManager), typeof(BarricadeDrop), typeof(StructureDrop),
                typeof(InteractableStorage), typeof(ResourceManager), typeof(InteractableFarm),
                typeof(InteractableDoor), typeof(InteractableFire), typeof(InteractableGenerator),
                typeof(InteractableOven), typeof(InteractableSign), typeof(InteractableLibrary),
                typeof(InteractableMannequin), typeof(InteractableStereo), typeof(InteractableBed),
                typeof(InteractableOxygenator), typeof(InteractableSafezone), typeof(InteractableSpot),
                typeof(ObjectManager), typeof(VehicleManager), typeof(UseableBarricade),
                typeof(UseableStructure), typeof(UseableHousingPlanner), typeof(UseableGun),
                typeof(UseableFisher), typeof(UseableMelee), typeof(PlayerCrafting),
                typeof(PlayerQuests), typeof(PlayerStance), typeof(PlayerClothing),
                typeof(PlayerAnimator), typeof(PlayerLife), typeof(PlayerInteract)
            };
            foreach (Type type in criticalTypes) CollectTargets(type, contextMethods, ownerMethods);

            foreach (MethodInfo method in contextMethods)
            {
                harmony.Patch(method, prefix: new HarmonyMethod(contextPrefix));
                if (HasOwnedPrefix(method, contextPrefix)) installed++;
            }
            foreach (MethodInfo method in ownerMethods)
            {
                harmony.Patch(method, prefix: new HarmonyMethod(ownerPrefix));
                if (HasOwnedPrefix(method, ownerPrefix)) installed++;
            }

            int contextTargets = contextMethods.Count;
            int ownerTargets = ownerMethods.Count;
            int discovered = contextTargets + ownerTargets;
            RegistrationValid = contextTargets >= MinimumExpectedContextTargetCount &&
                ownerTargets > 0 && installed == discovered;
            if (RegistrationValid)
                RoleLogger.Info("[Shared]", "[P2P-Quarantine] parsed gameplay RPC targets gated=" + installed +
                    " context=" + contextTargets + " owner=" + ownerTargets);
            else
                RoleLogger.Error("[Shared]", "[P2P-Quarantine] parsed gameplay RPC gate failed installed=" +
                    installed + "/" + discovered + " context=" + contextTargets + " owner=" + ownerTargets);
        }

        private static void CollectTargets(Type type, HashSet<MethodInfo> contextMethods,
            HashSet<MethodInfo> ownerMethods)
        {
            if (type == null) return;
            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(type))
            {
                if (IsBlockedContextTarget(method)) contextMethods.Add(method);
                else if (IsBlockedOwnerTarget(method)) ownerMethods.Add(method);
            }
        }

        internal static bool IsBlockedContextTarget(MethodInfo method)
        {
            if (method == null || method.Name.EndsWith("_Read", StringComparison.Ordinal) ||
                !method.Name.StartsWith("Receive", StringComparison.Ordinal))
                return false;
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length == 0 ||
                parameters[0].ParameterType != typeof(ServerInvocationContext).MakeByRefType()) return false;

            SteamCall steamCall = method.GetCustomAttribute<SteamCall>(false);
            if (steamCall == null || (steamCall.validation != ESteamCallValidation.SERVERSIDE &&
                steamCall.validation != ESteamCallValidation.ONLY_FROM_OWNER)) return false;

            // Always consume player-input payloads and chat at the generated reader layer. Remote
            // pending input is neutralized after PlayerInput.ReceiveInputs has parsed and queued it,
            // while chat commands are filtered by P2PListenHostCommandPermissionPatch.
            if (method.DeclaringType == typeof(PlayerInput) && method.Name == "ReceiveInputs") return false;
            if (method.DeclaringType == typeof(ChatManager) && method.Name == "ReceiveChatRequest") return false;
            return !(method.DeclaringType == typeof(SteamPlayer) &&
                method.Name == "ReceiveGetSteamAuthTicketForWebApiResponse");
        }

        internal static bool IsBlockedOwnerTarget(MethodInfo method)
        {
            if (method == null || method.IsStatic ||
                !method.Name.StartsWith("Receive", StringComparison.Ordinal)) return false;
            ParameterInfo[] parameters = method.GetParameters();
            // Context-bearing readers are handled by the generated reader layer. In particular,
            // PlayerInput.ReceiveInputs must remain live so the native input/ack pipeline can run.
            if (parameters.Length > 0 &&
                parameters[0].ParameterType == typeof(ServerInvocationContext).MakeByRefType())
                return false;
            if (method.DeclaringType == typeof(PlayerInput) && method.Name == "ReceiveInputs")
                return false;
            Type declaringType = method.DeclaringType;
            if (declaringType == null || AccessTools.Property(declaringType, "player") == null) return false;
            SteamCall steamCall = method.GetCustomAttribute<SteamCall>(false);
            return steamCall != null && steamCall.validation == ESteamCallValidation.ONLY_FROM_OWNER;
        }

        private static bool HasOwnedPrefix(MethodBase original, MethodInfo prefix)
        {
            HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
            if (info == null) return false;
            foreach (Patch patch in info.Prefixes)
                if (patch.owner == SteamP2PFriendsPlugin.HARMONY_ID && patch.PatchMethod == prefix) return true;
            return false;
        }

        internal static bool ContextPrefix(in ServerInvocationContext context)
        {
            return context.origin != ServerInvocationContext.EOrigin.Remote ||
                !ShouldBlock(context.GetCallingPlayer());
        }

        internal static bool OwnerPrefix(object __instance)
        {
            return !ShouldBlock(ExtractOwner(__instance));
        }

        private static SteamPlayer ExtractOwner(object instance)
        {
            if (instance == null) return null;
            try
            {
                PropertyInfo property = AccessTools.Property(instance.GetType(), "player");
                Player player = property?.GetValue(instance, null) as Player;
                return player?.channel?.owner;
            }
            catch { return null; }
        }

        private static bool ShouldBlock(SteamPlayer caller)
        {
            if (!HostManager.IsP2PHostMode || !Provider.isServer ||
                ReferenceEquals(caller, null) || ReferenceEquals(caller.playerID, null)) return false;
            if (caller.player?.channel?.IsLocalPlayer == true) return false;
            return P2PApprovalManager.IsPending(caller.playerID.steamID);
        }

        internal static bool ShouldBlockForTest(bool isP2PHost, bool isServer, bool isLocalHost, bool isPending)
        {
            return isP2PHost && isServer && !isLocalHost && isPending;
        }
    }

    [HarmonyPatch]
    internal static class P2PQuarantineDamageGuardPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(PlayerLife), nameof(PlayerLife.askDamage), new Type[]
            {
                typeof(byte), typeof(UnityEngine.Vector3), typeof(EDeathCause), typeof(ELimb),
                typeof(CSteamID), typeof(EPlayerKill).MakeByRefType(), typeof(bool),
                typeof(ERagdollEffect), typeof(bool), typeof(bool)
            });
        }

        [HarmonyPrefix]
        internal static bool Prefix(PlayerLife __instance, ref EPlayerKill kill)
        {
            kill = EPlayerKill.NONE;
            if (!Provider.isServer || __instance == null || __instance.player == null ||
                __instance.player.channel == null || __instance.player.channel.owner == null)
                return true;

            CSteamID target = __instance.player.channel.owner.playerID.steamID;
            return !P2PApprovalManager.IsPending(target);
        }
    }
}
