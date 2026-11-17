using System.Collections.Generic;
using FistVR;
using JerryGloves;
using UnityEngine;

namespace GT3PoseKit
{
    // Mag release: when the magazine leaves a held gun by the button (not pulled out by the other hand),
    // the holding hand briefly blends into its "MagRelease" pose (thumb on the release) and back.
    // GlovesTan3 has no working slot for this, so the blend is written into the gun's Idle / Fire start /
    // Fire end poses for that side (whichever GlovesTan3 is showing) and restored afterwards.
    public partial class PoseKitPlugin
    {
        const float MagBlendIn = 0.08f, MagHold = 0.22f, MagBlendOut = 0.18f;

        class MagWatch
        {
            public FVRFireArm gun;
            public FVRFireArmMagazine last;
            public float start = -1f;
            public bool left;
            public int preview;   // 0 none, 1 left, 2 right: VR editor is showing the Mag release slot
        }

        readonly Dictionary<int, MagWatch> _magWatch = new Dictionary<int, MagWatch>();
        float _magWatchCleanup;

        static PoseData FindPose(PoseData[] arr, string slot)
        {
            if (arr != null) foreach (var p in arr) if (p != null && p.slot == slot && p.joints != null && p.joints.Length > 0) return p;
            return null;
        }

        void UpdateMagRelease()
        {
            var mm = GM.CurrentMovementManager;
            if (mm == null || mm.Hands == null || mm.Hands.Length < 2) return;

            foreach (var e in _injected)
            {
                if (e.target != "gun" || e.hpi == null || e.parent == null || e.set == null) continue;
                int key = e.hpi.GetInstanceID();
                MagWatch w;
                if (!_magWatch.TryGetValue(key, out w))
                {
                    var fa0 = e.parent.GetComponentInParent<FVRFireArm>();
                    if (fa0 == null) continue;
                    w = new MagWatch { gun = fa0, last = fa0.Magazine };
                    _magWatch[key] = w;
                }
                var fa = w.gun;
                if (fa == null) continue;

                var mag = fa.Magazine;
                // Ejected by the button: the magazine left while the gun is held and no hand is holding the magazine.
                if (w.last != null && mag == null && fa.IsHeld && fa.m_hand != null && !w.last.IsHeld)
                {
                    bool left = fa.m_hand == mm.Hands[0]; // GlovesTan3's glove-to-hand mapping
                    if (FindPose(e.set.poses, "MagRelease" + (left ? "L" : "R")) != null)
                    {
                        if (w.start >= 0f && w.left != left) ApplyMagBlend(e, w.left, 0f);
                        w.start = Time.time;
                        w.left = left;
                    }
                }
                w.last = mag;

                // While the VR editor has this gun's Mag release slot selected, hold the pose so you can see it.
                int preview = 0;
                if (_vrPanel != null && _vrPanel.activeInHierarchy && _vrGun == fa && _vrTarget == "gun" && _vrSlot.StartsWith("MagRelease")
                    && FindPose(e.set.poses, _vrSlot) != null)
                    preview = _vrSlot.EndsWith("L") ? 1 : 2;
                if (preview != w.preview)
                {
                    if (w.preview != 0) ApplyMagBlend(e, w.preview == 1, 0f);
                    w.preview = preview;
                }
                if (preview != 0) { ApplyMagBlend(e, preview == 1, 1f); continue; }

                if (w.start >= 0f)
                {
                    float t = Time.time - w.start, total = MagBlendIn + MagHold + MagBlendOut;
                    float weight = t < MagBlendIn ? t / MagBlendIn
                                 : t < MagBlendIn + MagHold ? 1f
                                 : t < total ? 1f - (t - MagBlendIn - MagHold) / MagBlendOut
                                 : 0f;
                    ApplyMagBlend(e, w.left, Mathf.SmoothStep(0f, 1f, weight));
                    if (t >= total) w.start = -1f;
                }
            }

            // Forget guns that no longer exist.
            if (Time.time > _magWatchCleanup)
            {
                _magWatchCleanup = Time.time + 5f;
                var dead = new List<int>();
                foreach (var kv in _magWatch) if (kv.Value.gun == null) dead.Add(kv.Key);
                foreach (var k in dead) _magWatch.Remove(k);
            }
        }

        // Blend this side's showing poses toward MagRelease by weight (0 restores the saved poses exactly).
        void ApplyMagBlend(Injected e, bool left, float weight)
        {
            string side = left ? "L" : "R";
            var mr = FindPose(e.set.poses, "MagRelease" + side);
            if (mr == null) return;
            var data = GunPoses(e.set, e.frame);
            foreach (var kind in new[] { "Idle", "FireStart", "FireEnd" })
            {
                var hp = GetSlot(e.hpi, kind + side);
                var pd = FindPose(data, kind + side);
                if (hp == null || hp.Fingers == null || pd == null) continue;
                int n = Mathf.Min(hp.Fingers.Length, Mathf.Min(pd.joints.Length, mr.joints.Length));
                for (int i = 0; i < n; i++)
                {
                    if (hp.Fingers[i] == null) continue;
                    var t = hp.Fingers[i].transform;
                    t.localPosition = Vector3.Lerp(pd.joints[i].Pos, mr.joints[i].Pos, weight);
                    t.localRotation = Quaternion.Slerp(pd.joints[i].Rot, mr.joints[i].Rot, weight);
                }
            }
        }
    }
}
