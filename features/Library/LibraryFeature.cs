namespace FigCraft.Features.Library
{
    public sealed class LibraryFeature : IFeature
    {
        public string Name => "library";

        public string Summary => "builds a component library from controls rendered by Unity";

        public string Usage => "library --manifest <library.json> --figcraft <file> --out <file> [options]";

        public string Help =>
@"Builds a FigCraft component library from a manifest of controls rendered by Unity.

  --manifest <file>      library manifest (see features/README.md)
  --figcraft <file>      document to update
  --out <file>           document to write
  --dry-run              read and print the plan, change nothing
  --replace              delete components with the same name before rebuilding them
  --cli <path>           figcraft-cli executable (default: figcraft-cli)";

        public int Run(Cli args) => LibraryCommand.Run(LibraryOptions.Parse(args));
    }
}
