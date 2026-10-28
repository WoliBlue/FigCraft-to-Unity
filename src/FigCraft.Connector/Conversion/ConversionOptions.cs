using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FigCraft.Connector.Model;
using Newtonsoft.Json.Linq;

namespace FigCraft.Connector.Conversion
{
    /// <summary>What the converter is allowed to do when a property has no exact USS equivalent.</summary>
    public static class Profiles
    {
        public const string Strict = "strict";         // exact only; anything else is an error
        public const string Production = "production"; // approximate/degrade, but always report
        public const string Visual = "visual";         // prefer pixel fidelity (bake) over editable USS

        public static bool IsValid(string profile) =>
            profile == Strict || profile == Production || profile == Visual;
    }

    public sealed class RuleMatch
    {
        public string Name;        // wildcard: Button/*, *Icon*
        public string Component;   // wildcard on the component name (instances/components)
        public string Type;        // frame | text | vector | ...
        public string Gradient;    // linear|radial|angular|diamond, '|' separated
        public string Tag;         // "#bake" in the layer name
        public bool? HasTextOutline;
        public bool? HasShadow;

        public bool Matches(FigNode node, string gradient)
        {
            if (!string.IsNullOrEmpty(Type) &&
                !string.Equals(Type, node.RawType, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrEmpty(Name) && !Wildcard(Name, node.Name))
                return false;

            if (!string.IsNullOrEmpty(Component))
            {
                var name = node.MainComponentName ?? node.Name;
                if (!Wildcard(Component, name)) return false;
            }

            if (!string.IsNullOrEmpty(Gradient))
            {
                if (gradient == null) return false;
                var kinds = Gradient.Split('|');
                if (!kinds.Any(k => string.Equals(k.Trim(), gradient, StringComparison.OrdinalIgnoreCase)))
                    return false;
            }

            if (!string.IsNullOrEmpty(Tag))
            {
                if (node.Name == null || node.Name.IndexOf("#" + Tag, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }

            if (HasTextOutline.HasValue && HasTextOutline.Value == (node.Stroke == null || node.Strokes.Count == 0))
                return false;
            if (HasShadow.HasValue && HasShadow.Value == (node.Effects == null || node.Effects.Count == 0))
                return false;

            return true;
        }

        static bool Wildcard(string pattern, string value)
        {
            if (value == null) return false;
            var rx = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(value, rx, RegexOptions.IgnoreCase);
        }
    }

    public sealed class ConversionRule
    {
        public string Id;
        public int Priority;
        public RuleMatch Match = new RuleMatch();

        /// <summary>null | map | bake | drop | exact | approximate</summary>
        public string Action;

        /// <summary>UI Toolkit control for a FigCraft component/instance: Button, Toggle, TextField, Slider…</summary>
        public string Control;

        /// <summary>Component property that supplies the control's text.</summary>
        public string TextProperty;

        /// <summary>Extra USS class(es) added to the element.</summary>
        public string UssClass;

        public string Note;
    }

    public sealed class RuleSet
    {
        public string Profile;
        public int FontPadding = 9;

        /// <summary>What a mapped TextField does with the FigCraft placeholder: "value" (default) or "empty".</summary>
        public string TextFieldPlaceholder = "value";

        public readonly List<ConversionRule> Rules = new List<ConversionRule>();

        public static readonly RuleSet Empty = new RuleSet();

        public ConversionRule First(FigNode node, string gradient = null)
        {
            return Rules
                .Where(r => r.Match.Matches(node, gradient))
                .OrderByDescending(r => r.Priority)
                .FirstOrDefault();
        }

        public ConversionRule ControlRule(FigNode node)
        {
            return Rules
                .Where(r => r.Control != null && r.Match.Matches(node, null))
                .OrderByDescending(r => r.Priority)
                .FirstOrDefault();
        }

        public static RuleSet Load(string path)
        {
            var set = new RuleSet();
            if (string.IsNullOrEmpty(path)) return set;
            var root = JObject.Parse(File.ReadAllText(path));
            set.Profile = root.Value<string>("profile");
            if (root["fontPadding"] != null) set.FontPadding = root.Value<int>("fontPadding");
            if (root["textfieldPlaceholder"] != null) set.TextFieldPlaceholder = root.Value<string>("textfieldPlaceholder");

            if (root["rules"] is JArray rules)
            {
                foreach (var r in rules.OfType<JObject>())
                {
                    var rule = new ConversionRule
                    {
                        Id = r.Value<string>("id") ?? Guid.NewGuid().ToString("N").Substring(0, 8),
                        Priority = r["priority"] != null ? r.Value<int>("priority") : 0,
                        Action = r.Value<string>("action"),
                        Control = r.Value<string>("control"),
                        TextProperty = r.Value<string>("textProperty"),
                        UssClass = r.Value<string>("ussClass"),
                        Note = r.Value<string>("note"),
                    };

                    if (r["match"] is JObject m)
                    {
                        rule.Match = new RuleMatch
                        {
                            Name = m.Value<string>("name"),
                            Component = m.Value<string>("component"),
                            Type = m.Value<string>("type"),
                            Gradient = m.Value<string>("gradient"),
                            Tag = m.Value<string>("tag"),
                            HasTextOutline = m["hasTextOutline"]?.Value<bool>(),
                            HasShadow = m["hasShadow"]?.Value<bool>(),
                        };
                    }

                    set.Rules.Add(rule);
                }
            }

            return set;
        }
    }

    public sealed class ConversionOptions
    {
        /// <summary>Absolute folder the generated .uxml/.uss/report.json go to.</summary>
        public string OutputDirectory = ".";

        /// <summary>Path of OutputDirectory inside the Unity project, used for report metadata only.</summary>
        public string ProjectPath = "";

        public string Profile = Profiles.Production;
        public RuleSet Rules = RuleSet.Empty;

        /// <summary>"Inter|Bold" → "Assets/Fonts/Inter-Bold SDF.asset".</summary>
        public readonly Dictionary<string, string> Fonts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Unity version the generated USS has to run on. The project baseline is 6000.6.0f1 (6.6);
        /// 6.3 is accepted as a minimum. Fidelity features are gated per version: USS gradients arrive in 6.7,
        /// filter: drop-shadow() and backdrop-filter in 6.6, USS filters in 6.3. row-gap/column-gap need 6.7.</summary>
        public int UnityMajor = 6;
        public int UnityMinor = 6;

        /// <summary>Full editor label when the user gave one (e.g. 6000.6.0f1); informational only.</summary>
        public string UnityEditorVersion;

        public string UnityVersion => UnityMajor + "." + UnityMinor;

        /// <summary>true from 6.7: linear-gradient()/radial-gradient() in background-image.</summary>
        public bool SupportsUssGradients => UnityMajor > 6 || UnityMinor >= 7;

        /// <summary>true from 6.6: filter: drop-shadow().</summary>
        public bool SupportsDropShadow => UnityMajor > 6 || UnityMinor >= 6;

        public bool SupportsBackdropFilters => UnityMajor > 6 || UnityMinor >= 6;

        /// <summary>true from 6.3: filter: blur() and the other built-in filters.</summary>
        public bool SupportsFilters => UnityMajor > 6 || UnityMinor >= 3;

        /// <summary>true from 6.7: row-gap/column-gap. Before that, gaps are emulated with child margins.</summary>
        public bool SupportsFlexGap => UnityMajor > 6 || UnityMinor >= 7;

        public bool Bake = true;
        public string FigCraftCli = "figcraft-cli";
        public double BakeScale = 2;
        public string BakeFolder = "baked";

        public bool EmulateShadowWithOutline = true;
        public int TextOutlinePaddingLimit = 9;

        public string DefaultTheme = "";

        public static Dictionary<string, string> LoadFonts(string path)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return map;

            var root = JObject.Parse(File.ReadAllText(path));
            foreach (var family in root.Properties())
            {
                if (family.Value is JObject faces)
                {
                    foreach (var face in faces.Properties())
                        map[$"{family.Name}|{face.Name}"] = face.Value.Value<string>();
                }
                else if (family.Value.Type == JTokenType.String)
                {
                    map[family.Name] = family.Value.Value<string>();
                }
            }
            return map;
        }
    }
}
