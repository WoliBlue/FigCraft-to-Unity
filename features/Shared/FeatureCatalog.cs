using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace FigCraft.Features
{
    /// <summary>
    /// Finds features without a hardcoded list: every <c>FigCraft.Features.*.dll</c> in the given
    /// directory is loaded and scanned for <see cref="IFeature"/> implementations.
    /// </summary>
    public static class FeatureCatalog
    {
        const string Skipped = "FigCraft.Features";

        public static IReadOnlyList<IFeature> Discover(string directory, Action<string> warn = null)
        {
            if (directory == null) throw new ArgumentNullException(nameof(directory));
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
            warn = warn ?? (_ => { });

            var found = new Dictionary<string, IFeature>(StringComparer.Ordinal);
            var files = Directory.GetFiles(directory, Skipped + ".*.dll").OrderBy(p => p, StringComparer.Ordinal);

            foreach (var path in files)
            {
                var fileName = Path.GetFileNameWithoutExtension(path);
                if (fileName == Skipped || fileName == Skipped + ".Host") continue;

                Assembly assembly;
                try
                {
                    assembly = Assembly.LoadFrom(path);
                }
                catch (Exception ex)
                {
                    warn($"skipping {Path.GetFileName(path)}: {ex.Message}");
                    continue;
                }

                foreach (var type in assembly.GetTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
                {
                    if (type.IsAbstract || !typeof(IFeature).IsAssignableFrom(type)) continue;
                    if (type.GetConstructor(Type.EmptyTypes) == null) continue;

                    IFeature feature;
                    try
                    {
                        feature = (IFeature)Activator.CreateInstance(type);
                    }
                    catch (Exception ex)
                    {
                        warn($"skipping {type.FullName}: {ex.Message}");
                        continue;
                    }

                    if (found.TryGetValue(feature.Name, out var existing))
                    {
                        // The same assembly can be loaded twice (default and LoadFrom contexts).
                        // Only a different type claiming the same command name is a real conflict.
                        if (existing.GetType().FullName != type.FullName)
                            warn($"'{feature.Name}' from {type.FullName} is already provided by {existing.GetType().FullName}; ignoring it.");
                        continue;
                    }

                    found[feature.Name] = feature;
                }
            }

            return found.Values.OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
        }
    }
}
