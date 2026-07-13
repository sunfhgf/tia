using System.Collections.Generic;
using TiaAutomation.Core.Gsd;
using TiaAutomation.Core.Reports;
using TiaAutomation.Openness;

namespace TiaAutomation.Cli.Commands
{
    public class InspectCommand
    {
        public int Run(Dictionary<string, string> options)
        {
            options.TryGetValue("project", out var projectPath);
            options.TryGetValue("gsd-dir", out var gsdDir);
            options.TryGetValue("out", out var outPath);
            options.TryGetValue("openness-dll", out var opennessDll);

            if (string.IsNullOrWhiteSpace(outPath))
            {
                outPath = "inspect-report.json";
            }

            var gsdScan = new GsdCatalogScanner().Scan(gsdDir);
            ProjectInventory inventory = null;
            if (!string.IsNullOrWhiteSpace(projectPath))
            {
                inventory = new ProjectInspector().Inspect(projectPath, opennessDll);
            }

            new JsonReportWriter().Write(outPath, new
            {
                Project = inventory,
                GsdCatalog = gsdScan,
                Summary = new
                {
                    GsdDevices = gsdScan.Devices.Count,
                    IgnoredFiles = gsdScan.IgnoredFiles.Count,
                    GsdWarnings = gsdScan.Warnings.Count,
                    ProjectStatus = inventory?.Status ?? "NotRequested"
                }
            });

            System.Console.WriteLine($"Inspect report written: {outPath}");
            return 0;
        }
    }
}
