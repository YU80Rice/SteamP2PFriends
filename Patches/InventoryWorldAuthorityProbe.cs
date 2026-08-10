using HarmonyLib;
using SDG.NetTransport;
using SDG.Unturned;
using SteamP2PFriends.Shared;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace SteamP2PFriends.Patches
{
    /// <summary>
    /// Alpha inventory/world-item diagnostic probe.
    ///
    /// Read-only guarantees:
    /// - never changes arguments, return values, inventory contents, region contents, RPC routing, or reliability;
    /// - never invokes inventory or item-manager business methods;
    /// - catches every diagnostic exception so vanilla execution is not interrupted;
    /// - masks Steam IDs and never logs persona names or network addresses.
    /// </summary>
    public static class InventoryWorldAuthorityProbe
    {
        private const string Point = "[Alpha-AuthorityProbe]";
        private const int DuplicateGenerateLogLimit = 48;
        private const int SnapshotSendLogLimit = 80;
        private const int TakeLogLimit = 80;
        private const int InventoryOperationLogLimit = 100;
        private const int PreviewLimit = 1400;

        private static readonly Type[] GenerateItemsParameters = { typeof(byte), typeof(byte) };
        private static readonly Type[] AskItemsParameters =
        {
            typeof(ITransportConnection), typeof(byte), typeof(byte), typeof(float)
        };
        private static readonly Type[] ReceiveTakeParameters =
        {
            typeof(ServerInvocationContext).MakeByRefType(),
            typeof(byte), typeof(byte), typeof(uint),
            typeof(byte), typeof(byte), typeof(byte), typeof(byte)
        };
        private static readonly Type[] DragParameters =
        {
            typeof(byte), typeof(byte), typeof(byte),
            typeof(byte), typeof(byte), typeof(byte), typeof(byte)
        };
        private static readonly Type[] SwapParameters =
        {
            typeof(byte), typeof(byte), typeof(byte), typeof(byte),
            typeof(byte), typeof(byte), typeof(byte), typeof(byte)
        };
        private static readonly Type[] DropParameters =
        {
            typeof(byte), typeof(byte), typeof(byte)
        };
        private static readonly Type[] AddParameters =
        {
            typeof(byte), typeof(byte), typeof(byte), typeof(byte),
            typeof(ushort), typeof(byte), typeof(byte), typeof(byte[])
        };
        private static readonly Type[] RemoveParameters =
        {
            typeof(byte), typeof(byte), typeof(byte)
        };

        private static readonly int[,] GenerateCallCounts =
            new int[Regions.WORLD_SIZE, Regions.WORLD_SIZE];

        private static int _sampleGenerateLogs;
        private static int _duplicateGenerateLogs;
        private static int _snapshotSendLogs;
        private static int _takeLogs;
        private static int _inventoryOperationLogs;
        private static long _operationSequence;
        private static bool _resetCallbackRegistered;
        private static readonly List<string> RegistrationFailures = new List<string>();

        public static bool AllRegistrationsSucceeded { get; private set; }
        public static string RegistrationSummary { get; private set; } = "not registered";
        public static string TargetSignatureSummary { get; private set; } = "not checked";

        public sealed class GenerateState
        {
            internal bool ShouldLog;
            internal byte X;
            internal byte Y;
            internal int CallCount;
            internal int BeforeCount;
            internal string Source;
        }

        public sealed class TakeState
        {
            internal bool ShouldLog;
            internal byte X;
            internal byte Y;
            internal uint InstanceId;
            internal bool FoundBefore;
            internal Snapshot Before;
            internal string Player;
        }

        public sealed class InventoryOperationState
        {
            internal bool ShouldLog;
            internal long Sequence;
            internal string Operation;
            internal string Arguments;
            internal string Actor;
            internal Snapshot Before;
        }

        public sealed class Snapshot
        {
            internal int Count;
            internal string Sha256 = "unavailable";
            internal string Preview = "unavailable";
        }

        public static bool RegisterManual(Harmony harmony)
        {
            bool all = VerifyTargetSignatures();
            try
            {
                RegistrationFailures.Clear();
                if (!_resetCallbackRegistered)
                {
                    WorldSyncDiagnosticCore.RegisterSessionResetCallback(ResetForSession);
                    _resetCallbackRegistered = true;
                }

                all &= Register(harmony, typeof(ItemManager), "generateItems", GenerateItemsParameters,
                    nameof(GenerateItems_Prefix), HarmonyPatchType.Prefix);
                all &= Register(harmony, typeof(ItemManager), "generateItems", GenerateItemsParameters,
                    nameof(GenerateItems_Postfix), HarmonyPatchType.Postfix);
                all &= Register(harmony, typeof(ItemManager), "generateItems", GenerateItemsParameters,
                    nameof(GenerateItems_Finalizer), HarmonyPatchType.Finalizer);
                all &= Register(harmony, typeof(ItemManager), "askItems", AskItemsParameters,
                    nameof(AskItems_Prefix), HarmonyPatchType.Prefix);
                all &= Register(harmony, typeof(ItemManager), "ReceiveTakeItemRequest", ReceiveTakeParameters,
                    nameof(ReceiveTakeItemRequest_Prefix), HarmonyPatchType.Prefix);
                all &= Register(harmony, typeof(ItemManager), "ReceiveTakeItemRequest", ReceiveTakeParameters,
                    nameof(ReceiveTakeItemRequest_Postfix), HarmonyPatchType.Postfix);

                all &= Register(harmony, typeof(PlayerInventory), nameof(PlayerInventory.ReceiveDragItem), DragParameters,
                    nameof(ReceiveDragItem_Prefix), HarmonyPatchType.Prefix);
                all &= Register(harmony, typeof(PlayerInventory), nameof(PlayerInventory.ReceiveDragItem), DragParameters,
                    nameof(InventoryOperation_Postfix), HarmonyPatchType.Postfix);
                all &= Register(harmony, typeof(PlayerInventory), nameof(PlayerInventory.ReceiveSwapItem), SwapParameters,
                    nameof(ReceiveSwapItem_Prefix), HarmonyPatchType.Prefix);
                all &= Register(harmony, typeof(PlayerInventory), nameof(PlayerInventory.ReceiveSwapItem), SwapParameters,
                    nameof(InventoryOperation_Postfix), HarmonyPatchType.Postfix);
                all &= Register(harmony, typeof(PlayerInventory), nameof(PlayerInventory.ReceiveDropItem), DropParameters,
                    nameof(ReceiveDropItem_Prefix), HarmonyPatchType.Prefix);
                all &= Register(harmony, typeof(PlayerInventory), nameof(PlayerInventory.ReceiveDropItem), DropParameters,
                    nameof(InventoryOperation_Postfix), HarmonyPatchType.Postfix);
                all &= Register(harmony, typeof(PlayerInventory), nameof(PlayerInventory.ReceiveItemAdd), AddParameters,
                    nameof(ReceiveItemAdd_Prefix), HarmonyPatchType.Prefix);
                all &= Register(harmony, typeof(PlayerInventory), nameof(PlayerInventory.ReceiveItemAdd), AddParameters,
                    nameof(InventoryOperation_Postfix), HarmonyPatchType.Postfix);
                all &= Register(harmony, typeof(PlayerInventory), nameof(PlayerInventory.ReceiveItemRemove), RemoveParameters,
                    nameof(ReceiveItemRemove_Prefix), HarmonyPatchType.Prefix);
                all &= Register(harmony, typeof(PlayerInventory), nameof(PlayerInventory.ReceiveItemRemove), RemoveParameters,
                    nameof(InventoryOperation_Postfix), HarmonyPatchType.Postfix);
            }
            catch (Exception ex)
            {
                all = false;
                SafeError($"{Point} registration exception: {ex}");
            }

            AllRegistrationsSucceeded = all && VerifyRegistration();
            RegistrationSummary = AllRegistrationsSucceeded
                ? "16/16 identity-verified hooks; read-only; reset callback registered"
                : "failed=" + (RegistrationFailures.Count == 0
                    ? "final-verification"
                    : String.Join(",", RegistrationFailures));
            SafeInfo("[Shared]", $"{Point} registration={AllRegistrationsSucceeded} summary={RegistrationSummary}");
            return AllRegistrationsSucceeded;
        }

        public static bool VerifyTargetSignatures()
        {
            int resolved = 0;
            bool all = true;
            all &= Resolve(typeof(ItemManager), "generateItems", GenerateItemsParameters, nameof(GenerateItems_Prefix), ref resolved);
            all &= Resolve(typeof(ItemManager), "generateItems", GenerateItemsParameters, nameof(GenerateItems_Postfix), ref resolved);
            all &= Resolve(typeof(ItemManager), "generateItems", GenerateItemsParameters, nameof(GenerateItems_Finalizer), ref resolved);
            all &= Resolve(typeof(ItemManager), "askItems", AskItemsParameters, nameof(AskItems_Prefix), ref resolved);
            all &= Resolve(typeof(ItemManager), "ReceiveTakeItemRequest", ReceiveTakeParameters, nameof(ReceiveTakeItemRequest_Prefix), ref resolved);
            all &= Resolve(typeof(ItemManager), "ReceiveTakeItemRequest", ReceiveTakeParameters, nameof(ReceiveTakeItemRequest_Postfix), ref resolved);
            all &= Resolve(typeof(PlayerInventory), nameof(PlayerInventory.ReceiveDragItem), DragParameters, nameof(ReceiveDragItem_Prefix), ref resolved);
            all &= Resolve(typeof(PlayerInventory), nameof(PlayerInventory.ReceiveDragItem), DragParameters, nameof(InventoryOperation_Postfix), ref resolved);
            all &= Resolve(typeof(PlayerInventory), nameof(PlayerInventory.ReceiveSwapItem), SwapParameters, nameof(ReceiveSwapItem_Prefix), ref resolved);
            all &= Resolve(typeof(PlayerInventory), nameof(PlayerInventory.ReceiveSwapItem), SwapParameters, nameof(InventoryOperation_Postfix), ref resolved);
            all &= Resolve(typeof(PlayerInventory), nameof(PlayerInventory.ReceiveDropItem), DropParameters, nameof(ReceiveDropItem_Prefix), ref resolved);
            all &= Resolve(typeof(PlayerInventory), nameof(PlayerInventory.ReceiveDropItem), DropParameters, nameof(InventoryOperation_Postfix), ref resolved);
            all &= Resolve(typeof(PlayerInventory), nameof(PlayerInventory.ReceiveItemAdd), AddParameters, nameof(ReceiveItemAdd_Prefix), ref resolved);
            all &= Resolve(typeof(PlayerInventory), nameof(PlayerInventory.ReceiveItemAdd), AddParameters, nameof(InventoryOperation_Postfix), ref resolved);
            all &= Resolve(typeof(PlayerInventory), nameof(PlayerInventory.ReceiveItemRemove), RemoveParameters, nameof(ReceiveItemRemove_Prefix), ref resolved);
            all &= Resolve(typeof(PlayerInventory), nameof(PlayerInventory.ReceiveItemRemove), RemoveParameters, nameof(InventoryOperation_Postfix), ref resolved);
            TargetSignatureSummary = $"resolved={resolved}/16";
            return all && resolved == 16;
        }

        private static bool Resolve(Type targetType, string targetName, Type[] parameters, string patchName, ref int resolved)
        {
            MethodInfo original = AccessTools.Method(targetType, targetName, parameters);
            MethodInfo patch = AccessTools.Method(typeof(InventoryWorldAuthorityProbe), patchName);
            if (original == null || patch == null) return false;
            resolved++;
            return true;
        }

        public static bool VerifyRegistration()
        {
            bool all = true;
            all &= Verify(typeof(ItemManager), "generateItems", GenerateItemsParameters, nameof(GenerateItems_Prefix), HarmonyPatchType.Prefix);
            all &= Verify(typeof(ItemManager), "generateItems", GenerateItemsParameters, nameof(GenerateItems_Postfix), HarmonyPatchType.Postfix);
            all &= Verify(typeof(ItemManager), "generateItems", GenerateItemsParameters, nameof(GenerateItems_Finalizer), HarmonyPatchType.Finalizer);
            all &= Verify(typeof(ItemManager), "askItems", AskItemsParameters, nameof(AskItems_Prefix), HarmonyPatchType.Prefix);
            all &= Verify(typeof(ItemManager), "ReceiveTakeItemRequest", ReceiveTakeParameters, nameof(ReceiveTakeItemRequest_Prefix), HarmonyPatchType.Prefix);
            all &= Verify(typeof(ItemManager), "ReceiveTakeItemRequest", ReceiveTakeParameters, nameof(ReceiveTakeItemRequest_Postfix), HarmonyPatchType.Postfix);
            all &= Verify(typeof(PlayerInventory), nameof(PlayerInventory.ReceiveDragItem), DragParameters, nameof(ReceiveDragItem_Prefix), HarmonyPatchType.Prefix);
            all &= Verify(typeof(PlayerInventory), nameof(PlayerInventory.ReceiveDragItem), DragParameters, nameof(InventoryOperation_Postfix), HarmonyPatchType.Postfix);
            all &= Verify(typeof(PlayerInventory), nameof(PlayerInventory.ReceiveSwapItem), SwapParameters, nameof(ReceiveSwapItem_Prefix), HarmonyPatchType.Prefix);
            all &= Verify(typeof(PlayerInventory), nameof(PlayerInventory.ReceiveSwapItem), SwapParameters, nameof(InventoryOperation_Postfix), HarmonyPatchType.Postfix);
            all &= Verify(typeof(PlayerInventory), nameof(PlayerInventory.ReceiveDropItem), DropParameters, nameof(ReceiveDropItem_Prefix), HarmonyPatchType.Prefix);
            all &= Verify(typeof(PlayerInventory), nameof(PlayerInventory.ReceiveDropItem), DropParameters, nameof(InventoryOperation_Postfix), HarmonyPatchType.Postfix);
            all &= Verify(typeof(PlayerInventory), nameof(PlayerInventory.ReceiveItemAdd), AddParameters, nameof(ReceiveItemAdd_Prefix), HarmonyPatchType.Prefix);
            all &= Verify(typeof(PlayerInventory), nameof(PlayerInventory.ReceiveItemAdd), AddParameters, nameof(InventoryOperation_Postfix), HarmonyPatchType.Postfix);
            all &= Verify(typeof(PlayerInventory), nameof(PlayerInventory.ReceiveItemRemove), RemoveParameters, nameof(ReceiveItemRemove_Prefix), HarmonyPatchType.Prefix);
            all &= Verify(typeof(PlayerInventory), nameof(PlayerInventory.ReceiveItemRemove), RemoveParameters, nameof(InventoryOperation_Postfix), HarmonyPatchType.Postfix);
            return all;
        }

        private static bool Register(Harmony harmony, Type targetType, string targetName, Type[] parameters,
            string patchName, HarmonyPatchType patchType)
        {
            string label = $"{targetType.Name}.{targetName}.{patchType}.{patchName}";
            try
            {
                MethodInfo patch = AccessTools.Method(typeof(InventoryWorldAuthorityProbe), patchName);
                MethodInfo original = AccessTools.Method(targetType, targetName, parameters);
                if (patch == null || original == null || harmony == null)
                {
                    RegistrationFailures.Add(label + ":resolve-null");
                    return false;
                }

                if (WorldSyncDiagnosticCore.IsPatchRegistered(
                    targetType, targetName, patch, patchType, parameters, false))
                    return true;

                HarmonyMethod harmonyMethod = new HarmonyMethod(patch);
                if (patchType == HarmonyPatchType.Prefix)
                    harmony.Patch(original, prefix: harmonyMethod);
                else if (patchType == HarmonyPatchType.Postfix)
                    harmony.Patch(original, postfix: harmonyMethod);
                else if (patchType == HarmonyPatchType.Finalizer)
                    harmony.Patch(original, finalizer: harmonyMethod);
                else
                    throw new NotSupportedException("unsupported patch type " + patchType);

                bool verified = WorldSyncDiagnosticCore.IsPatchRegistered(
                    targetType, targetName, patch, patchType, parameters, true);
                if (!verified) RegistrationFailures.Add(label + ":verify-false");
                return verified;
            }
            catch (Exception ex)
            {
                Exception root = ex;
                while (root.InnerException != null) root = root.InnerException;
                RegistrationFailures.Add(label + ":" + root.GetType().Name + ":" + root.Message);
                return false;
            }
        }

        private static bool Verify(Type targetType, string targetName, Type[] parameters,
            string patchName, HarmonyPatchType patchType)
        {
            MethodInfo patch = AccessTools.Method(typeof(InventoryWorldAuthorityProbe), patchName);
            return WorldSyncDiagnosticCore.IsPatchRegistered(
                targetType, targetName, patch, patchType, parameters, true);
        }

        public static void ResetForSession()
        {
            try
            {
                Array.Clear(GenerateCallCounts, 0, GenerateCallCounts.Length);
                _sampleGenerateLogs = 0;
                _duplicateGenerateLogs = 0;
                _snapshotSendLogs = 0;
                _takeLogs = 0;
                _inventoryOperationLogs = 0;
                _operationSequence = 0;
                SafeInfo("[Shared]", $"{Point} session counters reset");
            }
            catch (Exception ex)
            {
                SafeError($"{Point} reset exception: {ex.Message}");
            }
        }

        public static void GenerateItems_Prefix(byte x, byte y, ref GenerateState __state)
        {
            try
            {
                AssertGameThreadForProbe("generateItems.Pre");
                if (!Regions.checkSafe(x, y)) return;

                int callCount = ++GenerateCallCounts[x, y];
                bool isDuplicate = callCount > 1;
                bool isSample = x == y && (x % 8 == 0);
                bool shouldLog = false;
                if (isDuplicate && _duplicateGenerateLogs < DuplicateGenerateLogLimit)
                {
                    _duplicateGenerateLogs++;
                    shouldLog = true;
                }
                else if (isSample && _sampleGenerateLogs < 12)
                {
                    _sampleGenerateLogs++;
                    shouldLog = true;
                }

                __state = new GenerateState
                {
                    ShouldLog = shouldLog,
                    X = x,
                    Y = y,
                    CallCount = callCount,
                    BeforeCount = GetRegionItemCount(x, y),
                    Source = shouldLog ? ClassifyGenerateCaller() : "not-sampled"
                };

                if (shouldLog)
                {
                    SafeInfo("[Host]", $"{Point} WORLD-GENERATE before region=({x},{y}) " +
                        $"call={callCount} duplicate={isDuplicate} source={__state.Source} " +
                        $"itemsBefore={__state.BeforeCount} server={Provider.isServer} dedicated={SafeDedicatedFlag()}");
                }
            }
            catch (Exception ex)
            {
                SafeError($"{Point} generateItems Prefix probe exception: {ex.Message}");
            }
        }

        public static void GenerateItems_Postfix(GenerateState __state, bool __runOriginal)
        {
            try
            {
                if (__state == null || !__state.ShouldLog) return;
                Snapshot after = CaptureRegion(__state.X, __state.Y);
                string outcome = __runOriginal ? "executed" : "skipped-by-authority-gate";
                SafeInfo("[Host]", $"{Point} WORLD-GENERATE {outcome} region=({__state.X},{__state.Y}) " +
                    $"call={__state.CallCount} source={__state.Source} itemsBefore={__state.BeforeCount} " +
                    $"itemsAfter={after.Count} sha256={after.Sha256} preview={after.Preview}");
            }
            catch (Exception ex)
            {
                SafeError($"{Point} generateItems Postfix probe exception: {ex.Message}");
            }
        }

        public static Exception GenerateItems_Finalizer(Exception __exception, GenerateState __state)
        {
            try
            {
                if (__exception != null && __state != null)
                {
                    SafeError($"{Point} WORLD-GENERATE abort region=({__state.X},{__state.Y}) " +
                        $"call={__state.CallCount} source={__state.Source} exception={__exception.GetType().Name}");
                }
            }
            catch { }
            return __exception;
        }

        public static void AskItems_Prefix(ITransportConnection transportConnection, byte x, byte y)
        {
            try
            {
                AssertGameThreadForProbe("askItems.Pre");
                if (_snapshotSendLogs >= SnapshotSendLogLimit) return;
                _snapshotSendLogs++;

                Snapshot snapshot = CaptureRegion(x, y);
                int generationCount = Regions.checkSafe(x, y) ? GenerateCallCounts[x, y] : -1;
                SafeInfo("[Host]", $"{Point} WORLD-SNAPSHOT send seq={_snapshotSendLogs}/{SnapshotSendLogLimit} " +
                    $"region=({x},{y}) recipient={MaskTransportOwner(transportConnection)} " +
                    $"transport={transportConnection?.GetType().Name ?? "null"} generationCalls={generationCount} " +
                    $"count={snapshot.Count} sha256={snapshot.Sha256} preview={snapshot.Preview}");
            }
            catch (Exception ex)
            {
                SafeError($"{Point} askItems Prefix probe exception: {ex.Message}");
            }
        }

        public static void ReceiveTakeItemRequest_Prefix(in ServerInvocationContext context,
            byte x, byte y, uint instanceID, byte to_x, byte to_y, byte to_rot, byte to_page,
            ref TakeState __state)
        {
            try
            {
                AssertGameThreadForProbe("ReceiveTakeItemRequest.Pre");
                if (_takeLogs >= TakeLogLimit) return;
                _takeLogs++;

                Player player = context.GetPlayer();
                __state = new TakeState
                {
                    ShouldLog = true,
                    X = x,
                    Y = y,
                    InstanceId = instanceID,
                    FoundBefore = RegionContainsInstance(x, y, instanceID),
                    Before = CaptureRegion(x, y),
                    Player = MaskPlayer(player)
                };

                SafeInfo("[Host]", $"{Point} WORLD-TAKE before seq={_takeLogs}/{TakeLogLimit} actor={__state.Player} " +
                    $"region=({x},{y}) instance={instanceID} found={__state.FoundBefore} " +
                    $"target=({to_page},{to_x},{to_y},{to_rot}) count={__state.Before.Count} sha256={__state.Before.Sha256}");
            }
            catch (Exception ex)
            {
                SafeError($"{Point} take Prefix probe exception: {ex.Message}");
            }
        }

        public static void ReceiveTakeItemRequest_Postfix(TakeState __state)
        {
            try
            {
                if (__state == null || !__state.ShouldLog) return;
                bool foundAfter = RegionContainsInstance(__state.X, __state.Y, __state.InstanceId);
                Snapshot after = CaptureRegion(__state.X, __state.Y);
                string outcome = __state.FoundBefore && !foundAfter ? "removed-authoritatively" :
                    (!__state.FoundBefore ? "stale-instance-request" : "not-removed");
                SafeInfo("[Host]", $"{Point} WORLD-TAKE after actor={__state.Player} " +
                    $"region=({__state.X},{__state.Y}) instance={__state.InstanceId} outcome={outcome} " +
                    $"foundAfter={foundAfter} count={after.Count} sha256={after.Sha256}");
            }
            catch (Exception ex)
            {
                SafeError($"{Point} take Postfix probe exception: {ex.Message}");
            }
        }

        public static void ReceiveDragItem_Prefix(PlayerInventory __instance,
            byte page_0, byte x_0, byte y_0, byte page_1, byte x_1, byte y_1, byte rot_1,
            ref InventoryOperationState __state)
        {
            BeginInventoryOperation(__instance, "server-drag",
                $"from=({page_0},{x_0},{y_0}) to=({page_1},{x_1},{y_1},{rot_1})", ref __state);
        }

        public static void ReceiveSwapItem_Prefix(PlayerInventory __instance,
            byte page_0, byte x_0, byte y_0, byte rot_0,
            byte page_1, byte x_1, byte y_1, byte rot_1,
            ref InventoryOperationState __state)
        {
            BeginInventoryOperation(__instance, "server-swap",
                $"a=({page_0},{x_0},{y_0},{rot_0}) b=({page_1},{x_1},{y_1},{rot_1})", ref __state);
        }

        public static void ReceiveDropItem_Prefix(PlayerInventory __instance,
            byte page, byte x, byte y, ref InventoryOperationState __state)
        {
            BeginInventoryOperation(__instance, "server-drop",
                $"slot=({page},{x},{y})", ref __state);
        }

        public static void ReceiveItemAdd_Prefix(PlayerInventory __instance,
            byte page, byte x, byte y, byte rot, ushort id, byte amount, byte quality, byte[] state,
            ref InventoryOperationState __state)
        {
            BeginInventoryOperation(__instance, "client-add-projection",
                $"slot=({page},{x},{y},{rot}) item={id}/{amount}/{quality}/state:{HashBytes(state)}", ref __state);
        }

        public static void ReceiveItemRemove_Prefix(PlayerInventory __instance,
            byte page, byte x, byte y, ref InventoryOperationState __state)
        {
            BeginInventoryOperation(__instance, "client-remove-projection",
                $"slot=({page},{x},{y})", ref __state);
        }

        public static void InventoryOperation_Postfix(PlayerInventory __instance, InventoryOperationState __state)
        {
            try
            {
                if (__state == null || !__state.ShouldLog) return;
                Snapshot after = CaptureInventory(__instance);
                bool changed = !String.Equals(__state.Before.Sha256, after.Sha256, StringComparison.Ordinal);
                SafeInfo(GetRoleLabel(__instance), $"{Point} INVENTORY after op={__state.Operation} " +
                    $"seq={__state.Sequence} actor={__state.Actor} args={__state.Arguments} changed={changed} " +
                    $"beforeCount={__state.Before.Count} beforeSha={__state.Before.Sha256} " +
                    $"afterCount={after.Count} afterSha={after.Sha256} after={after.Preview}");
            }
            catch (Exception ex)
            {
                SafeError($"{Point} inventory Postfix probe exception: {ex.Message}");
            }
        }

        private static void BeginInventoryOperation(PlayerInventory inventory, string operation,
            string arguments, ref InventoryOperationState state)
        {
            try
            {
                AssertGameThreadForProbe(operation);
                if (_inventoryOperationLogs >= InventoryOperationLogLimit) return;
                _inventoryOperationLogs++;

                state = new InventoryOperationState
                {
                    ShouldLog = true,
                    Sequence = ++_operationSequence,
                    Operation = operation,
                    Arguments = arguments,
                    Actor = MaskInventoryOwner(inventory),
                    Before = CaptureInventory(inventory)
                };

                SafeInfo(GetRoleLabel(inventory), $"{Point} INVENTORY before op={operation} " +
                    $"seq={state.Sequence} actor={state.Actor} args={arguments} " +
                    $"count={state.Before.Count} sha256={state.Before.Sha256} before={state.Before.Preview}");
            }
            catch (Exception ex)
            {
                SafeError($"{Point} inventory Prefix probe exception: {ex.Message}");
            }
        }

        private static Snapshot CaptureInventory(PlayerInventory inventory)
        {
            var entries = new List<string>();
            try
            {
                if (inventory?.items == null) return MakeSnapshot(entries);
                for (byte page = 2; page <= 6; page++)
                {
                    if (page >= inventory.items.Length || inventory.items[page] == null) continue;
                    byte count = inventory.getItemCount(page);
                    for (byte index = 0; index < count; index++)
                    {
                        ItemJar jar = inventory.getItem(page, index);
                        Item item = jar?.item;
                        if (jar == null || item == null)
                        {
                            entries.Add($"p{page}:i{index}:null");
                            continue;
                        }
                        entries.Add($"p{page}:i{index}:xy{jar.x},{jar.y}:r{jar.rot}:" +
                            $"id{item.id}:a{item.amount}:q{item.quality}:s{HashBytes(item.state)}");
                    }
                }
            }
            catch (Exception ex)
            {
                entries.Add("capture-error:" + ex.GetType().Name);
            }
            return MakeSnapshot(entries);
        }

        private static Snapshot CaptureRegion(byte x, byte y)
        {
            var entries = new List<string>();
            try
            {
                if (!Regions.checkSafe(x, y) || ItemManager.regions == null || ItemManager.regions[x, y] == null)
                    return MakeSnapshot(entries);

                List<ItemData> items = ItemManager.regions[x, y].items;
                if (items == null) return MakeSnapshot(entries);
                foreach (ItemData data in items)
                {
                    if (data?.item == null)
                    {
                        entries.Add("null");
                        continue;
                    }
                    entries.Add($"inst{data.instanceID}:id{data.item.id}:a{data.item.amount}:q{data.item.quality}:" +
                        $"s{HashBytes(data.item.state)}:p{F(data.point.x)},{F(data.point.y)},{F(data.point.z)}:" +
                        $"drop{data.isDropped}");
                }
                entries.Sort(StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                entries.Add("capture-error:" + ex.GetType().Name);
            }
            return MakeSnapshot(entries);
        }

        private static Snapshot MakeSnapshot(List<string> entries)
        {
            string canonical = String.Join("|", entries);
            return new Snapshot
            {
                Count = entries.Count,
                Sha256 = HashText(canonical),
                Preview = canonical.Length <= PreviewLimit
                    ? canonical
                    : canonical.Substring(0, PreviewLimit) + "...<truncated>"
            };
        }

        private static int GetRegionItemCount(byte x, byte y)
        {
            try
            {
                if (!Regions.checkSafe(x, y) || ItemManager.regions == null || ItemManager.regions[x, y] == null)
                    return -1;
                return ItemManager.regions[x, y].items?.Count ?? 0;
            }
            catch { return -1; }
        }

        private static bool RegionContainsInstance(byte x, byte y, uint instanceId)
        {
            try
            {
                if (!Regions.checkSafe(x, y) || ItemManager.regions == null || ItemManager.regions[x, y] == null)
                    return false;
                List<ItemData> items = ItemManager.regions[x, y].items;
                if (items == null) return false;
                foreach (ItemData item in items)
                {
                    if (item != null && item.instanceID == instanceId) return true;
                }
            }
            catch { }
            return false;
        }

        private static string ClassifyGenerateCaller()
        {
            try
            {
                string stack = new StackTrace(2, false).ToString();
                if (stack.IndexOf("ItemManagerP0B6RegenerateOnLevelLoadedPatch", StringComparison.Ordinal) >= 0)
                    return "plugin-P0B6-reflection";
                if (stack.IndexOf("onRegionUpdated", StringComparison.Ordinal) >= 0)
                    return "vanilla-local-region";
                if (stack.IndexOf("onLevelLoaded", StringComparison.Ordinal) >= 0)
                    return "vanilla-onLevelLoaded-transpiled";
                return "other";
            }
            catch { return "stack-unavailable"; }
        }

        private static string MaskTransportOwner(ITransportConnection connection)
        {
            try
            {
                if (Provider.clients != null)
                {
                    foreach (SteamPlayer steamPlayer in Provider.clients)
                    {
                        if (steamPlayer?.transportConnection == connection)
                            return DiagnosticMaskUtil.MaskSteamId(steamPlayer.playerID.steamID);
                    }
                }
            }
            catch { }
            return "unknown";
        }

        private static string MaskInventoryOwner(PlayerInventory inventory)
        {
            try
            {
                return MaskPlayer(inventory?.player);
            }
            catch { return "unknown"; }
        }

        private static string MaskPlayer(Player player)
        {
            try
            {
                CSteamID id = player?.channel?.owner?.playerID?.steamID ?? CSteamID.Nil;
                return DiagnosticMaskUtil.MaskSteamId(id);
            }
            catch { return "unknown"; }
        }

        private static string GetRoleLabel(PlayerInventory inventory)
        {
            try
            {
                if (Provider.isServer) return "[Host]";
                if (inventory?.player?.channel != null && inventory.player.channel.IsLocalPlayer) return "[Client]";
            }
            catch { }
            return "[Shared]";
        }

        private static void AssertGameThreadForProbe(string point)
        {
            try
            {
                ThreadUtil.assertIsGameThread();
            }
            catch (Exception ex)
            {
                SafeError($"{Point} MAIN-THREAD assertion failed at {point}: {ex.GetType().Name}");
            }
        }

        private static bool SafeDedicatedFlag()
        {
            try { return Dedicator.IsDedicatedServer; }
            catch { return false; }
        }

        private static string HashText(string value)
        {
            try
            {
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? String.Empty));
                    return ToHex(hash);
                }
            }
            catch { return "hash-error"; }
        }

        private static string HashBytes(byte[] value)
        {
            try
            {
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(value ?? Array.Empty<byte>());
                    string full = ToHex(hash);
                    return full.Length > 12 ? full.Substring(0, 12) : full;
                }
            }
            catch { return "hash-error"; }
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) builder.Append(b.ToString("X2", CultureInfo.InvariantCulture));
            return builder.ToString();
        }

        private static string F(float value)
        {
            return value.ToString("0.000", CultureInfo.InvariantCulture);
        }

        private static void SafeInfo(string role, string message)
        {
            try { RoleLogger.Info(role, message); } catch { }
        }

        private static void SafeError(string message)
        {
            try { RoleLogger.Error("[Shared]", message); } catch { }
        }
    }
}
