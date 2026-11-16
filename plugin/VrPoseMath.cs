using System;
using System.Collections.Generic;
using UnityEngine;

namespace GT3PoseKit
{
    // The hand skeleton plus everything needed to edit a pose in game: finger roles, knuckles, and per-joint
    // curl / spread / twist axes. This mirrors the browser editor's analyseRig, in Unity's (left-handed) space.
    public class VrRig
    {
        public const int Thumb = 1, Index = 2, Middle = 3, Ring = 4, Pinky = 5;
        public static readonly string[] RoleNames = { "All", "Thumb", "Index", "Middle", "Ring", "Pinky" };

        public int n;
        public int[] parent;
        public Vector3[] restP;       // bone offset from the parent joint (or hand root)
        public Quaternion[] restQ;    // rest rotation relative to the parent
        public int[] order;           // parents before children
        public List<int> rootKids = new List<int>();
        public List<int>[] fingers = new List<int>[6]; // by role; index 0 unused
        public string[] knuckle;      // "meta", "base", "mid", "tip" or null (not a finger joint)
        public int[] role;            // finger role of each joint, 0 if none
        public Vector3[] curlAxis, spreadAxis, twistAxis; // in each joint's own rest frame

        public static VrRig Build(RigHand rh, GameObject[] idleFingers, bool left)
        {
            var r = new VrRig();
            r.n = rh.joints.Length;
            r.parent = new int[r.n];
            r.restP = new Vector3[r.n];
            r.restQ = new Quaternion[r.n];
            var children = new List<int>[r.n];
            for (int i = 0; i < r.n; i++)
            {
                children[i] = new List<int>();
                r.parent[i] = rh.joints[i].parent;
                r.restP[i] = rh.joints[i].local.Pos;
                r.restQ[i] = rh.joints[i].local.Rot;
            }
            for (int i = 0; i < r.n; i++) if (r.parent[i] >= 0) children[r.parent[i]].Add(i); else r.rootKids.Add(i);

            // GlovesTan3's own idle pose is the neutral hand: use its rotations as "rest" where available.
            if (idleFingers != null && idleFingers.Length == r.n)
            {
                for (int i = 0; i < r.n; i++)
                {
                    int p = r.parent[i];
                    if (p < 0 || idleFingers[i] == null || idleFingers[p] == null) continue;
                    r.restQ[i] = Quaternion.Inverse(idleFingers[p].transform.rotation) * idleFingers[i].transform.rotation;
                }
            }

            var order = new List<int>();
            Action<int> visit = null;
            visit = i => { order.Add(i); foreach (var c in children[i]) visit(c); };
            foreach (var k in r.rootKids) visit(k);
            r.order = order.ToArray();

            Vector3[] wp; Quaternion[] wq;
            r.Fk(Vector3.zero, Quaternion.identity, r.restQ, out wp, out wq);

            // Finger chains: start where the parent branches (or at the root) and follow single children.
            var chains = new List<List<int>>();
            foreach (var i in r.order)
            {
                int p = r.parent[i];
                if (p >= 0 && children[p].Count == 1) continue;
                var chain = new List<int> { i };
                int c = i;
                while (children[c].Count == 1) { c = children[c][0]; chain.Add(c); }
                if (chain.Count >= 2) chains.Add(chain);
            }
            Func<List<int>, string, bool> named = (ch, s) =>
            {
                foreach (var j in ch) if (rh.joints[j].name.ToLowerInvariant().Contains(s)) return true;
                return false;
            };
            List<int> thumb = chains.Find(ch => named(ch, "thumb"));
            if (thumb == null && chains.Count >= 5)
            {
                float best = -1;
                foreach (var ch in chains)
                {
                    float s = 0;
                    foreach (var o in chains) if (o != ch) s += Vector3.Distance(wp[o[0]], wp[ch[0]]);
                    if (s > best) { best = s; thumb = ch; }
                }
            }
            var others = chains.FindAll(ch => ch != thumb);
            if (thumb != null) others.Sort((a, b) => Vector3.Distance(wp[a[0]], wp[thumb[0]]).CompareTo(Vector3.Distance(wp[b[0]], wp[thumb[0]])));
            string[][] keys = { null, new[] { "thumb" }, new[] { "index", "point" }, new[] { "middle" }, new[] { "ring" }, new[] { "pink", "little" } };
            var used = new HashSet<List<int>>();
            if (thumb != null) { r.fingers[Thumb] = thumb; used.Add(thumb); }
            for (int role = Index; role <= Pinky; role++)
                foreach (var ch in others)
                    if (!used.Contains(ch) && Array.Exists(keys[role], k => named(ch, k))) { r.fingers[role] = ch; used.Add(ch); break; }
            int next = 0;
            for (int role = Index; role <= Pinky; role++)
            {
                if (r.fingers[role] != null) continue;
                while (next < others.Count && used.Contains(others[next])) next++;
                if (next < others.Count) { r.fingers[role] = others[next]; used.Add(others[next]); }
            }

            // Palm normal (out of the palm). Unity is left-handed: Cross(N, d) curls towards the palm.
            var wrist = Vector3.zero;
            foreach (var k in r.rootKids) wrist += wp[k];
            wrist /= Mathf.Max(1, r.rootKids.Count);
            var N = Vector3.down;
            if (r.fingers[Index] != null && r.fingers[Pinky] != null)
            {
                var c = Vector3.Cross(wp[r.fingers[Index][0]] - wrist, wp[r.fingers[Pinky][0]] - wrist);
                if (c.sqrMagnitude > 1e-10f) N = c.normalized;
            }
            if (left) N = -N;

            r.knuckle = new string[r.n];
            r.role = new int[r.n];
            r.curlAxis = new Vector3[r.n];
            r.spreadAxis = new Vector3[r.n];
            r.twistAxis = new Vector3[r.n];
            for (int role = Thumb; role <= Pinky; role++)
            {
                var f = r.fingers[role];
                if (f == null) continue;
                for (int k = 0; k < f.Count; k++)
                {
                    int i = f[k];
                    var d = k + 1 < f.Count ? wp[f[k + 1]] - wp[i] : wp[i] - wp[f[k - 1]];
                    d.Normalize();
                    var a = Vector3.Cross(N, d);
                    if (a.sqrMagnitude < 1e-8f) a = Vector3.right;
                    a.Normalize();
                    var s = Vector3.Cross(d, a).normalized;
                    var inv = Quaternion.Inverse(wq[i]);
                    r.curlAxis[i] = inv * a;
                    r.spreadAxis[i] = inv * s;
                    r.twistAxis[i] = inv * d;
                    int fromTip = f.Count - 1 - k;
                    r.knuckle[i] = fromTip >= 3 ? "meta" : (fromTip == 2 ? "base" : fromTip == 1 ? "mid" : "tip");
                    r.role[i] = role;
                }
            }
            return r;
        }

