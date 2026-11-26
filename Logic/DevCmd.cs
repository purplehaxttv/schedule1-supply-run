using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using Il2CppScheduleOne.Delivery;
using Il2CppScheduleOne.Dialogue;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.ObjectScripts;
using Il2CppScheduleOne.Storage;
using Il2CppScheduleOne.UI.Items;
using Il2CppScheduleOne.UI.Phone.Delivery;
using GProperty = Il2CppScheduleOne.Property.Property;

namespace SupplyRunLogic
{
    // Development-only commands for driving and inspecting the courier over HTTP. Not in the release mod.
    internal static class DevCmd
    {
        private static GProperty Prop(string code)
        {
            var props = GProperty.OwnedProperties;
            for (int i = 0; i < props.Count; i++)
            {
                var p = props[i];
                if (p != null && string.Equals(p.PropertyCode, code, StringComparison.OrdinalIgnoreCase)) return p;
            }
            return null;
        }

        private static List<PlaceableStorageEntity> Racks(GProperty p)
        {
            var list = new List<PlaceableStorageEntity>();
            var builds = p.BuildableItems;
            for (int i = 0; i < builds.Count; i++)
            {
                var r = builds[i] != null ? builds[i].TryCast<PlaceableStorageEntity>() : null;
                if (r != null) list.Add(r);
            }
            return list;
        }

        private static string Slots(StorageEntity se)
        {
            var sb = new StringBuilder();
            var slots = se.ItemSlots;
            for (int j = 0; j < slots.Count; j++)
            {
                var slot = slots[j];
                if (slot == null || slot.ItemInstance == null || slot.Quantity <= 0) continue;
                var inst = slot.ItemInstance;
                sb.AppendLine($"      slot {j}: {slot.Quantity}x '{inst.Name}' id={inst.ID} limit={inst.StackLimit} json={inst.GetItemData().GetJson(false)}");
            }
            return sb.ToString();
        }

