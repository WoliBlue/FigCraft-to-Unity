using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FigCraft.Features.Content
{
    public sealed class TextEntry
    {
        public string Node;
        public int? Id;
        public string Value;

        public string Target => Id.HasValue ? "#" + Id.Value : Node;
    }

    public sealed class ImageEntry
    {
        public string Node;
        public int? Id;
        public string Path;
        public double X;
        public double Y;
        public double Width;
        public double Height;
        public string Mode = "fill";

        public string Target => Id.HasValue ? "#" + Id.Value : Node;
    }

    /// <summary>
    /// The contract between a Unity project (localization tables, ScriptableObjects, addressables) and
    /// this tool: which layer gets which real string or image. Producing the manifest is the only part
    /// that needs Unity; the manifest itself is plain JSON.
    /// </summary>
    public sealed class ContentManifest
    {
        static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" };
        static readonly string[] Modes = { "fill", "fit", "crop", "tile" };

        public string Locale;
        public string SourcePath;
        public readonly List<TextEntry> Texts = new List<TextEntry>();
        public readonly List<ImageEntry> Images = new List<ImageEntry>();

        public static ContentManifest Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("manifest path is empty", nameof(path));
            if (!File.Exists(path)) throw new FileNotFoundException("manifest not found", path);

            JObject root;
            try
            {
                root = JObject.Parse(File.ReadAllText(path));
            }
            catch (JsonException ex)
            {
                throw new ArgumentException($"{Path.GetFileName(path)} is not valid JSON: {ex.Message}");
            }

            var manifest = new ContentManifest
            {
                Locale = root.Value<string>("locale"),
                SourcePath = Path.GetFullPath(path),
            };

            var directory = Path.GetDirectoryName(manifest.SourcePath);
            var targets = new HashSet<string>(StringComparer.Ordinal);

            var texts = root["texts"] as JArray ?? new JArray();
            for (var i = 0; i < texts.Count; i++)
            {
                if (!(texts[i] is JObject entry)) throw new ArgumentException($"texts[{i}] is not an object");
                var text = new TextEntry
                {
                    Node = entry.Value<string>("node"),
                    Id = entry.Value<int?>("id"),
                    Value = entry.Value<string>("value"),
                };

                if (text.Id == null && string.IsNullOrWhiteSpace(text.Node))
                    throw new ArgumentException($"texts[{i}] needs a node path or an id");
                if (text.Value == null)
                    throw new ArgumentException($"texts[{i}] needs a value (an empty string is allowed)");
                if (!targets.Add(text.Target))
                    throw new ArgumentException($"'{text.Target}' is targeted twice");

                manifest.Texts.Add(text);
            }

            var images = root["images"] as JArray ?? new JArray();
            for (var i = 0; i < images.Count; i++)
            {
                if (!(images[i] is JObject entry)) throw new ArgumentException($"images[{i}] is not an object");
                var image = new ImageEntry
                {
                    Node = entry.Value<string>("node"),
                    Id = entry.Value<int?>("id"),
                    Path = entry.Value<string>("path"),
                    X = entry.Value<double?>("x") ?? 0,
                    Y = entry.Value<double?>("y") ?? 0,
                    Width = entry.Value<double?>("width") ?? 0,
                    Height = entry.Value<double?>("height") ?? 0,
                    Mode = entry.Value<string>("mode") ?? "fill",
                };

                if (image.Id == null && string.IsNullOrWhiteSpace(image.Node))
                    throw new ArgumentException($"images[{i}] needs a node path or an id");
                if (string.IsNullOrWhiteSpace(image.Path))
                    throw new ArgumentException($"images[{i}] needs a path");
                if (image.Width <= 0 || image.Height <= 0)
                    throw new ArgumentException($"images[{i}] needs a positive width and height");
                if (!Modes.Contains(image.Mode, StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException($"images[{i}] has an unknown mode '{image.Mode}' (fill|fit|crop|tile)");
                if (!targets.Add(image.Target))
                    throw new ArgumentException($"'{image.Target}' is targeted twice");

                image.Path = System.IO.Path.IsPathRooted(image.Path)
                    ? image.Path
                    : System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, image.Path));

                var extension = System.IO.Path.GetExtension(image.Path).ToLowerInvariant();
                if (!ImageExtensions.Contains(extension))
                    throw new ArgumentException(
                        $"images[{i}] is '{extension}'; image.place takes png/jpg/webp/gif/bmp " +
                        "(SVG needs file.import, which is a separate feature)");
                if (!File.Exists(image.Path))
                    throw new FileNotFoundException($"images[{i}] not found", image.Path);

                manifest.Images.Add(image);
            }

            if (manifest.Texts.Count == 0 && manifest.Images.Count == 0)
                throw new ArgumentException("the manifest has no texts and no images");

            return manifest;
        }
    }
}
