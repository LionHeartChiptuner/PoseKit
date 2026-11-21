using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using FistVR;
using HarmonyLib;
using JerryGloves;
using UnityEngine;

namespace GT3PoseKit
{
    // Empty hand: your own pose for the glove when it holds nothing. GlovesTan3 copies its built-in idle pose
    // (HandsPoseSystem.idlePose) onto the glove every frame while the hand is empty, so PoseKit re-poses that idle
    // pose. Joint transforms are relative to the idle pose's own transform (which follows the controller), with
    // its scale removed, so the wrist can be moved and turned too. Stored in empty-hand.json.
    [Serializable]
    public class EmptyHandSet
    {
        public string format = "gt3empty";
        public int version = 1;
        public PoseData[] poses = new PoseData[0];   // IdleL / IdleR
    }

    // While a hand holds nothing, GlovesTan3 shows its physics hand (HandPhys: hinge-jointed fingers whose motors
    // curl and open with your grip input), not the idle pose; the idle pose only drives the animated hand (HandAnim)
    // seen while blending out of a grab. With a custom empty-hand pose, this shows the animated hand posed as the
    // idle pose instead. GlovesTan3's UpdatePhysicalFingers then moves the physics fingers (kinematic) onto that same
    // pose, so they still push things. Everything else (grabbing, the release blend, support grips) is untouched.
    [HarmonyPatch(typeof(HandsPoseSystem), "UpdateOneHand")]
    static class EmptyHandPatch
    {
        public static PoseKitPlugin Plugin;
        static readonly FieldInfo LockedHpi = AccessTools.Field(typeof(HandsPoseSystem), "lockedHpi");
        static readonly FieldInfo ReleaseTimer = AccessTools.Field(typeof(HandsPoseSystem), "releaseTimer");

        static bool Prefix(HandsPoseSystem __instance, FVRViveHand __0)
        {
            try
            {
                var sys = __instance;
                if (Plugin == null || sys == null || __0 == null || !Plugin.EmptyPoseActive(sys)) return true;
                if (__0.CurrentInteractable != null || Plugin.SupportShowing(sys.isLeft)) return true;
                if (LockedHpi == null || LockedHpi.GetValue(sys) != null) return true;
                if (ReleaseTimer != null && (float)ReleaseTimer.GetValue(sys) > 0f) return true; // let the release blend finish
                if (sys.HandPhys == null || sys.HandAnim == null || sys.Fingers == null) return true;

                if (sys.HandPhys.activeSelf) sys.HandPhys.SetActive(false);
                if (!sys.HandAnim.activeSelf) sys.HandAnim.SetActive(true);
                if (sys.HP != null) sys.HP.HasPose = false;
                var idle = sys.idlePose.Fingers;
                int n = Mathf.Min(sys.Fingers.Length, idle.Length);
                for (int i = 0; i < n; i++)
                {
                    if (sys.Fingers[i] == null || idle[i] == null) continue;
                    sys.Fingers[i].transform.rotation = idle[i].transform.rotation;
                    sys.Fingers[i].transform.position = idle[i].transform.position;
                }
                return false;
            }
            catch { return true; } // never break GlovesTan3's own hand
        }
    }

    public partial class PoseKitPlugin
    {
        // A custom empty-hand pose is on this glove (set, enabled, and the right joint count).
        internal bool EmptyPoseActive(HandsPoseSystem sys)
        {
            if (_emptyEnabled == null || !_emptyEnabled.Value || sys.idlePose == null || sys.idlePose.Fingers == null) return false;
            var pd = EmptyPose(sys.isLeft);
            return pd != null && pd.joints.Length == sys.idlePose.Fingers.Length;
        }

        ConfigEntry<bool> _emptyEnabled;
        EmptyHandSet _empty = new EmptyHandSet();
        bool _emptyDirty;

        // GlovesTan3's own idle pose, per glove, so it can be restored and used as the editor's neutral hand.
        class IdleOriginal { public Vector3[] p; public Quaternion[] q; public bool applied; }
        readonly Dictionary<HandsPoseSystem, IdleOriginal> _idleOriginals = new Dictionary<HandsPoseSystem, IdleOriginal>();

        string EmptyHandPath { get { return Path.Combine(_dir, "empty-hand.json"); } }

        void BindEmptyConfig()
        {
            _emptyEnabled = Config.Bind("Empty hand", "Enabled", true, "Use your own empty-hand pose (made in the VR editor under the Empty hand target, saved in empty-hand.json) instead of GlovesTan3's idle pose. Off = GlovesTan3's own.");
        }

        void LoadEmptyHand()
        {
            _empty = new EmptyHandSet();
            _emptyDirty = false;
            try
            {
                if (File.Exists(EmptyHandPath))
                {
                    var s = JsonUtility.FromJson<EmptyHandSet>(File.ReadAllText(EmptyHandPath));
                    if (s != null && s.format == "gt3empty") { if (s.poses == null) s.poses = new PoseData[0]; _empty = s; }
                }
            }
            catch (Exception e) { Logger.LogError("Could not read empty-hand.json: " + e.Message); }
        }

