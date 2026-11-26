using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Il2CppScheduleOne.Delivery;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.ObjectScripts;
using Il2CppScheduleOne.Persistence;
using Il2CppScheduleOne.Storage;
using Il2CppScheduleOne.UI.Items;
using Il2CppScheduleOne.UI.Phone.Delivery;
using Il2CppScheduleOne.UI.Shop;
using GProperty = Il2CppScheduleOne.Property.Property;

namespace SupplyRunLogic
{
    // The courier service: every owned property with a courier rack becomes a pick-up point in the
    // phone's Deliveries app. Ordering from it ships the exact stacks sitting in that rack to another
    // property, using the game's own delivery van, order flow and loading docks.
    internal static class Courier
    {
        // One catalog row: one exact item and how much of it the racks hold.
        internal class Row
        {
            // The item's own save data with the quantity set to 1. Two stacks belong to the same row
            // only if this text is identical, so packaging, quality, colour, ammo, fill level and any
            // other per-item state always survive the trip.
            public string Key;
            public string ItemId;
            public string Name;
            public string Label;
            public string Detail;
            public Sprite Icon;
            public int Available;
            public StorableItemDefinition Definition;
            public ListingEntry Entry;
        }

        // One pick-up point (a property) as it exists in the phone UI.
        internal class Shop
        {
            public string Code;
            public GProperty Source;
            public string Title;
            public DeliveryShop Panel;
            public IntPtr PanelPtr;
            public GameObject Card;
            public Text CardTitle;
            public Text CardDescription;
            public Text PanelTitle;
            public Text PanelDescription;
            public string CardDescriptionText;
            public List<Row> Rows = new List<Row>();
            public List<StorageEntity> Storages = new List<StorageEntity>();
            // Stand-in item definitions made for rows whose item is rank-locked in shops (see BuildCatalog).
            public List<StorableItemDefinition> TempDefinitions = new List<StorableItemDefinition>();
            public float NextVanCheck;
            public float StaleSince = -1f;
        }

        private static DeliveryApp app;
        private static readonly Dictionary<string, Shop> shops = new Dictionary<string, Shop>();
        private static readonly Dictionary<IntPtr, Shop> byPanel = new Dictionary<IntPtr, Shop>();
        private static GameObject hintCard;

        // Set only while our own Place Order handler is inside DeliveryShop.SubmitOrder.
        internal static Shop Submitting;
        private static CourierRun pendingRun;
        private static string capturedId;

        private static ShopInterface cachedVan;
        private static float cachedVanUntil;
        private static float nextPrune;
        private static float nextSync;

        internal static bool Usable => Game.IsHost && State.Unlocked && Patches.Healthy;

        internal static IEnumerable<Shop> Shops => shops.Values;

        internal static Shop ByPanel(DeliveryShop panel)
        {
            if (panel == null || byPanel.Count == 0) return null;
            byPanel.TryGetValue(panel.Pointer, out Shop s);
            return s;
        }

        internal static Shop ByCode(string code)
        {
            if (code == null) return null;
            shops.TryGetValue(code.ToLowerInvariant(), out Shop s);
            return s;
        }

        // ---------------------------------------------------------------- names

        internal static string ShopTitle(GProperty p)
        {
            return Config.CustomShopName(p.PropertyCode, p.PropertyName) ?? p.PropertyName;
        }

        internal static string RunLabel(CourierRun run)
        {
            string title = run.SourceName;
            var p = FindProperty(run.SourceCode);
            if (p != null) title = ShopTitle(p);
            return Config.ServiceName + ": " + title;
        }

        private static GProperty FindProperty(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            var props = GProperty.OwnedProperties;
            if (props == null) return null;
            for (int i = 0; i < props.Count; i++)
            {
                var p = props[i];
                if (p != null && string.Equals(p.PropertyCode, code, StringComparison.OrdinalIgnoreCase)) return p;
            }
            return null;
        }

        // ---------------------------------------------------------------- racks

        private static string RackName(PlaceableStorageEntity rack)
        {
            try { string n = rack.GetManagementName(); if (!string.IsNullOrEmpty(n)) return n; } catch { }
            try { return rack.Name ?? ""; } catch { return ""; }
        }

