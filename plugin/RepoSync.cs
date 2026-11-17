using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Configuration;
using FistVR;
using UnityEngine;
using UnityEngine.UI;

namespace GT3PoseKit
{
    // Startup check of the GitHub pose repository (download only: nothing is ever uploaded). When it has pose
    // files you don't have yet, or newer versions of ones you synced, an in-game window asks whether to sync.
    // Poses you edited yourself are only replaced when you choose "Sync + replace mine", and are backed up first.
    public partial class PoseKitPlugin
    {
        ConfigEntry<bool> _repoCheck;
        ConfigEntry<string> _repoName;

        [Serializable] public class RepoEntry { public string name; public string type; public string sha; public string download_url; }
        [Serializable] class RepoListing { public RepoEntry[] items; }

        // What was last synced (or skipped) per repo file, so only real changes ask again.
        [Serializable] public class RepoFileState { public string name; public string sha; public string localFile; public string localHash; }
        [Serializable] public class RepoState { public string repo; public List<RepoFileState> files = new List<RepoFileState>(); }

        class RepoUpdate
        {
            public RepoEntry entry;
            public string text;        // downloaded file
            public PoseSet set;
            public string key;
            public List<string> overlapping = new List<string>(); // local files for the same gun(s)
            public bool conflict;      // would replace poses you made or edited yourself
        }

        readonly List<RepoUpdate> _repoUpdates = new List<RepoUpdate>();
        RepoState _repoState;
        object _repoPanelApi;
        GameObject _repoPanel;
        Text _repoText;
        readonly Dictionary<string, GameObject> _repoButtons = new Dictionary<string, GameObject>();

        string RepoStatePath { get { return Path.Combine(_dir, "repo-sync-state.dat"); } }

        void BindRepoConfig()
        {
            _repoCheck = Config.Bind("Pose repo", "CheckOnStart", true, "When the game starts, check the GitHub pose repository for new or updated poses and ask in game whether to sync them. Download only.");
            _repoName = Config.Bind("Pose repo", "Repo", "LionHeartChiptuner/PoseKit-Poses", "GitHub repository (owner/name) whose main folder holds the pose files.");
        }

        void Start()
        {
            if (_repoCheck != null && _repoCheck.Value) StartCoroutine(CheckRepo());
        }

        // ------------------------------------------------------------------ check

