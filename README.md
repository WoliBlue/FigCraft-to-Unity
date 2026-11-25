# FigCraft to Unity Tool

Unofficial. Not affiliated with FigCraft, Unity Technologies or Figma. The idea comes from Unity's
*UI Toolkit Connector for Figma design*; this is an independent tool for
[FigCraft](https://figcraft.app).

A Unity Editor package and two command-line tools that move UI between a running Unity game and
FigCraft:

- **Game → FigCraft.** In Play Mode, capture the screen. Every visible uGUI element becomes a
  FigCraft layer (frames, images, real text) that remembers the GameObject it came from.
- **FigCraft → game.** After editing the capture in FigCraft, a Unity window lists the changes and
  applies them one by one: position, size, text, font size, alignment, colour and image.
- **FigCraft → UI Toolkit** (command line only). Converts a FigCraft frame to `.uxml` + `.uss`, with
  tokens, themes and a report of every property that could not be converted exactly.

The capture starts from what the player sees at runtime (instantiated panels, texts filled in by
code), not from the prefabs on disk.

## Status

`0.1.0-preview.1`. Built while developing one Unity 6.6 game (uGUI + TextMeshPro). Expect rough
edges and breaking changes before 1.0.

| Piece | State |
|---|---|
| Capture uGUI screens in Play Mode | Works, round trip tested on one project |
| Apply FigCraft edits back to the game | Works for position/size, text, font size, alignment, colour, image |
| World Space canvases | Implemented, not tested on a real project yet |
| FigCraft → UI Toolkit converter | Command line only, with the known issues below |
| Converter window inside Unity | Not started |

## Requirements

- Unity 6000.6 (the only version tested), with uGUI and TextMeshPro.
- The [FigCraft](https://figcraft.app) desktop app, which installs `figcraft-cli`.

The package needs no .NET SDK and no separate executable. Its only extra dependency is
`com.unity.nuget.newtonsoft-json`, which the Package Manager installs.

## Install

1. In Unity, open **Window › Package Manager**, press **+** and choose
   **Install package from git URL…**
2. Paste:
   ```
   https://github.com/WoliBlue/figcraft-to-unity.git?path=/unity/com.figcraft-to-unity.tool
   ```
   To pin this version, add `#v0.1.0-preview.1` at the end.
3. Run **Tools › FigCraft › Set Up Automatic Import…** and pick the FigCraft document that will
   receive the captures. If `figcraft-cli` is not in its usual install folder, the tool asks for it.

## Use

**Capture a screen**

1. Close the document in FigCraft (the tool writes to the file; saving an older open copy would
   overwrite the capture).
2. Set the Game view to 1920×1080 and enter Play Mode.
3. Press **Ctrl+Shift+Alt+F** (*Tools › FigCraft › Capture Game Screen*).
4. Open the document in FigCraft: the screen is on the page **New captures**.

**Ctrl+Shift+Alt+G** (*Capture Selection Only*) captures only the panel selected in the Hierarchy and
its children.

**Apply the changes**

1. Edit the capture in FigCraft and save. Do not rename capture layers or move them to another
   parent: each one stores the path of its GameObject.
2. Leave Play Mode and open **Tools › FigCraft › Design Changes…**
3. Review each change and press **Apply**. Changes go through Undo; the scene is not saved for you.

The window warns when applying is not enough: a Layout Group on the parent will move the element
back, a script probably overwrites the text at runtime, the text uses auto-size, or the object is
created at runtime (the change goes to its prefab, or the window names the scripts that build it).

Each capture folder also contains `figcraft-report.md`, which lists what could not be reproduced.

## Command line

Building the command-line tools needs the [.NET 10 SDK](https://dotnet.microsoft.com/). They use the
same capture code as the Unity package (`unity/com.figcraft-to-unity.tool/Editor/Core`).

Convert the sample frame to UI Toolkit:

```
dotnet run --project src/FigCraft.Connector.Cli -- convert --input samples/shop.figcraft --frame "Screen/Login" --out Generated --rules samples/rules/production.rules.json --no-bake
```

This writes `screen-login.uxml`, `screen-login.uss`, `tokens.uss`, `themes.uss`, `styles.uss` and
`screen-login.report.json`. Every property that is not exact is reported as `approximated`,
`degraded` or `dropped`. Profiles (`--profile strict|production|visual`) and a rules file decide what
gets approximated, baked or dropped. Baking gradients and vectors to images calls `figcraft-cli`;
`--no-bake` skips it.

The capture and the other extras (`annotate`, `library`, `content`, `diff`) are in `features/`
([details](features/README.md)):

```
dotnet run --project features/Host -- capture --snapshot <capture>/snapshot.json --figcraft doc.figcraft --out doc.figcraft
dotnet run --project features/Host -- capture --changes changes.json --figcraft doc.figcraft --captures <folder>
```

Both tools print every option with `--help`.

## Known limitations

- Not applied from FigCraft (listed in the window only): new or deleted layers, font changes,
  rotated elements and the position of a World Space canvas itself.
- Converter: file names with accents break the `.uss` references in the generated `.uxml`.
- Converter: font maps must point to a `.ttf` font or a TextCore `FontAsset`, not a TextMeshPro
  font asset.
- Converter: image fills are referenced by hash and not extracted.

## Development

```
dotnet run --project tests/FigCraft.Connector.Tests
dotnet run --project features/tests/FigCraft.Features.Tests
```

The tests are console programs without a test framework; they return a non-zero exit code when a
check fails.

## License

[MIT](LICENSE) © 2026 Jose Arenas Venegas
