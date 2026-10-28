using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace FigCraft.Connector.Conversion
{
    public enum IssueSeverity { Info, Warning, Error }

    public enum ConversionOutcome { Exact, Approximated, Degraded, Dropped, Info }

    /// <summary>A suggested or executable remedy attached to an issue.</summary>
    public sealed class ReportFix
    {
        /// <summary>fontAssetPadding | bake | customControl | manual</summary>
        public string Kind;
        public double? Required;
        public string Asset;
        public string Command;
    }

    public sealed class ImportIssue
    {
        public int NodeId;
        public string NodePath;
        public string NodeName;
        public string Property;
        public string SourceValue;
        public string TargetValue;
        public ConversionOutcome Outcome;
        public IssueSeverity Severity;
        public string Rule;
        public string Message;
        public ReportFix Fix;
    }

    public sealed class IssueStats
    {
        public int Exact, Approximated, Degraded, Dropped, Info;
        public int Nodes;

        public void Add(ConversionOutcome outcome)
        {
            switch (outcome)
            {
                case ConversionOutcome.Exact: Exact++; break;
                case ConversionOutcome.Approximated: Approximated++; break;
                case ConversionOutcome.Degraded: Degraded++; break;
                case ConversionOutcome.Dropped: Dropped++; break;
                default: Info++; break;
            }
        }
    }

    /// <summary>
    /// The import report is the contract of the connector: every property that is not
    /// represented exactly has an entry here. Nothing is approximated silently.
    /// </summary>
    public sealed class ImportReport
    {
        public const int SchemaVersion = 1;

        public string SourceFile;
        public string Page;
        public string Frame;
        public string UxmlPath;
        public string UssPath;
        public string Profile;
        public string UnityVersion = "6.6";
        public string UnityEditorVersion;
        public bool UssGradients;
        public bool DropShadowFilter;
        public bool FlexGap;
        public DateTime GeneratedAt = DateTime.Now;
        public string FigCraftVersion;
        public readonly List<ImportIssue> Issues = new List<ImportIssue>();
        public readonly IssueStats Stats = new IssueStats();
        public readonly List<BakeJob> Bakes = new List<BakeJob>();
        public readonly List<string> Notes = new List<string>();

        readonly HashSet<int> _nonExactNodes = new HashSet<int>();

        public bool HasErrors => Issues.Any(i => i.Severity == IssueSeverity.Error);
        public int Count(IssueSeverity s) => Issues.Count(i => i.Severity == s);

        /// <summary>Called once the walk is done: exact = walked nodes with no lossy issue.</summary>
        public void Finish()
        {
            Stats.Exact = Math.Max(0, Stats.Nodes - _nonExactNodes.Count);
        }

        public ImportIssue Add(FigNodeRef node, string property, string sourceValue, string targetValue,
            ConversionOutcome outcome, IssueSeverity severity, string rule, string message, ReportFix fix = null)
        {
            var issue = new ImportIssue
            {
                NodeId = node.Id,
                NodeName = node.Name,
                NodePath = node.Path,
                Property = property,
                SourceValue = sourceValue,
                TargetValue = targetValue,
                Outcome = outcome,
                Severity = severity,
                Rule = rule,
                Message = message,
                Fix = fix,
            };
            Issues.Add(issue);
            Stats.Add(outcome);
            if (outcome == ConversionOutcome.Approximated || outcome == ConversionOutcome.Degraded ||
                outcome == ConversionOutcome.Dropped)
                _nonExactNodes.Add(node.Id);
            return issue;
        }

        public JObject ToJson()
        {
            var issues = new JArray();
            foreach (var i in Issues)
            {
                var o = new JObject
                {
                    ["nodeId"] = i.NodeId,
                    ["nodePath"] = i.NodePath,
                    ["property"] = i.Property,
                    ["figcraft"] = i.SourceValue,
                    ["unity"] = i.TargetValue,
                    ["outcome"] = i.Outcome.ToString().ToLowerInvariant(),
                    ["severity"] = i.Severity.ToString().ToLowerInvariant(),
                    ["rule"] = i.Rule,
                    ["message"] = i.Message,
                };
                if (i.Fix != null)
                {
                    var fix = new JObject { ["kind"] = i.Fix.Kind };
                    if (i.Fix.Required.HasValue) fix["required"] = i.Fix.Required.Value;
                    if (!string.IsNullOrEmpty(i.Fix.Asset)) fix["asset"] = i.Fix.Asset;
                    if (!string.IsNullOrEmpty(i.Fix.Command)) fix["command"] = i.Fix.Command;
                    o["fix"] = fix;
                }
                issues.Add(o);
            }

            var bakes = new JArray();
            foreach (var b in Bakes)
                bakes.Add(new JObject
                {
                    ["nodeId"] = b.NodeId,
                    ["nodePath"] = b.NodePath,
                    ["reason"] = b.Reason,
                    ["format"] = b.Format,
                    ["output"] = b.OutputRelativePath,
                    ["scale"] = b.Scale,
                });

            return new JObject
            {
                ["reportVersion"] = SchemaVersion,
                ["generatedAt"] = GeneratedAt.ToString("o"),
                ["profile"] = Profile,
                ["target"] = new JObject
                {
                    ["unity"] = UnityVersion,
                    ["editor"] = UnityEditorVersion ?? UnityVersion,
                    ["ussGradients"] = UssGradients,
                    ["dropShadowFilter"] = DropShadowFilter,
                    ["flexGap"] = FlexGap,
                },
                ["source"] = new JObject
                {
                    ["file"] = SourceFile,
                    ["page"] = Page,
                    ["frame"] = Frame,
                    ["figcraftVersion"] = FigCraftVersion,
                },
                ["output"] = new JObject { ["uxml"] = UxmlPath, ["uss"] = UssPath },
                ["stats"] = new JObject
                {
                    ["nodes"] = Stats.Nodes,
                    ["exact"] = Stats.Exact,
                    ["approximated"] = Stats.Approximated,
                    ["degraded"] = Stats.Degraded,
                    ["dropped"] = Stats.Dropped,
                    ["info"] = Stats.Info,
                    ["errors"] = Count(IssueSeverity.Error),
                    ["warnings"] = Count(IssueSeverity.Warning),
                },
                ["bakes"] = bakes,
                ["issues"] = issues,
                ["notes"] = new JArray(Notes),
            };
        }

        public void Save(string path)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, ToJson().ToString(Formatting.Indented));
        }

        public string ToConsole(int maxIssues = 40)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Import report | {Frame} | profile {Profile}");
            sb.AppendLine($"  nodes {Stats.Nodes} | exact {Stats.Exact} | approximated {Stats.Approximated} | degraded {Stats.Degraded} | dropped {Stats.Dropped} | info {Stats.Info}");
            sb.AppendLine($"  errors {Count(IssueSeverity.Error)} | warnings {Count(IssueSeverity.Warning)} | bakes {Bakes.Count}");

            var ordered = Issues
                .Where(i => i.Severity != IssueSeverity.Info)
                .OrderBy(i => i.Severity)
                .ThenBy(i => i.NodePath)
                .Take(maxIssues)
                .ToList();

            if (ordered.Count > 0) sb.AppendLine();
            foreach (var i in ordered)
            {
                sb.AppendLine($"  [{i.Severity.ToString().ToUpperInvariant(),-7}] {i.NodePath} | {i.Property}: {i.Message}");
                if (!string.IsNullOrEmpty(i.TargetValue))
                    sb.AppendLine($"            -> {i.TargetValue}");
            }

            var rest = Issues.Count(i => i.Severity != IssueSeverity.Info) - ordered.Count;
            if (rest > 0) sb.AppendLine($"  ... {rest} more in report.json");
            return sb.ToString();
        }
    }

    /// <summary>Just enough of a node to report on it without holding the whole document.</summary>
    public readonly struct FigNodeRef
    {
        public readonly int Id;
        public readonly string Name;
        public readonly string Path;

        public FigNodeRef(int id, string name, string path)
        {
            Id = id; Name = name; Path = path;
        }
    }

    public sealed class BakeJob
    {
        public int NodeId;
        public string NodePath;
        public string Reason;
        public string Format;              // svg | png
        public string OutputRelativePath;  // relative to the USS folder
        public double Scale = 2;

        /// <summary>FigCraft CLI invocation. <paramref name="outputRoot"/> is the USS folder; OutputRelativePath
        /// already contains the bake folder, so it must not be appended twice.</summary>
        public string ToCliArguments(string inputFile, string outputRoot)
        {
            var target = Path.Combine(outputRoot, OutputRelativePath.Replace('/', Path.DirectorySeparatorChar));
            return $"export \"{inputFile}\" -o \"{target}\" " +
                   $"--node {NodeId} --format {Format}" + (Format == "png" ? $" --scale {Numerics(Scale)}" : "");
        }

        static string Numerics(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }
}
