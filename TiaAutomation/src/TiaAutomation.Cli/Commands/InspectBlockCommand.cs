using System.Collections.Generic;
using System.IO;
using TiaAutomation.Core.Reports;
using TiaAutomation.Openness;

namespace TiaAutomation.Cli.Commands
{
    public class InspectBlockCommand
    {
        public int Run(Dictionary<string, string> options)
        {
            if (!options.TryGetValue("project", out var projectPath) || string.IsNullOrWhiteSpace(projectPath))
            {
                System.Console.Error.WriteLine("Missing required option: --project");
                return 2;
            }

            if (!options.TryGetValue("block", out var blockName) || string.IsNullOrWhiteSpace(blockName))
            {
                System.Console.Error.WriteLine("Missing required option: --block");
                return 2;
            }

            options.TryGetValue("export-dir", out var exportDir);
            options.TryGetValue("out", out var outPath);
            options.TryGetValue("openness-dll", out var opennessDll);

            if (string.IsNullOrWhiteSpace(exportDir))
            {
                exportDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath ?? "block-export")) ?? ".", "block-export");
            }

            if (string.IsNullOrWhiteSpace(outPath))
            {
                outPath = "block-inspect.json";
            }

            var result = new BlockInspector().InspectBlock(projectPath, blockName, exportDir, opennessDll);
            new JsonReportWriter().Write(outPath, result);
            System.Console.WriteLine($"Block inspect report: {outPath}");
            if (!string.IsNullOrWhiteSpace(result.ExportXmlPath))
            {
                System.Console.WriteLine($"Block XML exported: {result.ExportXmlPath}");
            }
            System.Console.WriteLine(result.Success ? "Block inspect succeeded." : $"Block inspect failed: {result.Diagnostic}");
            return result.Success ? 0 : 1;
        }
    }
}
