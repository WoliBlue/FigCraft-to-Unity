using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FigCraft.Features.Capture
{
    public sealed class ChangesOptions
    {
        public string FigCraftPath;
        public string CapturesPath;
        public string OutPath;
        /// <summary>Where the summary goes (the console by default; Unity passes its own writer).</summary>
        public TextWriter Log;
        internal TextWriter Output => Log ?? Console.Out;

        public static ChangesOptions Parse(Cli cli) => new ChangesOptions
        {
            OutPath = cli.Require("changes"),
            FigCraftPath = cli.Require("figcraft"),
            CapturesPath = cli.Require("captures"),
        };
    }

    /// <summary>
    /// One edit made in FigCraft to a layer that came from the game. Values that move or resize are deltas in
    /// frame units; the Unity side converts them with the node's unitScale from the snapshot. Flat on purpose:
    /// Unity reads it with JsonUtility.
    /// </summary>
    public sealed class LayerChange
    {
        public string Id;
        public string Capture;
        public string Snapshot;
        public int NodeId = -1;
        public string UnityPath;
        public string Scene;
        public string Layer;
        public int LayerId;
        public string Property;   // rect | text | fontSize | color | align | image | added | removed | font | nocapture
        public string Before;
        public string After;
        public double Dx, Dy, Dw, Dh;
        public double Number;
        public bool Applicable = true;
        public string Warning;
    }

    /// <summary>
    /// Compares the master document with the captures its frames came from and lists what was edited:
    /// position and size, text, font size, alignment, colour and image. Layers are matched by unity.path in pluginData;
    /// the frame by unity.capture. Structure changes (new or deleted layers) are listed but cannot be applied.
    /// </summary>
    public static class ChangesCommand
    {
        const double Tolerance = 0.5;

        public static int Run(ChangesOptions options)
        {
            var index = DocumentIndex.Load(options.FigCraftPath);
            var snapshots = LoadSnapshots(options.CapturesPath);
            var changes = new List<LayerChange>();

            var frames = index.Nodes.Where(n => Data(n, "unity.screen") != null && Data(n, "unity.capture") != null).ToList();
            foreach (var frame in frames)
            {
                var name = Data(frame, "unity.capture");
                if (!snapshots.TryGetValue(name, out var entry))
                {
                    changes.Add(new LayerChange
                    {
                        Id = $"{name}|missing", Capture = name, Layer = frame.Name, LayerId = frame.Id,
                        Property = "nocapture", Applicable = false,
                        Warning = $"No capture named '{name}' in {options.CapturesPath}: this frame cannot be compared.",
                    });
                    continue;
                }
                Compare(index, frame, entry.snapshot, entry.path, options.FigCraftPath, changes);
            }

            var json = JsonConvert.SerializeObject(new
            {
                figcraft = Path.GetFullPath(options.FigCraftPath),
                generatedAt = DateTime.Now.ToString("s", CultureInfo.InvariantCulture),
                changes = changes.Select(ToJson).ToList(),
            }, Formatting.Indented);
            File.WriteAllText(options.OutPath, json, new UTF8Encoding(false));

            options.Output.WriteLine($"frames    {frames.Count}");
            options.Output.WriteLine($"changes   {changes.Count(c => c.Applicable)} applicable, {changes.Count(c => !c.Applicable)} informative");
            foreach (var c in changes)
                options.Output.WriteLine($"  {(c.Applicable ? "~" : "!")} {c.Capture} | {c.UnityPath ?? c.Layer} | {c.Property}: {c.Before} -> {c.After}");
            options.Output.WriteLine($"wrote     {options.OutPath}");
            return 0;
        }

        static Dictionary<string, (CaptureSnapshot snapshot, string path)> LoadSnapshots(string folder)
        {
            var result = new Dictionary<string, (CaptureSnapshot, string)>(StringComparer.Ordinal);
            foreach (var file in Directory.GetFiles(folder, "snapshot.json", SearchOption.AllDirectories).OrderBy(f => f))
            {
                CaptureSnapshot snapshot;
                try { snapshot = CaptureSnapshot.Load(file); }
                catch (Exception ex) when (ex is InvalidDataException || ex is JsonException) { continue; }
                // A later capture with the same name wins (folders are named by date).
                result[snapshot.Name] = (snapshot, Path.GetFullPath(file));
            }
            return result;
        }

        static void Compare(DocumentIndex index, DocNode frame, CaptureSnapshot snapshot, string snapshotPath,
            string figcraftPath, List<LayerChange> changes)
        {
            var byPath = snapshot.Nodes.Where(n => n.Path != null).ToDictionary(n => n.Path, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var layer in Descendants(index, frame.Id))
            {
                var path = Data(layer, "unity.path");
                var role = Data(layer, "unity.role");
                if (path == null)
                {
                    if (role == null && Data(layer, "unity.capture") == null)
                        changes.Add(Info(snapshot, snapshotPath, layer, "added",
                            "New layer in FigCraft: it does not exist in the game. Create it by hand."));
                    continue;
                }
                if (!byPath.TryGetValue(path, out var node))
                {
                    changes.Add(Info(snapshot, snapshotPath, layer, "added",
                        $"The capture has no '{path}': was this layer copied from another screen?"));
                    continue;
                }
                seen.Add(path);

                var kind = Data(layer, "unity.kind");
                var parent = layer.Parent.HasValue ? index.Node(layer.Parent.Value) : null;
                var ownsRect = kind == "container" || Data(parent, "unity.path") != path;

                LayerChange Change(string property, string before, string after) => new LayerChange
                {
                    Id = $"{snapshot.Name}|{path}|{property}",
                    Capture = snapshot.Name, Snapshot = snapshotPath, NodeId = node.Id, UnityPath = path,
                    Scene = snapshot.Scene, Layer = layer.Name, LayerId = layer.Id, Property = property,
                    Before = before, After = after,
                };

                if (ownsRect)
                {
                    // A parent edited in FigCraft drags its children through their constraints (= Unity anchors),
                    // and applying the parent in Unity does the same. So the child is compared with where the game
                    // would put it after the parent's change; only what is left is the child's own edit.
                    var anchor = RectAncestor(index, layer, frame.Id);
                    var anchorPath = Data(anchor, "unity.path");
                    var parentNode = anchorPath != null && byPath.TryGetValue(anchorPath, out var an) ? an : null;
                    var (px, py) = anchor != null && anchor.Id != frame.Id ? Absolute(index, anchor, frame.Id) : (0, 0);
                    double pdw = 0, pdh = 0;
                    if (parentNode != null && anchor != null)
                    {
                        pdw = (anchor.Width ?? 0) - Math.Max(1, parentNode.W);
                        pdh = (anchor.Height ?? 0) - Math.Max(1, parentNode.H);
                    }
                    double minX = node.AnchorMin?.X ?? 0, maxX = node.AnchorMax?.X ?? 0;
                    double minY = node.AnchorMin?.Y ?? 1, maxY = node.AnchorMax?.Y ?? 1; // without anchors: top-left
                    var expectedX = px + (node.X - (parentNode?.X ?? 0)) + minX * pdw;
                    var expectedY = py + (node.Y - (parentNode?.Y ?? 0)) + (1 - maxY) * pdh;
                    // The build never makes a layer smaller than 1 unit; compare against what it wrote.
                    var expectedW = Math.Max(1, node.W) + (maxX - minX) * pdw;
                    var expectedH = Math.Max(1, node.H) + (maxY - minY) * pdh;

                    var (lx, ly) = Absolute(index, layer, frame.Id);
                    var lw = layer.Width ?? 0;
                    var lh = layer.Height ?? 0;
                    // The node's own edit, expressed from its captured box.
                    var x = node.X + (lx - expectedX);
                    var y = node.Y + (ly - expectedY);
                    var w = Math.Max(1, node.W) + (lw - expectedW);
                    var h = Math.Max(1, node.H) + (lh - expectedH);
                    if (Math.Abs(x - node.X) > Tolerance || Math.Abs(y - node.Y) > Tolerance ||
                        Math.Abs(w - Math.Max(1, node.W)) > Tolerance || Math.Abs(h - Math.Max(1, node.H)) > Tolerance)
                    {
                        var c = Change("rect", Rect(node.X, node.Y, node.W, node.H), Rect(x, y, w, h));
                        c.Dx = x - node.X;
                        c.Dy = y - node.Y;
                        c.Dw = w - node.W;
                        c.Dh = h - node.H;
                        if (Math.Abs(node.Rotation) > 0.5)
                        {
                            c.Applicable = false;
                            c.Warning = "Rotated element: moving or resizing from its on-screen box is not reliable.";
                        }
                        else if (node.RenderMode == "WorldSpace" && IsCanvasRoot(node))
                        {
                            // The canvas itself sits in the 3D world: its screen box depends on the camera.
                            c.Applicable = false;
                            c.Warning = "World Space canvas: the on-screen position depends on the camera; move it in the scene.";
                        }
                        else if (node.RenderMode == "WorldSpace")
                        {
                            // Inside the canvas the units are the canvas's own (each node keeps its px per unit),
                            // so the edit converts like on a screen canvas, as long as it faces the camera.
                            c.Warning = "Inside a World Space canvas: converted with the scale it had on screen. " +
                                        "If the canvas does not face the camera, the result is approximate.";
                        }
                        changes.Add(c);
                    }
                }

                if (kind == "text" && layer.Type == "text")
                {
                    var text = layer.Raw.Value<string>("characters") ?? "";
                    if (!string.Equals(Normalize(text), Normalize(node.Text ?? ""), StringComparison.Ordinal))
                    {
                        var c = Change("text", node.Text ?? "", text);
                        if (node.Notes.Any(n => n.StartsWith("Rich text", StringComparison.Ordinal)))
                            c.Warning = "The original text has rich text tags; applying replaces them with plain text.";
                        changes.Add(c);
                    }

                    var style = layer.Raw["style"] as JObject;
                    var size = style?.Value<double?>("font_size");
                    if (size.HasValue && Math.Abs(size.Value - Math.Max(1, node.FontSize)) > 0.25)
                    {
                        var c = Change("fontSize", Num(node.FontSize), Num(size.Value));
                        c.Number = size.Value;
                        changes.Add(c);
                    }

                    var family = style?.Value<string>("font_family");
                    if (!string.IsNullOrEmpty(family) && !string.IsNullOrEmpty(node.FontFamily) &&
                        !string.Equals(family, node.FontFamily, StringComparison.Ordinal))
                    {
                        var c = Change("font", node.FontFamily, family);
                        c.Applicable = false;
                        c.Warning = "Changing the font needs a TMP Font Asset: pick it by hand on the component.";
                        changes.Add(c);
                    }

                    var align = layer.Raw.Value<string>("align_h") ?? "left";
                    var before = node.Align ?? "left";
                    if (!string.Equals(align, before, StringComparison.Ordinal))
                        changes.Add(Change("align", before, align));
                }

                // Image swapped in FigCraft: the fill names its file by the SHA-256 of its bytes, and so does every
                // image the capture baked. A different hash is a different picture; it is copied out of the
                // document next to the capture so Unity can import it as a sprite.
                if (kind == "image" && layer.Type != "frame" && !string.IsNullOrEmpty(node.Image))
                {
                    var hash = ImageHash(layer.Raw);
                    var captured = Sha256Of(snapshot.FileOf(node.Image));
                    if (hash != null && captured != null && !string.Equals(hash, captured, StringComparison.OrdinalIgnoreCase))
                    {
                        var folder = Path.Combine(Path.GetDirectoryName(snapshotPath) ?? ".", "figcraft-images");
                        var file = ExtractImage(figcraftPath, hash, folder);
                        var c = Change("image", node.Image, file ?? hash);
                        if (file == null)
                        {
                            c.Applicable = false;
                            c.Warning = "The new image is not inside the .figcraft file: save the master in FigCraft and look again.";
                        }
                        changes.Add(c);
                    }
                }

                if ((kind == "text" || kind == "rect") && layer.Type != "frame")
                {
                    var color = SolidColor(layer.Raw);
                    if (color != null && node.Color != null &&
                        !string.Equals(color, node.Color, StringComparison.OrdinalIgnoreCase))
                        changes.Add(Change("color", node.Color.ToUpperInvariant(), color));
                }
            }

            // Layers deleted in FigCraft: only informative, deleting GameObjects is a decision for the game.
            foreach (var node in snapshot.Nodes.Where(n => n.Path != null && !seen.Contains(n.Path)))
                changes.Add(new LayerChange
                {
                    Id = $"{snapshot.Name}|{node.Path}|removed", Capture = snapshot.Name, Snapshot = snapshotPath,
                    NodeId = node.Id, UnityPath = node.Path, Scene = snapshot.Scene, Layer = node.Name,
                    Property = "removed", Applicable = false,
                    Warning = "Deleted in FigCraft: it still exists in the game (disable or delete it by hand if needed).",
                });
        }

        static LayerChange Info(CaptureSnapshot snapshot, string snapshotPath, DocNode layer, string property, string warning) =>
            new LayerChange
            {
                Id = $"{snapshot.Name}|#{layer.Id}|{property}", Capture = snapshot.Name, Snapshot = snapshotPath,
                Scene = snapshot.Scene, Layer = layer.Name, LayerId = layer.Id, Property = property,
                Applicable = false, Warning = warning,
            };

        static IEnumerable<DocNode> Descendants(DocumentIndex index, int root)
        {
            var children = index.Nodes.Where(n => n.Parent.HasValue).ToLookup(n => n.Parent.Value);
            var stack = new Stack<int>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                foreach (var child in children[id].OrderByDescending(c => c.Id))
                {
                    yield return child;
                    stack.Push(child.Id);
                }
            }
        }

        static DocNode RectAncestor(DocumentIndex index, DocNode layer, int frameId)
        {
            var path = Data(layer, "unity.path");
            for (var cur = layer.Parent.HasValue ? index.Node(layer.Parent.Value) : null; cur != null;
                 cur = cur.Parent.HasValue ? index.Node(cur.Parent.Value) : null)
            {
                if (cur.Id == frameId) return cur;
                var p = Data(cur, "unity.path");
                if (p != null && p != path) return cur;
            }
            return null;
        }

        // Position of a layer in its capture frame: the translations of every ancestor up to the frame.
        static (double x, double y) Absolute(DocumentIndex index, DocNode node, int frameId)
        {
            double x = 0, y = 0;
            for (var cur = node; cur != null && cur.Id != frameId; cur = cur.Parent.HasValue ? index.Node(cur.Parent.Value) : null)
            {
                if (cur.Raw["transform"] is JArray t && t.Count == 6)
                {
                    x += t[4].Value<double>();
                    y += t[5].Value<double>();
                }
            }
            return (x, y);
        }

        static string Data(DocNode node, string key) =>
            node?.Raw?["plugin_data"] is JObject data ? data.Value<string>(key) : null;

        // The root of a captured canvas: placed in the scene, not inside another rect.
        static bool IsCanvasRoot(CaptureNode node) =>
            node.Parent < 0 && node.Components.Contains("Canvas", StringComparer.Ordinal);

        // The first visible image fill of a layer (its hash), or null if it has none.
        static string ImageHash(JObject raw)
        {
            if (!(raw?["fills"] is JArray fills)) return null;
            var image = fills.OfType<JObject>().FirstOrDefault(f => f.Value<string>("type") == "image" && f.Value<bool?>("visible") != false);
            return image?.Value<string>("image");
        }

        static readonly Dictionary<string, string> HashCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static string Sha256Of(string file)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
            if (HashCache.TryGetValue(file, out var cached)) return cached;
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(file);
            var hash = string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2")));
            HashCache[file] = hash;
            return hash;
        }

        /// <summary>Copies images/&lt;hash&gt; out of the document into folder (extension from its bytes).
        /// Returns the full path, or null if the document has no such image.</summary>
        static string ExtractImage(string figcraftPath, string hash, string folder)
        {
            // The hash comes from the document and becomes a file name: only hex digits, so a crafted
            // document cannot point outside the images folder or write outside the capture.
            if (string.IsNullOrEmpty(hash) || !hash.All(Uri.IsHexDigit)) return null;
            byte[] bytes = null;
            if (Directory.Exists(figcraftPath))
            {
                var loose = Path.Combine(figcraftPath, "images", hash);
                if (File.Exists(loose)) bytes = File.ReadAllBytes(loose);
            }
            else if (File.Exists(figcraftPath))
            {
                using var stream = File.OpenRead(figcraftPath);
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
                var entry = zip.GetEntry("images/" + hash);
                if (entry != null)
                {
                    using var input = entry.Open();
                    using var copy = new MemoryStream();
                    input.CopyTo(copy);
                    bytes = copy.ToArray();
                }
            }
            if (bytes == null || bytes.Length == 0) return null;

            var ext = bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 ? ".jpg" : ".png";
            Directory.CreateDirectory(folder);
            var path = Path.GetFullPath(Path.Combine(folder, hash.Substring(0, Math.Min(16, hash.Length)) + ext));
            if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
            return path;
        }

        static string SolidColor(JObject raw)
        {
            if (!(raw["fills"] is JArray fills)) return null;
            var solid = fills.OfType<JObject>().FirstOrDefault(f => f.Value<string>("type") == "solid" && f.Value<bool?>("visible") != false);
            if (!(solid?["color"] is JObject c)) return null;
            int Byte(string k) => (int)Math.Round(Math.Clamp(c.Value<double>(k), 0, 1) * 255);
            return $"#{Byte("r"):X2}{Byte("g"):X2}{Byte("b"):X2}";
        }

        // FigCraft stores line breaks as \n; TMP may hand back \r\n or a trailing space.
        static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd();

        static string Rect(double x, double y, double w, double h) =>
            $"{Num(x)},{Num(y)} {Num(w)}x{Num(h)}";

        static string Num(double v) => Math.Round(v, 1).ToString(CultureInfo.InvariantCulture);

        static JObject ToJson(LayerChange c) => new JObject
        {
            ["id"] = c.Id, ["capture"] = c.Capture, ["snapshot"] = c.Snapshot, ["nodeId"] = c.NodeId,
            ["unityPath"] = c.UnityPath, ["scene"] = c.Scene, ["layer"] = c.Layer, ["layerId"] = c.LayerId,
            ["property"] = c.Property, ["before"] = c.Before, ["after"] = c.After,
            ["dx"] = c.Dx, ["dy"] = c.Dy, ["dw"] = c.Dw, ["dh"] = c.Dh, ["number"] = c.Number,
            ["applicable"] = c.Applicable, ["warning"] = c.Warning,
        };
    }
}
