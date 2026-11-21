using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Logging;
using FistVR;
using H3MP;
using H3MP.Networking;
using H3MP.Scripts;
using H3MP.Tracking;
using HarmonyLib;
using JerryGloves;
using JerryHand;
using UnityEngine;

namespace GT3PoseKit
{
    // Shows GlovesTan3 gloves on other players in H3MP.
    //
    // GlovesTan3 spawns its gloves locally and drives them from this machine's own controllers, so H3MP never
    // shows them to anyone else. Here, every player sends the bone poses of the glove they can see (relative to
    // their own hand, which H3MP already syncs). Every other player then draws a render-only copy of the glove
    // on that player's hand, using the glove mesh from their own GlovesTan3 install. The copy has no physics and
    // no GlovesTan3 scripts, so it can't latch onto the viewer's own hands.
    //
    // Only stock H3MP API is used, so this works with an unmodified H3MP. Everyone who wants to see gloves
    // needs PoseKit and GlovesTan3; players without them are unaffected.
    //
    // This file is the only one that touches H3MP types. PoseKitPlugin only calls into it when H3MP is loaded.
    internal class H3MPGloveSync : MonoBehaviour
    {
        const string PacketName = "GT3PoseKit.GloveState";
        const byte ProtocolVersion = 1;
        const int MaxRenderers = 8;
        const int MaxBones = 128;
        const float PosScale = 10000f;       // positions are sent as shorts in units of 0.1 mm (range +-3.27 m)
        const float RemoteTimeout = 1.0f;    // hide a remote glove if nothing arrives for this long
        const float SmoothingRate = 25f;     // how fast remote fingers catch up with the latest pose (per second)

        internal static ManualLogSource Log;
        internal static bool SendEnabled = true;
        internal static bool ShowEnabled = true;
        internal static float SendInterval = 0.05f;

        static int _packetIndex = -1;
        static H3MPGloveSync _instance;

        float _sendTimer;
        HandsPoseSystem[] _systems;
        float _systemsAt = -10f;
        readonly MemoryStream _buffer = new MemoryStream(2048);

        // ------------------------------------------------------------------ setup

