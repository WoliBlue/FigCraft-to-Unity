using System;
using System.Collections.Generic;
using System.Linq;

namespace FigCraft.Features.Host
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            IReadOnlyList<IFeature> features;
            try
            {
                features = FeatureCatalog.Discover(AppContext.BaseDirectory, m => Console.Error.WriteLine("warning: " + m));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: cannot load features: " + ex.Message);
                return 1;
            }

            if (args.Length == 0 || args[0] == "--help" || args[0] == "-h" || (args[0] == "help" && args.Length == 1))
            {
                PrintHelp(features);
                return 0;
            }

            var command = args[0];
            if (command == "help" || command == "--help")
            {
                var wanted = features.FirstOrDefault(f => f.Name == args[1]);
                if (wanted == null)
                {
                    Console.Error.WriteLine($"unknown command '{args[1]}'.");
                    PrintHelp(features);
                    return 2;
                }

                PrintFeature(wanted);
                return 0;
            }

            var feature = features.FirstOrDefault(f => f.Name == command);
            if (feature == null)
            {
                Console.Error.WriteLine($"unknown command '{command}'.");
                PrintHelp(features);
                return 2;
            }

            if (args.Length > 1 && (args[1] == "--help" || args[1] == "-h"))
            {
                PrintFeature(feature);
                return 0;
            }

            try
            {
                return feature.Run(Cli.Parse(args.Skip(1).ToArray()));
            }
            catch (FigCraftException ex)
            {
                Console.Error.WriteLine("figcraft: " + ex.Message);
                return 1;
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine($"usage: {feature.Usage}");
                Console.Error.WriteLine("  " + ex.Message);
                return 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return 1;
            }
        }

        static void PrintHelp(IReadOnlyList<IFeature> features)
        {
            Console.WriteLine("figcraft-unity-features - Unity -> FigCraft extras (separate from the converter core)");
            Console.WriteLine();
            Console.WriteLine("USAGE");
            if (features.Count == 0)
            {
                Console.WriteLine("  (no features found)");
            }
            else
            {
                foreach (var feature in features)
                    Console.WriteLine("  " + feature.Usage);
                Console.WriteLine("  figcraft-unity-features help <command>");
            }

            Console.WriteLine();
            Console.WriteLine("FEATURES");
            if (features.Count == 0)
            {
                Console.WriteLine("  None loaded. Build a feature project so its assembly sits next to this executable.");
            }
            else
            {
                var width = features.Max(f => f.Name.Length);
                foreach (var feature in features)
                    Console.WriteLine($"  {feature.Name.PadRight(width)}  {feature.Summary}");
            }

            Console.WriteLine();
            Console.WriteLine(@"NOTES
  Reads .figcraft directly (zip + document.json); only writes need figcraft-cli.
  Every figcraft-cli run is atomic: a failing step rolls back the whole script.
  Features are discovered from FigCraft.Features.*.dll next to the executable: each feature is its
  own project, and this package does not reference FigCraft.Connector.

EXIT CODES
  0 ok | 1 error or rejected by FigCraft | 2 usage");
        }

        static void PrintFeature(IFeature feature)
        {
            Console.WriteLine($"{feature.Name} - {feature.Summary}");
            Console.WriteLine();
            Console.WriteLine("USAGE");
            Console.WriteLine("  " + feature.Usage);
            Console.WriteLine();
            Console.WriteLine(feature.Help);
        }
    }
}
