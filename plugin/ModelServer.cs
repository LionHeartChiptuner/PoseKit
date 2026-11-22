using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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

        // The editor page, served at "/". It is the only web page allowed to use the bridge:
        //  * no CORS headers are ever sent, so other sites can neither read replies nor pass a preflight;
        //  * every data request needs the X-PoseKit header, which other sites can only send after a preflight;
        //  * a foreign Origin is refused, and so is a foreign Host (DNS rebinding);
        //  * the page has a Content-Security-Policy, so injected markup cannot run script as the editor.
        byte[] _page;
        string _pagePolicy;
        public byte[] PageHtml
        {
            get { return _page; }
            set { _pagePolicy = value != null ? PagePolicy(value) : null; _page = value; }
        }

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
                res.AddHeader("Cache-Control", "no-store");
                res.AddHeader("X-Content-Type-Options", "nosniff");
                res.AddHeader("X-Frame-Options", "DENY");

                var method = ctx.Request.HttpMethod;
                var reqPath = ctx.Request.Url.AbsolutePath.TrimEnd('/').ToLowerInvariant();
                string host = ctx.Request.Headers["Host"], origin = ctx.Request.Headers["Origin"];
                bool hostOk = host == "127.0.0.1:" + Port || host == "localhost:" + Port;
                bool allowed = hostOk && (origin == null || origin == "http://" + host);
                if (allowed && method == "GET" && PageHtml != null && (reqPath == "" || reqPath == "/editor" || reqPath == "/index.html"))
                {
                    res.StatusCode = 200;
                    res.ContentType = "text/html; charset=utf-8";
                    res.AddHeader("Content-Security-Policy", _pagePolicy);
                    res.ContentLength64 = PageHtml.Length;
                    res.OutputStream.Write(PageHtml, 0, PageHtml.Length);
                    res.OutputStream.Close();
                    return;
                }
                if (!allowed) { json = Err("only the editor page served by the game can use this"); status = 403; }
                else if (method != "GET" && method != "POST") { json = Err("only GET and POST are supported"); status = 405; }
                else if (ctx.Request.Headers["X-PoseKit"] == null) { json = Err("missing X-PoseKit header"); status = 403; }
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

        static string PagePolicy(byte[] page)
        {
            // Allow exactly the page's own script blocks (by hash; browsers hash them with LF line endings) and three.js.
            var html = Encoding.UTF8.GetString(page).Replace("\r\n", "\n").Replace("\r", "\n");
            var scripts = new StringBuilder();
            using (var sha = SHA256.Create())
                foreach (Match m in Regex.Matches(html, @"<script[^>]*>(.*?)</script>", RegexOptions.Singleline))
                    scripts.Append("'sha256-" + Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(m.Groups[1].Value))) + "' ");
            return "default-src 'none'; script-src " + scripts + "https://cdn.jsdelivr.net/npm/three@0.160.0/; "
                 + "style-src 'unsafe-inline'; img-src 'self' data: blob:; "
                 + "connect-src 'self' https://api.github.com https://raw.githubusercontent.com; "
                 + "base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
        }

        // Exception messages can contain full paths (and so the Windows user name): never send those.
        static readonly Regex FullPath = new Regex(@"(\b[A-Za-z]:[\\/]|\\\\)[^\r\n]*");

        public static string Err(string msg)
        {
            return "{\"error\":\"" + FullPath.Replace(msg ?? "", "(path hidden, see the BepInEx log)").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ") + "\"}";
        }
    }
}
