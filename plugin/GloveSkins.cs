using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx.Configuration;
using JerryGloves;
using UnityEngine;

namespace GT3PoseKit
{
    // Glove skins: put a new material on the GlovesTan3 gloves. GlovesTan3's own is Alloy/Core with a base colour
    // and normal map but an empty packed map (Metal R, AO G, Spec B, Rough A). PoseKit builds a new material (a
    // copy of the glove's, with the skin's maps replacing its own) or uses a ready-made one from an asset bundle.
    //
    // A skin is a folder in <pose folder>/skins/<name>/ holding either
    //   * texture files: names containing basecolor/albedo, packed/alloy, normal, emission (png or jpg), plus an
    //     optional skin.json for the shader, tint and extra values; or
    //   * an asset bundle (*.bundle, *.assetbundle or a file named "skin") containing a Material, built with
    //     Unity 5.6 (H3VR's version).
    [Serializable]
    public class SkinInfo
    {
        public string shader = "";               // empty: GlovesTan3's own shader
        public float[] color = { 1, 1, 1, 1 };   // tint (_Color)
        public float normalScale = 1f;           // _BumpScale
        public float[] emissionColor = { 1, 1, 1, 1 };
        public SkinFloat[] floats = new SkinFloat[0];        // any other shader values, by property name
        public string[] keywords = new string[0];             // shader keywords to switch on
        public bool fresh;                       // start from the shader's defaults, not a copy of the glove's material
    }

    [Serializable]
    public class SkinFloat
    {
        public string name;
        public float value;
    }

    public partial class PoseKitPlugin
    {
        // Property names tried for each map, Alloy's first, then Unity Standard's.
        static readonly string[] BaseProps = { "_MainTex" };
        static readonly string[] PackedProps = { "_SpecTex", "_MaterialMap", "_PackedMap", "_MetallicGlossMap" };
        static readonly string[] NormalProps = { "_BumpMap", "_NormalMap" };
        static readonly string[] EmissionProps = { "_EmissionMap", "_IncandescenceMap" };
        static readonly string[] ReportProps =
        {
            "_MainTex", "_Color", "_SpecTex", "_MaterialMap", "_PackedMap", "_MetallicGlossMap", "_BumpMap", "_BumpScale",
            "_NormalMap", "_EmissionMap", "_EmissionColor", "_IncandescenceMap", "_Metal", "_Metallic", "_Specularity",
            "_Roughness", "_Glossiness", "_Occlusion", "_OcclusionMap", "_Cutoff", "_DetailAlbedoMap", "_DetailNormalMap"
        };

        class GloveSkin
        {
            public string name, error;
            public SkinInfo info = new SkinInfo();
            public Texture2D baseColor, packed, normal, emission;
            public AssetBundle bundle;
            public Material bundleMat;
            public bool reported;
            public bool texturesFromBundle;   // textures belong to the bundle: unloaded with it, not destroyed
            public readonly Dictionary<Material, Material> built = new Dictionary<Material, Material>(); // GT3's material -> skin
        }

        ConfigEntry<string> _skinName;
        GloveSkin _skin;
        bool _skinDirty = true;
        bool _skinReported;
        readonly Dictionary<SkinnedMeshRenderer, Material[]> _skinOriginals = new Dictionary<SkinnedMeshRenderer, Material[]>();

        string SkinsDir { get { return Path.Combine(_dir, "skins"); } }

        void BindSkinConfig()
        {
            _skinName = Config.Bind("Glove skin", "Skin", "",
                "Skin to put on the gloves: a folder name in <pose folder>/skins, or a skin from another mod's GlovesTanSkins folder (empty: GlovesTan3's own look). See skins/README.txt.");
            _skinName.SettingChanged += (s, e) => { _skinDirty = true; };
            try
            {
                Directory.CreateDirectory(SkinsDir);
                var readme = Path.Combine(SkinsDir, "README.txt");
                if (!File.Exists(readme) || File.ReadAllText(readme) != SkinReadme) File.WriteAllText(readme, SkinReadme); // ours: keep it current
            }
            catch (Exception e) { Logger.LogWarning("Glove skins: could not create the skins folder: " + e.Message); }
        }

