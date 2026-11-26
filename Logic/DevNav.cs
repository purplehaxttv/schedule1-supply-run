using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Text;
using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppScheduleOne;
using Il2CppScheduleOne.Delivery;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.ObjectScripts;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.UI.Phone.Delivery;
using GProperty = Il2CppScheduleOne.Property.Property;

namespace SupplyRunLogic
{
    // Development-only commands for the cases that need a controller, a second player or nine busy
    // vans to reach by hand: they drive the game's own navigation and delivery code directly.
    // Object-based, so they work against the release DLL. Not in the release mod.
    internal static class DevNav
    {
        private static string Owner(Component c)
        {
            if (c == null) return "null";
            DeliveryShop shop = null;
            for (Transform t = c.transform; t != null && shop == null; t = t.parent) shop = t.GetComponent<DeliveryShop>();
            return (shop != null ? shop.name + "/" : "") + c.name;
        }

        private static string NavState()
        {
            var app = Game.App;
            var screen = app._deliveryScreen;
            var sb = new StringBuilder();
            var panels = screen.panels;
            int courier = 0, dead = 0;
            for (int i = 0; i < panels.Count; i++)
            {
                var p = panels[i];
                if (p == null) { dead++; continue; }
                string owner = Owner(p);
                if (!owner.StartsWith("SupplyRun")) continue;
                courier++;
                sb.AppendLine($"  registered '{owner}' parentScreen={(p.ParentScreen != null)} locked={p.IsLocked} visible={p.IsPanelVisible()} selectables={p.selectables.Count}");
            }
            sb.Insert(0, $"screen panels={panels.Count} courierPanels={courier} destroyedEntries={dead} screenSelected={screen.IsSelected}\n");
            var cur = screen.CurrentSelectedPanel;
            sb.AppendLine($"  current panel='{Owner(cur)}' selectable='{(cur != null && cur.CurrentSelectedSelectable != null ? cur.CurrentSelectedSelectable.name : "none")}'");
            return sb.ToString();
        }

        internal static string Handle(string cmd, NameValueCollection q)
        {
            switch (cmd)
            {
                case "g.navstate": return NavState();
                case "g.nav":
                {
                    // one press of a direction on the pad, delivered where the game delivers it
                    var screen = Game.App._deliveryScreen;
                    var cur = screen.CurrentSelectedPanel;
                    if (cur == null) return "no current panel\n" + NavState();
                    Vector2 dir;
                    switch ((q["dir"] ?? "right").ToLowerInvariant())
                    {
                        case "left": dir = new Vector2(-1, 0); break;
                        case "up": dir = new Vector2(0, 1); break;
                        case "down": dir = new Vector2(0, -1); break;
                        default: dir = new Vector2(1, 0); break;
                    }
                    cur.ResetNavigationData();
                    bool moved = cur.Navigate(dir);
                    var now = screen.CurrentSelectedPanel;
                    return $"moved={moved} panel='{Owner(now)}' selectable='{(now != null && now.CurrentSelectedSelectable != null ? now.CurrentSelectedSelectable.name : "none")}'";
                }
                case "g.navpanel":
                {
                    // the pad's "next section" action
                    var screen = Game.App._deliveryScreen;
                    var cur = screen.CurrentSelectedPanel;
                    Vector2 dir = (q["dir"] ?? "right") == "left" ? new Vector2(-1, 0) : new Vector2(1, 0);
                    bool moved = cur != null && cur.NavigateUsingCyclePanel(dir);
                    var now = screen.CurrentSelectedPanel;
                    return $"moved={moved} panel='{Owner(now)}' selectable='{(now != null && now.CurrentSelectedSelectable != null ? now.CurrentSelectedSelectable.name : "none")}'";
                }
                case "g.navselect":
                {
                    // put the pad focus on a named control of the current panel
                    var cur = Game.App._deliveryScreen.CurrentSelectedPanel;
                    if (cur == null) return "no current panel";
                    var list = cur.selectables;
                    var names = new List<string>();
                    for (int i = 0; i < list.Count; i++)
                    {
                        names.Add(list[i].name);
                        if (string.Equals(list[i].name, q["name"], StringComparison.OrdinalIgnoreCase))
                        {
                            cur.SelectSelectable(list[i], false);
                            return "selected '" + list[i].name + "' in '" + Owner(cur) + "'";
                        }
                    }
                    return "no such control; panel '" + Owner(cur) + "' has [" + string.Join(", ", names) + "]";
                }
                case "g.navclick":
                {
                    // the pad's confirm button on whatever has focus
                    var cur = Game.App._deliveryScreen.CurrentSelectedPanel;
                    if (cur == null || cur.CurrentSelectedSelectable == null) return "nothing focused";
                    string name = cur.CurrentSelectedSelectable.name;
                    cur.SendClickEventToCurrentSelectedSelectable();
                    return "confirm pressed on '" + name + "'";
                }
                case "c.fillvans": return FillVans(q);
                case "c.complete": return Complete(q);
                case "c.fakeuse": return FakeUse(q);
            }
            return null;
        }

