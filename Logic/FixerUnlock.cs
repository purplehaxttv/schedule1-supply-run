using System;
using UnityEngine;
using UnityEngine.Events;
using Il2CppScheduleOne.Dialogue;
using Il2CppScheduleOne.Money;
using Il2CppScheduleOne.UI;

namespace SupplyRunLogic
{
    // The courier is bought once from the Fixer, the same contact who supplies employees.
    internal static class FixerUnlock
    {
        private static DialogueController controller;
        private static DialogueController.DialogueChoice choice;
        private static DialogueController.DialogueChoice exitChoice;
        private static int exitChoicePriority;
        private static float nextLook;
        private static string pendingLine;
        private static float pendingLineAt;

        internal static bool Installed => controller != null && choice != null;

        internal static string ChoiceText =>
            $"I need a courier to move product between my properties ({MoneyManager.FormatAmount(Config.UnlockPrice, false, false)})";

        private static bool ShouldShow()
        {
            try { return Game.IsHost && Patches.Healthy && Config.RequireUnlock && !State.Data.Unlocked; }
            catch { return false; }
        }

        internal static void Tick()
        {
            if (pendingLine != null && Time.unscaledTime >= pendingLineAt)
            {
                string line = pendingLine;
                pendingLine = null;
                try { if (controller != null && controller.handler != null) controller.handler.ShowWorldspaceDialogue(line, 9f); }
                catch (Exception e) { Core.Log.Warning("Courier: Fixer line failed: " + e.Message); }
            }

            if (!Game.IsHost) return;
            if (Installed)
            {
                // keep the price in the line current if the setting changes
                try { string t = ChoiceText; if (choice.ChoiceText != t) choice.ChoiceText = t; } catch { }
                return;
            }
            if (Time.unscaledTime < nextLook) return;
            nextLook = Time.unscaledTime + 3f;
            var c = UnityEngine.Object.FindObjectOfType<DialogueController_Fixer>();
            if (c != null) Install(c);
        }

        private static void Install(DialogueController c)
        {
            var ch = new DialogueController.DialogueChoice();
            ch.ChoiceText = ChoiceText;
            ch.Enabled = true;
            ch.Conversation = null;
            if (ch.onChoosen == null) ch.onChoosen = new UnityEvent();
            ch.onChoosen.AddListener((UnityAction)OnChosen);
            ch.shouldShowCheck = new Func<bool, bool>(enabled => ShouldShow());
            // Keep the Fixer's "Nothing" (the plain way out of the conversation) as the last option.
            exitChoice = null;
            try
            {
                int found = 0;
                DialogueController.DialogueChoice candidate = null;
                for (int i = 0; i < c.Choices.Count; i++)
                {
                    var o = c.Choices[i];
                    if (o == null || o.Conversation != null || o.shouldShowCheck != null || o.isValidCheck != null) continue;
                    if (o.onChoosen != null && o.onChoosen.GetPersistentEventCount() > 0) continue;
                    candidate = o;
                    found++;
                }
                if (found == 1)
                {
                    exitChoice = candidate;
                    exitChoicePriority = candidate.Priority;
                    candidate.Priority = exitChoicePriority - 1;
                }
            }
            catch { exitChoice = null; }

            c.AddDialogueChoice(ch, 0);
            controller = c;
            choice = ch;
            Core.Log.Msg("Courier: Fixer conversation hooked.");
        }

        private static void Say(string line)
        {
            pendingLine = line;
            pendingLineAt = Time.unscaledTime + 0.4f;
        }

        internal static void OnChosen()
        {
            try
            {
                if (State.Data.Unlocked) return;
                var mm = Game.Money;
                float price = Config.UnlockPrice;
                string priceText = MoneyManager.FormatAmount(price, false, false);
                if (mm == null) return;
                if (mm.cashBalance < price)
                {
                    Say($"Setting up a courier is {priceText}, cash. Come back when you have it.");
                    return;
                }
                if (price > 0f) mm.ChangeCashBalance(-price, true, true);
                State.Data.Unlocked = true;
                Say($"Done. Name a storage rack \"{Config.RackName}\" at any of your properties, then book the run in your Deliveries app.");
                try
                {
                    var nm = UnityEngine.Object.FindObjectOfType<NotificationsManager>();
                    var app = Game.App;
                    if (nm != null) nm.SendNotification(Config.ServiceName + " service unlocked", "See your Deliveries app", app != null ? app.AppIcon : null, 6f, true);
                }
                catch { }
                Core.Log.Msg($"Courier: service bought from the Fixer for {priceText}.");
                Courier.SyncShops();
            }
            catch (Exception e) { Core.Log.Warning("Courier: unlock failed: " + e); }
        }

        internal static void Forget()
        {
            controller = null;
            choice = null;
            exitChoice = null;
            pendingLine = null;
        }

        internal static void Teardown()
        {
            try
            {
                if (controller != null && choice != null && controller.Choices != null) controller.Choices.Remove(choice);
                if (controller != null && exitChoice != null) exitChoice.Priority = exitChoicePriority;
            }
            catch { }
            Forget();
        }
    }
}
