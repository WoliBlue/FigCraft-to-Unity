# FigCraft to Unity Tool (Unity package)

Editor-only. Nothing from this package ends up in your builds.

| Menu | What it does |
|---|---|
| **Tools › FigCraft › Capture Game Screen** (Ctrl+Shift+Alt+F) | Captures the UI on screen, in Play Mode, into a capture folder |
| **Tools › FigCraft › Capture Selection Only** (Ctrl+Shift+Alt+G) | Captures only the panel selected in the Hierarchy, without the 3D world or the rest of the UI |
| **Tools › FigCraft › Design Changes…** | Lists what was edited in FigCraft and applies it to the game, one change at a time |
| **Tools › FigCraft › Set Up Automatic Import…** | Picks the FigCraft document that receives every capture (and `figcraft-cli`, if it is not in its usual place) |
| **Tools › FigCraft › Open Master Document in FigCraft** | Opens the document that receives the captures |
| **Tools › FigCraft › Choose / Open Capture Folder** | Where captures are written (outside `Assets`) |

With the Input System package installed, Ctrl+Shift+Alt+F also works while the Game view has focus
in Play Mode (Unity does not pass menu shortcuts through there).

Everything runs inside the Editor: you only need the FigCraft app installed. The capture code is in
`Editor/Core`; the command-line tool in the root of the repository builds from those same files.