        // Every skin PoseKit knows about: name -> folder. Your own skins/ folder, plus skins other mods ship in a
        // "GlovesTanSkins" folder (found by ScanSkins).
        const string ExternalSkinsFolder = "GlovesTanSkins";
        readonly Dictionary<string, string> _skinDirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> _skinSource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // name -> mod folder
        bool _skinsScanned;

        List<string> SkinNames()
        {
            if (!_skinsScanned) ScanSkins();
            var list = new List<string>(_skinDirs.Keys);
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        // Rebuilds the skin list. Other mods' skins are found in any folder named GlovesTanSkins inside
        // BepInEx/plugins (up to a few levels down, e.g. plugins/Author-Mod/GlovesTanSkins). Each subfolder of it is a
        // skin; a GlovesTanSkins folder holding skin files directly is one skin named after its mod.
        // Returns how many skins came from other mods.
        int ScanSkins()
        {
            _skinsScanned = true;
            _skinDirs.Clear();
            _skinSource.Clear();
            try { foreach (var d in Directory.GetDirectories(SkinsDir)) _skinDirs[Path.GetFileName(d)] = d; }
            catch { }

            int external = 0;
            var found = new List<string>();
            try { FindSkinFolders(BepInEx.Paths.PluginPath, 0, found); }
            catch (Exception e) { Logger.LogWarning("Glove skins: could not search the plugins folder: " + e.Message); }
            foreach (var root in found)
            {
                string mod = ModFolderName(root);
                var dirs = new List<string>();
                if (IsSkinFolder(root)) dirs.Add(root);
                try { foreach (var d in Directory.GetDirectories(root)) if (IsSkinFolder(d)) dirs.Add(d); } catch { }
                foreach (var d in dirs)
                {
                    string name = d == root ? mod : Path.GetFileName(d);
                    if (_skinDirs.ContainsKey(name)) name = name + " (" + mod + ")";   // two skins with one name
                    if (_skinDirs.ContainsKey(name)) continue;
                    _skinDirs[name] = d;
                    _skinSource[name] = mod;
                    external++;
                }
            }
            Logger.LogInfo("Glove skins: " + _skinDirs.Count + " found (" + external + " from other mods)");
            return external;
        }

        static void FindSkinFolders(string dir, int depth, List<string> found)
        {
            if (depth > 3) return;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); } catch { return; }
            foreach (var d in subs)
            {
                if (string.Equals(Path.GetFileName(d), ExternalSkinsFolder, StringComparison.OrdinalIgnoreCase)) { found.Add(d); continue; }
                FindSkinFolders(d, depth + 1, found);
            }
        }

        // The mod's own folder under plugins (e.g. "Author-ModName"), for labels.
        static string ModFolderName(string skinsRoot)
        {
            var plugins = Path.GetFullPath(BepInEx.Paths.PluginPath).TrimEnd('\\', '/');
            var d = new DirectoryInfo(skinsRoot).Parent;
            while (d != null && d.Parent != null && !string.Equals(d.Parent.FullName.TrimEnd('\\', '/'), plugins, StringComparison.OrdinalIgnoreCase)) d = d.Parent;
            return d != null ? d.Name : "plugins";
        }

        static bool IsSkinFolder(string dir)
        {
            try
            {
                foreach (var f in Directory.GetFiles(dir))
                {
                    var fn = Path.GetFileName(f).ToLowerInvariant();
                    if (fn.EndsWith(".bundle") || fn.EndsWith(".assetbundle") || fn == "skin" || fn.EndsWith(".png") || fn.EndsWith(".jpg") || fn.EndsWith(".jpeg")) return true;
                }
            }
            catch { }
            return false;
        }

        void SearchSkins()
        {
            int external = ScanSkins();
            var mods = new List<string>();
            foreach (var m in _skinSource.Values) if (!mods.Contains(m)) mods.Add(m);
            if (_skinDirs.Count == 0) Notify("No glove skins found (looked in " + SkinsDir + " and every " + ExternalSkinsFolder + " folder in your mods)");
            else Notify("Found " + _skinDirs.Count + " glove skin(s)" + (external > 0 ? ", " + external + " from " + string.Join(", ", mods.ToArray()) : "") + ". Use Next glove skin to try them.");
            // The chosen skin may have just appeared (or moved): load it again.
            if (!string.IsNullOrEmpty(_skinName.Value) && (_skin == null || _skin.error != null)) _skinDirty = true;
        }

