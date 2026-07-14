using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TiaAutomation.Core.Gsd;
using TiaAutomation.Core.Models;
using TiaAutomation.Core.Planning;
using TiaAutomation.Core.Reports;
using TiaAutomation.Openness;

namespace TiaAutomation.Api
{
    public class Program
    {
        public static int Main(string[] args)
        {
            int port = 5005;
            string dataDir = null;
            string gsdDir = null;
            string catalogPath = null;
            string opennessDll = null;

            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--port" && int.TryParse(args[i + 1], out var p)) port = p;
                if (args[i] == "--data-dir") dataDir = args[i + 1];
                if (args[i] == "--gsd-dir") gsdDir = args[i + 1];
                if (args[i] == "--catalog") catalogPath = args[i + 1];
                if (args[i] == "--openness-dll") opennessDll = args[i + 1];
            }

            // 默认 Openness DLL 路径
            if (string.IsNullOrWhiteSpace(opennessDll))
            {
                opennessDll = @"E:\SOFT\TIA\Portal V20\PublicAPI\V20\Siemens.Engineering.dll";
            }

            if (string.IsNullOrWhiteSpace(gsdDir))
            {
                gsdDir = ResolveDefaultGsdDir();
            }

            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
            var store = new ProjectStore(dataDir);
            var planner = new AutomationPlanner();
            var router = new Router();

            // ── 健康检查 ──
            router.Map("GET", "/api/health", async ctx =>
            {
                await ctx.WriteJson(200, new
                {
                    ok = true,
                    version,
                    server = "TiaAutomation.Api",
                    dataDir = store.RootDir,
                    opennessAvailable = File.Exists(opennessDll),
                    time = DateTime.UtcNow.ToString("o")
                });
            });

            // ── 项目 GSD 设备目录 ──
            router.Map("GET", "/api/gsd/devices", async ctx =>
            {
                var query = ctx.Request.QueryString;
                var search = (query["search"] ?? string.Empty).Trim();
                var limitText = query["limit"];
                var limit = 120;
                if (int.TryParse(limitText, out var parsedLimit)) limit = Math.Max(10, Math.Min(parsedLimit, 500));

                var scanDir = gsdDir;
                var scan = new GsdCatalogScanner().Scan(scanDir);
                var rows = scan.Devices
                    .SelectMany(g => (g.AccessPoints != null && g.AccessPoints.Count > 0 ? g.AccessPoints : new List<GsdAccessPointInfo> { new GsdAccessPointInfo() })
                        .Select(a => new
                        {
                            displayName = BuildGsdDisplayName(g, a),
                            vendorName = g.VendorName,
                            vendorId = g.VendorId,
                            deviceId = g.DeviceId,
                            mainFamily = g.MainFamily,
                            productFamily = g.ProductFamily,
                            accessPointId = a.Id,
                            dnsCompatibleName = a.DnsCompatibleName,
                            moduleIdentNumber = a.ModuleIdentNumber,
                            orderNumber = a.OrderNumber,
                            hardwareRelease = a.HardwareRelease,
                            softwareRelease = a.SoftwareRelease,
                            modules = (a.Modules ?? new List<GsdModuleInfo>()).Select(m => new
                            {
                                id = m.Id,
                                name = m.Name,
                                moduleIdentNumber = m.ModuleIdentNumber,
                                orderNumber = m.OrderNumber,
                                allowedInSlots = m.AllowedInSlots,
                                inputLength = m.InputLength,
                                outputLength = m.OutputLength,
                                submodules = (m.Submodules ?? new List<GsdSubmoduleInfo>()).Select(s => new
                                {
                                    id = s.Id,
                                    name = s.Name,
                                    submoduleIdentNumber = s.SubmoduleIdentNumber,
                                    allowedInSubslots = s.AllowedInSubslots,
                                    inputLength = s.InputLength,
                                    outputLength = s.OutputLength
                                }).ToList()
                            }).ToList(),
                            fileName = g.FileName,
                            filePath = g.FilePath
                        }))
                    .Where(x => MatchesSearch(search, x.displayName, x.vendorName, x.orderNumber, x.productFamily, x.fileName, x.deviceId))
                    .OrderBy(x => x.vendorName)
                    .ThenBy(x => x.displayName)
                    .Take(limit)
                    .ToList();

                await ctx.WriteJson(200, new
                {
                    gsdDir = scanDir,
                    totalFiles = scan.Devices.Count,
                    returned = rows.Count,
                    warnings = scan.Warnings.Take(20).ToList(),
                    devices = rows
                });
            });
            // ── 项目列表 ──
            router.Map("GET", "/api/projects", async ctx =>
            {
                await ctx.WriteJson(200, store.ListProjects());
            });

