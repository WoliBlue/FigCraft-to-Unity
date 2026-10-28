using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FigCraft.Features.Annotate
{
    public sealed class AnnotateOptions
    {
        public string ReportPath;
        public string FigCraftPath;
        public string OutPath;
        public string PatchPath;
        public string CliPath = "figcraft-cli";
        public string Prefix = "[Unity]";
        public bool DryRun;
        public HashSet<string> Severities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "warning", "error" };

        public static AnnotateOptions Parse(Cli cli)
        {
            var options = new AnnotateOptions
            {
                ReportPath = cli.Require("report"),
                FigCraftPath = cli.Require("figcraft"),
                OutPath = cli.Get("out"),
                PatchPath = cli.Get("patch"),
                CliPath = cli.Get("cli", "figcraft-cli"),
                Prefix = cli.Get("prefix", "[Unity]"),
                DryRun = cli.Has("dry-run"),
            };

            var severities = cli.List("severity");
            if (severities.Count > 0)
                options.Severities = new HashSet<string>(severities, StringComparer.OrdinalIgnoreCase);

            if (!options.DryRun && string.IsNullOrEmpty(options.OutPath))
                throw new ArgumentException("--out is required when the annotations are applied (or pass --dry-run)");

            return options;
        }
    }

    public sealed class AnnotatePlan
    {
        public readonly List<PatchStep> Steps = new List<PatchStep>();
        public int Added;
        public int Removed;
        public int Matched;
        public int Unanchored;
        public int Filtered;
        public readonly List<string> Missing = new List<string>();
    }

    /// <summary>
    /// Writes the import report back into the FigCraft document as annotations: designers see what the
    /// import could not express, on the layer it affects, instead of having to open the JSON.
    /// Annotations owned by this tool are marked with a label prefix and replaced on every run.
    /// </summary>
    public static class AnnotateCommand
    {
        public const string WarningCategory = "Unity import (warning)";
        public const string ErrorCategory = "Unity import (error)";

        public static int Run(AnnotateOptions options)
        {
            var report = JObject.Parse(File.ReadAllText(options.ReportPath));
            var index = DocumentIndex.Load(options.FigCraftPath);
            var client = new FigCraftClient { CliPath = options.CliPath };

            var read = client.Run(options.FigCraftPath, null, new List<PatchStep>
            {
                new PatchStep("annotation.categories"),
                new PatchStep("annotation.list", new JObject { ["page"] = "all" }),
            });

            var categories = read.FirstOrDefault(r => r.Command == "annotation.categories")?.Result?["categories"] as JArray
                             ?? new JArray();
            var existing = read.FirstOrDefault(r => r.Command == "annotation.list")?.Result?["annotations"] as JArray
                           ?? new JArray();

            var plan = Plan(report, index, existing, categories, options.Prefix, options.Severities);

            Console.WriteLine($"report   {options.ReportPath}");
            Console.WriteLine($"issues   {plan.Matched} matched by severity, {plan.Added} anchored, " +
                              $"{plan.Unanchored} without a layer, {plan.Filtered} filtered");
            if (plan.Missing.Count > 0)
            {
                var sample = string.Join(", ", plan.Missing.Take(5));
                Console.WriteLine($"missing  {sample}{(plan.Missing.Count > 5 ? ", ..." : "")}");
            }

            if (options.PatchPath != null)
            {
                WritePatch(plan.Steps, options.PatchPath);
                Console.WriteLine($"patch    {options.PatchPath}");
            }

            if (plan.Steps.Count == 0)
            {
                Console.WriteLine("nothing to change");
                return 0;
            }

            if (options.DryRun)
            {
                Console.WriteLine();
                foreach (var step in plan.Steps) Console.WriteLine("  " + step);
                Console.WriteLine();
                Console.WriteLine($"dry run  {plan.Added} annotation(s) would be added, {plan.Removed} replaced");
                return 0;
            }

            client.Run(options.FigCraftPath, options.OutPath, plan.Steps);
            Console.WriteLine($"annotations  +{plan.Added} -{plan.Removed}");
            Console.WriteLine($"wrote    {options.OutPath}");
            return 0;
        }

        /// <summary>Builds the script without touching FigCraft, so it can be tested and reviewed.</summary>
        public static AnnotatePlan Plan(JObject report, DocumentIndex index, JArray existingAnnotations,
            JArray categories, string prefix, ISet<string> severities)
        {
            var plan = new AnnotatePlan();
            var categoryNames = new HashSet<string>(
                (categories ?? new JArray()).OfType<JObject>().Select(c => c.Value<string>("name")).Where(n => n != null),
                StringComparer.OrdinalIgnoreCase);

            var issues = (report?["issues"] as JArray ?? new JArray()).OfType<JObject>().ToList();
            foreach (var issue in issues)
            {
                var severity = issue.Value<string>("severity") ?? "info";
                if (!severities.Contains(severity)) { plan.Filtered++; continue; }
                plan.Matched++;

                var nodeId = issue.Value<int?>("nodeId") ?? 0;
                var path = issue.Value<string>("nodePath");
                var resolved = index.Find(nodeId, path);
                if (resolved == null)
                {
                    plan.Unanchored++;
                    plan.Missing.Add(string.IsNullOrEmpty(path) ? "#" + nodeId : path);
                }
            }

            // Replace our own annotations before adding the fresh ones. Removing several annotations on
            // the same layer by index shifts the rest, so indices go from high to low.
            var removalSteps = new List<PatchStep>();
            var stale = (existingAnnotations ?? new JArray()).OfType<JObject>()
                .Where(a => (a.Value<string>("label") ?? "").StartsWith(prefix, StringComparison.Ordinal))
                .Select(a => new { Id = a.Value<int?>("id") ?? 0, Index = a.Value<int?>("index") ?? 0 })
                .Where(a => a.Id > 0)
                .GroupBy(a => a.Id)
                .OrderBy(g => g.Key)
                .SelectMany(g => g.OrderByDescending(a => a.Index));

            foreach (var annotation in stale)
            {
                removalSteps.Add(new PatchStep("annotation.remove", new JObject
                {
                    ["id"] = annotation.Id,
                    ["index"] = annotation.Index,
                }));
                plan.Removed++;
            }

            var adds = new List<(int nodeId, string label, string category)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var issue in issues)
            {
                var severity = issue.Value<string>("severity") ?? "info";
                if (!severities.Contains(severity)) continue;

                var path = issue.Value<string>("nodePath");
                var nodeId = issue.Value<int?>("nodeId") ?? 0;
                var resolved = index.Find(nodeId, path);
                if (resolved == null) continue;

                var rule = issue.Value<string>("rule") ?? "issue";
                var message = issue.Value<string>("message") ?? "";
                var key = $"{resolved}|{rule}|{message}";
                if (!seen.Add(key)) continue;

                var category = severity == "error" ? ErrorCategory : WarningCategory;
                adds.Add((resolved.Value, Label(issue, prefix), category));
            }

            var needsWarning = adds.Any(a => a.category == WarningCategory);
            var needsError = adds.Any(a => a.category == ErrorCategory);
            var categorySteps = new List<PatchStep>();
            if (needsWarning && !categoryNames.Contains(WarningCategory))
                categorySteps.Add(new PatchStep("annotation.addCategory", new JObject
                {
                    ["name"] = WarningCategory,
                    ["color"] = "#D97A06",
                }));
            if (needsError && !categoryNames.Contains(ErrorCategory))
                categorySteps.Add(new PatchStep("annotation.addCategory", new JObject
                {
                    ["name"] = ErrorCategory,
                    ["color"] = "#D64545",
                }));

            var addSteps = new List<PatchStep>();
            foreach (var add in adds)
            {
                addSteps.Add(new PatchStep("annotation.add", new JObject
                {
                    ["id"] = add.nodeId,
                    ["label"] = add.label,
                    ["category"] = add.category,
                    ["properties"] = new JArray("width", "height"),
                }));
                plan.Added++;
            }

            // Categories must exist before an annotation references them; removals are independent.
            plan.Steps.AddRange(categorySteps);
            plan.Steps.AddRange(removalSteps);
            plan.Steps.AddRange(addSteps);
            return plan;
        }

        static string Label(JObject issue, string prefix)
        {
            var rule = issue.Value<string>("rule") ?? "issue";
            var message = issue.Value<string>("message") ?? "";
            var target = issue.Value<string>("unity");
            var fix = issue["fix"] as JObject;

            var text = new StringBuilder();
            text.Append(prefix).Append(' ').Append(rule).Append(": ").Append(message);
            if (!string.IsNullOrEmpty(target)) text.Append(" -> ").Append(target);
            if (fix != null)
            {
                text.Append(" [fix: ").Append(fix.Value<string>("kind") ?? "manual");
                var required = fix["required"];
                if (required != null) text.Append(" >= ").Append(required);
                var command = fix.Value<string>("command");
                if (!string.IsNullOrEmpty(command)) text.Append(" (").Append(command).Append(')');
                text.Append(']');
            }

            var single = Regex.Replace(text.ToString(), @"\s+", " ").Trim();
            return single.Length <= 500 ? single : single.Substring(0, 497) + "...";
        }

        public static void WritePatch(IReadOnlyList<PatchStep> steps, string path)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, new JArray(steps.Select(s => s.ToJson())).ToString(Formatting.Indented));
        }
    }
}
