using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Host;
using System.Collections.Generic;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// Keeps the native input receive/ack pipeline alive while neutralizing gameplay fields for
    /// pending remote players. Running after ReceiveInputs preserves frame counters and parsing.
    /// </summary>
    [HarmonyPatch(typeof(PlayerInput), nameof(PlayerInput.ReceiveInputs))]
    internal static class P2PQuarantineClientInputPatch
    {
        [HarmonyPostfix]
        internal static void Postfix(PlayerInput __instance, Queue<PlayerInputPacket> ___serversidePackets)
        {
            if (!Provider.isServer || ReferenceEquals(__instance, null) ||
                ReferenceEquals(__instance.player, null) ||
                ReferenceEquals(__instance.player.channel, null) ||
                ReferenceEquals(__instance.player.channel.owner, null) ||
                ReferenceEquals(__instance.player.channel.owner.playerID, null) ||
                __instance.player.channel.IsLocalPlayer || ReferenceEquals(___serversidePackets, null) ||
                !P2PApprovalManager.IsPending(__instance.player.channel.owner.playerID.steamID)) return;

            foreach (PlayerInputPacket packet in ___serversidePackets)
                NeutralizePacket(packet, __instance.player.transform.position);
        }

        internal static void NeutralizePacket(PlayerInputPacket packet, UnityEngine.Vector3 authoritativePosition)
        {
            if (packet == null) return;
            packet.keys = 0;
            packet.primaryAttack = EAttackInputFlags.None;
            packet.secondaryAttack = EAttackInputFlags.None;
            packet.clientsideInputs?.Clear();
            packet.serversideInputs?.Clear();
            if (packet is WalkingPlayerInputPacket walking)
            {
                walking.analog = 0x11;
                walking.clientPosition = authoritativePosition;
            }
        }
    }
}