        internal static string Handle(string cmd, NameValueCollection q)
        {
            switch (cmd)
            {
                case "c.status": return Status();
                case "c.unlock":
                    State.Data.Unlocked = q["v"] != "0";
                    Courier.SyncShops();
                    return "unlocked=" + State.Data.Unlocked;
                case "c.commit": State.Commit(); return "committed " + State.CurrentKey;
                case "c.sync": Courier.Tick(); Courier.SyncShops(); return Status();
                case "c.racks": return RacksCmd(q);
                case "c.rackname": return RackName(q);
                case "c.open": return With(q, s => { Courier.OpenShop(s); return PanelInfo(s); });
                case "c.close": return With(q, s => { Game.App.CloseShop(s.Panel); return "closed"; });
                case "c.panel": return With(q, PanelInfo);
                case "c.qty": return With(q, s =>
                {
                    int.TryParse(q["row"] ?? "0", out int row);
                    int.TryParse(q["q"] ?? "1", out int n);
                    if (row < 0 || row >= s.Rows.Count) return "row out of range (" + s.Rows.Count + ")";
                    s.Rows[row].Entry.SetQuantity(n, true);
                    return PanelInfo(s);
                });
                case "c.dest": return With(q, s => Dest(s, q));
                case "c.order": return With(q, s =>
                {
                    s.Panel.OrderButton.onClick.Invoke();
                    return "clicked.\n" + Deliveries() + Runs();
                });
                case "c.cfg": return Cfg(q);
                case "c.tpfixer": return TpFixer(q);
                case "c.console":
                    Il2CppScheduleOne.Console.SubmitCommand(q["v"] ?? "");
                    return "submitted '" + q["v"] + "'";
                case "c.putvariants": return PutVariants(q);
                case "c.unlockprobe":
                {
                    var def = Il2CppScheduleOne.Registry.GetItem(q["id"] ?? "m1911").TryCast<StorableItemDefinition>();
                    if (def == null) return "not storable";
                    var sb2 = new StringBuilder();
                    sb2.AppendLine($"original: IsUnlocked={def.IsUnlocked} GetIsUnlocked={def.GetIsUnlocked()} RequiresLevel={def.RequiresLevelToPurchase} rank={def.RequiredRank}");
                    var clone = UnityEngine.Object.Instantiate(def);
                    clone.RequiresLevelToPurchase = false;
                    sb2.AppendLine($"clone: name='{clone.name}' ID={clone.ID} Name={clone.Name} IsUnlocked={clone.IsUnlocked} GetIsUnlocked={clone.GetIsUnlocked()} stack={clone.StackLimit} icon={(clone.Icon != null)}");
                    UnityEngine.Object.Destroy(clone);
                    return sb2.ToString();
                }
                case "c.insert":
                {
                    // StorageEntity.InsertItem of n more of whatever sits in the given slot (merge behaviour check)
                    var pp = Prop(q["prop"]);
                    int.TryParse(q["i"] ?? "0", out int ri);
                    int.TryParse(q["slot"] ?? "0", out int si);
                    int.TryParse(q["n"] ?? "1", out int nn);
                    var st = Racks(pp)[ri].StorageEntity;
                    var src = st.ItemSlots[si].ItemInstance;
                    if (src == null) return "slot empty";
                    var copy = src.GetCopy(nn);
                    int fit = st.HowManyCanFit(copy);
                    st.InsertItem(copy, true);
                    return $"HowManyCanFit={fit}, inserted {nn}" + Environment.NewLine + Slots(st);
                }
                case "c.store":
                {
                    // Courier.Store of n of whatever sits in slot `slot` of rack i, into rack `to` (default: same rack)
                    var pp = Prop(q["prop"]);
                    int.TryParse(q["i"] ?? "0", out int ri);
                    int.TryParse(q["slot"] ?? "0", out int si);
                    int.TryParse(q["n"] ?? "1", out int nn);
                    int.TryParse(q["to"] ?? q["i"] ?? "0", out int ti);
                    var racks2 = Racks(pp);
                    var src = racks2[ri].StorageEntity.ItemSlots[si].ItemInstance;
                    if (src == null) return "slot empty";
                    int placed = Courier.Store(racks2[ti].StorageEntity, src.GetCopy(1), nn);
                    return $"placed {placed} of {nn}" + Environment.NewLine + Slots(racks2[ti].StorageEntity);
                }
                case "c.storevan":
                {
                    // exercise Courier.Store on an idle van's storage (nothing else touches it)
                    DeliveryVehicle van = null;
                    foreach (var v in UnityEngine.Object.FindObjectsOfType<DeliveryVehicle>()) if (v.name == (q["van"] ?? "Oscar")) van = v;
                    if (van == null) return "no van";
                    var st = van.Vehicle.Storage;
                    if (q["clear"] == "1") { st.ClearContents(); return "cleared" + Environment.NewLine + Slots(st); }
                    var pp = Prop(q["prop"] ?? "barn");
                    int.TryParse(q["i"] ?? "12", out int ri);
                    int.TryParse(q["slot"] ?? "0", out int si);
                    int.TryParse(q["n"] ?? "1", out int nn);
                    var src = Racks(pp)[ri].StorageEntity.ItemSlots[si].ItemInstance;
                    if (src == null) return "source slot empty";
                    int before = st.ItemCount;
                    int placed = Courier.Store(st, src.GetCopy(1), nn);
                    return $"asked {nn}, placed {placed}, van count {before} -> {st.ItemCount}" + Environment.NewLine + Slots(st);
                }
                case "c.vans": return Vans();
                case "c.unloadvan": return UnloadVan(q);
                case "c.deliveries": return Deliveries();
                case "c.runs": return Runs();
                case "c.arrive": return Arrive(q);
                case "c.displays": return Displays();
                case "c.cards": return Cards();
                case "c.fixer": return Fixer(q);
                case "c.choose": return Choose(q);
                case "shot": return Shot(q);
            }
            return null;
        }

        private static string With(NameValueCollection q, Func<Courier.Shop, string> f)
        {
            var s = Courier.ByCode(q["prop"]);
            if (s == null) return "no courier pick-up point '" + q["prop"] + "'\n" + Status();
            return f(s);
        }

