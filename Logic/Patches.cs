using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppScheduleOne.Delivery;
using Il2CppScheduleOne.Money;
using Il2CppScheduleOne.UI.Phone.Delivery;
using Il2CppScheduleOne.Variables;
using GProperty = Il2CppScheduleOne.Property.Property;

namespace SupplyRunLogic
{
    // Hooks into the game's own delivery code. Every hook is a no-op unless a courier pick-up point
    // or a courier run is involved, so normal shop deliveries behave exactly as before.
    internal static class Patches
    {
        private const BindingFlags F = BindingFlags.Static | BindingFlags.NonPublic;

        // False when a hook the courier cannot work safely without did not apply (for example after a
        // game update renamed something). The courier then stays switched off instead of half-working.
        internal static bool Healthy { get; private set; } = true;

        internal static void Apply(HarmonyLib.Harmony h)
        {
            Healthy = true;
            hookedCode.Clear();
            // required: without these an order would charge wrongly or ship the wrong cargo
            Patch(h, true, AccessTools.Method(typeof(DeliveryShop), "GetCartCost"), null, nameof(CartCostPost));
            Patch(h, true, AccessTools.Method(typeof(DeliveryShop), "GetDeliveryFee"), null, nameof(DeliveryFeePost));
            Patch(h, true, AccessTools.Method(typeof(DeliveryManager), "SendDelivery"), nameof(SendDeliveryPre), null);
            Patch(h, true, AccessTools.Method(typeof(MoneyManager), "CreateOnlineTransaction"), nameof(TransactionPre), null);
            Patch(h, true, AccessTools.Method(typeof(DeliveryInstance), "AddItemsToDeliveryVehicle"), nameof(AddItemsPre), null);
            // comfort: the courier still moves the right items for the right fee without them
            Patch(h, false, AccessTools.Method(typeof(DeliveryShop), "RefreshOrderButton"), nameof(OrderButtonPre), nameof(OrderButtonPost));
            Patch(h, false, AccessTools.Method(typeof(DeliveryShop), "GetPotentialDestinations"), null, nameof(DestinationsPost));
            // Only the public method: in the game binary it and its RpcWriter are one and the same
            // function, and hooking one function twice makes every normal order call itself forever.
            Patch(h, false, AccessTools.Method(typeof(DeliveryManager), "RecordDeliveryReceipt_Server"), nameof(SkipWhileSubmitting), null);
            Patch(h, false, AccessTools.Method(typeof(VariableDatabase), "NotifyItemAcquired"), nameof(SkipWhileSubmitting), null);
            Patch(h, false, AccessTools.Method(typeof(DeliveryStatusDisplay), "AssignDelivery"), null, nameof(AssignDeliveryPost));
            Patch(h, false, AccessTools.Method(typeof(DeliveryApp), "SetOpen"), null, nameof(SetOpenPost));
            if (!Healthy) Core.Log.Error("Courier: a required game hook is missing (game update?). The courier is switched off until the mod is updated.");
        }

        private static readonly HashSet<IntPtr> hookedCode = new HashSet<IntPtr>();

        // Address of the native function behind a game method, or zero when it cannot be read.
        internal static IntPtr NativeCode(MethodBase target)
        {
            try
            {
                var field = Il2CppInterop.Common.Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(target);
                if (field == null) return IntPtr.Zero;
                IntPtr info = (IntPtr)field.GetValue(null);
                return info == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(info);
            }
            catch { return IntPtr.Zero; }
        }

        private static void Patch(HarmonyLib.Harmony h, bool required, MethodBase target, string prefix, string postfix)
        {
            string label = prefix ?? postfix;
            try
            {
                if (target == null)
                {
                    Core.Log.Warning("Courier: hook target missing for " + label);
                    if (required) Healthy = false;
                    return;
                }
                // Two game methods can share one native function (the compiler folds identical ones).
                // A second hook on the same function would recurse without end, so it is refused.
                IntPtr code = NativeCode(target);
                if (code != IntPtr.Zero && !hookedCode.Add(code))
                {
                    Core.Log.Warning("Courier: " + target.Name + " shares its code with a method that is already hooked, not hooking it again (" + label + ").");
                    if (required) Healthy = false;
                    return;
                }
                h.Patch(target,
                    prefix != null ? new HarmonyMethod(typeof(Patches).GetMethod(prefix, F)) : null,
                    postfix != null ? new HarmonyMethod(typeof(Patches).GetMethod(postfix, F)) : null);
            }
            catch (Exception e)
            {
                Core.Log.Warning("Courier: could not hook " + target?.Name + " (" + label + "): " + e.Message);
                if (required) Healthy = false;
            }
        }