        string SkinDir(string name)
        {
            if (!_skinsScanned) ScanSkins();
            string d;
            return _skinDirs.TryGetValue(name, out d) ? d : Path.Combine(SkinsDir, name);
        }

        void NextSkin()
        {
            var names = SkinNames();
            names.Insert(0, "");
            int i = names.FindIndex(n => string.Equals(n, _skinName.Value, StringComparison.OrdinalIgnoreCase));
            _skinName.Value = names[(i + 1) % names.Count];
            _skinDirty = true;
            if (names.Count == 1) Notify("No glove skins found: add a folder to " + SkinsDir + ", or install a mod with a " + ExternalSkinsFolder + " folder and press Search for glove skins");
            else if (_skinName.Value == "") Notify("Glove skin: GlovesTan3's own");
        }

        // Called from LateUpdate whenever the hand list is refreshed, and right away after a change.
        void UpdateGloveSkin(bool systemsRefreshed)
        {
            if (_systems == null) return;
            if (!_skinReported) ReportGloveMaterial();
            if (_skinDirty)
            {
                _skinDirty = false;
                RestoreGloveMaterials();
                UnloadSkin();
                if (!string.IsNullOrEmpty(_skinName.Value)) _skin = LoadSkin(_skinName.Value.Trim());
                systemsRefreshed = true;
            }
            if (!systemsRefreshed || _skin == null || _skin.error != null) return;

            foreach (var sys in _systems)
            {
                if (sys == null) continue;
                foreach (var smr in GloveRenderers(sys))
                {
                    if (smr == null) continue;
                    Material[] orig;
                    if (!_skinOriginals.TryGetValue(smr, out orig)) { orig = smr.sharedMaterials; _skinOriginals[smr] = orig; }
                    var mats = new Material[orig.Length];
                    bool changed = false;
                    for (int i = 0; i < orig.Length; i++)
                    {
                        var sm = SkinFor(orig[i]);
                        if (sm != null) ReportSkinMaterial(sm);
                        mats[i] = sm ?? orig[i];
                        if (mats[i] != smr.sharedMaterials[i]) changed = true;
                    }
                    if (changed) smr.sharedMaterials = mats;
                }
            }
        }

        // Every glove mesh of one hand: the physics hand, the posed hand and anything else under the system.
        static List<SkinnedMeshRenderer> GloveRenderers(HandsPoseSystem sys)
        {
            var list = new List<SkinnedMeshRenderer>();
            foreach (var go in new[] { sys.gameObject, sys.HandPhys, sys.HandAnim })
            {
                if (go == null) continue;
                foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (smr != null && !list.Contains(smr)) list.Add(smr);
            }
            return list;
        }

        // The skin material standing in for one of GlovesTan3's materials (built once per original).
        Material SkinFor(Material original)
        {
            if (original == null || _skin == null) return null;
            if (_skin.bundleMat != null) return _skin.bundleMat;
            Material m;
            if (_skin.built.TryGetValue(original, out m)) return m;
            m = BuildSkinMaterial(original);
            _skin.built[original] = m;
            return m;
        }