        public void Fk(Vector3 rootP, Quaternion rootQ, Quaternion[] local, out Vector3[] wp, out Quaternion[] wq)
        {
            wp = new Vector3[n];
            wq = new Quaternion[n];
            foreach (var i in order)
            {
                int p = parent[i];
                var pp = p >= 0 ? wp[p] : rootP;
                var pq = p >= 0 ? wq[p] : rootQ;
                wp[i] = pp + pq * restP[i];
                wq[i] = pq * local[i];
            }
        }

        // A pose in "local" form: hand root placement plus each joint's rotation relative to its parent.
        public class Local
        {
            public Vector3 rootP;
            public Quaternion rootQ;
            public Quaternion[] q;
        }

        public Local ToLocal(PoseData pd)
        {
            var L = new Local { q = new Quaternion[n] };
            int r0 = rootKids.Count > 0 ? rootKids[0] : 0;
            var w0 = pd.joints[r0];
            L.rootQ = w0.Rot * Quaternion.Inverse(restQ[r0]);
            L.rootP = w0.Pos - L.rootQ * restP[r0];
            for (int i = 0; i < n; i++)
            {
                int p = parent[i];
                var pq = p >= 0 ? pd.joints[p].Rot : L.rootQ;
                L.q[i] = Quaternion.Inverse(pq) * pd.joints[i].Rot;
            }
            return L;
        }

        public PoseData FromLocal(string slot, Local L)
        {
            Vector3[] wp; Quaternion[] wq;
            Fk(L.rootP, L.rootQ, L.q, out wp, out wq);
            var pd = new PoseData { slot = slot, joints = new Xf[n] };
            for (int i = 0; i < n; i++) pd.joints[i] = Xf.From(wp[i], wq[i]);
            return pd;
        }

        public Local Rest(Vector3 rootP, Quaternion rootQ)
        {
            return new Local { rootP = rootP, rootQ = rootQ, q = (Quaternion[])restQ.Clone() };
        }

        public Vector3 AxisOf(int joint, int axis)
        {
            return axis == 0 ? curlAxis[joint] : axis == 1 ? spreadAxis[joint] : twistAxis[joint];
        }

        // How much each knuckle bends when the whole finger curls.
        public float CurlWeight(int joint)
        {
            bool t = role[joint] == Thumb;
            switch (knuckle[joint])
            {
                case "meta": return t ? 0.3f : 0.15f;
                case "base": return t ? 0.5f : 1f;
                case "mid": return 1f;
                default: return t ? 1f : 0.8f;
            }
        }
    }
}
