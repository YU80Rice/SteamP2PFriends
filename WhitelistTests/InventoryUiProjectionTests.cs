using HarmonyLib;
using SteamP2PFriends.Patches;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SteamP2PFriends.WhitelistTests
{
    internal static class InventoryUiProjectionTests
    {
        // The independent test CLR cannot safely detour these Assembly-CSharp methods because
        // their bodies reference Unity/game native dependencies. Resolve and validate the real
        // production targets first, then bind the production postfixes to signature-equivalent
        // instance-method surrogates. Deriving from PlayerInventory preserves Harmony's typed
        // __instance contract rather than weakening the production postfix to object.
        private sealed class IUI6InventoryTarget : SDG.Unturned.PlayerInventory
        {
            public void Drag(byte page_0, byte x_0, byte y_0,
                byte page_1, byte x_1, byte y_1, byte rot_1)
            {
            }

            public void Swap(byte page_0, byte x_0, byte y_0, byte rot_0,
                byte page_1, byte x_1, byte y_1, byte rot_1)
            {
            }

            public void Drop(byte page, byte x, byte y)
            {
            }
        }

        internal static bool Test_IUI1_ExactProjectionNoRepair()
        {
            object a = new object();
            object b = new object();
            return ListenHostInventoryUiProjectionPatch.ProjectionIsExact(
                new[] { a, b }, new[] { a }, new[] { b });
        }

        internal static bool Test_IUI2_StaleRenderedJarDetected()
        {
            object current = new object();
            object stale = new object();
            return !ListenHostInventoryUiProjectionPatch.ProjectionIsExact(
                new[] { current }, new[] { stale, current }, Array.Empty<object>());
        }

        internal static bool Test_IUI3_StalePendingJarDetected()
        {
            object current = new object();
            object stale = new object();
            return !ListenHostInventoryUiProjectionPatch.ProjectionIsExact(
                new[] { current }, Array.Empty<object>(), new[] { stale, current });
        }

        internal static bool Test_IUI4_IdentityNotValueEquivalence()
        {
            string authoritative = new string(new[] { 'x' });
            string projection = new string(new[] { 'x' });
            return authoritative == projection &&
                !ReferenceEquals(authoritative, projection) &&
                !ListenHostInventoryUiProjectionPatch.ProjectionIsExact(
                    new object[] { authoritative }, new object[] { projection }, Array.Empty<object>());
        }

        internal static bool Test_IUI5_ReflectionContractExact()
        {
            FieldInfo pages = AccessTools.Field(
                typeof(SDG.Unturned.PlayerDashboardInventoryUI), "items");
            FieldInfo pending = AccessTools.Field(typeof(SDG.Unturned.SleekItems), "pendingItems");
            return ListenHostInventoryUiProjectionPatch.ReflectionContractAvailable &&
                pages != null && pages.FieldType == typeof(SDG.Unturned.SleekItems[]) &&
                pending != null && pending.FieldType == typeof(List<SDG.Unturned.ItemJar>);
        }

        internal static bool Test_IUI6_ProductionPostfixesActivate()
        {
            MethodInfo[] realOriginals =
            {
                AccessTools.Method(typeof(SDG.Unturned.PlayerInventory),
                    nameof(SDG.Unturned.PlayerInventory.ReceiveDragItem),
                    new[] { typeof(byte), typeof(byte), typeof(byte), typeof(byte), typeof(byte), typeof(byte), typeof(byte) }),
                AccessTools.Method(typeof(SDG.Unturned.PlayerInventory),
                    nameof(SDG.Unturned.PlayerInventory.ReceiveSwapItem),
                    new[] { typeof(byte), typeof(byte), typeof(byte), typeof(byte), typeof(byte), typeof(byte), typeof(byte), typeof(byte) }),
                AccessTools.Method(typeof(SDG.Unturned.PlayerInventory),
                    nameof(SDG.Unturned.PlayerInventory.ReceiveDropItem),
                    new[] { typeof(byte), typeof(byte), typeof(byte) })
            };
            MethodInfo[] postfixes =
            {
                ListenHostInventoryUiProjectionPatch.DragPostfix,
                ListenHostInventoryUiProjectionPatch.SwapPostfix,
                ListenHostInventoryUiProjectionPatch.DropPostfix
            };
            if (realOriginals.Any(x => x == null) || postfixes.Any(x => x == null))
                return false;

            int[] expectedParameterCounts = { 7, 8, 3 };
            for (int i = 0; i < realOriginals.Length; i++)
            {
                MethodInfo original = realOriginals[i];
                if (original == null || original.DeclaringType != typeof(SDG.Unturned.PlayerInventory) ||
                    original.ReturnType != typeof(void))
                    return false;

                ParameterInfo[] parameters = original.GetParameters();
                if (parameters.Length != expectedParameterCounts[i] ||
                    parameters.Any(parameter => parameter.ParameterType != typeof(byte)))
                    return false;
            }

            MethodInfo[] surrogates =
            {
                AccessTools.Method(typeof(IUI6InventoryTarget), nameof(IUI6InventoryTarget.Drag)),
                AccessTools.Method(typeof(IUI6InventoryTarget), nameof(IUI6InventoryTarget.Swap)),
                AccessTools.Method(typeof(IUI6InventoryTarget), nameof(IUI6InventoryTarget.Drop))
            };
            if (surrogates.Any(x => x == null) || postfixes.Any(x => x == null))
                return false;

            const string Owner = "com.yu80rice.steamp2pfriends.test.inventory-ui";
            Harmony harmony = new Harmony(Owner);
            try
            {
                for (int i = 0; i < surrogates.Length; i++)
                    harmony.Patch(surrogates[i], postfix: new HarmonyMethod(postfixes[i]));

                for (int i = 0; i < surrogates.Length; i++)
                {
                    HarmonyLib.Patches info = Harmony.GetPatchInfo(surrogates[i]);
                    if (info == null || !info.Postfixes.Any(p =>
                        p.owner == Owner && p.PatchMethod == postfixes[i]))
                        return false;
                }
                return true;
            }
            finally
            {
                foreach (MethodInfo surrogate in surrogates)
                {
                    try { harmony.Unpatch(surrogate, HarmonyPatchType.All, Owner); }
                    catch { }
                }
            }
        }
    }
}
