using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FigCraft.Features.Diff
{
    public sealed class DiffOptions
    {
        public string BeforePath;
        public string AfterPath;
        public string OutPath;
        public string AnnotatePath;
        public string PatchPath;
        public string CliPath = "figcraft-cli";
        public string Prefix = "[Unity diff]";
        public bool DryRun;

        public static DiffOptions Parse(Cli cli)
        {
            var options = new DiffOptions
            {
                BeforePath = cli.Require("before"),
                AfterPath = cli.Require("after"),
                OutPath = cli.Get("out"),
                AnnotatePath = cli.Get("annotate"),
                PatchPath = cli.Get("patch"),
                CliPath = cli.Get("cli", "figcraft-cli"),
                Prefix = cli.Get("prefix", "[Unity diff]"),
                DryRun = cli.Has("dry-run"),
            };

            return options;
        }
    }

    public sealed class AnnotationPlan
    {
        public readonly List<PatchStep> Steps = new List<PatchStep>();
        public int Added;
        public int Removed;
        public int Skipped;
    }

    /// <summary>
    /// Answers the question a Unity developer asks after a design update: which layers changed?
    /// Compares two saved versions of the document, prints the differences and can leave the
    /// changed layers annotated in the new version.
    /// </summary>
    public static class DiffCommand
    {
        public const string Category = "Unity diff";

        public static int Run(DiffOptions options)
        {
            var before = DocumentIndex.Load(options.BeforePath);
            var after = DocumentIndex.Load(options.AfterPath);
            var result = DiffEngine.Compare(before, after);

            Console.WriteLine($"before   {options.BeforePath}");
            Console.WriteLine($"after    {options.AfterPath}");
            Console.WriteLine($"changes  {result.Added.Count} added, {result.Removed.Count} removed, {result.Changed.Count} changed");

            foreach (var added in result.Added) Console.WriteLine("+ " + added.Path);
            foreach (var removed in result.Removed) Console.WriteLine("- " + removed.Path);
            foreach (var changed in result.Changed) Console.WriteLine("~ " + changed.Path + "  " + changed.Describe());

            if (result.Total == 0) Console.WriteLine("no changes");

            if (options.OutPath != null)
            {
                WriteReport(result, options.BeforePath, options.AfterPath, options.OutPath);
                Console.WriteLine($"report   {options.OutPath}");
            }

            if (options.AnnotatePath == null) return 0;

            var client = new FigCraftClient { CliPath = options.CliPath };
            JArray categories, existing;
            try
            {
                var read = client.Run(options.AfterPath, null, new List<PatchStep>
                {
                    new PatchStep("annotation.categories"),
                    new PatchStep("annotation.list", new JObject { ["page"] = "all" }),
                });

                categories = read.FirstOrDefault(r => r.Command == "annotation.categories")?.Result?["categories"] as JArray
                             ?? new JArray();
                existing = read.FirstOrDefault(r => r.Command == "annotation.list")?.Result?["annotations"] as JArray
                           ?? new JArray();
            }
            catch (FigCraftException ex) when (options.DryRun)
            {
                // A dry run should still show the additions when figcraft-cli is not around; it just
                // cannot know which annotations from a previous run would be replaced.
                Console.Error.WriteLine("warning  cannot read existing annotations (" + ex.Message + "); planning additions only");
                categories = new JArray();
                existing = new JArray();
            }

            var plan = PlanAnnotations(result, existing, categories, options.Prefix);

            if (options.PatchPath != null)
            {
                WritePatch(plan.Steps, options.PatchPath);
                Console.WriteLine($"patch    {options.PatchPath}");
            }

            if (plan.Steps.Count == 0)
            {
                Console.WriteLine("annotations  nothing to mark");
                return 0;
            }

            if (options.DryRun)
            {
                Console.WriteLine();
                foreach (var step in plan.Steps) Console.WriteLine("  " + step);
                Console.WriteLine();
                Console.WriteLine("dry run  nothing written");
                return 0;
            }

            client.Run(options.AfterPath, options.AnnotatePath, plan.Steps);
            Console.WriteLine($"annotations  +{plan.Added} -{plan.Removed}");
            if (plan.Skipped > 0)
                Console.WriteLine($"removed layers cannot be annotated: {plan.Skipped} only in the report");
            Console.WriteLine($"wrote    {options.AnnotatePath}");
            return 0;
        }

        /// <summary>Builds the annotation script without touching FigCraft, so it can be tested.</summary>
        public static AnnotationPlan PlanAnnotations(DiffResult result, JArray existingAnnotations,
            JArray categories, string prefix)
        {
            var plan = new AnnotationPlan();

            var known = new HashSet<string>(
                (categories ?? new JArray()).OfType<JObject>().Select(c => c.Value<string>("name")).Where(n => n != null),
                StringComparer.OrdinalIgnoreCase);

            var targets = result.Added.Concat(result.Changed).ToList();

            // Stale notes are removed even when this comparison found nothing: they describe an
            // earlier baseline, and leaving them would make the document lie about its changes.
            var stale = (existingAnnotations ?? new JArray()).OfType<JObject>()
                .Where(a => (a.Value<string>("label") ?? "").StartsWith(prefix, StringComparison.Ordinal))
                .Select(a => new { Id = a.Value<int?>("id") ?? 0, Index = a.Value<int?>("index") ?? 0 })
                .Where(a => a.Id > 0)
                .GroupBy(a => a.Id)
                .OrderBy(g => g.Key)
                .SelectMany(g => g.OrderByDescending(a => a.Index));

            foreach (var annotation in stale)
            {
                plan.Steps.Add(new PatchStep("annotation.remove", new JObject
                {
                    ["id"] = annotation.Id,
                    ["index"] = annotation.Index,
                }));
                plan.Removed++;
            }

            plan.Skipped = result.Removed.Count;
            if (targets.Count == 0) return plan;

            if (!known.Contains(Category))
                plan.Steps.Add(new PatchStep("annotation.addCategory", new JObject
                {
                    ["name"] = Category,
                    ["color"] = "#6B7280",
                }));

            foreach (var target in targets)
            {
                plan.Steps.Add(new PatchStep("annotation.add", new JObject
                {
                    ["id"] = target.Id,
                    ["label"] = target.Label(prefix),
                    ["category"] = Category,
                    ["properties"] = new JArray("width", "height"),
                }));
                plan.Added++;
            }

            return plan;
        }

        public static void WriteReport(DiffResult result, string beforePath, string afterPath, string path)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(path, ToJson(result, beforePath, afterPath).ToString(Formatting.Indented));
                return;
            }

            File.WriteAllText(path, ToMarkdown(result, beforePath, afterPath));
        }

        public static JObject ToJson(DiffResult result, string beforePath, string afterPath)
        {
            JArray Layers(IEnumerable<LayerChange> layers) => new JArray(layers.Select(l => new JObject
            {
                ["id"] = l.Id,
                ["path"] = l.Path,
            }));

            return new JObject
            {
                ["before"] = beforePath,
                ["after"] = afterPath,
                ["added"] = Layers(result.Added),
                ["removed"] = Layers(result.Removed),
                ["changed"] = new JArray(result.Changed.Select(change => new JObject
                {
                    ["id"] = change.Id,
                    ["path"] = change.Path,
                    ["fields"] = new JObject(change.Fields.Select(f => new JProperty(f.Field, new JObject
                    {
                        ["before"] = f.Before,
                        ["after"] = f.After,
                    }))),
                })),
            };
        }

        public static string ToMarkdown(DiffResult result, string beforePath, string afterPath)
        {
            var text = new System.Text.StringBuilder();
            text.AppendLine("# FigCraft changes");
            text.AppendLine();
            text.AppendLine($"`{beforePath}` → `{afterPath}`");
            text.AppendLine();

            text.AppendLine($"## Added ({result.Added.Count})");
            text.AppendLine();
            foreach (var added in result.Added) text.AppendLine($"- `{added.Path}`");
            if (result.Added.Count == 0) text.AppendLine("_None._");
            text.AppendLine();

            text.AppendLine($"## Removed ({result.Removed.Count})");
            text.AppendLine();
            foreach (var removed in result.Removed) text.AppendLine($"- `{removed.Path}`");
            if (result.Removed.Count == 0) text.AppendLine("_None._");
            text.AppendLine();

            text.AppendLine($"## Changed ({result.Changed.Count})");
            text.AppendLine();
            foreach (var change in result.Changed)
                text.AppendLine($"- `{change.Path}`: {change.Describe()}");
            if (result.Changed.Count == 0) text.AppendLine("_None._");

            return text.ToString();
        }

        public static void WritePatch(IReadOnlyList<PatchStep> steps, string path)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, new JArray(steps.Select(s => s.ToJson())).ToString(Formatting.Indented));
        }
    }
}
