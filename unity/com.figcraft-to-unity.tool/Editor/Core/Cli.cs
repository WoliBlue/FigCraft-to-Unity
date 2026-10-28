using System;
using System.Collections.Generic;
using System.Linq;

namespace FigCraft.Features
{
    /// <summary>Parses --key value, --key=value and --flag arguments.</summary>
    public sealed class Cli
    {
        readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static Cli Parse(string[] args)
        {
            var cli = new Cli();
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (!arg.StartsWith("--", StringComparison.Ordinal)) continue;

                var key = arg.Substring(2);
                var eq = key.IndexOf('=');
                if (eq >= 0)
                {
                    cli._values[key.Substring(0, eq)] = key.Substring(eq + 1);
                    continue;
                }

                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    cli._values[key] = args[++i];
                else
                    cli._flags.Add(key);
            }
            return cli;
        }

        public bool Has(string key) => _flags.Contains(key) || _values.ContainsKey(key);

        public string Get(string key, string fallback = null) =>
            _values.TryGetValue(key, out var value) ? value : fallback;

        public string Require(string key) =>
            Get(key) ?? throw new ArgumentException($"missing --{key}");

        public IReadOnlyList<string> List(string key, char separator = ',') =>
            (Get(key) ?? "")
                .Split(separator, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim())
                .Where(v => v.Length > 0)
                .ToList();
    }
}
