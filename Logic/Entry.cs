using System;
using System.Collections.Specialized;
using MelonLoader;

namespace SupplyRunLogic
{
    // Entry points the development host calls. All calls arrive on the Unity main thread.
    public static class Entry
    {
        internal static string Scene => Core.Scene;

        // TOOLS_ONLY builds carry just the inspection and test commands. They are used next to the
        // release SupplyRun.dll, which then owns the gameplay feature.
        public static void Load(MelonLogger.Instance log, string scene)
        {
#if TOOLS_ONLY
            Core.Log = log;
            Core.Scene = scene ?? "";
            Config.Init();
            log.Msg("SupplyRun dev tools loaded (tools only, gameplay comes from the release mod).");
#else
            Core.Init(log, scene);
#endif
        }

        public static void Unload()
        {
#if !TOOLS_ONLY
            // dev only: a hot reload throws the in-memory state away, so keep it across reloads
            try { State.Commit(); } catch { }
            Core.Shutdown();
#endif
        }

        public static void Update()
        {
#if !TOOLS_ONLY
            Core.Tick();
#endif
        }

        public static void OnScene(string scene)
        {
#if TOOLS_ONLY
            Core.Scene = scene ?? "";
#else
            Core.OnScene(scene);
#endif
        }

        public static string Handle(NameValueCollection q)
        {
            string cmd = (q["cmd"] ?? "").ToLowerInvariant();
            switch (cmd)
            {
                case "": case "ping": return "logic alive, scene='" + Scene + "'";
                case "xref": return SafeXref.Xref(q);
                case "find": return Inspect.Find(q);
                case "get": return Inspect.Get(q);
                case "dump": return Inspect.Dump(q);
                case "tree": return Inspect.Tree(q);
                case "members": return Inspect.Members(q);
                case "loadsave": return DevTools.LoadSave(q);
                case "status": return DevTools.Status();
            }
            return DevTrash.Handle(cmd, q) ?? DevNav.Handle(cmd, q) ?? DevUi.Handle(cmd, q) ?? DevCmd.Handle(cmd, q) ?? ("unknown cmd '" + cmd + "'");
        }
    }
}
