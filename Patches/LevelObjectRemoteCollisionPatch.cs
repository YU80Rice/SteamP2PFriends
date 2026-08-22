using HarmonyLib;
using SDG.Unturned;
using SteamP2PFriends.Host;
using SteamP2PFriends.Shared;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// Keeps collision for static level objects around remote P2P guests without enabling their renderers.
    /// A listen host is a graphical client, so vanilla only keeps non-important object colliders active
    /// around the host's local region. Dedicated servers do not have this limitation.
    /// </summary>
    public static class LevelObjectRemoteCollisionPatch
    {
        private const string HarmonyId = SteamP2PFriendsPlugin.HARMONY_ID;
        private const int CoverageLogLimit = 12;

        private static readonly Dictionary<ulong, int> RemotePlayerRegions = new Dictionary<ulong, int>();
        private static readonly Dictionary<ulong, int> NextRemotePlayerRegions = new Dictionary<ulong, int>();
        private static readonly HashSet<int> RemoteCoverage = new HashSet<int>();
        private static readonly HashSet<int> NextRemoteCoverage = new HashSet<int>();
        private static readonly HashSet<int> ChangedCoverage = new HashSet<int>();
        private static readonly Dictionary<UnityEngine.Animation, UnityEngine.AnimationCullingType> RemoteAnimationCulling =
            new Dictionary<UnityEngine.Animation, UnityEngine.AnimationCullingType>();

        private static Action<LevelObject> _refreshActiveState;
        private static bool _registrationAttempted;
        private static bool _reconcileFaultLogged;
        private static bool _rootActivationFaultLogged;
        private static bool _animationPolicyFaultLogged;
        private static int _coverageLogCount;
        private static int _rootActivationLogCount;
        private static int _animationPolicyLogCount;

        public static bool AllRegistrationsSucceeded { get; private set; }
        public static bool RootActivationPostfixRegistered { get; private set; }
        public static bool RegionTrackerPostfixRegistered { get; private set; }
        public static string RegistrationSummary { get; private set; } = "未登记";

        public static bool RegisterManual(Harmony harmony)
        {
            if (_registrationAttempted)
            {
                return AllRegistrationsSucceeded;
            }

            _registrationAttempted = true;
            AllRegistrationsSucceeded = false;

            if (harmony == null)
            {
                RegistrationSummary = "harmony=null";
                RoleLogger.Error("[Shared]", "[LevelObjectCollision] !!! " + RegistrationSummary);
                return false;
            }

            try
            {
                MethodInfo refresh = AccessTools.Method(typeof(LevelObject), "UpdateActiveAndRenderersEnabled");
                MethodInfo updateActive = AccessTools.Method(typeof(LevelObject), "UpdateActiveAndRenderersEnabled");
                MethodInfo levelObjectsUpdate = AccessTools.Method(typeof(LevelObjects), "Update");
                MethodInfo rootActivationPostfix = AccessTools.Method(typeof(LevelObjectRemoteCollisionPatch), nameof(UpdateActiveAndRenderersEnabled_Postfix));
                MethodInfo regionTrackerPostfix = AccessTools.Method(typeof(LevelObjectRemoteCollisionPatch), nameof(LevelObjectsUpdate_Postfix));

                if (refresh == null || updateActive == null || levelObjectsUpdate == null || rootActivationPostfix == null || regionTrackerPostfix == null)
                {
                    RegistrationSummary = "目标或补丁方法解析失败";
                    RoleLogger.Error("[Shared]", "[LevelObjectCollision] !!! " + RegistrationSummary);
                    return false;
                }

                _refreshActiveState = (Action<LevelObject>)Delegate.CreateDelegate(typeof(Action<LevelObject>), refresh);

                // U3-SDK applies root activation before renderer activation. A last postfix can restore only the
                // root GameObject for a covered remote region, preserving vanilla's disabled renderer state.
                harmony.Patch(updateActive, postfix: new HarmonyMethod(rootActivationPostfix) { priority = Priority.Last });
                harmony.Patch(levelObjectsUpdate, postfix: new HarmonyMethod(regionTrackerPostfix));

                RootActivationPostfixRegistered = HasExactOwnPostfix(updateActive, rootActivationPostfix);
                RegionTrackerPostfixRegistered = HasExactOwnPostfix(levelObjectsUpdate, regionTrackerPostfix);
                AllRegistrationsSucceeded = RootActivationPostfixRegistered && RegionTrackerPostfixRegistered;
                RegistrationSummary = "strategy=postfix-root-reactivation+dynamic-animation, rootPostfixOwner=" +
                    RootActivationPostfixRegistered + ", updatePostfixOwner=" + RegionTrackerPostfixRegistered;

                if (AllRegistrationsSucceeded)
                {
                    RoleLogger.Info("[Shared]", "[LevelObjectCollision] OK " + RegistrationSummary);
                }
                else
                {
                    RoleLogger.Error("[Shared]", "[LevelObjectCollision] !!! DIAGNOSTIC BUILD INVALID: " + RegistrationSummary);
                }

                return AllRegistrationsSucceeded;
            }
            catch (Exception ex)
            {
                RegistrationSummary = "登记异常: " + ex.GetType().Name + ": " + ex.Message;
                RoleLogger.Error("[Shared]", "[LevelObjectCollision] !!! " + RegistrationSummary);
                return false;
            }
        }

        /// <summary>
        /// U3-SDK's UpdateActiveAndRenderersEnabled first calls SetActive(shouldGameObjectBeActive), then
        /// SetRenderersEnabled(shouldRenderersBeEnabled). When a remote collision region is covered we restore
        /// only a regular static object's root GameObject after vanilla has disabled its renderers.
        /// </summary>
        public static void UpdateActiveAndRenderersEnabled_Postfix(LevelObject __instance)
        {
            try
            {
                if (!IsRemoteCollisionCoverageRequired(__instance) || !__instance.canDamageRubble)
                {
                    RestoreRemoteAnimationPolicy(__instance);
                    return;
                }

                ObjectAsset asset = __instance.asset;
                if (asset != null && asset.type == EObjectType.NPC)
                {
                    RestoreRemoteAnimationPolicy(__instance);
                    return;
                }

                UnityEngine.Transform transform = __instance.transform;
                if (ReferenceEquals(transform, null))
                    return;

                // U3-SDK identifies decals from this child transform and gives them separate root-activation
                // semantics. They must remain entirely under vanilla visibility control.
                UnityEngine.Transform decalTransform = transform.Find("Decal");
                if (!ReferenceEquals(decalTransform, null))
                {
                    RestoreRemoteAnimationPolicy(__instance);
                    return;
                }

                UnityEngine.GameObject gameObject = transform.gameObject;
                if (ReferenceEquals(gameObject, null))
                    return;

                // Requiring a collider also excludes marker-only assets whose root activation and renderer
                // visibility must remain under vanilla control.
                UnityEngine.Collider collider = transform.GetComponentInChildren<UnityEngine.Collider>(true);
                if (ReferenceEquals(collider, null))
                {
                    RestoreRemoteAnimationPolicy(__instance);
                    return;
                }

                ApplyRemoteAnimationPolicy(__instance, transform);

                if (!gameObject.activeSelf)
                {
                    gameObject.SetActive(true);
                    if (_rootActivationLogCount < CoverageLogLimit)
                    {
                        _rootActivationLogCount++;
                        RoleLogger.Info("[Host]", "[LevelObjectCollision] root reactivated #" + _rootActivationLogCount +
                            "/" + CoverageLogLimit + " collider=" + collider.GetType().Name);
                    }
                }
            }
            catch (Exception ex)
            {
                if (_rootActivationFaultLogged)
                    return;

                _rootActivationFaultLogged = true;
                RoleLogger.Warn("[Shared]", "[LevelObjectCollision] root reactivation skipped: " + ex.GetType().Name);
            }
        }

        private static bool IsRemoteCollisionCoverageRequired(LevelObject levelObject)
        {
            if (!HostManager.IsP2PHostMode || !HostManager.ShouldProcessClientHostListen() ||
                RemoteCoverage.Count == 0 || ReferenceEquals(levelObject, null))
            {
                return false;
            }

            try
            {
                UnityEngine.Transform transform = levelObject.transform;
                if (ReferenceEquals(transform, null))
                    return false;
                byte x;
                byte y;
                if (!Regions.tryGetCoordinate(transform.position, out x, out y))
                {
                    return false;
                }

                return RemoteCoverage.Contains(EncodeRegion(x, y));
            }
            catch (Exception ex)
            {
                if (!_rootActivationFaultLogged)
                {
                    _rootActivationFaultLogged = true;
                    RoleLogger.Warn("[Shared]", "[LevelObjectCollision] coverage predicate skipped: " + ex.GetType().Name);
                }
                return false;
            }
        }

        public static void LevelObjectsUpdate_Postfix()
        {
            try
            {
                ReconcileRemoteCoverage();
            }
            catch (Exception ex)
            {
                if (_reconcileFaultLogged)
                {
                    return;
                }

                _reconcileFaultLogged = true;
                RoleLogger.Error("[Shared]", "[LevelObjectCollision] 远端区域碰撞协调异常: " + ex);
            }
        }

        public static void RemoveRemotePlayer(ulong steamId)
        {
            if (steamId == 0UL)
            {
                return;
            }

            NextRemotePlayerRegions.Remove(steamId);
            if (!RemotePlayerRegions.Remove(steamId))
            {
                return;
            }

            RebuildCoverageAndRefresh("remote-disconnect");
        }

        public static void ResetAll()
        {
            int playerCount = RemotePlayerRegions.Count;
            int regionCount = RemoteCoverage.Count;

            RemotePlayerRegions.Clear();
            NextRemotePlayerRegions.Clear();
            RebuildCoverageAndRefresh("session-reset");
            RestoreAllRemoteAnimationPolicies();

            _coverageLogCount = 0;
            _reconcileFaultLogged = false;
            _rootActivationFaultLogged = false;
            _animationPolicyFaultLogged = false;
            _rootActivationLogCount = 0;
            _animationPolicyLogCount = 0;
            RoleLogger.Info("[Shared]", "[LevelObjectCollision] ResetAll remotePlayers=" + playerCount +
                " collisionRegions=" + regionCount);
        }

        private static void ApplyRemoteAnimationPolicy(LevelObject levelObject, UnityEngine.Transform transform)
        {
            if (!(levelObject.interactable is InteractableObjectBinaryState))
            {
                RestoreRemoteAnimationPolicy(levelObject);
                return;
            }

            try
            {
                UnityEngine.Animation[] animations = transform.GetComponentsInChildren<UnityEngine.Animation>(true);
                int changed = 0;
                for (int index = 0; index < animations.Length; index++)
                {
                    UnityEngine.Animation animation = animations[index];
                    if (animation == null)
                        continue;

                    if (!RemoteAnimationCulling.ContainsKey(animation))
                    {
                        RemoteAnimationCulling.Add(animation, animation.cullingType);
                    }

                    if (animation.cullingType != UnityEngine.AnimationCullingType.AlwaysAnimate)
                    {
                        animation.cullingType = UnityEngine.AnimationCullingType.AlwaysAnimate;
                        changed++;
                    }
                }

                if (changed > 0 && _animationPolicyLogCount < CoverageLogLimit)
                {
                    _animationPolicyLogCount++;
                    RoleLogger.Info("[Host]", "[LevelObjectCollision] dynamic animation preserved #" +
                        _animationPolicyLogCount + "/" + CoverageLogLimit + " changed=" + changed +
                        " tracked=" + RemoteAnimationCulling.Count);
                }
            }
            catch (Exception ex)
            {
                LogAnimationPolicyFault("apply", ex);
            }
        }

        private static void RestoreRemoteAnimationPolicy(LevelObject levelObject)
        {
            if (RemoteAnimationCulling.Count == 0 || ReferenceEquals(levelObject, null))
                return;

            try
            {
                UnityEngine.Transform transform = levelObject.transform;
                if (transform == null)
                    return;

                UnityEngine.Animation[] animations = transform.GetComponentsInChildren<UnityEngine.Animation>(true);
                for (int index = 0; index < animations.Length; index++)
                {
                    UnityEngine.Animation animation = animations[index];
                    if (animation == null)
                        continue;

                    UnityEngine.AnimationCullingType original;
                    if (!RemoteAnimationCulling.TryGetValue(animation, out original))
                        continue;

                    try
                    {
                        animation.cullingType = original;
                        RemoteAnimationCulling.Remove(animation);
                    }
                    catch (Exception ex)
                    {
                        LogAnimationPolicyFault("restore-object", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                LogAnimationPolicyFault("restore-object", ex);
            }
        }

        private static void RestoreAllRemoteAnimationPolicies()
        {
            if (RemoteAnimationCulling.Count == 0)
                return;

            try
            {
                var animations = new List<UnityEngine.Animation>(RemoteAnimationCulling.Keys);
                for (int index = 0; index < animations.Count; index++)
                {
                    UnityEngine.Animation animation = animations[index];
                    if (animation == null)
                        continue;

                    UnityEngine.AnimationCullingType original;
                    if (RemoteAnimationCulling.TryGetValue(animation, out original))
                    {
                        try
                        {
                            animation.cullingType = original;
                        }
                        catch (Exception ex)
                        {
                            LogAnimationPolicyFault("restore-all-item", ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogAnimationPolicyFault("restore-all", ex);
            }
            finally
            {
                RemoteAnimationCulling.Clear();
            }
        }

        private static void LogAnimationPolicyFault(string stage, Exception ex)
        {
            if (_animationPolicyFaultLogged)
                return;

            _animationPolicyFaultLogged = true;
            RoleLogger.Warn("[Shared]", "[LevelObjectCollision] dynamic animation policy " + stage +
                " skipped: " + ex.GetType().Name);
        }

        private static void ReconcileRemoteCoverage()
        {
            NextRemotePlayerRegions.Clear();

            if (HostManager.IsP2PHostMode && HostManager.ShouldProcessClientHostListen() && Provider.clients != null)
            {
                int clientCount = Provider.clients.Count;
                for (int index = 0; index < clientCount && index < Provider.clients.Count; index++)
                {
                    SteamPlayer steamPlayer = Provider.clients[index];
                    ulong steamId;
                    int region;
                    if (TryGetRemotePlayerRegion(steamPlayer, out steamId, out region))
                    {
                        NextRemotePlayerRegions[steamId] = region;
                    }
                }
            }

            if (DictionariesEqual(RemotePlayerRegions, NextRemotePlayerRegions))
            {
                return;
            }

            RemotePlayerRegions.Clear();
            foreach (KeyValuePair<ulong, int> pair in NextRemotePlayerRegions)
            {
                RemotePlayerRegions.Add(pair.Key, pair.Value);
            }

            RebuildCoverageAndRefresh("remote-region-change");
        }

        private static void RebuildCoverageAndRefresh(string cause)
        {
            NextRemoteCoverage.Clear();
            foreach (int center in RemotePlayerRegions.Values)
            {
                AddCoverageAround(center, NextRemoteCoverage);
            }

            ChangedCoverage.Clear();
            foreach (int region in RemoteCoverage)
            {
                if (!NextRemoteCoverage.Contains(region))
                {
                    ChangedCoverage.Add(region);
                }
            }

            foreach (int region in NextRemoteCoverage)
            {
                if (!RemoteCoverage.Contains(region))
                {
                    ChangedCoverage.Add(region);
                }
            }

            RemoteCoverage.Clear();
            foreach (int region in NextRemoteCoverage)
            {
                RemoteCoverage.Add(region);
            }

            foreach (int region in ChangedCoverage)
            {
                RefreshObjectsInRegion(region, cause);
            }

            if (_coverageLogCount < CoverageLogLimit)
            {
                _coverageLogCount++;
                RoleLogger.Info("[Host]", "[LevelObjectCollision] coverage change #" + _coverageLogCount + "/" + CoverageLogLimit +
                    " remotePlayers=" + RemotePlayerRegions.Count + " activeRegions=" + RemoteCoverage.Count +
                    " refreshedRegions=" + ChangedCoverage.Count + " cause=" + cause);
            }
        }

        private static bool TryGetRemotePlayerRegion(SteamPlayer steamPlayer, out ulong steamId, out int region)
        {
            steamId = 0;
            region = 0;
            if (steamPlayer == null || ReferenceEquals(steamPlayer.player, null) ||
                ReferenceEquals(steamPlayer.player.channel, null))
            {
                return false;
            }

            if (steamPlayer.player.channel.IsLocalPlayer)
            {
                return false;
            }

            steamId = steamPlayer.playerID?.steamID.m_SteamID ?? 0UL;
            if (steamId == 0UL)
            {
                return false;
            }

            try
            {
                // U3-SDK PlayerMovement.updateRegionAndBound derives its region from transform.position.
                // Use the same authoritative position here instead of the cached region fields, which may lag
                // after a network state update on a graphical listen host.
                UnityEngine.Transform transform = steamPlayer.player.transform;
                if (ReferenceEquals(transform, null))
                {
                    return false;
                }

                byte x;
                byte y;
                if (!Regions.tryGetCoordinate(transform.position, out x, out y))
                {
                    return false;
                }

                region = EncodeRegion(x, y);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void AddCoverageAround(int center, HashSet<int> destination)
        {
            int worldSize = Regions.WORLD_SIZE;
            int centerX = center / worldSize;
            int centerY = center % worldSize;
            int radius = LevelObjects.OBJECT_REGIONS;

            for (int x = centerX - radius; x <= centerX + radius; x++)
            {
                for (int y = centerY - radius; y <= centerY + radius; y++)
                {
                    if (Regions.checkSafe((byte)x, (byte)y))
                    {
                        destination.Add(EncodeRegion((byte)x, (byte)y));
                    }
                }
            }
        }

        private static void RefreshObjectsInRegion(int encodedRegion, string cause)
        {
            if (_refreshActiveState == null || LevelObjects.objects == null)
            {
                return;
            }

            int worldSize = Regions.WORLD_SIZE;
            byte x = (byte)(encodedRegion / worldSize);
            byte y = (byte)(encodedRegion % worldSize);
            List<LevelObject> objects = LevelObjects.objects[x, y];
            if (objects == null)
            {
                return;
            }

            for (int index = 0; index < objects.Count; index++)
            {
                LevelObject levelObject = objects[index];
                if (levelObject != null)
                {
                    try
                    {
                        _refreshActiveState(levelObject);
                    }
                    catch (Exception ex)
                    {
                        RoleLogger.Warn("[Shared]", "[LevelObjectCollision] 刷新物件失败 region=" + encodedRegion +
                            " index=" + index + " cause=" + cause + " error=" + ex.GetType().Name);
                    }
                }
            }
        }

        private static bool DictionariesEqual(Dictionary<ulong, int> left, Dictionary<ulong, int> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            foreach (KeyValuePair<ulong, int> pair in left)
            {
                int value;
                if (!right.TryGetValue(pair.Key, out value) || value != pair.Value)
                {
                    return false;
                }
            }

            return true;
        }

        private static int EncodeRegion(byte x, byte y)
        {
            return (x * Regions.WORLD_SIZE) + y;
        }

        private static bool HasExactOwnPostfix(MethodInfo original, MethodInfo patchMethod)
        {
            HarmonyLib.Patches patchInfo = Harmony.GetPatchInfo(original);
            if (patchInfo == null)
            {
                return false;
            }

            int ownCount = 0;
            foreach (Patch patch in patchInfo.Postfixes)
            {
                if (patch.owner == HarmonyId && patch.PatchMethod == patchMethod)
                {
                    ownCount++;
                }
            }

            return ownCount == 1;
        }
    }
}
