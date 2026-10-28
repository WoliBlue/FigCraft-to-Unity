namespace FigCraft.Features.Capture
{
    public sealed class CaptureFeature : IFeature
    {
        public string Name => "capture";

        public string Summary => "turns a capture of the running game (uGUI) into editable FigCraft layers";

        public string Usage => "capture --snapshot <snapshot.json> --out <file> [--figcraft <file>] [options] | capture --changes <out.json> --figcraft <file> --captures <dir>";

        public string Help =>
@"Turns a snapshot written by the Unity capture tool (Tools > FigCraft > Capture Game Screen)
into one frame of editable layers: frames for GameObjects, image layers for baked sprites, text
layers for TMP/Text. Every layer keeps its Unity path in pluginData (unity.path) so changes can be
traced back to the game.

  --snapshot <file>      snapshot.json from the capture folder
  --out <file>           document to write
  --figcraft <file>      document to add the frame to (default: a new document)
  --page <name>          page for the captures (default: Game)
  --replace              delete frames with the same name on that page first
  --no-reference         do not add the full screenshot as a hidden reference layer
  --dry-run              print the plan, change nothing
  --cli <path>           figcraft-cli executable (default: figcraft-cli)

The way back (FigCraft -> game):

  --changes <out.json>   compare --figcraft (the master document) with the captures in --captures and
                         write the list of edits (position/size, text, font size, alignment, colour) for
                         the Unity window (Tools > FigCraft > Design Changes). Changes nothing.
  --captures <dir>       folder with the capture folders (each one with a snapshot.json)";

        public int Run(Cli args) => args.Has("changes")
            ? ChangesCommand.Run(ChangesOptions.Parse(args))
            : CaptureCommand.Run(CaptureOptions.Parse(args));
    }
}
