using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace FigCraft.Connector.Model
{
    /// <summary>RGBA colour in 0..1 space, the way FigCraft stores it.</summary>
    public readonly struct FigColor
    {
        public readonly double R, G, B, A;

        public FigColor(double r, double g, double b, double a = 1)
        {
            R = Clamp(r); G = Clamp(g); B = Clamp(b); A = Clamp(a);
        }

        static double Clamp(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        public static readonly FigColor Transparent = new FigColor(0, 0, 0, 0);
        public static readonly FigColor Black = new FigColor(0, 0, 0, 1);

        public bool IsTransparent => A <= 0.0001;

        public string ToHex()
        {
            var r = (int)Math.Round(R * 255);
            var g = (int)Math.Round(G * 255);
            var b = (int)Math.Round(B * 255);
            if (A >= 0.9999)
                return $"#{r:X2}{g:X2}{b:X2}";
            var a = (int)Math.Round(A * 255);
            return $"#{r:X2}{g:X2}{b:X2}{a:X2}";
        }

        /// <summary>USS-safe colour: hex when opaque, rgba() when translucent.</summary>
        public string ToUss()
        {
            if (A >= 0.9999) return ToHex();
            var r = (int)Math.Round(R * 255);
            var g = (int)Math.Round(G * 255);
            var b = (int)Math.Round(B * 255);
            return $"rgba({r}, {g}, {b}, {Num.M(A)})";
        }

        public override string ToString() => ToUss();
    }

    public static class Num
    {
        /// <summary>Invariant, max 3 decimals, no trailing zeroes. 12 → "12", 12.5 → "12.5".</summary>
        public static string M(double v)
        {
            if (Math.Abs(v) < 0.0005) return "0";
            var s = v.ToString("0.###", CultureInfo.InvariantCulture);
            return s == "-0" ? "0" : s;
        }

        public static string Px(double v) => Math.Abs(v) < 0.0005 ? "0" : M(v) + "px";
    }

    public enum FigNodeType
    {
        Unknown, Page, Frame, Group, Rectangle, Ellipse, Polygon, Star, Line, Arrow,
        Vector, Text, Component, Instance, Section, Slice, Boolean
    }

    public sealed class FigPaint
    {
        public string Type = "solid";          // solid | gradient | image
        public string Kind;                    // linear | radial | angular | diamond
        public FigColor Color = FigColor.Black;
        public double Opacity = 1;
        public bool Visible = true;
        public string Blend = "normal";
        public List<FigGradientStop> Stops = new List<FigGradientStop>();
        public double[] From;
        public double[] To;
        public double[] Width;
        public string ImageRef;

        public bool IsGradient => Type == "gradient";
        public bool IsSolid => Type == "solid";
    }

    public sealed class FigGradientStop
    {
        public double Position;
        public FigColor Color;
    }

    public sealed class FigEffect
    {
        public string Type = "dropShadow";      // dropShadow | innerShadow | blur | layerBlur | backgroundBlur
        public FigColor Color = FigColor.Black;
        public double[] Offset = new double[] { 0, 0 };
        public double Radius;
        public double Spread;
        public bool Visible = true;
        public string Blend = "normal";
        public bool ShowBehind;
    }

    public sealed class FigStroke
    {
        public double Weight = 1;
        public string Align = "center";         // inside | center | outside
        public bool[] Sides;                    // top, right, bottom, left (null = all)
        public string CapStart, CapEnd, Join;
        public double MiterLimit = 4;
        public List<double> Dashes = new List<double>();
        public bool HasDashes => Dashes != null && Dashes.Count > 0;
    }

    public sealed class FigLayout
    {
        public string Mode = "none";            // none | horizontal | vertical | grid
        public bool Wrap;
        public double Gap = 0;
        public double CrossGap = 10;
        public double[] Padding = new double[] { 0, 0, 0, 0 }; // top, right, bottom, left
        public string MainAlign = "min";
        public string CrossAlign = "min";
        public string ContentAlign = "auto";
        public int GridColumns = 2;
        public int GridRows = 2;
        public bool ReverseZ;

        public bool IsAutoLayout => Mode == "horizontal" || Mode == "vertical" || Mode == "grid";
        public bool IsGrid => Mode == "grid";
    }

    public sealed class FigChildLayout
    {
        public string SizingH = "fixed";        // fixed | hug | fill
        public string SizingV = "fixed";
        public bool Absolute;
        public double? MinW, MaxW, MinH, MaxH;
        public int? GridColumn, GridRow, GridColumnSpan, GridRowSpan;
    }

    public sealed class FigTextRun
    {
        public int Start;
        public int End;
        public string StyleId;
    }

    public sealed class FigTextStyle
    {
        public string FontFamily = "Inter";
        public string FontStyle = "Regular";
        public double FontSize = 14;
        public string LineHeightUnit = "auto";  // auto | px | percent
        public double LineHeightValue;
        public string LetterSpacingUnit = "percent";
        public double LetterSpacingValue;
        public string Decoration = "none";
        public string TextCase = "original";
        public string Align = "left";
        public string VerticalAlign = "top";
        public string AutoResize = "widthAndHeight";
        public string StyleId;
        public bool FillIsBound;
        public List<FigTextRun> Runs = new List<FigTextRun>();

        public bool IsBold => FontStyle.IndexOf("Bold", StringComparison.OrdinalIgnoreCase) >= 0
                              || FontStyle.IndexOf("Semibold", StringComparison.OrdinalIgnoreCase) >= 0
                              || FontStyle.IndexOf("Black", StringComparison.OrdinalIgnoreCase) >= 0
                              || FontStyle.IndexOf("Heavy", StringComparison.OrdinalIgnoreCase) >= 0;
        public bool IsItalic => FontStyle.IndexOf("Italic", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public sealed class FigVectorVertex
    {
        public double X, Y;
        public double Radius;
    }

    public sealed class FigVectorSegment
    {
        public int A, B;
        public double[] TA, TB;
        public bool IsLine => (TA == null || (TA[0] == 0 && TA[1] == 0)) && (TB == null || (TB[0] == 0 && TB[1] == 0));
    }

    public sealed class FigVectorNetwork
    {
        public List<FigVectorVertex> Vertices = new List<FigVectorVertex>();
        public List<FigVectorSegment> Segments = new List<FigVectorSegment>();
        public List<List<int>> Loops = new List<List<int>>();
    }

    public sealed class FigNode
    {
        public int Id;
        public string Name = "";
        public FigNodeType Type = FigNodeType.Unknown;
        public string RawType = "";
        public int? Parent;
        public List<int> Children = new List<int>();

        // Local transform: [a, b, c, d, tx, ty]
        public double[] Transform = new double[] { 1, 0, 0, 1, 0, 0 };
        public double Width, Height;
        public double Opacity = 1;
        public bool Visible = true;
        public string Blend = "normal";
        public bool Clip;

        public List<FigPaint> Fills = new List<FigPaint>();
        public List<FigPaint> Strokes = new List<FigPaint>();
        public FigStroke Stroke;
        public List<FigEffect> Effects = new List<FigEffect>();
        public double[] CornerRadii = new double[] { 0, 0, 0, 0 };
        public double CornerSmoothing;

        public FigLayout Layout = new FigLayout();
        public FigChildLayout ChildLayout = new FigChildLayout();

        public string Characters;
        public FigTextStyle TextStyle;
        public FigVectorNetwork Network;

        public Dictionary<string, string> StyleRefs = new Dictionary<string, string>();
        public Dictionary<string, string> Bound = new Dictionary<string, string>();
        public Dictionary<string, string> VarModes = new Dictionary<string, string>();

        public int? MainComponent;
        public string MainComponentName;
        public Dictionary<string, string> ComponentProps = new Dictionary<string, string>();
        public Dictionary<string, string> PropRefs = new Dictionary<string, string>();
        public int? ComponentSet;

        public double X => Transform.Length >= 6 ? Transform[4] : 0;
        public double Y => Transform.Length >= 6 ? Transform[5] : 0;
        public double RotationDegrees
        {
            get
            {
                if (Transform.Length < 6) return 0;
                var a = Transform[0];
                var b = Transform[1];
                if (Math.Abs(b) < 1e-6 && Math.Abs(a - 1) < 1e-6) return 0;
                return Math.Atan2(b, a) * 180.0 / Math.PI;
            }
        }

        public bool HasMultipleFills => Fills.Count(p => p.Visible) > 1;

        public FigPaint PrimaryFill
        {
            get
            {
                var f = Fills.FirstOrDefault(p => p.Visible);
                return f;
            }
        }
    }

    public sealed class FigStyle
    {
        public string Id;
        public string Name = "";
        public string Description = "";
        public string Type;                     // paint | text | effect | grid
        public List<FigPaint> Paints;
        public FigTextStyle TextStyle;
        public List<FigEffect> Effects;
    }

    public sealed class FigVariableMode
    {
        public string Id;
        public string Name = "";
    }

    public sealed class FigVariableCollection
    {
        public string Id;
        public string Name = "";
        public List<FigVariableMode> Modes = new List<FigVariableMode>();

        public string DefaultModeId => Modes.Count > 0 ? Modes[0].Id : null;
    }

    public sealed class FigVariable
    {
        public string Id;
        public string Name = "";
        public string CollectionId;
        public string ResolvedType = "string";  // color | number | string | boolean
        public string Description = "";
        public Dictionary<string, FigVariableValue> Values = new Dictionary<string, FigVariableValue>();
    }

    public sealed class FigVariableValue
    {
        public string Type;
        public FigColor Color;
        public double Number;
        public string Text;
        public bool Bool;
        public string AliasOf;
    }

    public sealed class FigDocument
    {
        public string Name = "";
        public int FormatVersion = 1;
        public int CurrentPage;
        public List<int> Pages = new List<int>();
        public Dictionary<int, FigNode> Nodes = new Dictionary<int, FigNode>();
        public List<FigStyle> Styles = new List<FigStyle>();
        public List<FigVariableCollection> Collections = new List<FigVariableCollection>();
        public List<FigVariable> Variables = new List<FigVariable>();
        public string SourcePath = "";

        public IEnumerable<FigNode> TopLevel(int pageId)
        {
            if (!Nodes.TryGetValue(pageId, out var page)) yield break;
            foreach (var id in page.Children)
                if (Nodes.TryGetValue(id, out var n)) yield return n;
        }

        public FigNode Node(int id) => Nodes.TryGetValue(id, out var n) ? n : null;

        public FigVariable FindVariable(string idOrName)
        {
            if (string.IsNullOrEmpty(idOrName)) return null;
            return Variables.FirstOrDefault(v => v.Id == idOrName)
                   ?? Variables.FirstOrDefault(v => v.Name == idOrName);
        }

        public FigStyle FindStyle(string idOrName)
        {
            if (string.IsNullOrEmpty(idOrName)) return null;
            return Styles.FirstOrDefault(s => s.Id == idOrName)
                   ?? Styles.FirstOrDefault(s => s.Name == idOrName);
        }

        public FigVariableCollection FindCollection(string idOrName)
        {
            if (string.IsNullOrEmpty(idOrName)) return null;
            return Collections.FirstOrDefault(c => c.Id == idOrName)
                   ?? Collections.FirstOrDefault(c => c.Name == idOrName);
        }

        /// <summary>Path of names from the page down to the node, e.g. "Screen/Login/Title".</summary>
        public string PathOf(FigNode node)
        {
            var parts = new List<string>();
            var cur = node;
            var guard = 0;
            while (cur != null && guard++ < 256)
            {
                parts.Insert(0, cur.Name);
                if (!cur.Parent.HasValue) break;
                cur = Node(cur.Parent.Value);
            }
            return string.Join("/", parts);
        }
    }
}
