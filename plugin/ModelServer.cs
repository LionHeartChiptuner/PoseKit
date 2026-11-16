using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;

namespace GT3PoseKit
{
    // Tiny localhost HTTP bridge so the browser editor can pull data straight from the running game.
    // Requests are accepted on a background thread and answered on Unity's main thread (Pump),
    // because loading prefabs and reading meshes must happen there.
    public class ModelServer
    {
        public delegate void Handler(string method, string path, Dictionary<string, string> query, string body, out int status, out string json);

        const long MaxBody = 32L * 1024 * 1024;

        readonly HttpListener _listener = new HttpListener();
        readonly Queue<HttpListenerContext> _pending = new Queue<HttpListenerContext>();
        readonly Handler _handler;
        Thread _thread;
        volatile bool _running;

        public int Port { get; private set; }

        // The editor page, served at "/" so the browser talks to the game from the same origin
        // (no CORS, local-network or file:// restrictions).
        public byte[] PageHtml;

        public ModelServer(int port, Handler handler)
        {
            Port = port;
            _handler = handler;
            _listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
            _listener.Prefixes.Add("http://localhost:" + port + "/");
        }

        public void Start()
        {
            _listener.Start();
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "GT3PoseKit bridge" };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _listener.Stop(); _listener.Close(); } catch { }
        }

        void Loop()
        {
            while (_running)
            {
                try
                {
                    var ctx = _listener.GetContext();
                    lock (_pending) _pending.Enqueue(ctx);
                }
                catch { if (!_running) return; }
            }
        }

        // Call from Update(). Handles at most a few requests per frame to avoid long hitches.
        public void Pump(int max)
        {
            for (int n = 0; n < max; n++)
            {
                HttpListenerContext ctx;
                lock (_pending)
                {
                    if (_pending.Count == 0) return;
                    ctx = _pending.Dequeue();
                }
                Respond(ctx);
            }
        }

        void Respond(HttpListenerContext ctx)
        {
            int status = 200;
            string json;
            try
            {
                var res = ctx.Response;
                res.AddHeader("Access-Control-Allow-Origin", "*");
                res.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                res.AddHeader("Access-Control-Allow-Headers", "Content-Type, X-PoseKit");
                res.AddHeader("Access-Control-Allow-Private-Network", "true");
                res.AddHeader("Cache-Control", "no-store");

                var method = ctx.Request.HttpMethod;
                var reqPath = ctx.Request.Url.AbsolutePath.TrimEnd('/').ToLowerInvariant();
                if (method == "GET" && PageHtml != null && (reqPath == "" || reqPath == "/editor" || reqPath == "/index.html"))
                {
                    res.StatusCode = 200;
                    res.ContentType = "text/html; charset=utf-8";
                    res.ContentLength64 = PageHtml.Length;
                    res.OutputStream.Write(PageHtml, 0, PageHtml.Length);
                    res.OutputStream.Close();
                    return;
                }
                if (method == "OPTIONS") { json = ""; status = 204; }
                else if (method != "GET" && method != "POST") { json = Err("only GET and POST are supported"); status = 405; }
                // POSTs must carry the custom header. Browsers only send it after a CORS preflight, which
                // stops plain HTML forms on other sites from posting here.
                else if (method == "POST" && ctx.Request.Headers["X-PoseKit"] == null) { json = Err("missing X-PoseKit header"); status = 403; }
                else if (method == "POST" && ctx.Request.ContentLength64 > MaxBody) { json = Err("request too large"); status = 413; }
                else
                {
                    var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var qs = ctx.Request.QueryString;
                    foreach (string k in qs.AllKeys) if (k != null) q[k] = qs[k];
                    string body = null;
                    if (method == "POST")
                        using (var reader = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                            body = reader.ReadToEnd();
                    try { _handler(method, ctx.Request.Url.AbsolutePath.TrimEnd('/').ToLowerInvariant(), q, body, out status, out json); }
                    catch (Exception e) { status = 500; json = Err(e.Message); }
                }

                var bytes = Encoding.UTF8.GetBytes(json ?? "");
                res.StatusCode = status;
                res.ContentType = "application/json; charset=utf-8";
                res.ContentLength64 = bytes.Length;
                if (bytes.Length > 0) res.OutputStream.Write(bytes, 0, bytes.Length);
                res.OutputStream.Close();
            }
            catch { try { ctx.Response.Abort(); } catch { } }
        }

        public static string Err(string msg)
        {
            return "{\"error\":\"" + (msg ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ") + "\"}";
        }
    }
}