        // The cargo is the player's own stock, so it costs nothing. The game refuses an order whose
        // cart is worth exactly zero, so a non-empty courier cart reports a value that is positive
        // but so small it disappears when added to the fee (a balance equal to the fee still passes).
        // The amount actually charged is fixed in TransactionPre.
        private static void CartCostPost(DeliveryShop __instance, ref float __result)
        {
            try
            {
                var s = Courier.ByPanel(__instance);
                if (s == null) return;
                __result = Courier.CartHasItems(s) ? 1e-20f : 0f;
            }
            catch { }
        }

        private static void DeliveryFeePost(DeliveryShop __instance, ref float __result)
        {
            try { if (Courier.ByPanel(__instance) != null) __result = Config.DeliveryFee; }
            catch { }
        }

        // Before the game decides whether the order can go out: make sure the panel points at a van
        // that is actually free.
        private static void OrderButtonPre(DeliveryShop __instance)
        {
            try
            {
                var s = Courier.ByPanel(__instance);
                if (s != null) Courier.EnsureVan(s);
            }
            catch { }
        }

        private static void OrderButtonPost(DeliveryShop __instance)
        {
            try
            {
                var s = Courier.ByPanel(__instance);
                if (s == null) return;
                string reason = Courier.BlockReason(s);
                if (reason == null) return;
                if (__instance.OrderButton != null) __instance.OrderButton.interactable = false;
                var note = __instance.OrderButtonNote;
                if (note != null) { note.text = reason; note.enabled = true; }
            }
            catch { }
        }

        // A pick-up point does not offer itself as a destination.
        private static void DestinationsPost(DeliveryShop __instance, Il2CppSystem.Collections.Generic.List<GProperty> __result)
        {
            try
            {
                var s = Courier.ByPanel(__instance);
                if (s == null || __result == null || s.Source == null) return;
                for (int i = __result.Count - 1; i >= 0; i--)
                {
                    var p = __result[i];
                    if (p != null && p.Pointer == s.Source.Pointer) __result.RemoveAt(i);
                }
            }
            catch { }
        }

        private static void SendDeliveryPre(DeliveryInstance delivery)
        {
            try { if (Courier.Submitting != null) Courier.OnDeliveryCreated(delivery); }
            catch (Exception e) { Core.Log.Warning("Courier: SendDelivery hook failed: " + e.Message); }
        }

        // A courier run is not a purchase: no "item acquired" tracking and no reorder receipt.
        private static bool SkipWhileSubmitting()
        {
            return Courier.Submitting == null;
        }

        private static void TransactionPre(ref string _transaction_Name, ref float _unit_Amount, ref float _quantity)
        {
            try
            {
                var s = Courier.Submitting;
                if (s == null) return;
                _transaction_Name = Config.ServiceName + " run from " + s.Title;
                _unit_Amount = -Config.DeliveryFee;
                _quantity = 1f;
            }
            catch { }
        }

        // For a courier run the van is loaded with the exact stacks from the manifest. Once a run is
        // recognised the game's "default items by id" loading never runs on top of ours.
        private static bool AddItemsPre(DeliveryInstance __instance)
        {
            CourierRun run = null;
            try
            {
                State.Sync();
                if (__instance == null || !Courier.TryGetRun(__instance.DeliveryID, out run)) return true;
                if (run.Loaded) return false;
                return !Courier.LoadVan(__instance, run);
            }
            catch (Exception e)
            {
                Core.Log.Warning("Courier: van loading failed: " + e);
                return run == null || !run.Loaded;
            }
        }

        private static void AssignDeliveryPost(DeliveryStatusDisplay __instance, DeliveryInstance instance)
        {
            try
            {
                State.Sync();
                if (instance != null && Courier.TryGetRun(instance.DeliveryID, out CourierRun run))
                    Courier.ApplyRunLabel(__instance, run, true);
            }
            catch (Exception e) { Core.Log.Warning("Courier: could not label the delivery: " + e.Message); }
        }

        private static void SetOpenPost(DeliveryApp __instance, bool open)
        {
            try { if (open) Courier.OnAppOpened(__instance); }
            catch (Exception e) { Core.Log.Warning("Courier: app open hook failed: " + e.Message); }
        }
    }
}
