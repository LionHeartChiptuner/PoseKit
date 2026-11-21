using System.Collections.Generic;
using FistVR;
using JerryGloves;
using UnityEngine;

namespace GT3PoseKit
{
    // Retention reload: with H3VR's mag palming on, grabbing a second magazine with the hand that already holds one
    // stacks the two (FVRFireArmMagazine m_magChild / m_magParent). While the held magazine has a palmed partner,
    // the hand blends into the magazine's "Retain" pose. GlovesTan3 has no slot for this, so the blend is written
    // into the magazine's Idle / Fore poses for that side and restored when the partner is gone.
    public partial class PoseKitPlugin
    {
        const float RetainBlendTime = 0.1f;

        class RetainWatch
        {
            public FVRFireArmMagazine mag;
            public float weight;
            public bool left;
        }

        readonly Dictionary<int, RetainWatch> _retainWatch = new Dictionary<int, RetainWatch>();
        float _retainWatchCleanup;

        static bool HasPalmedPartner(FVRFireArmMagazine mag)
        {
            return mag != null && (mag.GetMagChild() != null || mag.GetMagParent() != null);
        }

        void UpdateRetention()
        {
            var mm = GM.CurrentMovementManager;
            if (mm == null || mm.Hands == null || mm.Hands.Length < 2) return;

            foreach (var e in _injected)
            {
                if (e.target != "mag" || e.hpi == null || e.parent == null || e.set == null || e.set.magazine == null) continue;
                int key = e.hpi.GetInstanceID();
                RetainWatch w;
                if (!_retainWatch.TryGetValue(key, out w))
                {
                    var m = e.parent.GetComponentInParent<FVRFireArmMagazine>();
                    if (m == null) continue;
                    w = new RetainWatch { mag = m };
                    _retainWatch[key] = w;
                }
                var mag = w.mag;
                if (mag == null) continue;

                bool held = mag.IsHeld && mag.m_hand != null;
                bool left = held && mag.m_hand == mm.Hands[0]; // GlovesTan3's glove-to-hand mapping
                string side = left ? "L" : "R";

                // While the VR editor has this magazine's Retention slot selected, hold the pose so you can see it.
                bool preview = held && _vrPanel != null && _vrPanel.activeInHierarchy && _vrTarget == "mag"
                               && _vrSlot == "Retain" + side && _vrSet != null && SetKey(_vrSet) == SetKey(e.set);

                bool want = held && (preview || HasPalmedPartner(mag))
                            && FindPose(e.set.magazine.poses, "Retain" + side) != null;

                // Hand changed while blended: put the old side back first.
                if (w.weight > 0f && held && w.left != left) { ApplyRetainBlend(e, w.left, 0f); w.weight = 0f; }
                if (held) w.left = left;

                float before = w.weight;
                w.weight = preview ? 1f : Mathf.MoveTowards(w.weight, want ? 1f : 0f, Time.deltaTime / RetainBlendTime);
                // Write while blending, and once more on reaching 0 so the saved poses are restored exactly.
                if (w.weight > 0f || before > 0f) ApplyRetainBlend(e, w.left, Mathf.SmoothStep(0f, 1f, w.weight));
            }

            if (Time.time > _retainWatchCleanup)
            {
                _retainWatchCleanup = Time.time + 5f;
                var dead = new List<int>();
                foreach (var kv in _retainWatch) if (kv.Value.mag == null) dead.Add(kv.Key);
                foreach (var k in dead) _retainWatch.Remove(k);
            }
        }

        internal static PalmedMag FindPalmed(MagazinePoses m, string side)
        {
            if (m != null && m.palmed != null) foreach (var p in m.palmed) if (p != null && p.side == side) return p;
            return null;
        }

        // The saved palmed-magazine placement for a held magazine, or null to leave H3VR's own.
        internal PalmedMag PalmedFor(FVRFireArmMagazine held)
        {
            var mm = GM.CurrentMovementManager;
            if (held == null || held.m_hand == null || mm == null || mm.Hands == null || mm.Hands.Length < 2) return null;
            string side = held.m_hand == mm.Hands[0] ? "L" : "R";
            var set = FindMagazineSet(held);
            return set != null ? FindPalmed(set.magazine, side) : null;
        }

        // Blend this side's magazine poses toward Retain by weight (0 restores the saved poses exactly).
        void ApplyRetainBlend(Injected e, bool left, float weight)
        {
            string side = left ? "L" : "R";
            var data = e.set.magazine.poses;
            var rp = FindPose(data, "Retain" + side);
            if (rp == null) return;
            foreach (var kind in new[] { "Idle", "Fore" })
            {
                var hp = GetSlot(e.hpi, kind + side);
                var pd = FindPose(data, kind + side) ?? FindPose(data, "Idle" + side);
                if (hp == null || hp.Fingers == null || pd == null) continue;
                int n = Mathf.Min(hp.Fingers.Length, Mathf.Min(pd.joints.Length, rp.joints.Length));
                for (int i = 0; i < n; i++)
                {
                    if (hp.Fingers[i] == null) continue;
                    var t = hp.Fingers[i].transform;
                    t.localPosition = Vector3.Lerp(pd.joints[i].Pos, rp.joints[i].Pos, weight);
                    t.localRotation = Quaternion.Slerp(pd.joints[i].Rot, rp.joints[i].Rot, weight);
                }
            }
        }
    }

    // H3VR places a palmed magazine in FVRFireArmMagazine.FVRFixedUpdate by pivot-locking it beside the held one
    // (same rotation), then FVRPhysicalObject.FU moves it there. Just before FU runs, swap in the saved placement.
    [HarmonyLib.HarmonyPatch(typeof(FVRPhysicalObject), "FU")]
    static class PalmedMagPatch
    {
        public static PoseKitPlugin Plugin;

        static void Prefix(FVRPhysicalObject __instance)
        {
            var mag = __instance as FVRFireArmMagazine;
            if (mag == null || Plugin == null || !mag.IsPivotLocked) return;
            var held = mag.GetMagParent();
            if (held == null || !held.IsHeld) return;
            var pm = Plugin.PalmedFor(held);
            if (pm == null) return;
            var hr = held.transform.rotation;
            mag.PivotLockPos = mag.PivotLockPos + hr * pm.Pos;
            mag.PivotLockRot = hr * Quaternion.Euler(pm.Euler);
        }
    }
}
