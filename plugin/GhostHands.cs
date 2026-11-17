using System;
using System.Collections.Generic;
using FistVR;
using JerryGloves;
using UnityEngine;

namespace GT3PoseKit
{
    // Ghost hands: a see-through copy of the GlovesTan3 glove, posed as the slot being edited (in the browser
    // or the VR editor), shown on the matching weapon nearest the VR editor panel (or the player's head).
    // The pose is applied to the glove mesh on the CPU once per change, then simply parented to the weapon.
    public partial class PoseKitPlugin
    {
        [Serializable]
        public class GhostRequest
        {
            public string format;   // "gt3ghost"
            public bool clear;
            public string target;   // "gun", "mag", "pump" or "part:<path>"
            public string slot;     // e.g. "IdleR"
            public PoseSet set;     // the editor's current (unsaved) pose set
        }

        class GhostFocus
        {
            public PoseSet set;
            public string target, slot, version;
            public float time;
        }

        GhostFocus _ghostBrowser;
        int _ghostBrowserCount;
        float _vrFocusTime = -1f;

        GameObject _ghostObj;
        Mesh _ghostMesh;
        Material _ghostMat;
        string _ghostBuiltKey;
        FVRPhysicalObject _ghostOn;
        float _ghostScanAt;
        string _ghostScanKey;
        readonly Dictionary<bool, GloveHand> _ghostGlove = new Dictionary<bool, GloveHand>();
        readonly HashSet<string> _ghostWarned = new HashSet<string>();

        // ------------------------------------------------------------------ input

        [Serializable]
        public class FreezeRequest
        {
            public string format;   // "gt3freeze"
            public bool on;
        }

        string HandleFreezeRequest(string body)
        {
            var req = JsonUtility.FromJson<FreezeRequest>(body ?? "");
            if (req == null || req.format != "gt3freeze") throw new Exception("expected a gt3freeze request");
            SetFreeze(req.on);
            return "{\"ok\":true,\"freeze\":" + (_freezeOn ? "true" : "false") + "}";
        }

        string HandleGhostRequest(string body)
        {
            var req = JsonUtility.FromJson<GhostRequest>(body ?? "");
            if (req == null || req.format != "gt3ghost") throw new Exception("expected a gt3ghost request");
            if (req.clear || req.set == null || string.IsNullOrEmpty(req.slot)) { _ghostBrowser = null; return "{\"ok\":true,\"ghost\":false}"; }
            _ghostBrowserCount++;
            _ghostBrowser = new GhostFocus { set = req.set, target = string.IsNullOrEmpty(req.target) ? "gun" : req.target, slot = req.slot, version = "b" + _ghostBrowserCount, time = Time.time };
            string where = _ghostOn != null ? _ghostOn.gameObject.name : "";
            return "{\"ok\":true,\"ghost\":true,\"on\":\"" + Esc(where) + "\"}";
        }

        // The VR editor counts as the focus while its panel is open and it has a session; the most recently
        // changed editor wins.
        GhostFocus CurrentGhostFocus()
        {
            GhostFocus vr = null;
            if (_vrPanel != null && _vrPanel.activeInHierarchy && _vrSet != null)
                vr = new GhostFocus { set = _vrSet, target = _vrTarget, slot = _vrSlot, time = _vrFocusTime, version = null };
            if (vr != null && (_ghostBrowser == null || vr.time >= _ghostBrowser.time)) return vr;
            return _ghostBrowser;
        }

        // ------------------------------------------------------------------ per frame

