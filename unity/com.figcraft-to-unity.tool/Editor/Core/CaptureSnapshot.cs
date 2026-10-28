using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace FigCraft.Features.Capture
{
    /// <summary>
    /// Contract with the Unity side (Assets/Editor/FigCraft/FigCraftCapture.cs in the game). Coordinates are
    /// absolute in the capture frame (top-left origin, frameWidth wide); nodes come in pre-order, so a parent
    /// always precedes its children.
    /// </summary>
    public sealed class CaptureSnapshot
    {
        public int Version;
        public string Name;
        public string Scene;
        public string CapturedAt;
        public bool PlayMode;
        public int ScreenWidth;
        public int ScreenHeight;
        public double FrameWidth;
        public double FrameHeight;
        public string Screenshot;
        public string World;
        public List<CaptureNode> Nodes = new List<CaptureNode>();
        public List<string> Notes = new List<string>();

        [JsonIgnore] public string Folder;

        public static CaptureSnapshot Load(string path)
        {
            var full = Path.GetFullPath(path);
            var snapshot = JsonConvert.DeserializeObject<CaptureSnapshot>(File.ReadAllText(full))
                           ?? throw new InvalidDataException($"{path} is not a capture snapshot");
            snapshot.Folder = Path.GetDirectoryName(full);
            if (snapshot.Version != 1)
                throw new InvalidDataException($"snapshot version {snapshot.Version} is not supported (expected 1)");
            if (string.IsNullOrWhiteSpace(snapshot.Name)) snapshot.Name = Path.GetFileName(snapshot.Folder);

            for (var i = 0; i < snapshot.Nodes.Count; i++)
            {
                var node = snapshot.Nodes[i];
                if (node.Id != i) throw new InvalidDataException($"node {i} has id {node.Id}; ids must be the list index");
                if (node.Parent >= i) throw new InvalidDataException($"node {i} comes before its parent {node.Parent}");
                if (node.Parent >= 0) snapshot.Nodes[node.Parent].Children.Add(node);
            }
            return snapshot;
        }

        /// <summary>Absolute path of a file referenced by the snapshot, or null when it is missing.</summary>
        public string FileOf(string relative)
        {
            if (string.IsNullOrEmpty(relative)) return null;
            var root = Path.GetFullPath(Folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                       Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(Folder, relative));
            // Only files inside the capture folder: a hand-edited snapshot.json cannot pull in other files.
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            return File.Exists(path) ? path : null;
        }

        public IEnumerable<CaptureNode> Roots => Nodes.Where(n => n.Parent < 0);
    }

    public sealed class Vec2
    {
        public double X, Y;
    }

    public sealed class CaptureNode
    {
        public int Id;
        public int Parent = -1;
        public string Name;
        public string Path;
        public string Kind = "container";
        public string Canvas;
        public string RenderMode;
        public double X, Y, W, H;
        public double Rotation;
        public double Opacity = 1;
        public double GraphicOpacity = 1;
        public bool Visible = true;
        public bool Clip;
        public string Image;
        public string Color;
        public string ImageType;
        public double FillAmount = 1;
        public string Text;
        public string FontFamily;
        public string FontStyle;
        public double FontSize;
        public string Align;
        public string Valign;
        public string TextCase;
        public bool Wrap;
        public double OutlineWidth;
        public string OutlineColor;
        public string[] Components = Array.Empty<string>();
        public Vec2 AnchorMin;   // null in captures made before anchors were recorded
        public Vec2 AnchorMax;
        public List<string> Notes = new List<string>();

        [JsonIgnore] public List<CaptureNode> Children = new List<CaptureNode>();

        public bool HasGraphic => Kind == "image" || Kind == "rect" || Kind == "text";
    }
}
