using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FigCraft.Features
{
    /// <summary>One step of a <c>figcraft-cli run</c> script.</summary>
    public sealed class PatchStep
    {
        public string Command { get; }
        public JObject Params { get; }

        public PatchStep(string command, JObject parameters = null)
        {
            Command = command ?? throw new ArgumentNullException(nameof(command));
            Params = parameters ?? new JObject();
        }

        public JObject ToJson() => new JObject
        {
            ["command"] = Command,
            ["params"] = Params,
        };

        public override string ToString() => Command + " " + Params.ToString(Formatting.None);
    }

    /// <summary>What one step of a run answered. Ids live here, so callers can use them in a later pass.</summary>
    public sealed class StepResult
    {
        public int Step;
        public string Command;
        public bool Ok;
        public JToken Result;
        public string Error;
    }

    public sealed class FigCraftException : Exception
    {
        public FigCraftException(string message) : base(message) { }
    }

    /// <summary>
    /// Runs patch scripts through <c>figcraft-cli run</c>. The CLI applies the script atomically: if a step
    /// fails, every change in that script is rolled back, so a failed pass never leaves half a component.
    /// </summary>
    public sealed class FigCraftClient
    {
        public string CliPath { get; set; } = "figcraft-cli";

        public List<StepResult> Run(string input, string output, IReadOnlyList<PatchStep> steps)
        {
            if (steps == null || steps.Count == 0) return new List<StepResult>();

            var script = Path.Combine(Path.GetTempPath(), "figcraft-features-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(script, new JArray(steps.Select(s => s.ToJson())).ToString(Formatting.Indented));

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = CliPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                psi.ArgumentList.Add("run");
                psi.ArgumentList.Add(script);
                // Without an input the run starts from a new, empty document.
                if (!string.IsNullOrEmpty(input))
                {
                    psi.ArgumentList.Add("--in");
                    psi.ArgumentList.Add(Path.GetFullPath(input));
                }
                if (!string.IsNullOrEmpty(output))
                {
                    psi.ArgumentList.Add("--out");
                    psi.ArgumentList.Add(Path.GetFullPath(output));
                }

                Process process;
                try
                {
                    process = Process.Start(psi) ?? throw new FigCraftException($"could not start {CliPath}");
                }
                catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException || ex is PlatformNotSupportedException)
                {
                    throw new FigCraftException($"could not run {CliPath}: {ex.Message}");
                }

                using (process)
                {
                    var stdout = process.StandardOutput.ReadToEnd();
                    var stderr = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    var results = Parse(stdout);
                    if (process.ExitCode != 0)
                    {
                        var failed = results.FirstOrDefault(r => !r.Ok);
                        var detail = failed != null
                            ? $"step {failed.Step} ({failed.Command}): {failed.Error}"
                            : stderr.Trim();
                        if (string.IsNullOrEmpty(detail)) detail = "the script was rolled back";
                        throw new FigCraftException(detail);
                    }

                    return results;
                }
            }
            finally
            {
                try { File.Delete(script); } catch { /* best effort */ }
            }
        }

        static List<StepResult> Parse(string stdout)
        {
            var results = new List<StepResult>();
            foreach (var line in stdout.Split('\n'))
            {
                var text = line.Trim();
                if (!text.StartsWith("{", StringComparison.Ordinal)) continue;

                JObject obj;
                try { obj = JObject.Parse(text); }
                catch (JsonException) { continue; }

                results.Add(new StepResult
                {
                    Step = obj.Value<int?>("step") ?? results.Count,
                    Command = obj.Value<string>("command"),
                    Ok = obj.Value<bool?>("ok") ?? false,
                    Result = obj["result"],
                    Error = obj.Value<string>("error"),
                });
            }
            return results;
        }
    }
}