        internal static bool IsCourierRack(PlaceableStorageEntity rack)
        {
            if (!Config.RequireRack) return true;
            return RackName(rack).IndexOf(Config.RackName, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // The storage units of a property the courier is allowed to take from.
        internal static List<StorageEntity> SourceStorages(GProperty p)
        {
            var list = new List<StorageEntity>();
            if (p == null) return list;
            var builds = p.BuildableItems;
            if (builds == null) return list;
            for (int i = 0; i < builds.Count; i++)
            {
                var b = builds[i];
                if (b == null) continue;
                var rack = b.TryCast<PlaceableStorageEntity>();
                if (rack == null || !IsCourierRack(rack)) continue;
                var se = rack.StorageEntity;
                if (se != null) list.Add(se);
            }
            return list;
        }

        // True while a player has one of these storage units open. Their view of the slots can be a
        // network round-trip ahead of ours, so nothing is taken from a rack that is in use.
        private static bool AnyInUse(List<StorageEntity> storages)
        {
            for (int i = 0; i < storages.Count; i++)
            {
                var se = storages[i];
                try { if (se != null && (se.CurrentPlayerAccessor != null || se.IsOpened)) return true; }
                catch { }
            }
            return false;
        }

        // The exact identity of an item: its save data with the quantity normalised.
        private static string KeyOf(ItemInstance inst)
        {
            return inst.GetCopy(1).GetItemData().GetJson(false);
        }

        private static bool Shippable(ItemSlot slot, out ItemInstance inst, out int quantity)
        {
            inst = null;
            quantity = 0;
            if (slot == null) return false;
            inst = slot.ItemInstance;
            if (inst == null) return false;
            quantity = slot.Quantity;
            if (quantity <= 0) return false;
            if (inst.TryCast<CashInstance>() != null) return false;
            return true;
        }

        private static string ItemLabel(ItemInstance inst)
        {
            try { return inst.Name; } catch { return inst.ID; }
        }

        // The quality of a product, or "" for items that have none.
        private static string ItemDetail(ItemInstance inst)
        {
            var q = inst.TryCast<QualityItemInstance>();
            return q != null ? q.Quality.ToString() : "";
        }

        private static string ReadyText(Row row)
        {
            return (row.Detail.Length > 0 ? row.Detail + ", " : "") + row.Available + " ready";
        }

        // Group everything in the given storage units into rows of identical items.
        internal static List<Row> ScanRows(List<StorageEntity> storages)
        {
            var rows = new List<Row>();
            var byKey = new Dictionary<string, Row>();
            foreach (StorageEntity se in storages)
            {
                var slots = se.ItemSlots;
                if (slots == null) continue;
                for (int j = 0; j < slots.Count; j++)
                {
                    if (!Shippable(slots[j], out ItemInstance inst, out int qty)) continue;
                    var def = inst.Definition != null ? inst.Definition.TryCast<StorableItemDefinition>() : null;
                    if (def == null) continue;
                    string key = KeyOf(inst);
                    if (!byKey.TryGetValue(key, out Row row))
                    {
                        row = new Row
                        {
                            Key = key,
                            ItemId = inst.ID,
                            Definition = def,
                            Label = ItemLabel(inst),
                            Detail = ItemDetail(inst),
                            Icon = inst.Icon
                        };
                        row.Name = row.Detail.Length > 0 ? row.Label + " [" + row.Detail + "]" : row.Label;
                        byKey[key] = row;
                        rows.Add(row);
                    }
                    row.Available += qty;
                }
            }
            return rows;
        }

        // Take `quantity` of the row's item out of the storage units. Returns how many really left.
        private static int RemoveFromSource(List<StorageEntity> storages, Row row, int quantity)
        {
            int remaining = quantity;
            foreach (StorageEntity se in storages)
            {
                if (remaining <= 0) break;
                var slots = se.ItemSlots;
                if (slots == null) continue;
                for (int j = 0; j < slots.Count && remaining > 0; j++)
                {
                    var slot = slots[j];
                    if (!Shippable(slot, out ItemInstance inst, out int have)) continue;
                    if (inst.ID != row.ItemId || KeyOf(inst) != row.Key) continue;
                    int take = Math.Min(remaining, have);
                    slot.ChangeQuantity(-take, false);
                    int left;
                    try { left = slot.ItemInstance != null ? slot.Quantity : 0; } catch { left = 0; }
                    int applied = Math.Max(0, Math.Min(take, have - left));
                    remaining -= applied;
                }
            }
            return quantity - remaining;
        }

        // ---------------------------------------------------------------- vans

        private static bool IsBusy(string storeName)
        {
            var dm = Game.Deliveries;
            if (dm == null) return true;
            var ds = dm.Deliveries;
            for (int i = 0; i < ds.Count; i++)
            {
                var d = ds[i];
                if (d != null && d.StoreName == storeName) return true;
            }
            return false;
        }

        // Each delivery shop owns one van. A courier run borrows the van of a shop that has nothing on
        // the road, preferring shops the player has not unlocked (their vans are never used otherwise).
        internal static ShopInterface PickVan()
        {
            if (app == null) return null;
            ShopInterface fallback = null;
            var els = app._shopElements;
            if (els == null) return null;
            for (int i = els.Count - 1; i >= 0; i--)
            {
                var el = els[i];
                if (el == null || el.Shop == null) continue;
                var si = el.Shop.MatchingShop;
                if (si == null || si.DeliveryVehicle == null) continue;
                if (IsBusy(si.ShopName)) continue;
                bool locked = el.Button != null && !el.Button.gameObject.activeSelf;
                if (locked) return si;
                if (fallback == null) fallback = si;
            }
            return fallback;
        }

        private static ShopInterface PickVanCached()
        {
            if (Time.unscaledTime > cachedVanUntil)
            {
                cachedVan = PickVan();
                cachedVanUntil = Time.unscaledTime + 0.5f;
            }
            return cachedVan;
        }

        private static ShopInterface AnyShopInterface()
        {
            var els = app._shopElements;
            for (int i = 0; i < els.Count; i++)
            {
                var el = els[i];
                if (el != null && el.Shop != null && el.Shop.MatchingShop != null) return el.Shop.MatchingShop;
            }
            return null;
        }

        // Point the panel at the van it would use. Vanilla code reads the van and "is this shop already
        // delivering" through these two members.
        private static void AssignVan(Shop s, ShopInterface van)
        {
            if (van == null) van = AnyShopInterface();
            if (van == null) return;
            s.Panel.MatchingShop = van;
            s.Panel.MatchingShopInterfaceName = van.ShopName;
        }

        // Called before the game evaluates the order button: if the van this panel points at has gone
        // out in the meantime, move the panel to one that is free.
        internal static void EnsureVan(Shop s)
        {
            if (app == null || s.Panel == null) return;
            if (Time.unscaledTime < s.NextVanCheck) return;
            s.NextVanCheck = Time.unscaledTime + 0.5f;
            if (!IsBusy(s.Panel.MatchingShopInterfaceName)) return;
            ShopInterface van = PickVan();
            if (van != null) AssignVan(s, van);
        }

        private static DeliveryVehicle VanFor(string storeName)
        {
            var all = ShopInterface.AllShops;
            if (all == null) return null;
            for (int i = 0; i < all.Count; i++)
            {
                var si = all[i];
                if (si != null && si.ShopName == storeName && si.DeliveryVehicle != null) return si.DeliveryVehicle;
            }
            return null;
        }

        // ---------------------------------------------------------------- phone UI

        private static Text FindText(Transform root, string path)
        {
            var t = root.Find(path);
            return t != null ? t.GetComponent<Text>() : null;
        }

        private static void SetIcon(Transform root, string path)
        {
            try
            {
                var t = root.Find(path);
                var img = t != null ? t.GetComponent<Image>() : null;
                if (img != null && app.AppIcon != null) img.sprite = app.AppIcon;
            }
            catch { }
        }

        private static DeliveryApp.DeliveryShopElement TemplateElement()
        {
            var els = app._shopElements;
            if (els == null || els.Count == 0) return null;
            DeliveryApp.DeliveryShopElement first = null;
            for (int i = 0; i < els.Count; i++)
            {
                var el = els[i];
                if (el == null || el.Shop == null || el.Button == null || el.Shop.MatchingShop == null) continue;
                if (first == null) first = el;
                if (el.Shop.AvailableByDefault) return el;
            }
            return first;
        }

        private static GameObject CloneCard(DeliveryApp.DeliveryShopElement tpl, string name, string title, string description, out Text titleText, out Text descText)
        {
            var go = UnityEngine.Object.Instantiate(tpl.Button.gameObject, tpl.Button.transform.parent);
            go.name = name;
            go.SetActive(true);
            titleText = FindText(go.transform, "Title");
            descText = FindText(go.transform, "Description");
            if (titleText != null) titleText.text = title;
            if (descText != null) descText.text = description;
            SetIcon(go.transform, "Icon/Image");
            try
            {
                var bg = go.GetComponent<Image>();
                if (bg != null) bg.color = Config.Accent;
                var iconBg = go.transform.Find("Icon");
                var img = iconBg != null ? iconBg.GetComponent<Image>() : null;
                if (img != null) img.color = Color.Lerp(Config.Accent, Color.black, 0.35f);
            }
            catch { }
            var btn = go.GetComponent<Button>();
            btn.onClick.RemoveAllListeners();
            try
            {
                var sel = go.GetComponent<Il2CppScheduleOne.UISelectable>();
                if (sel != null && app._listingPanel != null) app._listingPanel.AddSelectable(sel);
            }
            catch { }
            return go;
        }

        private static void DestroyCard(GameObject card)
        {
            if (card == null) return;
            try
            {
                var sel = card.GetComponent<Il2CppScheduleOne.UISelectable>();
                if (sel != null && app != null && app._listingPanel != null) app._listingPanel.RemoveSelectable(sel, true);
            }
            catch { }
            UnityEngine.Object.Destroy(card);
        }

        private static Shop CreateShop(GProperty p)
        {
            var tpl = TemplateElement();
            if (tpl == null) return null;
            string code = p.PropertyCode.ToLowerInvariant();
            var s = new Shop { Code = code, Source = p, Title = ShopTitle(p) };

            var panelGo = UnityEngine.Object.Instantiate(tpl.Shop.gameObject, tpl.Shop.transform.parent);
            panelGo.name = "SupplyRun " + code;
            panelGo.SetActive(false);
            var panel = panelGo.GetComponent<DeliveryShop>();
            s.Panel = panel;
            s.PanelPtr = panel.Pointer;
            panel.AvailableByDefault = false;
            panel.ShopColor = Config.Accent;
            // Never null: the app looks shops up through MatchingShop and would throw on a null one.
            panel.MatchingShop = tpl.Shop.MatchingShop;
            panel.MatchingShopInterfaceName = tpl.Shop.MatchingShopInterfaceName;
            ClearRows(s);

            s.PanelTitle = FindText(panelGo.transform, "Container/Header/Container/Labels/Container/Title");
            s.PanelDescription = FindText(panelGo.transform, "Container/Header/Container/Labels/Container/Description");
            SetIcon(panelGo.transform, "Container/Header/Container/Icon/Image");
            try
            {
                var header = panelGo.transform.Find("Container/Header");
                var img = header != null ? header.GetComponent<Image>() : null;
                if (img != null) img.color = Config.Accent;
                var iconBg = panelGo.transform.Find("Container/Header/Container/Icon");
                var iconImg = iconBg != null ? iconBg.GetComponent<Image>() : null;
                if (iconImg != null) iconImg.color = Color.Lerp(Config.Accent, Color.black, 0.35f);
            }
            catch { }

            // A clone keeps none of the click handlers the game wires at runtime, so wire our own.
            var theApp = app;
            panel.BackButton.onClick.RemoveAllListeners();
            panel.BackButton.onClick.AddListener((UnityAction)(() => theApp.CloseShop(panel)));
            panel.DestinationDropdown.onValueChanged.RemoveAllListeners();
            panel.DestinationDropdown.onValueChanged.AddListener((UnityAction<int>)(i => panel.DestinationDropdownSelected(i)));
            panel.LoadingDockDropdown.onValueChanged.RemoveAllListeners();
            panel.LoadingDockDropdown.onValueChanged.AddListener((UnityAction<int>)(i => panel.LoadingDockDropdownSelected(i)));
            panel.OrderButton.onClick.RemoveAllListeners();
            panel.OrderButton.onClick.AddListener((UnityAction)(() => PlaceOrder(s)));

            s.Card = CloneCard(tpl, "SupplyRun " + code + " Button", s.Title, "", out s.CardTitle, out s.CardDescription);
            s.Card.GetComponent<Button>().onClick.AddListener((UnityAction)(() => OpenShop(s)));

            // Registered the way the game registers its own shops, so back/escape closes our panel
            // and controller navigation knows the panel's sections.
            app.deliveryShops.Add(panel);
            try
            {
                var sections = panel.Panels;
                if (sections != null && app._deliveryScreen != null)
                    for (int i = 0; i < sections.Count; i++) { if (sections[i] != null) app._deliveryScreen.AddPanel(sections[i]); }
            }
            catch (Exception e) { Core.Log.Warning("Courier: controller navigation not registered: " + e.Message); }

            shops[code] = s;
            byPanel[s.PanelPtr] = s;
            RefreshTexts(s, 0);
            Core.Log.Msg($"Courier: pick-up point '{s.Title}' added to the Deliveries app.");
            return s;
        }

        // A pick-up point that no longer qualifies is removed, but not while its page is open, and not
        // while the game's short close animation is still running on it (the animation's last step
        // touches the panel). A page that stays switched on without being open is removed after a moment.
        private static bool SafeToRemove(Shop s)
        {
            try
            {
                if (s.Panel == null) return true;
                if (s.Panel.IsOpen) { s.StaleSince = -1f; return false; }
                if (!s.Panel.gameObject.activeSelf) return true;
                if (s.StaleSince < 0f) s.StaleSince = Time.time;
                return Time.time - s.StaleSince > 1.5f;
            }
            catch { return true; }
        }

        private static void DestroyTempDefinitions(Shop s)
        {
            foreach (var d in s.TempDefinitions) { try { if (d != null) UnityEngine.Object.Destroy(d); } catch { } }
            s.TempDefinitions.Clear();
        }

        private static void DestroyShop(Shop s)
        {
            shops.Remove(s.Code);
            byPanel.Remove(s.PanelPtr);
            try
            {
                if (app != null && s.Panel != null)
                {
                    if (s.Panel.IsOpen) app.CloseShop(s.Panel);
                    app.deliveryShops.Remove(s.Panel);
                    // A panel that was never shown does not unregister itself when destroyed.
                    var sections = s.Panel.Panels;
                    if (sections != null && app._deliveryScreen != null)
                        for (int i = 0; i < sections.Count; i++) { if (sections[i] != null) app._deliveryScreen.RemovePanel(sections[i]); }
                }
            }
            catch { }
            DestroyTempDefinitions(s);
            DestroyCard(s.Card);
            if (s.Panel != null) UnityEngine.Object.Destroy(s.Panel.gameObject);
        }

        private static void RefreshTexts(Shop s, int items)
        {
            s.Title = ShopTitle(s.Source);
            string desc = items > 0
                ? $"{Config.ServiceName} pick-up, {items} item{(items == 1 ? "" : "s")} ready"
                : $"{Config.ServiceName} pick-up, nothing on the rack";
            if (s.CardTitle != null && s.CardTitle.text != s.Title) s.CardTitle.text = s.Title;
            if (s.CardDescription != null && s.CardDescriptionText != desc) { s.CardDescription.text = desc; s.CardDescriptionText = desc; }
            if (s.PanelTitle != null && s.PanelTitle.text != s.Title) s.PanelTitle.text = s.Title;
            string pd = $"{Config.ServiceName} pick-up at {s.Source.PropertyName}";
            if (s.PanelDescription != null && s.PanelDescription.text != pd) s.PanelDescription.text = pd;
        }

        private static int CountItems(List<StorageEntity> storages)
        {
            int n = 0;
            foreach (StorageEntity se in storages)
            {
                try { n += se.ItemCount; } catch { }
            }
            return n;
        }

        // Bring the cards in the Deliveries app in line with which properties have courier racks.
        internal static void SyncShops()
        {
            if (app == null) return;
            var want = new Dictionary<string, KeyValuePair<GProperty, int>>();
            if (Usable)
            {
                var props = GProperty.OwnedProperties;
                if (props != null)
                {
                    for (int i = 0; i < props.Count; i++)
                    {
                        var p = props[i];
                        if (p == null || string.IsNullOrEmpty(p.PropertyCode)) continue;
                        var storages = SourceStorages(p);
                        if (storages.Count == 0) continue;
                        want[p.PropertyCode.ToLowerInvariant()] = new KeyValuePair<GProperty, int>(p, CountItems(storages));
                    }
                }
            }

            var stale = new List<Shop>();
            foreach (Shop s in shops.Values)
            {
                if (want.ContainsKey(s.Code)) { s.StaleSince = -1f; continue; }
                if (SafeToRemove(s)) stale.Add(s);
            }
            foreach (Shop s in stale) DestroyShop(s);

            foreach (var kv in want)
            {
                if (!shops.TryGetValue(kv.Key, out Shop s)) s = CreateShop(kv.Value.Key);
                if (s != null) RefreshTexts(s, kv.Value.Value);
            }

            // Unlocked but no courier rack anywhere: show a card that says how to set one up.
            bool showHint = Usable && want.Count == 0;
            if (showHint && hintCard == null)
            {
                var tpl = TemplateElement();
                if (tpl != null)
                    hintCard = CloneCard(tpl, "SupplyRun Hint Button", Config.ServiceName,
                        $"Name a storage rack \"{Config.RackName}\" to ship from it", out _, out _);
            }
            else if (!showHint && hintCard != null)
            {
                DestroyCard(hintCard);
                hintCard = null;
            }
        }

        private static void ClearRows(Shop s)
        {
            var panel = s.Panel;
            var container = panel.ListingContainer;
            if (container != null)
            {
                for (int i = container.childCount - 1; i >= 0; i--)
                {
                    var ch = container.GetChild(i);
                    if (ch.GetComponent<ListingEntry>() == null) continue;
                    try
                    {
                        var sel = ch.GetComponent<Il2CppScheduleOne.UISelectable>();
                        if (sel != null && panel._entriesPanel != null) panel._entriesPanel.RemoveSelectable(sel, false);
                    }
                    catch { }
                    ch.gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(ch.gameObject);
                }
            }
            if (panel.listingEntries != null) panel.listingEntries.Clear();
            s.Rows.Clear();
            DestroyTempDefinitions(s);
        }

        // Rebuild the pick-up point's catalog from what is in its racks right now.
        internal static void BuildCatalog(Shop s)
        {
            ClearRows(s);
            var panel = s.Panel;
            s.Storages = SourceStorages(s.Source);
            var rows = ScanRows(s.Storages);
            foreach (Row row in rows)
            {
                // The phone only lets the player pick items their rank has unlocked for purchase.
                // This is not a purchase: anything the player already owns can be shipped. For a
                // rank-locked item the row gets a private copy of the definition without the rank
                // requirement, so the real definition (and every real shop) is left untouched.
                StorableItemDefinition shown = row.Definition;
                try
                {
                    if (!shown.IsUnlocked)
                    {
                        var copy = UnityEngine.Object.Instantiate(row.Definition);
                        copy.RequiresLevelToPurchase = false;
                        s.TempDefinitions.Add(copy);
                        shown = copy;
                    }
                }
                catch (Exception e) { Core.Log.Warning("Courier: could not prepare '" + row.Name + "': " + e.Message); }

                var listing = new ShopListing();
                listing.name = row.Name;
                listing.Item = shown;
                listing.OverridePrice = true;
                listing.OverriddenPrice = 0f;
                listing.LimitedStock = true;
                listing.DefaultStock = row.Available;
                listing.CurrentStock = row.Available;
                listing.CanBeDelivered = true;
                listing.StockVariableName = "";
                listing.PurchasedQuantityVariableName = "";
                listing.ConditionalVisibilityVariableName = "";

                var go = UnityEngine.Object.Instantiate(panel.ListingEntryPrefab.gameObject, panel.ListingContainer);
                go.SetActive(true);
                var entry = go.GetComponent<ListingEntry>();
                entry.Initialize(listing);
                if (entry.ItemNameLabel != null) entry.ItemNameLabel.text = row.Label;
                if (entry.ItemPriceLabel != null) entry.ItemPriceLabel.text = ReadyText(row);
                if (entry.Icon != null && row.Icon != null) entry.Icon.sprite = row.Icon;
                row.Entry = entry;

                Row captured = row;
                entry.onQuantityChanged.AddListener((UnityAction)(() => OnQuantityChanged(s, captured)));
                try
                {
                    var sel = go.GetComponent<Il2CppScheduleOne.UISelectable>();
                    if (sel != null && panel._entriesPanel != null) panel._entriesPanel.AddSelectable(sel);
                }
                catch { }
                panel.listingEntries.Add(entry);
                s.Rows.Add(row);
            }
            try { LayoutRebuilder.ForceRebuildLayoutImmediate(panel.ListingContainer); } catch { }
            cachedVanUntil = 0f;
            s.NextVanCheck = 0f;
            AssignVan(s, PickVan());
            RefreshTexts(s, CountItems(s.Storages));
        }

        private static void OnQuantityChanged(Shop s, Row row)
        {
            try
            {
                if (row.Entry == null || s.Panel == null) return;
                if (row.Entry.SelectedQuantity > row.Available)
                {
                    row.Entry.SetQuantity(row.Available, true);
                    return;
                }
                s.Panel.RefreshCart();
                s.Panel.RefreshOrderButton();
            }
            catch (Exception e) { Core.Log.Warning("Courier: quantity change failed: " + e.Message); }
        }

        internal static void OpenShop(Shop s)
        {
            try
            {
                if (app == null || !Usable) return;
                BuildCatalog(s);
                app.OpenShop(s.Panel);
                // Opening writes the game's standard fee on the page; show ours and redo the totals.
                if (s.Panel.DeliveryFeeLabel != null)
                    s.Panel.DeliveryFeeLabel.text = Il2CppScheduleOne.Money.MoneyManager.FormatAmount(Config.DeliveryFee, false, false);
                s.Panel.RefreshCart();
                s.Panel.RefreshOrderButton();
            }
            catch (Exception e) { Core.Log.Warning("Courier: could not open pick-up point: " + e); }
        }

        internal static bool CartHasItems(Shop s)
        {
            for (int i = 0; i < s.Rows.Count; i++)
            {
                var e = s.Rows[i].Entry;
                if (e != null && e.SelectedQuantity > 0) return true;
            }
            return false;
        }

        // Courier-specific reasons the order button must stay disabled (null when there are none).
        internal static string BlockReason(Shop s)
        {
            if (!Game.IsHost) return "Only the host can book the courier";
            if (!State.Unlocked) return "Courier service is not set up";
            if (s.Rows.Count == 0) return Config.RequireRack ? $"The \"{Config.RackName}\" rack here is empty" : "Nothing in storage here";
            if (AnyInUse(s.Storages)) return "Someone is using the rack";
            if (PickVanCached() == null) return "All delivery vans are out";
            return null;
        }

        // ---------------------------------------------------------------- ordering

        private static void PlaceOrder(Shop s)
        {
            try
            {
                if (!Usable || s.Panel == null) return;
                var panel = s.Panel;

                var storages = SourceStorages(s.Source);
                s.Storages = storages;
                if (AnyInUse(storages))
                {
                    Core.Log.Msg("Courier: the rack is being used by a player, order not placed.");
                    panel.RefreshOrderButton();
                    return;
                }

                // The racks may have changed since the catalog was built. Never ship more than is there,
                // and if the numbers had to be cut back, let the player look at them before ordering.
                var fresh = ScanRows(storages);
                bool reduced = false;
                foreach (Row row in s.Rows)
                {
                    int have = 0;
                    foreach (Row f in fresh) { if (f.Key == row.Key) { have = f.Available; break; } }
                    if (have != row.Available)
                    {
                        row.Available = have;
                        if (row.Entry != null && row.Entry.ItemPriceLabel != null) row.Entry.ItemPriceLabel.text = ReadyText(row);
                    }
                    if (row.Entry != null && row.Entry.SelectedQuantity > have)
                    {
                        row.Entry.SetQuantity(have, true);
                        reduced = true;
                    }
                }
                if (reduced)
                {
                    Core.Log.Msg("Courier: the rack holds less than was selected, quantities were adjusted. Order not placed yet.");
                    panel.RefreshCart();
                    panel.RefreshOrderButton();
                    return;
                }

                cachedVanUntil = 0f;
                ShopInterface van = PickVan();
                if (van == null) { panel.RefreshOrderButton(); return; }
                AssignVan(s, van);

                var run = new CourierRun { SourceCode = s.Code, SourceName = s.Title, VanShop = van.ShopName };
                var taken = new List<KeyValuePair<Row, ManifestLine>>();
                var entries = panel.listingEntries;
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    if (e == null) continue;
                    int q = e.SelectedQuantity;
                    if (q <= 0) continue;
                    Row row = null;
                    foreach (Row r in s.Rows) { if (r.Entry != null && r.Entry.Pointer == e.Pointer) { row = r; break; } }
                    if (row == null) continue;
                    var line = new ManifestLine { ItemId = row.ItemId, ItemJson = row.Key, Name = row.Name, Quantity = q };
                    run.Lines.Add(line);
                    taken.Add(new KeyValuePair<Row, ManifestLine>(row, line));
                }
                if (run.Lines.Count == 0) { panel.RefreshOrderButton(); return; }

                // The game's own order flow does the rest: validation, fee, dispatch, UI.
                Exception submitError = null;
                pendingRun = run;
                capturedId = null;
                Submitting = s;
                try { panel.SubmitOrder(""); }
                catch (Exception e) { submitError = e; }
                finally { Submitting = null; pendingRun = null; }

                if (capturedId == null)
                {
                    if (submitError != null) Core.Log.Warning("Courier: the order could not be submitted: " + submitError);
                    else Core.Log.Msg("Courier: the order was not accepted (see the note under the order button).");
                    panel.RefreshOrderButton();
                    return;
                }

                // The delivery exists, so the cargo leaves the rack now, whatever else happened. The
                // manifest is corrected to what really left, so the van can never carry more than that.
                foreach (var kv in taken)
                {
                    int want = kv.Value.Quantity;
                    int got = RemoveFromSource(storages, kv.Key, want);
                    if (got != want)
                    {
                        Core.Log.Warning($"Courier: expected to pick up {want}x {kv.Key.Name} but only {got} left the rack.");
                        kv.Value.Quantity = got;
                    }
                }
                run.Lines.RemoveAll(l => l.Quantity <= 0);
                if (submitError != null) Core.Log.Warning("Courier: the order went out but the game reported an error afterwards: " + submitError.Message);
                Core.Log.Msg($"Courier: run {capturedId} booked from '{s.Title}' with {run.Lines.Count} line(s), using the {van.ShopName} van.");
                cachedVanUntil = 0f;
            }
            catch (Exception e) { Core.Log.Warning("Courier: order failed: " + e); }
        }

        // Called from the SendDelivery patch while our order is being submitted: this is the first
        // moment the delivery (and its id) exists, and it is before the phone UI is told about it.
        internal static void OnDeliveryCreated(DeliveryInstance delivery)
        {
            if (pendingRun == null || delivery == null) return;
            capturedId = delivery.DeliveryID;
            pendingRun.VanShop = delivery.StoreName;
            State.Data.Runs[capturedId] = pendingRun;
        }

        internal static bool TryGetRun(string deliveryId, out CourierRun run)
        {
            run = null;
            if (string.IsNullOrEmpty(deliveryId)) return false;
            return State.Data.Runs.TryGetValue(deliveryId, out run);
        }

        private static ItemInstance Rebuild(ManifestLine line)
        {
            ItemInstance item = null;
            try { item = ItemDeserializer.LoadItem(line.ItemJson); }
            catch (Exception e) { Core.Log.Warning("Courier: could not rebuild '" + line.Name + "': " + e.Message); }
            if (item != null) return item;
            // Last resort so the cargo is not lost: the plain item with that id.
            try
            {
                var def = Il2CppScheduleOne.Registry.GetItem(line.ItemId);
                if (def != null) item = def.GetDefaultInstance(1);
            }
            catch { }
            if (item != null) Core.Log.Warning("Courier: '" + line.Name + "' is delivered as a plain '" + line.ItemId + "'.");
            else Core.Log.Error("Courier: '" + line.Name + "' could not be delivered at all.");
            return item;
        }

        // Put up to `quantity` of `item` into a storage unit: matching stacks are topped up first, then
        // empty slots are filled, one stack-size at a time. Returns how many were really placed.
        // The game's own StorageEntity.InsertItem is deliberately not used: in 0.4.7, when the storage
        // already holds a partial stack of the same item it takes that stack away instead of adding to it.
        internal static int Store(StorageEntity storage, ItemInstance item, int quantity)
        {
            int remaining = quantity;
            var slots = storage != null ? storage.ItemSlots : null;
            if (slots == null || item == null || quantity <= 0) return 0;
            string key = null;
            for (int pass = 0; pass < 2 && remaining > 0; pass++)
            {
                for (int j = 0; j < slots.Count && remaining > 0; j++)
                {
                    var slot = slots[j];
                    if (slot == null) continue;
                    var held = slot.ItemInstance;
                    int have = held != null ? slot.Quantity : 0;
                    bool empty = held == null || have <= 0;
                    if (empty != (pass == 1)) continue;
                    if (!empty)
                    {
                        // only top up a stack of the very same item (same packaging, quality, state)
                        if (held.ID != item.ID) continue;
                        if (key == null) key = KeyOf(item);
                        if (KeyOf(held) != key) continue;
                    }
                    int room;
                    try { room = slot.GetCapacityForItem(item, true); } catch { room = 0; }
                    if (room <= 0) continue;
                    int n = Math.Min(room, remaining);
                    if (empty) slot.SetStoredItem(item.GetCopy(n), false);
                    else slot.ChangeQuantity(n, false);
                    int now;
                    try { now = slot.ItemInstance != null ? slot.Quantity : 0; } catch { now = have; }
                    remaining -= Math.Max(0, Math.Min(n, now - have));
                }
            }
            return quantity - remaining;
        }

        // Replaces the game's "create default items by id" van loading for courier runs: the van gets
        // the exact stacks that came off the rack, split across slots by stack size.
        // Returns false, before touching anything, when there is no van to load (the game's default
        // loading then runs instead).
        internal static bool LoadVan(DeliveryInstance delivery, CourierRun run)
        {
            var van = VanFor(delivery.StoreName);
            var storage = van != null && van.Vehicle != null ? van.Vehicle.Storage : null;
            if (storage == null)
            {
                Core.Log.Warning("Courier: no van storage for '" + delivery.StoreName + "', falling back to default loading.");
                return false;
            }
            run.Loaded = true;
            int loaded = 0;
            foreach (ManifestLine line in run.Lines)
            {
                ItemInstance item = Rebuild(line);
                if (item == null) continue;
                int placed = 0;
                try { placed = Store(storage, item, line.Quantity); }
                catch (Exception e) { Core.Log.Warning($"Courier: loading {line.Name} failed: {e.Message}"); }
                loaded += placed;
                int left = line.Quantity - placed;
                if (left > 0)
                {
                    Core.Log.Warning($"Courier: {left}x {line.Name} did not fit in the van and goes back to the rack.");
                    try { ReturnToSource(run, item, left); }
                    catch (Exception e) { Core.Log.Error($"Courier: {left}x {line.Name} could not be returned: {e.Message}"); }
                }
            }
            Core.Log.Msg($"Courier: van loaded with {loaded} item(s) for run {delivery.DeliveryID}.");
            return true;
        }

        private static void ReturnToSource(CourierRun run, ItemInstance item, int quantity)
        {
            var p = FindProperty(run.SourceCode);
            if (p != null)
            {
                foreach (StorageEntity se in SourceStorages(p))
                {
                    if (quantity <= 0) break;
                    quantity -= Store(se, item, quantity);
                }
            }
            if (quantity > 0) Core.Log.Error($"Courier: {quantity}x {item.ID} could not be returned to the rack (no room).");
        }

        // ---------------------------------------------------------------- active order display

        // Show a courier run under its own name and with the real (packaged) items.
        internal static void ApplyRunLabel(DeliveryStatusDisplay display, CourierRun run, bool items)
        {
            if (display == null) return;
            string label = RunLabel(run);
            if (display.ShopLabel != null)
            {
                display.ShopLabel.text = label;
                display.ShopLabel.color = Color.Lerp(Config.Accent, Color.white, 0.35f);
            }
            if (display._shopDescriptionLabel != null) display._shopDescriptionLabel.text = "Your own stock, moved between properties";
            if (!items || display.ItemEntryContainer == null) return;

            // The game made one entry per line (the last one becomes a "+N more" label when there are
            // too many). They are the newest children, after any template entry the prefab carries.
            var entries = display.ItemEntryContainer.GetComponentsInChildren<ItemEntryUI>(true);
            var delivery = display.DeliveryInstance;
            int lines = delivery != null && delivery.Items != null ? delivery.Items.Length : run.Lines.Count;
            if (lines != run.Lines.Count) return; // manifest was corrected after the order, leave the game's entries
            int max = display._maxItemsShown;
            bool overflow = max > 0 && lines > max;
            int made = overflow ? max : lines;
            int show = overflow ? max - 1 : lines;
            int offset = entries.Length - made;
            if (offset < 0) return;
            for (int i = 0; i < show; i++)
            {
                ManifestLine line = run.Lines[i];
                Sprite icon = null;
                try { var inst = ItemDeserializer.LoadItem(line.ItemJson); if (inst != null) icon = inst.Icon; } catch { }
                if (icon != null) entries[offset + i].Set(line.Name, line.Quantity, icon);
            }
        }

        private static void RelabelDisplays()
        {
            if (app == null || State.Data.Runs.Count == 0) return;
            var displays = app.statusDisplays;
            if (displays == null) return;
            for (int i = 0; i < displays.Count; i++)
            {
                var d = displays[i];
                if (d == null || d.DeliveryInstance == null) continue;
                if (!TryGetRun(d.DeliveryInstance.DeliveryID, out CourierRun run)) continue;
                if (d.ShopLabel != null && d.ShopLabel.text != RunLabel(run)) ApplyRunLabel(d, run, true);
            }
        }

        // Forget runs whose delivery no longer exists (finished, or the save was rolled back).
        private static void PruneRuns()
        {
            if (State.Data.Runs.Count == 0) return;
            var lm = Game.Load;
            var dm = Game.Deliveries;
            if (lm == null || dm == null || !lm.IsGameLoaded || lm.TimeSinceGameLoaded < 10f) return;
            var live = new HashSet<string>();
            var ds = dm.Deliveries;
            for (int i = 0; i < ds.Count; i++) { if (ds[i] != null) live.Add(ds[i].DeliveryID); }
            var dead = new List<string>();
            foreach (string id in State.Data.Runs.Keys) { if (!live.Contains(id)) dead.Add(id); }
            foreach (string id in dead) State.Data.Runs.Remove(id);
        }

        // ---------------------------------------------------------------- lifecycle

        internal static void Tick()
        {
            var current = Game.App;
            if (current == null) { Forget(); return; }
            if (app == null || app.Pointer != current.Pointer) { Forget(); app = current; }

            bool open = false;
            try { open = app.isOpen; } catch { }
            // The cards only matter while the app is on screen (opening it syncs at once, see
            // OnAppOpened). Otherwise only react to the service becoming unusable.
            if ((open && Time.unscaledTime > nextSync) || (shops.Count > 0 && !Usable))
            {
                nextSync = Time.unscaledTime + 1f;
                SyncShops();
            }
            // Keep the order button and its note current on an open courier page: whether a van is
            // free or someone is at the rack can change while the page is just sitting there.
            if (open)
            {
                foreach (Shop s in shops.Values)
                {
                    try { if (s.Panel != null && s.Panel.IsOpen) s.Panel.RefreshOrderButton(); }
                    catch { }
                }
            }
            RelabelDisplays();
            if (Time.unscaledTime > nextPrune) { nextPrune = Time.unscaledTime + 5f; PruneRuns(); }
        }

        // The Deliveries app was just opened: make sure the cards are current before it is seen.
        internal static void OnAppOpened(DeliveryApp opened)
        {
            if (!Game.InGame) return;
            State.Sync();
            if (app == null || app.Pointer != opened.Pointer) { Forget(); app = opened; }
            nextSync = Time.unscaledTime + 1f;
            SyncShops();
        }

        // Drop references without touching the objects (they died with the scene or the player).
        internal static void Forget()
        {
            shops.Clear();
            byPanel.Clear();
            hintCard = null;
            app = null;
            cachedVan = null;
            cachedVanUntil = 0f;
        }

        // Remove everything we added to the phone (mod unload / hot reload).
        internal static void Teardown()
        {
            foreach (Shop s in new List<Shop>(shops.Values)) DestroyShop(s);
            if (hintCard != null) { DestroyCard(hintCard); hintCard = null; }
            Forget();
        }
    }
}
