using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using Steamworks;
using System;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// Stage 10 v5 runtime fallback for listen-host deaths.
    ///
    /// Report-2 proved that PlayerLife.onPlayerDied was not delivered even though the authoritative
    /// host PlayerLife transitioned to dead. This patch observes the already-committed result of
    /// PlayerLife.doDamage. It never changes arguments, return values, damage, death, drops, XP,
    /// respawn, or PvP behavior. The existing per-victim cooldown suppresses a duplicate when both
    /// the vanilla event and this fallback are delivered.
    /// </summary>
    [HarmonyPatch]
    internal static class P2PWorldDeathCommitPatch
    {
        internal static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(PlayerLife), "doDamage", new Type[]
            {
                typeof(byte), typeof(UnityEngine.Vector3), typeof(EDeathCause), typeof(ELimb),
                typeof(CSteamID), typeof(EPlayerKill).MakeByRefType(), typeof(bool),
                typeof(ERagdollEffect), typeof(bool)
            });
        }

        [HarmonyPrefix]
        internal static void Prefix(PlayerLife __instance, out bool __state)
        {
            __state = __instance != null && !__instance.isDead;
        }

        [HarmonyPostfix]
        internal static void Postfix(PlayerLife __instance, EDeathCause newCause,
            CSteamID newKiller, bool __state)
        {
            try
            {
                if (!ShouldForwardCommittedDeath(__state, __instance != null && __instance.health == 0))
                    return;

                RoleLogger.Info("[Host]",
                    "[WorldBroadcast] death commit observed cause=" + newCause + " health=0");
                P2PWorldStatusBroadcaster.OnAuthoritativeDeathCommitted(
                    __instance, newCause, newKiller);
            }
            catch (Exception ex)
            {
                RoleLogger.Warn("[Host]",
                    "[WorldBroadcast] death commit fallback failed: " + ex.GetType().Name);
            }
        }

        internal static bool ShouldForwardCommittedDeath(bool wasAlive, bool healthIsZeroAfter)
        {
            // doDamage commits health=0 before SendDead/ReceiveDead flips isDead. Observing
            // isDead here is therefore one network/loopback step too late.
            return wasAlive && healthIsZeroAfter;
        }
    }
}
