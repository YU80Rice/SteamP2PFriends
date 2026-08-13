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
    /// Repairs a listen-host-only inventory UI projection defect without changing inventory state.
    ///
    /// U3's SleekItems UI delays creation through a private pending queue and removes elements by
    /// coordinates rather than ItemJar identity. In a listen host, authoritative inventory changes
    /// and the local UI projection happen on the same PlayerInventory instance. A storage-to-bag
    /// quick move followed by another move can therefore leave a stale visual element which is not
    /// present in the authoritative Items collection.
    ///
    /// This patch only compares the local host UI projection with the already-committed inventory.
    /// It rebuilds affected pages when, and only when, rendered+pending ItemJar identities differ.
    /// It never adds, removes, moves, or mutates an inventory Item/ItemJar.
    /// </summary>
    internal static class ListenHostInventoryUiProjectionPatch
    {
        private const string Point = "[InventoryUI-Reconcile]";

        private static readonly FieldInfo DashboardItemsField =
            AccessTools.Field(typeof(PlayerDashboardInventoryUI), "items");
        private static readonly FieldInfo PendingItemsField =
            AccessTools.Field(typeof(SleekItems), "pendingItems");

        internal static bool ReflectionContractAvailable =>
            DashboardItemsField != null &&
            DashboardItemsField.FieldType == typeof(SleekItems[]) &&
            PendingItemsField != null &&
            PendingItemsField.FieldType == typeof(List<ItemJar>);

        internal static bool ProjectionIsExact(
            IReadOnlyList<object> authoritative,
            IReadOnlyList<object> rendered,
            IReadOnlyList<object> pending)
        {
            if (authoritative == null || rendered == null || pending == null)
                return false;

            int projectedCount = rendered.Count + pending.Count;
            if (projectedCount != authoritative.Count)
                return false;

            bool[] matched = new bool[projectedCount];
            for (int a = 0; a < authoritative.Count; a++)
            {
                object expected = authoritative[a];
                if (expected == null)
                    return false;

                bool found = false;
                for (int p = 0; p < projectedCount; p++)
                {
                    if (matched[p])
                        continue;

                    object candidate = p < rendered.Count
                        ? rendered[p]
                        : pending[p - rendered.Count];
                    if (ReferenceEquals(expected, candidate))
                    {
                        matched[p] = true;
                        found = true;
                        break;
                    }
                }

                if (!found)
                    return false;
            }

            return true;
        }

        private static bool IsEligibleLocalHostInventory(PlayerInventory inventory)
        {
            if (inventory == null || !HostManager.IsP2PHostMode ||
                !Provider.isServer || !Provider.isClient)
                return false;

            Player owner = inventory.player;
            return owner != null && owner.channel != null && owner.channel.IsLocalPlayer &&
                ReferenceEquals(Player.LocalPlayer, owner);
        }

        private static void Reconcile(PlayerInventory inventory, byte firstPage, byte secondPage = byte.MaxValue)
        {
            try
            {
                if (!IsEligibleLocalHostInventory(inventory) || !ReflectionContractAvailable)
                    return;

                ReconcilePage(inventory, firstPage);
                if (secondPage != firstPage)
                    ReconcilePage(inventory, secondPage);
            }
            catch (Exception ex)
            {
                // UI repair must never interrupt the authoritative inventory transaction.
                RoleLogger.Warn("[Host]", $"{Point} best-effort repair aborted: {ex.GetType().Name}");
            }
        }

        private static void ReconcilePage(PlayerInventory inventory, byte page)
        {
            if (page < PlayerInventory.SLOTS || page >= PlayerInventory.PAGES - 1 ||
                inventory.items == null || page >= inventory.items.Length)
                return;

            Items model = inventory.items[page];
            if (model == null)
                return;

            SleekItems[] dashboardPages = DashboardItemsField.GetValue(null) as SleekItems[];
            int dashboardIndex = page - PlayerInventory.SLOTS;
            if (dashboardPages == null || dashboardIndex < 0 ||
                dashboardIndex >= dashboardPages.Length)
                return;

            SleekItems projection = dashboardPages[dashboardIndex];
            if (projection == null || projection.items == null)
                return;

            List<ItemJar> pending = PendingItemsField.GetValue(projection) as List<ItemJar>;
            if (pending == null)
                return;

            List<object> authoritative = new List<object>(model.getItemCount());
            for (byte index = 0; index < model.getItemCount(); index++)
                authoritative.Add(model.getItem(index));

            List<object> rendered = new List<object>(projection.items.Count);
            foreach (SleekItem item in projection.items)
                rendered.Add(item?.jar);

            List<object> queued = new List<object>(pending.Count);
            foreach (ItemJar jar in pending)
                queued.Add(jar);

            if (ProjectionIsExact(authoritative, rendered, queued))
                return;

            int oldRendered = rendered.Count;
            int oldPending = queued.Count;
            projection.clear();
            projection.resize(model.width, model.height);
            for (byte index = 0; index < model.getItemCount(); index++)
            {
                ItemJar jar = model.getItem(index);
                if (jar == null)
                    continue;

                projection.addItem(jar);

                // clear() also removes the original hotkey labels. Re-project any still-valid
                // vanilla hotkey on ordinary inventory pages. updateHotkey handles a queued jar
                // by creating its element immediately, while storage is intentionally excluded.
                if (page < PlayerInventory.STORAGE && inventory.player?.equipment != null &&
                    inventory.player.equipment.isItemHotkeyed(page, index, jar, out byte button))
                {
                    projection.updateHotkey(jar, button);
                }
            }

            RoleLogger.Info("[Host]",
                $"{Point} repaired page={page} authoritative={authoritative.Count} " +
                $"renderedBefore={oldRendered} pendingBefore={oldPending}");
        }

        [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.ReceiveDragItem),
            new Type[] { typeof(byte), typeof(byte), typeof(byte), typeof(byte), typeof(byte), typeof(byte), typeof(byte) })]
        private static class Drag
        {
            [HarmonyPostfix]
            private static void Postfix(PlayerInventory __instance, byte page_0, byte page_1) =>
                Reconcile(__instance, page_0, page_1);
        }

        [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.ReceiveSwapItem),
            new Type[] { typeof(byte), typeof(byte), typeof(byte), typeof(byte), typeof(byte), typeof(byte), typeof(byte), typeof(byte) })]
        private static class Swap
        {
            [HarmonyPostfix]
            private static void Postfix(PlayerInventory __instance, byte page_0, byte page_1) =>
                Reconcile(__instance, page_0, page_1);
        }

        [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.ReceiveDropItem),
            new Type[] { typeof(byte), typeof(byte), typeof(byte) })]
        private static class Drop
        {
            [HarmonyPostfix]
            private static void Postfix(PlayerInventory __instance, byte page) =>
                Reconcile(__instance, page);
        }

        internal static MethodInfo DragPostfix =>
            AccessTools.Method(typeof(Drag), "Postfix");
        internal static MethodInfo SwapPostfix =>
            AccessTools.Method(typeof(Swap), "Postfix");
        internal static MethodInfo DropPostfix =>
            AccessTools.Method(typeof(Drop), "Postfix");
    }
}
