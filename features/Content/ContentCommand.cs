using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FigCraft.Features.Content
{
    public sealed class ContentOptions
    {
        public string ManifestPath;
        public string FigCraftPath;
        public string OutPath;
        public string PatchPath;
        public string CliPath = "figcraft-cli";
        public bool DryRun;

        public static ContentOptions Parse(Cli cli)
        {
            var options = new ContentOptions
            {
                ManifestPath = cli.Require("manifest"),
                FigCraftPath = cli.Require("figcraft"),
                OutPath = cli.Get("out"),
                PatchPath = cli.Get("patch"),
                CliPath = cli.Get("cli", "figcraft-cli"),
                DryRun = cli.Has("dry-run"),
            };

            if (!options.DryRun && string.IsNullOrEmpty(options.OutPath))
                throw new ArgumentException("--out is required when the content is applied (or pass --dry-run)");

            return options;
        }
    }

    public sealed class ContentPlan
    {
        public readonly List<PatchStep> Steps = new List<PatchStep>();
        public readonly List<string> Missing = new List<string>();
        public readonly List<string> WrongType = new List<string>();
        public readonly List<string> Overflow = new List<string>();
        public int Texts;
        public int Images;
        public int Empty;
    }

    /// <summary>
    /// Puts real content into the design: the longest translation, empty lists, real prices, real
    /// avatars. Run it before writing UXML and truncation shows up in FigCraft instead of in a build.
    /// </summary>
    public static class ContentCommand
    {
        /// <summary>Only these node types can hold the image layer image.place creates.</summary>
        static readonly string[] Containers = { "frame", "component", "instance", "section", "page" };

        public static int Run(ContentOptions options)
        {
            var manifest = ContentManifest.Load(options.ManifestPath);
            var index = DocumentIndex.Load(options.FigCraftPath);
            var plan = Plan(manifest, index);

            Console.WriteLine($"manifest  {options.ManifestPath}" +
                              (string.IsNullOrEmpty(manifest.Locale) ? "" : $" ({manifest.Locale})"));
            Console.WriteLine($"targets   {plan.Texts} text(s), {plan.Images} image(s), {plan.Empty} empty");

            foreach (var missing in plan.Missing)
                Console.Error.WriteLine($"missing   {missing}");
            foreach (var wrong in plan.WrongType)
                Console.Error.WriteLine($"wrong type  {wrong}");

            if (plan.Missing.Count > 0 || plan.WrongType.Count > 0)
            {
                Console.Error.WriteLine("nothing written: fix the targets, then run again");
                return 1;
            }

            if (plan.Overflow.Count > 0)
            {
                Console.WriteLine("overflow  likely, fix before coding:");
                foreach (var line in plan.Overflow) Console.WriteLine("  " + line);
            }

            if (options.PatchPath != null)
            {
                WritePatch(plan.Steps, options.PatchPath);
                Console.WriteLine($"patch     {options.PatchPath}");
            }

            if (options.DryRun)
            {
                Console.WriteLine();
                foreach (var step in plan.Steps) Console.WriteLine("  " + step);
                Console.WriteLine();
                Console.WriteLine("dry run   nothing written");
                return 0;
            }

            var client = new FigCraftClient { CliPath = options.CliPath };
            client.Run(options.FigCraftPath, options.OutPath, plan.Steps);
            Console.WriteLine($"content   {plan.Texts} text(s), {plan.Images} image(s)");
            Console.WriteLine($"wrote     {options.OutPath}");
            return 0;
        }

        /// <summary>Builds the script without touching FigCraft, so it can be tested and reviewed.</summary>
        public static ContentPlan Plan(ContentManifest manifest, DocumentIndex index)
        {
            var plan = new ContentPlan();
            var targets = new HashSet<int>();

            foreach (var text in manifest.Texts)
            {
                var id = index.Find(text.Id ?? 0, text.Node);
                if (id == null)
                {
                    plan.Missing.Add($"{text.Target} (no such layer)");
                    continue;
                }
                if (!targets.Add(id.Value))
                {
                    plan.Missing.Add($"{text.Target} and another entry resolve to the same layer");
                    continue;
                }

                var node = index.Node(id.Value);
                if (!string.Equals(node.Type, "text", StringComparison.OrdinalIgnoreCase))
                {
                    plan.WrongType.Add($"{index.PathOf(id.Value)} is a {node.Type}, not a text layer");
                    continue;
                }

                plan.Texts++;
                if (text.Value.Length == 0) plan.Empty++;
                plan.Steps.Add(new PatchStep("node.set", new JObject
                {
                    ["id"] = id.Value,
                    ["characters"] = text.Value,
                }));

                var estimate = EstimateWidth(node, text.Value);
                var parent = node.Parent.HasValue ? index.Node(node.Parent.Value) : null;
                if (estimate.HasValue && parent?.Width > 0 && estimate.Value > parent.Width.Value)
                    plan.Overflow.Add($"{index.PathOf(id.Value)}: ~{estimate.Value:0}px of text in {parent.Width.Value:0}px");
            }

            foreach (var image in manifest.Images)
            {
                var id = index.Find(image.Id ?? 0, image.Node);
                if (id == null)
                {
                    plan.Missing.Add($"{image.Target} (no such layer)");
                    continue;
                }
                if (!targets.Add(id.Value))
                {
                    plan.Missing.Add($"{image.Target} and another entry resolve to the same layer");
                    continue;
                }

                var holder = index.Node(id.Value);
                if (!Containers.Contains(holder.Type, StringComparer.OrdinalIgnoreCase))
                {
                    plan.WrongType.Add(
                        $"{index.PathOf(id.Value)} is a {holder.Type}; images need a frame, component, instance, section or page to hold them");
                    continue;
                }

                plan.Images++;
                plan.Steps.Add(new PatchStep("image.place", new JObject
                {
                    ["path"] = image.Path,
                    ["parent"] = id.Value,
                    ["x"] = image.X,
                    ["y"] = image.Y,
                    ["width"] = image.Width,
                    ["height"] = image.Height,
                    ["mode"] = image.Mode.ToLowerInvariant(),
                }));
            }

            return plan;
        }

        /// <summary>
        /// Rough width of the new string, scaling the layer's current width by the character count.
        /// It is a warning, not a measurement: FigCraft lays the text out for real when it applies.
        /// </summary>
        static double? EstimateWidth(DocNode node, string value)
        {
            if (string.IsNullOrEmpty(node.Characters) || !(node.Width > 0)) return null;
            var perCharacter = node.Width.Value / node.Characters.Length;
            return perCharacter * Math.Max(1, value.Length);
        }

        public static void WritePatch(IReadOnlyList<PatchStep> steps, string path)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, new JArray(steps.Select(s => s.ToJson())).ToString(Formatting.Indented));
        }
    }
}
