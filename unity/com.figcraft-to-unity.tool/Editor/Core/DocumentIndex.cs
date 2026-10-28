using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace FigCraft.Features
{
    public sealed class DocNode
    {
        public int Id;
        public string Name = "";
        public string Type = "";
        public int? Parent;
        public string Characters;
        public double? Width;
        public double? Height;

        /// <summary>The node as it appears in document.json; comparison tools read properties here.</summary>
        public JObject Raw;
    }

    /// <summary>
    /// Reads the ids, names and parents of a FigCraft document. It deliberately does not depend on the
    /// connector core: this package has to be usable (and removable) on its own.
    /// </summary>
    public sealed class DocumentIndex
    {
        const string DocumentEntry = "document.json";

        readonly Dictionary<int, DocNode> _nodes = new Dictionary<int, DocNode>();
        readonly Dictionary<string, int> _byPath = new Dictionary<string, int>(StringComparer.Ordinal);

        public IEnumerable<DocNode> Nodes => _nodes.Values;

        public static DocumentIndex Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path is empty", nameof(path));
            if (!File.Exists(path) && !Directory.Exists(path))
                throw new FileNotFoundException("FigCraft document not found", path);

            string json;
            if (Directory.Exists(path))
            {
                json = File.ReadAllText(Path.Combine(path, DocumentEntry));
            }
            else if (path.EndsWith(".figcraft", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = File.OpenRead(path);
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
                var entry = zip.GetEntry(DocumentEntry)
                            ?? zip.Entries.FirstOrDefault(e => string.Equals(e.Name, DocumentEntry, StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException($"'{DocumentEntry}' not found inside {Path.GetFileName(path)}.");
                using var reader = new StreamReader(entry.Open());
                json = reader.ReadToEnd();
            }
            else
            {
                json = File.ReadAllText(path);
            }

            var root = JObject.Parse(json);
            var index = new DocumentIndex();
            if (root["nodes"] is JObject nodes)
            {
                foreach (var property in nodes.Properties())
                {
                    if (!(property.Value is JObject node)) continue;
                    var id = node.Value<int?>("id") ?? 0;
                    if (id <= 0) continue;
                    index._nodes[id] = new DocNode
                    {
                        Id = id,
                        Name = node.Value<string>("name") ?? "",
                        Type = node.Value<string>("type") ?? "",
                        Parent = node.Value<int?>("parent"),
                        Characters = node.Value<string>("characters"),
                        Width = node.Value<double?>("width"),
                        Height = node.Value<double?>("height"),
                        Raw = node,
                    };
                }
            }

            foreach (var node in index._nodes.Values)
            {
                var pathOf = index.PathOf(node.Id);
                if (string.IsNullOrEmpty(pathOf)) continue;
                // A duplicated path cannot identify a node; mark it ambiguous instead of picking one.
                index._byPath[pathOf] = index._byPath.TryGetValue(pathOf, out var existing) ? -1 : node.Id;
            }

            return index;
        }

        /// <summary>Report node ids are authoritative; the path is the fallback when the document moved.</summary>
        public int? Find(int id, string path)
        {
            if (id > 0 && _nodes.ContainsKey(id)) return id;
            if (!string.IsNullOrEmpty(path) && _byPath.TryGetValue(path, out var byPath) && byPath > 0) return byPath;
            return null;
        }

        public string PathOf(int id)
        {
            var parts = new List<string>();
            var current = id;
            var guard = 0;
            while (current > 0 && guard++ < 256)
            {
                if (!_nodes.TryGetValue(current, out var node)) break;
                parts.Insert(0, node.Name);
                if (!node.Parent.HasValue) break;
                current = node.Parent.Value;
            }
            return string.Join("/", parts);
        }

        public DocNode Node(int id) => _nodes.TryGetValue(id, out var node) ? node : null;
    }
}
