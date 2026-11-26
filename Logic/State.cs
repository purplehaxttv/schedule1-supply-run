using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.Events;
using Il2CppScheduleOne.Persistence;

namespace SupplyRunLogic
{
    // User-tunable settings (UserData/MelonPreferences.cfg, section [SupplyRun]).
    internal static class Config
    {
        private static MelonPreferences_Category cat;
        private static MelonPreferences_Entry<int> unlockPrice;
        private static MelonPreferences_Entry<int> deliveryFee;
        private static MelonPreferences_Entry<string> rackName;
        private static MelonPreferences_Entry<bool> requireRack;
        private static MelonPreferences_Entry<bool> requireUnlock;
        private static MelonPreferences_Entry<string> serviceName;
        private static MelonPreferences_Entry<string> shopNames;
        private static MelonPreferences_Entry<string> accentColor;

        private static string parsedNamesSource;
        private static Dictionary<string, string> parsedNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal static int UnlockPrice => unlockPrice != null ? Math.Max(0, unlockPrice.Value) : 5000;
        internal static int DeliveryFee => deliveryFee != null ? Math.Max(0, deliveryFee.Value) : 200;
        internal static string RackName => rackName != null && !string.IsNullOrWhiteSpace(rackName.Value) ? rackName.Value.Trim() : "Courier";
        internal static bool RequireRack => requireRack == null || requireRack.Value;
        internal static bool RequireUnlock => requireUnlock == null || requireUnlock.Value;
        internal static string ServiceName => serviceName != null && !string.IsNullOrWhiteSpace(serviceName.Value) ? serviceName.Value.Trim() : "Courier";

        internal static Color Accent
        {
            get
            {
                if (accentColor != null && ColorUtility.TryParseHtmlString(accentColor.Value, out Color c)) return c;
                return new Color(0.50f, 0.32f, 0.78f, 1f);
            }
        }

        internal static void Init()
        {
            cat = MelonPreferences.GetCategory("SupplyRun") ?? MelonPreferences.CreateCategory("SupplyRun");
            unlockPrice = Entry("UnlockPrice", 5000, "One-time cash price the Fixer charges to set up the courier service.");
            deliveryFee = Entry("DeliveryFee", 200, "Flat fee per courier run, paid from the bank balance. The cargo itself is free, you already own it.");
            rackName = Entry("CourierRackName", "Courier", "Storage whose name contains this text is a courier pick-up point. Rename a rack with the management clipboard.");
            requireRack = Entry("RequireCourierRack", true, "If true, only storage named after CourierRackName can be shipped from. If false, every storage unit in the property is offered.");
            requireUnlock = Entry("RequireFixerUnlock", true, "If true, the courier must first be bought from the Fixer. If false, it is always available.");
            serviceName = Entry("ServiceName", "Courier", "What the service is called in the Deliveries app.");
            shopNames = Entry("ShopNames", "", "Custom pick-up point names, as propertycode=Name pairs separated by semicolons. Example: bungalow=Stash House;barn=The Farm");
            accentColor = Entry("AccentColor", "#8052C7", "Colour used for courier entries in the Deliveries app (HTML colour).");
        }

        private static MelonPreferences_Entry<T> Entry<T>(string id, T def, string desc)
        {
            return cat.GetEntry<T>(id) ?? cat.CreateEntry(id, def, description: desc);
        }