        Material BuildSkinMaterial(Material original)
        {
            var info = _skin.info;
            Shader shader = null;
            if (!string.IsNullOrEmpty(info.shader))
            {
                shader = Shader.Find(info.shader);
                if (shader == null) Logger.LogWarning("Glove skin '" + _skin.name + "': shader '" + info.shader + "' not found, using the glove's own (" + original.shader.name + ")");
            }
            if (shader == null) shader = original.shader;

            // A new material. By default it starts as a copy of the glove's own (Alloy/Core with its base colour,
            // normal map and settings such as Roughness Source = Packed Map Alpha), and the skin's maps replace
            // those; so a skin with only a packed map gives the glove its missing metal/AO/spec/roughness.
            // "fresh": true in skin.json (or a different shader) starts from the shader's defaults instead.
            var m = info.fresh || shader != original.shader ? new Material(shader) : new Material(original);
            m.name = "PoseKit skin " + _skin.name;
            var set = new List<string>();
            var missing = new List<string>();
            Action<string, string[], Texture2D> tex = (label, props, t) =>
            {
                if (t == null) return;
                foreach (var p in props) if (m.HasProperty(p)) { m.SetTexture(p, t); set.Add(label + " -> " + p); return; }
                missing.Add(label);
            };
            tex("base colour", BaseProps, _skin.baseColor);
            tex("packed map", PackedProps, _skin.packed);
            tex("normal map", NormalProps, _skin.normal);
            tex("emission", EmissionProps, _skin.emission);

            if (m.HasProperty("_Color") && (info.fresh || info.color == null || info.color.Length < 3 || info.color[0] != 1 || info.color[1] != 1 || info.color[2] != 1 || (info.color.Length > 3 && info.color[3] != 1)))
                m.SetColor("_Color", ToColor(info.color, Color.white));
            if (_skin.normal != null)
            {
                if (m.HasProperty("_BumpScale")) m.SetFloat("_BumpScale", info.normalScale);
                m.EnableKeyword("_NORMALMAP");
            }
            if (_skin.emission != null)
            {
                if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", ToColor(info.emissionColor, Color.white));
                m.EnableKeyword("_EMISSION");
            }
            if (_skin.packed != null && m.HasProperty("_MetallicGlossMap")) m.EnableKeyword("_METALLICGLOSSMAP");
            foreach (var f in info.floats ?? new SkinFloat[0])
            {
                if (f == null || string.IsNullOrEmpty(f.name)) continue;
                if (m.HasProperty(f.name)) { m.SetFloat(f.name, f.value); set.Add(f.name + " = " + f.value); }
                else missing.Add(f.name);
            }
            foreach (var k in info.keywords ?? new string[0]) if (!string.IsNullOrEmpty(k)) m.EnableKeyword(k);

            Logger.LogInfo("Glove skin '" + _skin.name + "' on " + shader.name + ": " + (set.Count > 0 ? string.Join(", ", set.ToArray()) : "nothing set")
                           + (missing.Count > 0 ? "; this shader has no slot for: " + string.Join(", ", missing.ToArray()) : ""));
            return m;
        }

        static string TexName(Texture t) { return t != null ? "'" + t.name + "'" : "none"; }

        static Color ToColor(float[] c, Color fallback)
        {
            return c != null && c.Length >= 3 ? new Color(c[0], c[1], c[2], c.Length >= 4 ? c[3] : 1f) : fallback;
        }

        // ------------------------------------------------------------------ loading

