using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FigCraft.Features.Annotate;
using FigCraft.Features.Content;
using FigCraft.Features.Diff;
using FigCraft.Features.Library;
using Newtonsoft.Json.Linq;

namespace FigCraft.Features.Tests
{
    internal static class Program
    {
        static int Main()
        {
            var tests = new List<(string name, Action run)>
            {
                ("document index: ids and paths", DocumentIndexWorks),
                ("annotate: plan maps, dedupes and replaces", AnnotatePlanWorks),
                ("annotate: labels carry the fix", AnnotateLabelCarriesFix),
                ("library: manifest layout and renders", ManifestLayout),
                ("library: manifest rejects bad input", ManifestRejectsBadInput),
                ("library: create and decoration passes", LibraryPasses),
                ("content: manifest resolves renders and targets", ContentManifestWorks),
                ("content: manifest rejects bad input", ContentManifestRejectsBadInput),
                ("content: plan, empty strings and overflow", ContentPlanWorks),
                ("content: images need a container", ContentNeedsContainer),
                ("diff: nodes, text and sizes", DiffEngineWorks),
                ("diff: renumbered ids match by path", DiffMatchesByPath),
                ("diff: markdown and json reports", DiffReports),
                ("diff: annotations are planned and replaced", DiffAnnotations),
                ("features: each one is its own project", FeaturesAreIsolated),
                ("features: discovered without a list", FeatureCatalogDiscovers),
                ("features: names and help are usable", FeatureContracts),
            };

            var failures = 0;
            foreach (var (name, run) in tests)
            {
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
                if (!ok) failures++;
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
                if (!ok)
                    foreach (var failure in Failures.Skip(before))
                        Console.WriteLine("        " + failure);
            }

            Console.WriteLine();
            Console.WriteLine($"{Checks} checks, {Failures.Count} failure(s)");
            return failures == 0 ? 0 : 1;
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
            if (!Equals(expected, actual)) Failures.Add($"{what} (expected '{expected}', got '{actual}')");
        }

        static void Throws<T>(Action action, string what) where T : Exception
        {
            Checks++;
            try
            {
                action();
                Failures.Add($"{what} (no exception)");
            }
            catch (T) { }
            catch (Exception ex)
            {
                Failures.Add($"{what} (threw {ex.GetType().Name})");
            }
        }