            // ── 新建项目 ──
            router.Map("POST", "/api/projects", async ctx =>
            {
                var job = ctx.ReadBodyJson<AutomationJob>() ?? new AutomationJob();
                var result = store.CreateProject(job);
                await ctx.WriteJson(201, result);
            });

            // ── 获取项目 ──
            router.Map("GET", "/api/projects/{id}", async ctx =>
            {
                var job = store.LoadProject(ctx.PathParams["id"]);
                if (job == null) { await ctx.WriteJson(404, new { error = "not_found" }); return; }
                await ctx.WriteJson(200, job);
            });

            // ── 保存项目 ──
            router.Map("PUT", "/api/projects/{id}", async ctx =>
            {
                var id = ctx.PathParams["id"];
                var job = ctx.ReadBodyJson<AutomationJob>();
                if (job == null) { await ctx.WriteJson(400, new { error = "empty body" }); return; }
                if (!store.SaveProject(id, job)) { await ctx.WriteJson(404, new { error = "not_found" }); return; }
                await ctx.WriteJson(200, new { ok = true });
            });

            // ── 删除项目 ──
            router.Map("DELETE", "/api/projects/{id}", async ctx =>
            {
                var id = ctx.PathParams["id"];
                if (!store.DeleteProject(id)) { await ctx.WriteJson(404, new { error = "not_found" }); return; }
                await ctx.WriteJson(200, new { ok = true });
            });

            // ── CSV 导入 ──
            router.Map("POST", "/api/projects/{id}/import/{category}", async ctx =>
            {
                var id = ctx.PathParams["id"];
                var category = ctx.PathParams["category"];
                var csvText = ctx.ReadBodyText();
                if (string.IsNullOrWhiteSpace(csvText)) { await ctx.WriteJson(400, new { error = "empty body" }); return; }
                var result = store.ImportCsv(id, category, csvText);
                await ctx.WriteJson(result.Success ? 200 : 400, result);
            });

