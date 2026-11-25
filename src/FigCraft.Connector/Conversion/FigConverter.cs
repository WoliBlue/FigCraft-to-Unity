using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using FigCraft.Connector.Model;

namespace FigCraft.Connector.Conversion
{
    public sealed class ConversionResult
    {
        public string UxmlPath;
        public string UssPath;
        public string TokensPath;
        public string ThemesPath;
        public string StylesPath;
        public string ReportPath;
        public ImportReport Report;
        public string Uxml;
    }

    /// <summary>
    /// Walks a FigCraft frame and writes UI Toolkit content: one .uxml, one .uss per frame,
    /// tokens/themes/styles sheets, an import report and (optionally) baked assets.
    /// </summary>
    public sealed class FigConverter
    {
        readonly FigDocument _doc;
        readonly ConversionOptions _opts;
        readonly ImportReport _report = new ImportReport();

        readonly Dictionary<int, string> _classOf = new Dictionary<int, string>();
        readonly Dictionary<int, string> _nameOf = new Dictionary<int, string>();
        readonly HashSet<string> _usedClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        readonly Dictionary<string, string> _varNameOf = new Dictionary<string, string>();
        readonly Dictionary<string, string> _modeClassOf = new Dictionary<string, string>();
        readonly Dictionary<string, List<string>> _themeProps = new Dictionary<string, List<string>>();

        /// <summary>Style classes that actually produced declarations; the UXML only references these.</summary>
        readonly HashSet<string> _emittedStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Gap per container, emulated with margins when the target has no row-gap/column-gap.</summary>
        readonly Dictionary<int, string> _gapValueOf = new Dictionary<int, string>();
        readonly Dictionary<int, string> _crossGapValueOf = new Dictionary<int, string>();
        readonly HashSet<int> _gapEmulated = new HashSet<int>();
        readonly HashSet<int> _wrapGapReported = new HashSet<int>();
        readonly HashSet<int> _gridPlacementReported = new HashSet<int>();
        readonly Dictionary<string, string> _bakePathOf = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Registers a bake job once per node and kind and returns its relative path.</summary>
        string BakePath(FigNode n, string kind, string format)
        {
            var key = n.Id + "|" + kind;
            if (_bakePathOf.TryGetValue(key, out var existing)) return existing;

            var suffix = kind == "fill" ? "-fill" : kind == "stroke" ? "-stroke" : "";
            var file = _opts.BakeFolder + "/" + Slug(n.Name) + suffix + "." + format;
            _bakePathOf[key] = file;
            _report.Bakes.Add(new BakeJob
            {
                NodeId = n.Id,
                NodePath = _doc.PathOf(n),
                Reason = kind,
                Format = format,
                OutputRelativePath = file,
                Scale = _opts.BakeScale,
            });
            return file;
        }

        readonly StringBuilder _nodeUss = new StringBuilder();
        readonly StringBuilder _tokensUss = new StringBuilder();
        readonly StringBuilder _stylesUss = new StringBuilder();
        readonly HashSet<string> _reportedFonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Xml _xml;
        string _frameSlug;
        FigNode _frame;

        public FigConverter(FigDocument doc, ConversionOptions options)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _opts = options ?? new ConversionOptions();
        }

        public ImportReport Report => _report;

        // ================================================================ public

        public ConversionResult Convert(string frameSelector = null)
        {
            _frame = ResolveFrame(frameSelector);
            if (_frame == null) throw new InvalidOperationException("No frame to convert (the document has no top-level frames).");

            _frameSlug = Slug(_frame.Name);
            if (string.IsNullOrEmpty(_frameSlug)) _frameSlug = "frame-" + _frame.Id;

            _report.SourceFile = _doc.SourcePath;
            _report.Page = _frame.Parent.HasValue ? _doc.Node(_frame.Parent.Value)?.Name : null;
            _report.Frame = _frame.Name;
            _report.Profile = _opts.Profile;
            _report.UnityVersion = _opts.UnityVersion;
            _report.UnityEditorVersion = _opts.UnityEditorVersion;
            _report.UssGradients = _opts.SupportsUssGradients;
            _report.DropShadowFilter = _opts.SupportsDropShadow;
            _report.FlexGap = _opts.SupportsFlexGap;
            _report.Stats.Nodes = 0;

            BuildTokens();
            BuildThemes();
            BuildStyles();

            _xml = new Xml();
            _xml.Raw("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            _xml.Open("ui:UXML", ("xmlns:ui", "UnityEngine.UIElements"),
                                     ("xmlns:uie", "UnityEditor.UIElements"),
                                     ("editor-extension-mode", "False"));

            var sheets = new List<string> { "tokens.uss", "themes.uss", "styles.uss", _frameSlug + ".uss" };
            foreach (var sheet in sheets)
                _xml.SelfClose("Style", ("src", sheet));

            Emit(_frame, null, true);

            _xml.Close("ui:UXML");

            Directory.CreateDirectory(_opts.OutputDirectory);
            var uxmlPath = Path.Combine(_opts.OutputDirectory, _frameSlug + ".uxml");
            var ussPath = Path.Combine(_opts.OutputDirectory, _frameSlug + ".uss");
            var tokensPath = Path.Combine(_opts.OutputDirectory, "tokens.uss");
            var themesPath = Path.Combine(_opts.OutputDirectory, "themes.uss");
            var stylesPath = Path.Combine(_opts.OutputDirectory, "styles.uss");
            var reportPath = Path.Combine(_opts.OutputDirectory, _frameSlug + ".report.json");

            File.WriteAllText(uxmlPath, _xml.ToString());
            File.WriteAllText(ussPath, _nodeUss.ToString());
            File.WriteAllText(tokensPath, TokensHeader() + _tokensUss + "}\n");
            File.WriteAllText(themesPath, ThemesHeader() + RenderThemes());
            File.WriteAllText(stylesPath, StylesHeader() + _stylesUss);

            _report.UxmlPath = Relativise(uxmlPath);
            _report.UssPath = Relativise(ussPath);
            _report.Finish();
            _report.Save(reportPath);

            return new ConversionResult
            {
                UxmlPath = uxmlPath,
                UssPath = ussPath,
                TokensPath = tokensPath,
                ThemesPath = themesPath,
                StylesPath = stylesPath,
                ReportPath = reportPath,
                Report = _report,
                Uxml = _xml.ToString(),
            };
        }

        string Relativise(string path)
        {
            if (string.IsNullOrEmpty(_opts.ProjectPath)) return path;
            var file = Path.GetFileName(path);
            return _opts.ProjectPath.TrimEnd('/') + "/" + file;
        }

        FigNode ResolveFrame(string selector)
        {
            if (!string.IsNullOrEmpty(selector))
            {
                if (int.TryParse(selector, out var id) && _doc.Nodes.TryGetValue(id, out var byId)) return byId;
                var byName = _doc.Nodes.Values.FirstOrDefault(n =>
                    string.Equals(n.Name, selector, StringComparison.OrdinalIgnoreCase));
                if (byName != null) return byName;
                throw new InvalidOperationException($"No layer named '{selector}' in {_doc.Name}.");
            }

            var pages = _doc.Pages.Count > 0 ? _doc.Pages : _doc.Nodes.Values.Where(n => n.Type == FigNodeType.Page).Select(n => n.Id).ToList();
            foreach (var pid in pages)
                foreach (var n in _doc.TopLevel(pid))
                    if (n.Type == FigNodeType.Frame || n.Type == FigNodeType.Component)
                        return n;
            return _doc.Nodes.Values.FirstOrDefault(n => n.Type == FigNodeType.Frame);
        }

        // ================================================================ tokens

        string UssVar(string name)
        {
            if (_varNameOf.TryGetValue(name, out var existing)) return existing;
            var uss = "--" + Slug(name);
            _varNameOf[name] = uss;
            return uss;
        }

        string VarName(string variableId)
        {
            var v = _doc.FindVariable(variableId);
            return v != null ? UssVar(v.Name) : null;
        }

        void BuildTokens()
        {
            foreach (var col in _doc.Collections)
            {
                var defaultMode = col.DefaultModeId;
                foreach (var v in _doc.Variables.Where(x => x.CollectionId == col.Id))
                {
                    if (!v.Values.TryGetValue(defaultMode ?? "", out var value)) continue;
                    _tokensUss.AppendLine($"    {UssVar(v.Name)}: {FormatToken(v, value)};");
                }
            }
        }

        void BuildThemes()
        {
            foreach (var col in _doc.Collections)
            {
                for (var i = 0; i < col.Modes.Count; i++)
                {
                    var mode = col.Modes[i];
                    var cls = "theme-" + Slug(mode.Name);
                    _modeClassOf[mode.Id] = cls;
                    if (!_themeProps.ContainsKey(cls)) _themeProps[cls] = new List<string>();

                    var defaultMode = col.DefaultModeId;
                    foreach (var v in _doc.Variables.Where(x => x.CollectionId == col.Id))
                    {
                        var key = UssVar(v.Name);
                        if (!v.Values.TryGetValue(mode.Id, out var value)) continue;

                        if (i == 0)
                        {
                            if (!_themeProps[cls].Any(p => p.StartsWith(key + ":")))
                                _themeProps[cls].Add($"{key}: {FormatToken(v, value)};");
                            continue;
                        }

                        if (v.Values.TryGetValue(defaultMode ?? "", out var def) && SameValue(def, value))
                            continue;
                        if (!_themeProps[cls].Any(p => p.StartsWith(key + ":")))
                            _themeProps[cls].Add($"{key}: {FormatToken(v, value)};");
                    }
                }
            }
        }

        static bool SameValue(FigVariableValue a, FigVariableValue b)
        {
            if (a.Type != b.Type) return false;
            return a.Type switch
            {
                "color" => a.Color.ToHex() == b.Color.ToHex(),
                "number" => Math.Abs(a.Number - b.Number) < 0.0001,
                "boolean" => a.Bool == b.Bool,
                _ => a.Text == b.Text,
            };
        }

        string FormatToken(FigVariable v, FigVariableValue value)
        {
            switch (value.Type)
            {
                case "color": return value.Color.ToUss();
                case "number": return FormatNumberToken(v, value.Number);
                case "boolean": return value.Bool ? "true" : "false";
                case "alias":
                    var target = _doc.FindVariable(value.AliasOf);
                    return target != null ? $"var({UssVar(target.Name)})" : "0";
                default:
                    return string.IsNullOrEmpty(value.Text) ? "\"\"" : "\"" + value.Text.Replace("\"", "\\\"") + "\"";
            }
        }

