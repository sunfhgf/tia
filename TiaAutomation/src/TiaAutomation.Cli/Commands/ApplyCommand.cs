using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TiaAutomation.Core.Gsd;
using TiaAutomation.Core.Models;
using TiaAutomation.Core.Planning;
using TiaAutomation.Core.Reports;
using TiaAutomation.Openness;

namespace TiaAutomation.Cli.Commands
{
    public class ApplyCommand
    {
        public int Run(Dictionary<string, string> options)
        {
            if (!options.TryGetValue("job", out var jobPath) || string.IsNullOrWhiteSpace(jobPath))
            {
                Console.Error.WriteLine("Missing required option: --job");
                return 2;
            }

            if (!options.TryGetValue("source-project", out var sourceProject) || string.IsNullOrWhiteSpace(sourceProject))
            {
                Console.Error.WriteLine("Missing required option: --source-project");
                return 2;
            }

            if (!options.TryGetValue("target-project-dir", out var targetProjectDir) || string.IsNullOrWhiteSpace(targetProjectDir))
            {
                Console.Error.WriteLine("Missing required option: --target-project-dir");
                return 2;
            }

            options.TryGetValue("catalog", out var catalogPath);
            options.TryGetValue("gsd-dir", out var gsdDir);
            options.TryGetValue("out", out var outDir);
            options.TryGetValue("openness-dll", out var opennessDll);
            options.TryGetValue("tag-table", out var tagTableName);
            var enableTiaWrite = options.ContainsKey("enable-tia-write");
            if (string.IsNullOrWhiteSpace(tagTableName))
            {
                tagTableName = "TIA_AUTO_IO";
            }

            if (string.IsNullOrWhiteSpace(outDir))
            {
                outDir = Path.Combine(targetProjectDir, "automation-output");
            }

            var planner = new AutomationPlanner();
            var job = planner.LoadJob(jobPath);
            var catalog = planner.LoadCatalog(catalogPath);
            var gsdScan = new GsdCatalogScanner().Scan(gsdDir);
            var plan = planner.Plan(job, catalog, gsdScan);

            if (!plan.CanApply)
            {
                new JsonReportWriter().Write(Path.Combine(outDir, "refused-plan-report.json"), plan);
                Console.Error.WriteLine("Plan has errors. Apply refused.");
                return 1;
            }

            var copiedProjectDir = CopyTemplateProject(sourceProject, targetProjectDir);
            var copiedProjectPath = Path.Combine(copiedProjectDir, Path.GetFileName(sourceProject));
            Directory.CreateDirectory(outDir);

            new JsonReportWriter().Write(Path.Combine(outDir, "applied-plan-report.json"), plan);
            WriteTagCsv(Path.Combine(outDir, "planned-plc-tags.csv"), plan.TagsToCreate);
            new JsonReportWriter().Write(Path.Combine(outDir, "cylinder-mapping.json"), plan.CylinderMappings);
            new JsonReportWriter().Write(Path.Combine(outDir, "servo-mapping.json"), plan.ServoMappings);
            new JsonReportWriter().Write(Path.Combine(outDir, "motor-mapping.json"), plan.MotorMappings);
            new JsonReportWriter().Write(Path.Combine(outDir, "station-plan.json"), plan.StationPlans);
            new JsonReportWriter().Write(Path.Combine(outDir, "alarm-plan.json"), plan.AlarmPlans);
            File.WriteAllLines(Path.Combine(outDir, "manual-checklist.txt"), plan.ManualTasks, Encoding.UTF8);

            TiaWriteResult tiaWriteResult = null;
            DbWriteResult dbWriteResult = null;
            CombinedWriteResult combined = null;
            if (enableTiaWrite)
            {
                var scratch = Path.Combine(outDir, "db-xml");
                combined = new TiaProjectWriter().WriteAll(copiedProjectPath, plan.TagsToCreate, tagTableName, plan.StationCylinderPlans, plan.ServoMappings, plan.MotorMappings, plan.AlarmPlans, scratch, opennessDll);
                tiaWriteResult = combined.TagWriteResult;
                dbWriteResult = combined.DbWriteResult;
                if (tiaWriteResult != null) tiaWriteResult.ProjectPath = copiedProjectPath;
                if (dbWriteResult != null) dbWriteResult.ProjectPath = copiedProjectPath;
                new JsonReportWriter().Write(Path.Combine(outDir, "tia-write-result.json"), tiaWriteResult);
                new JsonReportWriter().Write(Path.Combine(outDir, "db-write-result.json"), dbWriteResult);
                if (combined.MappingFcResult != null)
                {
                    new JsonReportWriter().Write(Path.Combine(outDir, "mapping-fc-result.json"), combined.MappingFcResult);
                }
                if (combined.ServoFcResult != null)
                {
                    new JsonReportWriter().Write(Path.Combine(outDir, "servo-fc-result.json"), combined.ServoFcResult);
                }
                if (combined.MotorFcResult != null)
                {
                    new JsonReportWriter().Write(Path.Combine(outDir, "motor-fc-result.json"), combined.MotorFcResult);
                }
                if (combined.AlarmFcResult != null)
                {
                    new JsonReportWriter().Write(Path.Combine(outDir, "alarm-fc-result.json"), combined.AlarmFcResult);
                }
                if (plan.StationCylinderPlans != null && plan.StationCylinderPlans.Count > 0)
                {
                    WriteIoMappingCsv(Path.Combine(outDir, "io-station-mapping.csv"), plan.StationCylinderPlans);
                }
            }

            var log = new StringBuilder();
            log.AppendLine("MVP apply completed.");
            log.AppendLine("Hardware and PLC block writes are intentionally disabled.");
            log.AppendLine($"Copied project directory: {copiedProjectDir}");
            log.AppendLine($"Copied project file: {copiedProjectPath}");
            if (enableTiaWrite)
            {
                log.AppendLine("TIA tag writing was requested with --enable-tia-write.");
                if (combined != null && !string.IsNullOrWhiteSpace(combined.Diagnostic))
                {
                    log.AppendLine($"Combined diagnostic: {combined.Diagnostic}");
                    log.AppendLine($"Saved: {combined.Saved}");
                }
                log.AppendLine($"TIA tag write success: {tiaWriteResult?.Success}");
                log.AppendLine($"TIA tag write diagnostic: {tiaWriteResult?.Diagnostic}");
                if (dbWriteResult != null)
                {
                    log.AppendLine($"Station DB write success: {dbWriteResult.Success}");
                    log.AppendLine($"Station DB diagnostic: {dbWriteResult.Diagnostic}");
                    log.AppendLine($"Created blocks: {dbWriteResult.CreatedBlocks.Count}, existing: {dbWriteResult.ExistingBlocks.Count}, warnings: {dbWriteResult.Warnings.Count}");
                }
                else
                {
                    log.AppendLine("Station DB write result is null (Openness session may have failed).");
                }
                if (combined?.MappingFcResult != null)
                {
                    log.AppendLine($"Mapping FC success: {combined.MappingFcResult.Success}");
                    log.AppendLine($"Mapping FC diagnostic: {combined.MappingFcResult.Diagnostic}");
                    log.AppendLine($"Mapping FC warnings: {combined.MappingFcResult.Warnings.Count}");
                }
                if (combined?.ServoFcResult != null)
                {
                    log.AppendLine($"Servo FC success: {combined.ServoFcResult.Success}, generated: {combined.ServoFcResult.GeneratedFcs.Count}, warnings: {combined.ServoFcResult.Warnings.Count}");
                    log.AppendLine($"Servo FC diagnostic: {combined.ServoFcResult.Diagnostic}");
                }
                if (combined?.MotorFcResult != null)
                {
                    log.AppendLine($"Motor FC success: {combined.MotorFcResult.Success}, generated: {combined.MotorFcResult.GeneratedFcs.Count}, warnings: {combined.MotorFcResult.Warnings.Count}");
                    log.AppendLine($"Motor FC diagnostic: {combined.MotorFcResult.Diagnostic}");
                }
                if (combined?.AlarmFcResult != null)
                {
                    log.AppendLine($"Alarm FC success: {combined.AlarmFcResult.Success}, generated: {combined.AlarmFcResult.GeneratedFcs.Count}, warnings: {combined.AlarmFcResult.Warnings.Count}");
                    log.AppendLine($"Alarm FC diagnostic: {combined.AlarmFcResult.Diagnostic}");
                }
            }
            else
            {
                log.AppendLine("TIA tag writing was not requested. Use --enable-tia-write to write tags to the copied project.");
            }

            File.WriteAllText(Path.Combine(outDir, "build-log.txt"), log.ToString(), Encoding.UTF8);

            Console.WriteLine($"Template project copied to: {copiedProjectDir}");
            Console.WriteLine($"Apply artifacts written to: {outDir}");
            return 0;
        }

