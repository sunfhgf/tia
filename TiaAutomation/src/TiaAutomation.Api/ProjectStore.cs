using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TiaAutomation.Core.Models;

namespace TiaAutomation.Api
{
    /// <summary>
    /// 项目存储：每个项目一个目录 ~/Documents/TiaAutomation/{id}/，
    /// 内含 project.json + 上传的 CSV 文件 + 输出产物。
    /// </summary>
    public class ProjectStore
    {
        public string RootDir { get; }

        public ProjectStore(string rootDir = null)
        {
            RootDir = rootDir ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "TiaAutomation");
            Directory.CreateDirectory(RootDir);
        }

        // ── 列表 ──
        public List<ProjectSummary> ListProjects()
        {
            var result = new List<ProjectSummary>();
            foreach (var dir in Directory.GetDirectories(RootDir))
            {
                var metaPath = Path.Combine(dir, "project.json");
                if (!File.Exists(metaPath)) continue;
                try
                {
                    var job = Json.Deserialize<AutomationJob>(File.ReadAllText(metaPath));
                    result.Add(new ProjectSummary
                    {
                        Id = Path.GetFileName(dir),
                        ProjectName = job.ProjectName ?? job.Project?.BuildProjectName() ?? "(unnamed)",
                        StationCount = job.Stations?.Count ?? 0,
                        IoCount = job.IoPoints?.Count ?? 0,
                        ServoCount = job.Servos?.Count ?? 0,
                        MotorCount = job.Motors?.Count ?? 0,
                        CylinderCount = job.Cylinders?.Count ?? 0,
                        ModifiedAt = File.GetLastWriteTime(metaPath).ToString("o"),
                        TemplateProject = job.TemplateProject,
                        NetworkProjectDirectory = job.NetworkProjectDirectory,
                    });
                }
                catch { }
            }
            return result.OrderByDescending(p => p.ModifiedAt).ToList();
        }

        // ── 读取 ──
        public AutomationJob LoadProject(string id)
        {
            var path = GetProjectJsonPath(id);
            if (!File.Exists(path)) return null;
            return Json.Deserialize<AutomationJob>(File.ReadAllText(path));
        }

        // ── 保存 ──
        public ProjectCreationResult CreateProject(AutomationJob job)
        {
            job = job ?? new AutomationJob();
            var id = Guid.NewGuid().ToString("N").Substring(0, 8);
            var dir = GetProjectDir(id);
            Directory.CreateDirectory(dir);

            if (string.IsNullOrWhiteSpace(job.ProjectName))
            {
                job.ProjectName = job.Project?.BuildProjectName();
            }

            var result = new ProjectCreationResult { Id = id };
            PrepareNetworkProject(job, dir, result);

            File.WriteAllText(GetProjectJsonPath(id), Json.Serialize(job), Encoding.UTF8);
            return result;
        }

        public bool SaveProject(string id, AutomationJob job)
        {
            var dir = GetProjectDir(id);
            if (!Directory.Exists(dir)) return false;
            File.WriteAllText(GetProjectJsonPath(id), Json.Serialize(job), System.Text.Encoding.UTF8);
            return true;
        }

        // ── 删除 ──
        public bool DeleteProject(string id)
        {
            var dir = GetProjectDir(id);
            if (!Directory.Exists(dir)) return false;
            Directory.Delete(dir, true);
            return true;
        }

        // ── CSV 导入 ──
        public ImportResult ImportCsv(string id, string category, string csvText)
        {
            var job = LoadProject(id);
            if (job == null) return new ImportResult { Success = false, Error = "Project not found" };

            var rows = ParseCsv(csvText);
            if (rows.Count == 0) return new ImportResult { Success = false, Error = "CSV is empty" };

            var headers = rows[0];
            var imported = 0;
            var errors = new List<string>();

            try
            {
                switch (category)
                {
                    case "io":
                        imported = ImportIo(job, headers, rows, errors);
                        break;
                    case "servo":
                        imported = ImportServos(job, headers, rows, errors);
                        break;
                    case "motor":
                        imported = ImportMotors(job, headers, rows, errors);
                        break;
                    case "alarm":
                        imported = ImportAlarms(job, headers, rows, errors);
                        break;
                    default:
                        return new ImportResult { Success = false, Error = $"Unknown category: {category}" };
                }

                SaveProject(id, job);
                // 也把原始 CSV 存到项目目录
                var csvDir = Path.Combine(GetProjectDir(id), "csv");
                Directory.CreateDirectory(csvDir);
                File.WriteAllText(Path.Combine(csvDir, category + ".csv"), csvText, System.Text.Encoding.UTF8);

                return new ImportResult { Success = true, Imported = imported, Errors = errors };
            }
            catch (Exception ex)
            {
                return new ImportResult { Success = false, Error = ex.Message };
            }
        }