        // Custom name for a pick-up point, or null when the player has not set one.
        internal static string CustomShopName(string propertyCode, string propertyName)
        {
            string src = shopNames != null ? (shopNames.Value ?? "") : "";
            if (src != parsedNamesSource)
            {
                parsedNamesSource = src;
                parsedNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string pair in src.Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = pair.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = pair.Substring(0, eq).Trim();
                    string v = pair.Substring(eq + 1).Trim();
                    if (k.Length > 0 && v.Length > 0) parsedNames[k] = v;
                }
            }
            if (propertyCode != null && parsedNames.TryGetValue(propertyCode, out string n)) return n;
            if (propertyName != null && parsedNames.TryGetValue(propertyName, out n)) return n;
            return null;
        }
    }

    // One line of a courier run: an exact item (packaging, quality and all) and how many of it.
    internal class ManifestLine
    {
        public string ItemJson { get; set; }
        public string ItemId { get; set; }
        public string Name { get; set; }
        public int Quantity { get; set; }
    }

    // A courier run that is on the road or waiting to be unloaded.
    internal class CourierRun
    {
        public string SourceCode { get; set; }
        public string SourceName { get; set; }
        public string VanShop { get; set; }

        // True once the van was loaded in this session. Deliberately not saved: after a load a run that
        // has not arrived always has an empty van, so a stored "loaded" could only lose the cargo.
        [JsonIgnore]
        public bool Loaded { get; set; }
        public List<ManifestLine> Lines { get; set; } = new List<ManifestLine>();
    }

    internal class SaveData
    {
        public string Identity { get; set; }
        public bool Unlocked { get; set; }
        // deliveryID -> run
        public Dictionary<string, CourierRun> Runs { get; set; } = new Dictionary<string, CourierRun>();
    }

    // Per-save-game mod state (the unlock, and the manifests of runs on the road).
    // It is written only when the game finishes a save, to two places: a SupplyRun.json inside the
    // save folder, so it travels with the save through backups, export/import and slot copies, and a
    // copy under UserData/SupplyRun as a fallback for when the in-save copy is missing.
    internal static class State
    {
        private const string InSaveFile = "SupplyRun.json";

        internal static SaveData Data = new SaveData();
        private static string loadedPath;
        private static string currentKey;

        private static SaveManager hookedManager;
        private static UnityAction saveListener;

        private static string Dir => Path.Combine(MelonEnvironment.UserDataDirectory, "SupplyRun");

        private static string FileFor(string key)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) key = key.Replace(c, '_');
            return Path.Combine(Dir, key + ".json");
        }

        internal static string CurrentKey => currentKey;

        internal static bool Unlocked => !Config.RequireUnlock || Data.Unlocked;

        private static string KeyOf(string saveFolder)
        {
            string p = saveFolder.Replace('\\', '/').TrimEnd('/');
            string[] parts = p.Split('/');
            if (parts.Length >= 2) return parts[parts.Length - 2] + "_" + parts[parts.Length - 1];
            return parts[parts.Length - 1];
        }

        // Identifies the game in a save slot: organisation name plus creation time to the second.
        // Built from calendar parts so it does not depend on the Windows regional format.
        private static string CurrentIdentity(out string legacy)
        {
            legacy = null;
            try
            {
                var info = Game.Load != null ? Game.Load.ActiveSaveInfo : null;
                if (info == null) return null;
                var d = info.DateCreated;
                try { legacy = info.OrganisationName + "|" + d.ToString(); } catch { }
                return $"{info.OrganisationName}|{d.Year:D4}{d.Month:D2}{d.Day:D2}{d.Hour:D2}{d.Minute:D2}{d.Second:D2}";
            }
            catch { return null; }
        }

        // Forget the loaded state (called when leaving a game) so the next load reads the files again.
        internal static void Reset()
        {
            loadedPath = null;
            currentKey = null;
            Data = new SaveData();
        }

        // Make sure the state matches the save game that is currently loaded.
        internal static void Sync()
        {
            var lm = Game.Load;
            if (lm == null) return;
            string path;
            try { path = lm.LoadedGameFolderPath; } catch { return; }
            if (string.IsNullOrEmpty(path)) return;
            if (path == loadedPath) return;
            loadedPath = path;
            LoadFor(path);
        }

        private static SaveData Read(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                SaveData d = JsonSerializer.Deserialize<SaveData>(File.ReadAllText(file));
                if (d == null) return null;
                if (d.Runs == null) d.Runs = new Dictionary<string, CourierRun>();
                foreach (var run in d.Runs.Values) { if (run.Lines == null) run.Lines = new List<ManifestLine>(); }
                return d;
            }
            catch (Exception e)
            {
                Core.Log.Warning("Courier: could not read " + file + ": " + e.Message);
                return null;
            }
        }

        private static void LoadFor(string saveFolder)
        {
            currentKey = KeyOf(saveFolder);
            Data = new SaveData();
            string source = "none";

            // The copy inside the save folder belongs to exactly this save, so it is taken as is.
            SaveData inSave = Read(Path.Combine(saveFolder, InSaveFile));
            if (inSave != null)
            {
                Data = inSave;
                source = "save folder";
            }
            else
            {
                // Fallback copy, keyed by slot: only trusted if the same game still lives in that slot.
                SaveData user = Read(FileFor(currentKey));
                if (user != null)
                {
                    string id = CurrentIdentity(out string legacy);
                    if (user.Identity != null && id != null && user.Identity != id && user.Identity != legacy)
                        Core.Log.Msg("Courier: a different game now uses save slot '" + currentKey + "', starting with a clean courier state.");
                    else { Data = user; source = "UserData"; }
                }
            }
            Core.Log.Msg($"Courier: state for '{currentKey}' (from {source}): unlocked={Data.Unlocked}, runs in progress={Data.Runs.Count}.");
        }

        // Write the state. Called when the game has finished saving.
        internal static void Commit()
        {
            if (string.IsNullOrEmpty(currentKey) || string.IsNullOrEmpty(loadedPath)) return;
            string json;
            try
            {
                string id = CurrentIdentity(out _);
                if (id != null) Data.Identity = id;
                json = JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true });
            }
            catch (Exception e) { Core.Log.Warning("Courier: could not serialise state: " + e.Message); return; }

            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FileFor(currentKey), json);
            }
            catch (Exception e) { Core.Log.Warning("Courier: could not write state to UserData: " + e.Message); }

            try
            {
                if (Directory.Exists(loadedPath)) File.WriteAllText(Path.Combine(loadedPath, InSaveFile), json);
            }
            catch (Exception e) { Core.Log.Warning("Courier: could not write state to the save folder: " + e.Message); }
        }

        // Listen for "the game finished saving". The save manager lives for the whole process.
        internal static void AttachSaveHook()
        {
            var sm = Game.Save;
            if (sm == null) return;
            if (hookedManager != null && hookedManager.Pointer == sm.Pointer) return;
            if (saveListener == null) saveListener = (UnityAction)OnGameSaved;
            sm.onSaveComplete.AddListener(saveListener);
            hookedManager = sm;
        }

        internal static void DetachSaveHook()
        {
            try { if (hookedManager != null && saveListener != null) hookedManager.onSaveComplete.RemoveListener(saveListener); }
            catch { }
            hookedManager = null;
        }

        private static void OnGameSaved()
        {
            try
            {
                if (!Game.IsHost) return;
                Sync();
                Commit();
            }
            catch (Exception e) { Core.Log.Warning("Courier: save hook failed: " + e.Message); }
        }
    }
}