        void SaveEmptyHand()
        {
            File.WriteAllText(EmptyHandPath, JsonUtility.ToJson(_empty, true));
            _emptyDirty = false;
        }

        // ------------------------------------------------------------------ gestures

        // Empty-hand poses, picked by what your hand does on the controller (most specific first):
        //   Idle       nothing pressed
        //   Grip       grip + finger on the trigger + thumb resting (a fist)
        //   Point      grip, finger off the trigger, thumb resting
        //   ThumbsUp   grip + finger on the trigger, thumb lifted off the stick / pad / buttons
        //   FingerGun  grip, finger off the trigger, thumb lifted
        // A gesture you haven't posed uses a built-in default made from your Idle pose.
        internal static readonly string[] EmptyGestures = { "Idle", "Grip", "Point", "ThumbsUp", "FingerGun" };
        internal static readonly string[] EmptySlots = { "IdleL", "IdleR", "GripL", "GripR", "PointL", "PointR", "ThumbsUpL", "ThumbsUpR", "FingerGunL", "FingerGunR" };
        static readonly Dictionary<string, string> GesturePreset = new Dictionary<string, string>
        {
            { "Grip", "Fist" }, { "Point", "Point" }, { "ThumbsUp", "Thumbs up" }, { "FingerGun", "Finger gun" },
        };

        class EmptyLive { public Xf[] shown; public string gesture; }
        readonly Dictionary<bool, EmptyLive> _emptyLive = new Dictionary<bool, EmptyLive>();
        readonly Dictionary<string, PoseData> _gestureDefaults = new Dictionary<string, PoseData>();
        string _gestureDefaultsKey;
        const float GestureBlendTime = 0.08f;

        PoseData EmptySlot(string slot)
        {
            if (_empty == null || _empty.poses == null) return null;
            foreach (var p in _empty.poses) if (p != null && p.slot == slot && p.joints != null && p.joints.Length > 0) return p;
            return null;
        }

        // Any empty-hand pose for this side turns the feature on for that hand.
        PoseData EmptyPose(bool left)
        {
            string s = left ? "L" : "R";
            foreach (var g in EmptyGestures) { var p = EmptySlot(g + s); if (p != null) return p; }
            return null;
        }

        // The pose a gesture slot shows: yours, else the default built from your Idle (or GlovesTan3's).
        internal PoseData EmptyGesturePose(string slot)
        {
            var own = EmptySlot(slot);
            if (own != null) return own;
            bool left = slot.EndsWith("L");
            string side = left ? "L" : "R";
            var idle = EmptySlot("Idle" + side) ?? EmptyCapture(left, "Idle" + side);
            string gesture = slot.Substring(0, slot.Length - 1);
            if (gesture == "Idle" || idle == null) return idle;

            string key = JsonUtility.ToJson(_empty);
            if (key != _gestureDefaultsKey) { _gestureDefaults.Clear(); _gestureDefaultsKey = key; }
            PoseData pd;
            if (_gestureDefaults.TryGetValue(slot, out pd)) return pd;
            var rig = Rig(left);
            string preset;
            if (rig == null || idle.joints.Length != rig.n || !GesturePreset.TryGetValue(gesture, out preset)) return idle;
            var L = rig.ToLocal(idle);
            ApplyPreset(rig, L, preset);
            pd = rig.FromLocal(slot, L);
            _gestureDefaults[slot] = pd;
            return pd;
        }

        static string GestureFor(FVRViveHand hand)
        {
            var i = hand.Input;
            bool gripped = i.GripPressed || (i.FingerCurl_Middle > 0.65f && i.FingerCurl_Ring > 0.65f && i.FingerCurl_Pinky > 0.5f);
            if (!gripped) return "Idle";
            bool onTrigger = i.TriggerTouched || i.TriggerFloat > 0.1f || i.FingerCurl_Index > 0.6f;
            bool thumbDown = i.TouchpadTouched || i.Secondary2AxisInputTouched || i.AXButtonPressed || i.BYButtonPressed
                             || i.TouchpadPressed || i.FingerCurl_Thumb > 0.5f;
            if (onTrigger) return thumbDown ? "Grip" : "ThumbsUp";
            return thumbDown ? "Point" : "FingerGun";
        }

