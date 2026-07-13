using System.Collections.Generic;
using TiaAutomation.Core.Gsd;
using TiaAutomation.Core.Planning;
using TiaAutomation.Core.Reports;

namespace TiaAutomation.Cli.Commands
{
    public class PlanCommand
    {
        public int Run(Dictionary<string, string> options)
        {
            if (!options.TryGetValue("job", out var jobPath) || string.IsNullOrWhiteSpace(jobPath))
            {
                System.Console.Error.WriteLine("Missing required option: --job");
                return 2;
            }

            options.TryGetValue("catalog", out var catalogPath);
            options.TryGetValue("gsd-dir", out var gsdDir);
            options.TryGetValue("out", out var outPath);

            if (string.IsNullOrWhiteSpace(outPath))
            {
                outPath = "plan-report.json";
            }

            var planner = new AutomationPlanner();
            var job = planner.LoadJob(jobPath);
            var catalog = planner.LoadCatalog(catalogPath);
            var gsdScan = new GsdCatalogScanner().Scan(gsdDir);
            var plan = planner.Plan(job, catalog, gsdScan);

            new JsonReportWriter().Write(outPath, plan);
            System.Console.WriteLine($"Plan report written: {outPath}");
            System.Console.WriteLine(plan.CanApply ? "Plan is valid for MVP apply." : "Plan has errors; apply will refuse it.");
            return plan.CanApply ? 0 : 1;
        }
    }
}