        static string FormatNumberToken(FigVariable v, double n)
        {
            var name = (v.Name ?? "").ToLowerInvariant();
            // USS numbers need a unit for lengths; a few token families are unitless.
            if (name.Contains("opacity") || name.Contains("weight") || name.Contains("scale")
                || name.Contains("ratio") || name.Contains("order") || name.Contains("z-index"))
                return Num.M(n);
            return Num.M(n) + "px";
        }

        string RenderThemes()
        {
            var sb = new StringBuilder();
            foreach (var kv in _themeProps)
            {
                sb.AppendLine($".{kv.Key} {{");
                foreach (var p in kv.Value) sb.AppendLine("    " + p);
                sb.AppendLine("}");
                sb.AppendLine();
            }
            return sb.ToString();
        }

        static string TokensHeader() =>
            "/* Generated by FigCraft -> Unity UI Toolkit connector. Do not edit: re-import overwrites this file. */\n" +
            "/* Design tokens. Default values come from each collection's first mode; use the theme-* classes in themes.uss to override. */\n\n" +
            ":root {\n";

        static string ThemesHeader() =>
            "/* Generated. Theme overrides, applied as classes on any element (custom properties inherit down the tree). */\n\n";

        static string StylesHeader() =>
            "/* Generated from FigCraft styles (paint / text / effect). */\n\n";

        // ================================================================= styles

        void BuildStyles()
        {
            foreach (var style in _doc.Styles)
            {
                var cls = "style-" + Slug(style.Name);
                var props = new List<string>();
                switch (style.Type)
                {
                    case "paint":
                        var paint = style.Paints?.FirstOrDefault(p => p.Visible);
                        if (paint != null)
                        {
                            if (paint.IsSolid) props.Add($"background-color: {paint.Color.ToUss()};");
                            else if (paint.IsGradient)
                            {
                                var grad = Gradient(paint, null);
                                if (grad != null) props.Add($"background-image: {grad};");
                            }
                        }
                        break;
                    case "text":
                        if (style.TextStyle != null)
                            props.AddRange(TextProps(style.TextStyle, null, false));
                        break;
                    case "effect":
                        if (style.Effects != null)
                            AddEffects(new FigNodeRef(0, style.Name, "(effect style) " + style.Name), style.Effects, props);
                        break;
                }

                if (props.Count == 0) continue;
                _emittedStyles.Add(cls);
                AppendBlock(_stylesUss, cls, props);
            }
        }

        string StyleClass(FigNode n, string kind)
        {
            if (!n.StyleRefs.TryGetValue(kind, out var id) || string.IsNullOrEmpty(id)) return null;
            var style = _doc.FindStyle(id);
            return style != null ? "style-" + Slug(style.Name) : null;
        }

        /// <summary>The style is only usable if BuildStyles emitted a rule for it.</summary>
        FigStyle ActiveStyle(FigNode n, string kind)
        {
            var cls = StyleClass(n, kind);
            if (cls == null || !_emittedStyles.Contains(cls)) return null;
            return _doc.FindStyle(n.StyleRefs[kind]);
        }

        string ActiveStyleClass(FigNode n, string kind)
        {
            var style = ActiveStyle(n, kind);
            return style != null ? "style-" + Slug(style.Name) : null;
        }

        // ================================================================== nodes

        void Emit(FigNode n, FigNode parent, bool isRoot)
        {
            if (!n.Visible) return;
            _report.Stats.Nodes++;

            switch (n.Type)
            {
                case FigNodeType.Text:
                    EmitText(n, parent, isRoot);
                    break;
                case FigNodeType.Instance:
                    EmitInstance(n, parent, isRoot);
                    break;
                case FigNodeType.Vector:
                    EmitVector(n, parent, isRoot);
                    break;
                default:
                    EmitContainer(n, parent, isRoot);
                    break;
            }
        }

        void EmitContainer(FigNode n, FigNode parent, bool isRoot)
        {
            var cls = ClassFor(n);

            // A container that clips cannot host the shadow (it would be cut off), so the shadow moves to a
            // sibling inside a wrapper.
            if (ShadowGoesToUnderlay(n) && n.Clip)
            {
                EmitShadowWrapper(n, parent, isRoot, cls);
                return;
            }

            _xml.Open("ui:VisualElement", ("name", NameFor(n)), ("class", Classes(n, cls)));
            ApplyElement(n, parent, isRoot, cls);
            foreach (var childId in n.Children)
            {
                var child = _doc.Node(childId);
                if (child != null) Emit(child, n, false);
            }
            _xml.Close("ui:VisualElement");
        }

        void EmitInstance(FigNode n, FigNode parent, bool isRoot)
        {
            var rule = _opts.Rules.ControlRule(n);
            var control = rule?.Control ?? GuessControl(n);
            var cls = ClassFor(n);

            if (control != null && Profiles.IsValid(_opts.Profile))
            {
                var text = ControlText(n, rule);
                var attrs = new List<(string, string)>
                {
                    ("name", NameFor(n)),
                    ("class", Classes(n, cls)),
                };

                var label = FirstTextChild(n);
                var rich = label != null && (control == "Button" || control == "Label")
                    ? RichText(label, text)
                    : (Text: text, UsesTags: false);

                switch (control)
                {
                    case "Button":
                        attrs.Add(("text", rich.Text ?? ""));
                        if (rich.UsesTags) attrs.Add(("enable-rich-text", "true"));
                        break;
                    case "Toggle":
                        attrs.Add(("label", text ?? ""));
                        break;
                    case "TextField":
                        var empty = string.Equals(_opts.Rules.TextFieldPlaceholder, "empty",
                            StringComparison.OrdinalIgnoreCase);
                        attrs.Add(("value", empty ? "" : text ?? ""));
                        break;
                    case "Label":
                        attrs.Add(("text", rich.Text ?? ""));
                        if (rich.UsesTags) attrs.Add(("enable-rich-text", "true"));
                        break;
                }

                _xml.SelfClose("ui:" + control, attrs.ToArray());

                // A mapped control renders its own text: carry the label's typography over, otherwise the
                // component's 16px bold white label falls back to the theme's default button font.
                var extras = new List<string>();
                var placeholderEmpty = control == "TextField" && !string.IsNullOrEmpty(text)
                    && string.Equals(_opts.Rules.TextFieldPlaceholder, "empty", StringComparison.OrdinalIgnoreCase);

                if (label?.TextStyle != null)
                {
                    extras.AddRange(TextProps(label.TextStyle, label, false)
                        .Where(p => !p.StartsWith("-unity-text-align", StringComparison.Ordinal)));
                    if (!placeholderEmpty) extras.Add($"color: {FillColor(label)}");
                    _report.Add(Ref(n), "component.textStyle", label.Name, "carried to the control",
                        ConversionOutcome.Exact, IssueSeverity.Info, "component.textStyle.carried",
                        $"The label typography ({Num.M(label.TextStyle.FontSize)}px {(label.TextStyle.IsBold ? "bold " : "")}" +
                        $"{FillColor(label)}) is applied to <ui:{control}>, because the control draws its own text " +
                        "and the child Label is not emitted.");
                }

                if (control == "TextField" && !string.IsNullOrEmpty(text))
                {
                    if (placeholderEmpty)
                        _report.Add(Ref(n), "component.placeholder", text, "value=\"\"",
                            ConversionOutcome.Approximated, IssueSeverity.Info, "component.textfield.placeholderEmpty",
                            "The field is emitted empty (textfieldPlaceholder=empty in the rules) and the " +
                            "placeholder colour is not applied, so the text typed by the user keeps the theme's " +
                            "colour. Set textEdition.placeholder from code to show the hint.",
                            new ReportFix { Kind = "manual" });
                    else
                        _report.Add(Ref(n), "component.placeholder", text, $"value=\"{text}\"",
                            ConversionOutcome.Approximated, IssueSeverity.Warning, "component.textfield.placeholderAsValue",
                            "UI Toolkit has no placeholder attribute on TextField, so the FigCraft placeholder became the " +
                            "field's value. Set textEdition.placeholder in code (or use textfieldPlaceholder=empty in " +
                            "the rules) to restore the semantics.",
                            new ReportFix { Kind = "manual" });
                }

                ApplyElement(n, parent, isRoot, cls, skipBackground: false, extra: extras);

                _report.Add(Ref(n), "component", n.MainComponentName ?? n.Name, control,
                    ConversionOutcome.Exact, IssueSeverity.Info, "component.mapped",
                    $"FigCraft component resolved to <ui:{control}>" + (text != null ? $" with text \"{text}\"" : "") + ".");

                if (n.Children.Count > 1)
                    _report.Add(Ref(n), "component.children", n.Children.Count.ToString(), "flattened",
                        ConversionOutcome.Dropped, IssueSeverity.Warning, "component.children.flattened",
                        "The mapped control does not render the component's child layers; only the text property survives.");
                return;
            }

            _xml.Open("ui:VisualElement", ("name", NameFor(n)), ("class", Classes(n, cls)));
            ApplyElement(n, parent, isRoot, cls);
            foreach (var childId in n.Children)
            {
                var child = _doc.Node(childId);
                if (child != null) Emit(child, n, false);
            }
            _xml.Close("ui:VisualElement");

            _report.Add(Ref(n), "component", n.MainComponentName ?? n.Name, "VisualElement",
                ConversionOutcome.Degraded, IssueSeverity.Warning, "component.flattened",
                "No control mapping for this component: the instance is flattened to a VisualElement tree.",
                new ReportFix { Kind = "customControl", Command = "component-map.json" });
        }

        /// <summary>First text layer inside a component instance, in painting order.</summary>
        FigNode FirstTextChild(FigNode n)
        {
            foreach (var id in n.Children)
            {
                var child = _doc.Node(id);
                if (child == null || !child.Visible) continue;
                if (child.Type == FigNodeType.Text) return child;
                var nested = FirstTextChild(child);
                if (nested != null) return nested;
            }
            return null;
        }

        static string GuessControl(FigNode n)
        {
            var name = (n.MainComponentName ?? n.Name ?? "").ToLowerInvariant();
            if (name.Contains("button") || name.Contains("btn")) return "Button";
            if (name.Contains("toggle") || name.Contains("checkbox") || name.Contains("switch")) return "Toggle";
            if (name.Contains("input") || name.Contains("textfield") || name.Contains("field") || name.Contains("search"))
                return "TextField";
            if (name.Contains("slider")) return "Slider";
            if (name.Contains("label") || name.Contains("text")) return "Label";
            return null;
        }