        private static string Status()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"host={Game.IsHost} inGame={Game.InGame} key={State.CurrentKey} unlocked={State.Data.Unlocked} usable={Courier.Usable} fixerChoice={FixerUnlock.Installed} app={(Game.App != null)}");
            foreach (Courier.Shop s in Courier.Shops)
                sb.AppendLine($"  shop '{s.Code}' title='{s.Title}' rows={s.Rows.Count} card='{(s.CardDescription != null ? s.CardDescription.text : "")}' van='{s.Panel.MatchingShopInterfaceName}'");
            sb.Append(Runs());
            return sb.ToString();
        }

        private static string RacksCmd(NameValueCollection q)
        {
            var p = Prop(q["prop"]);
            if (p == null) return "no property";
            var sb = new StringBuilder();
            var racks = Racks(p);
            for (int i = 0; i < racks.Count; i++)
            {
                var r = racks[i];
                sb.AppendLine($"  [{i}] '{r.name}' name='{r.Name}' mgmt='{r.GetManagementName()}' courier={Courier.IsCourierRack(r)} items={r.StorageEntity.ItemCount}");
                if (q["items"] == "1") sb.Append(Slots(r.StorageEntity));
            }
            return sb.ToString();
        }

        private static string RackName(NameValueCollection q)
        {
            var p = Prop(q["prop"]);
            if (p == null) return "no property";
            var racks = Racks(p);
            int.TryParse(q["i"] ?? "0", out int i);
            if (i < 0 || i >= racks.Count) return "index out of range";
            var r = racks[i];
            if (r.Configuration == null || r.Configuration.Name == null) return "rack has no name field";
            r.Configuration.Name.SetValue(q["name"] ?? "Courier", true);
            return $"rack {i} renamed: name='{r.Name}' mgmt='{r.GetManagementName()}' courier={Courier.IsCourierRack(r)}";
        }

        private static string PanelInfo(Courier.Shop s)
        {
            var p = s.Panel;
            var sb = new StringBuilder();
            sb.AppendLine($"panel '{s.Title}' open={p.IsOpen} active={p.gameObject.activeInHierarchy} van='{p.MatchingShopInterfaceName}'");
            sb.AppendLine($"  title='{(s.PanelTitle != null ? s.PanelTitle.text : "?")}' desc='{(s.PanelDescription != null ? s.PanelDescription.text : "?")}'");
            for (int i = 0; i < s.Rows.Count; i++)
            {
                var r = s.Rows[i];
                sb.AppendLine($"  row {i}: '{r.Entry.ItemNameLabel.text}' price='{r.Entry.ItemPriceLabel.text}' avail={r.Available} selected={r.Entry.SelectedQuantity} locked={(r.Entry.LockedContainer != null && r.Entry.LockedContainer.gameObject.activeSelf)} unlocked={r.Definition.IsUnlocked}");
            }
            sb.AppendLine($"  itemTotal='{p.ItemTotalLabel.text}' fee='{p.DeliveryFeeLabel.text}' total='{p.OrderTotalLabel.text}' time='{p.DeliveryTimeLabel.text}'");
            sb.AppendLine($"  dest={(p.destinationProperty != null ? p.destinationProperty.PropertyCode : "none")} dock={p.loadingDockIndex}");
            var dd = p.DestinationDropdown;
            var opts = new List<string>();
            for (int i = 0; i < dd.options.Count; i++) opts.Add(dd.options[i].text);
            sb.AppendLine($"  destOptions=[{string.Join(", ", opts)}] value={dd.value}");
            string reason = "";
            bool can = false;
            try { can = p.CanOrder(out reason); } catch (Exception e) { reason = "CanOrder threw " + e.Message; }
            sb.AppendLine($"  orderButton.interactable={p.OrderButton.interactable} note='{p.OrderButtonNote.text}' noteEnabled={p.OrderButtonNote.enabled} CanOrder={can} reason='{reason}'");
            return sb.ToString();
        }

        private static string Dest(Courier.Shop s, NameValueCollection q)
        {
            var p = s.Panel;
            var dests = p.GetPotentialDestinations();
            int idx = -1;
            for (int i = 0; i < dests.Count; i++)
                if (string.Equals(dests[i].PropertyCode, q["to"], StringComparison.OrdinalIgnoreCase)) idx = i;
            if (idx < 0) return "destination not offered\n" + PanelInfo(s);
            p.DestinationDropdown.value = idx + 1;
            int.TryParse(q["dock"] ?? "1", out int dock);
            p.LoadingDockDropdown.value = dock;
            return PanelInfo(s);
        }

        private static string Vans()
        {
            var sb = new StringBuilder();
            var vans = UnityEngine.Object.FindObjectsOfType<DeliveryVehicle>();
            foreach (var v in vans)
            {
                if (v == null || v.Vehicle == null || v.Vehicle.Storage == null) continue;
                var st = v.Vehicle.Storage;
                sb.AppendLine($"van '{v.name}' items={st.ItemCount} slots={st.SlotCount} visible={v.Vehicle.IsVisible} pos={v.transform.position}");
                sb.Append(Slots(st));
            }
            return sb.ToString();
        }

        // move everything in the delivery vans into rack i of a property (stands in for the player unloading)
        private static string UnloadVan(NameValueCollection q)
        {
            var p = Prop(q["to"]);
            if (p == null) return "no property";
            var racks = Racks(p);
            int.TryParse(q["i"] ?? "0", out int i);
            var target = racks[i].StorageEntity;
            int moved = 0;
            foreach (var v in UnityEngine.Object.FindObjectsOfType<DeliveryVehicle>())
            {
                if (v == null || v.Vehicle == null || v.Vehicle.Storage == null) continue;
                var slots = v.Vehicle.Storage.ItemSlots;
                for (int j = 0; j < slots.Count; j++)
                {
                    var slot = slots[j];
                    if (slot == null || slot.ItemInstance == null || slot.Quantity <= 0) continue;
                    int n = slot.Quantity;
                    target.InsertItem(slot.ItemInstance.GetCopy(n), true);
                    slot.ChangeQuantity(-n, false);
                    moved += n;
                }
            }
            return $"moved {moved} into rack {i} of {p.PropertyCode}" + Environment.NewLine + Slots(target);
        }

        // set a [SupplyRun] preference: c.cfg&k=ShopNames&v=bungalow=Stash House
        private static string Cfg(NameValueCollection q)
        {
            var cat = MelonLoader.MelonPreferences.GetCategory("SupplyRun");
            if (cat == null) return "no category";
            string k = q["k"], v = q["v"] ?? "";
            try { var es = cat.GetEntry<string>(k); if (es != null) { es.Value = v; return k + "=" + es.Value; } } catch (InvalidCastException) { }
            try { var ei = cat.GetEntry<int>(k); if (ei != null) { ei.Value = int.Parse(v); return k + "=" + ei.Value; } } catch (InvalidCastException) { }
            try { var eb = cat.GetEntry<bool>(k); if (eb != null) { eb.Value = bool.Parse(v); return k + "=" + eb.Value; } } catch (InvalidCastException) { }
            return "no entry " + k;
        }

        // put the player in front of the Fixer, looking at them
        private static string TpFixer(NameValueCollection q)
        {
            var fixer = UnityEngine.Object.FindObjectOfType<Il2CppScheduleOne.NPCs.CharacterClasses.Fixer>();
            if (fixer == null) return "no fixer";
            var pm = UnityEngine.Object.FindObjectOfType<Il2CppScheduleOne.PlayerScripts.PlayerMovement>();
            if (pm == null) return "no player movement";
            float.TryParse(q["d"] ?? "1.6", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float d);
            Vector3 fpos = fixer.transform.position;
            Vector3 pos = fpos + fixer.transform.forward * d;
            Quaternion rot = Quaternion.LookRotation(fpos - pos, Vector3.up);
            pm.Teleport(pos, rot, false);
            return $"fixer at {fpos}, player to {pos}";
        }

        // put two guns with different ammo counts on rack i of a property (same item id, different state)
        private static string PutVariants(NameValueCollection q)
        {
            var p = Prop(q["prop"]);
            if (p == null) return "no property";
            var racks = Racks(p);
            int.TryParse(q["i"] ?? "0", out int i);
            var target = racks[i].StorageEntity;
            var def = Il2CppScheduleOne.Registry.GetItem(q["id"] ?? "m1911");
            if (def == null) return "no item";
            int[] values = { 7, 2, 7 };
            foreach (int v in values)
            {
                var inst = def.GetDefaultInstance(1);
                var integer = inst.TryCast<IntegerItemInstance>();
                if (integer != null) integer.SetValue(v);
                target.InsertItem(inst, true);
            }
            return Slots(target);
        }

        private static string Deliveries()
        {
            var dm = Game.Deliveries;
            var sb = new StringBuilder();
            var ds = dm.Deliveries;
            sb.AppendLine("deliveries: " + ds.Count);
            for (int i = 0; i < ds.Count; i++)
            {
                var d = ds[i];
                var items = new List<string>();
                for (int k = 0; k < d.Items.Length; k++) items.Add(d.Items[k].Int + "x" + d.Items[k].String);
                sb.AppendLine($"  {d.DeliveryID} store='{d.StoreName}' dest={d.DestinationCode} dock={d.LoadingDockIndex} status={d.Status} mins={d.TimeUntilArrival} items=[{string.Join(", ", items)}]");
            }
            return sb.ToString();
        }

        private static string Runs()
        {
            var sb = new StringBuilder();
            sb.AppendLine("courier runs: " + State.Data.Runs.Count);
            foreach (var kv in State.Data.Runs)
            {
                var lines = new List<string>();
                foreach (var l in kv.Value.Lines) lines.Add(l.Quantity + "x " + l.Name);
                sb.AppendLine($"  {kv.Key} from={kv.Value.SourceCode} van='{kv.Value.VanShop}' loaded={kv.Value.Loaded} [{string.Join(", ", lines)}]");
            }
            return sb.ToString();
        }

        private static string Arrive(NameValueCollection q)
        {
            var dm = Game.Deliveries;
            var ds = dm.Deliveries;
            int n = 0;
            for (int i = 0; i < ds.Count; i++) { ds[i].TimeUntilArrival = 0; n++; }
            dm.OnTimePass(1);
            return $"forced {n} deliveries to arrive\n" + Deliveries() + Vans();
        }

        private static string Displays()
        {
            var app = Game.App;
            var sb = new StringBuilder();
            var ds = app.statusDisplays;
            sb.AppendLine("status displays: " + ds.Count);
            for (int i = 0; i < ds.Count; i++)
            {
                var d = ds[i];
                sb.AppendLine($"  shop='{d.ShopLabel.text}' desc='{d._shopDescriptionLabel.text}' dest='{d.DestinationLabel.text}' dock='{d._loadingDockLabel.text}' status='{d.StatusLabel.text}'");
                var entries = d.ItemEntryContainer.GetComponentsInChildren<ItemEntryUI>(true);
                foreach (var e in entries)
                    sb.AppendLine($"    item '{e._nameLabel.text}' qty='{e._quantityLabel.text}' icon={(e._icon.sprite != null ? e._icon.sprite.name : "none")}");
            }
            return sb.ToString();
        }

        private static string Cards()
        {
            var app = Game.App;
            var sb = new StringBuilder();
            var parent = app._shopElements[0].Button.transform.parent;
            for (int i = 0; i < parent.childCount; i++)
            {
                var c = parent.GetChild(i);
                var t = c.Find("Title");
                var d = c.Find("Description");
                sb.AppendLine($"  [{i}] '{c.name}' active={c.gameObject.activeSelf} title='{(t != null ? t.GetComponent<Text>().text : "")}' desc='{(d != null ? d.GetComponent<Text>().text : "")}'");
            }
            return sb.ToString();
        }

        private static string Fixer(NameValueCollection q)
        {
            var c = UnityEngine.Object.FindObjectOfType<DialogueController_Fixer>();
            if (c == null) return "no fixer controller";
            var sb = new StringBuilder();
            sb.AppendLine("controller choices:");
            for (int i = 0; i < c.Choices.Count; i++)
            {
                var ch = c.Choices[i];
                bool show = false;
                try { show = ch.ShouldShow(); } catch (Exception e) { sb.AppendLine("   ShouldShow threw " + e.Message); }
                sb.AppendLine($"  [{i}] '{ch.ChoiceText}' enabled={ch.Enabled} prio={ch.Priority} shouldShow={show}");
            }
            if (q["start"] == "1")
            {
                c.StartGenericDialogue(true);
                sb.AppendLine("started generic dialogue");
            }
            var h = c.handler;
            if (h != null && h.CurrentChoices != null)
            {
                sb.AppendLine($"handler inProgress={h.IsDialogueInProgress} current choices:");
                for (int i = 0; i < h.CurrentChoices.Count; i++)
                    sb.AppendLine($"  [{i}] '{h.CurrentChoices[i].ChoiceText}' label='{h.CurrentChoices[i].ChoiceLabel}'");
            }
            return sb.ToString();
        }

        private static string Choose(NameValueCollection q)
        {
            var c = UnityEngine.Object.FindObjectOfType<DialogueController_Fixer>();
            if (c == null) return "no fixer controller";
            int.TryParse(q["i"] ?? "0", out int i);
            c.handler.ChoiceSelected(i);
            return $"chose {i}; unlocked={State.Data.Unlocked} cash={Game.Money.cashBalance}";
        }

        private static string Shot(NameValueCollection q)
        {
            string dir = @"D:\ModProjects\SupplyRun\shots";
            Directory.CreateDirectory(dir);
            // file name only: never let the request choose a folder
            string name = Path.GetFileName(q["name"] ?? "shot");
            if (string.IsNullOrEmpty(name)) name = "shot";
            string path = Path.Combine(dir, name + ".png");
            if (File.Exists(path)) File.Delete(path);
            ScreenCapture.CaptureScreenshot(path);
            return path;
        }
    }
}
