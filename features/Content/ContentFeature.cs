namespace FigCraft.Features.Content
{
    public sealed class ContentFeature : IFeature
    {
        public string Name => "content";

        public string Summary => "puts real strings and images into layers to catch truncation";

        public string Usage => "content --manifest <content.json> --figcraft <file> --out <file> [options]";

        public string Help =>
@"Puts real content into the design: the longest translation, empty strings, real prices, real
avatars. Text uses node.set characters; images are placed inside the target layer, covering it.

  --manifest <file>      content manifest (see features/README.md)
  --figcraft <file>      document to update
  --out <file>           document to write
  --patch <file>         also write the generated run script
  --dry-run              read and print the plan, change nothing
  --cli <path>           figcraft-cli executable (default: figcraft-cli)

Overflow is a warning: the width of the new string is estimated from the layer's current width and
character count. Run without --dry-run and look at the result before trusting it.";

        public int Run(Cli args) => ContentCommand.Run(ContentOptions.Parse(args));
    }
}