        static string ControlText(FigNode n, ConversionRule rule)
        {
            if (n.ComponentProps.Count == 0) return null;
            var key = rule?.TextProperty;
            if (!string.IsNullOrEmpty(key) && n.ComponentProps.TryGetValue(key, out var direct)) return direct;
            return n.ComponentProps.Values.FirstOrDefault();
        }

        void EmitVector(FigNode n, FigNode parent, bool isRoot)
        {
            var cls = ClassFor(n);
            _xml.SelfClose("ui:VisualElement", ("name", NameFor(n)), ("class", Classes(n, cls)));

            var props = BoxProps(n, parent, isRoot);
            var rule = _opts.Rules.First(n, null);
            var action = rule?.Action ?? (_opts.Bake ? "bake" : "drop");

            if (action == "drop")
            {
                props.Add("background-color: rgba(0, 0, 0, 0)");
                _report.Add(Ref(n), "vector", $"{n.Network?.Vertices.Count ?? 0} vertices", "transparent element",
                    ConversionOutcome.Dropped, IssueSeverity.Error, "vector.dropped",
                    "Vector geometry has no direct USS equivalent and baking is disabled.",
                    new ReportFix { Kind = "bake" });
            }
            else
            {
                var file = _opts.BakeFolder + "/" + Slug(n.Name) + ".svg";
                props.Add($"background-image: url(\"{file}\")");
                props.Add("-unity-background-scale-mode: scale-to-fit");
                var job = new BakeJob
                {
                    NodeId = n.Id,
                    NodePath = _doc.PathOf(n),
                    Reason = "vector",
                    Format = "svg",
                    OutputRelativePath = file,
                    Scale = _opts.BakeScale,
                };
                _report.Bakes.Add(job);
                _report.Add(Ref(n), "vector", $"{n.Network?.Vertices.Count ?? 0} vertices", file,
                    ConversionOutcome.Degraded, IssueSeverity.Info, "vector.baked",
                    "Vector exported as SVG and used as a background image (UI Toolkit 6 imports .svg as a vector image).");
            }

            AddRule(cls, props);
        }

        // ================================================================== text

        void EmitText(FigNode n, FigNode parent, bool isRoot)
        {
            var cls = ClassFor(n);
            var props = BoxProps(n, parent, isRoot);

            var outlineWidth = n.Strokes.Count > 0 && n.Stroke != null ? n.Stroke.Weight : 0;
            var outlineColor = n.Strokes.FirstOrDefault()?.Color ?? FigColor.Black;
            var effects = (n.Effects ?? new List<FigEffect>()).Where(e => e.Visible).ToList();
            var drops = effects.Where(e => e.Type == "dropShadow").ToList();
            var shadow = drops.FirstOrDefault();

            ReportTextEffects(n, effects);
            ReportTextFill(n);

            var hasOutline = outlineWidth > 0;
            var emulate = hasOutline && shadow != null && _opts.EmulateShadowWithOutline && _opts.Profile != Profiles.Strict;

            if (emulate)
            {
                EmitTextWithShadow(n, cls, props, outlineWidth, outlineColor, shadow);
                return;
            }

            var rich = RichText(n, n.Characters);
            var labelAttrs = new List<(string, string)>
            {
                ("text", rich.Text),
                ("name", NameFor(n)),
                ("class", Classes(n, cls)),
            };
            if (rich.UsesTags) labelAttrs.Add(("enable-rich-text", "true"));
            _xml.SelfClose("ui:Label", labelAttrs.ToArray());

            var styleText = ActiveStyle(n, "text");
            props.AddRange(TextProps(n.TextStyle, n, false, styleText?.TextStyle));
            if (styleText != null)
                _report.Add(Ref(n), "text.style", styleText.Name, "delegated to the style class",
                    ConversionOutcome.Exact, IssueSeverity.Info, "text.delegatedToStyle",
                    "Font metrics come from the FigCraft text style and are emitted once, in styles.uss; " +
                    "the node rule only carries real overrides.");
            ApplyTextColor(n, props);

            if (hasOutline) props.AddRange(TextOutlineProps(n, outlineWidth, outlineColor));
            if (shadow != null) props.Add($"text-shadow: {ShadowValue(shadow)};");

            AddRule(cls, props);

            if (shadow != null && outlineWidth > 0 && _opts.Profile == Profiles.Strict)
                _report.Add(Ref(n), "text.shadow", "shadow over outline", "dropped",
                    ConversionOutcome.Dropped, IssueSeverity.Error, "text.shadowWithOutline.dropped",
                    "UI Toolkit shadows the glyph, not the outline; strict profile refuses the approximation.",
                    new ReportFix { Kind = "manual" });
        }

        void EmitTextWithShadow(FigNode n, string cls, List<string> boxProps, double outlineWidth,
            FigColor outlineColor, FigEffect shadow)
        {
            var wrapper = cls + "-wrap";
            var shadowCls = cls + "-shadow";
            var textCls = cls + "-text";

            _xml.Open("ui:VisualElement", ("name", NameFor(n)), ("class", Classes(n, wrapper)));
            var rich = RichText(n, n.Characters);
            var shadowAttrs = new List<(string, string)>
            {
                ("text", rich.Text),
                ("name", NameFor(n) + "_shadow"),
                ("class", shadowCls),
                ("picking-mode", "Ignore"),
            };
            if (rich.UsesTags) shadowAttrs.Add(("enable-rich-text", "true"));
            _xml.SelfClose("ui:Label", shadowAttrs.ToArray());

            var textAttrs = new List<(string, string)>
            {
                ("text", rich.Text),
                ("name", NameFor(n) + "_text"),
                ("class", textCls),
            };
            if (rich.UsesTags) textAttrs.Add(("enable-rich-text", "true"));
            _xml.SelfClose("ui:Label", textAttrs.ToArray());
            _xml.Close("ui:VisualElement");

            // The two Labels are absolutely positioned inside the wrapper, so the wrapper has to own the
            // text's box; otherwise a flex parent gives it zero height and the text collapses.
            if (n.Width > 0) boxProps.Add($"width: {Num.Px(n.Width)}");
            if (n.Height > 0) boxProps.Add($"height: {Num.Px(n.Height)}");
            boxProps.Add("position: relative");
            AddRule(wrapper, boxProps);

            var textProps = TextProps(n.TextStyle, n, false, ActiveStyle(n, "text")?.TextStyle);

            var shadowProps = new List<string>(textProps)
            {
                $"color: {shadow.Color.ToUss()}",
                $"-unity-text-outline-color: {shadow.Color.ToUss()}",
                $"-unity-text-outline-width: {Num.Px(Math.Max(outlineWidth * 2 + shadow.Spread, outlineWidth))}",
                "position: absolute",
                $"left: {Num.Px(shadow.Offset[0])}",
                $"top: {Num.Px(shadow.Offset[1])}",
            };
            AddRule(shadowCls, shadowProps);

            var mainProps = new List<string>(textProps);
            mainProps.Add($"color: {FillColor(n)}");
            mainProps.AddRange(TextOutlineProps(n, outlineWidth, outlineColor));
            mainProps.Add("position: absolute");
            mainProps.Add("left: 0");
            mainProps.Add("top: 0");
            AddRule(textCls, mainProps);

            _report.Add(Ref(n), "text.shadow", $"shadow + {Num.M(outlineWidth)}px outline", "two Labels",
                ConversionOutcome.Approximated, IssueSeverity.Warning, "text.shadowWithOutline.emulated",
                "Emulated with a tinted duplicate whose outline is widened, so the shadow follows the outlined shape. " +
                "Costs one extra Label per text; disable with profile=strict or emulateShadowWithOutline=false.");
        }

        IEnumerable<string> TextOutlineProps(FigNode n, double width, FigColor color)
        {
            var align = n.Stroke?.Align ?? "center";
            var effective = align == "center" ? width : width * 2;
            if (align != "center")
            {
                _report.Add(Ref(n), "stroke.align", align, $"-unity-text-outline-width: {Num.Px(effective)}",
                    ConversionOutcome.Approximated, IssueSeverity.Warning, "text.outline.align.folded",
                    $"UI Toolkit outlines are centred; a {align} {Num.M(width)}px outline is approximated by doubling the width.");
            }

            var limit = Math.Max(1, _opts.TextOutlinePaddingLimit);
            if (effective > limit)
            {
                var required = (int)Math.Ceiling(effective / 2.0) + 2;
                _report.Add(Ref(n), "text.outline.width", $"{Num.M(width)}px {align}",                    $"{Num.Px(effective)} (SDF limit ~{limit}px)",
                    ConversionOutcome.Approximated, IssueSeverity.Warning, "text.outline.padding.exceeded",
                    $"Outline exceeds the assumed FontAsset SDF padding ({limit}px), so it will clip or fade. " +
                    $"Regenerate the FontAsset with padding >= {required}.",
                    new ReportFix { Kind = "fontAssetPadding", Required = required });
            }

            yield return $"-unity-text-outline-color: {color.ToUss()}";
            yield return $"-unity-text-outline-width: {Num.Px(effective)}";
        }

        void ApplyTextColor(FigNode n, List<string> props)
        {
            if (n.Bound.TryGetValue("fill", out var varId) && VarName(varId) != null)
                props.Add($"color: var({VarName(varId)})");
            else
                props.Add($"color: {FillColor(n)}");
        }

        /// <summary>Effects that USS cannot paint on a Label are reported here instead of vanishing.</summary>
        void ReportTextEffects(FigNode n, List<FigEffect> effects)
        {
            var drops = effects.Where(e => e.Type == "dropShadow").ToList();
            if (drops.Count > 1)
                _report.Add(Ref(n), "effect.dropShadow", drops.Count + " drop shadows", "first only",
                    ConversionOutcome.Dropped, IssueSeverity.Info, "text.shadow.multiple.dropped",
                    "USS text-shadow holds a single shadow; the extra FigCraft shadows are not emitted.",
                    new ReportFix { Kind = "bake" });

            foreach (var s in drops.Where(s => Math.Abs(s.Spread) > 0.001).Take(1))
                _report.Add(Ref(n), "effect.spread", Num.M(s.Spread) + "px", "dropped",
                    ConversionOutcome.Approximated, IssueSeverity.Warning, "text.shadow.spread.folded",
                    "USS text-shadow takes offset, blur and colour only; the spread radius is lost, so the shadow is thinner than in FigCraft.");

            foreach (var e in effects.Where(e => e.Type == "innerShadow"))
                _report.Add(Ref(n), "effect.innerShadow", Num.M(e.Radius) + "px blur", "dropped",
                    ConversionOutcome.Dropped, IssueSeverity.Warning, "text.innerShadow.dropped",
                    "USS has no inset text shadow; bake the text or draw the highlight yourself.",
                    new ReportFix { Kind = "bake" });

            foreach (var e in effects.Where(e => e.Type != "dropShadow" && e.Type != "innerShadow"))
                _report.Add(Ref(n), "effect", e.Type, "dropped", ConversionOutcome.Dropped, IssueSeverity.Warning,
                    "text.effect." + e.Type + ".dropped",
                    "Blur filters apply to the element box, not to its glyphs, so a blurred Label is not representable; bake it if the effect is essential.",
                    new ReportFix { Kind = "bake" });
        }

