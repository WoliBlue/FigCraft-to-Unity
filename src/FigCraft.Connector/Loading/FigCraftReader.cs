using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using FigCraft.Connector.Model;
using Newtonsoft.Json.Linq;

namespace FigCraft.Connector.Loading
{
    /// <summary>
    /// Reads a FigCraft document. Two inputs are supported:
    ///  * a .figcraft file (a zip whose document.json is the native model), and
    ///  * that document.json on its own.
    /// The native model is the flat node map FigCraft writes on disk.
    /// </summary>
    public static class FigCraftReader
    {
        public const string DocumentEntry = "document.json";

        public static FigDocument Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path is empty", nameof(path));
            if (!File.Exists(path) && !Directory.Exists(path))
                throw new FileNotFoundException("FigCraft document not found", path);

            string json;
            string source = path;

            if (Directory.Exists(path))
            {
                var docPath = Path.Combine(path, DocumentEntry);
                if (!File.Exists(docPath)) throw new FileNotFoundException($"'{DocumentEntry}' not found in folder", docPath);
                json = File.ReadAllText(docPath);
            }
            else if (path.EndsWith(".figcraft", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                json = ReadFromArchive(path);
            }
            else
            {
                json = File.ReadAllText(path);
                var head = json.TrimStart();
                if (head.StartsWith("PK", StringComparison.Ordinal))
                    throw new InvalidDataException("That looks like a binary archive with a non-archive extension.");
            }

            var doc = ParseNative(JObject.Parse(json));
            doc.SourcePath = source;
            return doc;
        }

