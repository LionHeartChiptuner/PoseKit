using System;
using UnityEngine;

// All data classes are plain [Serializable] types so Unity's JsonUtility can read and write them.
// Every position/rotation is in Unity coordinates, in metres, relative to the gun's root transform
// with the gun's scale removed ("gun space").
namespace GT3PoseKit
{
    [Serializable]
    public class Xf
    {
        public float[] p = new float[3];
        public float[] r = new float[] { 0, 0, 0, 1 };

        public Vector3 Pos { get { return p != null && p.Length >= 3 ? new Vector3(p[0], p[1], p[2]) : Vector3.zero; } }
        public Quaternion Rot
        {
            get
            {
                if (r == null || r.Length < 4) return Quaternion.identity;
                var q = new Quaternion(r[0], r[1], r[2], r[3]);
                float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
                return m < 1e-6f ? Quaternion.identity : new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m);
            }
        }

        public static Xf From(Vector3 pos, Quaternion rot)
        {
            return new Xf { p = new[] { pos.x, pos.y, pos.z }, r = new[] { rot.x, rot.y, rot.z, rot.w } };
        }
    }

    // ---------- Pose set: what the editor exports and the loader reads ----------

    [Serializable]
    public class PoseData
    {
        // IdleL, IdleR, ForeL, ForeR, FireStartL, FireStartR, FireEndL, FireEndR
        public string slot;
        public Xf[] joints;
    }

    [Serializable]
    public class MatchData
    {
        public string[] itemIds = new string[0];      // FVRObject.ItemID, e.g. "Glock17"
        public string[] objectNames = new string[0];  // GameObject name, e.g. "Glock17(Clone)"
    }

    [Serializable]
    public class PoseSet
    {
        public string format;       // "gt3poses"
        public int version;
        public string name;
        public MatchData match;
        public bool hasTrigger;
        public bool useGrabPos;
        public Xf grabPos;
        public int jointCount;
        public PoseData[] poses;
        public bool noAutoCopy;          // true: don't reuse these poses for guns with similar names
        public PartPoses[] parts;        // slide / bolt / charging handle grabs
        public PumpPoses pump;           // the forward pump (shotguns) or moving fore-handle
        public MagazinePoses magazine;   // poses used while holding this gun's magazines
    }

    // Poses for gripping the gun's pump, relative to the pump so the hand slides with it.
    // The loader finds the pump itself (TubeFedShotgunHandle first, then other moving fore-handles).
    [Serializable]
    public class PumpPoses
    {
        public PoseData[] poses;   // IdleL / IdleR
    }

    // Poses for a grabbable sub-part of the gun. Joint transforms are relative to the part
    // (its FVRInteractiveObject.GameObject), so the hand moves with the slide while racking.
    [Serializable]
    public class PartPoses
    {
        public string name;
        public string path;   // transform path from the gun root, e.g. "Slide"
        public string type;   // component type, e.g. "HandgunSlide" (fallback when the path doesn't match)
        public PoseData[] poses;
    }

    // Poses for magazines, relative to the magazine's root. Matched by item ID or magazine type.
    [Serializable]
    public class MagazinePoses
    {
        public string[] itemIds = new string[0];
        public string[] magazineTypes = new string[0];
        public PoseData[] poses;
        public PalmedMag[] palmed = new PalmedMag[0];   // where the palmed magazine sits during a retention reload
    }

    // The palmed (second) magazine during a retention reload, per hand. H3VR stacks it beside the held magazine
    // with the same rotation; this moves it (p, metres, in the held magazine's axes) and turns it (e, degrees,
    // Euler angles relative to the held magazine).
    [Serializable]
    public class PalmedMag
    {
        public string side = "R";
        public float[] p = new float[3];
        public float[] e = new float[3];

        public Vector3 Pos { get { return p != null && p.Length >= 3 ? new Vector3(p[0], p[1], p[2]) : Vector3.zero; } set { p = new[] { value.x, value.y, value.z }; } }
        public Vector3 Euler { get { return e != null && e.Length >= 3 ? new Vector3(e[0], e[1], e[2]) : Vector3.zero; } set { e = new[] { value.x, value.y, value.z }; } }
    }

    // ---------- Rig dump: the GlovesTan3 hand skeleton ----------

    [Serializable]
    public class RigJoint
    {
        public string name;
        public int parent;   // index into joints, or -1 = child of the hand root frame
        public Xf local;     // relative to parent joint (or hand root), unscaled
    }

    [Serializable]
    public class RigHand
    {
        public string side;      // "L" or "R"
        public string rootName;  // name of the hand root frame transform
        public RigJoint[] joints;
    }

    [Serializable]
    public class RigDump
    {
        public string format = "gt3rig";
        public int version = 1;
        public RigHand left;
        public RigHand right;
    }

    // ---------- Glove mesh: the GlovesTan3 hand model, skinned to the rig joints ----------

    [Serializable]
    public class GloveHand
    {
        public string side;
        public int jointCount;     // must match the rig the editor has loaded
        public Xf[] bakeJoints;    // each joint's transform (relative to the hand root frame) when the mesh was baked
        public float[] v;          // vertex positions, hand root frame, xyz triples
        public float[] uv;         // uv pairs (may be empty)
        public int[] t;            // triangles
        public int[] bi;           // 4 joint indices per vertex (-1 = the hand root frame)
        public float[] bw;         // 4 weights per vertex
        public string weights;     // "skin" (the model's own weights) or "nearest" (estimated from bone distance)
        public string texture;     // base64 PNG of the main texture, or empty
        public float[] color;      // material colour rgba
        public string meshName;
    }

    [Serializable]
    public class GloveDump
    {
        public string format = "gt3glove";
        public int version = 1;
        public GloveHand left;
        public GloveHand right;
    }

    // ---------- Capture: one hand's pose on a held object, plus reference geometry ----------

    [Serializable]
    public class GeoPrim
    {
        public string kind;   // box | sphere | capsule | bounds
        public string name;
        public Xf xf;         // transform of the collider's object, gun space
        public float[] scale; // lossy scale relative to the gun
        public float[] center;
        public float[] size;
        public float radius;
        public float height;
        public int dir;
        public bool trigger;
    }

    [Serializable]
    public class GeoMesh
    {
        public string name;
        public float[] v;  // xyz triples, gun space
        public int[] t;    // triangle indices
    }

    [Serializable]
    public class Marker
    {
        public string name;
        public Xf xf;
    }

    [Serializable]
    public class PartInfo
    {
        public string name;
        public string path;
        public string type;
        public Xf xf;   // the part's pose frame in gun space, at rest
    }

    [Serializable]
    public class GunGeo
    {
        public GeoPrim[] prims;
        public GeoMesh[] meshes;
        public Marker[] markers;
        public PartInfo[] parts;
        public PartInfo pump;      // the gun's pump / moving fore-handle, if it has one (empty path otherwise)
    }

    // ---------- Models and the item catalogue (served to the editor, or written by "Export held model") ----------

    [Serializable]
    public class ModelData
    {
        public string format = "gt3model";
        public int version = 1;
        public string itemId;
        public string name;
        public string category;
        public int readableMeshes;  // parts exported with full triangles
        public int boxedMeshes;     // parts the game won't let us read, exported as their bounding boxes
        public string magazineType;      // set for firearms and magazines
        public bool isMagazine;
        public string[] compatibleMags;  // item IDs of magazines this firearm takes
        public GunGeo geo;
    }

    [Serializable]
    public class ItemInfo
    {
        public string id;
        public string name;
        public string cat;
        public bool mod;
    }

    [Serializable]
    public class LocalSet
    {
        public string key;       // the set's identity (its name, else first item ID)
        public string file;      // file name in the pose folder
        public string[] itemIds;
        public string modified;  // file time, ISO 8601
    }

    [Serializable]
    public class LocalSetList
    {
        public string format = "gt3sets";
        public LocalSet[] sets;
    }

    [Serializable]
    public class ItemList
    {
        public string format = "gt3items";
        public ItemInfo[] items;
    }

    [Serializable]
    public class BridgeStatus
    {
        public string format = "gt3bridge";
        public string version;
        public int items;
        public bool gloves;
        public bool freeze;
    }

    [Serializable]
    public class CaptureData
    {
        public string format = "gt3capture";
        public int version = 1;
        public string itemId;
        public string objectName;
        public string side;          // "L" or "R"
        public bool onForegrip;
        public bool fromPhysics;     // captured from GlovesTan3's physical (collider) fingers
        public bool isPump;          // the hand held the gun's pump (partXf is then the pump's frame)
        public bool isSupport;       // free hand gripping near the hand holding the gun (two-handed support grip)
        [NonSerialized] public FistVR.FVRPhysicalObject source; // the object captured on (not saved)
        public string partPath;      // set when the hand held a sub-part (slide, bolt handleâ€¦)
        public string partType;
        public Xf partXf;            // that part's frame in gun space at capture time
        public string magazineType;  // set when the held object is a magazine
        public bool isMagazine;
        public bool magPalmed;      // a second magazine is palmed with it (retention reload)
        public Xf handRoot;          // hand root frame, gun space
        public Xf[] joints;          // every joint, gun space
        public RigHand rig;
        public GunGeo geo;
    }
}