        /// <summary>Label glyphs take one colour: gradient and image fills become their first stop.</summary>
        void ReportTextFill(FigNode n)
        {
            var fill = n.PrimaryFill;
            if (fill == null) return;
            if (fill.IsGradient)
                _report.Add(Ref(n), "text.fill", (fill.Kind ?? "linear") + " gradient", "solid colour from the first stop",
                    ConversionOutcome.Approximated, IssueSeverity.Warning, "text.fill.gradient.folded",
                    "USS colours a Label with a single colour, not a gradient; the first stop is used. Bake the text or overlay a gradient mask if it matters.",
                    new ReportFix { Kind = "bake" });
            else if (fill.Type == "image")
                _report.Add(Ref(n), "text.fill", fill.ImageRef, "solid colour", ConversionOutcome.Dropped,
                    IssueSeverity.Warning, "text.fill.image.dropped",
                    "Image fills on text are not representable in USS; the Label falls back to the paint's colour.",
                    new ReportFix { Kind = "bake" });
        }

        string FillColor(FigNode n)
        {
            var fill = n.PrimaryFill;
            if (fill == null) return "rgba(0, 0, 0, 0)";
            if (fill.IsGradient && fill.Stops.Count > 0)
                return fill.Stops.OrderBy(s => s.Position).First().Color.ToUss();
            return fill.Color.ToUss();
        }

        IEnumerable<string> TextProps(FigTextStyle t, FigNode n, bool includeColor, FigTextStyle baseline = null)
        {
            var props = TextPropsCore(t, n, includeColor);
            if (baseline == null) return props;

            // A referenced text style already ships these declarations in styles.uss. Only the values that differ
            // from it are repeated here, so editing the style keeps flowing into the frame.
            var baseProps = TextPropsCore(baseline, null, false, report: false);
            var baseByKey = baseProps.GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
            return props.Where(p => !baseByKey.TryGetValue(Key(p), out var b) || Value(b) != Value(p)).ToList();
        }

        static string Key(string property)
        {
            var i = property.IndexOf(':');
            return i < 0 ? property : property.Substring(0, i).Trim();
        }

        static string Value(string property)
        {
            var i = property.IndexOf(':');
            return i < 0 ? "" : property.Substring(i + 1).Trim();
        }

        List<string> TextPropsCore(FigTextStyle t, FigNode n, bool includeColor, bool report = true)
        {
            var props = new List<string>();
            if (t == null) return props;

            props.Add($"font-size: {Num.Px(t.FontSize)}");

            var face = (t.FontFamily ?? "Inter") + "|" + (t.FontStyle ?? "Regular");
            if (_opts.Fonts.TryGetValue(face, out var asset) || _opts.Fonts.TryGetValue(t.FontFamily ?? "", out asset))
            {
                props.Add($"-unity-font-definition: url(\"{AssetUrl(asset)}\")");
            }
            else
            {
                if (t.IsBold && t.IsItalic) props.Add("-unity-font-style: bold-and-italic");
                else if (t.IsBold) props.Add("-unity-font-style: bold");
                else if (t.IsItalic) props.Add("-unity-font-style: italic");

                if (report && _reportedFonts.Add(face))
                {
                    var where = n != null ? _doc.PathOf(n) : "(text style)";
                    _report.Add(new FigNodeRef(n?.Id ?? 0, n?.Name ?? "(style)", where), "font", face, "nearest face",
                        ConversionOutcome.Approximated, IssueSeverity.Warning, "font.unresolved",
                        $"No FontAsset mapped for '{face}'. Add it to fonts.json (family -> face -> SDF asset) so weights match exactly.",
                        new ReportFix { Kind = "manual", Asset = "Assets/Fonts/Inter-Bold SDF.asset" });
                }
            }

            if (string.Equals(t.LetterSpacingUnit, "percent", StringComparison.OrdinalIgnoreCase) && Math.Abs(t.LetterSpacingValue) > 0.0001)
            {
                var px = t.FontSize * t.LetterSpacingValue / 100.0;
                props.Add($"letter-spacing: {Num.Px(px)}");
            }
            else if (t.LetterSpacingUnit == "px" && Math.Abs(t.LetterSpacingValue) > 0.0001)
            {
                props.Add($"letter-spacing: {Num.Px(t.LetterSpacingValue)}");
            }

            var align = (t.Align ?? "left").ToLowerInvariant();
            var horizontal = align == "center" ? "center" : align == "right" ? "right" : "left";

            // -unity-text-align is "<vertical>-<horizontal>"; FigCraft stores the text box's vertical
            // alignment (top by default, like Figma), which only matters when the box is taller than the text.
            var vertical = (t.VerticalAlign ?? "").ToLowerInvariant();
            var verticalAnchor = vertical.StartsWith("bottom") || vertical.StartsWith("lower") ? "lower"
                : vertical.StartsWith("center") || vertical.StartsWith("middle") ? "middle"
                : vertical.StartsWith("top") || vertical.StartsWith("upper") ? "upper"
                : "middle";
            props.Add($"-unity-text-align: {verticalAnchor}-{horizontal}");
            return props;
        }

        // ============================================================== geometry

        List<string> BoxProps(FigNode n, FigNode parent, bool isRoot)
        {
            var props = new List<string>();
            var parentAuto = parent != null && parent.Layout.IsAutoLayout && !n.ChildLayout.Absolute && !isRoot;

            if (isRoot || parent == null || parent.Type == FigNodeType.Page)
            {
                props.Add($"width: {Num.Px(n.Width)}");
                props.Add($"height: {Num.Px(n.Height)}");
            }
            else if (!parentAuto)
            {
                props.Add("position: absolute");
                props.Add($"left: {Num.Px(n.X)}");
                props.Add($"top: {Num.Px(n.Y)}");
                if (n.Type != FigNodeType.Text)
                {
                    props.Add($"width: {Num.Px(n.Width)}");
                    props.Add($"height: {Num.Px(n.Height)}");
                }
            }
            else
            {
                var horizontal = parent.Layout.Mode == "horizontal";
                var mainSizing = horizontal ? n.ChildLayout.SizingH : n.ChildLayout.SizingV;
                var crossSizing = horizontal ? n.ChildLayout.SizingV : n.ChildLayout.SizingH;

                if (mainSizing == "fill") props.Add("flex-grow: 1");
                if (crossSizing == "fill") props.Add("align-self: stretch");

                if (n.Type != FigNodeType.Text)
                {
                    if (n.ChildLayout.SizingH != "hug" && n.ChildLayout.SizingH != "fill")
                        props.Add($"width: {Num.Px(n.Width)}");
                    if (n.ChildLayout.SizingV != "hug" && n.ChildLayout.SizingV != "fill")
                        props.Add($"height: {Num.Px(n.Height)}");
                }

                AddEmulatedGap(n, parent, props);
            }

            if (n.ChildLayout.MinW.HasValue) props.Add($"min-width: {Num.Px(n.ChildLayout.MinW.Value)}");
            if (n.ChildLayout.MaxW.HasValue) props.Add($"max-width: {Num.Px(n.ChildLayout.MaxW.Value)}");
            if (n.ChildLayout.MinH.HasValue) props.Add($"min-height: {Num.Px(n.ChildLayout.MinH.Value)}");
            if (n.ChildLayout.MaxH.HasValue) props.Add($"max-height: {Num.Px(n.ChildLayout.MaxH.Value)}");

            if (Math.Abs(n.Opacity - 1) > 0.001) props.Add($"opacity: {Num.M(n.Opacity)}");
            if (n.Clip) props.Add("overflow: hidden");

            var rotation = n.RotationDegrees;
            if (Math.Abs(rotation) > 0.001)
            {
                props.Add($"rotate: {Num.M(rotation)}deg");
                _report.Add(Ref(n), "rotation", Num.M(rotation) + "deg", "rotate", ConversionOutcome.Approximated,
                    IssueSeverity.Info, "transform.rotation", "Only uniform scale + rotation is preserved; skew is dropped.");
            }

            if (Math.Abs(n.CornerSmoothing) > 0.001)
                _report.Add(Ref(n), "cornerSmoothing", Num.M(n.CornerSmoothing), "dropped",
                    ConversionOutcome.Dropped, IssueSeverity.Info, "corner.smoothing.dropped",
                    "USS has no squircle smoothing; corners are plain radii.");

            return props;
        }

        /// <summary>Before Unity 6.7 a flex gap has to be a margin on every child but the first.</summary>
        void AddEmulatedGap(FigNode n, FigNode parent, List<string> props)
        {
            if (_opts.SupportsFlexGap || n.ChildLayout.Absolute) return;
            if (!_gapValueOf.TryGetValue(parent.Id, out var gap) || string.IsNullOrEmpty(gap)) return;

            var index = LayoutIndex(n, parent);
            if (index < 0) return;

            var horizontal = parent.Layout.Mode == "horizontal";
            var cross = _crossGapValueOf.TryGetValue(parent.Id, out var c) && !string.IsNullOrEmpty(c) ? c : gap;

            if (parent.Layout.IsGrid)
            {
                // FigCraft grids fill row by row in layer order; an explicit grid_column/grid_row wins.
                var columns = Math.Max(1, parent.Layout.GridColumns);
                var row = n.ChildLayout.GridRow ?? index / columns;
                var col = n.ChildLayout.GridColumn ?? index % columns;
                if (col != 0) props.Add($"margin-left: {gap}");
                if (row > 0) props.Add($"margin-top: {gap}");

                if ((n.ChildLayout.GridRow.HasValue || n.ChildLayout.GridColumn.HasValue)
                    && _gridPlacementReported.Add(parent.Id))
                    _report.Add(Ref(parent), "layout.grid", "grid_column/grid_row", "margins",
                        ConversionOutcome.Approximated, IssueSeverity.Info, "layout.grid.placement",
                        "Explicit grid placement is emulated with margins and the 0-based grid_column/grid_row " +
                        "values of the document; column sizes come from the widths FigCraft resolved, and spans " +
                        "are not re-flowed when the frame is resized.");
                return;
            }

            if (parent.Layout.Wrap && horizontal)
            {
                if (!TryWrapLines(parent, out var lines))
                {
                    // The line breaks depend on flex-grow or on measured text: keep the column spacing and drop the
                    // row spacing, instead of pushing the whole first row down.
                    if (index > 0) props.Add($"margin-left: {gap}");
                    if (_wrapGapReported.Add(parent.Id))
                        _report.Add(Ref(parent), "layout.wrap", "wrap", "row spacing dropped",
                            ConversionOutcome.Approximated, IssueSeverity.Warning, "layout.wrap.gap.dropped",
                            "USS wrap has no row-gap before Unity 6.7 and the line breaks depend on flex-grow or " +
                            "measured text, so the row spacing cannot be emulated; only the column spacing is kept.");
                    return;
                }

                if (index > 0 && lines[index] == lines[index - 1]) props.Add($"margin-left: {gap}");
                if (lines[index] > 0) props.Add($"margin-top: {cross}");
                return;
            }

            if (index == 0) return;
            if (horizontal) props.Add($"margin-left: {gap}");
            else props.Add($"margin-top: {gap}");
        }

