using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace FigCraft.Features.Library
{
    public sealed class LibraryOptions
    {
        public string ManifestPath;
        public string FigCraftPath;
        public string OutPath;
        public string CliPath = "figcraft-cli";
        public bool DryRun;
        public bool Replace;

        public static LibraryOptions Parse(Cli cli)
        {
            var options = new LibraryOptions
            {
                ManifestPath = cli.Require("manifest"),
                FigCraftPath = cli.Require("figcraft"),
                OutPath = cli.Get("out"),
                CliPath = cli.Get("cli", "figcraft-cli"),
                DryRun = cli.Has("dry-run"),
                Replace = cli.Has("replace"),
            };

            if (!options.DryRun && string.IsNullOrEmpty(options.OutPath))
                throw new ArgumentException("--out is required when the library is written (or pass --dry-run)");

            return options;
        }
    }

    /// <summary>
    /// Builds a FigCraft component library from a manifest of renders produced by the Unity side:
    /// one component per control, its render as the visual, its UXML/control properties as FigCraft
    /// component properties, and an annotation with the source path.
    ///
    /// The FigCraft side is split into passes because ids are only known after a step runs:
    /// create the component nodes, then decorate them with images/properties/annotations.
    /// <c>node.create type=component</c> already registers a main component; calling
    /// <c>component.create</c> on it again would create a second one, so it is not called.
    /// </summary>
    public static class LibraryCommand
    {
        public const string Category = "Unity components";

        public static int Run(LibraryOptions options)
        {
            var manifest = LibraryManifest.Load(options.ManifestPath);
            var client = new FigCraftClient { CliPath = options.CliPath };

            var discovery = client.Run(options.FigCraftPath, null, new List<PatchStep>
            {
                new PatchStep("page.list"),
                new PatchStep("component.list"),
                new PatchStep("annotation.categories"),
            });

            var pages = discovery.FirstOrDefault(r => r.Command == "page.list")?.Result as JArray ?? new JArray();
            var components = discovery.FirstOrDefault(r => r.Command == "component.list")?.Result as JArray ?? new JArray();
            var categories = discovery.FirstOrDefault(r => r.Command == "annotation.categories")?.Result?["categories"] as JArray
                             ?? new JArray();

            var page = pages.OfType<JObject>().FirstOrDefault(p =>
                string.Equals(p.Value<string>("name"), manifest.Page, StringComparison.Ordinal));
            int? pageId = page?.Value<int?>("id");
            var createsPage = pageId == null;

            var existing = components.OfType<JObject>()
                .Select(c => new { Name = c.Value<string>("name"), Id = c.Value<int?>("id") ?? 0 })
                .Where(c => c.Name != null && c.Id > 0)
                .ToList();

            var collisions = manifest.Components
                .Where(c => existing.Any(e => string.Equals(e.Name, c.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(c => c.Name)
                .ToList();

            if (collisions.Count > 0 && !options.Replace)
                throw new FigCraftException(
                    $"these components already exist: {string.Join(", ", collisions)}. " +
                    "Pass --replace to rebuild them (instances of the old components are not migrated).");

            var deleteIds = options.Replace
                ? existing.Where(e => manifest.Components.Any(c =>
                        string.Equals(c.Name, e.Name, StringComparison.OrdinalIgnoreCase)))
                    .Select(e => e.Id)
                    .ToList()
                : new List<int>();

            var positions = manifest.Layout();
            var needCategory = !categories.OfType<JObject>().Any(c =>
                string.Equals(c.Value<string>("name"), Category, StringComparison.OrdinalIgnoreCase));

            if (options.DryRun)
            {
                PrintPlan(manifest, positions, pageId, createsPage, collisions, needCategory);
                return 0;
            }

            if (Directory.Exists(options.FigCraftPath))
                throw new FigCraftException("library needs a .figcraft file, not a folder");

            // Every figcraft-cli run writes a complete document, so the passes are chained through
            // two temporary files and only the last pass writes the path the user asked for.
            var temp = Path.Combine(Path.GetTempPath(), "figcraft-features-" + Guid.NewGuid().ToString("N"));
            var workA = temp + "-a.figcraft";
            var workB = temp + "-b.figcraft";
            File.Copy(Path.GetFullPath(options.FigCraftPath), workA, true);

            try
            {
                var input = workA;
                var output = workB;
                List<StepResult> Pass(List<PatchStep> steps, bool final = false)
                {
                    var target = final ? options.OutPath : output;
                    var results = client.Run(input, target, steps);
                    if (!final)
                    {
                        var swap = input;
                        input = output;
                        output = swap;
                    }
                    return results;
                }

                if (createsPage)
                {
                    Pass(new List<PatchStep>
                    {
                        new PatchStep("page.add", new JObject { ["name"] = manifest.Page }),
                    });

                    var after = client.Run(input, null, new List<PatchStep> { new PatchStep("page.list") });
                    pages = after.FirstOrDefault()?.Result as JArray ?? new JArray();
                    page = pages.OfType<JObject>().FirstOrDefault(p =>
                        string.Equals(p.Value<string>("name"), manifest.Page, StringComparison.Ordinal));
                    pageId = page?.Value<int?>("id")
                             ?? throw new FigCraftException($"the page '{manifest.Page}' could not be created");
                }

                var createPass = new List<PatchStep>();
                if (deleteIds.Count > 0)
                    createPass.Add(new PatchStep("edit.delete", new JObject { ["ids"] = new JArray(deleteIds) }));
                createPass.AddRange(CreateSteps(manifest, pageId, positions));

                var created = Pass(createPass);
                var componentIds = created
                    .Where(r => r.Command == "node.create")
                    .Select(r => r.Result?.Value<int?>("id") ?? 0)
                    .ToList();

                if (componentIds.Count != manifest.Components.Count || componentIds.Any(id => id <= 0))
                    throw new FigCraftException(
                        $"expected {manifest.Components.Count} components, got {componentIds.Count}. " +
                        "The output was not written.");

                var decoration = DecorationSteps(manifest, componentIds, needCategory);
                Pass(decoration, final: true);

                Console.WriteLine($"page      {manifest.Page}" + (createsPage ? " (created)" : $" (id {pageId})"));
                Console.WriteLine($"created   {manifest.Components.Count} component(s)");
                Console.WriteLine($"images    {manifest.Components.Count(c => c.Render != null)}");
                Console.WriteLine($"props     {manifest.Components.Sum(c => c.Properties.Count)}");
                Console.WriteLine($"wrote     {options.OutPath}");
                return 0;
            }
            finally
            {
                try { File.Delete(workA); } catch { /* best effort */ }
                try { File.Delete(workB); } catch { /* best effort */ }
            }
        }

        public static List<PatchStep> CreateSteps(LibraryManifest manifest, int? pageId, (double x, double y)[] positions)
        {
            var steps = new List<PatchStep>();
            for (var i = 0; i < manifest.Components.Count; i++)
            {
                var component = manifest.Components[i];
                var parameters = new JObject
                {
                    ["type"] = "component",
                    ["name"] = component.Name,
                    ["x"] = positions[i].x,
                    ["y"] = positions[i].y,
                    ["width"] = component.Width,
                    ["height"] = component.Height,
                };
                if (pageId.HasValue) parameters["parent"] = pageId.Value;
                steps.Add(new PatchStep("node.create", parameters));
            }
            return steps;
        }

        public static List<PatchStep> DecorationSteps(LibraryManifest manifest, IReadOnlyList<int> componentIds,
            bool createCategory)
        {
            if (componentIds.Count != manifest.Components.Count)
                throw new ArgumentException("one component id is needed per manifest entry");

            var steps = new List<PatchStep>();
            if (createCategory)
                steps.Add(new PatchStep("annotation.addCategory", new JObject
                {
                    ["name"] = Category,
                    ["color"] = "#2F6FEB",
                }));

            for (var i = 0; i < manifest.Components.Count; i++)
            {
                var component = manifest.Components[i];
                var id = componentIds[i];

                if (component.Render != null)
                {
                    steps.Add(new PatchStep("image.place", new JObject
                    {
                        ["path"] = component.Render,
                        ["parent"] = id,
                        ["x"] = 0,
                        ["y"] = 0,
                        ["width"] = component.Width,
                        ["height"] = component.Height,
                        ["mode"] = "fill",
                    }));
                }

                foreach (var property in component.Properties)
                {
                    var propertyStep = PropertyStep(id, property);
                    if (propertyStep != null) steps.Add(propertyStep);
                }

                steps.Add(new PatchStep("annotation.add", new JObject
                {
                    ["id"] = id,
                    ["label"] = component.Annotation ?? $"Rendered from Unity: {component.Name}",
                    ["category"] = Category,
                    ["properties"] = new JArray("width", "height"),
                }));

                if (component.Link != null)
                    steps.Add(new PatchStep("devResource.add", new JObject
                    {
                        ["id"] = id,
                        ["url"] = component.Link.Url,
                        ["name"] = component.Link.Name,
                    }));
            }

            return steps;
        }

        static PatchStep PropertyStep(int componentId, PropertySpec property)
        {
            JToken value;
            switch (property.Type)
            {
                case "boolean":
                    value = property.Default ?? false;
                    break;
                case "variant":
                    value = property.Default ?? "Default";
                    break;
                case "instanceSwap":
                case "slot":
                    // These need a component id as their default; only emit them when the manifest has one.
                    if (property.Default == null || property.Default.Type != JTokenType.Integer) return null;
                    value = property.Default;
                    break;
                default:
                    value = property.Default ?? "";
                    break;
            }

            return new PatchStep("component.addProperty", new JObject
            {
                ["id"] = componentId,
                ["name"] = property.Name,
                ["type"] = property.Type,
                ["default"] = value,
            });
        }

        static void PrintPlan(LibraryManifest manifest, (double x, double y)[] positions, int? pageId,
            bool createsPage, IReadOnlyList<string> collisions, bool createCategory)
        {
            Console.WriteLine($"page      {manifest.Page}" + (createsPage ? " (would be created)" : $" (id {pageId})"));
            if (collisions.Count > 0)
                Console.WriteLine($"replace   {string.Join(", ", collisions)}");
            Console.WriteLine($"category  {Category}" + (createCategory ? " (would be created)" : " (exists)"));
            Console.WriteLine();
            for (var i = 0; i < manifest.Components.Count; i++)
            {
                var component = manifest.Components[i];
                var render = component.Render == null ? "no render" : System.IO.Path.GetFileName(component.Render);
                Console.WriteLine($"  {component.Name,-32} {component.Width}x{component.Height} " +
                                  $"at {positions[i].x},{positions[i].y}  {render}");
                foreach (var property in component.Properties)
                    Console.WriteLine($"      property {property.Type,-10} {property.Name}");
                if (component.Link != null) Console.WriteLine($"      link     {component.Link.Name} -> {component.Link.Url}");
            }
            Console.WriteLine();
            Console.WriteLine("dry run  no changes were written");
        }
    }
}