        // ── CSV 解析 ──
        private static List<List<string>> ParseCsv(string text)
        {
            var rows = new List<List<string>>();
            foreach (var line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = new List<string>();
                var current = new List<char>();
                bool inQuotes = false;
                foreach (var c in line)
                {
                    if (c == '"') { inQuotes = !inQuotes; }
                    else if (c == ',' && !inQuotes) { fields.Add(new string(current.ToArray())); current.Clear(); }
                    else { current.Add(c); }
                }
                fields.Add(new string(current.ToArray()));
                rows.Add(fields);
            }
            return rows;
        }

        private static string Val(List<string> row, int i) => i < row.Count ? row[i].Trim() : "";

        private static int FindCol(List<string> headers, params string[] names)
        {
            for (int i = 0; i < headers.Count; i++)
            {
                var h = headers[i].Trim().ToLowerInvariant();
                foreach (var n in names)
                {
                    if (h == n.ToLowerInvariant()) return i;
                }
            }
            return -1;
        }

        private int ImportIo(AutomationJob job, List<string> headers, List<List<string>> rows, List<string> errors)
        {
            var iDevice = FindCol(headers, "device");
            var iChannel = FindCol(headers, "channel");
            var iAddress = FindCol(headers, "address");
            var iTag = FindCol(headers, "tag");
            var iDataType = FindCol(headers, "datatype", "dataType", "type");
            var iComment = FindCol(headers, "comment");

            if (iTag < 0 || iAddress < 0)
            {
                errors.Add("CSV 缺少必需列: tag, address");
                return 0;
            }

            var count = 0;
            for (int r = 1; r < rows.Count; r++)
            {
                var row = rows[r];
                var tag = Val(row, iTag);
                if (string.IsNullOrWhiteSpace(tag)) continue;
                job.IoPoints.Add(new IoPoint
                {
                    Device = iDevice >= 0 ? Val(row, iDevice) : "",
                    Channel = iChannel >= 0 ? Val(row, iChannel) : "",
                    Address = Val(row, iAddress),
                    Tag = tag,
                    DataType = iDataType >= 0 ? Val(row, iDataType) : "Bool",
                    Comment = iComment >= 0 ? Val(row, iComment) : "",
                });
                count++;
            }
            return count;
        }

        private int ImportServos(AutomationJob job, List<string> headers, List<List<string>> rows, List<string> errors)
        {
            var iName = FindCol(headers, "name");
            var iStation = FindCol(headers, "station");
            var iDevice = FindCol(headers, "device");
            var iAxis = FindCol(headers, "axisname", "axisName", "axis");
            var iTel = FindCol(headers, "telegram");
            var iHwId = FindCol(headers, "hardwareid", "hardwareId", "hwid");
            var iTelAddr = FindCol(headers, "telegramaddress", "telegramAddress");
            var iLogic = FindCol(headers, "logicblock", "logicBlock");

            if (iName < 0) { errors.Add("CSV 缺少必需列: name"); return 0; }

            var count = 0;
            for (int r = 1; r < rows.Count; r++)
            {
                var row = rows[r];
                var name = Val(row, iName);
                if (string.IsNullOrWhiteSpace(name)) continue;
                job.Servos.Add(new ServoRequest
                {
                    Name = name,
                    Station = iStation >= 0 ? Val(row, iStation) : "",
                    Device = iDevice >= 0 ? Val(row, iDevice) : "",
                    AxisName = iAxis >= 0 ? Val(row, iAxis) : "",
                    Telegram = iTel >= 0 ? Val(row, iTel) : "",
                    HardwareId = iHwId >= 0 ? int.TryParse(Val(row, iHwId), out var hid) ? hid : (int?)null : null,
                    TelegramAddress = iTelAddr >= 0 ? int.TryParse(Val(row, iTelAddr), out var ta) ? ta : (int?)null : null,
                    LogicBlock = iLogic >= 0 ? Val(row, iLogic) : "",
                });
                count++;
            }
            return count;
        }