        /// <summary>Index of the child among the layout children (hidden and absolutely positioned layers don't count).</summary>
        int LayoutIndex(FigNode child, FigNode parent)
        {
            var index = 0;
            foreach (var id in parent.Children)
            {
                var sibling = _doc.Node(id);
                if (sibling == null || !sibling.Visible || sibling.ChildLayout.Absolute) continue;
                if (sibling.Id == child.Id) return index;
                index++;
            }
            return -1;
        }

        /// <summary>
        /// Replays the flex line breaking for a wrapping row: the margin goes on the first child of each new line
        /// (left) and on every line but the first (top). Returns false when the widths aren't knowable.
        /// </summary>
        bool TryWrapLines(FigNode parent, out List<int> lines)
        {
            lines = new List<int>();

            var children = new List<FigNode>();
            foreach (var id in parent.Children)
            {
                var child = _doc.Node(id);
                if (child == null || !child.Visible || child.ChildLayout.Absolute) continue;
                if (child.ChildLayout.SizingH == "fill") return false;
                children.Add(child);
            }

            var gap = GapPixels(parent);
            if (gap == null || children.Count == 0) return false;

            var available = parent.Width - parent.Layout.Padding[1] - parent.Layout.Padding[3];
            if (available <= 0) return false;

            var line = 0;
            var x = 0.0;
            foreach (var child in children)
            {
                if (x > 0 && x + gap.Value + child.Width > available + 0.5)
                {
                    line++;
                    x = 0;
                }
                lines.Add(line);
                x += (x > 0 ? gap.Value : 0) + child.Width;
            }
            return lines.Count == children.Count;
        }

        /// <summary>Numeric gap of a container: the literal or the bound token's value for the mode in scope.</summary>
        double? GapPixels(FigNode parent)
        {
            if (parent.Bound.TryGetValue("gap", out var id))
            {
                var variable = _doc.FindVariable(id);
                if (variable == null || variable.ResolvedType != "number") return null;
                var mode = parent.VarModes.TryGetValue(variable.CollectionId, out var m)
                    ? m
                    : _doc.FindCollection(variable.CollectionId)?.DefaultModeId;
                if (mode != null && variable.Values.TryGetValue(mode, out var value) && value.Type == "number")
                    return value.Number;
                return null;
            }
            return parent.Layout.Gap;
        }

        /// <summary>Which filters an element may carry when the shadow has its own element.</summary>
        enum FilterScope { All, ShadowsOnly, NoShadows }

        void ApplyElement(FigNode n, FigNode parent, bool isRoot, string cls, bool skipBackground = false,
            List<string> extra = null)
        {
            var props = BoxProps(n, parent, isRoot);

            if (ShadowGoesToUnderlay(n))
            {
                EmitShadowUnderlay(n, cls, skipBackground, wrapper: false);
                // The shape (background/border/filter) lives on the underlay; the container keeps the radius so
                // overflow: hidden still clips to the rounded box.
                AddRadius(n, props);
                AddLayout(n, props);
                // Blur and backdrop-filter still belong to the element that holds the content.
                AddEffects(n, props, FilterScope.NoShadows);
            }
            else
            {
                if (!skipBackground) AddBackground(n, props, cls);
                AddBorder(n, props);
                AddEffects(n, props);
                AddRadius(n, props);
                AddLayout(n, props);
            }

            if (extra != null) props.AddRange(extra);
            AddRule(cls, props);
        }

        /// <summary>
        /// A container shadow cannot live on the container: filter: drop-shadow() filters the element's whole
        /// rendered result, so text and icons inside would cast their own shadow.
        /// </summary>
        bool ShadowGoesToUnderlay(FigNode n)
        {
            if (!_opts.SupportsDropShadow) return false;
            if (n.Type == FigNodeType.Text || n.Type == FigNodeType.Instance) return false;
            return HasDropShadow(n) && HasVisibleChildren(n);
        }

        /// <summary>
        /// Clipped container: the underlay has to live outside the clip, so the node is wrapped in a layout-only
        /// element that keeps its slot in the parent, with the card filling it from the inside.
        /// </summary>
        void EmitShadowWrapper(FigNode n, FigNode parent, bool isRoot, string cls)
        {
            var wrapCls = cls + "-wrap";
            _xml.Open("ui:VisualElement", ("name", NameFor(n) + "_wrap"), ("class", wrapCls));

            var wrapProps = BoxProps(n, parent, isRoot).Where(p => p != "overflow: hidden").ToList();
            if (n.ChildLayout.SizingH == "hug")
            {
                wrapProps.Add($"width: {Num.Px(n.Width)}");
                _report.Add(Ref(n), "layout.hug", "hug width", "frozen at " + Num.Px(n.Width),
                    ConversionOutcome.Approximated, IssueSeverity.Info, "layout.hug.shadowWrapper",
                    "The shadow needs a wrapper whose only child is absolutely positioned, so a hug axis is frozen " +
                    "at the imported size and no longer grows with its content.");
            }
            if (n.ChildLayout.SizingV == "hug")
            {
                wrapProps.Add($"height: {Num.Px(n.Height)}");
                _report.Add(Ref(n), "layout.hug", "hug height", "frozen at " + Num.Px(n.Height),
                    ConversionOutcome.Approximated, IssueSeverity.Info, "layout.hug.shadowWrapper",
                    "The shadow needs a wrapper whose only child is absolutely positioned, so a hug axis is frozen " +
                    "at the imported size and no longer grows with its content.");
            }
            AddRule(wrapCls, wrapProps);

            EmitShadowUnderlay(n, cls, skipBackground: false, wrapper: true);

            _xml.Open("ui:VisualElement", ("name", NameFor(n)), ("class", Classes(n, cls)));

            var cardProps = new List<string>
            {
                "position: absolute", "left: 0", "top: 0", "right: 0", "bottom: 0",
            };
            // The shape (fill, border, radius) and the filter live on the underlay; painting them twice would
            // double the bake jobs and stack translucent fills.
            AddEffects(n, cardProps, FilterScope.NoShadows);
            AddRadius(n, cardProps);
            if (n.Clip) cardProps.Add("overflow: hidden");
            AddLayout(n, cardProps);
            AddRule(cls, cardProps);

            foreach (var childId in n.Children)
            {
                var child = _doc.Node(childId);
                if (child != null) Emit(child, n, false);
            }

            _xml.Close("ui:VisualElement");
            _xml.Close("ui:VisualElement");

            _report.Add(Ref(n), "effect.dropShadow", "clipped container", "wrapper + underlay",
                ConversionOutcome.Approximated, IssueSeverity.Info, "effect.dropShadow.wrapper",
                "The container clips its content, so the shadow is painted by a sibling underlay inside a wrapper " +
                "element: the node fills the wrapper and keeps the clipping, and the shadow is no longer clipped " +
                "nor applied to the children.");
        }

        bool HasDropShadow(FigNode n)
        {
            if (n.Effects != null && n.Effects.Any(e => e.Visible && e.Type == "dropShadow")) return true;
            var style = ActiveStyle(n, "effect");
            return style?.Effects != null && style.Effects.Any(e => e.Visible && e.Type == "dropShadow");
        }

        bool HasVisibleChildren(FigNode n)
        {
            foreach (var id in n.Children)
            {
                var child = _doc.Node(id);
                if (child != null && child.Visible) return true;
            }
            return false;
        }

        void EmitShadowUnderlay(FigNode n, string cls, bool skipBackground, bool wrapper)
        {
            var underlayCls = cls + "-shadow";

            // The underlay is a sibling of the card, so it does not inherit the card's theme classes: they have
            // to be repeated here or the token colours would resolve against the default mode.
            var classes = new List<string> { underlayCls };
            foreach (var pair in n.VarModes)
                if (_modeClassOf.TryGetValue(pair.Value, out var themeCls)) classes.Add(themeCls);
            foreach (var kind in new[] { "fill", "effect" })
            {
                var styleCls = ActiveStyleClass(n, kind);
                if (styleCls != null) classes.Add(styleCls);
            }

            var effectStyle = ActiveStyleClass(n, "effect");
            if (effectStyle != null)
                _report.Add(Ref(n), "effect", "effect style", effectStyle,
                    ConversionOutcome.Exact, IssueSeverity.Info, "effect.delegatedToStyle",
                    "The shadow comes from a FigCraft effect style, so the style class rides on this element and " +
                    "the filter is not repeated in the node rule: editing the style keeps working.");

            _xml.SelfClose("ui:VisualElement", ("name", NameFor(n) + "_shadow"),
                ("class", string.Join(" ", classes.Distinct())), ("picking-mode", "Ignore"));

            var props = new List<string> { "position: absolute", "left: 0", "top: 0", "right: 0", "bottom: 0" };
            if (!skipBackground) AddBackground(n, props, underlayCls);
            AddBorder(n, props);
            AddRadius(n, props);
            // A referenced effect style already carries the filter (the class is on this element).
            AddEffects(n, props, ActiveStyle(n, "effect") != null ? FilterScope.NoShadows : FilterScope.ShadowsOnly);
            AddRule(underlayCls, props);

            if (wrapper) return; // the wrapper reports the whole arrangement

            _report.Add(Ref(n), "effect.dropShadow", "drop shadow", underlayCls,
                ConversionOutcome.Approximated, IssueSeverity.Info, "effect.dropShadow.underlay",
                "The shadow is painted by an absolute underlay. filter: drop-shadow() applies to the whole subtree, " +
                "so keeping it on the container would also shadow the text and icons inside.");
        }

