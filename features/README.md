# Features (`figcraft-unity-features`)

Extras that work alongside the converter. Each feature is its own project in this folder; the host
(`Host/`) finds them by reflection, so adding a feature never touches the host or the converter in
`src/`.

| Command | What it does |
|---|---|
| `capture` | Snapshot of the running game (written by the Unity package) → editable FigCraft frame. With `--changes`: lists the edits made in FigCraft since the capture |
| `annotate` | Writes a converter report into the FigCraft document as annotations |
| `library` | Builds a FigCraft component library from images of Unity controls |
| `content` | Puts real strings and images into layers to catch truncation early |
| `diff` | Lists what changed between two saved versions of a document |

`figcraft-unity-features help <command>` shows every option.

## Adding a feature

1. Create `features/MyFeature/` with a `.csproj` that references `../Shared/FigCraft.Features.csproj`
   and sets `AssemblyName` to `FigCraft.Features.MyFeature`.
2. Add a public class with a parameterless constructor that implements `IFeature`.
3. Reference the project from `tests/FigCraft.Features.Tests` (a test checks that every feature
   project is discovered).
4. `dotnet build features/Host/FigCraft.Features.Host.csproj`: the command shows up in `--help`.

Writing to FigCraft documents needs `figcraft-cli`; reading them does not.
