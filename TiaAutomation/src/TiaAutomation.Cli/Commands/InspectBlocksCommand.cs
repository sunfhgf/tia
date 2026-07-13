using System.Collections.Generic;
using System.IO;
using System.Linq;
using TiaAutomation.Core.Reports;
using TiaAutomation.Openness;

namespace TiaAutomation.Cli.Commands
{
    public class InspectBlocksCommand
    {
        public int Run(Dictionary<string, string> options)
        {
            if (!options.TryGetValue("project", out var projectPath) || string.IsNullOrWhiteSpace(projectPath))
            {
                System.Console.Error.WriteLine("Missing required option: --project");
                return 2;
            }

            if (!options.TryGetValue("names", out var namesArg) || string.IsNullOrWhiteSpace(namesArg))
            {
                System.Console.Error.WriteLine("Missing required option: --names \"a,b,c\"");
                return 2;
            }

            options.TryGetValue("export-dir", out var exportDir);
            options.TryGetValue("out", out var outPath);
            options.TryGetValue("openness-dll", out var opennessDll);
            if (string.IsNullOrWhiteSpace(exportDir))
            {
                exportDir = "block-export";
            }
            if (string.IsNullOrWhiteSpace(outPath))
            {
                outPath = "blocks-inspect.json";
            }

            var names = namesArg.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            var reuse = options.ContainsKey("reuse-cache");
            var results = new BlockInspector().InspectBlocks(projectPath, names, exportDir, opennessDll, reuse);

            var allOk = true;
            foreach (var r in results)
            {
                System.Console.WriteLine($"{r.BlockName}: {(r.Success ? "OK" : "FAIL - " + r.Diagnostic)}");
                if (!r.Success)
                {
                    allOk = false;
                }
            }

            new JsonReportWriter().Write(outPath, results);
            System.Console.WriteLine($"Aggregated report: {outPath}");
            return allOk ? 0 : 1;
        }
    }
}
