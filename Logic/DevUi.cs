using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using Il2CppScheduleOne.UI.Phone.Delivery;

namespace SupplyRunLogic
{
    // Development-only commands that drive the courier purely through the game objects in the phone UI
    // (found by name), the way a player's clicks would. They work no matter which copy of the mod built
    // that UI, so they can test the release DLL as well as the hot-reloaded logic. Not in the release mod.
    internal static class DevUi
    {
        private static DeliveryShop Panel(string code)
        {
            var app = Game.App;
            if (app == null || code == null) return null;
            string name = "SupplyRun " + code.ToLowerInvariant();
            var list = app.deliveryShops;
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null && list[i].name == name) return list[i];
            // not a pick-up point: one of the game's own delivery shops, by shop name
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null && !list[i].name.StartsWith("SupplyRun ") && string.Equals(list[i].MatchingShopInterfaceName, code, StringComparison.OrdinalIgnoreCase)) return list[i];
            return null;
        }

        private static Transform Card(string code)
        {
            var app = Game.App;
            if (app == null || code == null) return null;
            var parent = app._shopElements[0].Button.transform.parent;
            return parent.Find("SupplyRun " + code.ToLowerInvariant() + " Button");
        }

        internal static string Handle(string cmd, NameValueCollection q)
        {
            switch (cmd)
            {
                case "g.open":
                {
                    var card = Card(q["prop"]);
                    if (card == null) return "no card for " + q["prop"];
                    card.GetComponent<Button>().onClick.Invoke();
                    return Info(q["prop"]);
                }
                case "g.openshop":
                {
                    var p = Panel(q["prop"]);
                    if (p == null) return "no shop " + q["prop"];
                    Game.App.OpenShop(p);
                    return Info(q["prop"]);
                }
                case "g.app":
                {
                    // open or close the Deliveries app itself (the release mod builds its cards on open)
                    Game.App.SetOpen(q["open"] != "0");
                    return "app open=" + Game.App.isOpen;
                }
                case "g.samecode":
                {
                    // which hooked delivery methods share one native function
                    var dm = typeof(Il2CppScheduleOne.Delivery.DeliveryManager);
                    var sb = new StringBuilder();
                    foreach (string n in new[] { "RecordDeliveryReceipt_Server", "RpcWriter___Server_RecordDeliveryReceipt_Server_2582461062", "SendDelivery" })
                        sb.AppendLine(n + " = 0x" + Patches.NativeCode(HarmonyLib.AccessTools.Method(dm, n)).ToInt64().ToString("X"));
                    return sb.ToString();
                }
                case "g.panel": return Info(q["prop"]);
                case "g.close":
                {
                    var p = Panel(q["prop"]);
                    if (p == null) return "no panel";
                    p.BackButton.onClick.Invoke();
                    return "back clicked, open=" + p.IsOpen;
                }
                case "g.qty":
                {
                    var p = Panel(q["prop"]);
                    if (p == null) return "no panel";
                    int.TryParse(q["row"] ?? "0", out int row);
                    int.TryParse(q["q"] ?? "1", out int n);
                    if (row < 0 || row >= p.listingEntries.Count) return "row out of range (" + p.listingEntries.Count + ")";
                    p.listingEntries[row].SetQuantity(n, true);
                    return Info(q["prop"]);
                }
                case "g.dest":
                {
                    var p = Panel(q["prop"]);
                    if (p == null) return "no panel";
                    var dd = p.DestinationDropdown;
                    int idx = -1;
                    string want = (q["to"] ?? "").Replace(" ", "");
                    for (int i = 0; i < dd.options.Count; i++)
                        if (string.Equals(dd.options[i].text.Replace(" ", ""), want, StringComparison.OrdinalIgnoreCase)) idx = i;
                    if (idx < 0) return "destination not offered\n" + Info(q["prop"]);
                    dd.value = idx;
                    int.TryParse(q["dock"] ?? "1", out int dock);
                    p.LoadingDockDropdown.value = dock;
                    return Info(q["prop"]);
                }
                case "g.esc":
                {
                    // what the Escape key does while the Deliveries app is in front
                    var app = Game.App;
                    var exit = new Il2CppScheduleOne.ExitAction(Il2CppScheduleOne.ExitType.Primary);
                    app.OnExit(exit);
                    var p = Panel(q["prop"]);
                    return $"exit used={exit.Used} appOpen={app.isOpen} panelOpen={(p != null ? p.IsOpen.ToString() : "n/a")}";
                }
                case "g.order":
                {
                    var p = Panel(q["prop"]);
                    if (p == null) return "no panel";
                    p.OrderButton.onClick.Invoke();
                    return "order clicked";
                }
            }
            return null;
        }

        private static string Info(string code)
        {
            var p = Panel(code);
            if (p == null) return "no panel for " + code;
            var sb = new StringBuilder();
            var title = p.transform.Find("Container/Header/Container/Labels/Container/Title");
            var desc = p.transform.Find("Container/Header/Container/Labels/Container/Description");
            sb.AppendLine($"panel '{p.name}' open={p.IsOpen} active={p.gameObject.activeInHierarchy} van='{p.MatchingShopInterfaceName}' title='{(title != null ? title.GetComponent<Text>().text : "?")}' desc='{(desc != null ? desc.GetComponent<Text>().text : "?")}'");
            for (int i = 0; i < p.listingEntries.Count; i++)
            {
                var e = p.listingEntries[i];
                sb.AppendLine($"  row {i}: '{e.ItemNameLabel.text}' / '{e.ItemPriceLabel.text}' selected={e.SelectedQuantity}");
            }
            sb.AppendLine($"  itemTotal='{p.ItemTotalLabel.text}' fee='{p.DeliveryFeeLabel.text}' total='{p.OrderTotalLabel.text}' time='{p.DeliveryTimeLabel.text}'");
            var opts = new List<string>();
            for (int i = 0; i < p.DestinationDropdown.options.Count; i++) opts.Add(p.DestinationDropdown.options[i].text);
            sb.AppendLine($"  dest={(p.destinationProperty != null ? p.destinationProperty.PropertyCode : "none")} dock={p.loadingDockIndex} options=[{string.Join(", ", opts)}]");
            sb.AppendLine($"  orderButton.interactable={p.OrderButton.interactable} note='{p.OrderButtonNote.text}' noteEnabled={p.OrderButtonNote.enabled}");
            return sb.ToString();
        }
    }
}
