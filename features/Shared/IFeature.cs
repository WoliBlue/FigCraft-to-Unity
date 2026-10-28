using System;

namespace FigCraft.Features
{
    /// <summary>
    /// Contract for an optional feature. The host finds implementations by reflection, so a feature
    /// is a project that references this assembly: dropping a folder in <c>features/</c> is enough
    /// to add a command. Nothing in <c>src/</c> knows about it.
    /// </summary>
    public interface IFeature
    {
        /// <summary>Command name, as typed on the command line.</summary>
        string Name { get; }

        /// <summary>One line shown in the command list.</summary>
        string Summary { get; }

        /// <summary>Single-line synopsis shown next to the name in <c>--help</c>.</summary>
        string Usage { get; }

        /// <summary>Full help block, shown by <c>help &lt;name&gt;</c>.</summary>
        string Help { get; }

        /// <summary>Runs the feature. Return a process exit code (0 ok, 1 error, 2 usage).</summary>
        int Run(Cli args);
    }
}