        void AddBackground(FigNode n, List<string> props, string cls)
        {
            if (n.Fills.Count == 0) return;

            if (n.Bound.TryGetValue("fill", out var varId) && VarName(varId) != null)
            {
                props.Add($"background-color: var({VarName(varId)})");
                if (n.StyleRefs.TryGetValue("fill", out var overridden) && !string.IsNullOrEmpty(overridden))
                    _report.Add(Ref(n), "fill", overridden, "variable wins", ConversionOutcome.Approximated,
                        IssueSeverity.Info, "style.overridden.byVariable",
                        "A bound variable takes precedence over the paint style in FigCraft, so the style class is informational.");
                return;
            }

            var style = ActiveStyle(n, "fill");

            if (style == null && n.Fills.Count(p => p.Visible) > 1)
            {
                _report.Add(Ref(n), "fills", n.Fills.Count(p => p.Visible) + " visible fills", "topmost only",
                    ConversionOutcome.Approximated, IssueSeverity.Warning, "fill.multiple.stacked",
                    "USS paints one background per element; extra fills need stacked elements or a baked texture.");
            }

            var fill = n.PrimaryFill;
            if (fill == null || !fill.Visible) return;
            if (style != null) return; // handled by the style class

            if (fill.IsSolid)
            {
                props.Add($"background-color: {fill.Color.ToUss()}");
            }
            else if (fill.IsGradient)
            {
                var grad = Gradient(fill, n);
                if (grad != null)
                {
                    props.Add($"background-image: {grad}");
                }
                else
                {
                    DegradeGradient(n, fill, props, cls);
                }
            }
            else if (fill.Type == "image")
            {
                props.Add($"background-image: url(\"{fill.ImageRef}\")");
                props.Add("-unity-background-scale-mode: scale-and-crop");
                _report.Add(Ref(n), "fill.image", fill.ImageRef, "background-image", ConversionOutcome.Degraded,
                    IssueSeverity.Info, "fill.image.referenced", "Image paints reference the source asset; import settings are not generated.");
            }

            if (fill.Opacity < 0.999)
                props.Add($"opacity: {Num.M(n.Opacity * fill.Opacity)}");
        }

        void DegradeGradient(FigNode n, FigPaint fill, List<string> props, string cls)
        {
            var rule = _opts.Rules.First(n, fill.Kind);
            var action = rule?.Action ?? (_opts.Bake ? "bake" : "drop");
            var kind = fill.Kind ?? "linear";
            var nativeKind = kind == "linear" || kind == "radial";

            if (action == "drop")
            {
                var why = !nativeKind
                    ? "UI Toolkit has no angular or diamond gradient at any version"
                    : $"USS gradients need Unity 6.7+ and the target is {_opts.UnityVersion}";
                _report.Add(Ref(n), "fill.gradient", kind, "dropped", ConversionOutcome.Dropped,
                    IssueSeverity.Error, "gradient." + kind + ".dropped",
                    why + "; the fill is omitted. Allow baking or add a custom rule/material for this project.",
                    new ReportFix { Kind = _opts.Bake ? "manual" : "bake" });
                return;
            }

            var file = _opts.BakeFolder + "/" + Slug(n.Name) + "-fill.png";
            props.Add($"background-image: url(\"{file}\")");
            props.Add("-unity-background-scale-mode: scale-and-crop");

            _report.Bakes.Add(new BakeJob
            {
                NodeId = n.Id,
                NodePath = _doc.PathOf(n),
                Reason = "gradient." + fill.Kind,
                Format = "png",
                OutputRelativePath = file,
                Scale = _opts.BakeScale,
            });

            var reason = !nativeKind
                ? $"UI Toolkit has no {kind} gradient at any version"
                : $"USS gradients need Unity 6.7+ and the target is {_opts.UnityVersion}";
            _report.Add(Ref(n), "fill.gradient", kind, file, ConversionOutcome.Degraded,
                IssueSeverity.Warning, "gradient." + kind + ".baked",
                reason + "; the fill is baked with FigCraft's renderer, so the colours are no longer editable in USS.",
                new ReportFix { Kind = "bake" });
        }

        void AddBorder(FigNode n, List<string> props)
        {
            if (n.Strokes.Count == 0 || n.Stroke == null) return;
            var paint = n.Strokes.FirstOrDefault(p => p.Visible);
            if (paint == null) return;

            if (paint.IsGradient)
            {
                _report.Add(Ref(n), "stroke", "gradient", "dropped", ConversionOutcome.Dropped,
                    IssueSeverity.Warning, "stroke.gradient.dropped",
                    "USS borders are a single colour; a gradient border needs a baked nine-slice or a second element.");
                return;
            }

            props.Add($"border-color: {paint.Color.ToUss()}");

            if (n.Stroke.Sides != null)
            {
                var sides = new[] { "top", "right", "bottom", "left" };
                for (var i = 0; i < 4; i++)
                    props.Add($"border-{sides[i]}-width: {Num.Px(n.Stroke.Sides[i] ? n.Stroke.Weight : 0)}");
            }
            else
            {
                props.Add($"border-width: {Num.Px(n.Stroke.Weight)}");
            }

            if (!string.Equals(n.Stroke.Align, "inside", StringComparison.OrdinalIgnoreCase))
            {
                _report.Add(Ref(n), "stroke.align", n.Stroke.Align, "border-width (inside)",
                    ConversionOutcome.Approximated, IssueSeverity.Info, "stroke.align.inside",
                    "USS borders are drawn inside the box; centre/outside strokes change the element's outer size by half the width.");
            }

            if (n.Stroke.HasDashes)
            {
                // A dashed border has no USS equivalent. When the node is only the stroke, the FigCraft renderer can
                // export it as an SVG and the dashes stay exact; a fill would need a second image, so it is reported.
                var hasFill = n.Fills.Any(p => p.Visible);
                var rule = _opts.Rules.First(n, null);
                if (_opts.Bake && !hasFill && rule?.Action != "drop")
                {
                    var file = BakePath(n, "stroke", "svg");
                    props.RemoveAll(p => p.StartsWith("border", StringComparison.Ordinal));
                    props.Add($"background-image: url(\"{file}\")");
                    props.Add("-unity-background-scale-mode: scale-to-fit");
                    _report.Add(Ref(n), "stroke.dashes", string.Join(",", n.Stroke.Dashes), file,
                        ConversionOutcome.Degraded, IssueSeverity.Info, "stroke.dashes.baked",
                        "USS has no dashed borders: the node is exported as SVG by FigCraft's renderer and used as " +
                        "a background, so the dashes are exact but no longer editable in USS.");
                }
                else
                {
                    _report.Add(Ref(n), "stroke.dashes", string.Join(",", n.Stroke.Dashes), "dropped",
                        ConversionOutcome.Dropped, IssueSeverity.Warning, "stroke.dashes.dropped",
                        hasFill
                            ? "USS has no dashed borders and the node also has a fill, so a single baked SVG would " +
                              "paint both; use a second element or a NineSlice/tiled image."
                            : "USS has no dashed borders; enable baking or use a NineSlice/tiled image.",
                        new ReportFix { Kind = "bake" });
                }
            }
        }

        void AddEffects(FigNode n, List<string> props, FilterScope scope = FilterScope.All)
        {
            if (n.Effects == null || n.Effects.Count == 0) return;

            // A referenced effect style already emits the filter once, in styles.uss. Emitting it again in the
            // node rule would shadow the style: the class would stop working the moment someone edits it.
            var styleShadow = ActiveStyle(n, "effect") != null;
            if (styleShadow && scope == FilterScope.All)
                _report.Add(Ref(n), "effect", n.Effects.Count + " effect(s)", "delegated to the style class",
                    ConversionOutcome.Exact, IssueSeverity.Info, "effect.delegatedToStyle",
                    "The effect comes from a FigCraft effect style and is emitted once, in styles.uss; " +
                    "the node rule does not repeat it, so editing the style keeps working.");

            AddEffects(Ref(n), n.Effects, props, styleShadow ? FilterScope.NoShadows : scope);
        }

