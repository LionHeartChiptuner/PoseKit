using System.Collections.Generic;
using FistVR;
using UnityEngine;

namespace GT3PoseKit
{
    // Ghost palmed magazine: while a Retention slot is being edited, a see-through copy of the magazine shows
    // where the second (palmed) magazine will sit: H3VR's own spot beside the held magazine, plus the pose set's
    // saved offset and turn for that hand. It's hidden when a real magazine is already palmed with it.
    public partial class PoseKitPlugin
    {
        GameObject _ghostMagObj;
        FVRFireArmMagazine _ghostMagFrom;
        Material _ghostMagMat;
        string _ghostMagColourKey;

        void UpdateGhostMag(GhostFocus focus, FVRPhysicalObject on, PoseData handPose)
        {
            var mag = on as FVRFireArmMagazine;
            bool want = focus != null && focus.target == "mag" && focus.slot != null && focus.slot.StartsWith("Retain")
                        && mag != null && mag.GetMagChild() == null && mag.GetMagParent() == null;
            if (!want) { HideGhostMag(); return; }

            if (_ghostMagObj == null || _ghostMagFrom != mag) BuildGhostMag(mag);
            if (_ghostMagObj == null) return;

            bool left = focus.slot.EndsWith("L");
            var side = left ? "L" : "R";
            var pm = FindPalmed(focus.set.magazine, side);

            // H3VR stacks the second magazine beside the held one, on the side it came from. For the ghost, use
            // the side the hand is on (from the Retention or Idle pose), else the magazine's right.
            float sign = 1f;
            var pd = handPose ?? FindPose(focus.set.magazine != null ? focus.set.magazine.poses : null, "Idle" + side);
            var rig = pd != null ? Rig(left) : null;
            if (rig != null && pd.joints.Length == rig.n) sign = rig.ToLocal(pd).rootP.x < 0f ? -1f : 1f;

            var t = mag.transform;
            float width = mag.GetMagWidth();
            var hr = t.rotation;
            var pos = t.position + t.right * (sign * (width + 0.02f));
            if (pm != null) pos += hr * pm.Pos;
            var rot = pm != null ? hr * Quaternion.Euler(pm.Euler) : hr;

            var g = _ghostMagObj.transform;
            if (g.parent != t) g.SetParent(t, true);
            g.position = pos;
            g.rotation = rot;
            ApplyGhostMagColour(left);
            if (!_ghostMagObj.activeSelf) _ghostMagObj.SetActive(true);
        }

        void HideGhostMag()
        {
            if (_ghostMagObj != null && _ghostMagObj.activeSelf) _ghostMagObj.SetActive(false);
        }

        // One child per visible mesh of the magazine, sharing its mesh (no copying, so unreadable meshes work),
        // placed relative to the magazine root. The root takes the magazine's scale so the pieces line up.
        void BuildGhostMag(FVRFireArmMagazine mag)
        {
            var old = _ghostMagObj;
            if (old != null) { old.transform.SetParent(null, false); old.SetActive(false); Destroy(old); }
            _ghostMagObj = null;
            _ghostMagFrom = mag;
            if (_ghostMagMat == null)
            {
                _ghostMagMat = new Material(GhostShader()) { renderQueue = 3000 };
                _ghostMagColourKey = null;
            }

            var root = new GameObject("GT3PoseKit_GhostPalmedMag");
            var rt = mag.transform;
            int pieces = 0;
            foreach (var mr in mag.GetComponentsInChildren<MeshRenderer>(false))
            {
                if (mr == null || !mr.enabled) continue;
                if (_ghostObj != null && mr.transform.IsChildOf(_ghostObj.transform)) continue;
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                var child = new GameObject(mr.gameObject.name);
                child.transform.SetParent(root.transform, false);
                // Pose relative to the magazine root, in the root's (scaled) space.
                child.transform.localPosition = rt.InverseTransformPoint(mr.transform.position);
                child.transform.localRotation = Quaternion.Inverse(rt.rotation) * mr.transform.rotation;
                var ls = mr.transform.lossyScale; var rs = rt.lossyScale;
                child.transform.localScale = new Vector3(ls.x / NonZero(rs.x), ls.y / NonZero(rs.y), ls.z / NonZero(rs.z));
                child.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                var r = child.AddComponent<MeshRenderer>();
                var mats = new Material[Mathf.Max(1, mf.sharedMesh.subMeshCount)];
                for (int i = 0; i < mats.Length; i++) mats[i] = _ghostMagMat;
                r.sharedMaterials = mats;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                pieces++;
            }
            if (pieces == 0)
            {
                Destroy(root);
                if (_ghostWarned.Add("ghostmag")) Logger.LogWarning("Ghost palmed magazine: the magazine has no visible meshes to copy");
                return;
            }
            root.transform.localScale = rt.lossyScale;
            _ghostMagObj = root;
        }

        static float NonZero(float f) { return Mathf.Abs(f) < 1e-6f ? 1f : f; }

        void ApplyGhostMagColour(bool left)
        {
            Color c;
            if (!ColorUtilityTryParse(left ? _ghostColourL.Value : _ghostColourR.Value, out c)) c = left ? new Color(0.24f, 0.76f, 0.81f) : new Color(0.94f, 0.63f, 0.32f);
            // Lighter than the hand, so the two are easy to tell apart.
            c = Color.Lerp(c, Color.white, 0.45f);
            c.a = Mathf.Clamp01(_ghostOpacity.Value);
            string key = c.ToString();
            if (key == _ghostMagColourKey) return;
            _ghostMagColourKey = key;
            if (_ghostMagMat.HasProperty("_Color")) _ghostMagMat.SetColor("_Color", c);
            if (_ghostMagMat.HasProperty("_TintColor")) _ghostMagMat.SetColor("_TintColor", new Color(c.r * 0.5f, c.g * 0.5f, c.b * 0.5f, c.a * 0.5f));
        }
    }
}