        GloveSkin LoadSkin(string name)
        {
            var skin = new GloveSkin { name = name };
            var dir = SkinDir(name);
            if (!Directory.Exists(dir)) { skin.error = "folder not found"; Notify("Glove skin '" + name + "' not found (press Search for glove skins if it comes from another mod)"); return skin; }
            try
            {
                var json = Path.Combine(dir, "skin.json");
                if (File.Exists(json))
                {
                    var info = JsonUtility.FromJson<SkinInfo>(File.ReadAllText(json));
                    if (info != null) skin.info = info;
                }

                // A ready-made material in an asset bundle wins.
                foreach (var f in Directory.GetFiles(dir))
                {
                    var fn = Path.GetFileName(f).ToLowerInvariant();
                    if (!(fn.EndsWith(".bundle") || fn.EndsWith(".assetbundle") || fn == "skin")) continue;
                    skin.bundle = AssetBundle.LoadFromFile(f);
                    if (skin.bundle == null) { Logger.LogWarning("Glove skin '" + name + "': " + fn + " isn't an asset bundle this Unity version can read"); continue; }
                    var names = skin.bundle.GetAllAssetNames();
                    Logger.LogInfo("Glove skin '" + name + "': " + fn + " contains " + names.Length + " asset(s): "
                                   + string.Join(", ", names, 0, Math.Min(names.Length, 20)) + (names.Length > 20 ? ", ..." : ""));
                    var mats = skin.bundle.LoadAllAssets<Material>();
                    Material mat = mats != null && mats.Length > 0 ? mats[0] : null;
                    // No material of its own: a prefab or model in the bundle may carry one.
                    if (mat == null)
                        foreach (var go in skin.bundle.LoadAllAssets<GameObject>())
                        {
                            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                                foreach (var rm in r.sharedMaterials) if (rm != null) { mat = rm; break; }
                            if (mat != null) { Logger.LogInfo("Glove skin '" + name + "': no Material asset; using the material on '" + go.name + "'"); break; }
                        }
                    if (mat == null)
                    {
                        // Only textures: use them like a texture skin, matched by name. Textures imported in Unity
                        // already carry the right settings (sRGB/linear, normal map), so they're used as they are.
                        int n = 0;
                        foreach (var t in skin.bundle.LoadAllAssets<Texture2D>())
                        {
                            var key = t.name.ToLowerInvariant().Replace("_", "").Replace("-", "").Replace(" ", "");
                            if (skin.normal == null && key.Contains("normal")) { skin.normal = t; n++; }
                            else if (skin.packed == null && (key.Contains("packed") || key.Contains("alloy") || key.Contains("materialmap") || key.EndsWith("pm"))) { skin.packed = t; n++; }
                            else if (skin.emission == null && (key.Contains("emission") || key.Contains("emissive"))) { skin.emission = t; n++; }
                            else if (skin.baseColor == null && (key.Contains("basecolor") || key.Contains("albedo") || key.Contains("diffuse") || key.Contains("color"))) { skin.baseColor = t; n++; }
                        }
                        if (n > 0)
                        {
                            skin.texturesFromBundle = true;
                            Logger.LogInfo("Glove skin '" + name + "': no material in " + fn + "; building one from its textures (base colour "
                                           + TexName(skin.baseColor) + ", packed " + TexName(skin.packed) + ", normal " + TexName(skin.normal) + ", emission " + TexName(skin.emission) + ")");
                            break;
                        }
                        Logger.LogWarning("Glove skin '" + name + "': " + fn + " has no material, model or recognisable textures in it");
                        continue;
                    }
                    skin.bundleMat = mat;
                    if (skin.bundleMat.shader == null || !skin.bundleMat.shader.isSupported)
                        Logger.LogWarning("Glove skin '" + name + "': the bundle's shader isn't usable here; include the shader in the bundle");
                    Logger.LogInfo("Glove skin '" + name + "': using material '" + skin.bundleMat.name + "' (" + (skin.bundleMat.shader != null ? skin.bundleMat.shader.name : "no shader") + ") from " + fn);
                    break;
                }

                if (skin.bundleMat == null && !skin.texturesFromBundle)
                {
                    foreach (var f in Directory.GetFiles(dir))
                    {
                        var ext = Path.GetExtension(f).ToLowerInvariant();
                        if (ext != ".png" && ext != ".jpg" && ext != ".jpeg") continue;
                        var key = Path.GetFileNameWithoutExtension(f).ToLowerInvariant().Replace("_", "").Replace("-", "").Replace(" ", "");
                        if (skin.normal == null && key.Contains("normal")) skin.normal = LoadTexture(f, true, true);
                        else if (skin.packed == null && (key.Contains("packed") || key.Contains("alloy") || key.Contains("materialmap"))) skin.packed = LoadTexture(f, true, false);
                        else if (skin.emission == null && (key.Contains("emission") || key.Contains("emissive"))) skin.emission = LoadTexture(f, false, false);
                        else if (skin.baseColor == null && (key.Contains("basecolor") || key.Contains("albedo") || key.Contains("diffuse") || key.Contains("color"))) skin.baseColor = LoadTexture(f, false, false);
                    }
                    if (skin.baseColor == null && skin.packed == null && skin.normal == null && skin.emission == null)
                    {
                        skin.error = "no textures";
                        Notify("Glove skin '" + name + "' has no textures or bundle (see skins/README.txt)");
                        return skin;
                    }
                }
                string src; Notify("Glove skin '" + name + "' applied" + (_skinSource.TryGetValue(name, out src) ? " (from " + src + ")" : ""));
            }
            catch (Exception e)
            {
                skin.error = e.Message;
                Logger.LogError("Glove skin '" + name + "': " + e);
                Notify("Glove skin '" + name + "' could not be loaded: " + e.Message);
            }
            return skin;
        }

