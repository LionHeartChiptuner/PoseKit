using System;
using System.Collections.Generic;
using JerryGloves;
using UnityEngine;

namespace GT3PoseKit
{
    // Exports the GlovesTan3 glove mesh so the editor can show (and pose) the real hand model.
    public static class GloveExport
    {
        public static GloveHand Build(HandsPoseSystem sys, bool left, int maxTexture, out string error)
        {
            error = null;
            if (sys == null) { error = "Spawn the GlovesTan3 gloves first"; return null; }

            // Index lookup for both joint arrays; they are index-compatible (GlovesTan3 copies one onto the other).
            var lookups = new List<KeyValuePair<GameObject[], Dictionary<Transform, int>>>();
            foreach (var arr in new[] { sys.Fingers, sys.realfingers })
            {
                if (arr == null || arr.Length == 0) continue;
                var d = new Dictionary<Transform, int>();
                for (int i = 0; i < arr.Length; i++) if (arr[i] != null) d[arr[i].transform] = i;
                lookups.Add(new KeyValuePair<GameObject[], Dictionary<Transform, int>>(arr, d));
            }
            if (lookups.Count == 0) { error = "GlovesTan3 hand has no joints"; return null; }

            // The visible hand: the physics hand when it's active, else the posed (animated) one.
            var holders = new List<GameObject>();
            if (sys.HandPhys != null && sys.HandPhys.activeInHierarchy) holders.Add(sys.HandPhys);
            if (sys.HandAnim != null) holders.Add(sys.HandAnim);
            if (sys.HandPhys != null && !holders.Contains(sys.HandPhys)) holders.Add(sys.HandPhys);
            holders.Add(sys.gameObject);

            SkinnedMeshRenderer[] smrs = null;
            foreach (var h in holders)
            {
                var found = h.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                if (found.Length > 0) { smrs = found; break; }
            }
            if (smrs == null) { error = "No skinned glove mesh found on the GlovesTan3 hand"; return null; }

            // Which joint array the mesh is bound to: the one that owns most of its bones.
            GameObject[] joints = lookups[0].Key;
            Dictionary<Transform, int> index = lookups[0].Value;
            int best = -1;
            foreach (var lk in lookups)
            {
                int hits = 0;
                foreach (var smr in smrs) foreach (var b in smr.bones ?? new Transform[0]) if (b != null && lk.Value.ContainsKey(b)) hits++;
                if (hits > best) { best = hits; joints = lk.Key; index = lk.Value; }
            }

            var root = CommonRoot(joints);
            if (root == null) { error = "Could not find the hand root"; return null; }
            var invRot = Quaternion.Inverse(root.rotation);
            Func<Vector3, Vector3> toRoot = w => invRot * (w - root.position);

            var verts = new List<float>();
            var uvs = new List<float>();
            var tris = new List<int>();
            var bi = new List<int>();
            var bw = new List<float>();
            bool allSkinned = true;
            string texture = "", meshName = "";
            float[] color = { 1, 1, 1, 1 };

            foreach (var smr in smrs)
            {
                if (smr == null || smr.sharedMesh == null) continue;
                var baked = new Mesh();
                try { smr.BakeMesh(baked); } catch { continue; }
                var bv = baked.vertices;
                var bt = baked.triangles;
                if (bv == null || bv.Length == 0 || bt == null || bt.Length == 0) continue;
                int offset = verts.Count / 3;

                // BakeMesh output is relative to the renderer's position and rotation (scale is already applied).
                var t = smr.transform;
                foreach (var p in bv)
                {
                    var r = toRoot(t.position + t.rotation * p);
                    verts.Add(r.x); verts.Add(r.y); verts.Add(r.z);
                }
                var buv = baked.uv;
                if (buv != null && buv.Length == bv.Length) foreach (var u in buv) { uvs.Add(u.x); uvs.Add(u.y); }
                else for (int i = 0; i < bv.Length; i++) { uvs.Add(0); uvs.Add(0); }
                foreach (var i in bt) tris.Add(i + offset);

                // Weights: the model's own when its mesh is readable, else estimated from distance to the bones.
                BoneWeight[] w = null;
                var src = smr.sharedMesh;
                if (src.isReadable) { try { w = src.boneWeights; } catch { w = null; } }
                var bones = smr.bones ?? new Transform[0];
                if (w != null && w.Length == bv.Length && bones.Length > 0)
                {
                    var map = new int[bones.Length];
                    for (int b = 0; b < bones.Length; b++) map[b] = JointFor(bones[b], index, root);
                    foreach (var x in w)
                    {
                        AddWeight(bi, bw, Map(map, x.boneIndex0), x.weight0);
                        AddWeight(bi, bw, Map(map, x.boneIndex1), x.weight1);
                        AddWeight(bi, bw, Map(map, x.boneIndex2), x.weight2);
                        AddWeight(bi, bw, Map(map, x.boneIndex3), x.weight3);
                    }
                }
                else
                {
                    allSkinned = false;
                    NearestWeights(bv, t, joints, root, toRoot, bi, bw);
                }

                if (meshName == "") meshName = src.name;
                if (texture == "")
                {
                    var mat = smr.sharedMaterial;
                    if (mat != null)
                    {
                        if (mat.HasProperty("_Color")) { var c = mat.color; color = new[] { c.r, c.g, c.b, c.a }; }
                        texture = ReadTexture(mat.mainTexture, maxTexture);
                    }
                }
                UnityEngine.Object.Destroy(baked);
            }
            if (verts.Count == 0) { error = "The glove mesh could not be read"; return null; }

            var bake = new Xf[joints.Length];
            for (int i = 0; i < joints.Length; i++)
            {
                if (joints[i] == null) { bake[i] = new Xf(); continue; }
                var jt = joints[i].transform;
                bake[i] = Xf.From(toRoot(jt.position), invRot * jt.rotation);
            }

            return new GloveHand
            {
                side = left ? "L" : "R",
                jointCount = joints.Length,
                bakeJoints = bake,
                v = verts.ToArray(),
                uv = uvs.ToArray(),
                t = tris.ToArray(),
                bi = bi.ToArray(),
                bw = bw.ToArray(),
                weights = allSkinned ? "skin" : "nearest",
                texture = texture,
                color = color,
                meshName = meshName
            };
        }