        private int ImportMotors(AutomationJob job, List<string> headers, List<List<string>> rows, List<string> errors)
        {
            var iName = FindCol(headers, "name");
            var iStation = FindCol(headers, "station");
            var iDevice = FindCol(headers, "device");
            var iType = FindCol(headers, "type");
            var iRunOut = FindCol(headers, "runoutput", "runOutput", "run");
            var iFault = FindCol(headers, "faultinput", "faultInput", "fault");
            var iLogic = FindCol(headers, "logicblock", "logicBlock");

            if (iName < 0) { errors.Add("CSV 缺少必需列: name"); return 0; }

            var count = 0;
            for (int r = 1; r < rows.Count; r++)
            {
                var row = rows[r];
                var name = Val(row, iName);
                if (string.IsNullOrWhiteSpace(name)) continue;
                job.Motors.Add(new MotorRequest
                {
                    Name = name,
                    Station = iStation >= 0 ? Val(row, iStation) : "",
                    Device = iDevice >= 0 ? Val(row, iDevice) : "",
                    Type = iType >= 0 ? Val(row, iType) : "",
                    RunOutput = iRunOut >= 0 ? Val(row, iRunOut) : "",
                    FaultInput = iFault >= 0 ? Val(row, iFault) : "",
                    LogicBlock = iLogic >= 0 ? Val(row, iLogic) : "",
                });
                count++;
            }
            return count;
        }

        private int ImportAlarms(AutomationJob job, List<string> headers, List<List<string>> rows, List<string> errors)
        {
            var iStation = FindCol(headers, "station");
            var iSource = FindCol(headers, "source");
            var iSourceType = FindCol(headers, "sourcetype", "sourceType");
            var iText = FindCol(headers, "text");
            var iLevel = FindCol(headers, "level");

            if (iSource < 0 || iStation < 0) { errors.Add("CSV 缺少必需列: station, source"); return 0; }

            var count = 0;
            for (int r = 1; r < rows.Count; r++)
            {
                var row = rows[r];
                var source = Val(row, iSource);
                if (string.IsNullOrWhiteSpace(source)) continue;
                job.Alarms.Add(new AlarmRequest
                {
                    Station = Val(row, iStation),
                    Source = source,
                    SourceType = iSourceType >= 0 ? Val(row, iSourceType) : "",
                    Text = iText >= 0 ? Val(row, iText) : "",
                    Level = iLevel >= 0 ? Val(row, iLevel) : "Error",
                });
                count++;
            }
            return count;
        }

        private void PrepareNetworkProject(AutomationJob job, string projectDir, ProjectCreationResult result)
        {
            var sourceProject = ResolveStandardProjectPath();
            if (string.IsNullOrWhiteSpace(sourceProject) || !File.Exists(sourceProject))
            {
                result.Warnings.Add("未找到标准工程 V20250626_V20-多设备，已创建项目配置但未复制 TIA 工程。");
                return;
            }

            var projectName = ResolveProjectName(job, result.Id);
            var safeProjectName = SanitizeFileName(projectName);
            var sourceDir = Path.GetDirectoryName(Path.GetFullPath(sourceProject));
            var networkRoot = Path.Combine(projectDir, "network-project");
            var targetDir = Path.Combine(networkRoot, safeProjectName);
            targetDir = MakeUniqueDirectory(targetDir);

            CopyDirectory(sourceDir, targetDir);

            var copiedOriginalProject = Path.Combine(targetDir, Path.GetFileName(sourceProject));
            var renamedProject = Path.Combine(targetDir, safeProjectName + Path.GetExtension(sourceProject));
            if (!string.Equals(copiedOriginalProject, renamedProject, StringComparison.OrdinalIgnoreCase) && File.Exists(copiedOriginalProject))
            {
                if (File.Exists(renamedProject)) File.Delete(renamedProject);
                File.Move(copiedOriginalProject, renamedProject);
            }

            job.ProjectName = projectName;
            job.TemplateProject = File.Exists(renamedProject) ? renamedProject : copiedOriginalProject;
            job.NetworkProjectDirectory = targetDir;
            job.NetworkPreparationReport = Path.Combine(projectDir, "network-preparation.txt");

            result.ProjectName = job.ProjectName;
            result.CopiedProjectDirectory = job.NetworkProjectDirectory;
            result.CopiedProjectPath = job.TemplateProject;
            result.NetworkPreparationReport = job.NetworkPreparationReport;

            WriteNetworkPreparationReport(job, sourceProject, result);
        }

