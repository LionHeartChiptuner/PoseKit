using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using FistVR;
using JerryGloves;
using UnityEngine;
using UnityEngine.UI;

namespace GT3PoseKit
{
    // In-game (VR) pose editor: a lockable Sodalite panel whose buttons edit the pose of the gun you hold.
    // Every change is applied live (same path as the browser editor's Live sync); Save writes the pose file.
    public partial class PoseKitPlugin
    {
        static readonly string[] VrGunSlots = { "IdleL", "IdleR", "ForeL", "ForeR", "FireStartL", "FireStartR", "FireEndL", "FireEndR", "MagReleaseL", "MagReleaseR" };
        static readonly string[] VrOtherSlots = { "IdleL", "IdleR" };
        static readonly string[] VrMagSlots = { "IdleL", "IdleR", "RetainL", "RetainR" };
        static readonly System.Text.RegularExpressions.Regex RackRe =
            new System.Text.RegularExpressions.Regex("slide|bolt|handle|charg|pump|fore.?end|lever|crank|cock", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        object _vrPanelApi;          // Sodalite LockablePanel (kept as object so this class loads without touching Sodalite)
        GameObject _vrPanel;
        Text _vrStatus;
        readonly Dictionary<string, Text> _vrLabels = new Dictionary<string, Text>();

        FVRPhysicalObject _vrGun;
        PoseSet _vrSet;
        string _vrTarget = "gun";
        string _vrSlot = "IdleR";
        int _vrFinger;                // 0 = all, else VrRig role
        int _vrKnuckle;               // 0 = whole finger, 1 base, 2 mid, 3 tip
        int _vrStepIndex = 1;
        static readonly float[] VrSteps = { 1f, 5f, 15f };
        bool _vrCoupleTip = true;
        bool _vrDirty;
        string _vrMsg = "";
        readonly List<string> _vrUndo = new List<string>();
        readonly Dictionary<bool, VrRig> _vrRigs = new Dictionary<bool, VrRig>();

        // [base, mid, tip] curls; thumb also sets its swing (spread at the base). Same values as the browser editor.
        static readonly Dictionary<string, float[][]> VrPresets = new Dictionary<string, float[][]>
        {
            // { fingers, thumb, index override (null = same as fingers), thumbSpread }
            { "Open",    new[] { new float[] { 0, 0, 0 },    new float[] { 0, 0, 0 },    null,                      new float[] { 0 } } },
            { "Relaxed", new[] { new float[] { 14, 24, 12 }, new float[] { 5, 12, 8 },   null,                      new float[] { 10 } } },
            { "Grip",    new[] { new float[] { 62, 78, 48 }, new float[] { 22, 38, 28 }, null,                      new float[] { 28 } } },
            { "Fist",    new[] { new float[] { 88, 102, 72 }, new float[] { 30, 48, 40 }, null,                     new float[] { 38 } } },
            { "Point",   new[] { new float[] { 88, 102, 72 }, new float[] { 30, 48, 40 }, new float[] { 2, 4, 2 },  new float[] { 38 } } },
            { "Trigger", new[] { new float[] { 62, 78, 48 }, new float[] { 22, 38, 28 }, new float[] { 28, 46, 22 }, new float[] { 28 } } },
            // Gesture defaults for the empty hand (negative curl straightens the thumb past the neutral hand).
            { "Thumbs up", new[] { new float[] { 88, 102, 72 }, new float[] { -12, -18, -12 }, null,                   new float[] { 8 } } },
            { "Finger gun", new[] { new float[] { 88, 102, 72 }, new float[] { -10, -12, -8 }, new float[] { 2, 4, 2 }, new float[] { 36 } } },
        };

        // ------------------------------------------------------------------ opening

        void OpenVrEditor(FVRViveHand hand)
        {
            if (FindSystem(true) == null && FindSystem(false) == null) { Notify("Spawn the GlovesTan3 gloves first (Wrist menu > Custom buttons > Tan Gloves?)"); return; }
            try { ShowVrPanel(hand); }
            catch (Exception e) { Logger.LogError("Could not open the VR editor: " + e); Notify("Could not open the VR editor (Sodalite missing?)"); return; }
            if (_vrGun == null) VrUseHeldGun(hand);
            VrRefresh();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void ShowVrPanel(FVRViveHand hand)
        {
            var api = _vrPanelApi as Sodalite.Api.LockablePanel;
            if (api == null)
            {
                api = new Sodalite.Api.LockablePanel();
                api.Configure += BuildVrPanel;
                _vrPanelApi = api;
            }
            _vrPanel = api.GetOrCreatePanel();
            _vrPanel.SetActive(true);

            // Float it in front of the hand that opened it, facing the player.
            Transform aim = hand != null && hand.PointingTransform != null ? hand.PointingTransform : null;
            var body = GM.CurrentPlayerBody;
            var head = body != null ? body.Head : null;
            Vector3 pos = aim != null ? aim.position + aim.forward * 0.35f : (head != null ? head.position + head.forward * 0.6f : _vrPanel.transform.position);
            _vrPanel.transform.position = pos;
            if (head != null)
            {
                var look = pos - head.position;
                look.y *= 0.5f;
                if (look.sqrMagnitude > 1e-6f) _vrPanel.transform.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void BuildVrPanel(GameObject panel)
        {
            _vrLabels.Clear();
            _vrPaints.Clear();
            var canvas = panel.transform.Find("OptionsCanvas_0_Main/Canvas");
            if (canvas == null) { Logger.LogError("VR editor: panel canvas not found"); return; }
            const float scale = 0.07f, w = 37f / scale, h = 24f / scale, statusH = 62f;

            Sodalite.UiWidgets.UiWidget.CreateAndConfigureWidget(canvas.gameObject, (Sodalite.UiWidgets.TextWidget t) =>
            {
                Place(t.RectTransform, scale, new Vector2(w - 16, statusH), new Vector2(0, (h - statusH) / 2f - 6));
                t.Text.alignment = TextAnchor.UpperLeft;
                t.Text.fontSize = 15;
                t.Text.resizeTextForBestFit = true;
                t.Text.resizeTextMinSize = 8;
                t.Text.resizeTextMaxSize = 15;
                t.Text.color = Color.white;
                t.Text.text = "PoseKit";
                _vrStatus = t.Text;
            });

            const int cols = 6, rows = 9;
            float gridH = h - statusH - 16;
            Sodalite.UiWidgets.UiWidget.CreateAndConfigureWidget(canvas.gameObject, (Sodalite.UiWidgets.GridLayoutWidget g) =>
            {
                Place(g.RectTransform, scale, new Vector2(w - 12, gridH), new Vector2(0, -(statusH / 2f) - 2));
                g.LayoutGroup.spacing = new Vector2(3, 3);
                g.LayoutGroup.cellSize = new Vector2((w - 12 - 3 * (cols - 1)) / cols, (gridH - 3 * (rows - 1)) / rows);
                g.LayoutGroup.startCorner = GridLayoutGroup.Corner.UpperLeft;
                g.LayoutGroup.startAxis = GridLayoutGroup.Axis.Horizontal;
                g.LayoutGroup.childAlignment = TextAnchor.UpperCenter;
                g.LayoutGroup.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                g.LayoutGroup.constraintCount = cols;

                // Each group of related buttons shares a colour (set before adding the group's buttons).
                Color group = ColNav;
                Action<string, string, Action> add = (key, label, onClick) =>
                {
                    var colour = group;
                    g.AddChild((Sodalite.UiWidgets.ButtonWidget b) =>
                    {
                        b.ButtonText.text = label;
                        b.ButtonText.fontSize = 13;
                        b.ButtonText.resizeTextForBestFit = true;
                        b.ButtonText.resizeTextMinSize = 6;
                        b.ButtonText.resizeTextMaxSize = 13;
                        b.AddButtonListener((s, a) => VrClick(onClick));
                        // A thin dark outline keeps the white text readable on the lighter shades.
                        var outline = b.ButtonText.gameObject.GetComponent<Outline>();
                        if (outline == null) outline = b.ButtonText.gameObject.AddComponent<Outline>();
                        outline.effectColor = new Color(0f, 0f, 0f, 0.65f);
                        outline.effectDistance = new Vector2(1f, -1f);
                        var paint = new VrPaint { pointable = b.Pointable, image = b.ButtonImage, text = b.ButtonText, colour = colour };
                        paint.Apply(false);
                        if (key != null) { _vrLabels[key] = b.ButtonText; _vrPaints[key] = paint; }
                    });
                };

                // Target and slot
                group = ColNav;
                add(null, "< Target", () => VrCycleTarget(-1));
                add("target", "Gun", () => VrCycleTarget(1));
                add(null, "Target >", () => VrCycleTarget(1));
                add(null, "< Slot", () => VrCycleSlot(-1));
                add("slot", "Idle R", () => VrCycleSlot(1));
                add(null, "Slot >", () => VrCycleSlot(1));
                // Whole pose
                group = ColPose;
                add(null, "Capture hands", VrCapture);
                add(null, "Reset", VrReset);
                add("copyidle", "Copy Idle", () => { if (OnRetainSlot()) { _vrAdjustMag = !_vrAdjustMag; _vrMsg = _vrAdjustMag ? "Move and Turn now adjust the palmed magazine (hold two palmed magazines to see it)" : "Move and Turn now adjust the hand"; } else VrCopyIdle(); });
                add(null, "Mirror L<>R", VrMirror);
                add(null, "Clear slot", VrClear);
                add(null, "Undo", VrUndoLast);
                // Presets
                group = ColPreset;
                foreach (var p in new[] { "Open", "Relaxed", "Grip", "Fist", "Point", "Trigger" }) { var name = p; add(null, p, () => VrPreset(name)); }
                // Finger
                group = ColFinger;
                for (int f = 0; f <= 5; f++) { int ff = f; add("finger" + f, VrRig.RoleNames[f], () => { _vrFinger = ff; if (ff == 0) _vrKnuckle = 0; }); }
                // Knuckle, step, coupling
                group = ColKnuckle;
                string[] kn = { "Whole", "Base", "Mid", "Tip" };
                for (int k = 0; k < 4; k++) { int kk = k; add("knuckle" + k, kn[k], () => { _vrKnuckle = kk; }); }
                add("step", "Step 5", () => { _vrStepIndex = (_vrStepIndex + 1) % VrSteps.Length; });
                add("couple", "Tip follows: on", () => { _vrCoupleTip = !_vrCoupleTip; });
                // Rotate fingers
                group = ColRotate;
                add(null, "Curl -", () => VrRotate(0, -1));
                add(null, "Curl +", () => VrRotate(0, 1));
                add(null, "Spread -", () => VrRotate(1, -1));
                add(null, "Spread +", () => VrRotate(1, 1));
                add(null, "Twist -", () => VrRotate(2, -1));
                add(null, "Twist +", () => VrRotate(2, 1));
                // Move hand (mm, along the gun's axes)
                group = ColMove;
                add(null, "Left", () => VrMove(Vector3.left));
                add(null, "Right", () => VrMove(Vector3.right));
                add(null, "Down", () => VrMove(Vector3.down));
                add(null, "Up", () => VrMove(Vector3.up));
                add(null, "Back", () => VrMove(Vector3.back));
                add(null, "Forward", () => VrMove(Vector3.forward));
                // Turn hand around the wrist
                group = ColTurn;
                add(null, "Pitch -", () => VrTurn(Vector3.right, -1));
                add(null, "Pitch +", () => VrTurn(Vector3.right, 1));
                add(null, "Yaw -", () => VrTurn(Vector3.up, -1));
                add(null, "Yaw +", () => VrTurn(Vector3.up, 1));
                add(null, "Roll -", () => VrTurn(Vector3.forward, -1));
                add(null, "Roll +", () => VrTurn(Vector3.forward, 1));
                // Session
                group = ColSession;
                add(null, "Use held gun", () => VrUseHeldGun(null));
                add("save", "Save", VrSave);
                add(null, "Revert", VrRevert);
                add(null, "Auto copy", VrAutoCopy);
                add("freeze", "Freeze gun: off", () =>
                {
                    SetFreeze(!_freezeOn);
                    _vrMsg = _freezeOn ? "Freeze on: the ghost's gun stays put when you let go" : "Freeze off: the gun has gravity again";
                    VrRefresh();
                });
                add(null, "Hide panel", () => { if (_vrPanel != null) _vrPanel.SetActive(false); });
            });
        }

        // Button group colours: related buttons share one.
        static readonly Color ColNav = Hex(0x2E6FD8);     // target and slot (blue)
        static readonly Color ColPose = Hex(0x17907F);    // whole-pose actions (teal)
        static readonly Color ColPreset = Hex(0x7B4FC9);  // presets (purple)
        static readonly Color ColFinger = Hex(0xD9822B);  // finger selection (orange)
        static readonly Color ColKnuckle = Hex(0xC9A227); // knuckle selection and options (gold)
        static readonly Color ColRotate = Hex(0xC9405A);  // curl / spread / twist (red)
        static readonly Color ColMove = Hex(0x3C8A3F);    // move hand (green)
        static readonly Color ColTurn = Hex(0x2A9BB8);    // turn hand (cyan)
        static readonly Color ColSession = Hex(0x5A5F6B); // session (grey)

        static Color Hex(int rgb)
        {
            return new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f);
        }

        // One button's colouring. Active (the selected finger / knuckle) is a lighter shade of its group;
        // hovering lightens it a little more. Text is always white.
        class VrPaint
        {
            public FVRPointableButton pointable;
            public Image image;
            public Text text;
            public Color colour;

            public void Apply(bool active)
            {
                var c = active ? Color.Lerp(colour, Color.white, 0.3f) : colour;
                var hover = Color.Lerp(c, Color.white, 0.15f);
                if (pointable != null) { pointable.ColorUnselected = c; pointable.ColorSelected = hover; }
                if (image != null) image.color = c;
                if (text != null) text.color = Color.white; // always white, on every shade
            }
        }

        readonly Dictionary<string, VrPaint> _vrPaints = new Dictionary<string, VrPaint>();

        static void Place(RectTransform rt, float scale, Vector2 size, Vector2 pos)
        {
            rt.localScale = new Vector3(scale, scale, scale);
            rt.localRotation = Quaternion.identity;
            rt.localPosition = Vector3.zero;
            rt.sizeDelta = size;
            rt.anchoredPosition = pos * scale;
        }

        void VrClick(Action a)
        {
            try { a(); }
            catch (Exception e) { _vrMsg = "Error: " + e.Message; Logger.LogError("VR editor: " + e); }
            VrRefresh();
        }

        // ------------------------------------------------------------------ session

        bool SideLeft { get { return _vrSlot.EndsWith("L"); } }

        void VrUseHeldGun(FVRViveHand clicking)
        {
            var mm = GM.CurrentMovementManager;
            FVRPhysicalObject held = null;
            if (mm != null && mm.Hands != null)
            {
                // Prefer the hand that didn't press the button.
                foreach (var hnd in mm.Hands)
                    if (hnd != null && hnd != clicking && held == null) held = RootObject(hnd.CurrentInteractable);
                foreach (var hnd in mm.Hands)
                    if (hnd != null && held == null) held = RootObject(hnd.CurrentInteractable);
            }
            if (held == null || held is FVRFireArmMagazine) { _vrMsg = "Hold a gun, then press Use held gun"; return; }
            if (_vrDirty && _vrGun != null && held != _vrGun) VrSave();
            _vrGun = held;
            var w = held.ObjectWrapper;
            var existing = FindSet(held);
            if (existing != null) _vrSet = JsonUtility.FromJson<PoseSet>(JsonUtility.ToJson(existing));
            else
            {
                _vrSet = new PoseSet
                {
                    format = "gt3poses", version = 2,
                    name = w != null && !string.IsNullOrEmpty(w.DisplayName) ? w.DisplayName : held.gameObject.name.Replace("(Clone)", ""),
                    match = new MatchData { itemIds = w != null && !string.IsNullOrEmpty(w.ItemID) ? new[] { w.ItemID } : new string[0], objectNames = w == null ? new[] { held.gameObject.name } : new string[0] },
                    hasTrigger = true, poses = new PoseData[0], parts = new PartPoses[0],
                    magazine = new MagazinePoses()
                };
            }
            if (_vrSet.magazine == null) _vrSet.magazine = new MagazinePoses();
            if (_vrSet.pump == null) _vrSet.pump = new PumpPoses();
            if (_vrSet.pump.poses == null) _vrSet.pump.poses = new PoseData[0];
            _vrDirty = false;
            // Pump-action gun: move Foregrip poses into the Pump section (relative to the pump), the same
            // conversion the loader uses in game, so they show up and can be edited under Pump.
            var pump = FindPump(held.transform);
            if (pump != null && !HasPoses(_vrSet.pump.poses) && HasForePoses(_vrSet.poses))
            {
                var moved = ForeOnPump(_vrSet.poses, ToGunSpace(held.transform, HpiParent(pump)));
                var keepIdle = new List<PoseData>();
                foreach (var p in moved) if (p.slot.StartsWith("Idle")) keepIdle.Add(p);
                _vrSet.pump.poses = keepIdle.ToArray();
                var rest = new List<PoseData>();
                foreach (var p in _vrSet.poses) if (p != null && p.slot != "ForeL" && p.slot != "ForeR") rest.Add(p);
                _vrSet.poses = rest.ToArray();
                _vrDirty = true;
            }
            var fa = held as FVRFireArm;
            if (fa != null && (_vrSet.magazine.magazineTypes == null || _vrSet.magazine.magazineTypes.Length == 0))
                _vrSet.magazine.magazineTypes = new[] { fa.MagazineType.ToString() };
            if (_vrSet.poses == null) _vrSet.poses = new PoseData[0];
            if (_vrSet.parts == null) _vrSet.parts = new PartPoses[0];
            _vrTarget = "gun";
            _vrUndo.Clear();
            _vrRigs.Clear();
            _vrMsg = existing != null ? "Editing the saved poses for " + _vrSet.name : "New pose set for " + _vrSet.name;
            if (_vrDirty)
            {
                VrApply();
                _vrMsg += ". Pump-action gun: Foregrip poses moved to Pump (Save to keep)";
            }
        }

        VrRig Rig(bool left)
        {
            VrRig r;
            if (_vrRigs.TryGetValue(left, out r)) return r;
            var sys = FindSystem(left);
            if (sys == null) return null;
            bool phys;
            var src = PoseSource(sys, out phys);
            // GlovesTan3's own idle pose is the neutral hand, even while a custom empty-hand pose is on.
            WithOriginalIdle(sys, () => { r = VrRig.Build(BuildRig(left, src), sys.idlePose != null ? sys.idlePose.Fingers : null, left); });
            _vrRigs[left] = r;
            return r;
        }

        List<string> VrTargets()
        {
            var list = new List<string>();
            if (_vrGun != null)
            {
                list.Add("gun");
                var parts = new List<FVRInteractiveObject>();
                foreach (var io in _vrGun.GetComponentsInChildren<FVRInteractiveObject>(true))
                    if (IsPart(io, _vrGun.transform)) parts.Add(io);
                parts.Sort((a, b) =>
                {
                    int ra = RackRe.IsMatch(a.GetType().Name + a.name) ? 0 : 1, rb = RackRe.IsMatch(b.GetType().Name + b.name) ? 0 : 1;
                    return ra != rb ? ra - rb : string.Compare(a.name, b.name, StringComparison.Ordinal);
                });
                foreach (var io in parts) list.Add("part:" + RelPath(_vrGun.transform, io.transform));
                if (FindPump(_vrGun.transform) != null) list.Add("pump");
            }
            if (_vrGun is FVRFireArm) list.Add("mag");
            list.Add("empty"); // the glove when it holds nothing: needs no gun
            return list;
        }

        string[] VrSlots() { return _vrTarget == "gun" ? VrGunSlots : _vrTarget == "empty" ? EmptySlots : _vrTarget == "mag" ? VrMagSlots : VrOtherSlots; }

        void VrCycleTarget(int dir)
        {
            var t = VrTargets();
            int i = Math.Max(0, t.IndexOf(_vrTarget));
            _vrTarget = t[(i + dir + t.Count) % t.Count];
            if (Array.IndexOf(VrSlots(), _vrSlot) < 0) _vrSlot = "Idle" + (SideLeft ? "L" : "R");
        }

        void VrCycleSlot(int dir)
        {
            var s = VrSlots();
            int i = Math.Max(0, Array.IndexOf(s, _vrSlot));
            _vrSlot = s[(i + dir + s.Length) % s.Length];
            // On a pump-action gun the foregrip is the pump: skip to the Pump target.
            if (_vrTarget == "gun" && _vrSlot.StartsWith("Fore") && GunHasPump())
            {
                _vrTarget = "pump";
                _vrSlot = "Idle" + _vrSlot.Substring(4);
                _vrMsg = "Pump-action gun: its foregrip is the Pump";
            }
        }

        bool VrReady()
        {
            if (_vrTarget == "empty") return true;
            if (_vrGun == null || _vrSet == null) { _vrMsg = "Hold a gun, then press Use held gun (or pick the Empty hand target)"; return false; }
            return true;
        }

        // ------------------------------------------------------------------ pose storage for the current target

        PoseData[] TargetArray(string target, bool create)
        {
            if (target == "empty") return _empty.poses;
            if (_vrSet == null) return null;
            if (target == "gun") return _vrSet.poses;
            if (target == "mag") return _vrSet.magazine.poses;
            if (target == "pump") return _vrSet.pump.poses;
            var path = target.Substring(5);
            foreach (var p in _vrSet.parts) if (p.path == path) return p.poses;
            return null;
        }

        void SetTargetArray(string target, PoseData[] arr)
        {
            if (target == "empty") { _empty.poses = arr; return; }
            if (target == "gun") { _vrSet.poses = arr; return; }
            if (target == "mag") { _vrSet.magazine.poses = arr; return; }
            if (target == "pump") { _vrSet.pump.poses = arr; return; }
            var path = target.Substring(5);
            foreach (var p in _vrSet.parts) if (p.path == path) { p.poses = arr; return; }
            string type = "", name = path;
            if (_vrGun != null)
                foreach (var io in _vrGun.GetComponentsInChildren<FVRInteractiveObject>(true))
                    if (IsPart(io, _vrGun.transform) && RelPath(_vrGun.transform, io.transform) == path) { type = io.GetType().Name; name = io.name; break; }
            var list = new List<PartPoses>(_vrSet.parts) { new PartPoses { name = name, path = path, type = type, poses = arr } };
            _vrSet.parts = list.ToArray();
        }

        PoseData GetSlot(string slot)
        {
            var arr = TargetArray(_vrTarget, false);
            if (arr != null) foreach (var p in arr) if (p != null && p.slot == slot) return p;
            return null;
        }

        void PutSlot(PoseData pd)
        {
            var list = new List<PoseData>();
            var arr = TargetArray(_vrTarget, false);
            if (arr != null) foreach (var p in arr) if (p != null && p.slot != pd.slot) list.Add(p);
            list.Add(pd);
            SetTargetArray(_vrTarget, list.ToArray());
        }

        void RemoveSlot(string slot)
        {
            var list = new List<PoseData>();
            var arr = TargetArray(_vrTarget, false);
            if (arr != null) foreach (var p in arr) if (p != null && p.slot != slot) list.Add(p);
            SetTargetArray(_vrTarget, list.ToArray());
        }

        // The current slot's pose in local form. An empty slot starts from Idle, else from the live hand.
        VrRig.Local CurrentLocal(VrRig rig, bool allowCapture)
        {
            var pd = GetSlot(_vrSlot);
            if (pd != null && pd.joints != null && pd.joints.Length == rig.n) return rig.ToLocal(pd);
            if (_vrTarget == "empty")
            {
                // An unposed gesture starts from its built-in default (or GlovesTan3's idle for Idle).
                var def = EmptyGesturePose(_vrSlot);
                return def != null && def.joints != null && def.joints.Length == rig.n ? rig.ToLocal(def) : null;
            }
            var idle = GetSlot("Idle" + (SideLeft ? "L" : "R"));
            if (idle != null && idle.joints != null && idle.joints.Length == rig.n) return rig.ToLocal(idle);
            if (allowCapture)
            {
                var cap = CaptureToTarget();
                if (cap != null) return rig.ToLocal(cap);
            }
            return null;
        }

        // ------------------------------------------------------------------ edits

        void Edit(Func<VrRig, VrRig.Local, bool> change, string done)
        {
            if (!VrReady()) return;
            var rig = Rig(SideLeft);
            if (rig == null) { _vrMsg = "Spawn the GlovesTan3 gloves first"; return; }
            var L = CurrentLocal(rig, true);
            if (L == null) { _vrMsg = "Empty slot: grab the " + VrTargetLabel().ToLowerInvariant() + " with your " + (SideLeft ? "left" : "right") + " hand and press Capture hands"; return; }
            PushUndo();
            if (!change(rig, L)) { _vrUndo.RemoveAt(_vrUndo.Count - 1); return; }
            PutSlot(rig.FromLocal(_vrSlot, L));
            VrApply();
            _vrMsg = done;
        }

        IEnumerable<int> SelectedJoints(VrRig rig)
        {
            for (int role = 1; role <= 5; role++)
            {
                if (_vrFinger != 0 && _vrFinger != role) continue;
                if (_vrFinger == 0 && role == VrRig.Thumb) continue; // "All" means the four fingers
                var f = rig.fingers[role];
                if (f == null) continue;
                foreach (var j in f)
                {
                    if (_vrKnuckle == 0) { yield return j; continue; }
                    var k = rig.knuckle[j];
                    if ((_vrKnuckle == 1 && k == "base") || (_vrKnuckle == 2 && k == "mid") || (_vrKnuckle == 3 && k == "tip")) yield return j;
                }
            }
        }

        void VrRotate(int axis, int sign)
        {
            float step = VrSteps[_vrStepIndex] * sign;
            string[] names = { "Curl", "Spread", "Twist" };
            Edit((rig, L) =>
            {
                bool any = false;
                foreach (var j in new List<int>(SelectedJoints(rig)))
                {
                    // Spread and twist act on single knuckles; a whole-finger spread/twist turns its base only.
                    if (axis != 0 && _vrKnuckle == 0 && rig.knuckle[j] != "base") continue;
                    float amt = axis == 0 && _vrKnuckle == 0 ? step * rig.CurlWeight(j) : step;
                    L.q[j] = L.q[j] * Quaternion.AngleAxis(amt, rig.AxisOf(j, axis));
                    any = true;
                    if (axis == 0 && _vrCoupleTip && _vrKnuckle == 2 && rig.knuckle[j] == "mid" && rig.role[j] != VrRig.Thumb)
                        foreach (var t in rig.fingers[rig.role[j]])
                            if (rig.knuckle[t] == "tip") L.q[t] = L.q[t] * Quaternion.AngleAxis(amt * 0.67f, rig.curlAxis[t]);
                }
                return any;
            }, names[axis] + " " + (sign > 0 ? "+" : "-") + VrSteps[_vrStepIndex] + " on " + SelectionLabel());
        }

        void VrPreset(string name)
        {
            Edit((rig, L) => { ApplyPreset(rig, L, name); return true; }, name + " preset");
        }

        // Sets every finger to a preset (relative to the neutral hand); the hand's position is kept.
        static void ApplyPreset(VrRig rig, VrRig.Local L, string name)
        {
            var p = VrPresets[name];
            for (int role = 1; role <= 5; role++)
            {
                var f = rig.fingers[role];
                if (f == null) continue;
                var curls = role == VrRig.Thumb ? p[1] : (role == VrRig.Index && p[2] != null ? p[2] : p[0]);
                foreach (var j in f)
                {
                    var k = rig.knuckle[j];
                    int idx = k == "base" ? 0 : k == "mid" ? 1 : k == "tip" ? 2 : -1;
                    var q = rig.restQ[j];
                    if (idx >= 0)
                    {
                        if (role == VrRig.Thumb && k == "base") q = q * Quaternion.AngleAxis(p[3][0], rig.spreadAxis[j]);
                        q = q * Quaternion.AngleAxis(curls[idx], rig.curlAxis[j]);
                    }
                    L.q[j] = q;
                }
            }
        }

        void VrMove(Vector3 dir)
        {
            float mm = VrSteps[_vrStepIndex];
            if (OnRetainSlot() && _vrAdjustMag) { PalmedEdit(pm => pm.Pos += dir * (mm / 1000f), "Moved the palmed magazine " + mm + " mm"); return; }
            Edit((rig, L) => { L.rootP += dir * (mm / 1000f); return true; }, "Moved the hand " + mm + " mm");
        }

        void VrTurn(Vector3 axis, int sign)
        {
            float deg = VrSteps[_vrStepIndex] * sign;
            if (OnRetainSlot() && _vrAdjustMag) { PalmedEdit(pm => pm.Euler = (Quaternion.AngleAxis(deg, axis) * Quaternion.Euler(pm.Euler)).eulerAngles, "Turned the palmed magazine " + deg + " deg"); return; }
            Edit((rig, L) =>
            {
                Vector3[] wp; Quaternion[] wq;
                rig.Fk(L.rootP, L.rootQ, L.q, out wp, out wq);
                var pivot = rig.rootKids.Count > 0 ? wp[rig.rootKids[0]] : L.rootP; // the wrist
                var dq = Quaternion.AngleAxis(deg, axis);
                L.rootQ = dq * L.rootQ;
                L.rootP = pivot + dq * (L.rootP - pivot);
                return true;
            }, "Turned the hand " + deg + " deg");
        }

        // On the magazine's Retention slot, Move / Turn / Clear slot can act on the palmed magazine instead of the hand.
        bool _vrAdjustMag;
        bool OnRetainSlot() { return _vrTarget == "mag" && _vrSlot.StartsWith("Retain"); }

        void PalmedEdit(Action<PalmedMag> change, string done)
        {
            if (!VrReady()) return;
            if (_vrSet.magazine == null) { _vrMsg = "This pose set has no magazine section yet: pose the magazine first"; return; }
            var side = SideLeft ? "L" : "R";
            PushUndo();
            var pm = FindPalmed(_vrSet.magazine, side);
            if (pm == null)
            {
                pm = new PalmedMag { side = side };
                var list = new List<PalmedMag>(_vrSet.magazine.palmed ?? new PalmedMag[0]) { pm };
                _vrSet.magazine.palmed = list.ToArray();
            }
            change(pm);
            VrApply();
            var e = pm.Euler; var p = pm.Pos * 1000f;
            _vrMsg = done + string.Format("  (offset {0:0} {1:0} {2:0} mm, turn {3:0} {4:0} {5:0} deg)",
                                          p.x, p.y, p.z, Mathf.DeltaAngle(0, e.x), Mathf.DeltaAngle(0, e.y), Mathf.DeltaAngle(0, e.z));
        }

        void VrReset()
        {
            Edit((rig, L) => { L.q = (Quaternion[])rig.restQ.Clone(); return true; }, "Fingers reset to the neutral hand");
        }

        void VrCopyIdle()
        {
            if (!VrReady()) return;
            var src = "Idle" + (SideLeft ? "L" : "R");
            if (_vrSlot == src) { _vrMsg = "This is the Idle slot"; return; }
            var idle = GetSlot(src);
            if (idle == null) { _vrMsg = "Idle " + (SideLeft ? "L" : "R") + " is empty"; return; }
            PushUndo();
            PutSlot(new PoseData { slot = _vrSlot, joints = (Xf[])idle.joints.Clone() });
            VrApply();
            _vrMsg = "Copied Idle into " + Pretty(_vrSlot);
        }

        void VrMirror()
        {
            if (!VrReady()) return;
            var other = _vrSlot.Substring(0, _vrSlot.Length - 1) + (SideLeft ? "R" : "L");
            var src = GetSlot(other);
            if (src == null) { _vrMsg = Pretty(other) + " is empty, nothing to mirror"; return; }
            PushUndo();
            var pd = new PoseData { slot = _vrSlot, joints = new Xf[src.joints.Length] };
            for (int i = 0; i < src.joints.Length; i++)
            {
                var p = src.joints[i].Pos; var q = src.joints[i].Rot;
                pd.joints[i] = Xf.From(new Vector3(-p.x, p.y, p.z), new Quaternion(q.x, -q.y, -q.z, q.w));
            }
            PutSlot(pd);
            VrApply();
            _vrMsg = "Mirrored " + Pretty(other) + " into " + Pretty(_vrSlot) + " (check finger twist)";
        }

        void VrClear()
        {
            if (!VrReady()) return;
            if (OnRetainSlot() && _vrAdjustMag)
            {
                var side = SideLeft ? "L" : "R";
                if (FindPalmed(_vrSet.magazine, side) == null) { _vrMsg = "The palmed magazine already sits where H3VR puts it"; return; }
                PushUndo();
                _vrSet.magazine.palmed = System.Array.FindAll(_vrSet.magazine.palmed, p => p != null && p.side != side);
                VrApply();
                _vrMsg = "The palmed magazine is back where H3VR puts it";
                return;
            }
            if (GetSlot(_vrSlot) == null) { _vrMsg = Pretty(_vrSlot) + " is already empty"; return; }
            PushUndo();
            RemoveSlot(_vrSlot);
            VrApply();
            _vrMsg = "Cleared " + Pretty(_vrSlot) + ". Grip naturally, then Capture hands";
        }

        // Capture every hand that's on this gun (or a magazine), each into the place it's holding:
        // grip -> Gun, pump -> Pump, slide/bolt -> that part, magazine -> Magazine. The slot's side doesn't matter.
        void VrCapture()
        {
            if (!VrReady()) return;
            if (_vrTarget == "empty")
            {
                // Nothing to grip: start both hands from GlovesTan3's own idle pose.
                PushUndo();
                int n = 0;
                foreach (var left in new[] { false, true })
                {
                    var pd = EmptyCapture(left, left ? "IdleL" : "IdleR");
                    if (pd != null) { PutSlot(pd); n++; }
                }
                if (n == 0) { _vrUndo.RemoveAt(_vrUndo.Count - 1); _vrMsg = "Spawn the GlovesTan3 gloves first"; return; }
                VrApply();
                _vrMsg = "Both empty hands set to GlovesTan3's idle pose: now shape them";
                return;
            }
            PushUndo();
            var done = new List<string>();
            string bestTarget = null, bestSlot = null;
            foreach (var left in new[] { false, true })
            {
                string target, slot, why;
                var pd = CaptureFor(left, out target, out slot, out why);
                if (pd == null) continue;
                var saved = _vrTarget;
                _vrTarget = target;
                PutSlot(pd);
                _vrTarget = saved;
                done.Add((left ? "left" : "right") + " > " + LabelOf(target) + " / " + Pretty(slot));
                // Focus the most specific capture (a pump, part or magazine beats the gun body).
                if (bestTarget == null || bestTarget == "gun") { bestTarget = target; bestSlot = slot; }
            }
            if (done.Count == 0)
            {
                _vrUndo.RemoveAt(_vrUndo.Count - 1);
                _vrMsg = "Grip " + (_vrSet != null ? _vrSet.name : "the gun") + " (grip, pump, slide, bolt or a magazine) first, then press Capture hands";
                return;
            }
            _vrTarget = bestTarget;
            _vrSlot = bestSlot;
            VrApply();
            _vrMsg = "Captured " + string.Join(", ", done.ToArray());
        }

        // One hand's capture, already converted into the space of whatever it holds. Null when the hand
        // holds nothing of this gun (why says so).
        PoseData CaptureFor(bool left, out string target, out string slot, out string why)
        {
            target = null; slot = null; why = null;
            var hand = FindHand(left);
            if (hand == null) { why = "no hand"; return null; }
            string error;
            var cap = BuildCapture(left, out error); // an empty hand next to the gun captures a support grip
            if (cap == null) { why = error; return null; }
            var held = cap.source;
            string side = left ? "L" : "R";
            if (cap.isMagazine) target = "mag";
            else if (held != _vrGun) { why = "holding something else"; return null; }
            else if (cap.isPump) target = "pump";
            else if (!string.IsNullOrEmpty(cap.partPath)) target = "part:" + cap.partPath;
            else target = "gun";

            if (target == "mag")
                // Holding two palmed magazines, or editing the Retention slot: capture into Retain.
                slot = (HasPalmedPartner(held as FVRFireArmMagazine) || (_vrTarget == "mag" && _vrSlot == "Retain" + side)) ? "Retain" + side : "Idle" + side;
            else if (target != "gun") slot = "Idle" + side;
            else if (cap.isSupport) slot = "Fore" + side; // free hand supporting a pistol: the support grip
            else if (cap.onForegrip) slot = GunHasPump() ? null : "Fore" + side;
            else if (_vrTarget == "gun" && _vrSlot.EndsWith(side) && !_vrSlot.StartsWith("Fore")) slot = _vrSlot; // keep e.g. Fire start
            else slot = "Idle" + side;
            if (slot == null) { target = "pump"; slot = "Idle" + side; } // pump-action: a foregrip hold is the pump

            var joints = cap.joints;
            if ((target.StartsWith("part:") || target == "pump") && cap.partXf != null)
            {
                var fp = cap.partXf.Pos; var fq = Quaternion.Inverse(cap.partXf.Rot);
                joints = new Xf[cap.joints.Length];
                for (int i = 0; i < joints.Length; i++)
                    joints[i] = Xf.From(fq * (cap.joints[i].Pos - fp), fq * cap.joints[i].Rot);
            }
            return new PoseData { slot = slot, joints = joints };
        }

        bool GunHasPump() { return _vrGun != null && FindPump(_vrGun.transform) != null; }

        // Capture for the current slot only (used when editing an empty slot).
        PoseData CaptureToTarget()
        {
            if (_vrTarget == "empty") return EmptyCapture(SideLeft, _vrSlot);
            string target, slot, why;
            var pd = CaptureFor(SideLeft, out target, out slot, out why);
            if (pd == null || target != _vrTarget) { _vrMsg = "Grip the " + VrTargetLabel().ToLowerInvariant() + " with your " + (SideLeft ? "left" : "right") + " hand, or press Capture hands"; return null; }
            pd.slot = _vrSlot;
            return pd;
        }

        // Undo entries are tagged: "E" = the empty-hand poses, "G" = the gun's pose set.
        void PushUndo()
        {
            _vrUndo.Add(_vrTarget == "empty" ? "E" + JsonUtility.ToJson(_empty) : "G" + JsonUtility.ToJson(_vrSet));
            if (_vrUndo.Count > 60) _vrUndo.RemoveAt(0);
        }

        void VrUndoLast()
        {
            if (_vrUndo.Count == 0) { _vrMsg = "Nothing to undo"; return; }
            var entry = _vrUndo[_vrUndo.Count - 1];
            _vrUndo.RemoveAt(_vrUndo.Count - 1);
            if (entry.StartsWith("E"))
            {
                _empty = JsonUtility.FromJson<EmptyHandSet>(entry.Substring(1));
                _vrTarget = "empty";
                if (Array.IndexOf(VrSlots(), _vrSlot) < 0) _vrSlot = "Idle" + (SideLeft ? "L" : "R");
            }
            else
            {
                _vrSet = JsonUtility.FromJson<PoseSet>(entry.Substring(1));
                if (_vrTarget == "empty") _vrTarget = "gun";
            }
            VrApply();
            _vrMsg = "Undone";
        }

        // Push the session to the game (no file write), exactly like the browser editor's Live sync.
        // The empty-hand pose is put on the gloves every frame, so it only needs marking as changed.
        void VrApply()
        {
            if (_vrTarget == "empty") { _emptyDirty = true; return; }
            _vrDirty = true;
            try { ApplyLive(_vrSet, false); }
            catch (Exception e) { _vrMsg = "Could not apply: " + e.Message; }
        }

        void VrSave()
        {
            var saved = new List<string>();
            if (_emptyDirty || _vrTarget == "empty")
            {
                try { SaveEmptyHand(); saved.Add("empty-hand.json"); }
                catch (Exception e) { _vrMsg = "Could not save: " + e.Message; return; }
            }
            if (_vrSet != null && (_vrDirty || _vrTarget != "empty"))
            {
                try { ApplyLive(_vrSet, true); _vrDirty = false; saved.Add(PoseFileName(_vrSet.name)); }
                catch (Exception e) { _vrMsg = "Could not save: " + e.Message; return; }
            }
            _vrMsg = saved.Count > 0 ? "Saved " + string.Join(" and ", saved.ToArray()) : "Hold a gun, then press Use held gun";
        }

        void VrRevert()
        {
            if (_vrTarget == "empty")
            {
                LoadEmptyHand();
                _vrUndo.Clear();
                _vrMsg = File.Exists(EmptyHandPath) ? "Empty hand reverted to the saved file" : "No saved empty-hand pose: back to GlovesTan3's own";
                return;
            }
            if (_vrGun == null) { _vrMsg = "Nothing to revert"; return; }
            var gun = _vrGun;
            var keepEmpty = _empty; bool keepEmptyDirty = _emptyDirty;
            Reload(); // re-read the saved files, dropping unsaved live changes
            _empty = keepEmpty; _emptyDirty = keepEmptyDirty; // the gun's revert leaves the empty hand alone
            _vrGun = null;
            _vrDirty = false;
            _vrGun = gun;
            var existing = FindSet(gun);
            if (existing != null) _vrSet = JsonUtility.FromJson<PoseSet>(JsonUtility.ToJson(existing));
            _vrUndo.Clear();
            _vrMsg = existing != null ? "Reverted to the saved file" : "No saved file: unsaved changes dropped";
        }

        // Give these poses to every firearm with a very similar name (same rule as the browser editor).
        void VrAutoCopy()
        {
            if (_vrSet == null) { _vrMsg = "Hold a gun, then press Use held gun"; return; }
            var ids = new List<string>(_vrSet.match.itemIds ?? new string[0]);
            if (ids.Count == 0) { _vrMsg = "This pose set has no item ID"; return; }
            var added = new List<string>();
            if (IM.OD != null)
                foreach (var kv in IM.OD)
                {
                    var o = kv.Value;
                    if (o == null || o.Category != FVRObject.ObjectCategory.Firearm || ids.Contains(kv.Key)) continue;
                    float best = 0;
                    foreach (var id in ids)
                    {
                        float s = Similarity.Score(kv.Key, id);
                        var other = DisplayNameOf(id);
                        if (!string.IsNullOrEmpty(o.DisplayName) && !string.IsNullOrEmpty(other)) s = Math.Max(s, Similarity.Score(o.DisplayName, other));
                        best = Math.Max(best, s);
                    }
                    if (best >= Similarity.DefaultThreshold) added.Add(kv.Key);
                }
            if (added.Count == 0) { _vrMsg = "No guns with a similar enough name"; return; }
            PushUndo();
            ids.AddRange(added);
            _vrSet.match.itemIds = ids.ToArray();
            VrApply();
            _vrMsg = "Poses copied to " + added.Count + " gun(s): " + string.Join(", ", added.GetRange(0, Math.Min(4, added.Count)).ToArray()) + (added.Count > 4 ? "..." : "") + ". Save to keep";
        }

        // ------------------------------------------------------------------ labels

        static string Pretty(string slot)
        {
            var side = slot.EndsWith("L") ? " L" : " R";
            var k = slot.Substring(0, slot.Length - 1);
            return (k == "Fore" ? "Fore/Support" : k == "FireStart" ? "Fire start" : k == "FireEnd" ? "Fire end" : k == "MagRelease" ? "Mag release"
                    : k == "Retain" ? "Retention" : k == "ThumbsUp" ? "Thumbs up" : k == "FingerGun" ? "Finger gun" : k) + side;
        }

        string VrTargetLabel() { return LabelOf(_vrTarget); }

        static string LabelOf(string target)
        {
            if (target == "gun") return "Gun";
            if (target == "mag") return "Magazine";
            if (target == "pump") return "Pump";
            if (target == "empty") return "Empty hand";
            var path = target.Substring(5);
            int slash = path.LastIndexOf('/');
            return (slash >= 0 ? path.Substring(slash + 1) : path).TrimStart('_');
        }

        string SelectionLabel()
        {
            string[] kn = { "", " base", " mid", " tip" };
            return (_vrFinger == 0 ? "all fingers" : VrRig.RoleNames[_vrFinger].ToLowerInvariant()) + kn[_vrKnuckle];
        }

        void VrRefresh()
        {
            _vrFocusTime = Time.time; // the VR editor was just used: ghost hands follow it
            Text t;
            if (_vrLabels.TryGetValue("target", out t)) t.text = VrTargetLabel();
            if (_vrLabels.TryGetValue("slot", out t))
            {
                bool set = (_vrSet != null || _vrTarget == "empty") && GetSlot(_vrSlot) != null;
                t.text = Pretty(_vrSlot) + (set ? " *" : "");
            }
            VrPaint paint;
            for (int f = 0; f <= 5; f++)
            {
                if (_vrLabels.TryGetValue("finger" + f, out t)) t.text = (f == _vrFinger ? "> " : "") + VrRig.RoleNames[f];
                if (_vrPaints.TryGetValue("finger" + f, out paint)) paint.Apply(f == _vrFinger);
            }
            string[] kn = { "Whole", "Base", "Mid", "Tip" };
            for (int k = 0; k < 4; k++)
            {
                if (_vrLabels.TryGetValue("knuckle" + k, out t)) t.text = (k == _vrKnuckle ? "> " : "") + kn[k];
                if (_vrPaints.TryGetValue("knuckle" + k, out paint)) paint.Apply(k == _vrKnuckle);
            }
            if (_vrPaints.TryGetValue("couple", out paint)) paint.Apply(_vrCoupleTip);
            if (_vrLabels.TryGetValue("step", out t)) t.text = "Step " + VrSteps[_vrStepIndex] + (VrSteps[_vrStepIndex] == 1 ? "" : "") + " deg/mm";
            if (_vrLabels.TryGetValue("couple", out t)) t.text = "Tip follows: " + (_vrCoupleTip ? "on" : "off");
            if (_vrLabels.TryGetValue("save", out t)) t.text = _vrDirty || _emptyDirty ? "Save *" : "Save";
            if (_vrLabels.TryGetValue("freeze", out t)) t.text = _freezeOn ? "Freeze gun: on" : "Freeze gun: off";
            if (_vrLabels.TryGetValue("copyidle", out t)) t.text = OnRetainSlot() ? (_vrAdjustMag ? "Adjust: palmed mag" : "Adjust: hand") : "Copy Idle";
            if (_vrPaints.TryGetValue("copyidle", out paint)) paint.Apply(OnRetainSlot() && _vrAdjustMag);

            if (_vrStatus != null)
            {
                string gun = _vrSet != null ? _vrSet.name + (_vrSet.match != null && _vrSet.match.itemIds != null && _vrSet.match.itemIds.Length > 1 ? " (+" + (_vrSet.match.itemIds.Length - 1) + " guns)" : "") : "no gun";
                _vrStatus.text = "PoseKit  |  " + gun + "  |  " + VrTargetLabel() + " / " + Pretty(_vrSlot)
                                 + (_vrTarget == "empty"
                                     ? (GetSlot(_vrSlot) == null ? (_vrSlot.StartsWith("Idle") ? " (GlovesTan3's own)" : " (built-in default)") : "") + (_emptyEnabled.Value ? "" : " (turned off in config)")
                                     : (_vrSet != null && GetSlot(_vrSlot) == null ? " (empty)" : ""))
                                 + (_vrDirty || _emptyDirty ? "  |  unsaved" : "")
                                 + "\n" + (string.IsNullOrEmpty(_vrMsg) ? "Hold the gun with the slot's hand and press buttons with the other." : _vrMsg);
            }
        }
    }
}
