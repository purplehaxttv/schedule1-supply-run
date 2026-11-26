using System;
using MelonLoader;
using UnityEngine;
using Il2CppScheduleOne.Delivery;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Money;
using Il2CppScheduleOne.Persistence;
using Il2CppScheduleOne.UI.Phone.Delivery;

namespace SupplyRunLogic
{
    // Cached access to the game singletons the mod needs.
    internal static class Game
    {
        private static LoadManager load;
        private static DeliveryManager deliveries;
        private static MoneyManager money;
        private static SaveManager save;

        internal static LoadManager Load
        {
            get { if (load == null) load = UnityEngine.Object.FindObjectOfType<LoadManager>(); return load; }
        }

        internal static DeliveryManager Deliveries
        {
            get { if (deliveries == null) deliveries = UnityEngine.Object.FindObjectOfType<DeliveryManager>(); return deliveries; }
        }

        internal static MoneyManager Money
        {
            get { if (money == null) money = UnityEngine.Object.FindObjectOfType<MoneyManager>(); return money; }
        }

        internal static SaveManager Save
        {
            get { if (save == null) save = UnityEngine.Object.FindObjectOfType<SaveManager>(); return save; }
        }

        // The local player's Deliveries phone app.
        internal static DeliveryApp App
        {
            get
            {
                try { if (PlayerSingleton<DeliveryApp>.InstanceExists) return PlayerSingleton<DeliveryApp>.Instance; }
                catch { }
                return null;
            }
        }

        // The courier moves real items around, so only the host (or a single-player game) runs it.
        internal static bool IsHost
        {
            get { try { return Il2CppFishNet.InstanceFinder.IsServer; } catch { return false; } }
        }

        internal static bool InGame
        {
            get
            {
                try { var lm = Load; return lm != null && lm.IsGameLoaded && Core.Scene != "Menu"; }
                catch { return false; }
            }
        }
    }

    // Lifecycle of the gameplay feature. The host (dev host or the release mod) drives these.
    internal static class Core
    {
        internal static MelonLogger.Instance Log;
        internal static string Scene = "";

        private static HarmonyLib.Harmony harmony;
        private static float nextTick;

        internal static void Init(MelonLogger.Instance log, string scene)
        {
            Log = log;
            Scene = scene ?? "";
            Config.Init();
            harmony = new HarmonyLib.Harmony("purplehax.supplyrun." + Guid.NewGuid().ToString("N"));
            Patches.Apply(harmony);
            Log.Msg("Supply Run courier ready.");
        }

        internal static void Shutdown()
        {
            try { harmony?.UnpatchSelf(); } catch (Exception e) { Log?.Warning("unpatch: " + e.Message); }
            State.DetachSaveHook();
            try { FixerUnlock.Teardown(); } catch (Exception e) { Log?.Warning("fixer teardown: " + e.Message); }
            try { Courier.Teardown(); } catch (Exception e) { Log?.Warning("courier teardown: " + e.Message); }
        }

        internal static void OnScene(string scene)
        {
            Scene = scene ?? "";
            if (Scene == "Menu")
            {
                Courier.Forget();
                FixerUnlock.Forget();
                State.Reset();
            }
        }

        internal static void Tick()
        {
            if (Time.unscaledTime < nextTick) return;
            nextTick = Time.unscaledTime + 0.5f;
            State.AttachSaveHook();
            if (!Game.InGame) return;
            State.Sync();
            FixerUnlock.Tick();
            Courier.Tick();
        }
    }
}