        // Called every LateUpdate: puts the right empty-hand pose (blended) on each glove's idle pose, or
        // GlovesTan3's own back.
        void UpdateEmptyHands()
        {
            if (_systems == null) return;
            var mm = GM.CurrentMovementManager;
            foreach (var sys in _systems)
            {
                if (sys == null || sys.idlePose == null || sys.idlePose.Fingers == null) continue;
                IdleOriginal orig;
                _idleOriginals.TryGetValue(sys, out orig);
                if (!EmptyPoseActive(sys))
                {
                    if (orig != null && orig.applied) RestoreIdle(sys, orig);
                    _emptyLive.Remove(sys.isLeft);
                    continue;
                }
                bool left = sys.isLeft;
                string side = left ? "L" : "R";
                // While you edit an empty-hand slot, that hand previews it whatever your fingers do.
                string gesture;
                if (_vrPanel != null && _vrPanel.activeInHierarchy && _vrTarget == "empty" && _vrSlot.EndsWith(side))
                    gesture = _vrSlot.Substring(0, _vrSlot.Length - 1);
                else
                {
                    var hand = mm != null && mm.Hands != null && mm.Hands.Length > 1 ? mm.Hands[left ? 0 : 1] : null;
                    gesture = hand != null ? GestureFor(hand) : "Idle";
                }
                var target = EmptyGesturePose(gesture + side);
                if (target == null || target.joints.Length != sys.idlePose.Fingers.Length) continue;

                EmptyLive live;
                if (!_emptyLive.TryGetValue(left, out live) || live.shown == null || live.shown.Length != target.joints.Length)
                {
                    live = new EmptyLive { shown = (Xf[])target.joints.Clone(), gesture = gesture };
                    _emptyLive[left] = live;
                }
                float t = Mathf.Clamp01(Time.deltaTime / GestureBlendTime);
                var shown = new Xf[target.joints.Length];
                for (int j = 0; j < shown.Length; j++)
                    shown[j] = Xf.From(Vector3.Lerp(live.shown[j].Pos, target.joints[j].Pos, t), Quaternion.Slerp(live.shown[j].Rot, target.joints[j].Rot, t));
                live.shown = shown;
                live.gesture = gesture;

                if (orig == null) { orig = SnapshotIdle(sys); _idleOriginals[sys] = orig; }
                PoseIdle(sys, new PoseData { slot = gesture + side, joints = shown });
                orig.applied = true;
            }
        }

        static IdleOriginal SnapshotIdle(HandsPoseSystem sys)
        {
            var f = sys.idlePose.Fingers;
            var o = new IdleOriginal { p = new Vector3[f.Length], q = new Quaternion[f.Length] };
            for (int i = 0; i < f.Length; i++)
                if (f[i] != null) { o.p[i] = f[i].transform.localPosition; o.q[i] = f[i].transform.localRotation; }
            return o;
        }

        static void RestoreIdle(HandsPoseSystem sys, IdleOriginal o)
        {
            var f = sys.idlePose.Fingers;
            for (int i = 0; i < f.Length && i < o.p.Length; i++)
                if (f[i] != null) { f[i].transform.localPosition = o.p[i]; f[i].transform.localRotation = o.q[i]; }
            o.applied = false;
        }

        static void PoseIdle(HandsPoseSystem sys, PoseData pd)
        {
            var f = sys.idlePose.Fingers;
            var frame = sys.idlePose.transform;
            var wp = new Vector3[f.Length];
            var wq = new Quaternion[f.Length];
            for (int i = 0; i < f.Length; i++)
            {
                wp[i] = frame.position + frame.rotation * pd.joints[i].Pos;
                wq[i] = frame.rotation * pd.joints[i].Rot;
            }
            // Parents before children, so moving a parent doesn't drag an already placed child.
            var order = new List<int>();
            for (int i = 0; i < f.Length; i++) if (f[i] != null) order.Add(i);
            order.Sort((a, b) => Depth(f[a].transform).CompareTo(Depth(f[b].transform)));
            foreach (var i in order) { f[i].transform.position = wp[i]; f[i].transform.rotation = wq[i]; }
        }

        static int Depth(Transform t) { int d = 0; while (t.parent != null) { t = t.parent; d++; } return d; }

        // GlovesTan3's own idle pose in empty-hand form (the starting point for a new empty-hand pose).
        PoseData EmptyCapture(bool left, string slot)
        {
            var sys = FindSystem(left);
            if (sys == null || sys.idlePose == null || sys.idlePose.Fingers == null) return null;
            PoseData pd = null;
            WithOriginalIdle(sys, () =>
            {
                var f = sys.idlePose.Fingers;
                var frame = sys.idlePose.transform;
                var inv = Quaternion.Inverse(frame.rotation);
                pd = new PoseData { slot = slot, joints = new Xf[f.Length] };
                for (int i = 0; i < f.Length; i++)
                {
                    if (f[i] == null) { pd.joints[i] = new Xf(); continue; }
                    pd.joints[i] = Xf.From(inv * (f[i].transform.position - frame.position), inv * f[i].transform.rotation);
                }
            });
            return pd;
        }

        // Runs `a` with GlovesTan3's original idle pose in place (it's put back as it was afterwards).
        void WithOriginalIdle(HandsPoseSystem sys, Action a)
        {
            IdleOriginal orig;
            if (sys == null || sys.idlePose == null || !_idleOriginals.TryGetValue(sys, out orig) || !orig.applied) { a(); return; }
            var f = sys.idlePose.Fingers;
            var keepP = new Vector3[f.Length]; var keepQ = new Quaternion[f.Length];
            for (int i = 0; i < f.Length; i++) if (f[i] != null) { keepP[i] = f[i].transform.localPosition; keepQ[i] = f[i].transform.localRotation; }
            RestoreIdle(sys, orig);
            try { a(); }
            finally
            {
                for (int i = 0; i < f.Length; i++) if (f[i] != null) { f[i].transform.localPosition = keepP[i]; f[i].transform.localRotation = keepQ[i]; }
                orig.applied = true;
            }
        }
    }
}
