using System;
using System.Collections.Specialized;
using Il2CppScheduleOne.Persistence;

namespace SupplyRunLogic
{
    // Development-only helpers for driving the game without a human at the keyboard.
    internal static class DevTools
    {
        // cmd=loadsave&slot=5  (slot is 1-based, as in the SaveGame_N folder name)
        internal static string LoadSave(NameValueCollection q)
        {
            int.TryParse(q["slot"] ?? "0", out int slot);
            var lm = UnityEngine.Object.FindObjectOfType<LoadManager>();
            if (lm == null) return "no LoadManager";
            lm.RefreshSaveInfo();
            var saves = LoadManager.SaveGames;
            if (slot < 1 || slot > saves.Length) return "slot out of range";
            SaveInfo info = saves[slot - 1];
            if (info == null) return "slot " + slot + " is empty";
            lm.StartGame(info, false, false);
            return "StartGame called for '" + info.OrganisationName + "' at " + info.SavePath;
        }

        internal static string Status()
        {
            var lm = UnityEngine.Object.FindObjectOfType<LoadManager>();
            if (lm == null) return "no LoadManager";
            return $"scene={Entry.Scene} loading={lm.IsLoading} loaded={lm.IsGameLoaded} status={lm.LoadStatus} folder={lm.LoadedGameFolderPath}";
        }
    }
}
