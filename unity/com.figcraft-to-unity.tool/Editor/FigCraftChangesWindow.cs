using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using FigCraft.Features.Capture;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace FigCraftToUnity.Editor
{
    /// <summary>
    /// The way back FigCraft → game. Asks the add-on for the changes in the master document compared with the
    /// captures (figcraft-unity-features capture --changes) and applies them one by one: position and size,
    /// text, font size, alignment, colour and image. Applies with Undo and never saves the scene: that is up to you.
    /// Applying updates the capture with the new value so the change stops showing up.
    /// </summary>
    public class FigCraftChangesWindow : EditorWindow
    {
        [Serializable]
        class ChangeList
        {
            public string figcraft;
            public string generatedAt;
            public List<Change> changes = new List<Change>();
        }

        [Serializable]
        class Change
        {
            public string id, capture, snapshot;
            public int nodeId = -1;
            public string unityPath, scene, layer;
            public int layerId;
            public string property, before, after;
            public float dx, dy, dw, dh, number;
            public bool applicable;
            public string warning;

            [NonSerialized] public string status;
            [NonSerialized] public string hint;
            [NonSerialized] public bool sceneClosed;
        }

        // Not kept across recompiles: a saved list may belong to another document or to old captures.
        [NonSerialized] ChangeList _list;
        [NonSerialized] string _error;
        [NonSerialized] bool _compared;
        Vector2 _scroll;
        readonly HashSet<string> _ignored = new HashSet<string>();
        readonly Dictionary<string, bool> _folded = new Dictionary<string, bool>();
        bool _showInfo = true;

        [MenuItem("Tools/FigCraft/Design Changes...", priority = 2)]
        static void Open() => GetWindow<FigCraftChangesWindow>("FigCraft Changes");

        void OnEnable() => titleContent = new GUIContent("FigCraft Changes");

        void OnGUI()
        {
            // On open or after a recompile, the first repaint compares again.
            if (!_compared)
            {
                _compared = true;
                Refresh();
            }
            var target = EditorPrefs.GetString(FigCraftCapture.PrefTarget, "");
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("Find changes", EditorStyles.toolbarButton, GUILayout.Width(110))) Refresh();
                _showInfo = GUILayout.Toggle(_showInfo, "Show what cannot be applied", EditorStyles.toolbarButton);
                GUILayout.FlexibleSpace();
                GUILayout.Label(string.IsNullOrEmpty(target) ? "no design document" : Path.GetFileName(target), EditorStyles.miniLabel);
            }

            if (string.IsNullOrEmpty(target))
            {
                EditorGUILayout.HelpBox("Choose the design document first: Tools > FigCraft > Set Up Automatic Import...",
                    MessageType.Warning);
                return;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                EditorGUILayout.HelpBox("You are in Play Mode: anything applied now is lost when you stop. Leave Play Mode to apply.", MessageType.Warning);
            if (!string.IsNullOrEmpty(_error)) EditorGUILayout.HelpBox(_error, MessageType.Error);
            if (_list == null) return;
            if (!SamePath(_list.figcraft, target))
            {
                EditorGUILayout.HelpBox("This list belongs to another document. Press Find changes.", MessageType.Warning);
                return;
            }

            var visible = _list.changes.Where(c => !_ignored.Contains(c.id) && (c.applicable || _showInfo)).ToList();
            var pending = visible.Count(c => c.applicable && c.status == null);
            EditorGUILayout.LabelField($"{pending} change(s) to apply · compared {_list.generatedAt} with the captures in {FigCraftCapture.Folder}",
                EditorStyles.wordWrappedMiniLabel);
            if (visible.Count == 0)
            {
                EditorGUILayout.HelpBox("The design document matches the captures: nothing to bring into the game.", MessageType.Info);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var group in visible.GroupBy(c => c.capture))
            {
                _folded.TryGetValue(group.Key, out var folded);
                folded = !EditorGUILayout.Foldout(!folded, $"{group.Key}  ({group.Count()})", true, EditorStyles.foldoutHeader);
                _folded[group.Key] = folded;
                if (folded) continue;
                foreach (var change in group) DrawChange(change);
            }
            EditorGUILayout.EndScrollView();
        }

        void DrawChange(Change c)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(Label(c.property), EditorStyles.boldLabel, GUILayout.Width(120));
                    GUILayout.Label(c.unityPath ?? c.layer, EditorStyles.wordWrappedMiniLabel);
                }

                if (!string.IsNullOrEmpty(c.before) || !string.IsNullOrEmpty(c.after))
                {
                    EditorGUILayout.LabelField("Game", Short(c.before), EditorStyles.wordWrappedLabel);
                    EditorGUILayout.LabelField("FigCraft", Short(c.after), EditorStyles.wordWrappedLabel);
                }
                if (!string.IsNullOrEmpty(c.warning)) EditorGUILayout.HelpBox(c.warning, MessageType.Warning);
                if (!string.IsNullOrEmpty(c.hint)) EditorGUILayout.HelpBox(c.hint, MessageType.Info);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (c.status != null) GUILayout.Label(c.status, EditorStyles.miniBoldLabel);
                    GUILayout.FlexibleSpace();
                    if (c.sceneClosed && GUILayout.Button($"Open {c.scene}", GUILayout.Width(140))) OpenScene(c.scene);
                    if (!string.IsNullOrEmpty(c.unityPath) && GUILayout.Button("Select", GUILayout.Width(90))) Select(c);
                    if (GUILayout.Button("Ignore", GUILayout.Width(70))) _ignored.Add(c.id);
                    using (new EditorGUI.DisabledScope(!c.applicable || c.status != null || EditorApplication.isPlayingOrWillChangePlaymode))
                        if (GUILayout.Button("Apply", GUILayout.Width(70))) Apply(c);
                }
            }
        }

        // ------------------------------------------------------------------ lista

        void Refresh()
        {
            _error = null;
            _list = null; // an old list would point to captures that may no longer exist
            var target = EditorPrefs.GetString(FigCraftCapture.PrefTarget, "");
            if (string.IsNullOrEmpty(target)) return;
            if (!File.Exists(target))
            {
                _error = $"The design document {target} does not exist.";
                return;
            }

            var output = Path.Combine(Path.GetTempPath(), "figcraft-changes.json");
            var options = new ChangesOptions
            {
                OutPath = output,
                FigCraftPath = target,
                CapturesPath = FigCraftCapture.Folder,
                Log = new StringWriter(),
            };

            try
            {
                EditorUtility.DisplayProgressBar("FigCraft", "Comparing the design document with the captures...", 0.5f);
                // Reads the document directly, no figcraft-cli needed: fast enough to wait for.
                var code = ChangesCommand.Run(options);
                if (code != 0)
                {
                    _error = $"The comparison failed (code {code}):\n{options.Log}";
                    return;
                }
                _list = JsonUtility.FromJson<ChangeList>(File.ReadAllText(output, Encoding.UTF8));
                foreach (var c in _list.changes) Analyze(c);
            }
            catch (Exception ex)
            {
                _error = $"Could not compare: {ex.Message}";
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            Repaint();
        }

        // What only the project can tell: whether the scene is open, whether a layout overrides the position,
        // whether a script writes the text.
        void Analyze(Change c)
        {
            if (!c.applicable || string.IsNullOrEmpty(c.unityPath)) return;
            var target = Resolve(c, false);
            c.sceneClosed = target.SceneClosed;
            if (target.Problem != null)
            {
                c.hint = target.Problem;
                return;
            }
            if (target.PrefabPath != null)
            {
                c.hint = $"Created at runtime: the change goes to the prefab {target.PrefabPath}.";
                return;
            }

            var go = target.GameObject;
            switch (c.property)
            {
                case "rect":
                    var group = go.transform.parent != null ? go.transform.parent.GetComponent<LayoutGroup>() : null;
                    var fitter = go.GetComponent<ContentSizeFitter>();
                    if (group != null && group.enabled)
                        c.hint = $"The parent has a {group.GetType().Name} that will move this element back. Change the layout (padding, spacing) or add LayoutElement > Ignore Layout.";
                    else if (fitter != null && fitter.enabled)
                        c.hint = "It has a ContentSizeFitter: its content decides the size.";
                    break;
                case "text":
                    var writer = WhoReferences(go);
                    if (writer != null)
                        c.hint = $"{writer} references this text. If it fills it in at runtime, change the data on that component (Inspector) instead.";
                    break;
                case "fontSize":
                    var tmp = go.GetComponent<TMP_Text>();
                    if (tmp != null && tmp.enableAutoSizing)
                        c.hint = "It uses auto-size: the maximum size (Font Size Max) is changed.";
                    break;
                case "image":
                    var img = go.GetComponent<Image>();
                    if (img != null && (img.type == Image.Type.Sliced || img.type == Image.Type.Tiled) &&
                        img.sprite != null && img.sprite.border != Vector4.zero)
                        c.hint = "The current image is 9-sliced: set the borders of the new one in the Sprite Editor if it must stretch the same way.";
                    else if (img != null && img.color != Color.white)
                        c.hint = "The Image has a colour tint: it is kept on top of the new picture.";
                    break;
            }
        }

        // ------------------------------------------------------------------ aplicar

        void Apply(Change c)
        {
            var snapshot = LoadSnapshot(c.snapshot, out var node, c.nodeId);
            if (node == null)
            {
                c.status = "The node is not in the capture.";
                return;
            }
            if ((c.property == "rect" || c.property == "fontSize") && node.unitScale <= 0)
            {
                c.status = "Old capture without scale data: capture this screen again.";
                return;
            }

            var target = Resolve(c, true);
            if (target.Problem != null)
            {
                c.status = target.Problem;
                c.sceneClosed = target.SceneClosed;
                return;
            }

            try
            {
                var go = target.GameObject;
                var result = ApplyTo(go, c, node);
                if (result != null)
                {
                    c.status = result;
                    return;
                }

                if (target.PrefabPath != null)
                    PrefabUtility.SaveAsPrefabAsset(target.PrefabRoot, target.PrefabPath);
                else
                    EditorSceneManager.MarkSceneDirty(go.scene);

                UpdateSnapshot(c, snapshot, node);
                c.status = target.PrefabPath != null ? "Applied and saved in the prefab." : "Applied (save the scene to keep it).";
            }
            finally
            {
                if (target.PrefabRoot != null) PrefabUtility.UnloadPrefabContents(target.PrefabRoot);
            }
        }

        /// <summary>Returns a message if it could not be applied.</summary>
        static string ApplyTo(GameObject go, Change c, FigCraftCapture.Node node)
        {
            switch (c.property)
            {
                case "rect":
                {
                    var rt = go.GetComponent<RectTransform>();
                    if (rt == null) return "It has no RectTransform.";
                    // Deltas in frame units → parent units (position) and own units (size).
                    var parentX = node.unitScaleX / Mathf.Max(0.0001f, Mathf.Abs(node.localScale.x));
                    var parentY = node.unitScale / Mathf.Max(0.0001f, Mathf.Abs(node.localScale.y));
                    float dx = c.dx / parentX, dy = c.dy / parentY, dw = c.dw / parentX, dh = c.dh / parentY;
                    Record(rt, "Move from FigCraft");
                    // The box moves by its top-left corner and grows down and to the right;
                    // anchoredPosition is the pivot's (y up).
                    rt.anchoredPosition += new Vector2(dx + rt.pivot.x * dw, -dy - (1 - rt.pivot.y) * dh);
                    rt.sizeDelta += new Vector2(c.dw / node.unitScaleX, c.dh / node.unitScale);
                    Commit(rt);
                    return null;
                }
                case "text":
                {
                    var tmp = go.GetComponent<TMP_Text>();
                    if (tmp != null)
                    {
                        Record(tmp, "Text from FigCraft");
                        tmp.text = c.after;
                        Commit(tmp);
                        return null;
                    }
                    var text = go.GetComponent<Text>();
                    if (text == null) return "It has no TextMeshPro or Text.";
                    Record(text, "Text from FigCraft");
                    text.text = c.after;
                    Commit(text);
                    return null;
                }
                case "fontSize":
                {
                    var size = c.number / node.unitScale;
                    var tmp = go.GetComponent<TMP_Text>();
                    if (tmp != null)
                    {
                        Record(tmp, "Font size from FigCraft");
                        if (tmp.enableAutoSizing) tmp.fontSizeMax = size;
                        else tmp.fontSize = size;
                        Commit(tmp);
                        return null;
                    }
                    var text = go.GetComponent<Text>();
                    if (text == null) return "It has no TextMeshPro or Text.";
                    Record(text, "Font size from FigCraft");
                    if (text.resizeTextForBestFit) text.resizeTextMaxSize = Mathf.RoundToInt(size);
                    else text.fontSize = Mathf.RoundToInt(size);
                    Commit(text);
                    return null;
                }
                case "align":
                {
                    var tmp = go.GetComponent<TMP_Text>();
                    if (tmp == null) return "Only TextMeshPro alignment is supported.";
                    Record(tmp, "Alignment from FigCraft");
                    tmp.horizontalAlignment = c.after switch
                    {
                        "center" => HorizontalAlignmentOptions.Center,
                        "right" => HorizontalAlignmentOptions.Right,
                        "justified" => HorizontalAlignmentOptions.Justified,
                        _ => HorizontalAlignmentOptions.Left,
                    };
                    Commit(tmp);
                    return null;
                }
                case "color":
                {
                    if (!ColorUtility.TryParseHtmlString(c.after, out var color)) return $"Invalid colour: {c.after}";
                    var graphic = go.GetComponent<Graphic>();
                    if (graphic == null) return "It has no graphic to colour.";
                    Record(graphic, "Colour from FigCraft");
                    graphic.color = new Color(color.r, color.g, color.b, graphic.color.a); // alpha is the layer opacity
                    Commit(graphic);
                    return null;
                }
                case "image":
                {
                    if (string.IsNullOrEmpty(c.after) || !File.Exists(c.after))
                        return "The new image is missing: press Find Changes again.";
                    var image = go.GetComponent<Image>();
                    var raw = go.GetComponent<RawImage>();
                    if (image == null && raw == null) return "It has no Image or RawImage.";

                    var assetPath = ImportImage(c.after, go.name, image != null ? image.sprite : null);
                    if (image != null)
                    {
                        var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
                        if (sprite == null) return $"Could not import {assetPath} as a Sprite.";
                        Record(image, "Image from FigCraft");
                        image.sprite = sprite;
                        Commit(image);
                    }
                    else
                    {
                        var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                        if (texture == null) return $"Could not import {assetPath}.";
                        Record(raw, "Image from FigCraft");
                        raw.texture = texture;
                        Commit(raw);
                    }
                    return null;
                }
                default:
                    return $"'{c.property}' cannot be applied from here.";
            }
        }

        const string ImagesFolder = "Assets/FigCraft/Sprites";

        /// <summary>Copies the image that came out of FigCraft into Assets and imports it as a Sprite with the
        /// settings of the sprite it replaces (pixels per unit, filter, compression). Returns the asset path.</summary>
        static string ImportImage(string source, string objectName, Sprite previous)
        {
            Directory.CreateDirectory(ImagesFolder);
            var stem = Path.GetFileNameWithoutExtension(source);
            var fileName = FigCraftCapture.Slug(objectName) + "_" + stem.Substring(0, Math.Min(8, stem.Length)) +
                           Path.GetExtension(source).ToLowerInvariant();
            var assetPath = ImagesFolder + "/" + fileName;
            File.Copy(source, assetPath, true);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

            if (AssetImporter.GetAtPath(assetPath) is TextureImporter importer)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                var old = previous != null
                    ? AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(previous.texture)) as TextureImporter
                    : null;
                if (old != null)
                {
                    importer.spritePixelsPerUnit = old.spritePixelsPerUnit;
                    importer.filterMode = old.filterMode;
                    importer.textureCompression = old.textureCompression;
                    importer.maxTextureSize = Mathf.Max(old.maxTextureSize, 2048);
                }
                importer.SaveAndReimport();
            }
            return assetPath;
        }

        static void Record(UnityEngine.Object o, string label) => Undo.RecordObject(o, label);

        static void Commit(UnityEngine.Object o)
        {
            EditorUtility.SetDirty(o);
            if (PrefabUtility.IsPartOfPrefabInstance(o)) PrefabUtility.RecordPrefabInstancePropertyModifications(o);
        }

        // The capture now describes the game with the change in place, so it drops off the list.
        static void UpdateSnapshot(Change c, FigCraftCapture.Snapshot snapshot, FigCraftCapture.Node node)
        {
            switch (c.property)
            {
                case "rect":
                    node.x += c.dx;
                    node.y += c.dy;
                    node.w += c.dw;
                    node.h += c.dh;
                    break;
                case "text": node.text = c.after; break;
                case "fontSize": node.fontSize = c.number; break;
                case "align": node.align = c.after; break;
                case "color": node.color = c.after; break;
                case "image":
                    // The new picture becomes the captured one (same bytes = same hash as in FigCraft).
                    var folder = Path.Combine(Path.GetDirectoryName(c.snapshot) ?? ".", "images");
                    Directory.CreateDirectory(folder);
                    var file = "figcraft_" + Path.GetFileName(c.after);
                    File.Copy(c.after, Path.Combine(folder, file), true);
                    node.image = "images/" + file;
                    break;
            }
            File.WriteAllText(c.snapshot, JsonUtility.ToJson(snapshot, true), new UTF8Encoding(false));
        }

        static FigCraftCapture.Snapshot LoadSnapshot(string path, out FigCraftCapture.Node node, int id)
        {
            node = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var snapshot = JsonUtility.FromJson<FigCraftCapture.Snapshot>(File.ReadAllText(path, Encoding.UTF8));
            if (snapshot != null && id >= 0 && id < snapshot.nodes.Count) node = snapshot.nodes[id];
            return snapshot;
        }

        // ------------------------------------------------------------------ finding the object

        struct Target
        {
            public GameObject GameObject;
            public string PrefabPath;
            public GameObject PrefabRoot;
            public string Problem;
            public bool SceneClosed;
        }

        static readonly Regex Indexed = new Regex(@"^(.*)\[(\d+)\]$");

        /// <summary>
        /// The path is 'Scene/Root/Child[i]/…'. If a segment is '(Clone)', the object is instantiated at runtime and is
        /// looked up in its prefab; with <paramref name="loadPrefab"/> the prefab is opened for editing (unload it after).
        /// </summary>
        static Target Resolve(Change c, bool loadPrefab)
        {
            var parts = c.unityPath.Split('/');
            var sceneName = parts[0];
            var segments = parts.Skip(1).ToArray();

            var cloneAt = Array.FindIndex(segments, s => Name(s).EndsWith("(Clone)", StringComparison.Ordinal));
            if (cloneAt >= 0) return ResolveInPrefab(segments, cloneAt, loadPrefab);

            var roots = new List<GameObject>();
            if (sceneName == "DontDestroyOnLoad")
            {
                for (var i = 0; i < SceneManager.sceneCount; i++)
                    if (SceneManager.GetSceneAt(i).isLoaded) roots.AddRange(SceneManager.GetSceneAt(i).GetRootGameObjects());
            }
            else
            {
                var scene = SceneManager.GetSceneByName(sceneName);
                if (!scene.IsValid() || !scene.isLoaded)
                    return new Target { Problem = $"The scene {sceneName} is not open.", SceneClosed = true };
                roots.AddRange(scene.GetRootGameObjects());
            }

            var go = Walk(roots.Select(r => r.transform), segments, out var deepest);
            return go != null
                ? new Target { GameObject = go }
                : new Target { Problem = BuiltAtRuntime(segments, deepest) };
        }

        /// <summary>The object is not in the saved scene: a script creates (or renames) it at runtime. The last
        /// ancestor that does exist usually carries that script, so its components say where to look.</summary>
        static string BuiltAtRuntime(string[] segments, Transform deepest)
        {
            if (deepest == null)
                return "Not in the saved scene (created or renamed at runtime). Apply it by hand.";
            var scripts = deepest.GetComponents<MonoBehaviour>()
                .Where(b => b != null)
                .Select(b => b.GetType().Name)
                .Distinct()
                .ToArray();
            var depth = 0;
            for (var t = deepest; t != null; t = t.parent) depth++;
            var missing = Name(segments[Math.Min(depth, segments.Length - 1)]);
            return scripts.Length > 0
                ? $"'{missing}' is not in the saved scene: a script on '{deepest.name}' builds it at runtime " +
                  $"({string.Join(", ", scripts)}). Change it in that script or turn it into a prefab."
                : $"'{missing}' is not in the saved scene (created or renamed at runtime, inside '{deepest.name}'). Apply it by hand.";
        }

        static Target ResolveInPrefab(string[] segments, int cloneAt, bool load)
        {
            var prefabName = Name(segments[cloneAt]);
            prefabName = prefabName.Substring(0, prefabName.Length - "(Clone)".Length).Trim();
            var path = AssetDatabase.FindAssets($"t:Prefab {prefabName}")
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == prefabName);
            if (path == null) return new Target { Problem = $"Created at runtime from '{prefabName}', but that prefab was not found." };
            if (!load) return new Target { PrefabPath = path };

            var root = PrefabUtility.LoadPrefabContents(path);
            var go = Walk(new[] { root.transform }, new[] { prefabName }.Concat(segments.Skip(cloneAt + 1)).ToArray());
            if (go == null)
            {
                PrefabUtility.UnloadPrefabContents(root);
                return new Target { Problem = $"The object was not found inside the prefab {path}." };
            }
            return new Target { GameObject = go, PrefabPath = path, PrefabRoot = root };
        }

        static GameObject Walk(IEnumerable<Transform> level, string[] segments) => Walk(level, segments, out _);

        /// <summary><paramref name="deepest"/>: the last segment found (useful when the whole path does not exist).</summary>
        static GameObject Walk(IEnumerable<Transform> level, string[] segments, out Transform deepest)
        {
            Transform current = null;
            deepest = null;
            var candidates = level.ToList();
            foreach (var segment in segments)
            {
                var name = Name(segment);
                var index = Index(segment);
                var matches = candidates.Where(t => t.name == name).ToList();
                if (index >= matches.Count) return null;
                current = matches[index];
                deepest = current;
                candidates = Enumerable.Range(0, current.childCount).Select(current.GetChild).ToList();
            }
            return current != null ? current.gameObject : null;
        }

        static string Name(string segment)
        {
            var m = Indexed.Match(segment);
            return m.Success ? m.Groups[1].Value : segment;
        }

        static int Index(string segment)
        {
            var m = Indexed.Match(segment);
            return m.Success ? int.Parse(m.Groups[2].Value) : 0;
        }

        // Which component in the scene holds a reference to this object or its components?
        static string WhoReferences(GameObject go)
        {
            var targets = new HashSet<UnityEngine.Object>(go.GetComponents<Component>()) { go };
            foreach (var root in go.scene.GetRootGameObjects())
            foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null || behaviour.gameObject == go) continue;
                for (var type = behaviour.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType)) continue;
                    if (field.GetValue(behaviour) is UnityEngine.Object value && targets.Contains(value))
                        return $"{behaviour.GetType().Name}.{field.Name} (en {behaviour.name})";
                }
            }
            return null;
        }

        static void Select(Change c)
        {
            var target = Resolve(c, false);
            if (target.GameObject != null)
            {
                Selection.activeGameObject = target.GameObject;
                EditorGUIUtility.PingObject(target.GameObject);
            }
            else if (target.PrefabPath != null)
                EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(target.PrefabPath));
        }

        void OpenScene(string sceneName)
        {
            var path = AssetDatabase.FindAssets($"t:Scene {sceneName}")
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == sceneName);
            if (path == null) return;
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.OpenScene(path);
            foreach (var c in _list.changes) { c.hint = null; c.sceneClosed = false; Analyze(c); }
        }

        static string Label(string property) => property switch
        {
            "rect" => "Position / size",
            "text" => "Text",
            "fontSize" => "Font size",
            "align" => "Alignment",
            "color" => "Colour",
            "image" => "Image",
            "font" => "Font",
            "added" => "New layer",
            "removed" => "Deleted layer",
            "nocapture" => "No capture",
            _ => property,
        };

        static bool SamePath(string a, string b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        static string Short(string s) => string.IsNullOrEmpty(s) ? "—" : s.Length > 300 ? s.Substring(0, 300) + "…" : s;
    }
}
