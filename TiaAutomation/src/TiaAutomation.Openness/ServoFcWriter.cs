using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TiaAutomation.Core.Models;

namespace TiaAutomation.Openness
{
    /// <summary>
    /// 按工位生成 FC_{station}_伺服 骨架：FC 体内为参数注释（轴名、硬件标识、报文地址），
    /// 用户在此基础上填入实际伺服块调用。SCL 仅含空语句 "; "，确保 GenerateBlocksFromSource 通过。
    /// </summary>
    public class ServoFcWriter
    {
        public const string DefaultFcNameFormat = "FC_{station}_伺服";

        public ServoFcResult WriteOnOpenedProject(object project, IEnumerable<ServoRequest> servos, string sourceScratchDir)
        {
            var result = new ServoFcResult();
            var list = (servos ?? Enumerable.Empty<ServoRequest>()).ToList();
            if (list.Count == 0)
            {
                result.Success = true;
                result.Diagnostic = "No servos to map.";
                return result;
            }

            try
            {
                foreach (var group in list.Where(s => !string.IsNullOrWhiteSpace(s.Station)).GroupBy(s => s.Station))
                {
                    var station = group.Key;
                    var fcName = DefaultFcNameFormat.Replace("{station}", station);
                    var sclText = BuildSclSource(fcName, station, group.ToList());
                    if (SclSourceImporter.Import(project, fcName, sclText, sourceScratchDir, out var sclPath, out var warning))
                    {
                        result.GeneratedFcs.Add(new ServoFcEntry { FcName = fcName, Station = station, SclPath = sclPath, ServoCount = group.Count() });
                    }
                    else
                    {
                        result.Warnings.Add(warning ?? $"导入 {fcName} 失败");
                    }
                }

                result.Success = true;
                result.Diagnostic = "伺服 FC 骨架已生成。";
            }
            catch (Exception ex)
            {
                result.Success = false;
                var b = ex.GetBaseException();
                result.Diagnostic = b.GetType().Name + ": " + b.Message + "\n" + b.StackTrace;
            }

            return result;
        }

        private static string BuildSclSource(string fcName, string station, List<ServoRequest> servos)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"FUNCTION \"{fcName}\" : Void");
            sb.AppendLine("{ S7_Optimized_Access := 'TRUE' }");
            sb.AppendLine("VERSION : 0.1");
            sb.AppendLine();
            sb.AppendLine("BEGIN");
            sb.AppendLine($"// 工位 {station} 伺服调用骨架，工具自动生成。");
            sb.AppendLine("// 请按照标准程序的伺服块在此处填入实际调用，参数已在下方注释。");
            sb.AppendLine();
            foreach (var s in servos)
            {
                sb.AppendLine($"// === 伺服 {s.Name} ===");
                sb.AppendLine($"//   轴名(AxisName)         : {s.AxisName}");
                sb.AppendLine($"//   通讯报文(Telegram)     : {s.Telegram}");
                sb.AppendLine($"//   硬件标识(HardwareId)   : {(s.HardwareId?.ToString() ?? "<未配置>")}");
                sb.AppendLine($"//   报文地址(TelAddress)   : {(s.TelegramAddress?.ToString() ?? "<未配置>")}");
                sb.AppendLine($"//   关联设备(Device)       : {s.Device}");
                sb.AppendLine($"//   逻辑块(LogicBlock)     : {s.LogicBlock}");
                sb.AppendLine();
            }
            sb.AppendLine("    ; // TODO: 在此填入伺服块调用");
            sb.AppendLine("END_FUNCTION");
            return sb.ToString();
        }
    }

    public class ServoFcResult
    {
        public bool Success { get; set; }
        public string Diagnostic { get; set; }
        public List<ServoFcEntry> GeneratedFcs { get; set; } = new List<ServoFcEntry>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class ServoFcEntry
    {
        public string FcName { get; set; }
        public string Station { get; set; }
        public string SclPath { get; set; }
        public int ServoCount { get; set; }
    }
}