        private static string CopyTemplateProject(string sourceProject, string targetProjectDir)
        {
            if (!File.Exists(sourceProject))
            {
                throw new FileNotFoundException("Source project not found.", sourceProject);
            }

            var sourceDir = Path.GetDirectoryName(Path.GetFullPath(sourceProject));
            var destination = Path.Combine(Path.GetFullPath(targetProjectDir), Path.GetFileName(sourceDir) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            CopyDirectory(sourceDir, destination);
            return destination;
        }

        private static void CopyDirectory(string sourceDir, string destinationDir)
        {
            Directory.CreateDirectory(destinationDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                if (file.EndsWith(".baiduyun.uploading.cfg", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), false);
            }

            foreach (var directory in Directory.GetDirectories(sourceDir))
            {
                CopyDirectory(directory, Path.Combine(destinationDir, Path.GetFileName(directory)));
            }
        }

        private static void WriteTagCsv(string path, IEnumerable<IoPoint> points)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Name,Address,DataType,Comment,Device,Channel");
            foreach (var point in points)
            {
                builder.Append(Escape(point.Tag)).Append(',')
                    .Append(Escape(point.Address)).Append(',')
                    .Append(Escape(point.DataType)).Append(',')
                    .Append(Escape(point.Comment)).Append(',')
                    .Append(Escape(point.Device)).Append(',')
                    .Append(Escape(point.Channel)).AppendLine();
            }

            File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
        }