        /// <summary>
        /// Effects are the least portable part of the model, so they are mapped per Unity version:
        ///  * 6.3 has no box-shadow at all and no drop-shadow filter: shadows are reported, never invented.
        ///  * 6.6+ can use filter: drop-shadow() (no spread, no inset).
        ///  * innerShadow and backgroundBlur have no USS equivalent at any version.
        /// Layer blur maps to filter: blur() from 6.3.
        /// </summary>
        void AddEffects(FigNodeRef node, List<FigEffect> effects, List<string> props,
            FilterScope scope = FilterScope.All)
        {
            if (effects == null || effects.Count == 0) return;

            var filters = new List<string>();
            var backdropFilters = new List<string>();

            foreach (var e in effects.Where(x => x.Visible))
            {
                switch (e.Type)
                {
                    case "dropShadow":
                        if (_opts.SupportsDropShadow)
                        {
                            var blur = e.Radius + Math.Abs(e.Spread);
                            if (scope != FilterScope.NoShadows)
                                filters.Add($"drop-shadow({Num.Px(e.Offset[0])} {Num.Px(e.Offset[1])} {Num.Px(blur)} {e.Color.ToUss()})");
                            if (Math.Abs(e.Spread) > 0.001)
                                _report.Add(node, "effect.spread", Num.M(e.Spread) + "px", "folded into the blur radius",
                                    ConversionOutcome.Approximated, IssueSeverity.Warning, "effect.dropShadow.spread.folded",
                                    "USS drop-shadow has no spread radius; the spread is added to the blur, so the shadow is softer than in FigCraft.");
                            else if (scope != FilterScope.NoShadows)
                                _report.Add(node, "effect.dropShadow", Num.M(e.Radius) + "px blur", "filter: drop-shadow()",
                                    ConversionOutcome.Exact, IssueSeverity.Info, "effect.dropShadow.filter",
                                    "Element shadow mapped to filter: drop-shadow().");
                        }
                        else
                        {
                            _report.Add(node, "effect.dropShadow",
                                $"{Num.M(e.Offset[0])} {Num.M(e.Offset[1])} {Num.M(e.Radius)} {e.Color.ToUss()}", "dropped",
                                ConversionOutcome.Dropped, IssueSeverity.Warning, "effect.dropShadow.needsUnity66",
                                $"Unity {_opts.UnityVersion} has neither box-shadow nor filter: drop-shadow() (added in 6.6), so the shadow is not emitted. " +
                                "Raise --unity-version to 6.6, bake the element (profile visual), or add a nine-sliced shadow element in the project.",
                                new ReportFix { Kind = "upgrade", Command = "--unity-version 6.6" });
                        }
                        break;

                    case "innerShadow":
                        _report.Add(node, "effect.innerShadow",
                            $"{Num.M(e.Offset[0])} {Num.M(e.Offset[1])} {Num.M(e.Radius)} {e.Color.ToUss()}", "dropped",
                            ConversionOutcome.Dropped, IssueSeverity.Warning, "effect.innerShadow.dropped",
                            "USS has no inset shadow, at any version; bake the element or draw it as a nine-sliced asset.",
                            new ReportFix { Kind = "bake" });
                        break;

                    case "blur":
                    case "layerBlur":
                        if (_opts.SupportsFilters && scope != FilterScope.ShadowsOnly)
                        {
                            filters.Add($"blur({Num.Px(e.Radius)})");
                            _report.Add(node, "effect.blur", Num.M(e.Radius) + "px", $"filter: blur({Num.Px(e.Radius)})",
                                ConversionOutcome.Approximated, IssueSeverity.Info, "effect.blur.filter",
                                "Layer blur mapped to filter: blur(). USS has no sigma control, so heavy blurs can differ slightly at the edges.");
                        }
                        break;

                    case "backgroundBlur":
                        if (_opts.SupportsBackdropFilters && scope != FilterScope.ShadowsOnly)
                        {
                            backdropFilters.Add($"blur({Num.Px(e.Radius)})");
                            _report.Add(node, "effect.backgroundBlur", Num.M(e.Radius) + "px",
                                $"backdrop-filter: blur({Num.Px(e.Radius)})", ConversionOutcome.Approximated,
                                IssueSeverity.Warning, "effect.backgroundBlur.backdropFilter",
                                "Backdrop blur maps to backdrop-filter: blur(). USS clamps the captured area to the element bounds, " +
                                "and Unity only renders it on URP screen-space panels; the built-in pipeline and HDRP ignore the property.");
                        }
                        else
                        {
                            _report.Add(node, "effect.backgroundBlur", Num.M(e.Radius) + "px", "dropped",
                                ConversionOutcome.Dropped, IssueSeverity.Warning, "effect.backgroundBlur.dropped",
                                $"backdrop-filter needs Unity 6.6+ and the target is {_opts.UnityVersion}; the blur is not emitted.",
                                new ReportFix { Kind = "upgrade", Command = "--unity-version 6.6" });
                        }
                        break;

                    default:
                        _report.Add(node, "effect", e.Type, "dropped", ConversionOutcome.Dropped, IssueSeverity.Info,
                            "effect.unknown.dropped", $"'{e.Type}' has no USS mapping.");
                        break;
                }

                if (e.Blend != "normal" && !string.IsNullOrEmpty(e.Blend))
                    _report.Add(node, "effect.blend", e.Blend, "dropped", ConversionOutcome.Dropped, IssueSeverity.Info,
                        "effect.blend.dropped", "USS filters have no blend mode.");
            }

            if (filters.Count > 0) props.Add("filter: " + string.Join(" ", filters));
            if (backdropFilters.Count > 0) props.Add("backdrop-filter: " + string.Join(" ", backdropFilters));
        }

        /// <summary>USS text-shadow takes exactly three lengths and a colour: no spread radius.</summary>
        static string ShadowValue(FigEffect e)
        {
            return $"{Num.Px(e.Offset[0])} {Num.Px(e.Offset[1])} {Num.Px(e.Radius)} {e.Color.ToUss()}";
        }

        void AddRadius(FigNode n, List<string> props)
        {
            if (n.Type == FigNodeType.Ellipse)
            {
                // USS border-radius takes lengths only (no percentages): a circle needs half the short side.
                var radius = Math.Min(n.Width, n.Height) / 2.0;
                if (radius > 0)
                {
                    props.Add($"border-radius: {Num.Px(radius)}");
                }
                else
                {
                    _report.Add(Ref(n), "cornerRadius", "ellipse", "dropped", ConversionOutcome.Dropped,
                        IssueSeverity.Warning, "ellipse.radius.unknown",
                        "The ellipse has no resolved size, so its border radius cannot be computed. Bind width/height or set a fixed size.");
                }
                return;
            }

            var r = n.CornerRadii;
            if (r == null || r.All(v => Math.Abs(v) < 0.001)) return;

            if (n.Bound.TryGetValue("cornerRadius", out var varId) && VarName(varId) != null)
            {
                props.Add($"border-radius: var({VarName(varId)})");
                return;
            }

            if (r.All(v => Math.Abs(v - r[0]) < 0.001))
                props.Add($"border-radius: {Num.Px(r[0])}");
            else
                props.Add($"border-radius: {Num.Px(r[0])} {Num.Px(r[1])} {Num.Px(r[2])} {Num.Px(r[3])}");
        }

        void AddLayout(FigNode n, List<string> props)
        {
            if (n.Type == FigNodeType.Text || !n.Layout.IsAutoLayout) return;

            var gap = GapToken(n);
            var crossGap = n.Layout.CrossGap > 0 ? Num.Px(n.Layout.CrossGap) : gap;

            if (n.Layout.IsGrid)
            {
                props.Add("flex-direction: row");
                props.Add("flex-wrap: wrap");
                _report.Add(Ref(n), "layout", "grid", "flex-wrap row", ConversionOutcome.Approximated,
                    IssueSeverity.Warning, "layout.grid.degraded",
                    "UI Toolkit has no grid; columns become a wrapping row, so track sizes/alignment are lost.");
            }
            else
            {
                props.Add($"flex-direction: {(n.Layout.Mode == "horizontal" ? "row" : "column")}");
                if (n.Layout.Wrap) props.Add("flex-wrap: wrap");
            }

            var wraps = n.Layout.IsGrid || n.Layout.Wrap;

            if (gap != null)
            {
                if (_opts.SupportsFlexGap)
                {
                    if (n.Layout.Mode == "horizontal" || wraps) props.Add($"column-gap: {gap}");
                    if (n.Layout.Mode == "vertical" || wraps)
                    {
                        // A FigCraft grid has a single gap; crossGap only means something for a wrapping layout.
                        var rowGap = n.Layout.IsGrid ? gap : (wraps ? crossGap ?? gap : gap);
                        props.Add($"row-gap: {rowGap}");
                    }
                }
                else
                {
                    // row-gap/column-gap only exist from 6.7: on 6.3 the spacing moves to the children.
                    _gapValueOf[n.Id] = gap;
                    _crossGapValueOf[n.Id] = wraps ? crossGap : null;
                    if (_gapEmulated.Add(n.Id))
                        _report.Add(Ref(n), "layout.gap", gap, "child margins", ConversionOutcome.Approximated,
                            IssueSeverity.Info, "layout.gap.margins",
                            $"USS gap needs Unity 6.7+ and the target is {_opts.UnityVersion}; the spacing is emulated with margins on the children, " +
                            "which distributes differently under justify-content/space-between.");
                }
            }

            props.Add($"justify-content: {MainAlign(n.Layout.MainAlign)}");
            props.Add($"align-items: {CrossAlign(n.Layout.CrossAlign)}");

            var pad = n.Layout.Padding;
            if (pad != null && pad.Any(v => Math.Abs(v) > 0.001))
            {
                props.Add(pad.All(v => Math.Abs(v - pad[0]) < 0.001)
                    ? $"padding: {Num.Px(pad[0])}"
                    : $"padding: {Num.Px(pad[0])} {Num.Px(pad[1])} {Num.Px(pad[2])} {Num.Px(pad[3])}");
            }

            if (n.Layout.ReverseZ)
                _report.Add(Ref(n), "layout.reverseZ", "true", "dropped", ConversionOutcome.Dropped,
                    IssueSeverity.Info, "layout.reverseZ.dropped",
                    "USS has no reverse stacking switch; reorder the children in UXML if the visual order matters.");
        }

        /// <summary>Gap as a USS length, honouring a bound gap token (which also wins over the literal, as in FigCraft).</summary>
        string GapToken(FigNode n)
        {
            if (n.Bound.TryGetValue("gap", out var gapId) && VarName(gapId) != null)
                return $"var({VarName(gapId)})";
            return n.Layout.Gap > 0 ? Num.Px(n.Layout.Gap) : null;
        }

        bool IsFirstLayoutChild(FigNode child, FigNode parent) => LayoutIndex(child, parent) == 0;

        internal static string AssetUrl(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return assetPath;
            if (assetPath.StartsWith("project://") || assetPath.StartsWith("http"))
                return assetPath;
            var p = assetPath.Replace('\\', '/');
            return p.StartsWith("Assets/") ? "project://database/" + p : p;
        }

        static string MainAlign(string value) => (value ?? "min").ToLowerInvariant() switch
        {
            "center" => "center",
            "max" => "flex-end",
            "spacebetween" => "space-between",
            "spacearound" => "space-around",
            // USS has no space-evenly; space-around is the closest distribution it can express.
            "spaceevenly" => "space-around",
            _ => "flex-start",
        };

        static string CrossAlign(string value) => (value ?? "min").ToLowerInvariant() switch
        {
            "center" => "center",
            "max" => "flex-end",
            "baseline" => "baseline",
            "stretch" => "stretch",
            _ => "flex-start",
        };

        /// <summary>Base class + theme classes + style classes + project rule classes.</summary>
        string Classes(FigNode n, string baseCls)
        {
            var classes = new List<string> { baseCls };

            foreach (var pair in n.VarModes)
                if (_modeClassOf.TryGetValue(pair.Value, out var themeCls))
                    classes.Add(themeCls);

            foreach (var kind in new[] { "fill", "text" })
            {
                var styleCls = ActiveStyleClass(n, kind);
                if (styleCls != null) classes.Add(styleCls);
            }

            // The effect style rides on the shadow element when there is one, so the filter is not inherited by
            // the children through the container.
            if (!ShadowGoesToUnderlay(n))
            {
                var effectCls = ActiveStyleClass(n, "effect");
                if (effectCls != null) classes.Add(effectCls);
            }

            var rule = _opts.Rules.First(n, null);
            if (!string.IsNullOrEmpty(rule?.UssClass))
                classes.AddRange(rule.UssClass.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));

