using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FigCraft.Features.Diff
{
    public sealed class FieldChange
    {
        public string Field;
        public string Before;
        public string After;

        public override string ToString()
        {
            var before = Field == "text" ? Quotes(Before) : Before;
            var after = Field == "text" ? Quotes(After) : After;
            return $"{Field}: {before} -> {after}";
        }

        static string Quotes(string text) =>
            "\"" + (text.Length <= 60 ? text : text.Substring(0, 57) + "...") + "\"";
    }

    public sealed class LayerChange
    {
        /// <summary>added, removed or changed.</summary>
        public string Status;
        public int Id;
        public string Path;
        public readonly List<FieldChange> Fields = new List<FieldChange>();

        public string Describe() => Status == "changed"
            ? string.Join("; ", Fields.Select(f => f.ToString()))
            : Status;

        public string Label(string prefix)
        {
            var text = Status == "added"
                ? $"{prefix} new layer"
                : $"{prefix} {Describe()}";
            return text.Length <= 500 ? text : text.Substring(0, 497) + "...";
        }
    }

    public sealed class DiffResult
    {
        public readonly List<LayerChange> Added = new List<LayerChange>();
        public readonly List<LayerChange> Removed = new List<LayerChange>();
        public readonly List<LayerChange> Changed = new List<LayerChange>();

        public IEnumerable<LayerChange> All => Added.Concat(Removed).Concat(Changed);

        public int Total => Added.Count + Removed.Count + Changed.Count;
    }

    /// <summary>
    /// Compares two revisions of the same screen. Layers are matched by id first and by full path
    /// second, so a re-import that renumbers ids still lines up. Only properties that a Unity
    /// developer has to act on are compared; layout-driven x/y drift is deliberately ignored.
    /// </summary>
    public static class DiffEngine
    {
        public static DiffResult Compare(DocumentIndex before, DocumentIndex after)
        {
            var result = new DiffResult();
            var beforeNodes = before.Nodes.ToDictionary(n => n.Id);
            var afterNodes = after.Nodes.ToDictionary(n => n.Id);

            var matchedBefore = new HashSet<int>();
            var matchedAfter = new HashSet<int>();

            // Same id: the layer was edited (or renamed, or moved) in place.
            foreach (var old in beforeNodes.Values.OrderBy(n => n.Id))
            {
                if (!afterNodes.TryGetValue(old.Id, out var current)) continue;

                matchedBefore.Add(old.Id);
                matchedAfter.Add(current.Id);
                CompareFields(old, current, before, after, result);
            }

            // Different id, same path: the file was re-imported or rebuilt.
            var afterByPath = UniquePaths(after, matchedAfter);
            foreach (var old in beforeNodes.Values.Where(n => !matchedBefore.Contains(n.Id)).OrderBy(n => n.Id))
            {
                var path = before.PathOf(old.Id);
                if (string.IsNullOrEmpty(path)) continue;
                if (!afterByPath.TryGetValue(path, out var currentId)) continue;
                if (!afterNodes.TryGetValue(currentId, out var current)) continue;

                matchedBefore.Add(old.Id);
                matchedAfter.Add(currentId);
                CompareFields(old, current, before, after, result);

                var change = result.Changed.FirstOrDefault(c => c.Id == currentId);
                if (change == null)
                {
                    change = new LayerChange { Status = "changed", Id = currentId, Path = after.PathOf(currentId) };
                    result.Changed.Add(change);
                }
                change.Fields.Insert(0, new FieldChange { Field = "id", Before = old.Id.ToString(), After = currentId.ToString() });
            }

            foreach (var old in beforeNodes.Values.Where(n => !matchedBefore.Contains(n.Id)).OrderBy(n => n.Id))
            {
                result.Removed.Add(new LayerChange
                {
                    Status = "removed",
                    Id = old.Id,
                    Path = before.PathOf(old.Id),
                });
            }

            foreach (var current in afterNodes.Values.Where(n => !matchedAfter.Contains(n.Id)).OrderBy(n => n.Id))
            {
                result.Added.Add(new LayerChange
                {
                    Status = "added",
                    Id = current.Id,
                    Path = after.PathOf(current.Id),
                });
            }

            result.Added.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            result.Removed.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            result.Changed.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            return result;
        }

        static void CompareFields(DocNode old, DocNode current, DocumentIndex before, DocumentIndex after, DiffResult result)
        {
            var fields = new List<FieldChange>();

            if (!string.Equals(old.Name, current.Name, StringComparison.Ordinal))
                fields.Add(Field("name", old.Name, current.Name));

            if (old.Parent != current.Parent)
                fields.Add(Field("parent",
                    old.Parent.HasValue ? before.PathOf(old.Parent.Value) : "(page)",
                    current.Parent.HasValue ? after.PathOf(current.Parent.Value) : "(page)"));

            if (old.Characters != null || current.Characters != null)
            {
                var a = old.Characters ?? "";
                var b = current.Characters ?? "";
                if (!string.Equals(a, b, StringComparison.Ordinal)) fields.Add(Field("text", a, b));
            }

            var oldSize = Size(old);
            var newSize = Size(current);
            if (!string.Equals(oldSize, newSize, StringComparison.Ordinal)) fields.Add(Field("size", oldSize, newSize));

            var oldVisible = Bool(old.Raw, "visible", true);
            var newVisible = Bool(current.Raw, "visible", true);
            if (oldVisible != newVisible) fields.Add(Field("visible", oldVisible.ToString(), newVisible.ToString()));

            var oldOpacity = Number(old.Raw, "opacity", 1);
            var newOpacity = Number(current.Raw, "opacity", 1);
            if (Math.Abs(oldOpacity - newOpacity) > 0.001) fields.Add(Field("opacity", Fmt(oldOpacity), Fmt(newOpacity)));

            var oldFill = Fill(old.Raw);
            var newFill = Fill(current.Raw);
            if (!string.Equals(oldFill, newFill, StringComparison.Ordinal)) fields.Add(Field("fill", oldFill, newFill));

            var oldRadius = Radius(old.Raw);
            var newRadius = Radius(current.Raw);
            if (!string.Equals(oldRadius, newRadius, StringComparison.Ordinal)) fields.Add(Field("radius", oldRadius, newRadius));

            var oldLayout = Layout(old.Raw);
            var newLayout = Layout(current.Raw);
            if (!string.Equals(oldLayout, newLayout, StringComparison.Ordinal)) fields.Add(Field("layout", oldLayout, newLayout));

            if (fields.Count == 0) return;

            var change = new LayerChange
            {
                Status = "changed",
                Id = current.Id,
                Path = after.PathOf(current.Id),
            };
            change.Fields.AddRange(fields);
            result.Changed.Add(change);
        }

        static FieldChange Field(string field, string before, string after) =>
            new FieldChange { Field = field, Before = before, After = after };

        static string Size(DocNode node) => $"{Fmt(node.Width ?? 0)}x{Fmt(node.Height ?? 0)}";

        static bool Bool(JObject node, string key, bool fallback) =>
            node?.Value<bool?>(key) ?? fallback;

        static double Number(JObject node, string key, double fallback) =>
            node?.Value<double?>(key) ?? fallback;

        static string Fmt(double value) =>
            Math.Abs(value - Math.Round(value)) < 0.001 ? ((int)Math.Round(value)).ToString() : value.ToString("0.##");

        static string Fill(JObject node)
        {
            if (!(node?["fills"] is JArray fills) || fills.Count == 0) return "none";

            var visible = fills.OfType<JObject>().Where(f => f.Value<bool?>("visible") ?? true).ToList();
            if (visible.Count == 0) return "none";

            var first = visible[0];
            string summary;
            switch ((first.Value<string>("type") ?? "").ToLowerInvariant())
            {
                case "solid":
                    var color = first["color"] as JObject;
                    summary = color == null ? "solid" : Hex(color);
                    break;
                case "image":
                    summary = "image";
                    break;
                case "lineargradient":
                case "radialgradient":
                case "angulargradient":
                case "diamondgradient":
                    summary = "gradient";
                    break;
                default:
                    summary = first.Value<string>("type") ?? "paint";
                    break;
            }

            return visible.Count > 1 ? summary + " +" + (visible.Count - 1) : summary;
        }

        static string Hex(JObject color)
        {
            var r = (int)Math.Round(Math.Max(0, Math.Min(1, color.Value<double?>("r") ?? 0)) * 255);
            var g = (int)Math.Round(Math.Max(0, Math.Min(1, color.Value<double?>("g") ?? 0)) * 255);
            var b = (int)Math.Round(Math.Max(0, Math.Min(1, color.Value<double?>("b") ?? 0)) * 255);
            var a = color.Value<double?>("a") ?? 1;
            var hex = $"#{r:X2}{g:X2}{b:X2}";
            return a >= 0.999 ? hex : hex + $" {a:0.##}";
        }

        static string Radius(JObject node)
        {
            var radius = node?["cornerRadius"];
            if (radius == null) return "0";
            if (radius is JArray array)
            {
                var values = array.Select(v => v.Value<double?>()).ToList();
                if (values.All(v => v.HasValue && Math.Abs(v.Value - values[0].Value) < 0.001)) return Fmt(values[0].Value);
                return radius.ToString(Formatting.None);
            }
            return Fmt(radius.Value<double?>() ?? 0);
        }

        static string Layout(JObject node) =>
            node?["layout"] is JObject layout ? layout.Value<string>("mode") ?? "none" : "none";

        static Dictionary<string, int> UniquePaths(DocumentIndex index, HashSet<int> alreadyMatched)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);

            foreach (var node in index.Nodes.Where(n => !alreadyMatched.Contains(n.Id)))
            {
                var path = index.PathOf(node.Id);
                if (string.IsNullOrEmpty(path)) continue;
                if (map.ContainsKey(path)) ambiguous.Add(path);
                else map[path] = node.Id;
            }

            foreach (var path in ambiguous) map.Remove(path);
            return map;
        }
    }
}
