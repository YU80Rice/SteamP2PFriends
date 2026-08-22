using HarmonyLib;
using SDG.NetTransport;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// Read-only Issue #7 trace for ObjectManager Binary_State interactables.
    /// The Debug build logs the native request, authoritative decision, target gathering,
    /// receive/application, object activation/collision, and nearby misprediction/teleport correction paths.
    /// Release builds deliberately register no hooks.
    /// </summary>
    public static class Issue7ObjectBinaryStateDiagnosticPatch
    {
#if DEBUG
        private const int SessionLogLimit = 400;
        private const float CorrelationWindowSeconds = 15f;
        private const float CollisionLogIntervalSeconds = 1f;

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, float> TrackedObjects = new Dictionary<string, float>();
        private static readonly Dictionary<string, string> LastActivationFingerprints = new Dictionary<string, string>();
        private static readonly Dictionary<string, float> LastCollisionLogTimes = new Dictionary<string, float>();
        private static readonly FieldInfo ObjectRegionsField = AccessTools.Field(typeof(ObjectManager), "regions");
        private static readonly PropertyInfo IsActiveInRegionProperty = AccessTools.Property(typeof(LevelObject), "isActiveInRegion");

        [ThreadStatic]
        private static AuthorityTrace _currentAuthorityTrace;

        private static int _sessionLogCount;
        private static long _nextSequence;
        private static float _lastActivityTime = -100f;
        private static bool _registrationAttempted;
        private static bool _resetCallbackRegistered;

        private static readonly Type[] ToggleParameters = { typeof(Transform), typeof(bool) };
        private static readonly Type[] AuthorityParameters =
        {
            typeof(ServerInvocationContext).MakeByRefType(), typeof(byte), typeof(byte), typeof(ushort), typeof(bool)
        };
        private static readonly Type[] GatherParameters = { typeof(byte), typeof(byte) };
        private static readonly Type[] ReceiveParameters = { typeof(byte), typeof(byte), typeof(ushort), typeof(bool) };
        private static readonly Type[] TeleportParameters = { typeof(Vector3), typeof(byte) };
        private static readonly Type[] ControllerHitParameters = { typeof(ControllerColliderHit) };
        private static readonly Type[] MispredictionParameters =
        {
            typeof(uint), typeof(EPlayerStance), typeof(Vector3), typeof(Vector3), typeof(byte), typeof(int), typeof(int)
        };

        private sealed class AuthorityTrace
        {
            internal long Sequence;
            internal byte X;
            internal byte Y;
            internal ushort Index;
            internal bool Desired;
            internal bool BeforeObjectPresent;
            internal bool BeforeInteractablePresent;
            internal bool BeforeUsed;
            internal byte? BeforeStateByte;
            internal string PredictedDecision;
            internal int RecipientCount = -1;
        }
#endif

        public static bool AllRegistrationsSucceeded { get; private set; }
        public static string RegistrationSummary { get; private set; } = "not attempted";

        public static bool RegisterManual(Harmony harmony)
        {
#if !DEBUG
            AllRegistrationsSucceeded = true;
            RegistrationSummary = "release-noop";
            return true;
#else
            if (_registrationAttempted)
                return AllRegistrationsSucceeded;

            _registrationAttempted = true;
            bool all = harmony != null;
            if (harmony == null)
            {
                RegistrationSummary = "harmony=null";
                RoleLogger.Error("[Shared]", "[Issue7/ObjectBinary] registration failed: harmony=null");
                return false;
            }

            all &= Register(harmony, typeof(ObjectManager), "toggleObjectBinaryState", ToggleParameters,
                nameof(ToggleObjectBinaryState_Prefix), HarmonyPatchType.Prefix);
            all &= Register(harmony, typeof(ObjectManager), "ReceiveToggleObjectBinaryStateRequest", AuthorityParameters,
                nameof(Authority_Prefix), HarmonyPatchType.Prefix);
            all &= Register(harmony, typeof(ObjectManager), "ReceiveToggleObjectBinaryStateRequest", AuthorityParameters,
                nameof(Authority_Postfix), HarmonyPatchType.Postfix);
            all &= Register(harmony, typeof(ObjectManager), "ReceiveToggleObjectBinaryStateRequest", AuthorityParameters,
                nameof(Authority_Finalizer), HarmonyPatchType.Finalizer);
            all &= Register(harmony, typeof(ObjectManager), "GatherRemoteClientConnections", GatherParameters,
                nameof(GatherRecipients_Postfix), HarmonyPatchType.Postfix);
            all &= Register(harmony, typeof(ObjectManager), "ReceiveObjectBinaryState", ReceiveParameters,
                nameof(Receive_Prefix), HarmonyPatchType.Prefix);
            all &= Register(harmony, typeof(ObjectManager), "ReceiveObjectBinaryState", ReceiveParameters,
                nameof(Receive_Postfix), HarmonyPatchType.Postfix);
            all &= Register(harmony, typeof(ObjectManager), "ReceiveObjectBinaryState", ReceiveParameters,
                nameof(Receive_Finalizer), HarmonyPatchType.Finalizer);
            all &= Register(harmony, typeof(LevelObject), "UpdateActiveAndRenderersEnabled", Type.EmptyTypes,
                nameof(Activation_Postfix), HarmonyPatchType.Postfix);
            all &= Register(harmony, typeof(Player), "ReceiveTeleport", TeleportParameters,
                nameof(ReceiveTeleport_Prefix), HarmonyPatchType.Prefix);
            all &= Register(harmony, typeof(PlayerMovement), "OnControllerColliderHit", ControllerHitParameters,
                nameof(ControllerColliderHit_Prefix), HarmonyPatchType.Prefix);
            all &= Register(harmony, typeof(PlayerInput), "ReceiveSimulateMispredictedInputs", MispredictionParameters,
                nameof(Misprediction_Prefix), HarmonyPatchType.Prefix);

            if (!_resetCallbackRegistered)
            {
                WorldSyncDiagnosticCore.RegisterSessionResetCallback(ResetForSession);
                _resetCallbackRegistered = true;
            }

            AllRegistrationsSucceeded = all;
            RegistrationSummary = all ? "debug-hooks=12/12 reset=true" : "one-or-more-debug-hooks-missing";
            if (all)
                RoleLogger.Info("[Shared]", "[Issue7/ObjectBinary] debug registration OK " + RegistrationSummary);
            else
                RoleLogger.Error("[Shared]", "[Issue7/ObjectBinary] DIAGNOSTIC BUILD INVALID: " + RegistrationSummary);
            return all;
#endif
        }

        public static bool VerifyRegistration()
        {
#if !DEBUG
            return true;
#else
            if (!AllRegistrationsSucceeded || !_resetCallbackRegistered)
                return false;

            bool all = true;
            all &= Verify(typeof(ObjectManager), "toggleObjectBinaryState", ToggleParameters,
                nameof(ToggleObjectBinaryState_Prefix), HarmonyPatchType.Prefix);
            all &= Verify(typeof(ObjectManager), "ReceiveToggleObjectBinaryStateRequest", AuthorityParameters,
                nameof(Authority_Prefix), HarmonyPatchType.Prefix);
            all &= Verify(typeof(ObjectManager), "ReceiveToggleObjectBinaryStateRequest", AuthorityParameters,
                nameof(Authority_Postfix), HarmonyPatchType.Postfix);
            all &= Verify(typeof(ObjectManager), "ReceiveToggleObjectBinaryStateRequest", AuthorityParameters,
                nameof(Authority_Finalizer), HarmonyPatchType.Finalizer);
            all &= Verify(typeof(ObjectManager), "GatherRemoteClientConnections", GatherParameters,
                nameof(GatherRecipients_Postfix), HarmonyPatchType.Postfix);
            all &= Verify(typeof(ObjectManager), "ReceiveObjectBinaryState", ReceiveParameters,
                nameof(Receive_Prefix), HarmonyPatchType.Prefix);
            all &= Verify(typeof(ObjectManager), "ReceiveObjectBinaryState", ReceiveParameters,
                nameof(Receive_Postfix), HarmonyPatchType.Postfix);
            all &= Verify(typeof(ObjectManager), "ReceiveObjectBinaryState", ReceiveParameters,
                nameof(Receive_Finalizer), HarmonyPatchType.Finalizer);
            all &= Verify(typeof(LevelObject), "UpdateActiveAndRenderersEnabled", Type.EmptyTypes,
                nameof(Activation_Postfix), HarmonyPatchType.Postfix);
            all &= Verify(typeof(Player), "ReceiveTeleport", TeleportParameters,
                nameof(ReceiveTeleport_Prefix), HarmonyPatchType.Prefix);
            all &= Verify(typeof(PlayerMovement), "OnControllerColliderHit", ControllerHitParameters,
                nameof(ControllerColliderHit_Prefix), HarmonyPatchType.Prefix);
            all &= Verify(typeof(PlayerInput), "ReceiveSimulateMispredictedInputs", MispredictionParameters,
                nameof(Misprediction_Prefix), HarmonyPatchType.Prefix);

            RoleLogger.Info("[Shared]", "[Issue7/ObjectBinary] VerifyRegistration all=" + all + " " + RegistrationSummary);
            return all;
#endif
        }

#if DEBUG
        public static void ToggleObjectBinaryState_Prefix(Transform transform, bool isUsed)
        {
            try
            {
                long sequence = NextSequence();
                if (ReferenceEquals(transform, null))
                {
                    Log(sequence, "client-request", "transform=null desired=" + isUsed);
                    return;
                }

                if (!ObjectManager.tryGetRegion(transform, out byte x, out byte y, out ushort index))
                {
                    Log(sequence, "client-request", "mapping=failed desired=" + isUsed + " pos=" + FormatVector(transform.position));
                    return;
                }

                TrackObject(x, y, index);
                TouchActivity();
                Log(sequence, "client-request", Describe(x, y, index, isUsed, "mapping=ok"));
            }
            catch (Exception ex)
            {
                Log(0, "client-request-exception", ex.GetType().Name + ":" + ex.Message);
            }
        }

        public static void Authority_Prefix(in ServerInvocationContext context, byte x, byte y, ushort index, bool isUsed)
        {
            try
            {
                long sequence = NextSequence();
                AuthorityTrace trace = new AuthorityTrace
                {
                    Sequence = sequence,
                    X = x,
                    Y = y,
                    Index = index,
                    Desired = isUsed
                };

                Player player = context.GetPlayer();
                LevelObject levelObject;
                InteractableObjectBinaryState interactable;
                TryGetBinaryObject(x, y, index, out levelObject, out interactable);
                trace.BeforeObjectPresent = levelObject != null;
                trace.BeforeInteractablePresent = interactable != null;
                trace.BeforeUsed = interactable != null && interactable.isUsed;
                trace.BeforeStateByte = GetStateByte(levelObject);
                trace.PredictedDecision = PredictAuthorityDecision(player, x, y, index, isUsed, levelObject, interactable);
                _currentAuthorityTrace = trace;

                TrackObject(x, y, index);
                TouchActivity();
                string caller = MaskPlayer(player);
                string playerPosition = player != null && !ReferenceEquals(player.transform, null)
                    ? FormatVector(player.transform.position)
                    : "null";
                Log(sequence, "authority-request",
                    Describe(x, y, index, isUsed,
                        "origin=" + context.origin + " caller=" + caller + " playerPos=" + playerPosition +
                        " predicted=" + trace.PredictedDecision));
            }
            catch (Exception ex)
            {
                _currentAuthorityTrace = null;
                Log(0, "authority-prefix-exception", ex.GetType().Name + ":" + ex.Message);
            }
        }

        public static void GatherRecipients_Postfix(byte x, byte y, PooledTransportConnectionList __result)
        {
            try
            {
                AuthorityTrace trace = _currentAuthorityTrace;
                if (trace == null || trace.X != x || trace.Y != y)
                    return;

                trace.RecipientCount = __result?.Count ?? -1;
                string types = DescribeConnectionTypes(__result);
                Log(trace.Sequence, "authority-broadcast-targets",
                    "region=" + x + "," + y + " recipients=" + trace.RecipientCount + " types=" + types);
            }
            catch (Exception ex)
            {
                Log(0, "authority-target-exception", ex.GetType().Name + ":" + ex.Message);
            }
        }

        public static void Authority_Postfix()
        {
            AuthorityTrace trace = _currentAuthorityTrace;
            if (trace == null)
                return;

            try
            {
                LevelObject levelObject;
                InteractableObjectBinaryState interactable;
                TryGetBinaryObject(trace.X, trace.Y, trace.Index, out levelObject, out interactable);
                bool afterUsed = interactable != null && interactable.isUsed;
                byte? afterStateByte = GetStateByte(levelObject);
                bool changed = trace.BeforeUsed != afterUsed || trace.BeforeStateByte != afterStateByte;
                string outcome;
                if (!string.Equals(trace.PredictedDecision, "eligible", StringComparison.Ordinal))
                {
                    outcome = trace.PredictedDecision == "reject:already-desired"
                        ? "no-op:already-desired"
                        : trace.PredictedDecision;
                }
                else if (!trace.BeforeObjectPresent || !trace.BeforeInteractablePresent || interactable == null)
                {
                    outcome = "returned-without-commit:object-unavailable";
                }
                else if (changed && afterUsed == trace.Desired)
                {
                    outcome = "committed";
                }
                else
                {
                    outcome = "returned-without-commit";
                }
                Log(trace.Sequence, "authority-result",
                    Describe(trace.X, trace.Y, trace.Index, trace.Desired,
                        "beforeUsed=" + trace.BeforeUsed + " afterUsed=" + afterUsed +
                        " beforeState=" + FormatNullableByte(trace.BeforeStateByte) +
                        " afterState=" + FormatNullableByte(afterStateByte) +
                        " changed=" + changed + " recipients=" + trace.RecipientCount +
                        " outcome=" + outcome + " predicted=" + trace.PredictedDecision));
            }
            catch (Exception ex)
            {
                Log(trace.Sequence, "authority-postfix-exception", ex.GetType().Name + ":" + ex.Message);
            }
            finally
            {
                _currentAuthorityTrace = null;
            }
        }

        public static Exception Authority_Finalizer(Exception __exception)
        {
            AuthorityTrace trace = _currentAuthorityTrace;
            if (__exception != null)
            {
                Log(trace?.Sequence ?? 0, "authority-exception", __exception.GetType().Name + ":" + __exception.Message);
            }
            _currentAuthorityTrace = null;
            return __exception;
        }

        public static void Receive_Prefix(byte x, byte y, ushort index, bool isUsed, ref string __state)
        {
            try
            {
                long sequence = NextSequence();
                __state = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
                TrackObject(x, y, index);
                TouchActivity();
                Log(sequence, "receive-before", Describe(x, y, index, isUsed,
                    "gate=" + PredictReceiveDecision(x, y, index)));
            }
            catch (Exception ex)
            {
                Log(0, "receive-prefix-exception", ex.GetType().Name + ":" + ex.Message);
            }
        }

        public static void Receive_Postfix(byte x, byte y, ushort index, bool isUsed, string __state)
        {
            long sequence = ParseSequence(__state);
            try
            {
                Log(sequence, "receive-after", Describe(x, y, index, isUsed, "applied-check"));
            }
            catch (Exception ex)
            {
                Log(sequence, "receive-postfix-exception", ex.GetType().Name + ":" + ex.Message);
            }
        }

        public static Exception Receive_Finalizer(Exception __exception, string __state)
        {
            if (__exception != null)
            {
                Log(ParseSequence(__state), "receive-exception", __exception.GetType().Name + ":" + __exception.Message);
            }
            return __exception;
        }

        [HarmonyPriority(Priority.Last)]
        public static void Activation_Postfix(LevelObject __instance)
        {
            try
            {
                if (ReferenceEquals(__instance, null) || !TryGetTrackedObjectKey(__instance, out string key))
                    return;

                string fingerprint = DescribeActivation(__instance);
                lock (Sync)
                {
                    if (LastActivationFingerprints.TryGetValue(key, out string previous) && previous == fingerprint)
                        return;
                    LastActivationFingerprints[key] = fingerprint;
                }
                Log(NextSequence(), "activation-change", "key=" + key + " " + fingerprint);
            }
            catch (Exception ex)
            {
                Log(0, "activation-exception", ex.GetType().Name + ":" + ex.Message);
            }
        }

        public static void ReceiveTeleport_Prefix(Player __instance, Vector3 position, byte angle)
        {
            try
            {
                if (Time.realtimeSinceStartup - _lastActivityTime > CorrelationWindowSeconds)
                    return;

                Vector3 before = __instance != null && !ReferenceEquals(__instance.transform, null)
                    ? __instance.transform.position
                    : Vector3.zero;
                float delta = Vector3.Distance(before, position);
                string beforeRegion = FormatRegion(before);
                string afterRegion = FormatRegion(position);
                bool isLocal = __instance?.channel?.IsLocalPlayer ?? false;
                Log(NextSequence(), "receive-teleport-window",
                    "receiver=" + MaskPlayer(__instance) + " isLocal=" + isLocal +
                    " before=" + FormatVector(before) + " requested=" + FormatVector(position) +
                    " delta=" + delta.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                    " beforeRegion=" + beforeRegion + " newRegion=" + afterRegion + " angle=" + angle);
            }
            catch (Exception ex)
            {
                Log(0, "receive-teleport-exception", ex.GetType().Name + ":" + ex.Message);
            }
        }

        public static void ControllerColliderHit_Prefix(PlayerMovement __instance, ControllerColliderHit hit)
        {
            try
            {
                if (Time.realtimeSinceStartup - _lastActivityTime > CorrelationWindowSeconds || hit == null || hit.collider == null)
                    return;
                if (!TryMatchTrackedCollider(hit.collider.transform, out string key))
                    return;

                Player player = __instance?.player;
                bool isLocal = player?.channel?.IsLocalPlayer ?? false;
                string throttleKey = key + "|" + (__instance != null ? __instance.GetInstanceID() : 0);
                float now = Time.realtimeSinceStartup;
                lock (Sync)
                {
                    if (LastCollisionLogTimes.TryGetValue(throttleKey, out float previous) &&
                        now - previous < CollisionLogIntervalSeconds)
                        return;
                    LastCollisionLogTimes[throttleKey] = now;
                }
                Log(NextSequence(), "controller-collision-window",
                    "player=" + MaskPlayer(player) + " isLocal=" + isLocal + " key=" + key +
                    " collider=" + hit.collider.GetType().Name + " colliderEnabled=" + hit.collider.enabled +
                    " colliderActive=" + hit.collider.gameObject.activeInHierarchy +
                    " point=" + FormatVector(hit.point) + " normal=" + FormatVector(hit.normal) +
                    " moveDirection=" + FormatVector(hit.moveDirection) +
                    " moveLength=" + hit.moveLength.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                Log(0, "controller-collision-exception", ex.GetType().Name + ":" + ex.Message);
            }
        }

        public static void Misprediction_Prefix(PlayerInput __instance, uint frameNumber, EPlayerStance stance,
            Vector3 position, Vector3 velocity, byte stamina, int lastTireOffset, int lastRestOffset)
        {
            try
            {
                if (Time.realtimeSinceStartup - _lastActivityTime > CorrelationWindowSeconds)
                    return;

                Player player = __instance?.player;
                Vector3 current = player != null && !ReferenceEquals(player.transform, null)
                    ? player.transform.position
                    : Vector3.zero;
                float delta = Vector3.Distance(current, position);
                bool isLocal = player?.channel?.IsLocalPlayer ?? false;
                Log(NextSequence(), "misprediction-correction-window",
                    "player=" + MaskPlayer(player) + " isLocal=" + isLocal + " frame=" + frameNumber +
                    " current=" + FormatVector(current) + " authoritative=" + FormatVector(position) +
                    " delta=" + delta.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                    " currentRegion=" + FormatRegion(current) + " authoritativeRegion=" + FormatRegion(position) +
                    " velocity=" + FormatVector(velocity) + " stance=" + stance + " stamina=" + stamina +
                    " lastTireOffset=" + lastTireOffset + " lastRestOffset=" + lastRestOffset);
            }
            catch (Exception ex)
            {
                Log(0, "misprediction-exception", ex.GetType().Name + ":" + ex.Message);
            }
        }

        private static bool Register(Harmony harmony, Type targetType, string targetName, Type[] parameters,
            string patchName, HarmonyPatchType patchType)
        {
            MethodInfo patch = AccessTools.Method(typeof(Issue7ObjectBinaryStateDiagnosticPatch), patchName);
            return WorldSyncDiagnosticCore.RegisterIdentityPatch(harmony, targetType, targetName, parameters,
                patch, patchType, "Issue7." + targetType.Name + "." + targetName + "." + patchName);
        }

        private static bool Verify(Type targetType, string targetName, Type[] parameters,
            string patchName, HarmonyPatchType patchType)
        {
            MethodInfo patch = AccessTools.Method(typeof(Issue7ObjectBinaryStateDiagnosticPatch), patchName);
            return WorldSyncDiagnosticCore.IsPatchRegistered(targetType, targetName, patch, patchType, parameters);
        }

        private static string PredictAuthorityDecision(Player player, byte x, byte y, ushort index, bool desired,
            LevelObject levelObject, InteractableObjectBinaryState interactable)
        {
            if (!Regions.checkSafe(x, y)) return "reject:unsafe-region";
            if (player == null) return "reject:no-player";
            if (player.life == null || player.life.isDead) return "reject:dead-player";
            if (LevelObjects.objects == null || LevelObjects.objects[x, y] == null || index >= LevelObjects.objects[x, y].Count)
                return "reject:index-out-of-range";
            if (levelObject == null) return "reject:null-object";
            if (ReferenceEquals(levelObject.transform, null)) return "reject:null-transform";
            if (interactable == null) return "reject:not-binary-state";
            if (!interactable.isUsable) return "reject:not-usable";
            if (interactable.isUsed == desired) return "reject:already-desired";

            if (interactable.modHookCounter <= 0)
            {
                if ((levelObject.transform.position - player.transform.position).sqrMagnitude > 400f)
                    return "reject:distance-over-20m";
                if (interactable.objectAsset.interactabilityRemote)
                    return "reject:remote-disabled";
            }

            if (!interactable.objectAsset.areConditionsMet(player)) return "reject:conditions";
            if (!interactable.objectAsset.areInteractabilityConditionsMet(player)) return "reject:interactability-conditions";
            return "eligible";
        }

        private static string PredictReceiveDecision(byte x, byte y, ushort index)
        {
            if (!Regions.checkSafe(x, y)) return "reject:unsafe-region";
            if (!Dedicator.IsDedicatedServer && !Provider.isServer && !IsRegionNetworked(x, y))
                return "reject:region-not-networked";
            if (LevelObjects.objects == null || LevelObjects.objects[x, y] == null || index >= LevelObjects.objects[x, y].Count)
                return "reject:index-out-of-range";
            LevelObject levelObject = LevelObjects.objects[x, y][index];
            if (levelObject == null) return "reject:null-object";
            if (!(levelObject.interactable is InteractableObjectBinaryState)) return "reject:not-binary-state";
            return "eligible";
        }

        private static string Describe(byte x, byte y, ushort index, bool desired, string extra)
        {
            TryGetBinaryObject(x, y, index, out LevelObject levelObject, out InteractableObjectBinaryState interactable);
            string guid = levelObject?.GUID.ToString("N") ?? "null";
            string assetGuid = levelObject?.asset?.GUID.ToString("N") ?? "null";
            string instanceId = levelObject != null ? levelObject.instanceID.ToString() : "null";
            string used = interactable != null ? interactable.isUsed.ToString() : "null";
            string usable = interactable != null ? interactable.isUsable.ToString() : "null";
            string state = FormatNullableByte(GetStateByte(levelObject));
            string position = levelObject != null && !ReferenceEquals(levelObject.transform, null)
                ? FormatVector(levelObject.transform.position)
                : "null";
            string activation = levelObject != null ? DescribeActivation(levelObject) : "activation=null";
            return "role=" + GetRole() + " region=" + x + "," + y + " index=" + index +
                " guid=" + guid + " assetGuid=" + assetGuid + " instanceId=" + instanceId +
                " desired=" + desired + " used=" + used + " usable=" + usable + " state0=" + state +
                " networked=" + IsRegionNetworked(x, y) + " objectPos=" + position + " " + activation + " " + extra;
        }

        private static string DescribeActivation(LevelObject levelObject)
        {
            if (ReferenceEquals(levelObject, null))
                return "activation=object-null";

            Transform transform = levelObject.transform;
            if (transform == null)
                return "activation=transform-null";

            GameObject root = transform.gameObject;
            if (root == null)
                return "activation=gameobject-null";

            bool? activeInRegion = null;
            try
            {
                if (IsActiveInRegionProperty != null)
                    activeInRegion = (bool)IsActiveInRegionProperty.GetValue(levelObject, null);
            }
            catch { }

            Collider[] colliders = transform.GetComponentsInChildren<Collider>(true);
            int enabled = 0;
            int activeAndEnabled = 0;
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null) continue;
                if (collider.enabled) enabled++;
                GameObject colliderObject = collider.gameObject;
                if (collider.enabled && colliderObject != null && colliderObject.activeInHierarchy) activeAndEnabled++;
            }

            Animation[] animations = transform.GetComponentsInChildren<Animation>(true);
            int alwaysAnimate = 0;
            for (int i = 0; i < animations.Length; i++)
            {
                Animation animation = animations[i];
                if (animation != null && animation.cullingType == AnimationCullingType.AlwaysAnimate)
                    alwaysAnimate++;
            }

            return "activeInRegion=" + (activeInRegion.HasValue ? activeInRegion.Value.ToString() : "unknown") +
                " rootSelf=" + root.activeSelf + " rootHierarchy=" + root.activeInHierarchy +
                " colliders=" + colliders.Length + " enabled=" + enabled + " activeEnabled=" + activeAndEnabled +
                " animations=" + animations.Length + " alwaysAnimate=" + alwaysAnimate;
        }

        private static bool TryGetBinaryObject(byte x, byte y, ushort index,
            out LevelObject levelObject, out InteractableObjectBinaryState interactable)
        {
            levelObject = null;
            interactable = null;
            try
            {
                if (!Regions.checkSafe(x, y) || LevelObjects.objects == null)
                    return false;
                List<LevelObject> region = LevelObjects.objects[x, y];
                if (region == null || index >= region.Count)
                    return false;
                levelObject = region[index];
                interactable = levelObject?.interactable as InteractableObjectBinaryState;
                return levelObject != null;
            }
            catch
            {
                return false;
            }
        }

        private static byte? GetStateByte(LevelObject levelObject)
        {
            byte[] state = levelObject?.state;
            return state != null && state.Length > 0 ? state[0] : (byte?)null;
        }

        private static bool IsRegionNetworked(byte x, byte y)
        {
            try
            {
                if (!Regions.checkSafe(x, y) || ObjectRegionsField == null)
                    return false;
                Array regions = ObjectRegionsField.GetValue(null) as Array;
                object region = regions?.GetValue(x, y);
                if (region == null)
                    return false;
                FieldInfo field = AccessTools.Field(region.GetType(), "isNetworked");
                return field != null && (bool)field.GetValue(region);
            }
            catch
            {
                return false;
            }
        }

        private static void TrackObject(byte x, byte y, ushort index)
        {
            string key = MakeKey(x, y, index);
            float expires = Time.realtimeSinceStartup + CorrelationWindowSeconds;
            lock (Sync)
            {
                TrackedObjects[key] = expires;
            }
        }

        private static bool TryGetTrackedObjectKey(LevelObject levelObject, out string key)
        {
            key = null;
            try
            {
                if (ReferenceEquals(levelObject, null))
                    return false;
                Transform transform = levelObject.transform;
                if (transform == null || !ObjectManager.tryGetRegion(transform, out byte x, out byte y, out ushort index))
                    return false;
                key = MakeKey(x, y, index);
            }
            catch
            {
                return false;
            }

            lock (Sync)
            {
                if (!TrackedObjects.TryGetValue(key, out float expires))
                    return false;
                if (Time.realtimeSinceStartup <= expires)
                    return true;
                TrackedObjects.Remove(key);
                LastActivationFingerprints.Remove(key);
                RemoveCollisionLogTimes(key);
                return false;
            }
        }

        private static bool TryMatchTrackedCollider(Transform colliderTransform, out string key)
        {
            key = null;
            if (ReferenceEquals(colliderTransform, null))
                return false;

            List<string> candidates;
            float now = Time.realtimeSinceStartup;
            lock (Sync)
            {
                candidates = new List<string>();
                foreach (KeyValuePair<string, float> pair in TrackedObjects)
                {
                    if (pair.Value >= now)
                        candidates.Add(pair.Key);
                }
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                string candidate = candidates[i];
                if (!TryParseKey(candidate, out byte x, out byte y, out ushort index))
                    continue;
                if (!TryGetBinaryObject(x, y, index, out LevelObject levelObject, out _))
                    continue;
                Transform root = levelObject.transform;
                if (!ReferenceEquals(root, null) &&
                    (colliderTransform == root || colliderTransform.IsChildOf(root)))
                {
                    key = candidate;
                    return true;
                }
            }
            return false;
        }

        private static bool TryParseKey(string key, out byte x, out byte y, out ushort index)
        {
            x = 0;
            y = 0;
            index = 0;
            if (string.IsNullOrEmpty(key))
                return false;
            string[] halves = key.Split(':');
            if (halves.Length != 2 || !ushort.TryParse(halves[1], out index))
                return false;
            string[] coordinates = halves[0].Split(',');
            return coordinates.Length == 2 && byte.TryParse(coordinates[0], out x) && byte.TryParse(coordinates[1], out y);
        }

        private static string MakeKey(byte x, byte y, ushort index)
        {
            return x + "," + y + ":" + index;
        }

        private static void RemoveCollisionLogTimes(string objectKey)
        {
            string prefix = objectKey + "|";
            var staleKeys = new List<string>();
            foreach (string key in LastCollisionLogTimes.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                    staleKeys.Add(key);
            }
            for (int i = 0; i < staleKeys.Count; i++)
                LastCollisionLogTimes.Remove(staleKeys[i]);
        }

        private static string MaskPlayer(Player player)
        {
            try
            {
                ulong steamId = player?.channel?.owner?.playerID?.steamID.m_SteamID ?? 0UL;
                return steamId == 0UL ? "none" : DiagnosticMaskUtil.MaskSteamId(steamId);
            }
            catch
            {
                return "mask-error";
            }
        }

        private static string DescribeConnectionTypes(PooledTransportConnectionList connections)
        {
            if (connections == null) return "null";
            var recipients = new List<string>(connections.Count);
            foreach (ITransportConnection connection in connections)
            {
                string name = connection?.GetType().Name ?? "null";
                recipients.Add(name + "@" + FindMaskedPlayerForConnection(connection));
            }
            recipients.Sort(StringComparer.Ordinal);
            return recipients.Count == 0 ? "none" : string.Join("|", recipients);
        }

        private static string FindMaskedPlayerForConnection(ITransportConnection connection)
        {
            try
            {
                if (connection == null || Provider.clients == null)
                    return "unmapped";
                foreach (SteamPlayer steamPlayer in Provider.clients)
                {
                    if (steamPlayer != null && ReferenceEquals(steamPlayer.transportConnection, connection))
                        return DiagnosticMaskUtil.MaskSteamId(steamPlayer.playerID.steamID.m_SteamID);
                }
            }
            catch { }
            return "unmapped";
        }

        private static string GetRole()
        {
            if (Provider.isServer)
                return Dedicator.IsDedicatedServer ? "dedicated" : "listen-host";
            return "client";
        }

        private static string FormatVector(Vector3 value)
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "({0:F2},{1:F2},{2:F2})", value.x, value.y, value.z);
        }

        private static string FormatRegion(Vector3 position)
        {
            return Regions.tryGetCoordinate(position, out byte x, out byte y) ? x + "," + y : "outside";
        }

        private static string FormatNullableByte(byte? value)
        {
            return value.HasValue ? value.Value.ToString() : "null";
        }

        private static long ParseSequence(string value)
        {
            return long.TryParse(value, out long sequence) ? sequence : 0;
        }

        private static long NextSequence()
        {
            return Interlocked.Increment(ref _nextSequence);
        }

        private static void TouchActivity()
        {
            _lastActivityTime = Time.realtimeSinceStartup;
        }

        private static void Log(long sequence, string stage, string details)
        {
            try
            {
                int count = Interlocked.Increment(ref _sessionLogCount);
                if (count > SessionLogLimit)
                    return;
                RoleLogger.Info("[Shared]", "[Issue7/ObjectBinary] seq=" + sequence + " stage=" + stage + " " + details);
            }
            catch
            {
                // Diagnostics must never alter the native call path.
            }
        }

        private static void ResetForSession()
        {
            lock (Sync)
            {
                TrackedObjects.Clear();
                LastActivationFingerprints.Clear();
                LastCollisionLogTimes.Clear();
            }
            _currentAuthorityTrace = null;
            Interlocked.Exchange(ref _sessionLogCount, 0);
            Interlocked.Exchange(ref _nextSequence, 0);
            _lastActivityTime = -100f;
            RoleLogger.Info("[Shared]", "[Issue7/ObjectBinary] session trace reset limit=" + SessionLogLimit);
        }
#endif
    }
}
