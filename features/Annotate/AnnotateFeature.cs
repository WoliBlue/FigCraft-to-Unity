namespace FigCraft.Features.Annotate
{
    public sealed class AnnotateFeature : IFeature
    {
        public string Name => "annotate";

        public string Summary => "writes report.json into the document as annotations";

        public string Usage => "annotate --report <report.json> --figcraft <file> --out <file> [options]";

        public string Help =>
@"Writes the import report into the FigCraft document as annotations, so designers see what the
import could not express on the layer it affects. Annotations owned by this tool carry a label
prefix and are replaced on every run.

  --report <file>        report.json written next to the generated .uxml
  --figcraft <file>      .figcraft (or document.json) to read ids from
  --out <file>           document to write
  --patch <file>         also write the generated run script
  --dry-run              read and plan, change nothing
  --severity <list>      comma separated (default: warning,error)
  --prefix <text>        ownership marker (default: [Unity])
  --cli <path>           figcraft-cli executable (default: figcraft-cli)";

        public int Run(Cli args) => AnnotateCommand.Run(AnnotateOptions.Parse(args));
    }
}