        private static string Escape(string value)
        {
            value = value ?? string.Empty;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static void WriteIoMappingCsv(string path, IEnumerable<TiaAutomation.Core.Models.StationCylinderPlan> stations)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Station,CylinderIndex,CylinderName,SourcePlcTag,Direction,DestDb,DestField");
            foreach (var sc in stations)
            {
                foreach (var c in sc.Cylinders ?? new List<TiaAutomation.Core.Models.StationCylinder>())
                {
                    if (!string.IsNullOrWhiteSpace(c.ExtendIo))
                    {
                        sb.AppendLine(string.Join(",", new[] { Escape(sc.StationId), c.Index.ToString(), Escape(c.Name), Escape(c.ExtendIo), "ExtendFb", Escape(sc.StationIDb), Escape($"气缸感应出_{c.Index}") }));
                    }
                    if (!string.IsNullOrWhiteSpace(c.RetractIo))
                    {
                        sb.AppendLine(string.Join(",", new[] { Escape(sc.StationId), c.Index.ToString(), Escape(c.Name), Escape(c.RetractIo), "RetractFb", Escape(sc.StationIDb), Escape($"气缸感应回_{c.Index}") }));
                    }
                    if (!string.IsNullOrWhiteSpace(c.ExtendOut))
                    {
                        sb.AppendLine(string.Join(",", new[] { Escape(sc.StationId), c.Index.ToString(), Escape(c.Name), Escape(c.ExtendOut), "ExtendOut", Escape(sc.StationQDb), Escape($"气缸出_{c.Index}") }));
                    }
                    if (!string.IsNullOrWhiteSpace(c.RetractOut))
                    {
                        sb.AppendLine(string.Join(",", new[] { Escape(sc.StationId), c.Index.ToString(), Escape(c.Name), Escape(c.RetractOut), "RetractOut", Escape(sc.StationQDb), Escape($"气缸回_{c.Index}") }));
                    }
                }
            }
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
    }
}