            // ── Plan（校验 + 计划，不写入） ──
            router.Map("POST", "/api/projects/{id}/plan", async ctx =>
            {
                var id = ctx.PathParams["id"];
                var job = store.LoadProject(id);
                if (job == null) { await ctx.WriteJson(404, new { error = "not_found" }); return; }

                var catalog = planner.LoadCatalog(catalogPath);
                var gsdScan = new GsdCatalogScanner().Scan(gsdDir);
                var plan = planner.Plan(job, catalog, gsdScan);

                // 保存计划到项目目录
                var planDir = Path.Combine(store.RootDir, id, "plans");
                Directory.CreateDirectory(planDir);
                var planFile = Path.Combine(planDir, $"plan_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                File.WriteAllText(planFile, Json.Serialize(plan), Encoding.UTF8);

                await ctx.WriteJson(200, plan);
            });

            // ── Apply（复制工程 + Openness 写入） ──
            router.Map("POST", "/api/projects/{id}/apply", async ctx =>
            {
                var id = ctx.PathParams["id"];
                var job = store.LoadProject(id);
                if (job == null) { await ctx.WriteJson(404, new { error = "not_found" }); return; }

                var body = ctx.ReadBodyJson<ApplyRequest>() ?? new ApplyRequest();

                // 先跑 plan 校验
                var catalog = planner.LoadCatalog(catalogPath);
                var gsdScan = new GsdCatalogScanner().Scan(gsdDir);
                var plan = planner.Plan(job, catalog, gsdScan);
                if (!plan.CanApply)
                {
                    await ctx.WriteJson(400, new { error = "plan_has_errors", issues = plan.Issues });
                    return;
                }

                // 模板工程路径：优先用 body.templateProject，其次用 job.templateProject
                var sourceProject = body.TemplateProject ?? job.TemplateProject;
                if (string.IsNullOrWhiteSpace(sourceProject) || !File.Exists(sourceProject))
                {
                    await ctx.WriteJson(400, new { error = "template_project_missing", hint = "请在配置页设置模板工程 .ap20 路径" });
                    return;
                }

                // 输出目录
                var outDir = Path.Combine(store.RootDir, id, "output", "latest");
                Directory.CreateDirectory(outDir);
                // 同一个项目只维护配置页中的这一份工程文件；生成时直接更新它，不再复制出新的工程副本。
                var copiedProjectPath = Path.GetFullPath(sourceProject);

                // 写入计划报告和标签 CSV
                new JsonReportWriter().Write(Path.Combine(outDir, "plan-report.json"), plan);
                WriteTagCsv(Path.Combine(outDir, "plc-tags.csv"), plan.TagsToCreate);

                var hasPlcHardwareWork = !string.IsNullOrWhiteSpace(job.Project?.PlcName) || !string.IsNullOrWhiteSpace(job.Project?.PlcIpAddress);
                var hasDeviceWork = (plan.DevicesToCreate?.Count ?? 0) > 0;
                var hasWriteWork = hasPlcHardwareWork || hasDeviceWork || (plan.TagsToCreate?.Count ?? 0) > 0
                    || (plan.StationCylinderPlans?.Count ?? 0) > 0
                    || (plan.ServoMappings?.Count ?? 0) > 0
                    || (plan.MotorMappings?.Count ?? 0) > 0
                    || (plan.AlarmPlans?.Count ?? 0) > 0;

                object resultObj;
                if (hasWriteWork)
                {
                    // Openness 写入
                    var tagTableName = body.TagTableName ?? "TIA_AUTO_IO";
                    var scratch = Path.Combine(outDir, "scratch");
                    var combined = new TiaProjectWriter().WriteAll(
                        copiedProjectPath, job.Project, plan.DevicesToCreate, plan.TagsToCreate, tagTableName,
                        plan.StationCylinderPlans, plan.ServoMappings, plan.MotorMappings, plan.AlarmPlans,
                        scratch, opennessDll);

                    resultObj = new
                    {
                        success = combined.Saved,
                        diagnostic = combined.Diagnostic,
                        projectPath = copiedProjectPath,
                        copiedOnly = false,
                        tags = combined.TagWriteResult,
                        plcHardware = combined.PlcHardwareResult,
                        devices = combined.DeviceWriteResult,
                        db = combined.DbWriteResult,
                        mappingFc = combined.MappingFcResult,
                        servoFc = combined.ServoFcResult,
                        motorFc = combined.MotorFcResult,
                        alarmFc = combined.AlarmFcResult,
                    };
                }
                else
                {
                    resultObj = new
                    {
                        success = true,
                        diagnostic = "项目工程已复制。当前未配置 IO/DB/FC 内容，已跳过 Openness 写入。",
                        projectPath = copiedProjectPath,
                        copiedOnly = true,
                        tags = (object)null,
                        plcHardware = (object)null,
                        devices = (object)null,
                        db = (object)null,
                        mappingFc = (object)null,
                        servoFc = (object)null,
                        motorFc = (object)null,
                        alarmFc = (object)null,
                    };
                }

                new JsonReportWriter().Write(Path.Combine(outDir, "apply-result.json"), resultObj);

                await ctx.WriteJson(200, resultObj);
            });

            // ── 获取生成历史 ──
            router.Map("GET", "/api/projects/{id}/runs", async ctx =>
            {
                var id = ctx.PathParams["id"];
                var outputDir = Path.Combine(store.RootDir, id, "output");
                if (!Directory.Exists(outputDir)) { await ctx.WriteJson(200, new object[0]); return; }

                var runs = new List<object>();
                foreach (var dir in Directory.GetDirectories(outputDir).OrderByDescending(d => d))
                {
                    var resultFile = Path.Combine(dir, "apply-result.json");
                    var planFile = Directory.GetFiles(dir, "plan-report.json").FirstOrDefault();
                    object result = null;
                    object planObj = null;
                    try { if (File.Exists(resultFile)) result = Json.Deserialize<object>(File.ReadAllText(resultFile)); } catch { }
                    try { if (planFile != null) planObj = Json.Deserialize<object>(File.ReadAllText(planFile)); } catch { }
                    runs.Add(new { dir = Path.GetFileName(dir), result, plan = planObj });
                }
                await ctx.WriteJson(200, runs);
            });

            // ── 启动 ──
            var server = new HttpServer(port, router);
            server.Start();
            Console.WriteLine($"[api] data dir: {store.RootDir}");

            var done = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("[api] shutdown requested");
                server.Stop();
                done.Set();
            };

