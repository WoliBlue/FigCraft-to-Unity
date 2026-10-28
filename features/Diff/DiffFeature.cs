namespace FigCraft.Features.Diff
{
    public sealed class DiffFeature : IFeature
    {
        public string Name => "diff";

        public string Summary => "shows what changed between two saved versions of a document";

        public string Usage => "diff --before <old.figcraft> --after <new.figcraft> [options]";

        public string Help =>
@"Compares two saved versions of the same document and lists the layers that are new, removed or
edited (text, size, name, parent, visibility, opacity, fill, radius, layout).

  --before <file>        the older document
  --after <file>         the newer document
  --out <file>           write a report: .md for people, .json for scripts
  --annotate <file>      write the newer document with the changed layers annotated
  --patch <file>         also write the generated annotation script
  --prefix <text>        ownership marker (default: [Unity diff])
  --dry-run              plan the annotations, change nothing
  --cli <path>           figcraft-cli executable (default: figcraft-cli)

Layers are matched by id first and by full path second, so a re-import that renumbers ids still
lines up. Removed layers cannot be annotated (they no longer exist); they stay in the report.";

        public int Run(Cli args) => DiffCommand.Run(DiffOptions.Parse(args));
    }
}
