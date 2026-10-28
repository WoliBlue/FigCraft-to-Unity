using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace FigCraft.Features.Library
{
    public sealed class PropertySpec
    {
        public string Name;
        public string Type = "text";
        public JToken Default;
    }

    public sealed class LinkSpec
    {
        public string Name;
        public string Url;
    }

    public sealed class ComponentSpec
    {
        public string Name;
        public double Width = 160;
        public double Height = 48;
        /// <summary>Absolute path to the rendered PNG produced by the Unity side.</summary>
        public string Render;
        public string Annotation;
        public readonly List<PropertySpec> Properties = new List<PropertySpec>();
        public LinkSpec Link;
    }

    /// <summary>
    /// A manifest is the contract between Unity (which renders controls) and this tool (which builds the
    /// FigCraft library). Keeping it as a file means the Unity side stays optional: any renderer can
    /// produce one, and the FigCraft side can be tested without Unity.
    /// </summary>
    public sealed class LibraryManifest
    {
        public string SourcePath;
        public string BaseDirectory;
        public string Page = "Unity Components";
        public int Columns = 4;
        public double Gap = 48;
        public double Margin = 80;
        public readonly List<ComponentSpec> Components = new List<ComponentSpec>();

        public static LibraryManifest Load(string path)
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full)) throw new FileNotFoundException("library manifest not found", full);

            var root = JObject.Parse(File.ReadAllText(full));
            var manifest = new LibraryManifest
            {
                SourcePath = full,
                BaseDirectory = Path.GetDirectoryName(full),
                Page = Str(root["page"]) ?? "Unity Components",
                Columns = Math.Max(1, Int(root["columns"], 4)),
                Gap = Dbl(root["gap"], 48),
                Margin = Dbl(root["margin"], 80),
            };

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in (root["components"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var component = new ComponentSpec
                {
                    Name = Str(token["name"]) ?? throw new ArgumentException("every component needs a name"),
                    Width = Dbl(token["width"], 160),
                    Height = Dbl(token["height"], 48),
                    Annotation = Str(token["annotation"]),
                };

                if (!names.Add(component.Name))
                    throw new ArgumentException($"duplicate component '{component.Name}' in the manifest");
                if (component.Width <= 0 || component.Height <= 0)
                    throw new ArgumentException($"component '{component.Name}' needs a positive width and height");

                var render = Str(token["render"]);
                if (!string.IsNullOrEmpty(render))
                {
                    component.Render = Path.GetFullPath(Path.Combine(manifest.BaseDirectory, render));
                    if (!File.Exists(component.Render))
                        throw new FileNotFoundException($"render for '{component.Name}' not found", component.Render);
                    var extension = Path.GetExtension(component.Render).ToLowerInvariant();
                    if (extension == ".svg")
                        throw new ArgumentException(
                            $"'{component.Name}': image.place does not take SVG; export a PNG for renders (SVG icons belong in a separate import)");
                    if (extension != ".png" && extension != ".jpg" && extension != ".jpeg" && extension != ".webp")
                        throw new ArgumentException($"'{component.Name}': unsupported render format '{extension}'");
                }

                foreach (var propertyToken in (token["properties"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    var property = new PropertySpec
                    {
                        Name = Str(propertyToken["name"]) ?? throw new ArgumentException($"'{component.Name}': property without a name"),
                        Type = Str(propertyToken["type"]) ?? "text",
                        Default = propertyToken["default"],
                    };
                    if (property.Type != "text" && property.Type != "boolean" && property.Type != "variant" &&
                        property.Type != "slot" && property.Type != "instanceSwap")
                        throw new ArgumentException($"'{component.Name}': unknown property type '{property.Type}'");
                    component.Properties.Add(property);
                }

                var link = token["link"] as JObject;
                if (link != null)
                {
                    var url = Str(link["url"]);
                    if (string.IsNullOrEmpty(url) || (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                                                      !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                        throw new ArgumentException($"'{component.Name}': dev links must be http or https");
                    component.Link = new LinkSpec { Name = Str(link["name"]) ?? "Open", Url = url };
                }

                manifest.Components.Add(component);
            }

            if (manifest.Components.Count == 0)
                throw new ArgumentException("the manifest has no components");

            return manifest;
        }

        /// <summary>Grid placement, row by row, using each row's tallest component.</summary>
        public (double x, double y)[] Layout()
        {
            var positions = new List<(double, double)>();
            double x = Margin, y = Margin, rowHeight = 0;
            var column = 0;

            foreach (var component in Components)
            {
                if (column == Columns)
                {
                    column = 0;
                    x = Margin;
                    y += rowHeight + Gap;
                    rowHeight = 0;
                }

                positions.Add((x, y));
                x += component.Width + Gap;
                rowHeight = Math.Max(rowHeight, component.Height);
                column++;
            }

            return positions.ToArray();
        }

        static string Str(JToken token) =>
            token == null || token.Type == JTokenType.Null ? null : token.Type == JTokenType.String ? token.Value<string>() : token.ToString();

        static double Dbl(JToken token, double fallback) =>
            token == null || token.Type == JTokenType.Null ? fallback : token.Value<double>();

        static int Int(JToken token, int fallback) =>
            token == null || token.Type == JTokenType.Null ? fallback : token.Value<int>();
    }
}
