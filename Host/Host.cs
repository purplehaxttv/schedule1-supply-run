using System;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using MelonLoader;

[assembly: MelonInfo(typeof(SupplyRunDev.Host), "Supply Run Dev Host", "1.0.0", "purplehaxttv")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace SupplyRunDev
{
    // Development-only host. It stays loaded for the life of the game process and
    // hot-loads the real mod logic from disk, so logic changes do not need a game restart.
    //   GET /ping            answered on the listener thread (proves the process is alive)
    //   GET /reload          (re)loads the logic assembly on the main thread
    //   GET /x?cmd=...       forwarded to the logic assembly on the main thread, reply is its return value
    // Every request must carry the per-session secret in an X-Dev-Token header. The secret is written
    // to UserData/SupplyRunDev.token at startup for the dev scripts to read. A web page cannot send a
    // custom header to another origin without a CORS preflight, which this listener never answers, so
    // a browser tab cannot drive the game through this port.
    public class Host : MelonMod
    {
        private const string LogicPath = @"D:\ModProjects\SupplyRun\Logic\bin\SupplyRunLogic.dll";
        private const int Port = 7901;

        private static HttpListener listener;
        private static Thread thread;
        private static volatile bool running;
        private static MelonLogger.Instance log;
        private static string token;

        private class Job
        {
            public Func<string> Work;
            public string Result;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        private static readonly ConcurrentQueue<Job> queue = new ConcurrentQueue<Job>();

        private static Action logicUnload;
        private static Action logicUpdate;
        private static Action<string> logicScene;
        private static Func<NameValueCollection, string> logicHandle;
        private static string logicInfo = "none";
        private static string lastScene = "";

        public override void OnInitializeMelon()
        {
            log = LoggerInstance;
            try { UnityEngine.Application.runInBackground = true; } catch { }
            try
            {
                token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                File.WriteAllText(Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "SupplyRunDev.token"), token);
                listener = new HttpListener();
                listener.Prefixes.Add($"http://localhost:{Port}/");
                listener.Start();
                running = true;
                thread = new Thread(Loop) { IsBackground = true, Name = "SupplyRunDev-Listener" };
                thread.Start();
                log.Msg($"Dev host listening on http://localhost:{Port}/ (ping, reload, x)");
            }
            catch (Exception e) { log.Error("Dev host failed to start: " + e); }

            if (File.Exists(LogicPath)) log.Msg(LoadLogic());
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            lastScene = sceneName ?? "";
            try { UnityEngine.Application.runInBackground = true; } catch { }
            try { logicScene?.Invoke(lastScene); }
            catch (Exception e) { log.Warning("logic OnScene threw: " + e); }
        }

        public override void OnUpdate()
        {
            while (queue.TryDequeue(out Job j))
            {
                try { j.Result = j.Work(); }
                catch (Exception e) { j.Result = "EXCEPTION: " + Unwrap(e); }
                j.Done.Set();
            }
            try { logicUpdate?.Invoke(); }
            catch (Exception e) { log.Warning("logic Update threw: " + Unwrap(e)); logicUpdate = null; }
        }

        public override void OnDeinitializeMelon()
        {
            running = false;
            try { logicUnload?.Invoke(); } catch { }
            try { listener?.Stop(); } catch { }
            try { listener?.Close(); } catch { }
        }

        private static string Unwrap(Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e.ToString();
        }

        private static string LoadLogic()
        {
            var sb = new StringBuilder();
            try
            {
                if (logicUnload != null)
                {
                    try { logicUnload(); sb.AppendLine("old logic unloaded"); }
                    catch (Exception e) { sb.AppendLine("old logic Unload threw: " + Unwrap(e)); }
                }
                logicUnload = null; logicUpdate = null; logicScene = null; logicHandle = null;

                byte[] bytes = File.ReadAllBytes(LogicPath);
                Assembly asm = Assembly.Load(bytes);
                Type t = asm.GetType("SupplyRunLogic.Entry", true);
                const BindingFlags F = BindingFlags.Public | BindingFlags.Static;
                MethodInfo load = t.GetMethod("Load", F);
                var unload = (Action)Delegate.CreateDelegate(typeof(Action), t.GetMethod("Unload", F));
                var update = (Action)Delegate.CreateDelegate(typeof(Action), t.GetMethod("Update", F));
                var scene = (Action<string>)Delegate.CreateDelegate(typeof(Action<string>), t.GetMethod("OnScene", F));
                var handle = (Func<NameValueCollection, string>)Delegate.CreateDelegate(typeof(Func<NameValueCollection, string>), t.GetMethod("Handle", F));

                load.Invoke(null, new object[] { log, lastScene });
                logicUnload = unload; logicUpdate = update; logicScene = scene; logicHandle = handle;
                logicInfo = $"{bytes.Length} bytes, built {File.GetLastWriteTime(LogicPath):HH:mm:ss}";
                sb.Append("logic loaded: " + logicInfo);
            }
            catch (Exception e)
            {
                sb.Append("LOAD FAILED: " + Unwrap(e));
            }
            return sb.ToString();
        }

        // Only the local dev scripts may talk to this port: right host name, no browser origin,
        // and the session secret in a custom header.
        private static bool Authorized(HttpListenerRequest r)
        {
            if (r.Headers["Origin"] != null) return false;
            string host = r.Headers["Host"] ?? "";
            if (host != "localhost:" + Port && host != "127.0.0.1:" + Port) return false;
            string sent = r.Headers["X-Dev-Token"];
            if (string.IsNullOrEmpty(sent) || token == null || sent.Length != token.Length) return false;
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(sent), Encoding.ASCII.GetBytes(token));
        }

        private static void Loop()
        {
            while (running)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch { break; }

                string reply;
                int status = 200;
                try
                {
                    string path = ctx.Request.Url.AbsolutePath.Trim('/').ToLowerInvariant();
                    NameValueCollection q = ctx.Request.QueryString;
                    if (!Authorized(ctx.Request))
                    {
                        reply = "forbidden";
                        status = 403;
                    }
                    else if (path == "ping")
                    {
                        reply = $"pong scene='{lastScene}' logic={logicInfo} queued={queue.Count}";
                    }
                    else
                    {
                        var job = new Job();
                        if (path == "reload") job.Work = LoadLogic;
                        else job.Work = () => logicHandle != null ? logicHandle(q) : "no logic loaded (GET /reload)";
                        queue.Enqueue(job);
                        int timeoutMs = 30000;
                        if (int.TryParse(q["timeout"], out int t) && t > 0) timeoutMs = t;
                        if (job.Done.Wait(timeoutMs)) reply = job.Result ?? "(null)";
                        else { reply = "TIMEOUT: main thread did not run the job (game paused, loading, or frozen)"; status = 504; }
                    }
                }
                catch (Exception e) { reply = "HOST ERROR: " + e; status = 500; }

                try
                {
                    byte[] buf = Encoding.UTF8.GetBytes(reply);
                    ctx.Response.StatusCode = status;
                    ctx.Response.ContentType = "text/plain; charset=utf-8";
                    ctx.Response.OutputStream.Write(buf, 0, buf.Length);
                    ctx.Response.OutputStream.Close();
                }
                catch { }
            }
        }
    }
}
