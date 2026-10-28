using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FigCraft.Features.Capture;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace FigCraftToUnity.Editor
{
    /// <summary>
    /// Captures the game UI as it looks in this frame (Play Mode) and writes it as a snapshot
    /// (snapshot.json + PNGs) that the core (<see cref="CaptureCommand"/>) turns into editable layers
    /// in a FigCraft document. It reads what is on screen, not the prefabs: whatever is built,
    /// enabled or re-texted at runtime comes out as it is.
    ///
    /// Each image is baked with its final look (tint, 9-slice, partial fill, preserveAspect) at the
    /// size it takes on screen; text stays text. Whatever cannot be reproduced goes to
    /// the node's notes, never silently lost.
    /// </summary>
    public static class FigCraftCapture
    {
        internal const string PrefFolder = "FigCraftCapture.Folder";
        internal const string PrefTarget = "FigCraftCapture.Target";
        internal const string PrefFigCraftCli = "FigCraftCapture.FigCraftCli";
        const float FrameWidth = 1920f;
        const int MaxBake = 4096;

        internal static string Folder
        {
            get => EditorPrefs.GetString(PrefFolder,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FigCraftCaptures"));
            set => EditorPrefs.SetString(PrefFolder, value);
        }

        [MenuItem("Tools/FigCraft/Capture Game Screen %#&f", priority = 0)]
        static void CaptureMenu()
        {
            if (!EditorApplication.isPlaying)
                Debug.LogWarning("[FigCraft] Not in Play Mode: capturing the UI as it is in the Editor, not the running game.");
            Capture(null);
        }

        // Clean view: only the panel selected in the Hierarchy (and its children), without the 3D world or the rest of the UI.
        [MenuItem("Tools/FigCraft/Capture Selection Only %#&g", priority = 1)]
        static void CaptureSelectionMenu()
        {
            var only = Selection.activeTransform as RectTransform;
            if (only == null)
            {
                EditorUtility.DisplayDialog("FigCraft",
                    "Select the UI panel you want to capture in the Hierarchy.", "OK");
                return;
            }
            if (!EditorApplication.isPlaying)
                Debug.LogWarning("[FigCraft] Not in Play Mode: capturing the UI as it is in the Editor, not the running game.");
            Capture($"{SceneManager.GetActiveScene().name} · {only.name}", only);
        }

        [MenuItem("Tools/FigCraft/Choose Capture Folder...", priority = 20)]
        static void ChooseFolder()
        {
            var folder = EditorUtility.OpenFolderPanel("Capture folder", Folder, "");
            if (!string.IsNullOrEmpty(folder)) Folder = folder;
        }

        [MenuItem("Tools/FigCraft/Open Capture Folder", priority = 21)]
        static void OpenFolder()
        {
            Directory.CreateDirectory(Folder);
            EditorUtility.RevealInFinder(Folder);
        }

        [MenuItem("Tools/FigCraft/Set Up Automatic Import...", priority = 40)]
        static void ConfigureBuilder()
        {
            var target = EditorUtility.SaveFilePanel("FigCraft document that receives the captures",
                Folder, "game-ui", "figcraft");
            if (string.IsNullOrEmpty(target)) return;
            if (FindFigCraftCli() == null && !LocateFigCraftCli()) return;
            EditorPrefs.SetString(PrefTarget, target);
            Debug.Log($"[FigCraft] Every capture will be added to {target} (using {FindFigCraftCli()}).");
        }

        [MenuItem("Tools/FigCraft/Turn Off Automatic Import", priority = 41)]
        static void DisableBuilder()
        {
            EditorPrefs.DeleteKey(PrefTarget);
        }

        /// <summary>figcraft-cli, installed with the FigCraft app: the one chosen by hand, else the usual install
        /// places. Null if it cannot be found.</summary>
        internal static string FindFigCraftCli()
        {
            var chosen = EditorPrefs.GetString(PrefFigCraftCli, "");
            if (!string.IsNullOrEmpty(chosen) && File.Exists(chosen)) return chosen;
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[]
            {
                @"C:\Program Files\FigCraft\figcraft-cli.exe",
                Path.Combine(local, "Programs", "FigCraft", "figcraft-cli.exe"),
                "/Applications/FigCraft.app/Contents/MacOS/figcraft-cli",
                "/usr/local/bin/figcraft-cli",
                "/opt/homebrew/bin/figcraft-cli",
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        /// <summary>Asks for figcraft-cli by hand. False if cancelled.</summary>
        internal static bool LocateFigCraftCli()
        {
            EditorUtility.DisplayDialog("FigCraft",
                "figcraft-cli was not found. It is installed with the FigCraft app: point to it in the next window.", "OK");
            var cli = EditorUtility.OpenFilePanel("figcraft-cli (installed with FigCraft)", "", "");
            if (string.IsNullOrEmpty(cli)) return false;
            EditorPrefs.SetString(PrefFigCraftCli, cli);
            return true;
        }

        /// <summary>Captures and returns the folder written. <paramref name="name"/> names the frame in FigCraft.</summary>
        public static string Capture(string name, RectTransform only = null)
        {
            var scene = SceneManager.GetActiveScene().name;
            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            name = string.IsNullOrWhiteSpace(name) ? $"{scene} {DateTime.Now:HH:mm:ss}" : name.Trim();
            var dir = Path.Combine(Folder, $"{stamp}_{Slug(name)}");
            Directory.CreateDirectory(Path.Combine(dir, "images"));

            var capture = new Capturer(dir);
            var snapshot = capture.Run(name, scene, only);

            // The reference screenshot is written at the end of the frame; everything else is from this frame already.
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, snapshot.screenshot));
            // With the Editor in the background the Game view does not repaint and the screenshot is never written.
            EditorApplication.QueuePlayerLoopUpdate();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            File.WriteAllText(Path.Combine(dir, "snapshot.json"), JsonUtility.ToJson(snapshot, true), new UTF8Encoding(false));

            var noted = snapshot.nodes.Count(n => n.notes.Count > 0);
            Debug.Log($"[FigCraft] Captured '{name}': {snapshot.nodes.Count} layers, {capture.ImageCount} images, " +
                      $"{noted} with notes -> {dir}");

            if (!string.IsNullOrEmpty(EditorPrefs.GetString(PrefTarget, "")))
                RunBuilderWhenReady(dir, Path.Combine(dir, snapshot.screenshot));
            return dir;
        }

        static void RunBuilderWhenReady(string dir, string screenshot)
        {
            var started = EditorApplication.timeSinceStartup;
            void Tick()
            {
                var ready = File.Exists(screenshot);
                if (!ready && EditorApplication.timeSinceStartup - started < 10) return;
                EditorApplication.update -= Tick;
                RunBuilder(dir);
            }
            EditorApplication.update += Tick;
        }

        static void RunBuilder(string dir)
        {
            var target = EditorPrefs.GetString(PrefTarget, "");
            var cli = FindFigCraftCli();
            if (cli == null)
            {
                Debug.LogError("[FigCraft] figcraft-cli not found: run Tools > FigCraft > Set Up Automatic Import... again.");
                return;
            }

            // Automatic captures land on an inbox page; move them to their place in FigCraft.
            var options = new CaptureOptions
            {
                SnapshotPath = Path.Combine(dir, "snapshot.json"),
                FigCraftPath = File.Exists(target) ? target : null,
                OutPath = target,
                Page = "New captures",
                CliPath = cli,
                Log = new StringWriter(),
            };

            // figcraft-cli runs a few times: off the main thread so the Editor keeps responding.
            var task = Task.Run(() => CaptureCommand.Run(options));
            void Poll()
            {
                if (!task.IsCompleted) return;
                EditorApplication.update -= Poll;
                if (task.IsFaulted)
                    Debug.LogError($"[FigCraft] Import failed: {task.Exception?.GetBaseException().Message}\n{options.Log}");
                else if (task.Result != 0)
                    Debug.LogError($"[FigCraft] Import failed (code {task.Result})\n{options.Log}");
                else
                    Debug.Log($"[FigCraft] Added to {target}\n{options.Log}");
            }
            EditorApplication.update += Poll;
        }

        internal static string Slug(string s)
        {
            var normalized = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (var c in normalized)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
                sb.Append(char.IsLetterOrDigit(c) && c < 128 ? char.ToLowerInvariant(c) : '-');
            }
            var slug = string.Join("-", sb.ToString().Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries));
            return slug.Length > 48 ? slug.Substring(0, 48) : (slug.Length == 0 ? "capture" : slug);
        }

        // ---------------------------------------------------------------- model (contract with the add-on)

        [Serializable]
        public class Snapshot
        {
            public int version = 1;
            public string name;
            public string scene;
            public string capturedAt;
            public string unityVersion;
            public bool playMode;
            public int screenWidth;
            public int screenHeight;
            public float frameWidth;
            public float frameHeight;
            public float scale;
            public string screenshot = "screenshot.png";
            public string world;
            public List<Node> nodes = new List<Node>();
            public List<string> notes = new List<string>();
        }

        /// <summary>
        /// A GameObject with a RectTransform. Coordinates in the frame (1920 wide), absolute, top-left origin.
        /// kind: container | image | rect | text. A node with a graphic and children is built as a frame + a layer for the graphic.
        /// </summary>
        [Serializable]
        public class Node
        {
            public int id;
            public int parent = -1;
            public string name;
            public string path;
            public string kind = "container";
            public string canvas;
            public string renderMode;
            public float x, y, w, h;
            public float rotation;
            public float opacity = 1;
            public float graphicOpacity = 1;
            public bool visible = true;
            public bool clip;
            public string image;
            public string color;
            public string imageType;
            public float fillAmount = 1;
            public string text;
            public string fontFamily;
            public string fontStyle;
            public float fontSize;
            public string align;
            public string valign;
            public string textCase;
            public bool wrap;
            public float outlineWidth;
            public string outlineColor;
            public string[] components = new string[0];
            // Frame units per RectTransform unit (including its localScale): what the way back
            // FigCraft → game needs to convert positions, sizes and font sizes.
            public float unitScale;
            public float unitScaleX;
            public Vector2 localScale = Vector2.one;
            // RectTransform anchors: they become constraints in FigCraft, so resizing a parent
            // moves its children the same way the game does.
            public Vector2 anchorMin;
            public Vector2 anchorMax;
            [NonSerialized] public float lw, lh; // unrotated size, to bake rotated nodes
            public List<string> notes = new List<string>();
        }

        // ---------------------------------------------------------------- captura

        sealed class Capturer
        {
            readonly string _dir;
            readonly Dictionary<string, string> _baked = new Dictionary<string, string>();
            readonly Dictionary<Texture, Texture2D> _readable = new Dictionary<Texture, Texture2D>();
            readonly Vector3[] _corners = new Vector3[4];
            Snapshot _snap;
            float _scale;

            public int ImageCount => _baked.Count;

            public Capturer(string dir) { _dir = dir; }

            public Snapshot Run(string name, string scene, RectTransform only = null)
            {
                _scale = FrameWidth / Screen.width;
                _snap = new Snapshot
                {
                    name = name,
                    scene = scene,
                    capturedAt = DateTime.Now.ToString("s"),
                    unityVersion = Application.unityVersion,
                    playMode = EditorApplication.isPlaying,
                    screenWidth = Screen.width,
                    screenHeight = Screen.height,
                    frameWidth = FrameWidth,
                    frameHeight = Mathf.Round(Screen.height * _scale),
                    scale = _scale,
                };

                var canvases = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Exclude)
                    .Where(c => c.isRootCanvas && c.enabled)
                    .OrderBy(c => c.renderMode == RenderMode.ScreenSpaceOverlay ? 1 : 0)
                    .ThenBy(c => c.sortingLayerID == 0 ? 0 : SortingLayer.GetLayerValueFromID(c.sortingLayerID))
                    .ThenBy(c => c.sortingOrder)
                    .ToList();

                if (only != null)
                {
                    // Just that panel: no world background and no other canvases.
                    var root = only.GetComponentInParent<Canvas>();
                    if (root == null)
                    {
                        _snap.notes.Add($"'{only.name}' is not inside a Canvas: nothing to capture.");
                        return _snap;
                    }
                    _snap.notes.Add($"Capture of '{only.name}' only (no 3D world, no other UI).");
                    Visit(only, -1, root.rootCanvas);
                }
                else
                {
                    _snap.world = RenderWorld(canvases);
                    foreach (var canvas in canvases) Visit(canvas.transform, -1, canvas);
                }

                foreach (var t in _readable.Values) UnityEngine.Object.DestroyImmediate(t);
                if (Mathf.Abs(Screen.width / (float)Screen.height - 16f / 9f) > 0.01f)
                    _snap.notes.Add($"The Game view is not 16:9 ({Screen.width}x{Screen.height}); the frame is {FrameWidth}x{_snap.frameHeight}.");
                return _snap;
            }

            // The 3D world without any UI, as the frame background.
            string RenderWorld(List<Canvas> canvases)
            {
                var cam = Camera.main;
                if (cam == null)
                {
                    _snap.notes.Add("No Camera.main: the frame has no world background.");
                    return null;
                }

                var hidden = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Exclude)
                    .Where(c => c.enabled && c.renderMode != RenderMode.ScreenSpaceOverlay).ToList();
                var rt = RenderTexture.GetTemporary(Screen.width, Screen.height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                var previous = cam.targetTexture;
                try
                {
                    foreach (var c in hidden) c.enabled = false;
                    cam.targetTexture = rt;
                    cam.Render();
                }
                finally
                {
                    cam.targetTexture = previous;
                    foreach (var c in hidden) c.enabled = true;
                }

                var tex = ReadBack(rt, Screen.width, Screen.height);
                RenderTexture.ReleaseTemporary(rt);
                File.WriteAllBytes(Path.Combine(_dir, "world.png"), tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
                if (Camera.allCamerasCount > 1)
                    _snap.notes.Add($"{Camera.allCamerasCount} cameras are active; the background only shows {cam.name}.");
                return "world.png";
            }

            void Visit(Transform t, int parentId, Canvas rootCanvas)
            {
                if (!t.gameObject.activeInHierarchy) return;
                var rt = t as RectTransform;
                if (rt == null) return;

                var nested = t.GetComponent<Canvas>();
                if (nested != null && !nested.enabled) return;

                var cam = rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay
                    ? null
                    : rootCanvas.worldCamera != null ? rootCanvas.worldCamera : Camera.main;

                rt.GetWorldCorners(_corners);
                var screen = new Vector2[4];
                for (var i = 0; i < 4; i++)
                {
                    if (cam != null && cam.WorldToScreenPoint(_corners[i]).z < 0) return; // behind the camera
                    screen[i] = RectTransformUtility.WorldToScreenPoint(cam, _corners[i]);
                }

                var minX = screen.Min(p => p.x);
                var maxX = screen.Max(p => p.x);
                var minY = screen.Min(p => p.y);
                var maxY = screen.Max(p => p.y);
                var pxPerUnit = rt.rect.height > 0.001f ? Vector2.Distance(screen[0], screen[1]) / rt.rect.height : 0f;
                var pxPerUnitX = rt.rect.width > 0.001f ? Vector2.Distance(screen[0], screen[3]) / rt.rect.width : 0f;

                var node = new Node
                {
                    id = _snap.nodes.Count,
                    parent = parentId,
                    name = t.name,
                    path = PathOf(t),
                    canvas = rootCanvas.name,
                    renderMode = rootCanvas.renderMode.ToString(),
                    x = minX * _scale,
                    y = (Screen.height - maxY) * _scale,
                    w = (maxX - minX) * _scale,
                    h = (maxY - minY) * _scale,
                    components = t.GetComponents<Component>()
                        .Where(c => c != null && !(c is RectTransform) && !(c is CanvasRenderer))
                        .Select(c => c.GetType().Name).ToArray(),
                    lw = rt.rect.width * pxPerUnitX * _scale,
                    lh = rt.rect.height * pxPerUnit * _scale,
                    unitScale = pxPerUnit * _scale,
                    unitScaleX = pxPerUnitX * _scale,
                    localScale = new Vector2(t.localScale.x, t.localScale.y),
                    anchorMin = rt.anchorMin,
                    anchorMax = rt.anchorMax,
                };

                var angle = Mathf.Atan2(screen[3].y - screen[0].y, screen[3].x - screen[0].x) * Mathf.Rad2Deg;
                if (Mathf.Abs(angle) > 0.5f)
                {
                    node.rotation = angle;
                    node.notes.Add($"Rotated {angle:0.#}°: images are baked rotated inside their on-screen box; text is not rotated.");
                }
                if (rootCanvas.renderMode == RenderMode.WorldSpace)
                    node.notes.Add("World Space canvas: projected to the screen from " + (cam != null ? cam.name : "no camera") + ".");

                var group = t.GetComponent<CanvasGroup>();
                if (group != null && group.enabled)
                {
                    node.opacity = group.alpha;
                    if (group.alpha <= 0.001f) node.visible = false;
                }

                if (t.GetComponent<RectMask2D>() is RectMask2D rm && rm.enabled) node.clip = true;
                var mask = t.GetComponent<Mask>();
                if (mask != null && mask.enabled) node.clip = true;

                Describe(t, node, mask, pxPerUnit);

                _snap.nodes.Add(node);
                for (var i = 0; i < t.childCount; i++) Visit(t.GetChild(i), node.id, rootCanvas);
            }

            void Describe(Transform t, Node node, Mask mask, float pxPerUnit)
            {
                var graphic = t.GetComponent<Graphic>();
                if (graphic == null || !graphic.enabled) return;
                if (mask != null && mask.enabled && !mask.showMaskGraphic) return;

                var crossFade = graphic.canvasRenderer != null ? graphic.canvasRenderer.GetAlpha() : 1f;
                node.graphicOpacity = graphic.color.a * crossFade;
                if (graphic.canvasRenderer != null && graphic.canvasRenderer.cull)
                    node.notes.Add("Culled by a RectMask2D: not visible in the game.");

                foreach (var effect in t.GetComponents<BaseMeshEffect>())
                    if (effect.enabled) node.notes.Add($"Effect {effect.GetType().Name} not reproduced.");

                switch (graphic)
                {
                    case TMP_Text tmp:
                        DescribeTmp(tmp, node, pxPerUnit);
                        break;
                    case Text text:
                        DescribeText(text, node, pxPerUnit);
                        break;
                    case Image image:
                        DescribeImage(image, node, pxPerUnit);
                        break;
                    case RawImage raw:
                        DescribeRaw(raw, node);
                        break;
                    default:
                        node.notes.Add($"Graphic {graphic.GetType().Name} not supported: only its box is kept.");
                        break;
                }

                if (node.graphicOpacity <= 0.001f && t.childCount == 0) node.visible = false;
            }

            void DescribeTmp(TMP_Text tmp, Node node, float pxPerUnit)
            {
                node.kind = "text";
                node.text = tmp.GetParsedText();
                node.fontFamily = tmp.font != null ? tmp.font.faceInfo.familyName : null;
                var style = tmp.font != null ? tmp.font.faceInfo.styleName : "Regular";
                if ((tmp.fontStyle & FontStyles.Bold) != 0 && !style.Contains("Bold")) style = "Bold";
                if ((tmp.fontStyle & FontStyles.Italic) != 0) style = style == "Regular" ? "Italic" : style + " Italic";
                node.fontStyle = style;
                node.fontSize = tmp.fontSize * pxPerUnit * _scale;
                node.color = Hex(tmp.color, false);
                node.align = tmp.horizontalAlignment switch
                {
                    HorizontalAlignmentOptions.Center => "center",
                    HorizontalAlignmentOptions.Right => "right",
                    HorizontalAlignmentOptions.Justified => "justified",
                    HorizontalAlignmentOptions.Flush => "justified",
                    _ => "left",
                };
                node.valign = tmp.verticalAlignment switch
                {
                    VerticalAlignmentOptions.Middle => "middle",
                    VerticalAlignmentOptions.Geometry => "middle",
                    VerticalAlignmentOptions.Capline => "middle",
                    VerticalAlignmentOptions.Bottom => "bottom",
                    _ => "top",
                };
                if ((tmp.fontStyle & FontStyles.UpperCase) != 0) node.textCase = "upper";
                else if ((tmp.fontStyle & FontStyles.LowerCase) != 0) node.textCase = "lower";
                else if ((tmp.fontStyle & FontStyles.SmallCaps) != 0) node.textCase = "smallCaps";
                node.wrap = tmp.textWrappingMode == TextWrappingModes.Normal || tmp.textWrappingMode == TextWrappingModes.PreserveWhitespace;

                // TMP margins: the text lives inside the box minus the margin.
                var m = tmp.margin * pxPerUnit * _scale;
                if (m != Vector4.zero)
                {
                    node.x += m.x;
                    node.y += m.y;
                    node.w = Mathf.Max(1, node.w - m.x - m.z);
                    node.h = Mathf.Max(1, node.h - m.y - m.w);
                }

                if (tmp.enableAutoSizing) node.notes.Add($"Auto-size: resolved size {tmp.fontSize:0.#} (min {tmp.fontSizeMin:0.#}, max {tmp.fontSizeMax:0.#}).");
                if (Mathf.Abs(tmp.characterSpacing) > 0.01f) node.notes.Add($"characterSpacing {tmp.characterSpacing:0.#} not reproduced.");
                if (Mathf.Abs(tmp.lineSpacing) > 0.01f) node.notes.Add($"lineSpacing {tmp.lineSpacing:0.#} not reproduced.");
                if (tmp.enableVertexGradient) node.notes.Add("Vertex gradient not reproduced (base colour used).");
                if (tmp.richText && tmp.text != null && tmp.text.Contains("<")) node.notes.Add("Rich text: per-run styles flattened.");
                if (tmp.overflowMode != TextOverflowModes.Overflow) node.notes.Add($"Overflow {tmp.overflowMode}.");

                var mat = tmp.fontSharedMaterial;
                if (mat != null)
                {
                    if (mat.IsKeywordEnabled("OUTLINE_ON") || (mat.HasProperty("_OutlineWidth") && mat.GetFloat("_OutlineWidth") > 0.001f))
                    {
                        node.outlineWidth = mat.HasProperty("_OutlineWidth") ? mat.GetFloat("_OutlineWidth") : 0f;
                        node.outlineColor = mat.HasProperty("_OutlineColor") ? Hex(mat.GetColor("_OutlineColor"), true) : "#000000FF";
                        if (node.outlineWidth > 0.001f) node.notes.Add($"TMP outline (_OutlineWidth {node.outlineWidth:0.###}): approximated stroke.");
                    }
                    if (mat.IsKeywordEnabled("UNDERLAY_ON")) node.notes.Add("TMP underlay/shadow not reproduced.");
                }
            }

            void DescribeText(Text text, Node node, float pxPerUnit)
            {
                node.kind = "text";
                node.text = text.text;
                node.fontFamily = text.font != null ? text.font.name : null;
                node.fontStyle = text.fontStyle switch
                {
                    FontStyle.Bold => "Bold",
                    FontStyle.Italic => "Italic",
                    FontStyle.BoldAndItalic => "Bold Italic",
                    _ => "Regular",
                };
                var size = text.resizeTextForBestFit && text.cachedTextGenerator.characterCountVisible > 0
                    ? text.cachedTextGenerator.fontSizeUsedForBestFit / Mathf.Max(0.0001f, text.canvas != null ? text.canvas.scaleFactor : 1f)
                    : text.fontSize;
                node.fontSize = size * pxPerUnit * _scale;
                node.color = Hex(text.color, false);
                var a = text.alignment.ToString();
                node.align = a.EndsWith("Center") ? "center" : a.EndsWith("Right") ? "right" : "left";
                node.valign = a.StartsWith("Middle") ? "middle" : a.StartsWith("Lower") ? "bottom" : "top";
                node.wrap = text.horizontalOverflow == HorizontalWrapMode.Wrap;
                node.notes.Add("Legacy text (UnityEngine.UI.Text).");
            }

            void DescribeImage(Image image, Node node, float pxPerUnit)
            {
                var sprite = image.overrideSprite;
                node.imageType = image.type.ToString();
                if (sprite == null)
                {
                    node.kind = "rect";
                    node.color = Hex(image.color, false);
                    return;
                }

                node.kind = "image";
                node.fillAmount = image.type == Image.Type.Filled ? image.fillAmount : 1f;
                var w = Mathf.Clamp(Mathf.RoundToInt(node.w), 1, MaxBake);
                var h = Mathf.Clamp(Mathf.RoundToInt(node.h), 1, MaxBake);
                var rotated = Mathf.Abs(node.rotation) > 0.5f;
                var rw = rotated ? Mathf.Clamp(Mathf.RoundToInt(node.lw), 1, MaxBake) : w;
                var rh = rotated ? Mathf.Clamp(Mathf.RoundToInt(node.lh), 1, MaxBake) : h;
                var key = $"{sprite.GetEntityId()}|{node.rotation:0.0}|{image.color}|{image.type}|{w}x{h}|{image.fillAmount}|{image.fillMethod}|{image.fillOrigin}|{image.fillClockwise}|{image.preserveAspect}|{image.fillCenter}|{image.pixelsPerUnitMultiplier}";
                node.image = Bake(key, sprite.name, w, h, () =>
                {
                    var src = SpritePixels(sprite, out var sw, out var sh);
                    var dst = new Color32[rw * rh];

                    // Target pixels per sprite pixel: what scales the 9-slice borders.
                    var canvas = image.canvas;
                    var refPpu = canvas != null ? canvas.referencePixelsPerUnit : 100f;
                    var ppu = sprite.pixelsPerUnit * image.pixelsPerUnitMultiplier / refPpu;
                    var k = ppu > 0 ? pxPerUnit * _scale / ppu : 1f;

                    var sliced = (image.type == Image.Type.Sliced || image.type == Image.Type.Tiled) && sprite.border != Vector4.zero;
                    if (image.type == Image.Type.Tiled) node.notes.Add("Tiled image: baked as a stretched 9-slice.");
                    if (sliced)
                        NineSlice(src, sw, sh, dst, rw, rh, sprite.border, k, image.fillCenter);
                    else
                    {
                        var box = new RectInt(0, 0, rw, rh);
                        if (image.preserveAspect)
                        {
                            var aspect = sw / (float)sh;
                            var bw = Mathf.Min(rw, Mathf.RoundToInt(rh * aspect));
                            var bh = Mathf.Min(rh, Mathf.RoundToInt(rw / aspect));
                            box = new RectInt((rw - bw) / 2, (rh - bh) / 2, Mathf.Max(1, bw), Mathf.Max(1, bh));
                        }
                        Stretch(src, sw, sh, dst, rw, rh, box);
                    }

                    if (image.type == Image.Type.Filled && image.fillAmount < 0.999f)
                        ApplyFill(dst, rw, rh, image, node);
                    Tint(dst, image.color);
                    return rotated ? Rotate(dst, rw, rh, w, h, node.rotation) : dst;
                });
            }

            void DescribeRaw(RawImage raw, Node node)
            {
                if (raw.texture == null)
                {
                    node.kind = "rect";
                    node.color = Hex(raw.color, false);
                    return;
                }

                node.kind = "image";
                node.imageType = "Raw";
                var w = Mathf.Clamp(Mathf.RoundToInt(node.w), 1, MaxBake);
                var h = Mathf.Clamp(Mathf.RoundToInt(node.h), 1, MaxBake);
                var uv = raw.uvRect;
                var rotated = Mathf.Abs(node.rotation) > 0.5f;
                var rw = rotated ? Mathf.Clamp(Mathf.RoundToInt(node.lw), 1, MaxBake) : w;
                var rh = rotated ? Mathf.Clamp(Mathf.RoundToInt(node.lh), 1, MaxBake) : h;
                var key = $"raw{raw.texture.GetEntityId()}|{node.rotation:0.0}|{raw.color}|{w}x{h}|{uv}";
                if (raw.texture is RenderTexture) node.notes.Add("RawImage with a RenderTexture: content of this frame.");
                node.image = Bake(key, raw.texture.name, w, h, () =>
                {
                    var rt = RenderTexture.GetTemporary(rw, rh, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    Graphics.Blit(raw.texture, rt, new Vector2(uv.width, uv.height), new Vector2(uv.x, uv.y));
                    var tex = ReadBack(rt, rw, rh);
                    RenderTexture.ReleaseTemporary(rt);
                    var pixels = tex.GetPixels32();
                    UnityEngine.Object.DestroyImmediate(tex);
                    Tint(pixels, raw.color);
                    return rotated ? Rotate(pixels, rw, rh, w, h, node.rotation) : pixels;
                });
            }

            string Bake(string key, string label, int w, int h, Func<Color32[]> render)
            {
                if (_baked.TryGetValue(key, out var existing)) return existing;
                string hash;
                using (var sha = SHA1.Create())
                    hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "").Substring(0, 8).ToLowerInvariant();
                var file = $"images/{Slug(label)}_{w}x{h}_{hash}.png";

                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.SetPixels32(render());
                tex.Apply();
                File.WriteAllBytes(Path.Combine(_dir, file), tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
                _baked[key] = file;
                return file;
            }

            Color32[] SpritePixels(Sprite sprite, out int w, out int h)
            {
                // uGUI draws the sprite's full rect, transparent margins included; the texture only
                // stores textureRect (trimmed), placed at textureRectOffset inside that rect.
                w = Mathf.Max(1, Mathf.RoundToInt(sprite.rect.width));
                h = Mathf.Max(1, Mathf.RoundToInt(sprite.rect.height));
                Rect r;
                Vector2 offset;
                try
                {
                    r = sprite.textureRect;
                    offset = sprite.textureRectOffset;
                }
                catch (Exception)
                {
                    // Tight packing: there is no rectangular rect; the original rect is read.
                    r = sprite.rect;
                    offset = Vector2.zero;
                }

                var texture = sprite.texture;
                if (!_readable.TryGetValue(texture, out var readable))
                {
                    var rt = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    Graphics.Blit(texture, rt);
                    readable = ReadBack(rt, texture.width, texture.height);
                    RenderTexture.ReleaseTemporary(rt);
                    _readable[texture] = readable;
                }

                int rx = Mathf.RoundToInt(r.x), ry = Mathf.RoundToInt(r.y);
                int rw = Mathf.Clamp(Mathf.RoundToInt(r.width), 1, texture.width - rx);
                int rh = Mathf.Clamp(Mathf.RoundToInt(r.height), 1, texture.height - ry);
                var trimmed = readable.GetPixels32(0);
                var full = new Color32[w * h];
                int ox = Mathf.RoundToInt(offset.x), oy = Mathf.RoundToInt(offset.y);
                for (var y = 0; y < rh; y++)
                {
                    var dy = y + oy;
                    if (dy < 0 || dy >= h) continue;
                    for (var x = 0; x < rw; x++)
                    {
                        var dx = x + ox;
                        if (dx < 0 || dx >= w) continue;
                        full[dy * w + dx] = trimmed[(ry + y) * texture.width + rx + x];
                    }
                }
                return full;
            }

            static Texture2D ReadBack(RenderTexture rt, int w, int h)
            {
                var previous = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                RenderTexture.active = previous;
                return tex;
            }

            static Color32 Sample(Color32[] src, int sw, int sh, float x, float y)
            {
                x = Mathf.Clamp(x - 0.5f, 0, sw - 1);
                y = Mathf.Clamp(y - 0.5f, 0, sh - 1);
                int x0 = (int)x, y0 = (int)y;
                int x1 = Mathf.Min(x0 + 1, sw - 1), y1 = Mathf.Min(y0 + 1, sh - 1);
                float fx = x - x0, fy = y - y0;
                var a = Color32.Lerp(src[y0 * sw + x0], src[y0 * sw + x1], fx);
                var b = Color32.Lerp(src[y1 * sw + x0], src[y1 * sw + x1], fx);
                return Color32.Lerp(a, b, fy);
            }

            // Rotates the image (rw x rh, unrotated) around its centre into the w x h box it takes on screen.
            static Color32[] Rotate(Color32[] src, int rw, int rh, int w, int h, float degrees)
            {
                var dst = new Color32[w * h];
                var rad = degrees * Mathf.Deg2Rad;
                float cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);
                for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    // From the screen pixel to the unrotated local space (inverse rotation).
                    float px = x + 0.5f - w / 2f, py = y + 0.5f - h / 2f;
                    var lx = px * cos + py * sin + rw / 2f;
                    var ly = -px * sin + py * cos + rh / 2f;
                    if (lx < 0 || ly < 0 || lx >= rw || ly >= rh) continue;
                    dst[y * w + x] = Sample(src, rw, rh, lx, ly);
                }
                return dst;
            }

            static void Stretch(Color32[] src, int sw, int sh, Color32[] dst, int w, int h, RectInt box)
            {
                for (var y = box.yMin; y < box.yMax; y++)
                for (var x = box.xMin; x < box.xMax; x++)
                    dst[y * w + x] = Sample(src, sw, sh, (x - box.xMin + 0.5f) / box.width * sw, (y - box.yMin + 0.5f) / box.height * sh);
            }

            // 9-slice the way uGUI does it: borders at scale k, centre stretched; borders shrink if they do not fit.
            static void NineSlice(Color32[] src, int sw, int sh, Color32[] dst, int w, int h, Vector4 border, float k, bool fillCenter)
            {
                float L = border.x, B = border.y, R = border.z, T = border.w;
                var kx = (L + R) * k > w ? w / (L + R) : k;
                var ky = (B + T) * k > h ? h / (B + T) : k;

                float Map(float d, float size, float lo, float hi, float scale, int srcSize)
                {
                    if (d < lo * scale) return d / scale;
                    if (d >= size - hi * scale) return srcSize - (size - d) / scale;
                    var mid = size - (lo + hi) * scale;
                    return lo + (d - lo * scale) / Mathf.Max(0.0001f, mid) * (srcSize - lo - hi);
                }

                for (var y = 0; y < h; y++)
                {
                    var dy = y + 0.5f;
                    var inMidY = dy >= B * ky && dy < h - T * ky;
                    var sy = Map(dy, h, B, T, ky, sh);
                    for (var x = 0; x < w; x++)
                    {
                        var dx = x + 0.5f;
                        if (!fillCenter && inMidY && dx >= L * kx && dx < w - R * kx) continue;
                        dst[y * w + x] = Sample(src, sw, sh, Map(dx, w, L, R, kx, sw), sy);
                    }
                }
            }

            static void ApplyFill(Color32[] dst, int w, int h, Image image, Node node)
            {
                var amount = image.fillAmount;
                Func<int, int, bool> keep;
                switch (image.fillMethod)
                {
                    case Image.FillMethod.Horizontal:
                        keep = image.fillOrigin == 0 ? (x, y) => x + 0.5f <= w * amount : (Func<int, int, bool>)((x, y) => x + 0.5f >= w * (1 - amount));
                        break;
                    case Image.FillMethod.Vertical:
                        keep = image.fillOrigin == 0 ? (x, y) => y + 0.5f <= h * amount : (Func<int, int, bool>)((x, y) => y + 0.5f >= h * (1 - amount));
                        break;
                    case Image.FillMethod.Radial360:
                        var origin = new[] { -90f, 0f, 90f, 180f }[Mathf.Clamp(image.fillOrigin, 0, 3)];
                        var clockwise = image.fillClockwise;
                        keep = (x, y) =>
                        {
                            var a = Mathf.Atan2(y + 0.5f - h / 2f, x + 0.5f - w / 2f) * Mathf.Rad2Deg;
                            var delta = clockwise ? origin - a : a - origin;
                            delta = (delta % 360 + 360) % 360;
                            return delta <= amount * 360f;
                        };
                        break;
                    default:
                        node.notes.Add($"{image.fillMethod} fill at {amount:P0} not reproduced (full image).");
                        return;
                }

                for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    if (!keep(x, y)) dst[y * w + x] = new Color32(0, 0, 0, 0);
                node.notes.Add($"{image.fillMethod} fill at {amount:P0} baked: it changes at runtime in the game.");
            }

            // The colour's alpha goes to the layer opacity (editable); the RGB is baked.
            static void Tint(Color32[] pixels, Color color)
            {
                if (color.r > 0.999f && color.g > 0.999f && color.b > 0.999f) return;
                var c = (Color32)color;
                for (var i = 0; i < pixels.Length; i++)
                {
                    var p = pixels[i];
                    pixels[i] = new Color32((byte)(p.r * c.r / 255), (byte)(p.g * c.g / 255), (byte)(p.b * c.b / 255), p.a);
                }
            }

            static string Hex(Color c, bool withAlpha)
            {
                var c32 = (Color32)c;
                return withAlpha ? $"#{c32.r:X2}{c32.g:X2}{c32.b:X2}{c32.a:X2}" : $"#{c32.r:X2}{c32.g:X2}{c32.b:X2}";
            }

            static string PathOf(Transform t)
            {
                var parts = new List<string>();
                for (var cur = t; cur != null; cur = cur.parent)
                {
                    var label = cur.name;
                    var parent = cur.parent;
                    if (parent != null)
                    {
                        var same = 0;
                        var index = 0;
                        for (var i = 0; i < parent.childCount; i++)
                        {
                            var sibling = parent.GetChild(i);
                            if (sibling.name != cur.name) continue;
                            if (sibling == cur) index = same;
                            same++;
                        }
                        if (same > 1) label += $"[{index}]";
                    }
                    parts.Add(label);
                }
                parts.Reverse();
                return t.gameObject.scene.name + "/" + string.Join("/", parts);
            }
        }
    }
}
