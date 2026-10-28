using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FigCraft.Connector.Conversion;
using FigCraft.Connector.Loading;
using FigCraft.Connector.Model;

namespace FigCraft.Connector.Tests
{
    /// <summary>
    /// Dependency-free test runner for the conversion core. It is a console app so the checks can run
    /// without a test framework (and without a Unity install), which is the point of the core library.
    ///
    ///   dotnet run --project tests/FigCraft.Connector.Tests            run everything
    ///   dotnet run --project tests/FigCraft.Connector.Tests -- --update   refresh the golden files
    /// </summary>
    internal static class Program
    {
        static int Main(string[] args)
        {
            var updateGoldens = args.Contains("--update");
            var filter = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));

            var tests = new List<(string name, Action run)>
            {
                ("reader: .figcraft archive", ReaderLoadsArchive),
                ("reader: colours", ReaderParsesColours),
                ("generate: UXML and USS are well formed (6.3)", () => GeneratedFilesAreValid("6.3")),
                ("generate: UXML and USS are well formed (6.7)", () => GeneratedFilesAreValid("6.7")),
                ("generate: default target is 6.6", DefaultTargetIsUnity66),
                ("generate: version gates (6.3 vs 6.7)", VersionGates),
                ("effects: backdrop blur on 6.6", BackdropBlur),
                ("fidelity: text outline alignment", TextOutlineAlignment),
                ("fidelity: thick outline vs SDF padding", TextOutlinePadding),
                ("fidelity: shadow over outline", TextShadowOverOutline),
                ("fidelity: angular gradient", AngularGradient),
                ("layout: gap needs 6.7", GapIsEmulatedBeforeUnity67),
                ("layout: grid gap keeps rows", GridGapKeepsRows),
                ("effects: container shadow uses an underlay", ContainerShadowUsesUnderlay),
                ("styles: effect style is emitted once", EffectStyleIsEmittedOnce),
                ("text: case, decoration and alignment", TextCaseIsApplied),
                ("strokes: dashes are baked", DashedStrokeIsBaked),
                ("tokens: values, units, aliases and themes", TokensAndThemes),
                ("components: mapping and flattening", ComponentMapping),
                ("report: contract and stats", ReportContract),
                ("profiles: strict refuses lossy output", StrictProfileRefusesLoss),
                ("output: deterministic", OutputIsDeterministic),
                ("golden: sample screens", () => GoldenSample(updateGoldens)),
            };