        // Put a far-away delivery on the road for every vanilla shop that has none, so no van is free.
        private static string FillVans(NameValueCollection q)
        {
            var app = Game.App;
            var dm = Game.Deliveries;
            int made = 0;
            int.TryParse(q["leave"] ?? "0", out int leave);
            var els = app._shopElements;
            for (int i = 0; i < els.Count; i++)
            {
                var si = els[i].Shop.MatchingShop;
                if (si == null || si.DeliveryVehicle == null) continue;
                bool busy = false;
                var ds = dm.Deliveries;
                for (int k = 0; k < ds.Count; k++) if (ds[k].StoreName == si.ShopName) busy = true;
                if (busy) continue;
                if (leave > 0) { leave--; continue; }
                var items = new Il2CppReferenceArray<StringIntPair>(1);
                items[0] = new StringIntPair("cuke", 1);
                var d = new DeliveryInstance(Guid.NewGuid().ToString(), si.ShopName, "dockswarehouse", 0, items, EDeliveryStatus.InTransit, 5000);
                dm.SendDelivery(d);
                made++;
            }
            return $"made {made} filler deliveries, total now {dm.Deliveries.Count}";
        }

        // Finish deliveries of one store (or all filler ones) the way the game does when a van is emptied.
        private static string Complete(NameValueCollection q)
        {
            var dm = Game.Deliveries;
            var ids = new List<string>();
            var ds = dm.Deliveries;
            for (int k = 0; k < ds.Count; k++)
            {
                var d = ds[k];
                bool filler = d.DestinationCode == "dockswarehouse" && d.TimeUntilArrival > 1000;
                if ((q["store"] != null && d.StoreName == q["store"]) || (q["store"] == null && filler)) ids.Add(d.DeliveryID);
            }
            foreach (string id in ids) dm.SetDeliveryState(id, EDeliveryStatus.Completed);
            return $"completed {ids.Count}, total now {dm.Deliveries.Count}";
        }

        // Make a property's first courier storage look opened by a player (stands in for a co-op partner).
        private static string FakeUse(NameValueCollection q)
        {
            GProperty p = null;
            var props = GProperty.OwnedProperties;
            for (int i = 0; i < props.Count; i++)
                if (string.Equals(props[i].PropertyCode, q["prop"], StringComparison.OrdinalIgnoreCase)) p = props[i];
            if (p == null) return "no property";
            var builds = p.BuildableItems;
            for (int i = 0; i < builds.Count; i++)
            {
                var r = builds[i] != null ? builds[i].TryCast<PlaceableStorageEntity>() : null;
                if (r == null || !Courier.IsCourierRack(r)) continue;
                r.StorageEntity.CurrentPlayerAccessor = q["on"] == "0" ? null : Player.Local;
                return $"rack '{r.GetManagementName()}' accessor={(r.StorageEntity.CurrentPlayerAccessor != null ? r.StorageEntity.CurrentPlayerAccessor.PlayerName : "none")} isOpened={r.StorageEntity.IsOpened}";
            }
            return "no courier rack";
        }
    }
}
