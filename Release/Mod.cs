using System;
using MelonLoader;
using SupplyRunLogic;

[assembly: MelonInfo(typeof(SupplyRun.Mod), "Supply Run", "1.0.1", "purplehaxttv")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace SupplyRun
{
    // Supply Run: a courier, bought from the Fixer, that moves your own stock between your properties
    // with the game's delivery van. The feature itself lives in the shared Logic sources.
    public class Mod : MelonMod
    {
        private float nextErrorLog;

        public override void OnInitializeMelon()
        {
            Core.Init(LoggerInstance, "");
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            try { Core.OnScene(sceneName); }
            catch (Exception e) { LoggerInstance.Warning("scene change: " + e.Message); }
        }

        public override void OnUpdate()
        {
            try { Core.Tick(); }
            catch (Exception e)
            {
                // never spam the log from a per-frame callback
                if (UnityEngine.Time.unscaledTime < nextErrorLog) return;
                nextErrorLog = UnityEngine.Time.unscaledTime + 10f;
                LoggerInstance.Warning("update: " + e);
            }
        }

        public override void OnDeinitializeMelon()
        {
            Core.Shutdown();
        }
    }
}