        private static string ResolveProjectName(AutomationJob job, string id)
        {
            var generated = job.Project?.BuildProjectName();
            if (!string.IsNullOrWhiteSpace(job.Project?.Code)
                && !string.IsNullOrWhiteSpace(job.Project?.Station)
                && !string.IsNullOrWhiteSpace(job.Project?.VersionDate)
                && !string.IsNullOrWhiteSpace(generated))
            {
                return generated.Trim();
            }

            var name = job.ProjectName;
            if (string.IsNullOrWhiteSpace(name)) name = generated;
            if (string.IsNullOrWhiteSpace(name)) name = "TIA_Project_" + id;
            return name.Trim();
        }

        private static string ResolveStandardProjectPath()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var start in new[] { baseDir, Environment.CurrentDirectory })
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    var candidate = Path.Combine(dir.FullName, "V20250626_V20-多设备", "V20250626_V20-多设备.ap20");
                    if (File.Exists(candidate)) return candidate;

                    var sibling = Path.Combine(dir.FullName, "..", "V20250626_V20-多设备", "V20250626_V20-多设备.ap20");
                    sibling = Path.GetFullPath(sibling);
                    if (File.Exists(sibling)) return sibling;

                    dir = dir.Parent;
                }
            }
            return null;
        }

        private static string MakeUniqueDirectory(string path)
        {
            if (!Directory.Exists(path) && !File.Exists(path)) return path;
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            return path + "_" + stamp;
        }

        private static void CopyDirectory(string source, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var file in Directory.GetFiles(source))
            {
                if (file.EndsWith(".baiduyun.uploading.cfg", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), false);
            }
            foreach (var dir in Directory.GetDirectories(source))
            {
                CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
            }
        }

        private static string SanitizeFileName(string value)
        {
            value = string.IsNullOrWhiteSpace(value) ? "TIA_Project" : value.Trim();
            foreach (var c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
            return value;
        }

        private static void WriteNetworkPreparationReport(AutomationJob job, string sourceProject, ProjectCreationResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 网络配置准备结果");
            sb.AppendLine();
            sb.AppendLine("1. 已复制标准模板工程。");
            sb.AppendLine("2. 已按新项目名称重命名复制后的 .ap20 文件。");
            sb.AppendLine("3. 请在配置页继续补充设备、PROFINET 名称、IP、I/Q 起始地址，并导入 IO/伺服/电机/报警 CSV。");
            sb.AppendLine("4. GSD 安装、硬件设备新增、接口分配和伺服报文切换仍需在 TIA Portal 中确认；工具会在生成计划中校验配置并输出待确认项。");
            sb.AppendLine();
            sb.AppendLine("项目名称: " + job.ProjectName);
            sb.AppendLine("PLC 名称: " + (job.Project?.PlcName ?? string.Empty));
            sb.AppendLine("PLC IP: " + (job.Project?.PlcIpAddress ?? string.Empty));
            sb.AppendLine("标准工程: " + sourceProject);
            sb.AppendLine("复制目录: " + job.NetworkProjectDirectory);
            sb.AppendLine("项目文件: " + job.TemplateProject);
            sb.AppendLine();
            sb.AppendLine("计划中的网络设备:");
            foreach (var device in job.Devices ?? new List<DeviceRequest>())
            {
                sb.AppendLine($"- {device.Name} | {device.DeviceType} | PROFINET={device.ProfinetName} | IP={device.IpAddress} | I={device.InputStart} | Q={device.OutputStart}");
            }
            if (job.Devices == null || job.Devices.Count == 0)
            {
                sb.AppendLine("- 暂无设备，请在配置页添加或通过 CSV 导入。");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(job.NetworkPreparationReport));
            File.WriteAllText(job.NetworkPreparationReport, sb.ToString(), Encoding.UTF8);
        }
        // ── 路径 ──
        private string GetProjectDir(string id) => Path.Combine(RootDir, id);
        private string GetProjectJsonPath(string id) => Path.Combine(GetProjectDir(id), "project.json");
    }

    public class ProjectCreationResult
    {
        public string Id { get; set; }
        public string ProjectName { get; set; }
        public string CopiedProjectPath { get; set; }
        public string CopiedProjectDirectory { get; set; }
        public string NetworkPreparationReport { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }
    public class ProjectSummary
    {
        public string Id { get; set; }
        public string ProjectName { get; set; }
        public int StationCount { get; set; }
        public int IoCount { get; set; }
        public int ServoCount { get; set; }
        public int MotorCount { get; set; }
        public int CylinderCount { get; set; }
        public string ModifiedAt { get; set; }
        public string TemplateProject { get; set; }
        public string NetworkProjectDirectory { get; set; }
    }

    public class ImportResult
    {
        public bool Success { get; set; }
        public int Imported { get; set; }
        public string Error { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }
}