            var failed = 0;
            foreach (var (name, run) in tests)
            {
                if (filter != null && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var before = Failures.Count;
                try
                {
                    run();
                }
                catch (Exception ex)
                {
                    Failures.Add($"  threw {ex.GetType().Name}: {ex.Message}");
                }

                var ok = Failures.Count == before;
                if (!ok) failed++;
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
                if (!ok)
                    foreach (var f in Failures.Skip(before))
                        Console.WriteLine("        " + f);
            }

            Console.WriteLine();
            Console.WriteLine($"{Checks} checks, {Failures.Count} failure(s){(updateGoldens ? " (goldens updated)" : "")}");
            return failed == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------ harness

        static readonly List<string> Failures = new List<string>();
        static int Checks;

        static void Check(bool condition, string what, string detail = null)
        {
            Checks++;
            if (!condition) Failures.Add(detail == null ? what : $"{what} ({detail})");
        }

        static void Eq(object expected, object actual, string what)
        {
            Checks++;
            if (!Equals(expected, actual))
                Failures.Add($"{what} (expected '{expected}', got '{actual}')");
        }

        static void Contains(string text, string needle, string what)
        {
            Checks++;
            if (text == null || text.IndexOf(needle, StringComparison.Ordinal) < 0)
                Failures.Add($"{what} (missing '{needle}')");
        }

        static void DoesNotContain(string text, string needle, string what)
        {
            Checks++;
            if (text != null && text.IndexOf(needle, StringComparison.Ordinal) >= 0)
                Failures.Add($"{what} (unexpected '{needle}')");
        }

        // ------------------------------------------------------------------ helpers

        static string Root
        {
            get
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null && !File.Exists(Path.Combine(dir.FullName, "samples", "shop.figcraft")))
                    dir = dir.Parent;
                if (dir == null) throw new InvalidOperationException("repository root not found from " + AppContext.BaseDirectory);
                return dir.FullName;
            }
        }

        static string Sample(string name) => Path.Combine(Root, "samples", name);

        static string NewOutput()
        {
            var dir = Path.Combine(Path.GetTempPath(), "figcraft-unity-tests", Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        static FigDocument SampleDoc() => FigCraftReader.Load(Sample("shop.figcraft"));

        static ConversionResult Convert(FigDocument doc, string profile = null, int minor = 6, bool bake = true)
        {
            var options = new ConversionOptions
            {
                OutputDirectory = NewOutput(),
                Bake = bake,
                UnityMajor = 6,
                UnityMinor = minor,
                Profile = profile ?? Profiles.Production,
            };
            return new FigConverter(doc, options).Convert();
        }

        static ImportIssue Issue(ImportReport report, string rule) =>
            report.Issues.FirstOrDefault(i => i.Rule == rule);

        static bool Has(ImportReport report, string rule) => Issue(report, rule) != null;

        static string Read(string path) => File.ReadAllText(path).Replace("\r\n", "\n");

        sealed class Graph
        {
            readonly Dictionary<int, FigNode> _nodes = new Dictionary<int, FigNode>();

            public FigNode Make(int id, string name, FigNodeType type, double w, double h)
            {
                var n = new FigNode
                {
                    Id = id,
                    Name = name,
                    Type = type,
                    RawType = type == FigNodeType.Text ? "text" : "frame",
                    Width = w,
                    Height = h,
                };
                _nodes[id] = n;
                return n;
            }

            public FigNode Frame(int id, string name, double w, double h, params FigNode[] children) =>
                Parent(Make(id, name, FigNodeType.Frame, w, h), children);

            public FigNode Text(int id, string name, string chars, double w, double h)
            {
                var node = Make(id, name, FigNodeType.Text, w, h);
                node.Characters = chars;
                return node;
            }

            public FigNode Parent(FigNode node, params FigNode[] children)
            {
                foreach (var child in children)
                {
                    node.Children.Add(child.Id);
                    child.Parent = node.Id;
                }
                return node;
            }

            public FigDocument Build(FigNode frame)
            {
                var doc = new FigDocument
                {
                    Name = "test",
                    CurrentPage = 1,
                    Pages = new List<int> { 1 },
                };
                var page = new FigNode { Id = 1, Name = "Page 1", Type = FigNodeType.Page };
                doc.Nodes[1] = page;
                page.Children.Add(frame.Id);
                frame.Parent = 1;
                Put(doc, frame);
                return doc;
            }

            void Put(FigDocument doc, FigNode node)
            {
                doc.Nodes[node.Id] = node;
                foreach (var id in node.Children)
                    if (_nodes.TryGetValue(id, out var child)) Put(doc, child);
            }
        }

        static FigTextStyle TextStyle(double size, string style = "Regular") =>
            new FigTextStyle { FontFamily = "Inter", FontStyle = style, FontSize = size };

        static FigPaint Solid(FigColor color) => new FigPaint { Type = "solid", Color = color };

        static FigPaint Gradient(string kind, params (double pos, FigColor color)[] stops) => new FigPaint
        {
            Type = "gradient",
            Kind = kind,
            Stops = stops.Select(s => new FigGradientStop { Position = s.pos, Color = s.color }).ToList(),
        };

        // ------------------------------------------------------------------- tests

        static void ReaderLoadsArchive()
        {
            var doc = SampleDoc();
            Check(doc.Nodes.Count > 10, "nodes parsed", doc.Nodes.Count + " nodes");
            Check(doc.Collections.Count >= 1, "variable collections parsed");
            Check(doc.Variables.Count >= 4, "variables parsed", doc.Variables.Count + " variables");
            Check(doc.Styles.Count >= 3, "styles parsed", doc.Styles.Count + " styles");

            var frame = doc.Nodes.Values.FirstOrDefault(n => n.Name == "Screen/Login");
            Check(frame != null && frame.Type == FigNodeType.Frame, "frame by name");
            Check(frame != null && frame.Layout.Mode == "vertical", "auto layout parsed");
            Check(frame != null && frame.Children.Count > 5, "children parsed", frame?.Children.Count + " children");
            Check(!string.IsNullOrEmpty(doc.PathOf(frame)), "node path", doc.PathOf(frame));
        }

        static void ReaderParsesColours()
        {
            Eq("#336699", FigCraftReader.ParseColorString("#369").ToHex(), "short hex");
            Eq("#11223344", FigCraftReader.ParseColorString("#11223344").ToHex(), "hex with alpha");
            Eq("rgba(255, 0, 0, 0.5)", FigCraftReader.ParseColorString("rgba(255, 0, 0, 0.5)").ToUss(), "rgba()");
        }

        static void GeneratedFilesAreValid(string version)
        {
            var minor = version.EndsWith("7") ? 7 : 3;
            var doc = SampleDoc();
            var result = Convert(doc, Profiles.Production, minor);

            Check(File.Exists(result.UxmlPath), "uxml written", result.UxmlPath);

            UxmlIsValid(result);
            foreach (var uss in new[] { result.UssPath, result.TokensPath, result.ThemesPath, result.StylesPath })
            {
                Check(File.Exists(uss), "uss written", uss);
                ValidateUss(uss, minor);
            }
        }

        static void UxmlIsValid(ConversionResult result)
        {
            var xml = XDocument.Parse(File.ReadAllText(result.UxmlPath));
            Eq("UXML", xml.Root?.Name.LocalName, "UXML root element");

            var root = Path.GetDirectoryName(result.UxmlPath);
            foreach (var style in xml.Descendants().Where(e => e.Name.LocalName == "Style"))
            {
                var src = (string)style.Attribute("src");
                Check(File.Exists(Path.Combine(root, src)), "style sheet referenced by the UXML exists next to it", src);
            }

            var names = xml.Descendants().Select(e => (string)e.Attribute("name")).Where(n => n != null).ToList();
            Eq(names.Count, names.Distinct().Count(), "element names are unique");

            var classes = xml.Descendants().Select(e => (string)e.Attribute("class")).Where(c => c != null).ToList();
            Check(classes.Count > 0, "elements carry classes");
        }

        /// <summary>The USS properties UI Toolkit 6.3 understands. Anything outside this set means the
        /// generated sheet would be silently ignored by the importer, which is the failure mode this
        /// connector exists to avoid.</summary>
        static readonly HashSet<string> UssProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "align-content", "align-items", "align-self", "all", "aspect-ratio",
            "backdrop-filter",
            "background-color", "background-image", "background-position", "background-position-x",
            "background-position-y", "background-repeat", "background-size",
            "border-bottom-color", "border-bottom-left-radius", "border-bottom-right-radius", "border-bottom-width",
            "border-color", "border-left-color", "border-left-width", "border-radius",
            "border-right-color", "border-right-width", "border-top-color", "border-top-left-radius",
            "border-top-right-radius", "border-top-width", "border-width",
            "bottom", "color", "cursor", "display", "filter",
            "flex", "flex-basis", "flex-direction", "flex-grow", "flex-shrink", "flex-wrap",
            "font-size", "height", "width", "justify-content", "left", "letter-spacing",
            "margin", "margin-bottom", "margin-left", "margin-right", "margin-top",
            "max-height", "max-width", "min-height", "min-width", "opacity", "overflow",
            "padding", "padding-bottom", "padding-left", "padding-right", "padding-top",
            "position", "right", "rotate", "scale", "text-overflow", "text-shadow", "top",
            "transform-origin", "transition", "transition-delay", "transition-duration",
            "transition-property", "transition-timing-function", "translate", "visibility",
            "white-space", "word-spacing",
        };

        static void ValidateUss(string path, int unityMinor)
        {
            var fileName = Path.GetFileName(path);
            var text = Read(path);
            var stripped = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);

            Eq(stripped.Count(c => c == '{'), stripped.Count(c => c == '}'), $"{fileName}: balanced braces");
            DoesNotContain(stripped, "box-shadow", $"{fileName}: no box-shadow (not a USS property)");

            if (unityMinor < 7)
            {
                DoesNotContain(stripped, "row-gap", $"{fileName}: no row-gap before 6.7");
                DoesNotContain(stripped, "column-gap", $"{fileName}: no column-gap before 6.7");
                DoesNotContain(stripped, "gradient(", $"{fileName}: no USS gradients before 6.7");
            }

            var inBlock = 0;
            var lineNo = 0;
            foreach (var raw in stripped.Split('\n'))
            {
                lineNo++;
                var line = raw.Trim();
                if (line.Length == 0) continue;

                if (line.EndsWith("{")) { inBlock++; continue; }
                if (line == "}") { inBlock--; continue; }
                if (inBlock <= 0) { Failures.Add($"{fileName}:{lineNo}: declaration outside a block: {line}"); Checks++; continue; }

                if (!line.EndsWith(";"))
                {
                    Checks++;
                    Failures.Add($"{fileName}:{lineNo}: declaration without ';': {line}");
                    continue;
                }

                var colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    Checks++;
                    Failures.Add($"{fileName}:{lineNo}: malformed declaration: {line}");
                    continue;
                }

                var property = line.Substring(0, colon).Trim();
                var value = line.Substring(colon + 1).Trim().TrimEnd(';').Trim();

                if (property.StartsWith("--", StringComparison.Ordinal)) continue;   // custom property
                if (property.StartsWith("-unity-", StringComparison.Ordinal)) continue;
                if (unityMinor >= 7 && (property == "gap" || property == "row-gap" || property == "column-gap")) continue;
                if (unityMinor < 6 && property == "backdrop-filter") continue;

                Checks++;
                if (!UssProperties.Contains(property))
                    Failures.Add($"{fileName}:{lineNo}: '{property}' is not a USS property for Unity 6.{unityMinor}");

                if (property == "justify-content" && value.Contains("space-evenly"))
                    Failures.Add($"{fileName}:{lineNo}: justify-content: space-evenly is not supported by USS");
                if (property == "text-shadow" && !Regex.IsMatch(value, @"^([-0-9.]+px\s+){3}.+$"))
                    Failures.Add($"{fileName}:{lineNo}: text-shadow takes three lengths and a colour: {value}");
                if (property == "border-radius" && value.Contains("%"))
                    Failures.Add($"{fileName}:{lineNo}: border-radius percentages are not reliable in USS: {value}");
                if (property == "filter" && value.Contains("drop-shadow") && unityMinor < 6)
                    Failures.Add($"{fileName}:{lineNo}: filter: drop-shadow() requires Unity 6.6+");
                if (property == "backdrop-filter" && unityMinor < 6)
                    Failures.Add($"{fileName}:{lineNo}: backdrop-filter requires Unity 6.6+");
            }

            Eq(0, inBlock, $"{fileName}: every block is closed");
        }

        static void DefaultTargetIsUnity66()
        {
            var options = new ConversionOptions { OutputDirectory = NewOutput() };
            Eq("6.6", options.UnityVersion, "default target is 6.6 (6000.6.0f1)");
            Check(options.SupportsDropShadow, "6.6 has filter: drop-shadow()");
            Check(options.SupportsBackdropFilters, "6.6 has backdrop-filter");
            Check(!options.SupportsUssGradients, "6.6 has no USS gradients yet");
            Check(!options.SupportsFlexGap, "6.6 has no flex gap yet");

            var sample = new FigConverter(SampleDoc(), options).Convert();
            var uss = Read(sample.UssPath);
            var sheets = uss + "\n" + Read(sample.StylesPath);
            Contains(sheets, "filter: drop-shadow(", "sample emits a native drop shadow on 6.6");
            DoesNotContain(uss, "gradient(", "sample still bakes gradients on 6.6");
            DoesNotContain(uss, "row-gap", "sample emulates gap with margins on 6.6");
        }

        static void BackdropBlur()
        {
            var g = new Graph();
            var panel = g.Make(2, "Panel", FigNodeType.Rectangle, 300, 200);
            panel.Fills.Add(Solid(new FigColor(1, 1, 1, 0.2)));
            panel.Effects.Add(new FigEffect { Type = "backgroundBlur", Radius = 12 });
            var doc = g.Build(g.Frame(5, "Screen", 400, 300, panel));

            var six = Convert(doc, Profiles.Production, 6);
            Contains(Read(six.UssPath), "backdrop-filter: blur(12px)", "6.6: backdrop blur is emitted");
            var issue = Issue(six.Report, "effect.backgroundBlur.backdropFilter");
            Eq(ConversionOutcome.Approximated, issue?.Outcome, "backdrop blur is approximated (URP, screen-space only)");
            Check(issue != null && issue.Message.Contains("URP"), "the report names the URP limitation");

            var three = Convert(doc, Profiles.Production, 3);
            DoesNotContain(Read(three.UssPath), "backdrop-filter", "6.3: no backdrop-filter");
            Check(Has(three.Report, "effect.backgroundBlur.dropped"), "6.3: backdrop blur reported as dropped");
        }

        static void VersionGates()
        {
            var doc = SampleDoc();
            var old = Convert(doc, Profiles.Production, 3);
            var six = Convert(doc, Profiles.Production, 6);
            var modern = Convert(doc, Profiles.Production, 7);

            var oldUss = Read(old.UssPath) + "\n" + Read(old.StylesPath);
            var newUss = Read(modern.UssPath) + "\n" + Read(modern.StylesPath);
            var sixUss = Read(six.UssPath) + "\n" + Read(six.StylesPath);

            DoesNotContain(oldUss, "gradient(", "6.3: gradients are baked");
            Contains(oldUss, "baked/card-promo-fill.png", "6.3: promo gradient baked");
            Contains(newUss, "linear-gradient(", "6.7: linear gradient emitted");
            Contains(newUss, "filter: drop-shadow(", "6.7: drop shadow filter emitted");
            Contains(sixUss, "filter: drop-shadow(", "6.6: drop shadow filter emitted");
            DoesNotContain(oldUss, "filter: drop-shadow(", "6.3: no drop-shadow filter");
            DoesNotContain(oldUss, "row-gap", "6.3: no row-gap");
            Contains(newUss, "row-gap: var(--space-gutter)", "6.7: native gap tokens");

            Check(old.Report.Bakes.Any(b => b.OutputRelativePath.EndsWith("card-promo-fill.png")),
                "6.3: gradient bake job recorded");
            Check(modern.Report.Bakes.All(b => !b.OutputRelativePath.Contains("card-promo")),
                "6.7: linear gradient is not baked");
            Check(old.Report.Issues.Any(i => i.Rule == "effect.dropShadow.needsUnity66"), "6.3: shadow dropped with a rule");
            Check(old.Report.Issues.Any(i => i.Rule == "layout.gap.margins"), "6.3: gap emulated with margins");
            Check(!modern.Report.Issues.Any(i => i.Rule == "layout.gap.margins"), "6.7: native gaps, no emulation");
        }

        static void TextOutlineAlignment()
        {
            foreach (var align in new[] { "inside", "center", "outside" })
            {
                var g = new Graph();
                var text = g.Text(2, "Title", "Hello", 200, 40);
                text.TextStyle = TextStyle(32);
                text.Strokes.Add(Solid(FigColor.Black));
                text.Stroke = new FigStroke { Weight = 2, Align = align };

                var doc = g.Build(g.Frame(5, "Screen", 400, 300, text));
                var result = Convert(doc);
                var uss = Read(result.UssPath);

                var expected = align == "center" ? "2px" : "4px";
                Contains(uss, $"-unity-text-outline-width: {expected}", $"{align}: outline width");

                if (align == "center")
                    Check(!Has(result.Report, "text.outline.align.folded"), "center outline is exact");
                else
                {
                    var issue = Issue(result.Report, "text.outline.align.folded");
                    Check(issue != null, $"{align}: alignment folded into width", "rule missing");
                    Eq(ConversionOutcome.Approximated, issue?.Outcome, $"{align}: outcome");
                }
            }
        }

        static void TextOutlinePadding()
        {
            var g = new Graph();
            var text = g.Text(2, "Big", "Hello", 200, 40);
            text.TextStyle = TextStyle(32);
            text.Strokes.Add(Solid(FigColor.Black));
            text.Stroke = new FigStroke { Weight = 12, Align = "center" };
            var result = Convert(g.Build(g.Frame(5, "Screen", 400, 300, text)));

            var issue = Issue(result.Report, "text.outline.padding.exceeded");
            Check(issue != null, "thick outline reports the SDF padding limit", "rule missing");
            Eq("fontAssetPadding", issue?.Fix?.Kind, "fix kind");
            Eq(8.0, issue?.Fix?.Required, "required padding (12/2 + 2)");

            // A project that raises the padding is not warned.
            var options = new ConversionOptions { OutputDirectory = NewOutput(), TextOutlinePaddingLimit = 24 };
            var relaxed = new FigConverter(g.Build(g.Frame(5, "Screen", 400, 300, text)), options).Convert();
            Check(!Has(relaxed.Report, "text.outline.padding.exceeded"), "no warning when the FontAsset has room");
        }

        static FigDocument ShadowOverOutlineDoc()
        {
            var g = new Graph();
            var text = g.Text(2, "Outlined", "OUTLINE", 220, 48);
            text.TextStyle = TextStyle(32, "Bold");
            text.Strokes.Add(Solid(new FigColor(0.1, 0.1, 0.1)));
            text.Stroke = new FigStroke { Weight = 3, Align = "outside" };
            text.Effects.Add(new FigEffect { Type = "dropShadow", Offset = new double[] { 2, 4 }, Radius = 6, Spread = 1 });
            return g.Build(g.Frame(5, "Screen", 400, 300, text));
        }

        static void TextShadowOverOutline()
        {
            var result = Convert(ShadowOverOutlineDoc());
            var uxml = Read(result.UxmlPath);
            Eq(2, Regex.Matches(uxml, "<ui:Label").Count, "two Labels (shadow + text)");
            Contains(uxml, "-wrap", "wrapper element");
            Contains(uxml, "picking-mode=\"Ignore\"", "shadow label does not take clicks");

            var issue = Issue(result.Report, "text.shadowWithOutline.emulated");
            Check(issue != null, "emulation is reported", "rule missing");
            Eq(ConversionOutcome.Approximated, issue?.Outcome, "outcome is approximated");
            Check(result.Report.Issues.Any(i => i.Rule == "text.shadow.spread.folded"), "spread loss is reported");

            var strict = Convert(ShadowOverOutlineDoc(), Profiles.Strict);
            Eq(1, Regex.Matches(Read(strict.UxmlPath), "<ui:Label").Count, "strict: one Label");
            var refused = Issue(strict.Report, "text.shadowWithOutline.dropped");
            Check(refused != null, "strict: shadow-over-outline refused", "rule missing");
            Eq(IssueSeverity.Error, refused?.Severity, "strict: reported as an error");
            Check(strict.Report.HasErrors, "strict: report has errors");
        }

        static FigDocument AngularDoc(string kind = "angular")
        {
            var g = new Graph();
            var card = g.Make(2, "Card", FigNodeType.Rectangle, 320, 120);
            card.Fills.Add(Gradient(kind, (0, new FigColor(1, 0, 0)), (1, new FigColor(0, 0, 1))));
            return g.Build(g.Frame(5, "Screen", 400, 300, card));
        }

        static void AngularGradient()
        {
            var degraded = Convert(AngularDoc());
            var uss = Read(degraded.UssPath);
            Contains(uss, "background-image: url(\"baked/card-fill.png\")", "angular gradient is baked");
            Eq(1, degraded.Report.Bakes.Count(b => b.Reason == "gradient.angular"), "one bake job per gradient");
            var issue = Issue(degraded.Report, "gradient.angular.baked");
            Eq(ConversionOutcome.Degraded, issue?.Outcome, "angular gradient outcome");

            var dropped = Convert(AngularDoc(), bake: false);
            DoesNotContain(Read(dropped.UssPath), "baked/card-fill.png", "no bake when baking is off");
            var droppedIssue = Issue(dropped.Report, "gradient.angular.dropped");
            Eq(ConversionOutcome.Dropped, droppedIssue?.Outcome, "dropped outcome");
            Eq(IssueSeverity.Error, droppedIssue?.Severity, "dropping a fill is an error, not a silent omission");

            var linear = Convert(AngularDoc("linear"), Profiles.Production, 3);
            Check(linear.Report.Issues.Any(i => i.Rule == "gradient.linear.baked"),
                "linear gradients are baked on 6.3 as well");
        }

        static void GapIsEmulatedBeforeUnity67()
        {
            var g = new Graph();
            var column = g.Make(2, "Column", FigNodeType.Frame, 300, 200);
            column.Layout = new FigLayout { Mode = "vertical", Gap = 12 };
            var a = g.Make(3, "A", FigNodeType.Rectangle, 100, 20);
            var b = g.Make(4, "B", FigNodeType.Rectangle, 100, 20);
            g.Parent(column, a, b);
            var doc = g.Build(g.Frame(5, "Screen", 400, 300, column));

            var old = Convert(doc, Profiles.Production, 3);
            var oldUss = Read(old.UssPath);
            DoesNotContain(oldUss, "row-gap", "6.3: no row-gap");
            Eq(1, Regex.Matches(oldUss, "margin-top: 12px").Count, "6.3: gap becomes a margin on the second child only");
            DoesNotContain(Block(oldUss, ".fc-a"), "margin-top", "6.3: first child has no gap margin");
            Contains(Block(oldUss, ".fc-b"), "margin-top: 12px", "6.3: second child carries the gap");

            var modern = Convert(doc, Profiles.Production, 7);
            Contains(Read(modern.UssPath), "row-gap: 12px", "6.7: native row-gap");
        }

        static void GridGapKeepsRows()
        {
            var g = new Graph();
            var grid = g.Make(2, "Grid", FigNodeType.Frame, 340, 300);
            grid.Layout = new FigLayout { Mode = "grid", Gap = 8, CrossGap = 10, GridColumns = 3 };
            var a = g.Make(3, "A", FigNodeType.Rectangle, 100, 20);
            var b = g.Make(4, "B", FigNodeType.Rectangle, 100, 20);
            var c = g.Make(5, "C", FigNodeType.Rectangle, 100, 20);
            var d = g.Make(6, "D", FigNodeType.Rectangle, 100, 20);
            g.Parent(grid, a, b, c, d);
            var doc = g.Build(g.Frame(7, "Screen", 400, 400, grid));

            var uss = Read(Convert(doc, Profiles.Production, 3).UssPath);
            DoesNotContain(Block(uss, ".fc-a"), "margin", "first cell has no margin");
            Contains(Block(uss, ".fc-b"), "margin-left: 8px", "second column is spaced on the left");
            DoesNotContain(Block(uss, ".fc-b"), "margin-top", "second column stays on the first row");
            Contains(Block(uss, ".fc-d"), "margin-top: 8px", "second row is spaced on top");
            DoesNotContain(Block(uss, ".fc-d"), "margin-left", "first column of a row has no left margin");
        }

        static FigDocument ShadowDoc(bool clip)
        {
            var g = new Graph();
            var card = g.Make(2, "Card", FigNodeType.Frame, 200, 100);
            card.Clip = clip;
            card.CornerRadii = new double[] { 16, 16, 16, 16 };
            card.Fills.Add(Solid(new FigColor(1, 1, 1)));
            card.Effects.Add(new FigEffect
            {
                Type = "dropShadow",
                Offset = new double[] { 0, 8 },
                Radius = 24,
                Color = new FigColor(0, 0, 0, 0.2),
            });

            var label = g.Text(3, "Label", "Hello", 60, 20);
            label.TextStyle = TextStyle(14);
            g.Parent(card, label);

            return g.Build(g.Frame(4, "Screen", 400, 300, card));
        }

        static void ContainerShadowUsesUnderlay()
        {
            var result = Convert(ShadowDoc(false));
            var uxml = Read(result.UxmlPath);
            var uss = Read(result.UssPath);

            Contains(uxml, "name=\"Card_shadow\"", "the shadow gets its own element");
            Contains(Block(uss, ".fc-card-shadow"), "filter: drop-shadow", "the filter lives on the underlay");
            DoesNotContain(Block(uss, ".fc-card"), "filter", "the container is not filtered");
            Contains(Block(uss, ".fc-card-shadow"), "background-color: #FFFFFF", "the underlay paints the shape");
            Contains(Block(uss, ".fc-card-shadow"), "border-radius: 16px", "the underlay keeps the rounded shape");
            Check(Has(result.Report, "effect.dropShadow.underlay"), "the underlay is reported");

            var clipped = Convert(ShadowDoc(true));
            var clippedUxml = Read(clipped.UxmlPath);
            var clippedUss = Read(clipped.UssPath);
            Contains(clippedUxml, "name=\"Card_wrap\"", "a clipped container is wrapped");
            Contains(clippedUxml, "name=\"Card_shadow\"", "the shadow is a sibling, outside the clip");
            DoesNotContain(Block(clippedUss, ".fc-card"), "filter", "the clipped card is not filtered");
            Contains(Block(clippedUss, ".fc-card-shadow"), "filter: drop-shadow", "the sibling carries the filter");
            Contains(Block(clippedUss, ".fc-card"), "overflow: hidden", "the card still clips its content");
            Contains(Block(clippedUss, ".fc-card"), "position: absolute", "the card fills the wrapper");
            Check(Has(clipped.Report, "effect.dropShadow.wrapper"), "the wrapper is reported");
        }

        static FigDocument EffectStyleDoc()
        {
            var doc = ShadowDoc(false);
            doc.Styles.Add(new FigStyle
            {
                Id = "S:1",
                Name = "Shadow/Card",
                Type = "effect",
                Effects = new List<FigEffect>
                {
                    new FigEffect
                    {
                        Type = "dropShadow",
                        Offset = new double[] { 0, 8 },
                        Radius = 24,
                        Color = new FigColor(0, 0, 0, 0.2),
                    },
                },
            });
            doc.Node(2).StyleRefs["effect"] = "S:1";
            return doc;
        }

        static void EffectStyleIsEmittedOnce()
        {
            var result = Convert(EffectStyleDoc());
            var uss = Read(result.UssPath);
            var styles = Read(result.StylesPath);

            Contains(styles, ".style-shadow-card", "an effect style becomes a class");
            Contains(Block(styles, ".style-shadow-card"), "filter: drop-shadow", "the style owns the filter");
            DoesNotContain(Block(uss, ".fc-card"), "filter", "the node rule does not repeat the style");
            Check(Has(result.Report, "effect.delegatedToStyle"), "delegation is reported");
            Check(!result.Report.Issues.Any(i => i.Rule == "effect.dropShadow.filter" && i.NodeId == 2),
                "no second copy of the same shadow is reported");
        }

        static void TextCaseIsApplied()
        {
            var g = new Graph();
            var title = g.Text(2, "Title", "Hello world", 200, 30);
            title.TextStyle = TextStyle(20, "Bold");
            title.TextStyle.TextCase = "upper";
            title.TextStyle.Decoration = "underline";
            title.TextStyle.VerticalAlign = "bottom";
            var lead = g.Text(8, "Lead", "Loose", 100, 20);
            lead.TextStyle = TextStyle(18, "Regular");
            lead.TextStyle.LineHeightUnit = "percent";
            lead.TextStyle.LineHeightValue = 150;
            var doc = g.Build(g.Frame(3, "Screen", 300, 100, title, lead));

            var result = Convert(doc, Profiles.Production, 6);
            var uxml = Read(result.UxmlPath);
            Contains(uxml, "&lt;u&gt;&lt;uppercase&gt;Hello world&lt;/uppercase&gt;&lt;/u&gt;",
                "case and decoration are applied with rich text tags");
            Contains(uxml, "&lt;line-height=27px&gt;Loose&lt;/line-height&gt;",
                "a percentage line height is written in px and closed by name");
            Contains(uxml, "enable-rich-text=\"true\"", "the label turns rich text on");
            Contains(Read(result.UssPath), "-unity-text-align: lower-left;",
                "the vertical alignment of the text box is kept");
            Check(Has(result.Report, "text.case.richText"), "the case tag is reported");
            Check(Has(result.Report, "text.decoration.richText"), "the decoration tag is reported");
            Check(Has(result.Report, "text.lineHeight.richText"), "the line height tag is reported");

            // Literal markup in the content must not be parsed as tags, with or without transforms.
            var literal = g.Text(4, "Literal", "a < b", 100, 20);
            literal.TextStyle = TextStyle(12, "Regular");
            var withLiteral = Convert(g.Build(g.Frame(5, "Screen", 200, 60, literal)), Profiles.Production, 6);
            Contains(Read(withLiteral.UxmlPath), "&lt;noparse&gt;a &lt; b&lt;/noparse&gt;",
                "tag characters in the text are protected with <noparse>");
        }

        static void DashedStrokeIsBaked()
        {
            var g = new Graph();
            var divider = g.Make(2, "Divider", FigNodeType.Rectangle, 200, 2);
            divider.Strokes.Add(Solid(new FigColor(0.6, 0.6, 0.6)));
            divider.Stroke = new FigStroke
            {
                Weight = 1,
                Align = "inside",
                Dashes = new List<double> { 4, 4 },
            };
            var doc = g.Build(g.Frame(3, "Screen", 300, 100, divider));

            var baked = Convert(doc, Profiles.Production, 6);
            var uss = Read(baked.UssPath);
            Contains(Block(uss, ".fc-divider"), "url(\"baked/divider-stroke.svg\")",
                "the dashed line is baked as SVG");
            DoesNotContain(Block(uss, ".fc-divider"), "border-width",
                "the solid border is not drawn on top of the bake");
            Check(baked.Report.Bakes.Any(b => b.Reason == "stroke" && b.Format == "svg"),
                "the stroke bake is registered");
            Check(Has(baked.Report, "stroke.dashes.baked"), "the bake is reported");

            var noBake = Convert(doc, Profiles.Production, 6, bake: false);
            Check(Has(noBake.Report, "stroke.dashes.dropped"), "without baking the dash loss is an error entry");
            DoesNotContain(Read(noBake.UssPath), "divider-stroke.svg", "no bake, no image");
        }

        static FigDocument TokenDoc()
        {
            var g = new Graph();
            var frame = g.Frame(5, "Screen", 400, 300);
            var doc = g.Build(frame);

            doc.Collections.Add(new FigVariableCollection
            {
                Id = "c1",
                Name = "Theme",
                Modes = new List<FigVariableMode>
                {
                    new FigVariableMode { Id = "m-light", Name = "Light" },
                    new FigVariableMode { Id = "m-dark", Name = "Dark" },
                },
            });

            doc.Variables.Add(new FigVariable
            {
                Id = "v-surface",
                Name = "color/surface",
                CollectionId = "c1",
                ResolvedType = "color",
                Values =
                {
                    ["m-light"] = new FigVariableValue { Type = "color", Color = new FigColor(1, 1, 1) },
                    ["m-dark"] = new FigVariableValue { Type = "color", Color = new FigColor(0.04, 0.04, 0.06) },
                },
            });
            doc.Variables.Add(new FigVariable
            {
                Id = "v-radius",
                Name = "radius/card",
                CollectionId = "c1",
                ResolvedType = "number",
                Values = { ["m-light"] = new FigVariableValue { Type = "number", Number = 16 } },
            });
            doc.Variables.Add(new FigVariable
            {
                Id = "v-opacity",
                Name = "opacity/overlay",
                CollectionId = "c1",
                ResolvedType = "number",
                Values = { ["m-light"] = new FigVariableValue { Type = "number", Number = 0.4 } },
            });
            doc.Variables.Add(new FigVariable
            {
                Id = "v-brand",
                Name = "color/brand",
                CollectionId = "c1",
                ResolvedType = "color",
                Values = { ["m-light"] = new FigVariableValue { Type = "alias", AliasOf = "v-surface" } },
            });
            return doc;
        }

        static void TokensAndThemes()
        {
            var result = Convert(TokenDoc());
            var tokens = Read(result.TokensPath);
            var themes = Read(result.ThemesPath);

            Contains(tokens, ":root {", "tokens declare :root");
            Eq(1, tokens.Count(c => c == '{'), "tokens have a single block");
            Eq(tokens.Count(c => c == '{'), tokens.Count(c => c == '}'), "tokens block is closed");
            Contains(tokens, "--color-surface: #FFFFFF;", "colour token");
            Contains(tokens, "--radius-card: 16px;", "length token gets a unit");
            Contains(tokens, "--opacity-overlay: 0.4;", "unitless token stays unitless");
            Contains(tokens, "--color-brand: var(--color-surface);", "alias resolves to var()");

            Contains(themes, ".theme-light {", "theme class per mode");
            Contains(themes, ".theme-dark {", "second mode class");
            Contains(themes, ".theme-dark {\n    --color-surface: #0A0A0F;", "dark mode overrides the colour");
        }

        static FigDocument ComponentDoc()
        {
            var g = new Graph();
            var button = g.Make(2, "Sign in", FigNodeType.Instance, 200, 48);
            button.MainComponentName = "Button/Primary";
            button.ComponentProps["Label"] = "Sign in";

            var label = g.Text(6, "Label", "Sign in", 70, 20);
            label.RawType = "text";
            label.TextStyle = TextStyle(16, "Bold");
            label.Fills.Add(Solid(new FigColor(1, 1, 1)));
            g.Parent(button, label);

            var input = g.Make(3, "Email", FigNodeType.Instance, 200, 48);
            input.MainComponentName = "Input/Default";
            input.ComponentProps["Placeholder"] = "you@example.com";
            var hint = g.Text(7, "Placeholder", "you@example.com", 160, 20);
            hint.TextStyle = TextStyle(14, "Regular");
            hint.Fills.Add(Solid(new FigColor(0.5, 0.5, 0.5)));
            g.Parent(input, hint);
            var unknown = g.Make(4, "Card", FigNodeType.Instance, 300, 100);
            unknown.MainComponentName = "Promo/Card";
            return g.Build(g.Frame(5, "Screen", 400, 300, button, input, unknown));
        }

        static void ComponentMapping()
        {
            var result = Convert(ComponentDoc());
            var uxml = Read(result.UxmlPath);
            Contains(uxml, "<ui:Button", "Button component maps to <ui:Button>");
            Contains(uxml, "text=\"Sign in\"", "Button text comes from the component property");
            Contains(uxml, "<ui:TextField", "Input component maps to <ui:TextField>");
            Contains(uxml, "value=\"you@example.com\"", "TextField value comes from the component property");

            var buttonBlock = Block(Read(result.UssPath), ".fc-sign-in");
            Contains(buttonBlock, "font-size: 16px", "mapped Button keeps the label size");
            Contains(buttonBlock, "color: #FFFFFF", "mapped Button keeps the label colour");
            Contains(buttonBlock, "-unity-font-style: bold", "mapped Button keeps the label weight");
            Check(Has(result.Report, "component.textStyle.carried"), "carrying the typography is reported");
            Check(Has(result.Report, "component.textfield.placeholderAsValue"), "placeholder-as-value is reported");

            var flattened = Issue(result.Report, "component.flattened");
            Check(flattened != null, "unmapped component is flattened", "rule missing");
            Eq(ConversionOutcome.Degraded, flattened?.Outcome, "flattening is degraded");
            Contains(uxml, "<ui:VisualElement name=\"Card\"", "unmapped component still emits its children");

            var withRules = new ConversionOptions
            {
                OutputDirectory = NewOutput(),
                Rules = RuleSet.Load(Sample(Path.Combine("rules", "production.rules.json"))),
            };
            var ruled = new FigConverter(ComponentDoc(), withRules).Convert();
            Contains(Read(ruled.UxmlPath), "text=\"Sign in\"", "rules file drives the same mapping");

            var emptyPlaceholder = new ConversionOptions
            {
                OutputDirectory = NewOutput(),
                Rules = new RuleSet { TextFieldPlaceholder = "empty" },
            };
            var empty = new FigConverter(ComponentDoc(), emptyPlaceholder).Convert();
            var emptyUxml = Read(empty.UxmlPath);
            Contains(emptyUxml, "<ui:TextField", "the field is still mapped with the empty policy");
            Contains(emptyUxml, "value=\"\"", "textfieldPlaceholder=empty leaves the field empty");
            Check(Has(empty.Report, "component.textfield.placeholderEmpty"),
                "the empty placeholder policy is reported");
            DoesNotContain(Block(Read(empty.UssPath), ".fc-email"),
                "color: #", "the placeholder colour does not leak into the empty field");
        }

        static void ReportContract()
        {
            var result = Convert(SampleDoc());
            var json = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(result.ReportPath));

            Eq(1, (int)json["reportVersion"], "report version");            Eq("6.6", (string)json["target"]?["unity"], "target unity version");
            Eq("6.6", (string)json["target"]?["editor"], "target editor label falls back to the version");
            Check(json["stats"] != null, "stats block");
            Check(json["issues"] is Newtonsoft.Json.Linq.JArray, "issues array");

            var stats = json["stats"];
            var nodes = (int)stats["nodes"];
            Check(nodes > 0, "nodes counted");
            Check((int)stats["exact"] >= 0 && (int)stats["exact"] <= nodes, "exact is within range");
            Eq((int)stats["errors"], result.Report.Count(IssueSeverity.Error), "error count matches the model");
            Eq((int)stats["warnings"], result.Report.Count(IssueSeverity.Warning), "warning count matches the model");

            foreach (var issue in (Newtonsoft.Json.Linq.JArray)json["issues"])
            {
                foreach (var key in new[] { "nodeId", "nodePath", "property", "outcome", "severity", "rule", "message" })
                    Check(issue[key] != null, "issue field", key);
            }

            foreach (var bake in (Newtonsoft.Json.Linq.JArray)json["bakes"])
            {
                Check(bake["output"] != null, "bake output");
                Check(!Path.IsPathRooted((string)bake["output"]), "bake paths are relative to the USS folder");
            }

            var text = result.Report.ToConsole();
            Contains(text, "Import report", "console summary");
        }

        static void StrictProfileRefusesLoss()
        {
            var doc = SampleDoc();
            var strict = Convert(doc, Profiles.Strict);
            Check(strict.Report.Count(IssueSeverity.Error) > 0, "strict reports errors for lossy properties");

            var visual = Convert(doc, Profiles.Visual);
            Check(visual.Report.Bakes.Count >= 2, "visual profile still bakes");
        }

        static void OutputIsDeterministic()
        {
            var docA = SampleDoc();
            var first = Convert(docA, Profiles.Production, 3);
            var second = Convert(SampleDoc(), Profiles.Production, 3);

            Eq(Read(first.UxmlPath), Read(second.UxmlPath), "UXML is deterministic");
            Eq(Read(first.UssPath), Read(second.UssPath), "USS is deterministic");
            Eq(Read(first.TokensPath), Read(second.TokensPath), "tokens are deterministic");
        }

        static void GoldenSample(bool update)
        {
            foreach (var minor in new[] { 3, 6, 7 })
            {
                var version = "6." + minor;
                var result = Convert(SampleDoc(), Profiles.Production, minor);
                var dir = Path.Combine(Root, "tests", "golden", version);
                Directory.CreateDirectory(dir);

                var files = new (string source, string name)[]
                {
                    (result.UxmlPath, "screen-login.uxml"),
                    (result.UssPath, "screen-login.uss"),
                    (result.TokensPath, "tokens.uss"),
                    (result.ThemesPath, "themes.uss"),
                    (result.StylesPath, "styles.uss"),
                };

                foreach (var (source, name) in files)
                {
                    var golden = Path.Combine(dir, name);
                    if (update || !File.Exists(golden))
                    {
                        File.WriteAllText(golden, Read(source));
                        continue;
                    }

                    var expected = Read(golden);
                    var actual = Read(source);
                    if (expected != actual)
                    {
                        Checks++;
                        var diff = FirstDifference(expected, actual);
                        Failures.Add($"{version}/{name} differs from the golden at line {diff.line}: " +
                                     $"'{diff.expected}' != '{diff.actual}' (re-run with --update to accept)");
                    }
                }
            }
        }

        static string Block(string uss, string selector)
        {
            var start = uss.IndexOf(selector + " {", StringComparison.Ordinal);
            if (start < 0) return "";
            var end = uss.IndexOf("}", start, StringComparison.Ordinal);
            return end < 0 ? uss.Substring(start) : uss.Substring(start, end - start);
        }

        static (int line, string expected, string actual) FirstDifference(string expected, string actual)
        {
            var e = expected.Split('\n');
            var a = actual.Split('\n');
            for (var i = 0; i < Math.Max(e.Length, a.Length); i++)
            {
                var le = i < e.Length ? e[i] : "(end)";
                var la = i < a.Length ? a[i] : "(end)";
                if (le != la) return (i + 1, le, la);
            }
            return (0, "", "");
        }
    }
}