        void UpdateGhost()
        {
            if (!_ghostEnabled.Value) { HideGhost(); return; }
            var focus = CurrentGhostFocus();
            if (focus == null || focus.set == null) { HideGhost(); return; }

            PoseData pd = FindPose(PosesFor(focus.set, focus.target), focus.slot);
            if (pd == null) { HideGhost(); return; }

            // Pick the weapon (re-scanned twice a second, or straight away when the focus changes).
            string scanKey = SetKey(focus.set) + "|" + focus.target;
            if (_ghostOn == null || scanKey != _ghostScanKey || Time.time > _ghostScanAt)
            {
                _ghostOn = NearestMatch(focus.set, focus.target == "mag");
                _ghostScanKey = scanKey;
                _ghostScanAt = Time.time + 0.5f;
            }
            if (_ghostOn == null) { HideGhost(); return; }

            Transform frame = GhostFrame(_ghostOn, focus.set, focus.target);
            if (frame == null) { HideGhost(); return; }

            bool left = focus.slot.EndsWith("L");
            // Rebuild only when the pose, the slot or the anchor changes.
            string key = frame.GetInstanceID() + "|" + focus.slot + "|" + JsonUtility.ToJson(pd);
            if (key != _ghostBuiltKey || _ghostObj == null)
            {
                if (!BuildGhostMesh(left, pd)) { HideGhost(); return; }
                _ghostBuiltKey = key;
            }
            if (_ghostObj.transform.parent != frame)
            {
                _ghostObj.transform.SetParent(frame, false);
                _ghostObj.transform.localPosition = Vector3.zero;
                _ghostObj.transform.localRotation = Quaternion.identity;
            }
            // Pose data is unscaled object space; cancel the object's scale (like the injected poses).
            var s = frame.lossyScale;
            _ghostObj.transform.localScale = new Vector3(SafeInv(s.x), SafeInv(s.y), SafeInv(s.z));
            ApplyGhostColour(left);
            if (!_ghostObj.activeSelf) _ghostObj.SetActive(true);
            UpdateFreeze(_ghostOn);
        }

        void HideGhost()
        {
            if (_ghostObj != null && _ghostObj.activeSelf) _ghostObj.SetActive(false);
            UpdateFreeze(null);
        }

        // ------------------------------------------------------------------ freeze in place

        // While on, the weapon showing the ghost stays exactly where it is (no gravity, no drifting) whenever
        // nobody holds it, so it can be left floating while you pose. Grabbing it works as normal; letting go
        // freezes it again where it was released.
        internal bool _freezeOn;
        FVRPhysicalObject _frozen;
        Rigidbody _frozenRb;
        bool _frozenWasKinematic, _frozenHadGravity;