        static string ReadFromArchive(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var entry = zip.GetEntry(DocumentEntry)
                            ?? zip.Entries.FirstOrDefault(e =>
                                string.Equals(e.Name, DocumentEntry, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                    throw new InvalidDataException($"'{DocumentEntry}' not found inside {Path.GetFileName(path)}.");
                using (var reader = new StreamReader(entry.Open()))
                    return reader.ReadToEnd();
            }
        }

        // ---------------------------------------------------------------- native

        static FigDocument ParseNative(JObject root)
        {
            var doc = new FigDocument
            {
                Name = Str(root, "name", "Untitled"),
                FormatVersion = Int(root, "version", 1),
                CurrentPage = Int(root, "current_page", 0),
            };

            if (root["pages"] is JArray pages)
                doc.Pages = pages.Select(t => t.Value<int>()).ToList();

            if (root["nodes"] is JObject nodes)
            {
                foreach (var prop in nodes.Properties())
                {
                    if (prop.Value is JObject no)
                    {
                        var node = ParseNode(no);
                        doc.Nodes[node.Id] = node;
                    }
                }
            }

            if (root["styles"] is JArray styles)
                foreach (var s in styles.OfType<JObject>())
                    doc.Styles.Add(ParseStyle(s));

            if (root["variables"] is JObject vars)
            {
                if (vars["collections"] is JArray cols)
                {
                    foreach (var c in cols.OfType<JObject>())
                    {
                        var col = new FigVariableCollection
                        {
                            Id = Str(c, "id", ""),
                            Name = Str(c, "name", ""),
                        };
                        if (c["modes"] is JArray modes)
                            foreach (var m in modes.OfType<JObject>())
                                col.Modes.Add(new FigVariableMode { Id = Str(m, "id", ""), Name = Str(m, "name", "") });
                        doc.Collections.Add(col);
                    }
                }

                if (vars["variables"] is JArray vlist)
                {
                    foreach (var v in vlist.OfType<JObject>())
                    {
                        var fv = new FigVariable
                        {
                            Id = Str(v, "id", ""),
                            Name = Str(v, "name", ""),
                            CollectionId = Str(v, "collection", ""),
                            ResolvedType = Str(v, "resolvedType", "string"),
                            Description = Str(v, "description", ""),
                        };
                        if (v["values"] is JObject values)
                        {
                            foreach (var mode in values.Properties())
                                fv.Values[mode.Name] = ParseVariableValue(mode.Value as JObject);
                        }
                        doc.Variables.Add(fv);
                    }
                }
            }

            return doc;
        }

        static FigVariableValue ParseVariableValue(JObject o)
        {
            var result = new FigVariableValue();
            if (o == null) return result;
            var type = Str(o, "type", "");
            var value = o["value"];
            result.Type = type;
            switch (type)
            {
                case "color":
                    result.Color = ParseColor(value);
                    break;
                case "number":
                    result.Number = value != null ? value.Value<double>() : 0;
                    break;
                case "boolean":
                    result.Bool = value != null && value.Value<bool>();
                    break;
                case "alias":
                    result.AliasOf = value?.Value<string>();
                    break;
                default:
                    result.Text = value?.Type == JTokenType.String ? value.Value<string>() : value?.ToString();
                    break;
            }
            return result;
        }

        static FigStyle ParseStyle(JObject o)
        {
            var style = new FigStyle
            {
                Id = Str(o, "id", ""),
                Name = Str(o, "name", ""),
                Description = Str(o, "description", ""),
                Type = Str(o, "type", ""),
            };
            if (o["paints"] is JArray paints)
                style.Paints = paints.OfType<JObject>().Select(ParsePaint).ToList();
            if (o["style"] is JObject text)
                style.TextStyle = ParseTextStyle(text);
            if (o["effects"] is JArray effects)
                style.Effects = effects.OfType<JObject>().Select(ParseEffect).ToList();
            return style;
        }

        static FigNode ParseNode(JObject o)
        {
            var node = new FigNode
            {
                Id = Int(o, "id", 0),
                Name = Str(o, "name", ""),
                RawType = Str(o, "type", ""),
                Type = ParseType(Str(o, "type", "")),
                Width = D(o, "width"),
                Height = D(o, "height"),
                Opacity = o["opacity"] != null ? D(o, "opacity") : 1.0,
                Visible = o["visible"] == null || o["visible"].Value<bool>(),
                Blend = Str(o, "blend", "normal"),
                Clip = o["clip"] != null && o["clip"].Value<bool>(),
                Characters = Str(o, "characters", null),
            };

            if (o["parent"] != null && o["parent"].Type != JTokenType.Null) node.Parent = o["parent"].Value<int>();
            if (o["children"] is JArray ch) node.Children = ch.Select(t => t.Value<int>()).ToList();

            if (o["transform"] is JArray tr && tr.Count >= 6)
                node.Transform = tr.Select(t => t.Value<double>()).ToArray();

            if (o["fills"] is JArray fills)
                foreach (var f in fills.OfType<JObject>()) node.Fills.Add(ParsePaint(f));
            if (o["strokes"] is JArray strokes)
                foreach (var s in strokes.OfType<JObject>()) node.Strokes.Add(ParsePaint(s));
            if (o["stroke"] is JObject st) node.Stroke = ParseStroke(st);
            if (o["effects"] is JArray fx)
                foreach (var e in fx.OfType<JObject>()) node.Effects.Add(ParseEffect(e));

            if (o["corners"] is JObject corners)
            {
                if (corners["radii"] is JArray radii)
                {
                    var values = radii.Select(t => t.Value<double>()).ToList();
                    if (values.Count == 1) node.CornerRadii = new[] { values[0], values[0], values[0], values[0] };
                    else if (values.Count >= 4) node.CornerRadii = new[] { values[0], values[1], values[2], values[3] };
                }
                node.CornerSmoothing = D(corners, "smoothing");
            }

            if (o["layout"] is JObject lay) node.Layout = ParseLayout(lay);
            if (o["child_layout"] is JObject cl) node.ChildLayout = ParseChildLayout(cl);

            if (o["style"] is JObject style) node.TextStyle = ParseTextStyle(style);
            if (o["network"] is JObject net) node.Network = ParseNetwork(net);

            if (o["styles"] is JObject stl)
                foreach (var p in stl.Properties())
                    if (p.Value.Type == JTokenType.String) node.StyleRefs[p.Name] = p.Value.Value<string>();

            if (o["bound"] is JObject bound)
                foreach (var p in bound.Properties())
                    if (p.Value.Type == JTokenType.String) node.Bound[p.Name] = p.Value.Value<string>();

            if (o["var_modes"] is JObject vmodes)
                foreach (var p in vmodes.Properties())
                    if (p.Value.Type == JTokenType.String) node.VarModes[p.Name] = p.Value.Value<string>();

            if (o["component"] != null && o["component"].Type != JTokenType.Null)
                node.MainComponent = o["component"].Value<int>();
            if (o["source"] != null && o["source"].Type != JTokenType.Null)
                node.MainComponent = o["source"].Value<int>();
            node.MainComponentName = Str(o, "mainComponentName", null);
            if (o["component_set"] != null && o["component_set"].Type != JTokenType.Null)
                node.ComponentSet = o["component_set"].Value<int>();

            if (o["props"] is JObject props)
                foreach (var p in props.Properties())
                    node.ComponentProps[p.Name] = p.Value.Type == JTokenType.String ? p.Value.Value<string>() : p.Value.ToString();
            if (o["prop_refs"] is JObject refs)
                foreach (var p in refs.Properties())
                    if (p.Value.Type == JTokenType.String) node.PropRefs[p.Name] = p.Value.Value<string>();

            return node;
        }

        static FigNodeType ParseType(string raw)
        {
            switch ((raw ?? "").ToLowerInvariant())
            {
                case "page": return FigNodeType.Page;
                case "frame": return FigNodeType.Frame;
                case "group": return FigNodeType.Group;
                case "rectangle": return FigNodeType.Rectangle;
                case "ellipse": return FigNodeType.Ellipse;
                case "polygon": return FigNodeType.Polygon;
                case "star": return FigNodeType.Star;
                case "line": return FigNodeType.Line;
                case "arrow": return FigNodeType.Arrow;
                case "vector": return FigNodeType.Vector;
                case "text": return FigNodeType.Text;
                case "component": return FigNodeType.Component;
                case "instance": return FigNodeType.Instance;
                case "section": return FigNodeType.Section;
                case "slice": return FigNodeType.Slice;
                case "boolean": return FigNodeType.Boolean;
                default: return FigNodeType.Unknown;
            }
        }

        static FigPaint ParsePaint(JObject o)
        {
            var paint = new FigPaint
            {
                Type = Str(o, "type", "solid"),
                Kind = Str(o, "kind", null),
                Opacity = o["opacity"] != null ? D(o, "opacity") : 1.0,
                Visible = o["visible"] == null || o["visible"].Value<bool>(),
                Blend = Str(o, "blend", "normal"),
                ImageRef = Str(o, "image", Str(o, "imageRef", null)),
            };

            if (o["color"] != null) paint.Color = ParseColor(o["color"]);

            if (o["stops"] is JArray stops)
                foreach (var s in stops.OfType<JObject>())
                    paint.Stops.Add(new FigGradientStop
                    {
                        Position = D(s, "position"),
                        Color = ParseColor(s["color"]),
                    });

            paint.From = ParsePair(o["from"]);
            paint.To = ParsePair(o["to"]);
            paint.Width = ParsePair(o["width"]);
            return paint;
        }

        static double[] ParsePair(JToken t)
        {
            if (t is JArray a && a.Count >= 2) return new[] { a[0].Value<double>(), a[1].Value<double>() };
            return null;
        }

        static FigStroke ParseStroke(JObject o)
        {
            var s = new FigStroke
            {
                Weight = D(o, "weight", 1),
                Align = Str(o, "align", "center"),
                CapStart = Str(o, "cap_start", null),
                CapEnd = Str(o, "cap_end", null),
                Join = Str(o, "join", null),
                MiterLimit = D(o, "miter_limit", 4),
            };
            if (o["sides"] is JArray sides && sides.Count == 4)
                s.Sides = sides.Select(t => t.Value<bool>()).ToArray();
            if (o["dashes"] is JArray dashes)
                s.Dashes = dashes.Select(t => t.Value<double>()).ToList();
            return s;
        }

        static FigEffect ParseEffect(JObject o)
        {
            return new FigEffect
            {
                Type = Str(o, "type", "dropShadow"),
                Color = ParseColor(o["color"]),
                Offset = ParsePair(o["offset"]) ?? new double[] { 0, 0 },
                Radius = D(o, "radius"),
                Spread = D(o, "spread"),
                Visible = o["visible"] == null || o["visible"].Value<bool>(),
                Blend = Str(o, "blend", "normal"),
                ShowBehind = o["show_behind"] != null && o["show_behind"].Value<bool>(),
            };
        }

        static FigLayout ParseLayout(JObject o)
        {
            var l = new FigLayout
            {
                Mode = Str(o, "mode", "none"),
                Wrap = o["wrap"] != null && o["wrap"].Value<bool>(),
                Gap = D(o, "gap"),
                CrossGap = D(o, "cross_gap", 10),
                MainAlign = Str(o, "main_align", "min"),
                CrossAlign = Str(o, "cross_align", "min"),
                ContentAlign = Str(o, "content_align", "auto"),
                GridColumns = Int(o, "grid_columns", 2),
                GridRows = Int(o, "grid_rows", 2),
                ReverseZ = o["reverse_z"] != null && o["reverse_z"].Value<bool>(),
            };
            if (o["padding"] is JArray p && p.Count >= 4)
                l.Padding = p.Select(t => t.Value<double>()).ToArray();
            return l;
        }

        static FigChildLayout ParseChildLayout(JObject o)
        {
            return new FigChildLayout
            {
                SizingH = Str(o, "sizing_h", "fixed"),
                SizingV = Str(o, "sizing_v", "fixed"),
                Absolute = o["absolute"] != null && o["absolute"].Value<bool>(),
                MinW = DNull(o, "min_w"), MaxW = DNull(o, "max_w"),
                MinH = DNull(o, "min_h"), MaxH = DNull(o, "max_h"),
                GridColumn = INull(o, "grid_column"),
                GridRow = INull(o, "grid_row"),
                GridColumnSpan = INull(o, "grid_column_span"),
                GridRowSpan = INull(o, "grid_row_span"),
            };
        }

        static FigTextStyle ParseTextStyle(JObject o)
        {
            var t = new FigTextStyle
            {
                FontFamily = Str(o, "font_family", "Inter"),
                FontStyle = Str(o, "font_style", "Regular"),
                FontSize = D(o, "font_size", 14),
                Decoration = Str(o, "decoration", "none"),
                TextCase = Str(o, "case", "original"),
                Align = Str(o, "align", Str(o, "text_align", "left")),
                VerticalAlign = Str(o, "align_vertical", "top"),
                AutoResize = Str(o, "auto_resize", "widthAndHeight"),
                StyleId = Str(o, "style_id", null),
            };
            if (o["line_height"] is JObject lh)
            {
                t.LineHeightUnit = Str(lh, "unit", "auto");
                t.LineHeightValue = D(lh, "value");
            }
            if (o["letter_spacing"] is JObject ls)
            {
                t.LetterSpacingUnit = Str(ls, "unit", "percent");
                t.LetterSpacingValue = D(ls, "value");
            }
            return t;
        }

        static FigVectorNetwork ParseNetwork(JObject o)
        {
            var net = new FigVectorNetwork();
            if (o["vertices"] is JArray vs)
                foreach (var v in vs.OfType<JObject>())
                    net.Vertices.Add(new FigVectorVertex { X = D(v, "x"), Y = D(v, "y"), Radius = D(v, "radius") });
            if (o["segments"] is JArray ss)
                foreach (var s in ss.OfType<JObject>())
                    net.Segments.Add(new FigVectorSegment
                    {
                        A = Int(s, "a"), B = Int(s, "b"),
                        TA = ParsePair(s["ta"]), TB = ParsePair(s["tb"]),
                    });
            if (o["regions"] is JArray regions)
                foreach (var r in regions.OfType<JObject>())
                    if (r["loops"] is JArray loops)
                        foreach (var loop in loops.OfType<JArray>())
                            net.Loops.Add(loop.Select(t => t.Value<int>()).ToList());
            return net;
        }

        // ------------------------------------------------------------------ util

        internal static FigColor ParseColor(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return FigColor.Black;
            if (t.Type == JTokenType.String) return ParseColorString(t.Value<string>());
            if (t is JObject o)
                return new FigColor(D(o, "r"), D(o, "g"), D(o, "b"), o["a"] != null ? D(o, "a") : 1.0);
            if (t is JArray a && a.Count >= 3)
                return new FigColor(a[0].Value<double>(), a[1].Value<double>(), a[2].Value<double>(),
                                    a.Count > 3 ? a[3].Value<double>() : 1.0);
            return FigColor.Black;
        }

        public static FigColor ParseColorString(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return FigColor.Black;
            s = s.Trim();
            if (s.StartsWith("#"))
            {
                var hex = s.Substring(1);
                if (hex.Length == 3)
                    hex = string.Concat(hex.Select(c => new string(c, 2)));
                if (hex.Length == 6) hex += "FF";
                if (hex.Length != 8) return FigColor.Black;
                var r = Convert.ToInt32(hex.Substring(0, 2), 16) / 255.0;
                var g = Convert.ToInt32(hex.Substring(2, 2), 16) / 255.0;
                var b = Convert.ToInt32(hex.Substring(4, 2), 16) / 255.0;
                var a = Convert.ToInt32(hex.Substring(6, 2), 16) / 255.0;
                return new FigColor(r, g, b, a);
            }
            if (s.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
            {
                var inner = s.Substring(s.IndexOf('(') + 1).TrimEnd(')');
                var parts = inner.Split(',').Select(p => p.Trim()).ToArray();
                double Get(int i)
                {
                    if (i >= parts.Length) return i == 3 ? 1 : 0;
                    return parts[i].EndsWith("%")
                        ? double.Parse(parts[i].TrimEnd('%'), CultureInfo.InvariantCulture) / 100.0
                        : double.Parse(parts[i], CultureInfo.InvariantCulture) / (i < 3 ? 255.0 : 1.0);
                }
                return new FigColor(Get(0), Get(1), Get(2), Get(3));
            }
            return FigColor.Black;
        }

        static string Str(JObject o, string name, string fallback)
        {
            var t = o?[name];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            return t.Type == JTokenType.String ? t.Value<string>() : t.ToString();
        }

        static double D(JObject o, string name, double fallback = 0)
        {
            var t = o?[name];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            return t.Value<double>();
        }

        static double? DNull(JObject o, string name)
        {
            var t = o?[name];
            if (t == null || t.Type == JTokenType.Null) return null;
            return t.Value<double>();
        }

        static int Int(JObject o, string name, int fallback = 0)
        {
            var t = o?[name];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            return t.Value<int>();
        }

        static int? INull(JObject o, string name)
        {
            var t = o?[name];
            if (t == null || t.Type == JTokenType.Null) return null;
            return t.Value<int>();
        }
    }
}