        static string Root
        {
            get
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null && !File.Exists(Path.Combine(dir.FullName, "samples", "shop.figcraft")))
                    dir = dir.Parent;
                if (dir == null) throw new InvalidOperationException("repository root not found");
                return dir.FullName;
            }
        }

        static string Sample(string relative) => Path.Combine(Root, relative);

        static string TempFile(string name, string content)
        {
            var dir = Path.Combine(Path.GetTempPath(), "figcraft-features-tests", Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, content);
            return path;
        }

        static PatchStep Step(AnnotatePlan plan, string command, int occurrence = 0) =>
            plan.Steps.Where(s => s.Command == command).ElementAt(occurrence);

        // ------------------------------------------------------------------- tests

        static void DocumentIndexWorks()
        {
            var index = DocumentIndex.Load(Sample(Path.Combine("samples", "shop.figcraft")));

            Eq(23, index.Find(23, null), "id is authoritative");
            Eq(6, index.Find(0, "Page 1/Screen/Login/Title"), "path is the fallback");
            Eq(null, index.Find(9999, "Page 1/Nope"), "unknown node");
            Eq("Page 1/Screen/Login/Card/Angular", index.PathOf(23), "path of a nested node");
            Eq("Card/Angular", index.Node(23)?.Name, "node name");
        }

        static JObject Report(params JObject[] issues)
        {
            return new JObject { ["issues"] = new JArray(issues) };
        }

        static JObject Issue(int nodeId, string path, string severity, string rule, string message,
            JObject fix = null, string unity = null)
        {
            var issue = new JObject
            {
                ["nodeId"] = nodeId,
                ["nodePath"] = path,
                ["severity"] = severity,
                ["rule"] = rule,
                ["message"] = message,
            };
            if (unity != null) issue["unity"] = unity;
            if (fix != null) issue["fix"] = fix;
            return issue;
        }

        static void AnnotatePlanWorks()
        {
            var index = DocumentIndex.Load(Sample(Path.Combine("samples", "shop.figcraft")));
            var report = Report(
                Issue(23, "Page 1/Screen/Login/Card/Angular", "warning", "gradient.angular.baked",
                    "Angular gradient baked.", unity: "baked/card-angular-fill.png"),
                Issue(0, "Page 1/Screen/Login/Title", "error", "text.outline.padding.exceeded",
                    "Outline too thick.",
                    new JObject { ["kind"] = "fontAssetPadding", ["required"] = 8 }, unity: "6px"),
                Issue(6, "Page 1/Screen/Login/Title", "info", "component.mapped", "fine"),
                Issue(9999, "Page 1/Ghost", "warning", "ghost.rule", "not there"));

            var existing = new JArray(
                new JObject { ["id"] = 23, ["index"] = 0, ["label"] = "[Unity] old" },
                new JObject { ["id"] = 23, ["index"] = 1, ["label"] = "[Unity] older" },
                new JObject { ["id"] = 6, ["index"] = 0, ["label"] = "Designer note" });

            var plan = AnnotateCommand.Plan(report, index, existing, new JArray(),
                "[Unity]", new HashSet<string>(new[] { "warning", "error" }, StringComparer.OrdinalIgnoreCase));

            Eq(3, plan.Matched, "issues matched by severity");
            Eq(1, plan.Filtered, "info issues filtered");
            Eq(1, plan.Unanchored, "issue without a layer");
            Eq(2, plan.Removed, "our annotations replaced");
            Eq(2, plan.Added, "annotations added");

            // Categories first, then removals (high index first), then adds.
            Eq("annotation.addCategory", plan.Steps[0].Command, "warning category first");
            Eq("annotation.addCategory", plan.Steps[1].Command, "error category second");
            Eq("annotation.remove", plan.Steps[2].Command, "removals after categories");
            Eq(1, (int)plan.Steps[2].Params["index"], "highest annotation index removed first");
            Eq(0, (int)plan.Steps[3].Params["index"], "then the lower one");

            var add = plan.Steps.First(s => s.Command == "annotation.add");
            Eq(23, (int)add.Params["id"], "add targets the id from the report");
            Check(((string)add.Params["label"]).StartsWith("[Unity] gradient.angular.baked:", StringComparison.Ordinal),
                "label carries prefix and rule");
            Eq("Unity import (warning)", (string)add.Params["category"], "warning category");

            var errorAdd = plan.Steps.Where(s => s.Command == "annotation.add").ElementAt(1);
            Eq(6, (int)errorAdd.Params["id"], "path fallback resolves the title");
            Eq("Unity import (error)", (string)errorAdd.Params["category"], "error category");

            // A foreign annotation is never removed.
            Check(plan.Steps.Where(s => s.Command == "annotation.remove").All(s => (int)s.Params["id"] == 23),
                "annotations not owned by the tool are left alone");
        }

        static void AnnotateLabelCarriesFix()
        {
            var index = DocumentIndex.Load(Sample(Path.Combine("samples", "shop.figcraft")));
            var report = Report(Issue(6, null, "warning", "text.outline.padding.exceeded", "Outline too thick.",
                new JObject { ["kind"] = "fontAssetPadding", ["required"] = 12 }, unity: "8px"));

            var plan = AnnotateCommand.Plan(report, index, new JArray(), new JArray(), "[Unity]",
                new HashSet<string>(new[] { "warning" }, StringComparer.OrdinalIgnoreCase));

            var label = (string)Step(plan, "annotation.add").Params["label"];
            Check(label.Contains("fontAssetPadding >= 12"), "fix requirement in the label", label);
            Check(label.Contains("-> 8px"), "target value in the label", label);
            Check(label.Contains("text.outline.padding.exceeded"), "rule in the label", label);
        }

        static void ManifestLayout()
        {
            var manifest = LibraryManifest.Load(Sample(Path.Combine("features", "samples", "library.json")));

            Eq(2, manifest.Components.Count, "components");
            Eq("Unity Components", manifest.Page, "page");
            Check(manifest.Components[0].Render != null && File.Exists(manifest.Components[0].Render), "render resolved");

            var positions = manifest.Layout();
            Eq((96d, 96d), (positions[0].x, positions[0].y), "first position");
            Eq((320d, 96d), (positions[1].x, positions[1].y), "second position uses width + gap");
        }

        static void ManifestRejectsBadInput()
        {
            var source = Sample(Path.Combine("features", "samples", "library.json"));
            var root = JObject.Parse(File.ReadAllText(source));
            var baseDir = Path.GetDirectoryName(source);
            var button = Path.Combine(baseDir, "button-primary.png");
            var toggle = Path.Combine(baseDir, "toggle-default.png");

            // Absolute render paths keep the unrelated checks focused on the rule under test.
            JObject Clone()
            {
                var clone = (JObject)root.DeepClone();
                clone["components"][0]["render"] = button;
                clone["components"][1]["render"] = toggle;
                return clone;
            }

            var missing = Clone();
            missing["components"][0]["render"] = Path.Combine(baseDir, "does-not-exist.png");
            Throws<FileNotFoundException>(() => LibraryManifest.Load(TempFile("missing.json", missing.ToString())),
                "missing render");

            var duplicate = Clone();
            duplicate["components"][1]["name"] = "Unity/Button/Primary";
            Throws<ArgumentException>(() => LibraryManifest.Load(TempFile("dup.json", duplicate.ToString())),
                "duplicate component names");

            var badLink = Clone();
            badLink["components"][0]["link"]["url"] = "file:///C:/secret";
            Throws<ArgumentException>(() => LibraryManifest.Load(TempFile("link.json", badLink.ToString())),
                "non-http dev links");

            var badType = Clone();
            badType["components"][0]["properties"][0]["type"] = "gradient";
            Throws<ArgumentException>(() => LibraryManifest.Load(TempFile("type.json", badType.ToString())),
                "unknown property type");

            var svg = Clone();
            var svgDir = Path.Combine(Path.GetTempPath(), "figcraft-features-tests", Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(svgDir);
            var svgPath = Path.Combine(svgDir, "icon.svg");
            File.WriteAllText(svgPath, "<svg/>");
            svg["components"][0]["render"] = svgPath;
            var svgManifest = Path.Combine(svgDir, "manifest.json");
            File.WriteAllText(svgManifest, svg.ToString());
            Throws<ArgumentException>(() => LibraryManifest.Load(svgManifest),
                "SVG renders are rejected with a hint");
        }

        static void LibraryPasses()
        {
            var manifest = LibraryManifest.Load(Sample(Path.Combine("features", "samples", "library.json")));
            var positions = manifest.Layout();

            var creates = LibraryCommand.CreateSteps(manifest, 26, positions);
            Eq(2, creates.Count, "one create per component");
            Eq("node.create", creates[0].Command, "create command");
            Eq("component", (string)creates[0].Params["type"], "component type");
            Eq(26, (int)creates[0].Params["parent"], "parent page");
            Eq("Unity/Button/Primary", (string)creates[0].Params["name"], "name");
            Eq(96d, (double)creates[0].Params["x"], "x");

            var decoration = LibraryCommand.DecorationSteps(manifest, new[] { 27, 28 }, createCategory: true);
            Eq("annotation.addCategory", decoration[0].Command, "category before annotations");

            var images = decoration.Where(s => s.Command == "image.place").ToList();
            Eq(2, images.Count, "image per render");
            Eq(27, (int)images[0].Params["parent"], "image inside the component");
            Eq("fill", (string)images[0].Params["mode"], "image mode");

            var properties = decoration.Where(s => s.Command == "component.addProperty").ToList();
            Eq(4, properties.Count, "properties from the manifest");
            Eq("text", (string)properties[0].Params["type"], "text property");
            Eq("Sign in", (string)properties[0].Params["default"], "text default");
            Check((bool)properties[1].Params["default"] == false, "boolean default");

            var annotations = decoration.Where(s => s.Command == "annotation.add").ToList();
            Eq(2, annotations.Count, "annotation per component");
            Eq("Unity components", (string)annotations[0].Params["category"], "annotation category");
            Check(((string)annotations[0].Params["label"]).Contains("ButtonPrimary.uxml"), "source path in the label");

            Eq(1, decoration.Count(s => s.Command == "devResource.add"), "dev link for the component that has one");

            Throws<ArgumentException>(() => LibraryCommand.DecorationSteps(manifest, new[] { 27 }, true),
                "one id per component is required");
        }

        // ------------------------------------------------------------- content

        static ContentManifest ContentSample() =>
            ContentManifest.Load(Sample(Path.Combine("features", "samples", "content.json")));

        static void ContentManifestWorks()
        {
            var manifest = ContentSample();

            Eq("es-ES", manifest.Locale, "locale");
            Eq(3, manifest.Texts.Count, "texts");
            Eq(1, manifest.Images.Count, "images");
            Eq("Page 1/Screen/Login/Title", manifest.Texts[0].Node, "node path");
            Eq(9, manifest.Texts[2].Id, "id target");
            Eq("", manifest.Texts[1].Value, "empty string is a valid value");
            Check(Path.IsPathRooted(manifest.Images[0].Path) && File.Exists(manifest.Images[0].Path),
                "image path resolved next to the manifest");
            Eq(103d, manifest.Images[0].Width, "image width");
        }

        static void ContentManifestRejectsBadInput()
        {
            var source = Sample(Path.Combine("features", "samples", "content.json"));
            var root = JObject.Parse(File.ReadAllText(source));
            var avatar = Path.Combine(Path.GetDirectoryName(source), "avatar.png");

            JObject Clone()
            {
                var clone = (JObject)root.DeepClone();
                clone["images"][0]["path"] = avatar;
                return clone;
            }

            var duplicate = Clone();
            duplicate["texts"][0]["node"] = "Page 1/Screen/Login/Title";
            duplicate["texts"][1]["node"] = "Page 1/Screen/Login/Title";
            Throws<ArgumentException>(() => ContentManifest.Load(TempFile("dup.json", duplicate.ToString())),
                "same layer twice");

            var noTarget = Clone();
            ((JObject)noTarget["texts"][0]).Remove("node");
            Throws<ArgumentException>(() => ContentManifest.Load(TempFile("notarget.json", noTarget.ToString())),
                "text without a target");

            var noValue = Clone();
            ((JObject)noValue["texts"][0]).Remove("value");
            Throws<ArgumentException>(() => ContentManifest.Load(TempFile("novalue.json", noValue.ToString())),
                "text without a value");

            var badMode = Clone();
            badMode["images"][0]["mode"] = "stretch";
            Throws<ArgumentException>(() => ContentManifest.Load(TempFile("mode.json", badMode.ToString())),
                "unknown image mode");

            var missing = Clone();
            missing["images"][0]["path"] = Path.Combine(Path.GetDirectoryName(avatar), "nope.png");
            Throws<FileNotFoundException>(() => ContentManifest.Load(TempFile("missing.json", missing.ToString())),
                "missing image");

            var zero = Clone();
            zero["images"][0]["width"] = 0;
            Throws<ArgumentException>(() => ContentManifest.Load(TempFile("zero.json", zero.ToString())),
                "image without a size");

            var empty = new JObject { ["locale"] = "es-ES" };
            Throws<ArgumentException>(() => ContentManifest.Load(TempFile("empty.json", empty.ToString())),
                "manifest without entries");
        }

        static void ContentPlanWorks()
        {
            var index = DocumentIndex.Load(Sample(Path.Combine("samples", "shop.figcraft")));
            var plan = ContentCommand.Plan(ContentSample(), index);

            Eq(4, plan.Steps.Count, "one step per entry");
            Eq("node.set", plan.Steps[0].Command, "texts first");
            Eq(6, (int)plan.Steps[0].Params["id"], "path resolves to the title");
            Eq("Bienvenido de nuevo a tu tienda", (string)plan.Steps[0].Params["characters"], "characters");
            Eq("", (string)plan.Steps[1].Params["characters"], "empty string is applied");
            Eq(1, plan.Empty, "empty text counted");
            Eq(9, (int)plan.Steps[2].Params["id"], "id target");

            var image = plan.Steps[3];
            Eq("image.place", image.Command, "image step");
            Eq(18, (int)image.Params["parent"], "image inside the container");
            Eq("fill", (string)image.Params["mode"], "mode");
            Eq(4, plan.Texts + plan.Images, "applied entries");

            Eq(1, plan.Overflow.Count, "long translation flagged");
            Check(plan.Overflow[0].Contains("375px"), "overflow names the container width", plan.Overflow[0]);
            Eq(0, plan.Missing.Count, "no missing targets");

            var avatar = Path.Combine(Root, "features", "samples", "avatar.png");

            var missing = JObject.Parse(File.ReadAllText(Sample(Path.Combine("features", "samples", "content.json"))));
            missing["texts"][0]["node"] = "Page 1/Screen/Login/Ghost";
            missing["images"][0]["path"] = avatar;
            var missingManifest = ContentManifest.Load(TempFile("ghost.json", missing.ToString()));
            Eq(1, ContentCommand.Plan(missingManifest, index).Missing.Count, "unknown layer reported");

            var sameTarget = JObject.Parse(File.ReadAllText(Sample(Path.Combine("features", "samples", "content.json"))));
            sameTarget["images"][0]["path"] = avatar;
            sameTarget["texts"][2]["id"] = 6;
            var sameManifest = ContentManifest.Load(TempFile("same.json", sameTarget.ToString()));
            var samePlan = ContentCommand.Plan(sameManifest, index);
            Eq(1, samePlan.Missing.Count, "two entries on the same layer are refused");
        }

        static void ContentNeedsContainer()
        {
            var index = DocumentIndex.Load(Sample(Path.Combine("samples", "shop.figcraft")));
            var manifest = JObject.Parse(File.ReadAllText(Sample(Path.Combine("features", "samples", "content.json"))));
            manifest["texts"] = new JArray();
            manifest["images"][0]["node"] = "Page 1/Screen/Login/Title";
            manifest["images"][0]["path"] = Path.Combine(Root, "features", "samples", "avatar.png");

            var plan = ContentCommand.Plan(ContentManifest.Load(TempFile("leaf.json", manifest.ToString())), index);
            Eq(0, plan.Steps.Count, "nothing is written for a leaf target");
            Eq(1, plan.WrongType.Count, "leaf target reported");
            Check(plan.WrongType[0].Contains("frame, component, instance, section or page"),
                "message says what can hold an image", plan.WrongType[0]);
        }

        // ---------------------------------------------------------------- diff

        static JObject Solid(double r, double g, double b) => new JObject
        {
            ["type"] = "solid",
            ["color"] = new JObject { ["r"] = r, ["g"] = g, ["b"] = b, ["a"] = 1.0 },
            ["visible"] = true,
        };

        static JObject Node(int id, string name, string type, int? parent, string characters = null,
            double width = 100, double height = 20, JObject fill = null, bool? visible = null)
        {
            var node = new JObject
            {
                ["id"] = id,
                ["name"] = name,
                ["type"] = type,
                ["width"] = width,
                ["height"] = height,
            };
            if (parent.HasValue) node["parent"] = parent.Value;
            if (characters != null) node["characters"] = characters;
            if (fill != null) node["fills"] = new JArray(fill);
            if (visible.HasValue) node["visible"] = visible.Value;
            return node;
        }

        static JObject Doc(params JObject[] nodes)
        {
            var map = new JObject();
            foreach (var node in nodes) map[(string)node["id"]] = node;
            return new JObject { ["nodes"] = map };
        }

        static string TempDocument(string name, JObject document)
        {
            var directory = Path.Combine(Path.GetTempPath(), "figcraft-features-tests",
                name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "document.json"), document.ToString());
            return directory;
        }

        static DiffResult SampleDiff()
        {
            var before = TempDocument("before", Doc(
                Node(1, "Page 1", "page", null),
                Node(2, "Title", "text", 1, characters: "Hello", fill: Solid(0, 0, 0)),
                Node(3, "Card", "frame", 1),
                Node(4, "Old", "rectangle", 3, fill: Solid(1, 0, 0))));

            var after = TempDocument("after", Doc(
                Node(1, "Page 1", "page", null),
                Node(2, "Title", "text", 1, characters: "Hello world", width: 150, fill: Solid(0, 0, 1)),
                Node(3, "Card", "frame", 1),
                Node(5, "New", "rectangle", 3, fill: Solid(0, 0, 1))));

            return DiffEngine.Compare(DocumentIndex.Load(before), DocumentIndex.Load(after));
        }

        static void DiffEngineWorks()
        {
            var result = SampleDiff();

            Eq(1, result.Added.Count, "added");
            Eq("Page 1/Card/New", result.Added[0].Path, "added path");
            Eq(1, result.Removed.Count, "removed");
            Eq("Page 1/Card/Old", result.Removed[0].Path, "removed path");
            Eq(1, result.Changed.Count, "changed");

            var change = result.Changed[0];
            Eq("Page 1/Title", change.Path, "changed path");
            Eq("text,size,fill", string.Join(",", change.Fields.Select(f => f.Field)), "field order");
            var text = change.Fields[0];
            Eq("Hello", text.Before, "old text");
            Eq("Hello world", text.After, "new text");
            Check(text.ToString().Contains("text: \"Hello\" -> \"Hello world\""), "text is quoted for people", text.ToString());
            Eq("100x20", change.Fields[1].Before, "old size");
            Eq("150x20", change.Fields[1].After, "new size");
            Eq("#000000", change.Fields[2].Before, "old fill");
            Eq("#0000FF", change.Fields[2].After, "new fill");
            Check(change.Label("[d]").StartsWith("[d] ", StringComparison.Ordinal), "label carries the prefix");
        }

        static void DiffMatchesByPath()
        {
            var before = TempDocument("renum-before", Doc(
                Node(1, "Page 1", "page", null),
                Node(2, "Title", "text", 1, characters: "Hi")));
            var after = TempDocument("renum-after", Doc(
                Node(1, "Page 1", "page", null),
                Node(10, "Title", "text", 1, characters: "Hi")));

            var result = DiffEngine.Compare(DocumentIndex.Load(before), DocumentIndex.Load(after));

            Eq(0, result.Added.Count, "nothing added");
            Eq(0, result.Removed.Count, "nothing removed");
            Eq(1, result.Changed.Count, "the layer was renumbered");
            Eq("id", result.Changed[0].Fields[0].Field, "id change first");
            Eq("2", result.Changed[0].Fields[0].Before, "old id");
            Eq("10", result.Changed[0].Fields[0].After, "new id");
        }

        static void DiffReports()
        {
            var result = SampleDiff();
            var markdown = DiffCommand.ToMarkdown(result, "old.figcraft", "new.figcraft");

            Check(markdown.Contains("# FigCraft changes"), "title");
            Check(markdown.Contains("## Added (1)"), "added section");
            Check(markdown.Contains("- `Page 1/Card/New`"), "added path in the report");
            Check(markdown.Contains("## Changed (1)"), "changed section");
            Check(markdown.Contains("text: \"Hello\" -> \"Hello world\""), "change in the report");

            var json = DiffCommand.ToJson(result, "old.figcraft", "new.figcraft");
            Eq(1, ((JArray)json["added"]).Count, "json added");
            Eq(1, ((JArray)json["removed"]).Count, "json removed");
            var changed = (JObject)((JArray)json["changed"])[0];
            Eq("Hello", (string)changed["fields"]["text"]["before"], "json text before");
            Eq("Hello world", (string)changed["fields"]["text"]["after"], "json text after");
        }

        static void DiffAnnotations()
        {
            var result = SampleDiff();
            var existing = new JArray(
                new JObject { ["id"] = 2, ["index"] = 0, ["label"] = "[Unity diff] old" },
                new JObject { ["id"] = 2, ["index"] = 1, ["label"] = "Designer note" });

            var plan = DiffCommand.PlanAnnotations(result, existing, new JArray(), "[Unity diff]");

            var category = plan.Steps.First(s => s.Command == "annotation.addCategory");
            Eq("Unity diff", (string)category.Params["name"], "category name");
            var firstAdd = plan.Steps.FindIndex(s => s.Command == "annotation.add");
            Check(plan.Steps.IndexOf(category) < firstAdd, "category before its annotations");

            var removal = plan.Steps.First(s => s.Command == "annotation.remove");
            Eq(0, (int)removal.Params["index"], "only the owned annotation");
            Eq(1, plan.Removed, "removed count");
            Eq(2, plan.Added, "added and changed annotated");
            Eq(1, plan.Skipped, "removed layers cannot be annotated");

            var adds = plan.Steps.Where(s => s.Command == "annotation.add").ToList();
            Eq(2, adds.Count, "one annotation per target");
            Eq(5, (int)adds[0].Params["id"], "the new layer");
            Check(((string)adds[0].Params["label"]).Contains("new layer"), "added label");
            Eq(2, (int)adds[1].Params["id"], "the changed layer");

            var known = JArray.Parse("[{\"name\":\"Unity diff\"}]");
            var withoutCategory = DiffCommand.PlanAnnotations(result, new JArray(), known, "[Unity diff]");
            Eq(0, withoutCategory.Steps.Count(s => s.Command == "annotation.addCategory"), "category reused");

            var unchanged = DiffEngine.Compare(
                DocumentIndex.Load(TempDocument("same-a", Doc(Node(1, "Page 1", "page", null)))),
                DocumentIndex.Load(TempDocument("same-b", Doc(Node(1, "Page 1", "page", null)))));
            Eq(0, DiffCommand.PlanAnnotations(unchanged, new JArray(), new JArray(), "[Unity diff]").Steps.Count,
                "no plan without changes");

            var leftovers = DiffCommand.PlanAnnotations(unchanged, existing, new JArray(), "[Unity diff]");
            Eq(1, leftovers.Steps.Count, "stale notes are removed even without changes");
            Eq("annotation.remove", leftovers.Steps[0].Command, "only the removal");
            Eq(0, leftovers.Added, "nothing annotated");
        }

        // ------------------------------------------------------------- features

        static void FeaturesAreIsolated()
        {
            var featuresDir = Path.Combine(Root, "features");

            // The base never references features: it must build and ship without them.
            var baseFiles = Directory.GetFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(Path.Combine(Root, "tests"), "*.csproj", SearchOption.AllDirectories))
                .Concat(Directory.GetFiles(Root, "*.sln", SearchOption.TopDirectoryOnly));

            foreach (var file in baseFiles)
            {
                var text = File.ReadAllText(file);
                Check(!text.Contains("FigCraft.Features") && !text.Contains("features\\") && !text.Contains("features/"),
                    "base project does not reference features", Path.GetFileName(file));
            }

            // Features depend on Shared only, never on the converter or on each other.
            var featureProjects = Directory.GetFiles(featuresDir, "*.csproj", SearchOption.AllDirectories)
                .Where(p => !p.Contains(Path.Combine("features", "Host")) &&
                            !p.Contains(Path.Combine("features", "Shared")) &&
                            !p.Contains(Path.Combine("features", "tests")))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            Check(featureProjects.Count >= 3, "one project per implemented feature",
                $"{featureProjects.Count} found");

            var discovered = FeatureCatalog.Discover(AppContext.BaseDirectory);
            Eq(featureProjects.Count, discovered.Count, "every feature project is discovered");

            foreach (var project in featureProjects)
            {
                var name = Path.GetFileName(project);
                var text = File.ReadAllText(project);
                Check(!text.Contains("FigCraft.Connector"), "feature does not reference the converter", name);
                Check(text.Contains("FigCraft.Features.csproj"), "feature uses Shared", name);
                Check(Regex.IsMatch(Path.GetFileNameWithoutExtension(project), @"^FigCraft\.Features\.[A-Z][A-Za-z]+$"),
                    "feature follows the assembly naming", name);
            }

            // The host discovers projects with a glob, so adding a folder needs no edit here.
            var host = File.ReadAllText(Path.Combine(featuresDir, "Host", "FigCraft.Features.Host.csproj"));
            Check(host.Contains(@"..\*\*.csproj"), "host references feature projects with a glob");
            Check(!host.Contains("Annotate\\") && !host.Contains("Library\\"),
                "host has no hardcoded feature list");
        }

        static void FeatureCatalogDiscovers()
        {
            var warnings = new List<string>();
            var features = FeatureCatalog.Discover(AppContext.BaseDirectory, warnings.Add);

            Eq(0, warnings.Count, "no discovery warnings");
            Check(features.Count >= 3, "discovered every feature assembly", $"{features.Count} found");
            Eq(string.Join(",", features.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal)),
                string.Join(",", features.Select(f => f.Name)), "commands sorted by name");
            foreach (var expected in new[] { "annotate", "content", "library" })
                Check(features.Any(f => f.Name == expected), "command present", expected);

            var empty = Path.Combine(Path.GetTempPath(), "figcraft-features-empty", Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(empty);
            Eq(0, FeatureCatalog.Discover(empty).Count, "a directory without features is not an error");
            Throws<DirectoryNotFoundException>(() => FeatureCatalog.Discover(Path.Combine(empty, "nope")),
                "missing directory is reported");
        }

        static void FeatureContracts()
        {
            var features = FeatureCatalog.Discover(AppContext.BaseDirectory, m => Failures.Add("warning: " + m));

            foreach (var feature in features)
            {
                Check(Regex.IsMatch(feature.Name, @"^[a-z][a-z0-9-]*$"), "lowercase command name", feature.Name);
                Check(!string.IsNullOrWhiteSpace(feature.Summary), "summary", feature.Name);
                Check(feature.Usage.StartsWith(feature.Name + " ", StringComparison.Ordinal),
                    "usage starts with the command", feature.Usage);
                Check(feature.Help.Contains("--cli"), "help documents how to reach FigCraft", feature.Name);
            }

            var hostAssembly = typeof(IFeature).Assembly;
            var declared = hostAssembly.GetTypes().Where(t => typeof(IFeature).IsAssignableFrom(t) && t.IsInterface).ToList();
            Eq(1, declared.Count, "the contract lives in Shared");
        }
    }
}