        // sRGB for colour maps, linear for data maps. Normal maps are swizzled into the layout Unity's
        // UnpackNormal reads on PC (X in alpha, Y in green), since they're loaded from plain image files.
        static Texture2D LoadTexture(string path, bool linear, bool normal)
        {
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = Path.GetFileName(path) };
            if (!t.LoadImage(File.ReadAllBytes(path))) throw new Exception("could not read " + Path.GetFileName(path));
            if (normal)
            {
                var px = t.GetPixels32();
                for (int i = 0; i < px.Length; i++) px[i].a = px[i].r;
                t.SetPixels32(px);
            }
            t.wrapMode = TextureWrapMode.Repeat;
            t.filterMode = FilterMode.Trilinear;
            t.anisoLevel = 4;
            t.Apply(true, true); // no longer readable: frees the CPU copy
            return t;
        }

        void RestoreGloveMaterials()
        {
            foreach (var kv in _skinOriginals) if (kv.Key != null) kv.Key.sharedMaterials = kv.Value;
            _skinOriginals.Clear();
        }

        void UnloadSkin()
        {
            if (_skin == null) return;
            foreach (var m in _skin.built.Values) if (m != null) Destroy(m);
            if (!_skin.texturesFromBundle)
                foreach (var t in new[] { _skin.baseColor, _skin.packed, _skin.normal, _skin.emission }) if (t != null) Destroy(t);
            if (_skin.bundle != null) _skin.bundle.Unload(true);
            _skin = null;
        }

        void ReloadSkin() { _skinDirty = true; _skinsScanned = false; }

        // Writes skins/glove-material.txt: the glove's own shader and which of the usual slots it has,
        // so skin makers know what the material looks like.
        void ReportGloveMaterial()
        {
            foreach (var sys in _systems)
            {
                if (sys == null) continue;
                var all = GloveRenderers(sys);
                if (all.Count == 0) continue;
                var smr = all[0];
                Material[] mats;
                if (!_skinOriginals.TryGetValue(smr, out mats)) mats = smr.sharedMaterials;
                if (mats == null || mats.Length == 0 || mats[0] == null) continue;
                _skinReported = true;
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("GlovesTan3 glove material, as found by PoseKit");
                    sb.AppendLine("Renderer: " + smr.name + ", " + mats.Length + " material slot(s)");
                    for (int i = 0; i < mats.Length; i++) DescribeMaterial(sb, mats[i], i);
                    File.WriteAllText(Path.Combine(SkinsDir, "glove-material.txt"), sb.ToString());
                    Logger.LogInfo("Glove skins: wrote the glove's material details to skins/glove-material.txt (shader '" + mats[0].shader.name + "')");
                }
                catch (Exception e) { Logger.LogWarning("Glove skins: could not write glove-material.txt: " + e.Message); }
                return;
            }
        }

        static void DescribeMaterial(StringBuilder sb, Material m, int i)
        {
            if (m == null) { sb.AppendLine("[" + i + "] none"); return; }
            sb.AppendLine();
            sb.AppendLine("[" + i + "] material '" + m.name + "', shader '" + (m.shader != null ? m.shader.name : "none") + "'"
                          + (m.shader != null && !m.shader.isSupported ? " (NOT SUPPORTED HERE)" : "") + ", render queue " + m.renderQueue);
            sb.AppendLine("  keywords: " + (m.shaderKeywords != null && m.shaderKeywords.Length > 0 ? string.Join(" ", m.shaderKeywords) : "(none)"));
            foreach (var p in ReportProps)
            {
                if (!m.HasProperty(p)) continue;
                sb.AppendLine("  " + p + ": " + DescribeProp(m, p));
            }
        }

        static string DescribeProp(Material m, string p)
        {
            if (p == "_BumpScale" || p.StartsWith("_Metal") || p == "_Specularity" || p == "_Roughness" || p == "_Glossiness" || p == "_Occlusion" || p == "_Cutoff")
                try { return "value " + m.GetFloat(p); } catch { }
            if (p.EndsWith("Color")) try { return "colour " + m.GetColor(p); } catch { }
            try
            {
                var t = m.GetTexture(p);
                return t != null ? "texture '" + t.name + "' " + t.width + "x" + t.height : "texture slot, empty";
            }
            catch { return "present"; }
        }

        // Writes skins/applied-material.txt for the material the skin actually put on the gloves, and logs
        // whether its packed map (Metal R, AO G, Spec B, Rough A) is filled.
        void ReportSkinMaterial(Material m)
        {
            if (m == null || _skin == null || _skin.reported) return;
            _skin.reported = true;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("Material PoseKit put on the gloves for skin '" + _skin.name + "'");
                DescribeMaterial(sb, m, 0);
                File.WriteAllText(Path.Combine(SkinsDir, "applied-material.txt"), sb.ToString()); // not in the skin's folder: it may belong to another mod
            }
            catch (Exception e) { Logger.LogWarning("Glove skin: could not write applied-material.txt: " + e.Message); }

            if (!m.HasProperty("_SpecTex")) { Logger.LogInfo("Glove skin '" + _skin.name + "': the material's shader has no Alloy packed map slot (_SpecTex)"); return; }
            var packed = m.GetTexture("_SpecTex");
            string scal = "";
            foreach (var p in new[] { "_Metal", "_Specularity", "_Roughness", "_Occlusion" })
                if (m.HasProperty(p)) scal += (scal == "" ? "" : ", ") + p.TrimStart('_') + " " + m.GetFloat(p);
            Logger.LogInfo("Glove skin '" + _skin.name + "': packed map (_SpecTex) " + (packed != null ? "is '" + packed.name + "' " + packed.width + "x" + packed.height : "is EMPTY")
                           + (scal != "" ? "; " + scal : "") + ". Details in skins/applied-material.txt");
        }

        const string SkinReadme =
