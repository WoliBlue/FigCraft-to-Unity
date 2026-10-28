using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using FigCraft.Connector.Conversion;
using FigCraft.Connector.Loading;
using Newtonsoft.Json.Linq;

namespace FigCraft.Connector.Cli
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0 || args[0] == "--help" || args[0] == "help")
                {
                    PrintHelp();
                    return 0;
                }

                switch (args[0])
                {
                    case "convert": return Convert(args.Skip(1).ToArray());
                    case "push": return Push(args.Skip(1).ToArray());
                    case "report": return Report(args.Skip(1).ToArray());
                    default:
                        Console.Error.WriteLine($"Unknown command '{args[0]}'.");
                        PrintHelp();
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                if (Environment.GetEnvironmentVariable("FIGCRAFT_DEBUG") == "1")
                    Console.Error.WriteLine(ex);
                return 1;
            }
        }

        static void PrintHelp()
        {
            Console.WriteLine(@"figcraft-unity - FigCraft -> Unity UI Toolkit connector (core CLI)

USAGE
  figcraft-unity convert --input <file.figcraft|document.json> [options]
  figcraft-unity push    --input <file.figcraft> --script <patch.json> [--out <file>]
  figcraft-unity report  --input <report.json>

CONVERT OPTIONS
  --frame <name|id>       Frame to convert (default: first top-level frame)
  --out <dir>             Output folder (default: ./Generated)
  --project-path <path>   Same folder as a Unity project path, for the report (e.g. Assets/UI/Generated)
  --profile <name>        strict | production | visual (default: production, or the rules file's)
  --unity-version <x.y>   Target Unity version (default 6.6 = 6000.6.0f1, minimum 6.3)
                          Accepts 6.6, 6000.6 or 6000.6.0f1. Gates: filters 6.3, drop-shadow and
                          backdrop-filter 6.6, USS gradients and gap 6.7
  --rules <file.json>     Conversion rules (mapping, bake/drop overrides, fontPadding, textfieldPlaceholder)
  --fonts <file.json>     Font map: { ""Inter"": { ""Bold"": ""Assets/Fonts/Inter-Bold SDF.asset"" } }
  --no-bake               Do not bake vector/gradient assets
  --scale <n>             Bake scale (default 2)
  --figcraft-cli <path>   figcraft-cli executable (default: figcraft-cli from PATH)
  --dry-run               Print bake commands instead of running them
  --quiet                 Only print the summary line

PUSH
  Sends a restricted command script to FigCraft with `figcraft-cli run`. Only layout/style/token
  commands are allowed; structure is one-way by design.

EXIT CODES
  0 ok | 1 error | 3 converted but the report has errors (strict profile)
");
        }

        // ------------------------------------------------------------------ convert

        static int Convert(string[] args)
        {
            var a = Args.Parse(args);
            var input = a.Require("input");
            var output = a.Get("out", Path.Combine(Directory.GetCurrentDirectory(), "Generated"));
            var profile = a.Get("profile", null);
            var rulesPath = a.Get("rules", null);
            var fontsPath = a.Get("fonts", null);
            var frame = a.Get("frame", null);
            var projectPath = a.Get("project-path", "");
            var bake = !a.Has("no-bake");
            var dryRun = a.Has("dry-run");
            var quiet = a.Has("quiet");

            var doc = FigCraftReader.Load(input);

            var options = new ConversionOptions
            {
                OutputDirectory = Path.GetFullPath(output),
                ProjectPath = projectPath,
                Bake = bake,
                BakeScale = a.GetDouble("scale", 2),
                FigCraftCli = a.Get("figcraft-cli", "figcraft-cli"),
            };

            var unityVersion = a.Get("unity-version", null);
            if (!string.IsNullOrEmpty(unityVersion)) ApplyUnityVersion(options, unityVersion);

            if (!string.IsNullOrEmpty(rulesPath))
            {
                options.Rules = RuleSet.Load(rulesPath);
                if (!string.IsNullOrEmpty(options.Rules.Profile)) options.Profile = options.Rules.Profile;
                options.TextOutlinePaddingLimit = options.Rules.FontPadding;
            }
            if (!string.IsNullOrEmpty(profile))
            {
                if (!Profiles.IsValid(profile)) throw new ArgumentException($"Unknown profile '{profile}'.");
                options.Profile = profile;
            }
            foreach (var f in ConversionOptions.LoadFonts(fontsPath))
                options.Fonts[f.Key] = f.Value;

            var converter = new FigConverter(doc, options);
            var result = converter.Convert(frame);

            if (bake && result.Report.Bakes.Count > 0)
            {
                foreach (var job in result.Report.Bakes)
                    RunBake(options, input, job, dryRun);
            }

            if (!quiet) Console.Write(result.Report.ToConsole());
            Console.WriteLine();
            var editorLabel = options.UnityEditorVersion != null && options.UnityEditorVersion != options.UnityVersion
                ? $" ({options.UnityEditorVersion})"
                : "";
            Console.WriteLine($"target  Unity {options.UnityVersion}{editorLabel}" +
                              $" | gradients {(options.SupportsUssGradients ? "native" : "baked")}" +
                              $" | shadows {(options.SupportsDropShadow ? "filter" : "not emitted")}" +
                              $" | gap {(options.SupportsFlexGap ? "native" : "margins")}");
            Console.WriteLine($"uxml    {result.UxmlPath}");
            Console.WriteLine($"uss     {result.UssPath}");
            Console.WriteLine($"report  {result.ReportPath}");
            if (result.Report.Bakes.Count > 0)
                Console.WriteLine($"baked   {result.Report.Bakes.Count} asset(s) -> {Path.Combine(options.OutputDirectory, options.BakeFolder)}");

            return result.Report.HasErrors && options.Profile == Profiles.Strict ? 3 : 0;
        }

        static void ApplyUnityVersion(ConversionOptions options, string value)
        {
            var parts = value.Trim().Split('.');
            if (parts.Length < 2 ||
                !int.TryParse(parts[0], out var major) ||
                !int.TryParse(parts[1], out var minor))
                throw new ArgumentException($"--unity-version must look like 6.6, 6000.6 or 6000.6.0f1 (got '{value}').");

            // Unity names 6.6 as 6000.6.0f1 on disk; both spellings mean the same generation.
            if (major >= 1000) major /= 1000;

            if (major < 6 || (major == 6 && minor < 3))
                throw new ArgumentException($"Unity {value} is not supported: the connector needs 6.3 or later.");

            options.UnityMajor = major;
            options.UnityMinor = minor;
            options.UnityEditorVersion = value.Trim();
        }

        static void RunBake(ConversionOptions options, string input, BakeJob job, bool dryRun)
        {
            var target = Path.Combine(options.OutputDirectory, job.OutputRelativePath.Replace('/', Path.DirectorySeparatorChar));
            var targetDir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

            var args = job.ToCliArguments(Path.GetFullPath(input), options.OutputDirectory);
            if (dryRun)
            {
                Console.WriteLine($"  bake {job.Format,-4} {job.OutputRelativePath}  |  {options.FigCraftCli} {args}");
                return;
            }

            var psi = new ProcessStartInfo(options.FigCraftCli, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            try
            {
                using var p = Process.Start(psi);
                var stdout = p.StandardOutput.ReadToEnd();
                var stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0)
                    Console.Error.WriteLine($"  bake failed ({job.OutputRelativePath}): {stderr.Trim()} {stdout.Trim()}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  bake skipped ({job.OutputRelativePath}): {ex.Message}. " +
                                        "Pass --figcraft-cli with the full path or --no-bake.");
            }
        }

        // --------------------------------------------------------------------- push

        static readonly string[] AllowedPushCommands =
        {
            "node.set", "variable.set", "variable.bind", "style.update", "style.apply", "style.bind",
            "select.byName", "select.set", "select.children", "node.get", "variable.list", "style.list",
        };

        static int Push(string[] args)
        {
            var a = Args.Parse(args);
            var input = a.Require("input");
            var script = a.Require("script");
            var output = a.Get("out", null);
            var dryRun = a.Has("dry-run");
            var cli = a.Get("figcraft-cli", "figcraft-cli");

            var steps = JArray.Parse(File.ReadAllText(script));
            var rejected = steps
                .OfType<JObject>()
                .Select(s => s.Value<string>("command"))
                .Where(c => c == null || !AllowedPushCommands.Contains(c))
                .Distinct()
                .ToList();

            if (rejected.Count > 0)
            {
                Console.Error.WriteLine("push rejected: these commands would change structure or settings:");
                foreach (var r in rejected) Console.Error.WriteLine("  - " + r);
                Console.Error.WriteLine("Allowed: " + string.Join(", ", AllowedPushCommands));
                return 4;
            }

            var cliArgs = $"run \"{Path.GetFullPath(script)}\" --in \"{Path.GetFullPath(input)}\"" +
                          (string.IsNullOrEmpty(output) ? "" : $" --out \"{Path.GetFullPath(output)}\"");

            if (dryRun)
            {
                Console.WriteLine($"{cli} {cliArgs}");
                return 0;
            }

            var psi = new ProcessStartInfo(cli, cliArgs)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Console.Write(stdout);
            if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.Write(stderr);
            Console.WriteLine(p.ExitCode == 0
                ? $"push applied ({steps.Count} step(s))" + (string.IsNullOrEmpty(output) ? "" : $" -> {output}")
                : $"push failed with exit code {p.ExitCode}");
            return p.ExitCode;
        }

        // ------------------------------------------------------------------- report

        static int Report(string[] args)
        {
            var a = Args.Parse(args);
            var path = a.Require("input");
            var root = JObject.Parse(File.ReadAllText(path));
            var stats = root["stats"] as JObject;
            Console.WriteLine($"{root["source"]?["frame"]} | profile {root["profile"]} | Unity {root["target"]?["unity"]}");
            Console.WriteLine($"nodes {stats?["nodes"]} | exact {stats?["exact"]} | approximated {stats?["approximated"]} | " +
                              $"degraded {stats?["degraded"]} | dropped {stats?["dropped"]} | errors {stats?["errors"]}");

            foreach (var issue in (root["issues"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var severity = issue.Value<string>("severity");
                if (severity == "info") continue;
                Console.WriteLine($"  [{severity}] {issue.Value<string>("nodePath")} | {issue.Value<string>("property")}: {issue.Value<string>("message")}");
            }
            return 0;
        }

        // -------------------------------------------------------------------- args

        sealed class Args
        {
            readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            readonly HashSet<string> _flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public static Args Parse(string[] args)
            {
                var result = new Args();
                for (var i = 0; i < args.Length; i++)
                {
                    var arg = args[i];
                    if (!arg.StartsWith("--")) continue;
                    var key = arg.Substring(2);
                    var eq = key.IndexOf('=');
                    if (eq >= 0)
                    {
                        result._values[key.Substring(0, eq)] = key.Substring(eq + 1);
                        continue;
                    }
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    {
                        result._values[key] = args[++i];
                    }
                    else
                    {
                        result._flags.Add(key);
                    }
                }
                return result;
            }

            public bool Has(string key) => _flags.Contains(key) || _values.ContainsKey(key);
            public string Get(string key, string fallback) => _values.TryGetValue(key, out var v) ? v : fallback;
            public string Require(string key) => _values.TryGetValue(key, out var v)
                ? v
                : throw new ArgumentException($"missing --{key}");
            public double GetDouble(string key, double fallback) =>
                _values.TryGetValue(key, out var v) && double.TryParse(v, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : fallback;
        }
    }
}