            return string.Join(" ", classes.Distinct());
        }

        // ============================================================== helpers

        string ClassFor(FigNode n)
        {
            if (_classOf.TryGetValue(n.Id, out var existing)) return existing;
            var baseName = "fc-" + Slug(n.Name);
            var name = baseName;
            var i = 2;
            while (!_usedClasses.Add(name))
                name = baseName + "-" + i++;
            _classOf[n.Id] = name;
            return name;
        }

        string NameFor(FigNode n)
        {
            if (_nameOf.TryGetValue(n.Id, out var existing)) return existing;
            var raw = n.Name ?? "node";
            var sb = new StringBuilder();
            foreach (var c in raw)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            var name = sb.ToString().Trim('_');
            if (name.Length == 0) name = "node";
            if (char.IsDigit(name[0])) name = "n" + name;
            var candidate = name;
            var i = 2;
            while (!_usedNames.Add(candidate))
                candidate = name + "_" + i++;
            _nameOf[n.Id] = candidate;
            return candidate;
        }

        FigNodeRef Ref(FigNode n) => new FigNodeRef(n.Id, n.Name, _doc.PathOf(n));

        void AddRule(string selector, List<string> props) => AppendBlock(_nodeUss, selector, props);

        /// <summary>Writes a USS rule block, guaranteeing one declaration per line and a trailing ';'.</summary>
        static void AppendBlock(StringBuilder sb, string selector, List<string> props)
        {
            if (props == null || props.Count == 0) return;
            sb.AppendLine($".{selector} {{");
            foreach (var p in props.Distinct())
                sb.AppendLine("    " + p.TrimEnd().TrimEnd(';') + ";");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        string Gradient(FigPaint fill, FigNode n)
        {
            // USS gradients only exist from 6.7. On 6.3 a gradient has to be baked, keeping the colours intact.
            if (!_opts.SupportsUssGradients) return null;

            var stops = string.Join(", ", fill.Stops
                .OrderBy(s => s.Position)
                .Select(s => $"{s.Color.ToUss()} {Num.M(s.Position * 100)}%"));
            if (string.IsNullOrEmpty(stops)) return null;

            switch ((fill.Kind ?? "linear").ToLowerInvariant())
            {
                case "linear":
                {
                    var angle = LinearAngle(fill, n);
                    return $"linear-gradient({Num.M(angle)}deg, {stops})";
                }
                case "radial":
                {
                    var cx = fill.From?[0] ?? 0.5;
                    var cy = fill.From?[1] ?? 0.5;
                    return $"radial-gradient(circle at {Num.M(cx * 100)}% {Num.M(cy * 100)}%, {stops})";
                }
                default:
                    return null; // angular / diamond
            }
        }

        static double LinearAngle(FigPaint fill, FigNode n)
        {
            var from = fill.From ?? new double[] { 0, 0 };
            var to = fill.To ?? new double[] { 1, 0 };
            var w = n?.Width > 0 ? n.Width : 1;
            var h = n?.Height > 0 ? n.Height : 1;
            var dx = (to[0] - from[0]) * w;
            var dy = (to[1] - from[1]) * h;
            // CSS/USS: 0deg points up, angles grow clockwise.
            var angle = Math.Atan2(dx, -dy) * 180.0 / Math.PI;
            if (angle < 0) angle += 360;
            return angle;
        }

        /// <summary>
        /// USS has no line-height, text-transform or text-decoration, but UI Toolkit's rich text does
        /// (&lt;line-height&gt;, &lt;uppercase&gt;/&lt;lowercase&gt;, &lt;u&gt;/&lt;s&gt;). Literal text that contains
        /// tag characters is wrapped in &lt;noparse&gt; so it can never be parsed as markup.
        /// </summary>
        (string Text, bool UsesTags) RichText(FigNode n, string text)
        {
            text = text ?? "";
            var t = n.TextStyle;
            if (t == null) return (text, false);

            var tags = new List<(string Name, string Value)>();

            var lineHeight = LineHeightPixels(t);
            if (lineHeight != null)
            {
                tags.Add(("line-height", Num.Px(lineHeight.Value)));
                _report.Add(Ref(n), "text.lineHeight", $"{Num.M(lineHeight.Value)}px", "<line-height> rich text",
                    ConversionOutcome.Approximated, IssueSeverity.Info, "text.lineHeight.richText",
                    "USS has no line-height, so it is applied with the <line-height> rich text tag. The element " +
                    "height stays the one imported from FigCraft.");
            }

            var textCase = (t.TextCase ?? "original").ToLowerInvariant();
            var caseTag = textCase == "upper" || textCase == "uppercase" ? "uppercase"
                : textCase == "lower" || textCase == "lowercase" ? "lowercase"
                : textCase == "smallcaps" || textCase == "small-caps" ? "smallcaps"
                : null;

            if (caseTag != null)
            {
                tags.Add((caseTag, null));
                _report.Add(Ref(n), "text.case", t.TextCase, $"<{caseTag}> rich text",
                    ConversionOutcome.Approximated, IssueSeverity.Info, "text.case.richText",
                    "USS has no text-transform, so the case is applied with a rich text tag: the label keeps the " +
                    "authored characters (a dynamic value is re-cased by Unity too) but label.text returns the tag.");
            }
            else if (textCase == "title" || textCase == "titlecase" || textCase == "capitalize")
            {
                // Rich text has no title-case tag, so the string itself is capitalized.
                text = CaseText(text, textCase);
                _report.Add(Ref(n), "text.case", t.TextCase, "capitalized in the string",
                    ConversionOutcome.Approximated, IssueSeverity.Info, "text.case.applied",
                    "Rich text has no title-case tag, so the string is capitalized. A value assigned from code " +
                    "will keep its own case; re-apply it there if it is dynamic.");
            }

            var decoration = (t.Decoration ?? "").ToLowerInvariant();
            var underline = decoration.Contains("underline");
            var strikethrough = decoration.Contains("strike") || decoration.Contains("line-through");
            if (underline) tags.Add(("u", null));
            if (strikethrough) tags.Add(("s", null));
            if (underline || strikethrough)
                _report.Add(Ref(n), "text.decoration", t.Decoration, "<u>/<s> rich text",
                    ConversionOutcome.Approximated, IssueSeverity.Info, "text.decoration.richText",
                    "USS has no underline or strikethrough, so the decoration is applied with the <u>/<s> rich " +
                    "text tags; label.text returns the tags.");

            var hasMarkupChars = text.IndexOf('<') >= 0 || text.IndexOf('>') >= 0 || text.IndexOf('&') >= 0;
            if (tags.Count == 0 && !hasMarkupChars) return (text, false);

            // <noparse> keeps literal markup literal; without it a label showing "a < b" would lose the text
            // after the '<'. enable-rich-text is emitted whenever the label carries tags of any kind.
            var body = hasMarkupChars ? $"<noparse>{text}</noparse>" : text;
            foreach (var (name, value) in tags)
                body = value == null ? $"<{name}>{body}</{name}>" : $"<{name}={value}>{body}</{name}>";
            return (body, true);
        }

        static double? LineHeightPixels(FigTextStyle t)
        {
            var unit = (t.LineHeightUnit ?? "auto").ToLowerInvariant().Replace("%", "percent");
            if (t.LineHeightValue <= 0) return null;
            if (unit == "px") return t.LineHeightValue;
            if (unit == "percent") return t.FontSize * t.LineHeightValue / 100.0;
            return null;
        }

        /// <summary>USS has no text-transform, so the case is applied to the string itself.</summary>
        static string CaseText(string text, string textCase)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            switch ((textCase ?? "original").ToLowerInvariant())
            {
                case "upper":
                case "uppercase":
                    return text.ToUpperInvariant();
                case "lower":
                case "lowercase":
                    return text.ToLowerInvariant();
                case "title":
                case "titlecase":
                case "capitalize":
                    var sb = new StringBuilder(text.Length);
                    var startOfWord = true;
                    foreach (var c in text)
                    {
                        sb.Append(startOfWord ? char.ToUpperInvariant(c) : c);
                        startOfWord = !char.IsLetterOrDigit(c);
                    }
                    return sb.ToString();
                default:
                    return text;
            }
        }

        static string Slug(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "item";
            var sb = new StringBuilder();
            var lastDash = false;
            foreach (var c in value.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(c);
                    lastDash = false;
                }
                else if (!lastDash)
                {
                    sb.Append('-');
                    lastDash = true;
                }
            }
            var s = sb.ToString().Trim('-');
            return s.Length == 0 ? "item" : s;
        }
    }

    /// <summary>Minimal indented XML writer that can also patch the class list of the open element.</summary>
    internal sealed class Xml
    {
        readonly StringBuilder _sb = new StringBuilder();
        readonly List<(string tag, int line)> _open = new List<(string, int)>();
        int _depth;
        public override string ToString() => _sb.ToString();

        public Xml Raw(string line)
        {
            _sb.AppendLine(line);
            return this;
        }

        public void Open(string tag, params (string, string)[] attrs)
        {
            var line = _sb.Length;
            _sb.Append(new string(' ', _depth * 4)).Append('<').Append(tag);
            foreach (var (k, v) in attrs)
                _sb.Append(' ').Append(k).Append("=\"").Append(Esc(v)).Append('"');
            _sb.AppendLine(">");
            _open.Add((tag, line));
            _depth++;
        }

        public void SelfClose(string tag, params (string, string)[] attrs)
        {
            _sb.Append(new string(' ', _depth * 4)).Append('<').Append(tag);
            foreach (var (k, v) in attrs)
                _sb.Append(' ').Append(k).Append("=\"").Append(Esc(v)).Append('"');
            _sb.AppendLine(" />");
        }

        public void Close(string tag)
        {
            _depth = Math.Max(0, _depth - 1);
            _sb.Append(new string(' ', _depth * 4)).Append("</").Append(tag).AppendLine(">");
            if (_open.Count > 0) _open.RemoveAt(_open.Count - 1);
        }

        /// <summary>Appends a class to the element opened last, rewriting its class attribute in place.</summary>
        public void AddClassToLast(string cls)
        {
            if (_open.Count == 0 || string.IsNullOrEmpty(cls)) return;
            var (_, line) = _open[_open.Count - 1];
            var text = _sb.ToString(line, _sb.Length - line);
            var marker = " class=\"";
            var idx = text.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return;
            var start = idx + marker.Length;
            var end = text.IndexOf('"', start);
            if (end < 0) return;
            var current = text.Substring(start, end - start);
            if (current.Split(' ').Contains(cls)) return;
            _sb.Remove(line + start, end - start);
            _sb.Insert(line + start, current + " " + cls);
        }

        static string Esc(string v)
        {
            if (v == null) return "";
            return v.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }
    }
}