@"PoseKit glove skins
===================

Each folder in here is one skin. Choose it with [Glove skin] Skin in gt3posekit.loader.cfg, or cycle through
them with the wrist menu button ""PoseKit: Next glove skin"". Skins from other mods are found too (see
""Releasing skins as your own mod"" below). ""PoseKit: Rescan Folders"" re-reads the files, so you
can export from your painting app and reload without restarting.

PoseKit puts a new material on the gloves. GlovesTan3's own is Alloy/Core with a base colour and a normal map,
but its packed map (Metal(R) AO(G) Spec(B) Rough(A)) is empty. A texture skin starts as a copy of that material and
replaces whichever maps you provide, so a folder with only a packed map fills in the missing one and keeps the
rest. There are two ways to make a skin:

1. Texture files (no Unity needed)
   Put PNG or JPG files in the folder. They're recognised by name:
     *basecolor* / *albedo* / *diffuse*  base colour (sRGB)
     *packed* / *alloy* / *materialmap*  Alloy packed map: R metallic, G ambient occlusion, B specularity,
                                         A roughness (linear)
     *normal*                            normal map (OpenGL / Unity style, Y+)
     *emission* / *emissive*             emission
   Use the same UV layout as the GlovesTan3 glove.

   Optional skin.json in the same folder:
     {
       ""shader"": """",                      (empty: the glove's own shader, see glove-material.txt)
       ""color"": [1, 1, 1, 1],             (tint)
       ""normalScale"": 1,
       ""emissionColor"": [1, 1, 1, 1],
       ""floats"": [ { ""name"": ""_Roughness"", ""value"": 1 } ],
       ""keywords"": [],
       ""fresh"": false                    (true: start from the shader's defaults instead of the glove's material)
     }

2. A ready-made material (Unity 5.6)
   Build an asset bundle containing your Material (with its shader) and put it in the folder as
   <anything>.bundle, <anything>.assetbundle, or a file named skin. PoseKit uses that material as it is.

Releasing skins as your own mod
-------------------------------
Skins don't have to live here. Ship them in your own mod inside a folder named GlovesTanSkins, one subfolder
per skin (same contents as above), for example in your Thunderstore zip:
    GlovesTanSkins/Camo/camo.bundle
    GlovesTanSkins/Desert/desert_basecolor.png ...
Once installed it ends up somewhere under BepInEx/plugins, and PoseKit finds every GlovesTanSkins folder there
(at game start, or with the wrist button ""PoseKit: Search for glove skins""). The skin's name is its subfolder
name; if two skins share a name, the mod's folder name is added in brackets. The mod needs no code. Players need
PoseKit to wear the skins, so list it as a dependency if you want mod managers to install it too. Don't set the
skin bundles up to be loaded by OtherLoader: PoseKit loads them itself, and Unity can't load one bundle twice.

applied-material.txt lists the material a skin actually put on the gloves.
glove-material.txt (written here when the gloves first appear) lists GlovesTan3's own shader and which of the
usual texture slots it has, so you can see what the textures will be plugged into. The BepInEx log also says
which slot each texture went to when a skin is applied.
";
    }
}
