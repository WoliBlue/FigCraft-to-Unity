using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FigCraft.Features.Capture
{
    public sealed class CaptureOptions
    {
        public string SnapshotPath;
        public string FigCraftPath;
        public string OutPath;
        public string Page = "Game";
        public string CliPath = "figcraft-cli";
        public bool Replace;
        public bool Reference = true;
        public bool DryRun;
        /// <summary>Where the summary goes (the console by default; Unity passes its own writer).</summary>
        public TextWriter Log;
        internal TextWriter Output => Log ?? Console.Out;

        public static CaptureOptions Parse(Cli cli)
        {
            var options = new CaptureOptions
            {
                SnapshotPath = cli.Require("snapshot"),
                FigCraftPath = cli.Get("figcraft"),
                OutPath = cli.Get("out"),
                Page = cli.Get("page", "Game"),
                CliPath = cli.Get("cli", "figcraft-cli"),
                Replace = cli.Has("replace"),
                Reference = !cli.Has("no-reference"),
                DryRun = cli.Has("dry-run"),
            };

            if (!options.DryRun && string.IsNullOrEmpty(options.OutPath))
                throw new ArgumentException("--out is required when the document is written (or pass --dry-run)");
            if (!string.IsNullOrEmpty(options.FigCraftPath) && !File.Exists(options.FigCraftPath))
                throw new ArgumentException($"--figcraft {options.FigCraftPath} does not exist");

            return options;
        }
    }

    /// <summary>
    /// Builds one FigCraft frame from a capture of the running game.
    ///
    /// <c>figcraft-cli run</c> only returns ids when a script ends, so the build takes two passes whatever the
    /// depth of the hierarchy: the first creates every layer loose on the page (absolute positions), the second
    /// nests each one in its parent, in pre-order, and sets the position relative to it. Appending in pre-order
    /// keeps the uGUI draw order (later siblings on top).
    /// </summary>
    public static class CaptureCommand
    {
        const double FrameGap = 200;

        enum Role { Frame, World, Container, Graphic, Reference }

        sealed class Layer
        {
            public Role Role;
            public CaptureNode Node;
            public Layer Parent;
            public double X, Y, W, H;  // absolute in the capture frame
            public string Name;
            public PatchStep Create;
            public int Id;
            public bool Alone;          // graphic without its own container frame
        }

        public static int Run(CaptureOptions options)
        {
            var snapshot = CaptureSnapshot.Load(options.SnapshotPath);
            var index = string.IsNullOrEmpty(options.FigCraftPath) ? null : DocumentIndex.Load(options.FigCraftPath);

            var page = index?.Nodes.FirstOrDefault(n => n.Type == "page" &&
                                                        string.Equals(n.Name, options.Page, StringComparison.Ordinal));
            var frames = page == null
                ? new List<DocNode>()
                : index.Nodes.Where(n => n.Parent == page.Id && n.Type != "page").ToList();
            var collisions = frames.Where(f => string.Equals(f.Name, snapshot.Name, StringComparison.Ordinal)).ToList();
            if (collisions.Count > 0 && !options.Replace)
                throw new FigCraftException($"the page '{options.Page}' already has a frame named '{snapshot.Name}'. " +
                                            "Pass --replace to rebuild it.");

            // A rebuilt frame keeps its place; a new one goes to the right of the others.
            var kept = frames.Except(collisions).ToList();
            var originX = collisions.Count > 0 ? XOf(collisions[0])
                : kept.Count == 0 ? 0 : kept.Max(f => XOf(f) + (f.Width ?? 0)) + FrameGap;

            var layers = Plan(snapshot, options.Reference, out var missing);
            if (options.DryRun)
            {
                PrintPlan(snapshot, options, layers, missing, page != null, collisions.Count, originX);
                return 0;
            }

            var client = new FigCraftClient { CliPath = options.CliPath };
            var temp = Path.Combine(Path.GetTempPath(), "figcraft-capture-" + Guid.NewGuid().ToString("N"));
            var workA = temp + "-a.figcraft";
            var workB = temp + "-b.figcraft";

            try
            {
                // Setup: the page exists and its id is known.
                var setup = new List<PatchStep>();
                if (index == null) setup.Add(new PatchStep("page.rename", new JObject { ["name"] = options.Page }));
                else if (page == null) setup.Add(new PatchStep("page.add", new JObject { ["name"] = options.Page }));
                setup.Add(new PatchStep("page.list"));
                var listed = client.Run(options.FigCraftPath, workA, setup).Last().Result as JArray ?? new JArray();
                var pageId = listed.OfType<JObject>()
                                 .FirstOrDefault(p => p.Value<string>("name") == options.Page)?.Value<int?>("id")
                             ?? throw new FigCraftException($"the page '{options.Page}' could not be created");

                // Pass 1: every layer loose on the page.
                var create = new List<PatchStep>();
                if (collisions.Count > 0)
                    create.Add(new PatchStep("edit.delete", new JObject { ["ids"] = new JArray(collisions.Select(c => c.Id)) }));
                foreach (var layer in layers)
                {
                    var p = layer.Create.Params;
                    p["parent"] = pageId;
                    p["x"] = Round(originX + layer.X);
                    p["y"] = Round(layer.Y);
                    create.Add(layer.Create);
                }

                var created = client.Run(workA, workB, create)
                    .Where(r => r.Command == "node.create" || r.Command == "image.place")
                    .Select(r => r.Result?.Value<int?>("id") ?? 0)
                    .ToList();
                if (created.Count != layers.Count || created.Any(id => id <= 0))
                    throw new FigCraftException($"expected {layers.Count} layers, got {created.Count}. The output was not written.");
                for (var i = 0; i < layers.Count; i++) layers[i].Id = created[i];

                // Pass 2: nest, place relative to the parent, decorate.
                var structure = new List<PatchStep>();
                foreach (var layer in layers)
                {
                    if (layer.Parent != null)
                        structure.Add(new PatchStep("node.reparent", new JObject
                        {
                            ["ids"] = new JArray(layer.Id),
                            ["parent"] = layer.Parent.Id,
                        }));
                    structure.Add(new PatchStep("node.set", Decorate(layer, snapshot, originX)));
                }
                client.Run(workB, options.OutPath, structure);

                var frameId = layers[0].Id;
                WriteSidecars(snapshot, options, layers, missing, frameId);

                options.Output.WriteLine($"capture   {snapshot.Name} ({snapshot.Scene}, {snapshot.ScreenWidth}x{snapshot.ScreenHeight})");
                options.Output.WriteLine($"page      {options.Page}" + (page == null ? " (created)" : $" (id {pageId})") +
                                  (collisions.Count > 0 ? $", replaced {collisions.Count} frame(s)" : ""));
                options.Output.WriteLine($"frame     id {frameId} at x {Round(originX)}");
                options.Output.WriteLine($"layers    {layers.Count} ({Count(layers, Role.Container)} frames, " +
                                  $"{layers.Count(l => l.Role == Role.Graphic && l.Node.Kind == "image")} images, " +
                                  $"{layers.Count(l => l.Role == Role.Graphic && l.Node.Kind == "text")} texts, " +
                                  $"{layers.Count(l => l.Role == Role.Graphic && l.Node.Kind == "rect")} colours)");
                options.Output.WriteLine($"notes     {snapshot.Nodes.Count(n => n.Notes.Count > 0)} node(s) with notes" +
                                  (missing.Count > 0 ? $", {missing.Count} missing file(s)" : ""));
                options.Output.WriteLine($"wrote     {options.OutPath}");
                return 0;
            }
            finally
            {
                try { File.Delete(workA); } catch { /* best effort */ }
                try { File.Delete(workB); } catch { /* best effort */ }
            }
        }

        static List<Layer> Plan(CaptureSnapshot snapshot, bool reference, out List<string> missing)
        {
            var layers = new List<Layer>();
            var lost = new List<string>();
            var frame = new Layer
            {
                Role = Role.Frame,
                Name = snapshot.Name,
                W = snapshot.FrameWidth,
                H = snapshot.FrameHeight,
            };
            frame.Create = Create("frame", frame, new JObject { ["fill"] = "#000000", ["clip"] = true });
            layers.Add(frame);

            var world = snapshot.FileOf(snapshot.World);
            if (world != null) layers.Add(Picture(Role.World, "3D world (no UI)", world, frame, snapshot));
            else if (!string.IsNullOrEmpty(snapshot.World)) lost.Add(snapshot.World);

            void Add(CaptureNode node, Layer parent)
            {
                var container = node.Children.Count > 0 || !node.HasGraphic;
                var host = parent;
                if (container)
                {
                    host = new Layer { Role = Role.Container, Node = node, Parent = parent, Name = node.Name,
                                       X = node.X, Y = node.Y, W = node.W, H = node.H };
                    host.Create = Create("frame", host, new JObject { ["fills"] = new JArray(), ["clip"] = node.Clip });
                    layers.Add(host);
                }

                if (node.HasGraphic)
                {
                    var graphic = new Layer
                    {
                        Role = Role.Graphic, Node = node, Parent = host, Alone = !container,
                        Name = container ? $"{node.Name} · {Label(node.Kind)}" : node.Name,
                        X = node.X, Y = node.Y, W = node.W, H = node.H,
                    };
                    graphic.Create = GraphicStep(graphic, snapshot, lost);
                    layers.Add(graphic);
                }

                foreach (var child in node.Children) Add(child, host);
            }

            foreach (var root in snapshot.Roots) Add(root, frame);

            var shot = snapshot.FileOf(snapshot.Screenshot);
            if (reference && shot != null) layers.Add(Picture(Role.Reference, "Reference · game screenshot", shot, frame, snapshot));
            else if (reference && !string.IsNullOrEmpty(snapshot.Screenshot)) lost.Add(snapshot.Screenshot);

            missing = lost;
            return layers;
        }

        static Layer Picture(Role role, string name, string path, Layer frame, CaptureSnapshot snapshot)
        {
            var layer = new Layer { Role = role, Name = name, Parent = frame, W = snapshot.FrameWidth, H = snapshot.FrameHeight };
            layer.Create = new PatchStep("image.place", new JObject
            {
                ["path"] = path,
                ["width"] = Round(layer.W),
                ["height"] = Round(layer.H),
                ["mode"] = "fill",
            });
            return layer;
        }

        static PatchStep GraphicStep(Layer layer, CaptureSnapshot snapshot, List<string> missing)
        {
            var node = layer.Node;
            switch (node.Kind)
            {
                case "image":
                    var file = snapshot.FileOf(node.Image);
                    if (file != null)
                        return new PatchStep("image.place", new JObject
                        {
                            ["path"] = file,
                            ["width"] = Round(Math.Max(1, layer.W)),
                            ["height"] = Round(Math.Max(1, layer.H)),
                            ["mode"] = "fill",
                        });
                    missing.Add(node.Image ?? node.Path);
                    node.Notes.Add($"Image file {node.Image} is missing: drawn as a placeholder.");
                    return Create("rectangle", layer, new JObject { ["fill"] = "#FF00FF" });

                case "text":
                    var props = new JObject
                    {
                        ["fontSize"] = Round(Math.Max(1, node.FontSize)),
                        ["fill"] = node.Color ?? "#000000",
                        ["textAlign"] = node.Align ?? "left",
                        ["textAlignVertical"] = node.Valign ?? "top",
                        ["textAutoResize"] = "none",
                    };
                    if (!string.IsNullOrEmpty(node.FontFamily)) props["fontFamily"] = node.FontFamily;
                    if (!string.IsNullOrEmpty(node.FontStyle)) props["fontStyle"] = node.FontStyle;
                    if (!string.IsNullOrEmpty(node.TextCase)) props["textCase"] = node.TextCase;
                    var step = Create("text", layer, props);
                    step.Params["text"] = node.Text ?? "";
                    step.Params.Remove("height"); // text height only sticks once auto-resize is off (pass 2)
                    return step;

                default:
                    return Create("rectangle", layer, new JObject { ["fill"] = node.Color ?? "#FFFFFF" });
            }
        }

        static PatchStep Create(string type, Layer layer, JObject props) => new PatchStep("node.create", new JObject
        {
            ["type"] = type,
            ["name"] = layer.Name,
            ["width"] = Round(Math.Max(1, layer.W)),
            ["height"] = Round(Math.Max(1, layer.H)),
            ["props"] = props,
            ["select"] = false,
        });

        static JObject Decorate(Layer layer, CaptureSnapshot snapshot, double originX)
        {
            var set = new JObject { ["ids"] = new JArray(layer.Id), ["name"] = layer.Name };
            if (layer.Parent != null)
            {
                set["x"] = Round(layer.X - layer.Parent.X);
                set["y"] = Round(layer.Y - layer.Parent.Y);
            }

            var data = new JObject { ["unity.capture"] = snapshot.Name, ["unity.scene"] = snapshot.Scene ?? "" };
            switch (layer.Role)
            {
                case Role.Frame:
                    data["unity.screen"] = $"{snapshot.ScreenWidth}x{snapshot.ScreenHeight}";
                    data["unity.capturedAt"] = snapshot.CapturedAt ?? "";
                    if (snapshot.Notes.Count > 0) data["unity.notes"] = string.Join(" | ", snapshot.Notes);
                    break;
                case Role.World:
                case Role.Reference:
                    set["locked"] = true;
                    if (layer.Role == Role.Reference) set["visible"] = false;
                    data["unity.role"] = layer.Role == Role.World ? "world" : "reference";
                    break;
                default:
                    var node = layer.Node;
                    data["unity.path"] = node.Path ?? "";
                    data["unity.kind"] = layer.Role == Role.Container ? "container" : node.Kind;
                    if (node.Components.Length > 0) data["unity.components"] = string.Join(",", node.Components);
                    if (node.Notes.Count > 0) data["unity.notes"] = string.Join(" | ", node.Notes);

                    double opacity;
                    bool visible;
                    if (layer.Role == Role.Container) { opacity = node.Opacity; visible = node.Visible; }
                    else if (layer.Alone) { opacity = node.Opacity * node.GraphicOpacity; visible = node.Visible; }
                    else { opacity = node.GraphicOpacity; visible = node.GraphicOpacity > 0.001; }
                    if (opacity < 0.999) set["opacity"] = Math.Round(Math.Clamp(opacity, 0, 1), 3);

                    var constraints = layer.Role == Role.Graphic && !layer.Alone
                        ? new JObject { ["horizontal"] = "leftRight", ["vertical"] = "topBottom" } // fills its own frame
                        : Constraints(node);
                    if (constraints != null) set["constraints"] = constraints;
                    if (!visible) set["visible"] = false;

                    if (layer.Role == Role.Graphic && node.Kind == "text")
                    {
                        set["height"] = Round(Math.Max(1, layer.H));
                        if (node.OutlineWidth > 0.001)
                        {
                            // TMP outlines live in SDF units; about a third of the font size per unit is close on screen.
                            set["strokes"] = new JArray(node.OutlineColor ?? "#000000FF");
                            set["strokeWeight"] = Round(Math.Max(0.5, node.OutlineWidth * node.FontSize * 0.33));
                            set["strokeAlign"] = "outside";
                        }
                    }
                    break;
            }
            set["pluginData"] = data;
            return set;
        }

        static void WriteSidecars(CaptureSnapshot snapshot, CaptureOptions options, List<Layer> layers,
            List<string> missing, int frameId)
        {
            var map = new JObject
            {
                ["figcraft"] = Path.GetFullPath(options.OutPath),
                ["page"] = options.Page,
                ["frame"] = frameId,
                ["capture"] = snapshot.Name,
                ["layers"] = new JArray(layers.Where(l => l.Node != null).Select(l => new JObject
                {
                    ["id"] = l.Id,
                    ["unityPath"] = l.Node.Path,
                    ["role"] = l.Role == Role.Container ? "container" : l.Node.Kind,
                })),
            };
            File.WriteAllText(Path.Combine(snapshot.Folder, "figcraft-map.json"), map.ToString(Formatting.Indented),
                new UTF8Encoding(false));

            var md = new StringBuilder();
            md.AppendLine($"# Capture '{snapshot.Name}'").AppendLine();
            md.AppendLine($"- Scene: {snapshot.Scene} · screen {snapshot.ScreenWidth}x{snapshot.ScreenHeight} · {(snapshot.PlayMode ? "Play Mode" : "Edit Mode")} · {snapshot.CapturedAt}");
            md.AppendLine($"- FigCraft: `{Path.GetFullPath(options.OutPath)}`, page '{options.Page}', frame id {frameId}");
            md.AppendLine($"- Layers: {layers.Count}");
            foreach (var note in snapshot.Notes) md.AppendLine($"- {note}");
            foreach (var file in missing) md.AppendLine($"- Missing file `{file}`");
            var noted = snapshot.Nodes.Where(n => n.Notes.Count > 0).ToList();
            if (noted.Count > 0)
            {
                md.AppendLine().AppendLine("## What is not exact").AppendLine();
                foreach (var node in noted)
                    md.AppendLine($"- `{node.Path}`: {string.Join(" ", node.Notes)}");
            }
            File.WriteAllText(Path.Combine(snapshot.Folder, "figcraft-report.md"), md.ToString(), new UTF8Encoding(false));
        }

        static void PrintPlan(CaptureSnapshot snapshot, CaptureOptions options, List<Layer> layers, List<string> missing,
            bool pageExists, int replaced, double originX)
        {
            options.Output.WriteLine($"capture   {snapshot.Name} ({snapshot.Scene}, {snapshot.ScreenWidth}x{snapshot.ScreenHeight})");
            options.Output.WriteLine($"page      {options.Page}" + (pageExists ? "" : " (would be created)") +
                              (replaced > 0 ? $", would replace {replaced} frame(s)" : ""));
            options.Output.WriteLine($"frame     at x {Round(originX)}, {snapshot.FrameWidth}x{snapshot.FrameHeight}");
            options.Output.WriteLine($"layers    {layers.Count}");
            foreach (var file in missing) options.Output.WriteLine($"missing   {file}");
            foreach (var layer in layers)
            {
                var depth = 0;
                for (var p = layer.Parent; p != null; p = p.Parent) depth++;
                options.Output.WriteLine($"  {new string(' ', depth * 2)}{layer.Role,-9} {layer.Name}  " +
                                  $"{Round(layer.W)}x{Round(layer.H)} @ {Round(layer.X)},{Round(layer.Y)}");
            }
        }

        // Unity anchors → FigCraft constraints, so resizing a parent in FigCraft moves children like the game does.
        static JObject Constraints(CaptureNode node)
        {
            if (node.AnchorMin == null || node.AnchorMax == null) return null;
            static string Axis(double min, double max, string low, string high, string both)
            {
                const double e = 0.001;
                if (Math.Abs(min - max) < e)
                    return Math.Abs(min) < e ? low : Math.Abs(min - 1) < e ? high : Math.Abs(min - 0.5) < e ? "center" : "scale";
                return Math.Abs(min) < e && Math.Abs(max - 1) < e ? both : "scale";
            }
            return new JObject
            {
                ["horizontal"] = Axis(node.AnchorMin.X, node.AnchorMax.X, "left", "right", "leftRight"),
                // Unity's y grows upwards: anchor 0 is the bottom edge.
                ["vertical"] = Axis(node.AnchorMin.Y, node.AnchorMax.Y, "bottom", "top", "topBottom"),
            };
        }

        static double XOf(DocNode node) =>
            node.Raw?["transform"] is JArray t && t.Count == 6 ? t[4].Value<double>() : node.Raw?.Value<double?>("x") ?? 0;

        static string Label(string kind) => kind switch
        {
            "image" => "image",
            "text" => "text",
            _ => "colour",
        };

        static int Count(List<Layer> layers, Role role) => layers.Count(l => l.Role == role);

        static double Round(double v) => Math.Round(v, 2);
    }
}
