using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using FistVR;
using JerryGloves;
using UnityEngine;

namespace GT3PoseKit
{
    [BepInPlugin(Guid, "GlovesTan3 PoseKit", Version)]
    [BepInProcess("h3vr.exe")]
    [BepInDependency(H3MPGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public partial class PoseKitPlugin : BaseUnityPlugin
    {
        public const string Guid = "gt3posekit.loader";
        public const string Version = "1.0.0";
        const string InjectedName = "GT3PoseKit_HandPoseItem";
        const string H3MPGuid = "VIP.TommySoucy.H3MP";

        static readonly string[] Slots = { "IdleL", "IdleR", "ForeL", "ForeR", "FireStartL", "FireStartR", "FireEndL", "FireEndR" };

        string _dir;
        readonly Dictionary<string, PoseSet> _byItemId = new Dictionary<string, PoseSet>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, PoseSet> _byName = new Dictionary<string, PoseSet>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, PoseSet> _magById = new Dictionary<string, PoseSet>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, PoseSet> _magByType = new Dictionary<string, PoseSet>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<int> _checked = new HashSet<int>();

        ConfigEntry<KeyCode> _keyCaptureL, _keyCaptureR, _keyReload, _keyDumpRig, _keyExportModel, _keyOpenEditor, _keyVrEditor;
        ConfigEntry<bool> _exportMeshes, _bridgeEnabled, _allowLive, _autoCopy, _openOnStart, _supportEnabled, _ghostEnabled, _freezeDefault;
        ConfigEntry<float> _autoCopyThreshold, _supportRange, _ghostOpacity;
        ConfigEntry<string> _ghostColourL, _ghostColourR;
        const string SupportName = "GT3PoseKit_SupportGrip";
        ConfigEntry<int> _maxMeshVerts, _bridgePort, _gloveTexSize;
        ModelServer _server;

        void Awake()
        {
            _dir = Path.Combine(Paths.ConfigPath, "GlovesTan3Poses");
            Directory.CreateDirectory(_dir);
            Directory.CreateDirectory(Path.Combine(_dir, "captures"));
            Directory.CreateDirectory(Path.Combine(_dir, "models"));

            _keyCaptureL = Config.Bind("Keys", "CaptureLeft", KeyCode.F5, "Capture the left hand's pose on the object it holds.");
            _keyCaptureR = Config.Bind("Keys", "CaptureRight", KeyCode.F6, "Capture the right hand's pose on the object it holds.");
            _keyReload = Config.Bind("Keys", "ReloadPoses", KeyCode.F7, "Rescan Folders: re-read every pose file, the empty-hand poses and glove skins, and re-apply to spawned objects.");
            _keyDumpRig = Config.Bind("Keys", "DumpRig", KeyCode.F8, "Write the GlovesTan3 hand skeleton to rig.json.");
            _exportMeshes = Config.Bind("Capture", "ExportMeshes", true, "Include readable gun meshes in captures (the editor shows them as reference).");
            _maxMeshVerts = Config.Bind("Capture", "MaxMeshVertices", 120000, "Upper limit on total exported mesh vertices per capture or model.");
            _gloveTexSize = Config.Bind("Capture", "GloveTextureSize", 1024, "Largest side of the glove texture sent to the editor (pixels).");
            _keyExportModel = Config.Bind("Keys", "ExportHeldModel", KeyCode.F9, "Write the model of the object in either hand to the models folder.");
            _keyOpenEditor = Config.Bind("Keys", "OpenEditor", KeyCode.F10, "Open the PoseKit editor in your default browser.");
            _keyVrEditor = Config.Bind("Keys", "VrEditor", KeyCode.F11, "Open the in-game (VR) pose editor panel.");
            _bridgeEnabled = Config.Bind("Editor bridge", "Enabled", true, "Serve item models, the hand rig and captures to the PoseKit editor over localhost.");
            _openOnStart = Config.Bind("Editor bridge", "OpenEditorOnStart", false, "Open the editor in your default browser automatically when the game starts.");
            _bridgePort = Config.Bind("Editor bridge", "Port", 48261, "Localhost port for the editor bridge (must match the port in the editor).");
            _allowLive = Config.Bind("Editor bridge", "AllowLiveImport", true, "Let the editor push pose sets into the running game (and save them into the pose folder when asked).");
            _autoCopy = Config.Bind("Auto copy", "InGameFallback", false, "Off by default: use the editor's \"Auto copy to similar guns\" button instead. When on, a gun without poses silently reuses the poses of the most similarly named gun that has them (e.g. G18 uses G17's).");
            _autoCopyThreshold = Config.Bind("Auto copy", "MinimumSimilarity", Similarity.DefaultThreshold, "How alike the names must be, 0 to 1. 0.9 = only number variants like G17/G18; 0.85 also allows suffixes like G17L.");
            _supportEnabled = Config.Bind("Support grip", "Enabled", true, "On pistols (and guns without a foregrip), when H3VR two-hand stabilizes the gun (free hand empty, palm within 15 cm of the shooting hand) that hand shows its Foregrip pose: a support grip.");
            _ghostEnabled = Config.Bind("Ghost hands", "Enabled", true, "Show a see-through glove, posed as the slot being edited (browser or VR editor), on the matching weapon nearest the VR editor panel (or your head).");
            _ghostOpacity = Config.Bind("Ghost hands", "Opacity", 0.35f, "Ghost hand opacity, 0 to 1.");
            _ghostColourL = Config.Bind("Ghost hands", "ColourLeft", "#3CC2CF", "Ghost colour for left-hand slots (#RRGGBB).");
            _ghostColourR = Config.Bind("Ghost hands", "ColourRight", "#F0A052", "Ghost colour for right-hand slots (#RRGGBB).");
            _freezeDefault = Config.Bind("Ghost hands", "FreezeWeapon", false, "Start with 'Freeze gun' on: the weapon showing the ghost floats in place (no gravity) whenever nobody holds it. Toggle it from either editor.");
            _freezeOn = _freezeDefault.Value;
            BindRepoConfig();
            BindEmptyConfig(); BindSkinConfig();
            LoadEmptyHand();
            _supportRange = Config.Bind("Support grip", "Range", 0.08f, "Palm-to-palm distance (metres) within which the support pose shows. H3VR stabilizes up to 0.15; smaller is less sensitive. It lets go a little further out (x1.25) so it doesn't flicker.");

            LoadPoseSets();
            try
            {
                SupportGripKeepPatch.Plugin = this;
                new HarmonyLib.Harmony(Guid).PatchAll(typeof(SupportGripKeepPatch));
            }
            catch (Exception e) { Logger.LogWarning("Support grip may flicker (Harmony patch failed): " + e.Message); }
            try
            {
                EmptyHandPatch.Plugin = this;
                new HarmonyLib.Harmony(Guid + ".emptyhand").PatchAll(typeof(EmptyHandPatch));
            }
            catch (Exception e) { Logger.LogWarning("Empty hand pose unavailable (Harmony patch failed): " + e.Message); }
            try
            {
                PalmedMagPatch.Plugin = this;
                new HarmonyLib.Harmony(Guid + ".palmedmag").PatchAll(typeof(PalmedMagPatch));
            }
            catch (Exception e) { Logger.LogWarning("Palmed magazine placement unavailable (Harmony patch failed): " + e.Message); }
            try
            {
                SpawnGuardPatch.Log = Logger;
                new HarmonyLib.Harmony(Guid + ".spawnguard").PatchAll(typeof(SpawnGuardPatch));
            }
            catch (Exception e) { Logger.LogWarning("Glove spawn guard unavailable (Harmony patch failed): " + e.Message); }
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(H3MPGuid))
            {
                try { StartH3MPGloveSync(); }
                catch (Exception e) { Logger.LogWarning("H3MP glove sync unavailable (other players won't see your gloves): " + e.Message); }
            }
            try { RegisterWristMenu(); }
            catch (Exception e) { Logger.LogWarning("Sodalite wrist menu unavailable, hotkeys only: " + e.Message); }
            if (_bridgeEnabled.Value)
            {
                try
                {
                    _server = new ModelServer(_bridgePort.Value, HandleRequest);
                    _server.PageHtml = LoadEditorPage();
                    _server.Start();
                    Logger.LogInfo("Editor bridge listening on http://127.0.0.1:" + _bridgePort.Value + "/"
                                   + (_server.PageHtml != null ? "  (open that address in a browser for the editor)" : ""));
                    if (_openOnStart.Value) OpenEditor();
                }
                catch (Exception e) { _server = null; Logger.LogError("Editor bridge could not start on port " + _bridgePort.Value + ": " + e.Message); }
            }
            Logger.LogInfo("Pose folder: " + _dir);
        }

        // Kept in its own method so H3MP's types are only loaded when H3MP is installed.
        [MethodImpl(MethodImplOptions.NoInlining)]
        void StartH3MPGloveSync()
        {
            var send = Config.Bind("Multiplayer", "ShareGloves", true, "In H3MP, show your GlovesTan3 gloves to other players (they need PoseKit and GlovesTan3 too).");
            var show = Config.Bind("Multiplayer", "ShowOthersGloves", true, "In H3MP, show other players' GlovesTan3 gloves on their hands.");
            var rate = Config.Bind("Multiplayer", "SendRate", 20, "How many times per second your glove pose is sent to other players (5 to 60).");
            H3MPGloveSync.SendEnabled = send.Value;
            H3MPGloveSync.ShowEnabled = show.Value;
            H3MPGloveSync.SendInterval = 1f / Mathf.Clamp(rate.Value, 5, 60);
            send.SettingChanged += (s, e) => H3MPGloveSync.SendEnabled = send.Value;
            show.SettingChanged += (s, e) => H3MPGloveSync.ShowEnabled = show.Value;
            rate.SettingChanged += (s, e) => H3MPGloveSync.SendInterval = 1f / Mathf.Clamp(rate.Value, 5, 60);
            H3MPGloveSync.Install(gameObject, Logger, new HarmonyLib.Harmony(Guid + ".h3mp"));
        }

        // The editor page: an index.html next to the DLL wins (handy for updates), else the built-in copy.
        byte[] LoadEditorPage()
        {
            try
            {
                var local = Path.Combine(Path.GetDirectoryName(Info.Location), "index.html");
                if (File.Exists(local)) return File.ReadAllBytes(local);
                using (var s = typeof(PoseKitPlugin).Assembly.GetManifestResourceStream("GT3PoseKit.editor.html"))
                {
                    if (s == null) return null;
                    var buf = new byte[s.Length];
                    int read = 0;
                    while (read < buf.Length) { int n = s.Read(buf, read, buf.Length - read); if (n <= 0) break; read += n; }
                    return buf;
                }
            }
            catch (Exception e) { Logger.LogWarning("Editor page unavailable: " + e.Message); return null; }
        }

        void OnDestroy()
        {
            if (_server != null) _server.Stop();
        }

        // ------------------------------------------------------------------ keep each hand on the right poses

        static readonly System.Reflection.FieldInfo LockedOnForegripField =
            typeof(HandsPoseSystem).GetField("_lockedOnForegrip", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        static readonly System.Reflection.MethodInfo RefreshPoseNowMethod =
            typeof(HandPoseItem).GetMethod("RefreshPoseNow", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        HandsPoseSystem[] _systems;
        float _systemsAt = -10f;

        static HandPoseItem DirectHpi(Transform t)
        {
            for (int i = 0; i < t.childCount; i++)
            {
                var h = t.GetChild(i).GetComponent<HandPoseItem>();
                if (h != null) return h;
            }
            return null;
        }

        // GlovesTan3 picks a hand's poses once, when the grab starts, and keeps them while the hand stays near
        // the gun. Moving from the grip to the pump (or poses being added to the pump while you hold it) left
        // the hand on the gun's Idle pose. Each frame, point every glove at the poses attached to what its
        // hand actually holds.
        void LateUpdate()
        {
            if (LockedHpiField == null) return;
            bool refreshed = false;
            if (_systems == null || Time.time - _systemsAt > 2f) { _systems = FindObjectsOfType<HandsPoseSystem>(); _systemsAt = Time.time; refreshed = true; }
            try { UpdateGloveSkin(refreshed); }
            catch (Exception e) { if (_ghostWarned.Add("skin:" + e.Message)) Logger.LogWarning("Glove skin: " + e.Message); }
            try { UpdateEmptyHands(); }
            catch (Exception e) { if (_ghostWarned.Add("empty")) Logger.LogWarning("Empty hand pose: " + e.Message); }
            var mm = GM.CurrentMovementManager;
            if (mm == null || mm.Hands == null || mm.Hands.Length < 2) return;
            foreach (var sys in _systems)
            {
                if (sys == null) continue;
                var hand = mm.Hands[sys.isLeft ? 0 : 1]; // GlovesTan3's own glove-to-hand mapping
                var io = hand != null ? hand.CurrentInteractable : null;
                if (io == null) continue;
                var desired = DirectHpi(HpiParent(io));
                if (desired == null) continue; // nothing attached to this object: leave GlovesTan3's choice alone
                // A one-handed hold by the foregrip is an "alternate hold" of the gun itself.
                var physIo = io as FVRPhysicalObject;
                bool onFore = io is FVRAlternateGrip || (physIo != null && physIo.IsAltHeld);
                bool sameItem = ReferenceEquals(LockedHpiField.GetValue(sys), desired);
                bool sameFore = LockedOnForegripField == null || (bool)LockedOnForegripField.GetValue(sys) == onFore;
                if (sameItem && sameFore) continue;
                LockedHpiField.SetValue(sys, desired);
                if (LockedOnForegripField != null) LockedOnForegripField.SetValue(sys, onFore);
                if (RefreshPoseNowMethod != null)
                {
                    try { RefreshPoseNowMethod.Invoke(desired, new object[] { sys.isLeft, hand, onFore }); }
                    catch (Exception e) { Logger.LogWarning("Could not refresh the hand pose: " + e.Message); }
                }
            }
            UpdateSupportGrips(mm.Hands);
        }

        // Which support-grip item each glove is currently showing (by glove side).
        readonly Dictionary<bool, HandPoseItem> _supportOn = new Dictionary<bool, HandPoseItem>();

        // Support grip: while H3VR two-hand stabilizes a foregrip-less gun (empty off hand, palms within 15 cm),
        // that hand takes its side's Foregrip pose, snapped onto the gun. Moving the hand away lets it go again.
        void UpdateSupportGrips(FVRViveHand[] hands)
        {
            foreach (var sys in _systems)
            {
                if (sys == null) continue;
                bool left = sys.isLeft;
                var hand = hands[left ? 0 : 1];
                var other = hands[left ? 1 : 0];
                HandPoseItem active;
                _supportOn.TryGetValue(left, out active);
                if (active == null) _supportOn.Remove(left); // destroyed (e.g. gun despawned)

                HandPoseItem want = null;
                if (_supportEnabled.Value && hand != null && other != null)
                {
                    // Exactly when H3VR's own two-handed stabilization is on (off hand empty, palms within 15 cm).
                    var gun = RootObject(other.CurrentInteractable) as FVRFireArm;
                    if (StabilizingWith(gun, hand) && WithinSupportRange(gun, hand, active != null))
                    {
                        var sup = FindSupport(HpiParent(gun));
                        if (sup != null && (left ? sup.ForeL : sup.ForeR) != null) want = sup;
                    }
                }

                if (want != null)
                {
                    if (active == want && ReferenceEquals(LockedHpiField.GetValue(sys), want)) continue;
                    LockedHpiField.SetValue(sys, want);
                    if (LockedOnForegripField != null) LockedOnForegripField.SetValue(sys, true);
                    if (RefreshPoseNowMethod != null)
                    {
                        try { RefreshPoseNowMethod.Invoke(want, new object[] { left, hand, true }); }
                        catch (Exception e) { Logger.LogWarning("Could not show the support grip: " + e.Message); }
                    }
                    _supportOn[left] = want;
                }
                else if (active != null)
                {
                    if (ReferenceEquals(LockedHpiField.GetValue(sys), active)) LockedHpiField.SetValue(sys, null);
                    if (LockedOnForegripField != null) LockedOnForegripField.SetValue(sys, false);
                    _supportOn.Remove(left);
                }
            }
        }

        // Kept in its own method so a missing Sodalite only fails here.
        [MethodImpl(MethodImplOptions.NoInlining)]
        void RegisterWristMenu()
        {
            AddButton("PoseKit: Capture L", h => Capture(true));
            AddButton("PoseKit: Capture R", h => Capture(false));
            AddButton("PoseKit: Rescan Folders", h => Reload());
            AddButton("PoseKit: Dump rig", h => DumpRig());
            AddButton("PoseKit: Export held model", h => ExportHeldModel());
            AddButton("PoseKit: Open editor", h => OpenEditor());
            AddButton("PoseKit: VR editor", h => OpenVrEditor(h));
            AddButton("PoseKit: Next glove skin", h => NextSkin());
            AddButton("PoseKit: Search for glove skins", h => SearchSkins());
        }

        // Opens the editor the plugin serves in the desktop's default browser.
        void OpenEditor()
        {
            if (_server == null || _server.PageHtml == null)
            {
                Notify("The editor bridge isn't running (check [Editor bridge] Enabled in gt3posekit.loader.cfg)");
                return;
            }
            var url = "http://127.0.0.1:" + _server.Port + "/";
            try { Application.OpenURL(url); Notify("Opened the PoseKit editor in your browser: " + url); }
            catch (Exception e) { Notify("Could not open a browser (" + e.Message + "). Open " + url + " yourself."); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void AddButton(string text, Action<FVRViveHand> onClick)
        {
            var btn = new Sodalite.Api.WristMenuButton(text, (sender, args) => onClick(args.Hand));
#pragma warning disable 618 // same API GlovesTan3 itself uses; puts buttons under WristMenu -> CustomButtons
            Sodalite.Api.WristMenuAPI.Buttons.Add(btn);
#pragma warning restore 618
        }

        void Update()
        {
            if (Input.GetKeyDown(_keyCaptureL.Value)) Capture(true);
            if (Input.GetKeyDown(_keyCaptureR.Value)) Capture(false);
            if (Input.GetKeyDown(_keyReload.Value)) Reload();
            if (Input.GetKeyDown(_keyDumpRig.Value)) DumpRig();
            if (Input.GetKeyDown(_keyExportModel.Value)) ExportHeldModel();
            if (Input.GetKeyDown(_keyOpenEditor.Value)) OpenEditor();
            if (Input.GetKeyDown(_keyVrEditor.Value)) OpenVrEditor(null);
            if (_server != null) _server.Pump(2);
            if (_injected.Count > 0) UpdateMagRelease();
            if (_injected.Count > 0) UpdateRetention();
            try { UpdateGhost(); }
            catch (Exception e) { if (_ghostWarned.Add("update")) Logger.LogWarning("Ghost hands: " + e.Message); }

            if (_byItemId.Count == 0 && _byName.Count == 0 && _magById.Count == 0 && _magByType.Count == 0) return;
            var mm = GM.CurrentMovementManager;
            if (mm == null || mm.Hands == null) return;
            foreach (var hand in mm.Hands)
            {
                if (hand == null) continue;
                var phys = RootObject(hand.CurrentInteractable);
                if (phys != null) TryInject(phys);
            }
        }

        // ------------------------------------------------------------------ loading

        static bool HasPoses(PoseData[] poses)
        {
            if (poses == null) return false;
            foreach (var p in poses) if (p != null && p.joints != null && p.joints.Length > 0) return true;
            return false;
        }

        void LoadPoseSets()
        {
            _byItemId.Clear();
            _byName.Clear();
            _magById.Clear();
            _magByType.Clear();
            int n = 0;
            foreach (var file in Directory.GetFiles(_dir, "*.json"))
            {
                if (Path.GetFileName(file).Equals("rig.json", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var set = JsonUtility.FromJson<PoseSet>(File.ReadAllText(file));
                    if (set == null || set.format != "gt3poses") continue;
                    if (set.match == null) { Logger.LogWarning(file + ": no match block, skipped"); continue; }
                    Register(set);
                    n++;
                }
                catch (Exception e) { Logger.LogError("Failed to read " + file + ": " + e.Message); }
            }
            Logger.LogInfo("Loaded " + n + " pose set(s).");
        }

        void Register(PoseSet set)
        {
            if (set.match != null)
            {
                foreach (var id in set.match.itemIds ?? new string[0]) if (!string.IsNullOrEmpty(id)) _byItemId[id.Trim()] = set;
                foreach (var nm in set.match.objectNames ?? new string[0]) if (!string.IsNullOrEmpty(nm)) _byName[nm.Trim()] = set;
            }
            var mag = set.magazine;
            if (mag != null && HasPoses(mag.poses))
            {
                foreach (var id in mag.itemIds ?? new string[0]) if (!string.IsNullOrEmpty(id)) _magById[id.Trim()] = set;
                foreach (var t in mag.magazineTypes ?? new string[0]) if (!string.IsNullOrEmpty(t)) _magByType[t.Trim()] = set;
            }
        }

        // A pose set's identity across live updates: its name, else its first match key.
        static string SetKey(PoseSet s)
        {
            if (s == null) return "";
            if (!string.IsNullOrEmpty(s.name)) return s.name.Trim();
            if (s.match != null && s.match.itemIds != null && s.match.itemIds.Length > 0) return s.match.itemIds[0];
            if (s.magazine != null && s.magazine.magazineTypes != null && s.magazine.magazineTypes.Length > 0) return "mag:" + s.magazine.magazineTypes[0];
            return "";
        }

        static void Unregister(Dictionary<string, PoseSet> dict, string key)
        {
            var drop = new List<string>();
            foreach (var kv in dict) if (SetKey(kv.Value) == key) drop.Add(kv.Key);
            foreach (var k in drop) dict.Remove(k);
        }

        // ------------------------------------------------------------------ live import from the editor

        // Every HandPoseItem we created, so a live update can find and refresh it.
        class Injected
        {
            public HandPoseItem hpi;
            public Transform parent;
            public PoseSet set;
            public string target; // "gun", "mag" or "part:<path>"
            public bool foreFromIdle; // part is an alternate grip (pump…): Fore slots mirror Idle
            public Xf frame;          // "pumpfore" and "gun": the pump's rest frame in gun space (null if no pump)
        }
        readonly List<Injected> _injected = new List<Injected>();

        static PoseData[] PosesFor(PoseSet set, string target)
        {
            if (target == "gun") return set.poses;
            if (target == "mag") return set.magazine != null ? set.magazine.poses : null;
            if (target == "pump") return set.pump != null ? set.pump.poses : null;
            if (target.StartsWith("part:"))
            {
                var path = target.Substring(5);
                foreach (var p in set.parts ?? new PartPoses[0]) if (p != null && p.path == path) return p.poses;
            }
            return null;
        }

        static HandPose GetSlot(HandPoseItem h, string slot)
        {
            switch (slot)
            {
                case "IdleL": return h.IdleL;
                case "IdleR": return h.IdleR;
                case "ForeL": return h.ForeL;
                case "ForeR": return h.ForeR;
                case "FireStartL": return h.FireStartL;
                case "FireStartR": return h.FireStartR;
                case "FireEndL": return h.FireEndL;
                default: return h.FireEndR;
            }
        }

        // Rewrite the joints of an existing HandPoseItem. GlovesTan3 copies these into its working pose
        // every frame, so a held gun shows the change at once. Returns false when the slot layout changed.
        static bool UpdateInPlace(HandPoseItem h, PoseData[] data, bool hasTrigger)
        {
            var poses = new Dictionary<string, PoseData>();
            foreach (var p in data ?? new PoseData[0])
                if (p != null && !string.IsNullOrEmpty(p.slot) && p.joints != null && p.joints.Length > 0) poses[p.slot] = p;
            foreach (var slot in Slots)
            {
                var hp = GetSlot(h, slot);
                PoseData pd;
                bool has = poses.TryGetValue(slot, out pd);
                if ((hp != null) != has) return false;
                if (hp != null && (hp.Fingers == null || hp.Fingers.Length != pd.joints.Length)) return false;
            }
            foreach (var slot in Slots)
            {
                var hp = GetSlot(h, slot);
                if (hp == null) continue;
                var pd = poses[slot];
                for (int i = 0; i < hp.Fingers.Length; i++)
                {
                    if (hp.Fingers[i] == null) continue;
                    hp.Fingers[i].transform.localPosition = pd.joints[i].Pos;
                    hp.Fingers[i].transform.localRotation = pd.joints[i].Rot;
                }
            }
            h.hasTrigger = hasTrigger && ((h.FireStartL != null && h.FireEndL != null) || (h.FireStartR != null && h.FireEndR != null));
            return true;
        }

        static readonly System.Reflection.FieldInfo LockedHpiField =
            typeof(HandsPoseSystem).GetField("lockedHpi", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Point any hand that was locked onto the old item at its replacement, so a held gun doesn't need regrabbing.
        static void Relock(HandPoseItem oldHpi, HandPoseItem newHpi)
        {
            if (LockedHpiField == null) return;
            foreach (var sys in FindObjectsOfType<HandsPoseSystem>())
                if (sys != null && ReferenceEquals(LockedHpiField.GetValue(sys), oldHpi)) LockedHpiField.SetValue(sys, newHpi);
        }

        // Apply a pose set sent by the editor. Returns a short JSON summary.
        string ApplyLive(PoseSet set, bool save)
        {
            var key = SetKey(set);
            if (key == "") throw new Exception("pose set needs a name or an item ID");
            Unregister(_byItemId, key); Unregister(_byName, key); Unregister(_magById, key); Unregister(_magByType, key);
            Register(set);

            int updated = 0, rebuilt = 0, removed = 0;
            foreach (var e in _injected.ToArray())
            {
                if (e.hpi == null || e.parent == null) { _injected.Remove(e); continue; }
                if (SetKey(e.set) != key && !SharesKeys(e.set, set)) continue;
                var data = PosesFor(set, e.target);
                if (e.target == "gun") data = GunPoses(set, e.frame);
                if (e.foreFromIdle) data = WithForeFromIdle(data);
                if (e.target == "pumpfore")
                    // Foregrip poses used on the pump. Once the Pump section has poses, they take over: drop this
                    // item so the next frame injects the Pump poses instead.
                    data = set.pump != null && HasPoses(set.pump.poses) ? null : ForeOnPump(set.poses, e.frame);
                bool support = e.target == "support";
                if (support) data = SupportPoses(set.poses);
                bool trig = e.target == "gun" && set.hasTrigger;
                if (!HasPoses(data))
                {
                    Destroy(e.hpi.gameObject); _injected.Remove(e); removed++;
                    continue;
                }
                if (!support) e.hpi.transform.SetSiblingIndex(0); // also fixes items injected before the foregrip fix
                if (UpdateInPlace(e.hpi, data, trig)) updated++;
                else
                {
                    var old = e.hpi;
                    e.hpi = InjectHpi(e.parent, data, trig, e.target == "gun" && set.useGrabPos ? set.grabPos : null);
                    if (support) { e.hpi.gameObject.name = SupportName; e.hpi.transform.SetAsLastSibling(); }
                    Relock(old, e.hpi);
                    foreach (var k in new List<bool>(_supportOn.Keys)) if (_supportOn[k] == old) _supportOn[k] = e.hpi;
                    // Immediate, so GlovesTan3 can't find the old item among the children later this frame.
                    DestroyImmediate(old.gameObject);
                    rebuilt++;
                }
                e.set = set;
            }
            // The gun's own item must stay the first child (GlovesTan3 searches with GetComponentInChildren).
            foreach (var e in _injected) if (e.target == "gun" && e.hpi != null) e.hpi.transform.SetSiblingIndex(0);
            _checked.Clear(); // let held objects that had no poses yet pick this set up
            _similarCache.Clear();

            string saved = "";
            if (save)
            {
                saved = Path.Combine(_dir, SafeFileName(key) + ".json");
                File.WriteAllText(saved, JsonUtility.ToJson(set, true));
            }
            Logger.LogInfo("Live update '" + key + "': " + updated + " updated, " + rebuilt + " rebuilt, " + removed + " removed" + (save ? ", saved to " + saved : ""));
            return "{\"ok\":true,\"key\":\"" + Esc(key) + "\",\"updated\":" + updated + ",\"rebuilt\":" + rebuilt + ",\"removed\":" + removed +
                   ",\"saved\":\"" + Esc(saved) + "\"}";
        }

        // True when two sets target the same gun or magazines (e.g. the set was renamed in the editor).
        static bool SharesKeys(PoseSet a, PoseSet b)
        {
            Func<string[], string[], bool> overlap = (x, y) =>
            {
                if (x == null || y == null) return false;
                foreach (var s in x) foreach (var t in y)
                    if (!string.IsNullOrEmpty(s) && string.Equals(s.Trim(), (t ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            };
            if (a == null || b == null) return false;
            if (a.match != null && b.match != null &&
                (overlap(a.match.itemIds, b.match.itemIds) || overlap(a.match.objectNames, b.match.objectNames))) return true;
            return a.magazine != null && b.magazine != null &&
                   (overlap(a.magazine.itemIds, b.magazine.itemIds) || overlap(a.magazine.magazineTypes, b.magazine.magazineTypes));
        }

        static string Esc(string s) { return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\""); }

        void Reload()
        {
            foreach (var hpi in FindObjectsOfType<HandPoseItem>())
                if (hpi != null && (hpi.gameObject.name == InjectedName || hpi.gameObject.name == SupportName)) Destroy(hpi.gameObject);
            _supportOn.Clear();
            _injected.Clear();
            _checked.Clear();
            _similarCache.Clear();
            LoadPoseSets();
            LoadEmptyHand();
            ReloadSkin();
            Notify("Rescanned folders (" + (_byItemId.Count + _byName.Count) + " gun keys, " + (_magById.Count + _magByType.Count) + " magazine keys)");
        }

        PoseSet FindSet(FVRPhysicalObject phys)
        {
            PoseSet set;
            var id = phys.ObjectWrapper != null ? phys.ObjectWrapper.ItemID : null;
            if (!string.IsNullOrEmpty(id) && _byItemId.TryGetValue(id, out set)) return set;
            if (_byName.TryGetValue(phys.gameObject.name, out set)) return set;
            return AutoCopy(phys, id);
        }

        // ------------------------------------------------------------------ auto copy to similar guns

        readonly Dictionary<string, PoseSet> _similarCache = new Dictionary<string, PoseSet>(StringComparer.OrdinalIgnoreCase);

        static string DisplayNameOf(string itemId)
        {
            FVRObject o;
            return IM.OD != null && IM.OD.TryGetValue(itemId, out o) && o != null ? o.DisplayName : null;
        }

        // A gun without its own poses borrows those of the most similarly named gun that has them
        // (G18 uses the G17 set). Compares item IDs and display names; magazines match by type instead.
        PoseSet AutoCopy(FVRPhysicalObject phys, string id)
        {
            if (!_autoCopy.Value || string.IsNullOrEmpty(id) || phys is FVRFireArmMagazine || !(phys is FVRFireArm)) return null;
            PoseSet cached;
            if (_similarCache.TryGetValue(id, out cached)) return cached;

            var name = phys.ObjectWrapper != null ? phys.ObjectWrapper.DisplayName : null;
            PoseSet best = null;
            string bestKey = null;
            float bestScore = _autoCopyThreshold.Value;
            foreach (var kv in _byItemId)
            {
                if (kv.Value == null || kv.Value.noAutoCopy || !HasPoses(kv.Value.poses) && (kv.Value.parts == null || kv.Value.parts.Length == 0)) continue;
                float s = Similarity.Score(id, kv.Key);
                var otherName = DisplayNameOf(kv.Key);
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(otherName)) s = Math.Max(s, Similarity.Score(name, otherName));
                if (s >= bestScore) { bestScore = s; best = kv.Value; bestKey = kv.Key; }
            }
            _similarCache[id] = best;
            if (best != null)
                Logger.LogInfo("Auto copy: " + id + " uses the poses of " + bestKey + " ('" + best.name + "', similarity " + bestScore.ToString("0.00") + ")");
            return best;
        }

        PoseSet FindMagazineSet(FVRFireArmMagazine mag)
        {
            PoseSet set;
            var id = mag.ObjectWrapper != null ? mag.ObjectWrapper.ItemID : null;
            if (!string.IsNullOrEmpty(id) && _magById.TryGetValue(id, out set)) return set;
            if (_magByType.TryGetValue(mag.MagazineType.ToString(), out set)) return set;
            return null;
        }

        // ------------------------------------------------------------------ injection

        // GlovesTan3 looks for the HandPoseItem among the direct children of FVRInteractiveObject.GameObject.
        static Transform HpiParent(FVRInteractiveObject io)
        {
            return io.GameObject != null ? io.GameObject.transform : io.transform;
        }

        static bool HasDirectHpi(Transform t)
        {
            for (int i = 0; i < t.childCount; i++)
                if (t.GetChild(i).GetComponent<HandPoseItem>() != null) return true;
            return false;
        }

        static string RelPath(Transform root, Transform t)
        {
            var names = new List<string>();
            for (; t != null && t != root; t = t.parent) names.Insert(0, t.name);
            return string.Join("/", names.ToArray());
        }

        // Grabbable sub-parts: slides, bolt and charging handles… Magazines and attachments are physical objects
        // of their own, fixed forward grips use the gun's Foregrip slots, and pumps have their own Pump section,
        // so alternate grips are left out.
        static bool IsPart(FVRInteractiveObject io, Transform root)
        {
            return io != null && io.transform != root && !(io is FVRPhysicalObject) && !(io is FVRAlternateGrip)
                   && io.GetComponentInParent<HandPoseItem>() == null;
        }

        // The gun's pump: a shotgun pump (TubeFedShotgunHandle) first, then a moving fore-handle
        // (ClosedBoltForeHandle), then any other special alternate grip. Plain fixed foregrips don't count.
        static FVRAlternateGrip FindPump(Transform root)
        {
            FVRAlternateGrip best = null;
            int bestRank = int.MaxValue;
            foreach (var g in root.GetComponentsInChildren<FVRAlternateGrip>(true))
            {
                if (g == null || g.GetType() == typeof(FVRAlternateGrip) || g.GetComponentInParent<HandPoseItem>() != null) continue;
                var n = g.GetType().Name;
                int rank = n == "TubeFedShotgunHandle" ? 0 : n == "ClosedBoltForeHandle" ? 1 : 2;
                if (rank < bestRank) { best = g; bestRank = rank; }
            }
            return best;
        }

        // GlovesTan3 treats a held alternate grip (e.g. a pump) as "on the foregrip" and may read the Fore slots,
        // so for those parts the Fore slots mirror Idle and the hand gets the part's pose either way.
        static PoseData[] WithForeFromIdle(PoseData[] data)
        {
            var list = new List<PoseData>();
            bool hasForeL = false, hasForeR = false;
            PoseData idleL = null, idleR = null;
            foreach (var p in data ?? new PoseData[0])
            {
                if (p == null) continue;
                list.Add(p);
                if (p.slot == "ForeL") hasForeL = true;
                if (p.slot == "ForeR") hasForeR = true;
                if (p.slot == "IdleL") idleL = p;
                if (p.slot == "IdleR") idleR = p;
            }
            if (!hasForeL && idleL != null) list.Add(new PoseData { slot = "ForeL", joints = idleL.joints });
            if (!hasForeR && idleR != null) list.Add(new PoseData { slot = "ForeR", joints = idleR.joints });
            return list.ToArray();
        }

        static FVRInteractiveObject FindPart(Transform root, PartPoses part)
        {
            var all = root.GetComponentsInChildren<FVRInteractiveObject>(true);
            foreach (var io in all)
                if (IsPart(io, root) && RelPath(root, io.transform) == part.path) return io;
            if (!string.IsNullOrEmpty(part.type))
                foreach (var io in all)
                    if (IsPart(io, root) && io.GetType().Name == part.type) return io;
            return null;
        }

        void TryInject(FVRPhysicalObject phys)
        {
            int key = phys.GetInstanceID();
            if (_checked.Contains(key)) return;
            _checked.Add(key);

            var set = FindSet(phys);
            if (set != null)
            {
                try { InjectGun(phys, set); }
                catch (Exception e) { Logger.LogError("Could not apply '" + set.name + "': " + e); }
                return;
            }

            var mag = phys as FVRFireArmMagazine;
            if (mag != null)
            {
                set = FindMagazineSet(mag);
                if (set == null) return;
                var parent = HpiParent(mag);
                if (HasDirectHpi(parent)) return;
                try
                {
                    Track(InjectHpi(parent, set.magazine.poses, false, null), parent, set, "mag");
                    Logger.LogInfo("Applied magazine poses from '" + set.name + "' to " + mag.gameObject.name);
                }
                catch (Exception e) { Logger.LogError("Could not apply magazine poses from '" + set.name + "': " + e); }
            }
        }

        void InjectGun(FVRPhysicalObject phys, PoseSet set)
        {
            var root = phys.transform;
            var parent = HpiParent(phys);
            // On a pump gun, remember where the pump rests: holding the gun one-handed by the pump is an
            // "alternate hold" of the gun itself, which GlovesTan3 shows with the gun's Foregrip slots.
            var pumpIo = FindPump(root);
            Xf pumpFrame = pumpIo != null ? ToGunSpace(root, HpiParent(pumpIo)) : null;
            var gunData = GunPoses(set, pumpFrame);
            if (HasPoses(gunData) && !HasDirectHpi(parent))
            {
                Track(InjectHpi(parent, gunData, set.hasTrigger, set.useGrabPos ? set.grabPos : null), parent, set, "gun", false, pumpFrame);
                Logger.LogInfo("Applied pose set '" + set.name + "' to " + phys.gameObject.name);
            }
            // Guns without any foregrip (pistols…): the Foregrip poses become a two-handed support grip. They get
            // their own HandPoseItem, kept after the gun's own so GlovesTan3 never mistakes it for the gun's.
            if (SupportsStabilizationPose(phys, root) && HasForePoses(set.poses) && FindSupport(parent) == null)
            {
                var sup = InjectHpi(parent, SupportPoses(set.poses), false, null);
                sup.gameObject.name = SupportName;
                sup.transform.SetAsLastSibling();
                Track(sup, parent, set, "support");
                Logger.LogInfo("Support grip ready on " + phys.gameObject.name + " (uses its Foregrip poses)");
            }
            foreach (var part in set.parts ?? new PartPoses[0])
            {
                if (part == null || !HasPoses(part.poses)) continue;
                var io = FindPart(root, part);
                if (io == null) { Logger.LogWarning("'" + set.name + "': part '" + part.path + "' not found on " + phys.gameObject.name); continue; }
                var pp = HpiParent(io);
                if (HasDirectHpi(pp)) continue;
                Track(InjectHpi(pp, part.poses, false, null), pp, set, "part:" + part.path);
                Logger.LogInfo("Applied '" + (part.name ?? part.path) + "' poses to " + phys.gameObject.name);
            }

            // Pump poses go on the pump itself, so the hand slides with it. On a pump-action gun a foregrip hold
            // IS the pump: without Pump poses, the gun's Foregrip poses are used on the pump instead.
            bool hasPump = set.pump != null && HasPoses(set.pump.poses);
            if (hasPump || HasForePoses(set.poses))
            {
                var pump = FindPump(root);
                if (pump == null)
                {
                    if (hasPump) Logger.LogWarning("'" + set.name + "' has pump poses but " + phys.gameObject.name + " has no pump");
                }
                else
                {
                    var pp = HpiParent(pump);
                    if (!HasDirectHpi(pp))
                    {
                        if (hasPump)
                        {
                            // GlovesTan3 treats a held pump as a foregrip, so the Fore slots carry the same pose.
                            Track(InjectHpi(pp, WithForeFromIdle(set.pump.poses), false, null), pp, set, "pump", true);
                            Logger.LogInfo("Applied pump poses to " + pump.name + " (" + pump.GetType().Name + ") on " + phys.gameObject.name);
                        }
                        else
                        {
                            var frame = ToGunSpace(root, pp); // where the pump rests now, in gun space
                            Track(InjectHpi(pp, ForeOnPump(set.poses, frame), false, null), pp, set, "pumpfore", false, frame);
                            Logger.LogInfo("Pump-action " + phys.gameObject.name + ": Foregrip poses of '" + set.name + "' used on the pump " + pump.name + " (" + pump.GetType().Name + ")");
                        }
                    }
                }
            }
        }

        // The gun's own poses. On a pump gun (pumpFrame set), any Foregrip slot the gun lacks is filled from the
        // Pump pose placed where the pump rests, so a one-handed hold by the pump keeps the hand on the pump
        // instead of falling back to Idle (the trigger position).
        static PoseData[] GunPoses(PoseSet set, Xf pumpFrame)
        {
            if (pumpFrame == null || set.pump == null || !HasPoses(set.pump.poses)) return set.poses;
            var list = new List<PoseData>();
            bool hasL = false, hasR = false;
            foreach (var p in set.poses ?? new PoseData[0])
            {
                if (p == null) continue;
                list.Add(p);
                if (p.slot == "ForeL") hasL = true;
                if (p.slot == "ForeR") hasR = true;
            }
            var fp = pumpFrame.Pos;
            var fq = pumpFrame.Rot;
            foreach (var p in set.pump.poses)
            {
                if (p == null || p.joints == null || (p.slot != "IdleL" && p.slot != "IdleR")) continue;
                var side = p.slot.Substring(4);
                if ((side == "L" && hasL) || (side == "R" && hasR)) continue;
                var j = new Xf[p.joints.Length];
                for (int i = 0; i < j.Length; i++) j[i] = Xf.From(fp + fq * p.joints[i].Pos, fq * p.joints[i].Rot);
                list.Add(new PoseData { slot = "Fore" + side, joints = j });
            }
            return list.ToArray();
        }

        // Which guns get the support grip: pistols always (H3VR gives many of them a "_ForeGrip" grab point for
        // two-handed holds, which shouldn't rule this out), plus any other gun without a forward grip. Rifles
        // with a real foregrip don't, so an empty hand near their grip never jumps to the foregrip pose.
        static bool SupportsStabilizationPose(FVRPhysicalObject phys, Transform root)
        {
            if (!(phys is FVRFireArm)) return false;
            if (phys is Handgun || phys is Revolver || phys is SingleActionRevolver || phys is Derringer) return true;
            return root.GetComponentInChildren<FVRAlternateGrip>(true) == null;
        }

        // The support-grip item's poses: each side's Foregrip pose in every slot GlovesTan3 might read.
        static PoseData[] SupportPoses(PoseData[] gunPoses)
        {
            var list = new List<PoseData>();
            foreach (var p in gunPoses ?? new PoseData[0])
            {
                if (p == null || (p.slot != "ForeL" && p.slot != "ForeR") || p.joints == null) continue;
                var side = p.slot.Substring(4);
                list.Add(new PoseData { slot = "Idle" + side, joints = p.joints });
                list.Add(new PoseData { slot = "Fore" + side, joints = p.joints });
            }
            return list.ToArray();
        }

        static HandPoseItem FindSupport(Transform gunParent)
        {
            var t = gunParent.Find(SupportName);
            return t != null ? t.GetComponent<HandPoseItem>() : null;
        }

        static bool HasForePoses(PoseData[] poses)
        {
            foreach (var p in poses ?? new PoseData[0])
                if (p != null && (p.slot == "ForeL" || p.slot == "ForeR") && p.joints != null && p.joints.Length > 0) return true;
            return false;
        }

        // The gun's ForeL/ForeR poses re-expressed relative to the pump (as its Idle and Fore slots).
        static PoseData[] ForeOnPump(PoseData[] gunPoses, Xf frame)
        {
            var list = new List<PoseData>();
            var fp = frame.Pos;
            var inv = Quaternion.Inverse(frame.Rot);
            foreach (var p in gunPoses ?? new PoseData[0])
            {
                if (p == null || (p.slot != "ForeL" && p.slot != "ForeR") || p.joints == null) continue;
                var j = new Xf[p.joints.Length];
                for (int i = 0; i < j.Length; i++) j[i] = Xf.From(inv * (p.joints[i].Pos - fp), inv * p.joints[i].Rot);
                var side = p.slot.Substring(4);
                list.Add(new PoseData { slot = "Idle" + side, joints = j });
                list.Add(new PoseData { slot = "Fore" + side, joints = j });
            }
            return list.ToArray();
        }

        void Track(HandPoseItem hpi, Transform parent, PoseSet set, string target, bool foreFromIdle = false, Xf frame = null)
        {
            _injected.Add(new Injected { hpi = hpi, parent = parent, set = set, target = target, foreFromIdle = foreFromIdle, frame = frame });
        }

        HandPoseItem InjectHpi(Transform parent, PoseData[] data, bool hasTrigger, Xf grabPos)
        {
            var go = new GameObject(InjectedName);
            go.SetActive(false);
            go.transform.SetParent(parent, false);
            // First child: when you grab a foregrip, GlovesTan3 searches the gun with GetComponentInChildren,
            // which returns the first HandPoseItem it meets. As the last child, a part's poses (e.g. the
            // bolt's) were found before the gun's own, so the foregrip used the bolt pose.
            go.transform.SetSiblingIndex(0);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            // Pose data is unscaled object space; cancel out the object's own scale.
            var s = parent.lossyScale;
            go.transform.localScale = new Vector3(SafeInv(s.x), SafeInv(s.y), SafeInv(s.z));

            var poses = new Dictionary<string, PoseData>();
            foreach (var p in data ?? new PoseData[0])
                if (p != null && !string.IsNullOrEmpty(p.slot) && p.joints != null && p.joints.Length > 0) poses[p.slot] = p;

            var hpi = go.AddComponent<HandPoseItem>();
            hpi.IdleL = Build(go.transform, "IdleL", poses);
            hpi.IdleR = Build(go.transform, "IdleR", poses);
            hpi.ForeL = Build(go.transform, "ForeL", poses);
            hpi.ForeR = Build(go.transform, "ForeR", poses);
            hpi.FireStartL = Build(go.transform, "FireStartL", poses);
            hpi.FireStartR = Build(go.transform, "FireStartR", poses);
            hpi.FireEndL = Build(go.transform, "FireEndL", poses);
            hpi.FireEndR = Build(go.transform, "FireEndR", poses);
            // CurPose is the scratch pose GlovesTan3 blends into; it needs its own joints.
            hpi.CurPoseL = hpi.IdleL != null ? Build(go.transform, "CurPoseL", poses["IdleL"]) : null;
            hpi.CurPoseR = hpi.IdleR != null ? Build(go.transform, "CurPoseR", poses["IdleR"]) : null;
            hpi.hasTrigger = hasTrigger &&
                ((hpi.FireStartL != null && hpi.FireEndL != null) || (hpi.FireStartR != null && hpi.FireEndR != null));

            if (grabPos != null)
            {
                var gp = new GameObject("GrabPos");
                gp.transform.SetParent(go.transform, false);
                gp.transform.localPosition = grabPos.Pos;
                gp.transform.localRotation = grabPos.Rot;
                hpi.grabpos = gp;
            }

            go.SetActive(true);
            return hpi;
        }

        static HandPose Build(Transform parent, string slot, Dictionary<string, PoseData> poses)
        {
            PoseData pd;
            return poses.TryGetValue(slot, out pd) ? Build(parent, slot, pd) : null;
        }

        static HandPose Build(Transform parent, string name, PoseData pd)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var hp = go.AddComponent<HandPose>();
            var fingers = new GameObject[pd.joints.Length];
            for (int i = 0; i < fingers.Length; i++)
            {
                var j = new GameObject("j" + i);
                j.transform.SetParent(go.transform, false);
                j.transform.localPosition = pd.joints[i].Pos;
                j.transform.localRotation = pd.joints[i].Rot;
                fingers[i] = j;
            }
            hp.Fingers = fingers;
            return hp;
        }

        static float SafeInv(float v) { return Mathf.Abs(v) < 1e-6f ? 1f : 1f / v; }

        // ------------------------------------------------------------------ capture & rig

        static FVRPhysicalObject RootObject(FVRInteractiveObject io)
        {
            if (io == null) return null;
            var alt = io as FVRAlternateGrip;
            if (alt != null && alt.PrimaryObject != null) return alt.PrimaryObject;
            var phys = io as FVRPhysicalObject;
            if (phys != null) return phys;
            return io.GetComponentInParent<FVRPhysicalObject>();
        }

        static HandsPoseSystem FindSystem(bool left)
        {
            foreach (var s in FindObjectsOfType<HandsPoseSystem>())
                if (s != null && s.isLeft == left) return s;
            return null;
        }

        // The hand a glove belongs to, matched exactly the way GlovesTan3 does it (Hands[0] for its left glove,
        // Hands[1] for its right), so captures and pose switching always agree with the glove you see.
        static FVRViveHand FindHand(bool left)
        {
            var mm = GM.CurrentMovementManager;
            if (mm == null || mm.Hands == null) return null;
            if (mm.Hands.Length >= 2) return mm.Hands[left ? 0 : 1];
            foreach (var h in mm.Hands)
                if (h != null && h.IsThisTheRightHand != left) return h;
            return null;
        }

        static Xf ToGunSpace(Transform gun, Transform t)
        {
            var inv = Quaternion.Inverse(gun.rotation);
            return Xf.From(inv * (t.position - gun.position), inv * t.rotation);
        }

        // The deepest transform that contains every joint: the frame the editor moves the hand by.
        static Transform CommonRoot(GameObject[] joints)
        {
            List<Transform> chain = null;
            foreach (var g in joints)
            {
                if (g == null) continue;
                var anc = new List<Transform>();
                for (var t = g.transform; t != null; t = t.parent) anc.Insert(0, t);
                if (chain == null) { chain = anc; continue; }
                int n = 0;
                while (n < chain.Count && n < anc.Count && chain[n] == anc[n]) n++;
                chain.RemoveRange(n, chain.Count - n);
            }
            if (chain == null || chain.Count == 0) return null;
            var root = chain[chain.Count - 1];
            // If the common ancestor is itself a joint, use its parent so the joint stays editable.
            foreach (var g in joints) if (g != null && g.transform == root) return root.parent ?? root;
            return root;
        }

        static RigHand BuildRig(bool left, GameObject[] src)
        {
            var root = CommonRoot(src);
            var index = new Dictionary<Transform, int>();
            for (int i = 0; i < src.Length; i++) if (src[i] != null) index[src[i].transform] = i;

            var joints = new RigJoint[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                var rj = new RigJoint { name = src[i] != null ? src[i].name : "missing" + i, parent = -1, local = new Xf() };
                joints[i] = rj;
                if (src[i] == null) continue;
                var t = src[i].transform;
                Transform parentT = root;
                for (var a = t.parent; a != null && a != root; a = a.parent)
                {
                    int pi;
                    if (index.TryGetValue(a, out pi)) { rj.parent = pi; parentT = a; break; }
                }
                if (parentT == null) { rj.local = Xf.From(t.position, t.rotation); continue; }
                var inv = Quaternion.Inverse(parentT.rotation);
                rj.local = Xf.From(inv * (t.position - parentT.position), inv * t.rotation);
            }
            return new RigHand { side = left ? "L" : "R", rootName = root != null ? root.name : "", joints = joints };
        }

        static GameObject[] PoseSource(HandsPoseSystem sys, out bool fromPhysics)
        {
            fromPhysics = sys.HandPhys != null && sys.HandPhys.activeInHierarchy && sys.realfingers != null
                          && sys.Fingers != null && sys.realfingers.Length == sys.Fingers.Length;
            return fromPhysics ? sys.realfingers : sys.Fingers;
        }

        RigDump BuildRigDump(out string error)
        {
            error = null;
            var l = FindSystem(true);
            var r = FindSystem(false);
            if (l == null && r == null) { error = "Spawn the GlovesTan3 gloves first"; return null; }
            bool phys;
            return new RigDump
            {
                left = l != null ? BuildRig(true, PoseSource(l, out phys)) : null,
                right = r != null ? BuildRig(false, PoseSource(r, out phys)) : null
            };
        }

        void DumpRig()
        {
            string error;
            var dump = BuildRigDump(out error);
            if (dump == null) { Notify(error); return; }
            var path = Path.Combine(_dir, "rig.json");
            File.WriteAllText(path, JsonUtility.ToJson(dump, true));
            Notify("Rig written: " + path);
            var glove = BuildGloveDump(out error);
            if (glove != null)
            {
                var gpath = Path.Combine(_dir, "glove.json");
                File.WriteAllText(gpath, JsonUtility.ToJson(glove));
                Notify("Glove model written: " + gpath);
            }
            else Logger.LogWarning("Glove model not exported: " + error);
        }

        GloveDump BuildGloveDump(out string error)
        {
            error = null;
            var l = FindSystem(true);
            var r = FindSystem(false);
            if (l == null && r == null) { error = "Spawn the GlovesTan3 gloves first"; return null; }
            string el = null, er = null;
            var dump = new GloveDump
            {
                left = l != null ? GloveExport.Build(l, true, _gloveTexSize.Value, out el) : null,
                right = r != null ? GloveExport.Build(r, false, _gloveTexSize.Value, out er) : null
            };
            if (dump.left == null && dump.right == null) { error = er ?? el ?? "No glove mesh found"; return null; }
            return dump;
        }

        CaptureData BuildCapture(bool left, out string error)
        {
            error = null;
            var sys = FindSystem(left);
            if (sys == null) { error = "Spawn the GlovesTan3 gloves first"; return null; }
            var hand = FindHand(left);
            var io = hand != null ? hand.CurrentInteractable : null;
            var phys = RootObject(io);
            bool support = false;
            if (phys == null && hand != null)
            {
                // A free hand stabilizing a gun held by the other hand (H3VR's two-handed stabilization):
                // capture it as that gun's support (Foregrip) grip.
                var other = FindHand(!left);
                var gunHeld = other != null ? RootObject(other.CurrentInteractable) as FVRFireArm : null;
                if (StabilizingWith(gunHeld, hand) && WithinSupportRange(gunHeld, hand, true))
                {
                    phys = gunHeld;
                    support = true;
                }
            }
            if (phys == null) { error = "Hold an object with the " + (left ? "left" : "right") + " hand to capture"; return null; }

            bool fromPhysics;
            var src = PoseSource(sys, out fromPhysics);
            var gun = phys.transform;
            var root = CommonRoot(src);
            int readable, boxed;

            var cap = new CaptureData
            {
                itemId = phys.ObjectWrapper != null ? phys.ObjectWrapper.ItemID : "",
                objectName = phys.gameObject.name,
                side = left ? "L" : "R",
                onForegrip = io is FVRAlternateGrip || support,
                isSupport = support,
                source = phys,
                fromPhysics = fromPhysics,
                handRoot = root != null ? ToGunSpace(gun, root) : new Xf(),
                joints = new Xf[src.Length],
                rig = BuildRig(left, src),
                geo = BuildGeo(gun, out readable, out boxed)
            };
            for (int i = 0; i < src.Length; i++)
                cap.joints[i] = src[i] != null ? ToGunSpace(gun, src[i].transform) : new Xf();
            if (IsPart(io, gun))
            {
                cap.partPath = RelPath(gun, io.transform);
                cap.partType = io.GetType().Name;
                cap.partXf = ToGunSpace(gun, HpiParent(io));
            }
            else if (io is FVRAlternateGrip && io == FindPump(gun))
            {
                cap.isPump = true;
                cap.onForegrip = false;
                cap.partType = io.GetType().Name;
                cap.partXf = ToGunSpace(gun, HpiParent(io));
            }
            else if (io == phys && phys.IsAltHeld)
            {
                // Held one-handed by the foregrip ("alternate hold"): that's the pump on a pump gun,
                // otherwise the gun's Foregrip slot.
                var pump = FindPump(gun);
                if (pump != null)
                {
                    cap.isPump = true;
                    cap.onForegrip = false;
                    cap.partType = pump.GetType().Name;
                    cap.partXf = ToGunSpace(gun, HpiParent(pump));
                }
                else cap.onForegrip = true;
            }
            var mag = phys as FVRFireArmMagazine;
            if (mag != null) { cap.isMagazine = true; cap.magazineType = mag.MagazineType.ToString(); cap.magPalmed = HasPalmedPartner(mag); }
            return cap;
        }

        void Capture(bool left)
        {
            string error;
            var cap = BuildCapture(left, out error);
            if (cap == null) { Notify(error); return; }
            var safe = SafeFileName(string.IsNullOrEmpty(cap.itemId) ? cap.objectName : cap.itemId);
            var file = Path.Combine(Path.Combine(_dir, "captures"),
                safe + "_" + cap.side + (cap.onForegrip ? "_fore" : "") + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json");
            File.WriteAllText(file, JsonUtility.ToJson(cap));
            Notify("Captured " + cap.side + " on " + safe);
            Logger.LogInfo("Capture written: " + file);
        }

        static string SafeFileName(string s)
        {
            if (string.IsNullOrEmpty(s)) s = "object";
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        // ------------------------------------------------------------------ models

        ModelData BuildModel(string itemId, string name, string category, Transform root)
        {
            int readable, boxed;
            var geo = BuildGeo(root, out readable, out boxed);
            var model = new ModelData { itemId = itemId, name = name, category = category, readableMeshes = readable, boxedMeshes = boxed, geo = geo };
            var mag = root.GetComponent<FVRFireArmMagazine>();
            var gun = root.GetComponent<FVRFireArm>();
            if (mag != null) { model.isMagazine = true; model.magazineType = mag.MagazineType.ToString(); }
            else if (gun != null)
            {
                model.magazineType = gun.MagazineType.ToString();
                var ids = new List<string>();
                List<FVRObject> compat;
                if (IM.CompatMags != null && IM.CompatMags.TryGetValue(gun.MagazineType, out compat) && compat != null)
                    foreach (var o in compat) if (o != null && !string.IsNullOrEmpty(o.ItemID)) ids.Add(o.ItemID);
                model.compatibleMags = ids.ToArray();
            }
            return model;
        }

        void ExportHeldModel()
        {
            var mm = GM.CurrentMovementManager;
            FVRPhysicalObject phys = null;
            if (mm != null && mm.Hands != null)
                foreach (var h in mm.Hands)
                    if (h != null && phys == null) phys = RootObject(h.CurrentInteractable);
            if (phys == null) { Notify("Hold an object to export its model"); return; }
            var w = phys.ObjectWrapper;
            var id = w != null ? w.ItemID : phys.gameObject.name;
            var model = BuildModel(id, w != null ? w.DisplayName : phys.gameObject.name, w != null ? w.Category.ToString() : "", phys.transform);
            var file = Path.Combine(Path.Combine(_dir, "models"), SafeFileName(id) + ".json");
            File.WriteAllText(file, JsonUtility.ToJson(model));
            Notify("Model written: " + file + " (" + model.readableMeshes + " meshes, " + model.boxedMeshes + " boxed)");
        }

        // ------------------------------------------------------------------ editor bridge

        void HandleRequest(string method, string path, Dictionary<string, string> q, string body, out int status, out string json)
        {
            status = 200;
            string error, v;
            if (method == "POST")
            {
                if (path == "/freeze")
                {
                    try { json = HandleFreezeRequest(body); }
                    catch (Exception e) { status = 400; json = ModelServer.Err(e.Message); }
                    return;
                }
                if (path == "/ghost")
                {
                    // Show-only: draws the editor's current pose as a ghost; nothing is applied or saved.
                    try { json = HandleGhostRequest(body); }
                    catch (Exception e) { status = 400; json = ModelServer.Err(e.Message); }
                    return;
                }
                if (path != "/poses") { status = 404; json = ModelServer.Err("Unknown endpoint " + path); return; }
                if (!_allowLive.Value) { status = 403; json = ModelServer.Err("Live import is turned off in gt3posekit.loader.cfg"); return; }
                PoseSet set;
                try { set = JsonUtility.FromJson<PoseSet>(body ?? ""); }
                catch (Exception e) { status = 400; json = ModelServer.Err("Not valid JSON: " + e.Message); return; }
                if (set == null || set.format != "gt3poses") { status = 400; json = ModelServer.Err("Expected a gt3poses pose set"); return; }
                bool save = q.TryGetValue("save", out v) && (v == "1" || v == "true");
                try { json = ApplyLive(set, save); }
                catch (Exception e) { status = 400; json = ModelServer.Err(e.Message); }
                return;
            }
            switch (path)
            {
                case "/status":
                    json = JsonUtility.ToJson(new BridgeStatus
                    {
                        version = Version,
                        items = IM.OD != null ? IM.OD.Count : 0,
                        gloves = FindSystem(true) != null || FindSystem(false) != null,
                        freeze = _freezeOn,
                        poseFolder = _dir
                    });
                    return;

                case "/posesets":
                {
                    // The pose sets saved in the pose folder, so the editor can tell new downloads from replacements.
                    var list = new List<LocalSet>();
                    foreach (var file in Directory.GetFiles(_dir, "*.json"))
                    {
                        var fn = Path.GetFileName(file);
                        if (fn.Equals("rig.json", StringComparison.OrdinalIgnoreCase) || fn.Equals("glove.json", StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            var set = JsonUtility.FromJson<PoseSet>(File.ReadAllText(file));
                            if (set == null || set.format != "gt3poses") continue;
                            list.Add(new LocalSet
                            {
                                key = SetKey(set), file = fn,
                                itemIds = set.match != null && set.match.itemIds != null ? set.match.itemIds : new string[0],
                                modified = File.GetLastWriteTime(file).ToString("yyyy-MM-ddTHH:mm:ss")
                            });
                        }
                        catch { }
                    }
                    json = JsonUtility.ToJson(new LocalSetList { sets = list.ToArray() });
                    return;
                }

                case "/items":
                {
                    var list = new List<ItemInfo>();
                    if (IM.OD != null)
                        foreach (var kv in IM.OD)
                        {
                            var o = kv.Value;
                            if (o == null) continue;
                            list.Add(new ItemInfo { id = kv.Key, name = o.DisplayName, cat = o.Category.ToString(), mod = o.IsModContent });
                        }
                    list.Sort((a, b) => string.Compare(a.name ?? a.id, b.name ?? b.id, StringComparison.OrdinalIgnoreCase));
                    json = JsonUtility.ToJson(new ItemList { items = list.ToArray() });
                    return;
                }

                case "/model":
                {
                    FVRObject o;
                    if (!q.TryGetValue("id", out v) || IM.OD == null || !IM.OD.TryGetValue(v, out o) || o == null)
                    { status = 404; json = ModelServer.Err("No item with ID '" + v + "'"); return; }
                    GameObject prefab;
                    try { prefab = o.GetGameObject(); }
                    catch (Exception e) { status = 500; json = ModelServer.Err("Could not load " + v + ": " + e.Message); return; }
                    if (prefab == null) { status = 404; json = ModelServer.Err(v + " has no prefab"); return; }
                    json = JsonUtility.ToJson(BuildModel(o.ItemID, o.DisplayName, o.Category.ToString(), prefab.transform));
                    return;
                }

                case "/rig":
                {
                    var dump = BuildRigDump(out error);
                    if (dump == null) { status = 409; json = ModelServer.Err(error); return; }
                    json = JsonUtility.ToJson(dump);
                    return;
                }

                case "/glove":
                {
                    var dump = BuildGloveDump(out error);
                    if (dump == null) { status = 409; json = ModelServer.Err(error); return; }
                    json = JsonUtility.ToJson(dump);
                    return;
                }

                case "/capture":
                {
                    bool left = q.TryGetValue("side", out v) && v.ToUpperInvariant().StartsWith("L");
                    var cap = BuildCapture(left, out error);
                    if (cap == null) { status = 409; json = ModelServer.Err(error); return; }
                    json = JsonUtility.ToJson(cap);
                    return;
                }

                default:
                    status = 404;
                    json = ModelServer.Err("Unknown endpoint " + path);
                    return;
            }
        }

        // Active within the object's own hierarchy (prefabs are never "activeInHierarchy").
        static bool ActiveUnder(Transform t, Transform root)
        {
            for (; t != null; t = t.parent)
            {
                if (!t.gameObject.activeSelf) return false;
                if (t == root) return true;
            }
            return true;
        }

        GunGeo BuildGeo(Transform gun, out int readable, out int boxed)
        {
            readable = 0;
            boxed = 0;
            var prims = new List<GeoPrim>();
            var meshes = new List<GeoMesh>();
            var markers = new List<Marker>();
            var inv = Quaternion.Inverse(gun.rotation);
            var gs = gun.lossyScale;

            Func<Transform, bool> skip = t => t.GetComponentInParent<HandPoseItem>() != null;
            Func<Transform, float[]> relScale = t =>
            {
                var s = t.lossyScale;
                return new[] { s.x * SafeInv(gs.x), s.y * SafeInv(gs.y), s.z * SafeInv(gs.z) };
            };
            Func<Vector3, float[]> f3 = v => new[] { v.x, v.y, v.z };

            foreach (var col in gun.GetComponentsInChildren<Collider>(true))
            {
                if (col == null || skip(col.transform)) continue;
                var gp = new GeoPrim { name = col.name, xf = ToGunSpace(gun, col.transform), scale = relScale(col.transform), trigger = col.isTrigger };
                var bc = col as BoxCollider;
                var sc = col as SphereCollider;
                var cc = col as CapsuleCollider;
                var mc = col as MeshCollider;
                if (bc != null) { gp.kind = "box"; gp.center = f3(bc.center); gp.size = f3(bc.size); }
                else if (sc != null) { gp.kind = "sphere"; gp.center = f3(sc.center); gp.radius = sc.radius; }
                else if (cc != null) { gp.kind = "capsule"; gp.center = f3(cc.center); gp.radius = cc.radius; gp.height = cc.height; gp.dir = cc.direction; }
                else if (mc != null && mc.sharedMesh != null)
                {
                    // Local-space bounds work for prefabs too (world bounds need a spawned object).
                    var b = mc.sharedMesh.bounds;
                    gp.kind = "bounds"; gp.center = f3(b.center); gp.size = f3(b.size);
                }
                else continue;
                prims.Add(gp);
            }

            if (_exportMeshes.Value)
            {
                int budget = _maxMeshVerts.Value;
                var parts = new List<KeyValuePair<Transform, Mesh>>();
                foreach (var mf in gun.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf == null || mf.sharedMesh == null || skip(mf.transform) || !ActiveUnder(mf.transform, gun)) continue;
                    var mr = mf.GetComponent<MeshRenderer>();
                    if (mr == null || !mr.enabled) continue;
                    parts.Add(new KeyValuePair<Transform, Mesh>(mf.transform, mf.sharedMesh));
                }
                foreach (var smr in gun.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (smr == null || smr.sharedMesh == null || !smr.enabled || skip(smr.transform) || !ActiveUnder(smr.transform, gun)) continue;
                    parts.Add(new KeyValuePair<Transform, Mesh>(smr.transform, smr.sharedMesh));
                }

                foreach (var part in parts)
                {
                    var t = part.Key;
                    var mesh = part.Value;
                    Vector3[] verts = null;
                    int[] tris = null;
                    // Checking isReadable first avoids Unity logging an error for every locked mesh.
                    if (mesh.isReadable && mesh.vertexCount <= budget)
                    {
                        try { verts = mesh.vertices; tris = mesh.triangles; } catch { verts = null; }
                    }
                    if (verts != null && verts.Length > 0 && tris != null && tris.Length > 0)
                    {
                        budget -= verts.Length;
                        var v = new float[verts.Length * 3];
                        for (int i = 0; i < verts.Length; i++)
                        {
                            var w = inv * (t.TransformPoint(verts[i]) - gun.position);
                            v[i * 3] = w.x; v[i * 3 + 1] = w.y; v[i * 3 + 2] = w.z;
                        }
                        meshes.Add(new GeoMesh { name = t.name, v = v, t = tris });
                        readable++;
                    }
                    else
                    {
                        // Locked mesh: its bounds are still available, so show it as a solid box.
                        var b = mesh.bounds;
                        prims.Add(new GeoPrim
                        {
                            kind = "meshbox", name = t.name, xf = ToGunSpace(gun, t), scale = relScale(t),
                            center = f3(b.center), size = f3(b.size)
                        });
                        boxed++;
                    }
                }
            }

            var seen = new HashSet<Transform>();
            Action<string, Transform> mark = (n, t) =>
            {
                if (t == null || seen.Contains(t) || markers.Count >= 64) return;
                seen.Add(t);
                markers.Add(new Marker { name = n, xf = ToGunSpace(gun, t) });
            };
            foreach (var io in gun.GetComponentsInChildren<FVRInteractiveObject>(true))
                if (io != null && io.PoseOverride != null) mark("PoseOverride: " + io.name, io.PoseOverride);
            string[] keys = { "trigger", "safety", "magrelease", "mag_release", "selector", "foregrip", "grip", "hammer", "slide", "bolt", "handle" };
            foreach (var t in gun.GetComponentsInChildren<Transform>(true))
            {
                if (skip(t)) continue;
                var n = t.name.ToLowerInvariant();
                foreach (var k in keys) if (n.Contains(k)) { mark(t.name, t); break; }
            }

            var partInfos = new List<PartInfo>();
            foreach (var io in gun.GetComponentsInChildren<FVRInteractiveObject>(true))
                if (IsPart(io, gun))
                    partInfos.Add(new PartInfo { name = io.name, path = RelPath(gun, io.transform), type = io.GetType().Name, xf = ToGunSpace(gun, HpiParent(io)) });

            var pumpIo = FindPump(gun);
            var pumpInfo = pumpIo != null
                ? new PartInfo { name = pumpIo.name, path = RelPath(gun, pumpIo.transform), type = pumpIo.GetType().Name, xf = ToGunSpace(gun, HpiParent(pumpIo)) }
                : new PartInfo { name = "", path = "", type = "", xf = new Xf() };

            return new GunGeo { prims = prims.ToArray(), meshes = meshes.ToArray(), markers = markers.ToArray(), parts = partInfos.ToArray(), pump = pumpInfo };
        }

        void Notify(string msg)
        {
            Logger.LogMessage(msg);
        }
    }
}