        IEnumerator CheckRepo()
        {
            string repo = (_repoName.Value ?? "").Trim().Trim('/');
            if (repo.IndexOf('/') <= 0) { Logger.LogWarning("Pose repo: '" + repo + "' is not owner/name, skipping the update check"); yield break; }

            var listingReq = new Fetched();
            yield return StartCoroutine(Fetch("https://api.github.com/repos/" + repo + "/contents/", listingReq));
            if (listingReq.text == null) { Logger.LogWarning("Pose repo check failed: " + listingReq.error); yield break; }

            RepoListing listing;
            try { listing = JsonUtility.FromJson<RepoListing>("{\"items\":" + listingReq.text + "}"); }
            catch (Exception e) { Logger.LogWarning("Pose repo check: unexpected reply (" + e.Message + ")"); yield break; }
            if (listing == null || listing.items == null) yield break;

            _repoState = LoadRepoState(repo);
            var locals = LocalPoseFiles();
            _repoUpdates.Clear();

            foreach (var entry in listing.items)
            {
                if (entry == null || entry.type != "file" || string.IsNullOrEmpty(entry.download_url)) continue;
                if (!entry.name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                var known = _repoState.files.Find(f => f.name == entry.name);
                if (known != null && known.sha == entry.sha) continue; // nothing new since the last sync or skip

                var fileReq = new Fetched();
                yield return StartCoroutine(Fetch(entry.download_url, fileReq));
                if (fileReq.text == null) { Logger.LogWarning("Pose repo: could not download " + entry.name + ": " + fileReq.error); continue; }

                PoseSet set = null;
                try { set = JsonUtility.FromJson<PoseSet>(fileReq.text); } catch { }
                string key = set != null && set.format == "gt3poses" && set.match != null ? SetKey(set) : "";
                if (key == "") { MarkRepoFile(entry, null, null); continue; } // not a pose set (e.g. rig.json): never ask about it

                var up = new RepoUpdate { entry = entry, text = fileReq.text, set = set, key = key };
                string normalised = JsonUtility.ToJson(set);
                bool identical = false;
                foreach (var l in locals)
                {
                    if (l.Value == null) continue;
                    if (SetKey(l.Value) != key && !SharesKeys(l.Value, set)) continue;
                    up.overlapping.Add(l.Key);
                    if (JsonUtility.ToJson(l.Value) == normalised) identical = true;
                }
                if (identical && up.overlapping.Count == 1)
                {
                    // You already have exactly this (e.g. synced from the browser editor).
                    var lf = Path.Combine(_dir, up.overlapping[0]);
                    MarkRepoFile(entry, up.overlapping[0], Hash(File.ReadAllText(lf)));
                    continue;
                }
                foreach (var name in up.overlapping)
                {
                    // Unchanged since PoseKit wrote it from the repo = safe to update; anything else is yours.
                    var tracked = _repoState.files.Find(f => f.localFile == name);
                    string current;
                    try { current = Hash(File.ReadAllText(Path.Combine(_dir, name))); } catch { current = ""; }
                    if (tracked == null || tracked.localHash != current) up.conflict = true;
                }
                _repoUpdates.Add(up);
            }
            SaveRepoState();

            if (_repoUpdates.Count == 0) { Logger.LogInfo("Pose repo " + repo + ": you're up to date"); yield break; }
            Logger.LogMessage("Pose repo " + repo + ": " + _repoUpdates.Count + " pose set(s) to sync");

            // Wait until there's a player to show the window to.
            float giveUp = Time.realtimeSinceStartup + 600f;
            while (GM.CurrentPlayerBody == null || GM.CurrentPlayerBody.Head == null)
            {
                if (Time.realtimeSinceStartup > giveUp) yield break;
                yield return new WaitForSeconds(1f);
            }
            yield return new WaitForSeconds(3f); // let the scene settle
            try { ShowRepoPanel(); }
            catch (Exception e) { Logger.LogWarning("Could not show the pose update window (Sodalite missing?): " + e.Message); }
        }

        class Fetched { public string text, error; public bool done; }

        // Unity's WWW first; if that can't connect (old Mono/Unity TLS), Windows' own curl.exe, which does TLS 1.2+.
        IEnumerator Fetch(string url, Fetched result)
        {
            if (!_useCurl)
            {
                WWW www = null;
                try
                {
                    var headers = new Dictionary<string, string> { { "Accept", "application/vnd.github+json" }, { "User-Agent", "GT3PoseKit" } };
                    try { www = new WWW(url, null, headers); }
                    catch { www = new WWW(url); }
                }
                catch (Exception e) { result.error = e.Message; }
                if (www != null)
                {
                    yield return www;
                    if (string.IsNullOrEmpty(www.error) && !string.IsNullOrEmpty(www.text)) { result.text = www.text; result.done = true; yield break; }
                    result.error = www.error;
                    // A real HTTP answer (e.g. 403 rate limit, 404) won't get better through curl.
                    if (!string.IsNullOrEmpty(www.error) && System.Text.RegularExpressions.Regex.IsMatch(www.error, @"^\s*[45]\d\d")) { result.done = true; yield break; }
                }
                _useCurl = true;
                Logger.LogInfo("Pose repo: Unity couldn't connect (" + result.error + "), using Windows curl.exe instead");
            }

            string text = null, error = null; bool finished = false;
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("curl.exe",
                        "-sSfL --max-time 20 -A GT3PoseKit -H \"Accept: application/vnd.github+json\" \"" + url.Replace("\"", "") + "\"")
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                    };
                    using (var p = System.Diagnostics.Process.Start(psi))
                    {
                        string err = null;
                        var errThread = new System.Threading.Thread(() => { err = p.StandardError.ReadToEnd(); });
                        errThread.Start();
                        text = p.StandardOutput.ReadToEnd();
                        p.WaitForExit();
                        errThread.Join();
                        if (p.ExitCode != 0) { error = "curl: " + (err ?? "").Trim(); text = null; }
                    }
                }
                catch (Exception e) { error = "curl.exe not available: " + e.Message; }
                finished = true;
            }) { IsBackground = true };
            thread.Start();
            while (!finished) yield return null;
            result.text = text;
            result.error = text != null ? null : error;
            result.done = true;
        }

        bool _useCurl;

        Dictionary<string, PoseSet> LocalPoseFiles()
        {
            var d = new Dictionary<string, PoseSet>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(_dir, "*.json"))
            {
                var fn = Path.GetFileName(file);
                if (fn.Equals("rig.json", StringComparison.OrdinalIgnoreCase) || fn.Equals("glove.json", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var set = JsonUtility.FromJson<PoseSet>(File.ReadAllText(file));
                    if (set != null && set.format == "gt3poses" && set.match != null) d[fn] = set;
                }
                catch { }
            }
            return d;
        }

        // ------------------------------------------------------------------ sync

        void SyncRepo(bool replaceMine)
        {
            int added = 0, updated = 0, kept = 0, failed = 0;
            string backup = null;
            foreach (var up in _repoUpdates)
            {
                if (up.conflict && !replaceMine) { kept++; continue; }
                try
                {
                    string target = SafeFileName(up.key) + ".json";
                    // Set your own versions aside first (never deleted), so the repo set is the only one for that gun.
                    foreach (var name in up.overlapping)
                    {
                        if (up.conflict)
                        {
                            if (backup == null) { backup = Path.Combine(_dir, Path.Combine("backup", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"))); Directory.CreateDirectory(backup); }
                            File.Copy(Path.Combine(_dir, name), Path.Combine(backup, name), true);
                        }
                        if (!name.Equals(target, StringComparison.OrdinalIgnoreCase)) File.Delete(Path.Combine(_dir, name));
                    }
                    if (up.overlapping.Count > 0) UnregisterSet(up.overlapping);
                    ApplyLive(up.set, true);
                    MarkRepoFile(up.entry, target, Hash(File.ReadAllText(Path.Combine(_dir, target))));
                    if (up.overlapping.Count > 0) updated++; else added++;
                }
                catch (Exception e) { failed++; Logger.LogError("Pose repo: " + up.entry.name + ": " + e.Message); }
            }
            SaveRepoState();
            _repoUpdates.RemoveAll(u => !(u.conflict && !replaceMine));

            var sb = new StringBuilder("Synced: " + added + " new, " + updated + " updated.");
            if (kept > 0) sb.Append("\nKept your own poses for " + kept + " gun(s). Choose Sync + replace mine to take the repo's.");
            if (failed > 0) sb.Append("\n" + failed + " failed (see the BepInEx log).");
            if (backup != null) sb.Append("\nYour replaced poses were backed up to\n" + backup);
            Logger.LogMessage(sb.ToString());
            SetRepoPanel(sb.ToString(), kept > 0);
        }

        void UnregisterSet(List<string> files)
        {
            // Reload is simplest and keeps every index consistent after files moved.
            LoadPoseSets();
            _checked.Clear();
        }

        void SkipRepoUpdate()
        {
            // Remember these versions so they don't ask again; a later change in the repo will.
            foreach (var up in _repoUpdates) MarkRepoFile(up.entry, null, null);
            SaveRepoState();
            _repoUpdates.Clear();
            CloseRepoPanel();
        }

        void MarkRepoFile(RepoEntry entry, string localFile, string localHash)
        {
            var f = _repoState.files.Find(x => x.name == entry.name);
            if (f == null) { f = new RepoFileState { name = entry.name }; _repoState.files.Add(f); }
            f.sha = entry.sha;
            if (localFile != null) { f.localFile = localFile; f.localHash = localHash; }
        }

        RepoState LoadRepoState(string repo)
        {
            try
            {
                if (File.Exists(RepoStatePath))
                {
                    var s = JsonUtility.FromJson<RepoState>(File.ReadAllText(RepoStatePath));
                    if (s != null && s.repo == repo && s.files != null) return s;
                }
            }
            catch { }
            return new RepoState { repo = repo };
        }

        void SaveRepoState()
        {
            try { File.WriteAllText(RepoStatePath, JsonUtility.ToJson(_repoState, true)); }
            catch (Exception e) { Logger.LogWarning("Pose repo: could not save sync state: " + e.Message); }
        }

        static string Hash(string text)
        {
            using (var md5 = MD5.Create())
            {
                var b = md5.ComputeHash(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n")));
                var sb = new StringBuilder();
                foreach (var x in b) sb.Append(x.ToString("x2"));
                return sb.ToString();
            }
        }

        // ------------------------------------------------------------------ window

        string RepoSummary()
        {
            var fresh = new List<string>(); var upd = new List<string>(); var mine = new List<string>();
            foreach (var u in _repoUpdates)
            {
                string label = !string.IsNullOrEmpty(u.set.name) ? u.set.name : u.key;
                if (u.conflict) mine.Add(label); else if (u.overlapping.Count > 0) upd.Add(label); else fresh.Add(label);
            }
            var sb = new StringBuilder("New poses on GitHub (" + _repoName.Value + ")\n");
            if (fresh.Count > 0) sb.Append("\n" + fresh.Count + " new: " + Shorten(fresh));
            if (upd.Count > 0) sb.Append("\n" + upd.Count + " updated: " + Shorten(upd));
            if (mine.Count > 0) sb.Append("\n" + mine.Count + " would replace poses you made: " + Shorten(mine) + "\n(Sync now keeps yours; Sync + replace mine backs yours up first.)");
            sb.Append("\n\nSync to the latest poses?");
            return sb.ToString();
        }

        static string Shorten(List<string> names)
        {
            if (names.Count <= 6) return string.Join(", ", names.ToArray());
            return string.Join(", ", names.GetRange(0, 6).ToArray()) + " and " + (names.Count - 6) + " more";
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void ShowRepoPanel()
        {
            var api = _repoPanelApi as Sodalite.Api.LockablePanel;
            if (api == null)
            {
                api = new Sodalite.Api.LockablePanel();
                api.Configure += BuildRepoPanel;
                _repoPanelApi = api;
            }
            _repoPanel = api.GetOrCreatePanel();
            _repoPanel.SetActive(true);

            var head = GM.CurrentPlayerBody.Head;
            var fwd = head.forward; fwd.y = 0;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            _repoPanel.transform.position = head.position + fwd * 0.7f - Vector3.up * 0.1f;
            _repoPanel.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);
            bool anyConflict = _repoUpdates.Exists(u => u.conflict);
            SetRepoPanel(RepoSummary(), null);
            ShowRepoButtons(true, anyConflict);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void BuildRepoPanel(GameObject panel)
        {
            _repoButtons.Clear();
            var canvas = panel.transform.Find("OptionsCanvas_0_Main/Canvas");
            if (canvas == null) { Logger.LogError("Pose update window: panel canvas not found"); return; }
            const float scale = 0.07f, w = 37f / scale, h = 24f / scale, buttonsH = 120f;

            Sodalite.UiWidgets.UiWidget.CreateAndConfigureWidget(canvas.gameObject, (Sodalite.UiWidgets.TextWidget t) =>
            {
                Place(t.RectTransform, scale, new Vector2(w - 24, h - buttonsH - 24), new Vector2(0, buttonsH / 2f));
                t.Text.alignment = TextAnchor.UpperLeft;
                t.Text.fontSize = 20;
                t.Text.resizeTextForBestFit = true;
                t.Text.resizeTextMinSize = 9;
                t.Text.resizeTextMaxSize = 20;
                t.Text.color = Color.white;
                t.Text.text = "PoseKit";
                _repoText = t.Text;
            });

            Sodalite.UiWidgets.UiWidget.CreateAndConfigureWidget(canvas.gameObject, (Sodalite.UiWidgets.GridLayoutWidget g) =>
            {
                Place(g.RectTransform, scale, new Vector2(w - 24, buttonsH - 12), new Vector2(0, -(h - buttonsH) / 2f));
                g.LayoutGroup.spacing = new Vector2(6, 6);
                g.LayoutGroup.cellSize = new Vector2((w - 24 - 6) / 2f, (buttonsH - 12 - 6) / 2f);
                g.LayoutGroup.startCorner = GridLayoutGroup.Corner.UpperLeft;
                g.LayoutGroup.startAxis = GridLayoutGroup.Axis.Horizontal;
                g.LayoutGroup.childAlignment = TextAnchor.UpperCenter;
                g.LayoutGroup.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                g.LayoutGroup.constraintCount = 2;

                Action<string, string, Color, Action> add = (key, label, colour, onClick) =>
                {
                    g.AddChild((Sodalite.UiWidgets.ButtonWidget b) =>
                    {
                        b.ButtonText.text = label;
                        b.ButtonText.fontSize = 18;
                        b.ButtonText.resizeTextForBestFit = true;
                        b.ButtonText.resizeTextMinSize = 8;
                        b.ButtonText.resizeTextMaxSize = 18;
                        b.AddButtonListener((s, a) =>
                        {
                            try { onClick(); }
                            catch (Exception e) { Logger.LogError("Pose update window: " + e); SetRepoPanel("Error: " + e.Message, null); }
                        });
                        var outline = b.ButtonText.gameObject.GetComponent<Outline>();
                        if (outline == null) outline = b.ButtonText.gameObject.AddComponent<Outline>();
                        outline.effectColor = new Color(0f, 0f, 0f, 0.65f);
                        outline.effectDistance = new Vector2(1f, -1f);
                        new VrPaint { pointable = b.Pointable, image = b.ButtonImage, text = b.ButtonText, colour = colour }.Apply(false);
                        _repoButtons[key] = b.gameObject;
                    });
                };
                add("sync", "Sync now", ColPose, () => SyncRepo(false));
                add("replace", "Sync + replace mine", ColRotate, () => SyncRepo(true));
                add("later", "Not now", ColSession, CloseRepoPanel);
                add("skip", "Skip this update", ColSession, SkipRepoUpdate);
                add("close", "Close", ColSession, CloseRepoPanel);
            });
        }

        // asking = the question (Sync / Not now / Skip); otherwise just Close (plus "replace" if some were kept).
        void ShowRepoButtons(bool asking, bool offerReplace)
        {
            GameObject go;
            if (_repoButtons.TryGetValue("sync", out go)) go.SetActive(asking);
            if (_repoButtons.TryGetValue("replace", out go)) go.SetActive(offerReplace);
            if (_repoButtons.TryGetValue("later", out go)) go.SetActive(asking);
            if (_repoButtons.TryGetValue("skip", out go)) go.SetActive(asking);
            if (_repoButtons.TryGetValue("close", out go)) go.SetActive(!asking);
        }

        // offerReplace: null = leave the buttons as they are; else switch to the result view.
        void SetRepoPanel(string text, bool? offerReplace)
        {
            if (_repoText != null) _repoText.text = text;
            if (offerReplace.HasValue) ShowRepoButtons(false, offerReplace.Value);
        }

        void CloseRepoPanel()
        {
            if (_repoPanel != null) _repoPanel.SetActive(false);
        }
    }
}
