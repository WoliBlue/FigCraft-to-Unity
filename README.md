# FigCraft to Unity Tool

> **Unofficial.** Not affiliated with FigCraft, Unity Technologies or Figma. Inspired by Unity's
> *UI Toolkit Connector for Figma design*, built independently for [FigCraft](https://figcraft.app).
>

Move UI between your **running game** and **FigCraft** instead of rebuilding it by hand.

- **Game → FigCraft.** Capture any screen while the game is playing. Every visible UI element
  becomes an editable FigCraft layer: frames, images, real text.
- **FigCraft → game.** Redesign in FigCraft, then review the edits in a Unity window and apply them
  one by one: position, size, text, font size, alignment, colour, image.
- **FigCraft → UI Toolkit.** Turn a FigCraft frame into editable `.uxml` + `.uss`, with design
  tokens, themes and a report of everything that could not be expressed exactly.

## Why

Most game UI is not a static design: panels are instantiated, texts are filled in by code,
elements are toggled at runtime. Design tools only see the mock-up and prefabs only show half of
the picture. These tools start from **what the player actually sees**, and keep a link from every
layer back to its GameObject so design changes can land in the game safely.

## Status: early preview

Built and tested while developing a Unity 6.6 game (uGUI + TextMeshPro). Expect rough edges and
breaking changes before 1.0.

| Piece | State |
|---|---|
| Capture uGUI screens in Play Mode → FigCraft | Works; round trip verified |
| Apply FigCraft edits back to the game | Works: position/size, text, font size, alignment, colour, image |
| FigCraft → UI Toolkit converter (command line) | Works, with known issues |
| Everything running inside Unity (install from a git URL, nothing to build) | Works |
| Converter window in Unity | Not yet |

## Requirements

- Unity **6000.6** (the only version tested so far), uGUI and TextMeshPro.
- [FigCraft](https://figcraft.app) desktop app, which installs `figcraft-cli`.

Nothing else: no .NET SDK, no separate executable. The package depends on Unity's own
`com.unity.nuget.newtonsoft-json`, which the Package Manager installs for you.

## Install

1. In Unity open **Window › Package Manager**, press **+** and choose
   **Install package from git URL…**
2. Paste this URL and press **Install**:
   ```
   https://github.com/WoliBlue/figcraft-to-unity.git?path=/unity/com.figcraft-to-unity.tool
   ```
   Add `#v0.1.0` at the end to pin a release.
3. **Tools › FigCraft › Set Up Automatic Import…** Choose the FigCraft document that will receive
   your captures. If `figcraft-cli` is not in the usual install folder, the tool asks for it once.

To update, press **Update** in the Package Manager (or change the `#version` in the URL).

## Use

**Capture a screen**

1. Close the document in FigCraft. The tool writes to the file; saving from an old open copy
   would overwrite the new capture.
2. Set the Game view to 1920×1080 and enter Play Mode.
3. Reach the screen you want and press **Ctrl+Shift+Alt+F** (*Tools › FigCraft › Capture Game
   Screen*).
4. Open the document in FigCraft. The screen is on the page **New captures**.

To redesign a single piece, select its panel in the Hierarchy and press **Ctrl+Shift+Alt+G**
(*Tools › FigCraft › Capture Selection Only*): you get that panel and its children, without the 3D
world or the rest of the UI.

**Apply design changes**

1. Edit the capture in FigCraft and save. Do not rename capture layers or move them to another
   parent: each one stores the path of its GameObject.
2. Leave Play Mode and open **Tools › FigCraft › Design Changes…**
3. Review each change (*Game* → *FigCraft*) and press **Apply**. Undo works; the scene is never
   saved for you.

The window tells you when applying is not enough:

- a Layout Group on the parent will move the element back;
- a script references that text and probably overwrites it at runtime;
- the text uses auto-size, so the maximum size is changed instead;
- the object is created at runtime, so the change goes to its prefab;
- the object is built by a script at runtime (no prefab): the window names the scripts on its
  nearest saved ancestor, so you know where to change it.

Swapping a picture in FigCraft is a change too: the new image is copied out of the document,
imported as a Sprite with the settings of the old one and assigned to the Image.

New or deleted layers, font changes, rotated elements and the position of a World Space canvas
itself are listed but not applied. Elements inside a World Space canvas (health bars, speech
bubbles) are applied, converted with the scale they had on screen.

## How the capture works

- It reads the live hierarchy in the frame you capture, not the assets on disk.
- Images are baked with their final look: tint, 9-slice, fill amount, preserve aspect, rotation
  and sprite padding. Texts stay texts, with the size TextMeshPro actually resolved.
- Unity anchors become FigCraft constraints, so resizing a panel in FigCraft moves its children the
  way the game would. Moving or resizing a parent counts as one change, not one per child.
- Each frame gets the 3D world without UI as a locked background, plus a hidden screenshot of the
  real screen for comparison.
- Nothing is dropped silently: what cannot be reproduced is listed in `figcraft-report.md` next to
  each capture.
- Screen Space Overlay and Camera canvases are supported. World Space canvases are projected to
  the screen (implemented, not verified on a real project yet).

## Command line

The Unity package is all you need. The same code also runs from the command line, for scripts and
CI; building it needs the [.NET 10 SDK](https://dotnet.microsoft.com/):

```
dotnet build features/Host/FigCraft.Features.Host.csproj -c Release
figcraft-unity-features capture --snapshot <capture>/snapshot.json --figcraft doc.figcraft --out doc.figcraft
figcraft-unity-features capture --changes changes.json --figcraft doc.figcraft --captures <folder>
figcraft-unity convert --input design.figcraft --frame <name> --out Assets/UI/Generated
```

The capture code lives in `unity/com.figcraft-to-unity.tool/Editor/Core` and the .NET projects link
those same files, so there is one copy of it.

`figcraft-unity` (in `src/`) converts FigCraft frames to UI Toolkit. Every property that cannot be
expressed exactly is reported as `approximated`, `degraded` or `dropped`, with a rule and a fix.
Profiles (`strict`, `production`, `visual`) and per-project rules decide what to approximate, bake
or drop. Run either tool with `--help` for every option. Other extras live in
[`features/`](features/README.md).

## Known issues

- Converter: file names with accents break the `.uss` references in the generated `.uxml`.
- Converter: font maps must point to a `.ttf` font or a TextCore `FontAsset`, not a TextMeshPro font asset.
- Converter: image fills are referenced by hash and not extracted.

## Development

```
dotnet run --project tests/FigCraft.Connector.Tests
dotnet run --project features/tests/FigCraft.Features.Tests
```

## License

[MIT](LICENSE) © 2026 Jose Arenas Venegas