        static int Map(int[] map, int boneIndex) { return boneIndex >= 0 && boneIndex < map.Length ? map[boneIndex] : -1; }

        static void AddWeight(List<int> bi, List<float> bw, int joint, float weight)
        {
            bi.Add(joint); bw.Add(weight);
        }

        // A skin bone that isn't one of the rig joints follows its nearest rig ancestor (or the hand root).
        static int JointFor(Transform bone, Dictionary<Transform, int> index, Transform root)
        {
            for (var t = bone; t != null && t != root; t = t.parent)
            {
                int i;
                if (index.TryGetValue(t, out i)) return i;
            }
            return -1;
        }

        // For locked meshes: weight each vertex by its distance to the two nearest bone segments.
        static void NearestWeights(Vector3[] bv, Transform smrT, GameObject[] joints, Transform root,
                                   Func<Vector3, Vector3> toRoot, List<int> bi, List<float> bw)
        {
            int n = joints.Length;
            var pos = new Vector3[n];
            var parent = new int[n];
            var hasChild = new bool[n];
            var idx = new Dictionary<Transform, int>();
            for (int i = 0; i < n; i++) if (joints[i] != null) { idx[joints[i].transform] = i; pos[i] = joints[i].transform.position; }
            for (int i = 0; i < n; i++)
            {
                parent[i] = -1;
                if (joints[i] == null) continue;
                for (var a = joints[i].transform.parent; a != null && a != root; a = a.parent)
                {
                    int p;
                    if (idx.TryGetValue(a, out p)) { parent[i] = p; break; }
                }
                if (parent[i] >= 0) hasChild[parent[i]] = true;
            }
            // Segment per joint: towards its first child, or onward past a leaf.
            var segA = new Vector3[n];
            var segB = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                segA[i] = pos[i];
                int child = -1;
                for (int c = 0; c < n; c++) if (parent[c] == i) { child = c; break; }
                if (child >= 0) segB[i] = pos[child];
                else if (parent[i] >= 0) segB[i] = pos[i] + (pos[i] - pos[parent[i]]) * 0.7f;
                else segB[i] = pos[i];
            }
            foreach (var p in bv)
            {
                var wpt = smrT.position + smrT.rotation * p;
                int b1 = -1, b2 = -1;
                float d1 = float.MaxValue, d2 = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    if (joints[i] == null) continue;
                    float d = SegDist(wpt, segA[i], segB[i]);
                    if (d < d1) { d2 = d1; b2 = b1; d1 = d; b1 = i; }
                    else if (d < d2) { d2 = d; b2 = i; }
                }
                float w1 = 1f / Mathf.Pow(Mathf.Max(d1, 1e-4f), 4), w2 = b2 >= 0 ? 1f / Mathf.Pow(Mathf.Max(d2, 1e-4f), 4) : 0;
                float s = w1 + w2;
                bi.Add(b1); bw.Add(w1 / s);
                bi.Add(b2 >= 0 ? b2 : b1); bw.Add(w2 / s);
                bi.Add(b1); bw.Add(0);
                bi.Add(b1); bw.Add(0);
            }
        }

        static float SegDist(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = b - a;
            float l = ab.sqrMagnitude;
            float t = l < 1e-10f ? 0 : Mathf.Clamp01(Vector3.Dot(p - a, ab) / l);
            return (p - (a + ab * t)).magnitude;
        }

        // Copies a (possibly unreadable) texture through a RenderTexture and returns it as base64 PNG.
        static string ReadTexture(Texture tex, int maxSize)
        {
            if (tex == null) return "";
            try
            {
                int w = tex.width, h = tex.height;
                float s = Mathf.Min(1f, (float)maxSize / Mathf.Max(w, h));
                w = Mathf.Max(1, Mathf.RoundToInt(w * s)); h = Mathf.Max(1, Mathf.RoundToInt(h * s));
                var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                var prev = RenderTexture.active;
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var copy = new Texture2D(w, h, TextureFormat.ARGB32, false);
                copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                copy.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                var png = copy.EncodeToPNG();
                UnityEngine.Object.Destroy(copy);
                return png != null ? Convert.ToBase64String(png) : "";
            }
            catch { return ""; }
        }

        static Transform CommonRoot(GameObject[] joints)
        {
            List<Transform> chain = null;
            foreach (var g in joints)
            {
                if (g == null) continue;
                var anc = new List<Transform>();
                for (var t = g.transform; t != null; t = t.parent) anc.Insert(0, t);
                if (chain == null) { chain = anc; continue; }
                int k = 0;
                while (k < chain.Count && k < anc.Count && chain[k] == anc[k]) k++;
                chain.RemoveRange(k, chain.Count - k);
            }
            if (chain == null || chain.Count == 0) return null;
            var root = chain[chain.Count - 1];
            foreach (var g in joints) if (g != null && g.transform == root) return root.parent ?? root;
            return root;
        }
    }
}