        internal static void Install(GameObject host, ManualLogSource log, Harmony harmony)
        {
            Log = log;
            _instance = host.AddComponent<H3MPGloveSync>();

            H3MP.Mod.OnConnection += OnConnected;
            H3MP.Mod.CustomPacketHandlerReceived += OnHandlerRegistered;
            H3MP.Mod.OnPlayerRemoved += OnPlayerRemoved;

            // Keep H3MP from turning the gloves into a networked item. Other players would get a copy of the
            // whole glove, including the GlovesTan3 scripts that attach it to *their* controllers.
            var sync = AccessTools.Method(typeof(GameManager), "SyncTrackedObjects", new[] { typeof(Transform), typeof(bool), typeof(TrackedObjectData) });
            if (sync != null)
                harmony.Patch(sync, prefix: new HarmonyMethod(typeof(H3MPGloveSync).GetMethod(nameof(SyncTrackedObjectsPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
            else
                Log.LogWarning("H3MP glove sync: GameManager.SyncTrackedObjects not found; H3MP may still try to network the gloves themselves.");

            Log.LogInfo("H3MP detected: GlovesTan3 gloves will be shared with other players who have PoseKit.");
        }

        static bool SyncTrackedObjectsPrefix(Transform root)
        {
            return !IsGloveObject(root);
        }

        // A physical object that is (part of) a GlovesTan3 glove.
        static bool IsGloveObject(Transform t)
        {
            if (t == null || t.GetComponent<FVRPhysicalObject>() == null) return false;
            return t.GetComponentInChildren<HandsPoseSystem>(true) != null || t.GetComponentInParent<HandsPoseSystem>() != null
                || t.GetComponentInChildren<HandsPalm>(true) != null || t.GetComponentInParent<HandsPalm>() != null;
        }

        static bool Connected()
        {
            return H3MP.Mod.managerObject != null && (ThreadManager.host || Client.isFullyConnected);
        }

        // Fired by H3MP on the host once the server is up, and on a client once it has fully joined.
        static void OnConnected()
        {
            try
            {
                int index;
                if (H3MP.Mod.registeredCustomPacketIDs.TryGetValue(PacketName, out index)) { SetPacketIndex(index); return; }
                if (ThreadManager.host) SetPacketIndex(Server.RegisterCustomPacketType(PacketName));
                else ClientSend.RegisterCustomPacketType(PacketName); // the index arrives via CustomPacketHandlerReceived
            }
            catch (Exception e) { Log.LogWarning("H3MP glove sync: could not register packet type: " + e.Message); }
        }

        // H3MP assigned an index to a custom packet type (ours, or another mod's).
        static void OnHandlerRegistered(string id, int index)
        {
            if (id == PacketName) SetPacketIndex(index);
        }

        static void SetPacketIndex(int index)
        {
            if (index < 0) return;
            var handlers = H3MP.Mod.customPacketHandlers;
            if (handlers == null || index >= handlers.Length) return;
            _packetIndex = index;
            handlers[index] = HandlePacket;
        }

        static void OnPlayerRemoved(PlayerManager player)
        {
            if (player != null && _instance != null) _instance.RemoveRemote(player.ID);
        }

        // ------------------------------------------------------------------ per frame

        void Update()
        {
            if (!Connected())
            {
                // H3MP clears registered custom packets when the session ends.
                if (_packetIndex != -1 || _remotes.Count > 0) { _packetIndex = -1; ClearRemotes(); }
                return;
            }
            if (!SendEnabled || _packetIndex < 0 || GameManager.playersPresent.Count == 0) return;

            _sendTimer -= Time.unscaledDeltaTime;
            if (_sendTimer > 0f) return;
            _sendTimer = SendInterval;

            var body = GM.CurrentPlayerBody;
            if (body == null || body.LeftHand == null || body.RightHand == null) return;
            if (_systems == null || Time.unscaledTime - _systemsAt > 2f) { _systems = FindObjectsOfType<HandsPoseSystem>(); _systemsAt = Time.unscaledTime; }

            bool sentL = false, sentR = false;
            foreach (var sys in _systems)
            {
                if (sys == null || !sys.gameObject.activeInHierarchy || !IsOurs(sys)) continue;
                if (sys.isLeft ? sentL : sentR) continue;
                try
                {
                    var payload = BuildPayload(sys, sys.isLeft ? body.LeftHand : body.RightHand);
                    if (payload == null) continue;
                    SendOwn(payload);
                    if (sys.isLeft) sentL = true; else sentR = true;
                }
                catch (Exception e) { Log.LogWarning("H3MP glove sync: send failed: " + e.Message); }
            }
        }

        // Gloves that are another player's networked item (e.g. they don't have PoseKit) aren't ours to send.
        static bool IsOurs(HandsPoseSystem sys)
        {
            var tracked = sys.GetComponentInParent<TrackedObject>();
            return tracked == null || tracked.data == null || tracked.data.controller == GameManager.ID;
        }

        // The glove renderers of one hand, in an order that's the same on the prefab and on any spawned copy.
        static List<SkinnedMeshRenderer> GloveRenderers(HandsPoseSystem sys)
        {
            var list = new List<SkinnedMeshRenderer>();
            var seen = new HashSet<SkinnedMeshRenderer>();
            foreach (var holder in new[] { sys.HandPhys, sys.HandAnim, sys.gameObject })
            {
                if (holder == null) continue;
                foreach (var smr in holder.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (smr != null && seen.Add(smr)) list.Add(smr);
            }
            return list;
        }

        // Payload: version, side (0 left / 1 right), renderer count, then per visible renderer: its index, bone
        // count and every bone's pose relative to the player's hand.
        byte[] BuildPayload(HandsPoseSystem sys, Transform hand)
        {
            var smrs = GloveRenderers(sys);
            _buffer.SetLength(0);
            var w = new BinaryWriter(_buffer);
            w.Write(ProtocolVersion);
            w.Write((byte)(sys.isLeft ? 0 : 1));

            var visible = new List<int>();
            for (int i = 0; i < smrs.Count && i < MaxRenderers; i++)
                if (smrs[i].enabled && smrs[i].gameObject.activeInHierarchy && smrs[i].sharedMesh != null) visible.Add(i);
            w.Write((byte)visible.Count);

            var invRot = Quaternion.Inverse(hand.rotation);
            var handPos = hand.position;
            foreach (int i in visible)
            {
                var bones = smrs[i].bones ?? new Transform[0];
                int n = Mathf.Min(bones.Length, MaxBones);
                w.Write((byte)i);
                w.Write((byte)n);
                for (int b = 0; b < n; b++)
                {
                    Vector3 p = Vector3.zero;
                    Quaternion q = Quaternion.identity;
                    if (bones[b] != null)
                    {
                        p = invRot * (bones[b].position - handPos);
                        q = invRot * bones[b].rotation;
                    }
                    WritePos(w, p);
                    WriteRot(w, q);
                }
            }
            w.Flush();
            return _buffer.ToArray();
        }

        static void WritePos(BinaryWriter w, Vector3 p)
        {
            w.Write(ToShort(p.x * PosScale));
            w.Write(ToShort(p.y * PosScale));
            w.Write(ToShort(p.z * PosScale));
        }

        static void WriteRot(BinaryWriter w, Quaternion q)
        {
            float mag = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (mag < 1e-6f) q = Quaternion.identity; else { q.x /= mag; q.y /= mag; q.z /= mag; q.w /= mag; }
            w.Write(ToShort(q.x * 32767f));
            w.Write(ToShort(q.y * 32767f));
            w.Write(ToShort(q.z * 32767f));
            w.Write(ToShort(q.w * 32767f));
        }

        static short ToShort(float v)
        {
            return (short)Mathf.Clamp(Mathf.RoundToInt(v), short.MinValue, short.MaxValue);
        }

        // ------------------------------------------------------------------ network

        // Client -> host: [payload]. Host -> client: [int source player ID][payload].
        // Host-to-client packets go out as single unbatched UDP datagrams: H3MP 1.14.1's batched UDP path
        // can't carry custom packets.
        static void SendOwn(byte[] payload)
        {
            if (ThreadManager.host)
            {
                SendToClients(GameManager.ID, payload, GameManager.playersPresent, -1);
            }
            else
            {
                using (var packet = new Packet(_packetIndex))
                {
                    packet.Write(payload);
                    ClientSend.SendUDPData(packet, true);
                }
            }
        }

        static void SendToClients(int source, byte[] payload, List<int> to, int exclude)
        {
            using (var packet = new Packet(-_packetIndex - 2)) // custom packet IDs are negative on the wire
            {
                packet.Write(source);
                packet.Write(payload);
                packet.WriteLength();
                foreach (int id in to)
                {
                    if (id == exclude || id == GameManager.ID) continue;
                    ServerClient client;
                    if (Server.clients.TryGetValue(id, out client) && client != null && client.connected && client.udp != null && client.udp.endPoint != null)
                        client.udp.SendData(packet);
                }
            }
        }

        static void HandlePacket(int clientID, Packet packet)
        {
            try
            {
                if (ThreadManager.host)
                {
                    // From client clientID: check it, show it, and pass it on to the players in the same place.
                    var payload = ReadRest(packet);
                    var update = Parse(payload);
                    if (update == null) return;
                    if (_instance != null) _instance.Apply(clientID, update);

                    ServerClient sender;
                    if (!Server.clients.TryGetValue(clientID, out sender) || sender == null || sender.player == null) return;
                    Dictionary<int, List<int>> instances;
                    List<int> players;
                    if (GameManager.playersByInstanceByScene.TryGetValue(sender.player.scene, out instances) &&
                        instances.TryGetValue(sender.player.instance, out players))
                        SendToClients(clientID, payload, players, clientID);
                }
                else
                {
                    int source = packet.ReadInt();
                    if (source == GameManager.ID) return;
                    var update = Parse(ReadRest(packet));
                    if (update != null && _instance != null) _instance.Apply(source, update);
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("H3MP glove sync: bad glove packet from " + clientID + ": " + e.Message);
            }
        }

        static byte[] ReadRest(Packet packet)
        {
            int n = packet.UnreadLength();
            return n > 0 ? packet.ReadBytes(n) : new byte[0];
        }

        class HandUpdate
        {
            public bool left;
            public int[] rendererIndex;
            public Vector3[][] pos;
            public Quaternion[][] rot;
        }

        // Returns null for anything malformed, so nothing bad gets shown or passed on.
        static HandUpdate Parse(byte[] payload)
        {
            if (payload == null || payload.Length < 3) return null;
            using (var r = new BinaryReader(new MemoryStream(payload)))
            {
                if (r.ReadByte() != ProtocolVersion) return null;
                byte side = r.ReadByte();
                if (side > 1) return null;
                int count = r.ReadByte();
                if (count > MaxRenderers) return null;
                var u = new HandUpdate
                {
                    left = side == 0,
                    rendererIndex = new int[count],
                    pos = new Vector3[count][],
                    rot = new Quaternion[count][]
                };
                for (int i = 0; i < count; i++)
                {
                    int idx = r.ReadByte();
                    int n = r.ReadByte();
                    if (idx >= MaxRenderers || n > MaxBones) return null;
                    if (r.BaseStream.Length - r.BaseStream.Position < n * 14L) return null;
                    u.rendererIndex[i] = idx;
                    u.pos[i] = new Vector3[n];
                    u.rot[i] = new Quaternion[n];
                    for (int b = 0; b < n; b++)
                    {
                        u.pos[i][b] = new Vector3(r.ReadInt16() / PosScale, r.ReadInt16() / PosScale, r.ReadInt16() / PosScale);
                        var q = new Quaternion(r.ReadInt16() / 32767f, r.ReadInt16() / 32767f, r.ReadInt16() / 32767f, r.ReadInt16() / 32767f);
                        float mag = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
                        u.rot[i][b] = mag < 1e-4f ? Quaternion.identity : new Quaternion(q.x / mag, q.y / mag, q.z / mag, q.w / mag);
                    }
                }
                return u;
            }
        }

        // ------------------------------------------------------------------ the glove template

        class RendererTemplate
        {
            public Mesh mesh;
            public Material[] materials;
            public Vector3[] boneScale;
            public UnityEngine.Rendering.ShadowCastingMode shadows;
            public bool receiveShadows;
            public SkinQuality quality;
        }

        // Per side (true = left): the glove renderers, taken from the GlovesTan3 prefab without spawning it.
        readonly Dictionary<bool, List<RendererTemplate>> _templates = new Dictionary<bool, List<RendererTemplate>>();
        bool _templatesTried;

        List<RendererTemplate> Template(bool left)
        {
            if (!_templatesTried)
            {
                _templatesTried = true;
                try { BuildTemplates(); }
                catch (Exception e) { Log.LogWarning("H3MP glove sync: could not read the GlovesTan3 glove model: " + e.Message); }
            }
            List<RendererTemplate> t;
            return _templates.TryGetValue(left, out t) ? t : null;
        }

        void BuildTemplates()
        {
            var spawner = IM.GetSpawnerID("GlovesTan3");
            var prefab = spawner != null && spawner.MainObject != null ? spawner.MainObject.GetGameObject() : null;
            if (prefab == null) { Log.LogWarning("H3MP glove sync: GlovesTan3 item not found, other players' gloves can't be shown."); return; }

            foreach (var sys in prefab.GetComponentsInChildren<HandsPoseSystem>(true))
            {
                if (sys == null || _templates.ContainsKey(sys.isLeft)) continue;
                var list = new List<RendererTemplate>();
                foreach (var smr in GloveRenderers(sys))
                {
                    var bones = smr.bones ?? new Transform[0];
                    var scale = new Vector3[bones.Length];
                    for (int i = 0; i < bones.Length; i++)
                    {
                        var s = bones[i] != null ? bones[i].lossyScale : Vector3.one;
                        scale[i] = IsUsableScale(s) ? s : Vector3.one;
                    }
                    list.Add(new RendererTemplate
                    {
                        mesh = smr.sharedMesh,
                        materials = smr.sharedMaterials,
                        boneScale = scale,
                        shadows = smr.shadowCastingMode,
                        receiveShadows = smr.receiveShadows,
                        quality = smr.quality
                    });
                }
                _templates[sys.isLeft] = list;
            }
            if (_templates.Count == 0) Log.LogWarning("H3MP glove sync: the GlovesTan3 item has no hands, other players' gloves can't be shown.");
        }

        static bool IsUsableScale(Vector3 s)
        {
            return !float.IsNaN(s.x) && !float.IsNaN(s.y) && !float.IsNaN(s.z) && Mathf.Abs(s.x) > 1e-4f && Mathf.Abs(s.y) > 1e-4f && Mathf.Abs(s.z) > 1e-4f;
        }

        // ------------------------------------------------------------------ remote gloves

        class RemoteRenderer
        {
            public SkinnedMeshRenderer smr;
            public Transform[] bones;
            public Vector3[] curPos, targetPos;
            public Quaternion[] curRot, targetRot;
            public bool active, snap;
        }

        class RemoteHand
        {
            public int player;
            public bool left;
            public GameObject root;
            public RemoteRenderer[] renderers = new RemoteRenderer[MaxRenderers];
            public float lastUpdate;
        }

        readonly Dictionary<long, RemoteHand> _remotes = new Dictionary<long, RemoteHand>();
        readonly List<long> _toRemove = new List<long>();

        static long Key(int player, bool left) { return ((long)player << 1) | (left ? 1L : 0L); }

        void Apply(int player, HandUpdate u)
        {
            if (!ShowEnabled || player == GameManager.ID) return;
            var template = Template(u.left);
            if (template == null) return;

            RemoteHand hand;
            long key = Key(player, u.left);
            if (!_remotes.TryGetValue(key, out hand) || hand.root == null)
            {
                hand = new RemoteHand { player = player, left = u.left, root = new GameObject("GT3PoseKit_RemoteGlove_" + player + (u.left ? "L" : "R")) };
                DontDestroyOnLoad(hand.root);
                _remotes[key] = hand;
            }
            hand.lastUpdate = Time.unscaledTime;

            var shown = new bool[MaxRenderers];
            for (int i = 0; i < u.rendererIndex.Length; i++)
            {
                int idx = u.rendererIndex[i];
                if (idx >= template.Count) continue;
                var rr = hand.renderers[idx] ?? (hand.renderers[idx] = CreateRenderer(hand, template[idx], idx));
                if (rr == null || u.pos[i].Length != rr.bones.Length) continue; // a different glove version
                if (!rr.active) rr.snap = true;
                Array.Copy(u.pos[i], rr.targetPos, rr.bones.Length);
                Array.Copy(u.rot[i], rr.targetRot, rr.bones.Length);
                shown[idx] = true;
            }
            for (int i = 0; i < MaxRenderers; i++)
                if (hand.renderers[i] != null) hand.renderers[i].active = shown[i];
        }

        RemoteRenderer CreateRenderer(RemoteHand hand, RendererTemplate t, int index)
        {
            if (t.mesh == null) return null;
            var go = new GameObject("Renderer" + index);
            go.transform.SetParent(hand.root.transform, false);

            int n = t.boneScale.Length;
            var bones = new Transform[n];
            for (int i = 0; i < n; i++)
            {
                var b = new GameObject("Bone" + i).transform;
                b.SetParent(hand.root.transform, false); // flat: every bone is placed in world space each frame
                b.localScale = t.boneScale[i];
                bones[i] = b;
            }

            var smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.bones = bones;
            smr.sharedMesh = t.mesh;
            smr.sharedMaterials = t.materials;
            smr.updateWhenOffscreen = true;
            smr.shadowCastingMode = t.shadows;
            smr.receiveShadows = t.receiveShadows;
            smr.quality = t.quality;
            smr.enabled = false;

            var rr = new RemoteRenderer
            {
                smr = smr,
                bones = bones,
                curPos = new Vector3[n],
                targetPos = new Vector3[n],
                curRot = new Quaternion[n],
                targetRot = new Quaternion[n],
                snap = true
            };
            for (int i = 0; i < n; i++) { rr.curRot[i] = Quaternion.identity; rr.targetRot[i] = Quaternion.identity; }
            return rr;
        }

        void LateUpdate()
        {
            if (_remotes.Count == 0) return;
            float k = 1f - Mathf.Exp(-SmoothingRate * Time.deltaTime);

            _toRemove.Clear();
            foreach (var entry in _remotes)
            {
                var hand = entry.Value;
                PlayerManager player;
                if (hand.root == null || !GameManager.players.TryGetValue(hand.player, out player) || player == null)
                {
                    _toRemove.Add(entry.Key);
                    continue;
                }

                Transform anchor = hand.left ? player.leftHand : player.rightHand;
                bool visible = ShowEnabled && anchor != null && player.visible
                               && GameManager.playersPresent.Contains(hand.player)
                               && Time.unscaledTime - hand.lastUpdate < RemoteTimeout;

                foreach (var rr in hand.renderers)
                {
                    if (rr == null) continue;
                    bool on = visible && rr.active;
                    if (rr.smr.enabled != on) rr.smr.enabled = on;
                    if (!on) { rr.snap = true; continue; }

                    float t = rr.snap ? 1f : k;
                    rr.snap = false;
                    for (int b = 0; b < rr.bones.Length; b++)
                    {
                        rr.curPos[b] = Vector3.Lerp(rr.curPos[b], rr.targetPos[b], t);
                        rr.curRot[b] = Quaternion.Slerp(rr.curRot[b], rr.targetRot[b], t);
                        rr.bones[b].position = anchor.TransformPoint(rr.curPos[b]);
                        rr.bones[b].rotation = anchor.rotation * rr.curRot[b];
                    }
                }
            }
            foreach (var key in _toRemove) DestroyRemote(key);
        }

        void RemoveRemote(int player)
        {
            DestroyRemote(Key(player, true));
            DestroyRemote(Key(player, false));
        }

        void DestroyRemote(long key)
        {
            RemoteHand hand;
            if (!_remotes.TryGetValue(key, out hand)) return;
            _remotes.Remove(key);
            if (hand.root != null) Destroy(hand.root);
        }

        void ClearRemotes()
        {
            foreach (var hand in _remotes.Values) if (hand.root != null) Destroy(hand.root);
            _remotes.Clear();
        }

        void OnDestroy()
        {
            ClearRemotes();
        }
    }
}