            done.Wait();
            return 0;
        }

        private static void CopyDirectory(string source, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var f in Directory.GetFiles(source))
            {
                if (f.EndsWith(".baiduyun.uploading.cfg", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
            }
            foreach (var d in Directory.GetDirectories(source))
            {
                CopyDirectory(d, Path.Combine(dest, Path.GetFileName(d)));
            }
        }

        private static string ResolveDefaultGsdDir()
        {
            var candidates = new List<string>();
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var current = new DirectoryInfo(baseDir);
            while (current != null)
            {
                candidates.Add(Path.Combine(current.FullName, "gsd"));
                current = current.Parent;
            }

            candidates.Add(Path.Combine(Environment.CurrentDirectory, "gsd"));
            candidates.Add(@"C:\ProgramData\Siemens\Automation\Portal V20\data\xdd\gsd");

            return candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
        }
        private static bool MatchesSearch(string search, params string[] fields)
        {
            if (string.IsNullOrWhiteSpace(search)) return true;
            return fields.Any(f => !string.IsNullOrWhiteSpace(f) && f.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string BuildGsdDisplayName(GsdDeviceInfo device, GsdAccessPointInfo accessPoint)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(device?.VendorName)) parts.Add(device.VendorName);
            if (!string.IsNullOrWhiteSpace(accessPoint?.OrderNumber)) parts.Add(accessPoint.OrderNumber);
            else if (!string.IsNullOrWhiteSpace(device?.ProductFamily)) parts.Add(device.ProductFamily);
            if (!string.IsNullOrWhiteSpace(accessPoint?.DnsCompatibleName)) parts.Add(accessPoint.DnsCompatibleName);
            if (parts.Count == 0) parts.Add(device?.FileName ?? "GSDML device");
            return string.Join(" / ", parts.Distinct());
        }
        private static void WriteTagCsv(string path, System.Collections.Generic.IEnumerable<IoPoint> points)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Name,Address,DataType,Comment,Device,Channel");
            foreach (var p in points)
            {
                sb.AppendLine($"\"{p.Tag}\",\"{p.Address}\",\"{p.DataType}\",\"{p.Comment}\",\"{p.Device}\",\"{p.Channel}\"");
            }
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        private class ApplyRequest
        {
            public string TemplateProject { get; set; }
            public string TagTableName { get; set; }
        }
    }
}