        void UpdateFreeze(FVRPhysicalObject showing)
        {
            // A magazine sitting in a gun rides on the gun: freeze the gun instead.
            if (showing != null && showing.transform.parent != null)
            {
                var gun = showing.transform.parent.GetComponentInParent<FVRFireArm>();
                if (gun != null) showing = gun;
            }
            FVRPhysicalObject want = null;
            if (_freezeOn && showing != null && !showing.IsHeld && showing.QuickbeltSlot == null) want = showing;
            if (want == _frozen && (want == null || _frozenRb != null)) return;

            if (_frozen != null && _frozenRb != null)
            {
                // Hand the weapon back its own physics (a held gun needs a non-kinematic body to follow the hand).
                _frozenRb.isKinematic = _frozenWasKinematic;
                _frozenRb.useGravity = _frozenHadGravity;
            }
            _frozen = null;
            _frozenRb = null;

            if (want == null) return;
            var rb = want.GetComponent<Rigidbody>();
            if (rb == null) return;
            _frozen = want;
            _frozenRb = rb;
            _frozenWasKinematic = rb.isKinematic;
            _frozenHadGravity = rb.useGravity;
            if (!rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            rb.useGravity = false;
            rb.isKinematic = true;
        }

        void SetFreeze(bool on)
        {
            _freezeOn = on;
            if (!on) UpdateFreeze(null);
        }

        // ------------------------------------------------------------------ which weapon

        // The spawned object matching the pose set that's nearest the VR editor panel (or the head).
        FVRPhysicalObject NearestMatch(PoseSet set, bool magazine)
        {
            Vector3 from;
            if (_vrPanel != null && _vrPanel.activeInHierarchy) from = _vrPanel.transform.position;
            else
            {
                var body = GM.CurrentPlayerBody;
                if (body == null || body.Head == null) return null;
                from = body.Head.position;
            }
            FVRPhysicalObject best = null;
            float bestD = float.MaxValue;
            foreach (var o in FindObjectsOfType<FVRPhysicalObject>())
            {
                if (o == null || !o.gameObject.activeInHierarchy) continue;
                if (!(magazine ? MatchesMagazine(o, set) : MatchesGun(o, set))) continue;
                float d = (o.transform.position - from).sqrMagnitude;
                if (d < bestD) { bestD = d; best = o; }
            }
            return best;
        }

        static bool MatchesGun(FVRPhysicalObject o, PoseSet set)
        {
            if (o is FVRFireArmMagazine || set.match == null) return false;
            var id = o.ObjectWrapper != null ? o.ObjectWrapper.ItemID : null;
            if (!string.IsNullOrEmpty(id) && set.match.itemIds != null)
                foreach (var m in set.match.itemIds) if (string.Equals(m, id, StringComparison.OrdinalIgnoreCase)) return true;
            if (set.match.objectNames != null)
                foreach (var m in set.match.objectNames) if (string.Equals(m, o.gameObject.name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static bool MatchesMagazine(FVRPhysicalObject o, PoseSet set)
        {
            var mag = o as FVRFireArmMagazine;
            if (mag == null || set.magazine == null) return false;
            var id = mag.ObjectWrapper != null ? mag.ObjectWrapper.ItemID : null;
            if (!string.IsNullOrEmpty(id) && set.magazine.itemIds != null)
                foreach (var m in set.magazine.itemIds) if (string.Equals(m, id, StringComparison.OrdinalIgnoreCase)) return true;
            var type = mag.MagazineType.ToString();
            if (set.magazine.magazineTypes != null)
                foreach (var m in set.magazine.magazineTypes) if (string.Equals(m, type, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // The transform the target's poses are relative to (same places the loader attaches poses).
        static Transform GhostFrame(FVRPhysicalObject obj, PoseSet set, string target)
        {
            if (target == "gun" || target == "mag") return HpiParent(obj);
            if (target == "pump")
            {
                var pump = FindPump(obj.transform);
                return pump != null ? HpiParent(pump) : null;
            }
            if (target.StartsWith("part:"))
            {
                var path = target.Substring(5);
                PartPoses part = null;
                foreach (var p in set.parts ?? new PartPoses[0]) if (p != null && p.path == path) part = p;
                if (part == null) part = new PartPoses { path = path };
                var io = FindPart(obj.transform, part);
                return io != null ? HpiParent(io) : null;
            }
            return null;
        }

        // ------------------------------------------------------------------ mesh

        GloveHand Glove(bool left)
        {
            GloveHand g;
            if (_ghostGlove.TryGetValue(left, out g)) return g;
            var sys = FindSystem(left);
            if (sys == null) return null;
            string error;
            g = GloveExport.Build(sys, left, 4, out error); // texture isn't used by the ghost: keep it tiny
            if (g == null)
            {
                if (_ghostWarned.Add("glove" + left)) Logger.LogWarning("Ghost hands: no glove model (" + error + ")");
                return null;
            }
            _ghostGlove[left] = g;
            return g;
        }

        bool BuildGhostMesh(bool left, PoseData pd)
        {
            var glove = Glove(left);
            var rig = Rig(left);
            if (glove == null || rig == null) return false;
            if (glove.jointCount != pd.joints.Length || rig.n != pd.joints.Length)
            {
                if (_ghostWarned.Add("count")) Logger.LogWarning("Ghost hands: pose has " + pd.joints.Length + " joints, the glove has " + glove.jointCount);
                return false;
            }
            int n = glove.v.Length / 3;
            if (n == 0 || n > 65000) return false; // Unity 5.6 meshes use 16-bit indices

            // Linear blend skinning: each joint's pose transform times the inverse of its bake transform.
            var m = new Matrix4x4[pd.joints.Length];
            for (int i = 0; i < m.Length; i++)
            {
                var bake = glove.bakeJoints[i];
                m[i] = Matrix4x4.TRS(pd.joints[i].Pos, pd.joints[i].Rot, Vector3.one)
                       * Matrix4x4.TRS(bake.Pos, bake.Rot, Vector3.one).inverse;
            }
            var L = rig.ToLocal(pd);
            var mRoot = Matrix4x4.TRS(L.rootP, L.rootQ, Vector3.one); // vertices tied to the hand root frame

            var verts = new Vector3[n];
            for (int k = 0; k < n; k++)
            {
                var v = new Vector3(glove.v[k * 3], glove.v[k * 3 + 1], glove.v[k * 3 + 2]);
                Vector3 acc = Vector3.zero;
                float wsum = 0f;
                for (int j = 0; j < 4; j++)
                {
                    float w = glove.bw[k * 4 + j];
                    if (w <= 0f) continue;
                    int bi = glove.bi[k * 4 + j];
                    var mm = bi >= 0 && bi < m.Length ? m[bi] : mRoot;
                    acc += w * mm.MultiplyPoint3x4(v);
                    wsum += w;
                }
                verts[k] = wsum > 1e-6f ? acc / wsum : mRoot.MultiplyPoint3x4(v);
            }

            if (_ghostObj == null)
            {
                _ghostObj = new GameObject("GT3PoseKit_GhostHand");
                _ghostMesh = new Mesh { name = "GT3PoseKit ghost" };
                _ghostMesh.MarkDynamic();
                _ghostObj.AddComponent<MeshFilter>().sharedMesh = _ghostMesh;
                var r = _ghostObj.AddComponent<MeshRenderer>();
                _ghostMat = new Material(GhostShader());
                r.sharedMaterial = _ghostMat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            _ghostMesh.Clear();
            _ghostMesh.vertices = verts;
            if (glove.uv != null && glove.uv.Length == n * 2)
            {
                var uv = new Vector2[n];
                for (int k = 0; k < n; k++) uv[k] = new Vector2(glove.uv[k * 2], glove.uv[k * 2 + 1]);
                _ghostMesh.uv = uv;
            }
            _ghostMesh.triangles = glove.t;
            _ghostMesh.RecalculateNormals();
            _ghostMesh.RecalculateBounds();
            _ghostColourKey = null; // re-apply the colour (vertex colours are per mesh)
            return true;
        }

        // A see-through shader that takes a colour and opacity. Sprites/Default and UI/Default are always in
        // Unity builds (alpha-blended, colour via _Color, no depth write). Texture-only shaders such as
        // Unlit/Transparent ignore _Color, which is what made the ghost solid white; they're last resorts.
        static Shader GhostShader()
        {
            foreach (var name in new[] { "Sprites/Default", "UI/Default", "Legacy Shaders/Transparent/Diffuse", "Transparent/Diffuse", "Particles/Alpha Blended", "Unlit/Transparent" })
            {
                var s = Shader.Find(name);
                if (s != null) return s;
            }
            return Shader.Find("Standard");
        }

        Texture2D _ghostTex;
        string _ghostColourKey;

        void ApplyGhostColour(bool left)
        {
            if (_ghostMat == null) return;
            Color c;
            if (!ColorUtilityTryParse(left ? _ghostColourL.Value : _ghostColourR.Value, out c)) c = left ? new Color(0.24f, 0.76f, 0.81f) : new Color(0.94f, 0.63f, 0.32f);
            c.a = Mathf.Clamp01(_ghostOpacity.Value);
            string key = c.ToString();
            if (key == _ghostColourKey) return;
            _ghostColourKey = key;

            _ghostMat.renderQueue = 3000; // transparent pass, drawn after the gun
            if (_ghostMat.HasProperty("_Color")) _ghostMat.SetColor("_Color", c);
            if (_ghostMat.HasProperty("_TintColor")) _ghostMat.SetColor("_TintColor", new Color(c.r * 0.5f, c.g * 0.5f, c.b * 0.5f, c.a * 0.5f)); // particle shaders double it
            if (!_ghostMat.HasProperty("_Color") && !_ghostMat.HasProperty("_TintColor"))
            {
                // Texture-only shader: the colour and opacity have to come from the texture itself.
                if (_ghostTex == null) _ghostTex = new Texture2D(1, 1, TextureFormat.ARGB32, false);
                _ghostTex.SetPixel(0, 0, c);
                _ghostTex.Apply();
                _ghostMat.mainTexture = _ghostTex;
            }
            // Sprite/UI shaders also multiply by vertex colour: keep it white.
            if (_ghostMesh != null && _ghostMesh.vertexCount > 0)
            {
                var cols = new Color32[_ghostMesh.vertexCount];
                for (int i = 0; i < cols.Length; i++) cols[i] = new Color32(255, 255, 255, 255);
                _ghostMesh.colors32 = cols;
            }
        }

        // "#RRGGBB" -> colour (Unity 5.6's ColorUtility exists, but this keeps it simple and forgiving).
        static bool ColorUtilityTryParse(string s, out Color c)
        {
            c = Color.white;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim().TrimStart('#');
            int rgb;
            if (s.Length != 6 || !int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out rgb)) return false;
            c = new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f);
            return true;
        }
    }
}
