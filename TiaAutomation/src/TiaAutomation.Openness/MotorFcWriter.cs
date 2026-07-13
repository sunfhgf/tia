using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TiaAutomation.Core.Models;

namespace TiaAutomation.Openness
{
    /// <summary>
    /// 按工位生成 FC_{station}_电机：注释每台电机的运行输出和故障输入，
    /// 提供占位 SCL 等用户填入电机块调用。FC 体内仅含空语句以确保编译通过。
    /// </summary>
    public class MotorFcWriter
    {
        public const string DefaultFcNameFormat = "FC_{station}_电机";

        public MotorFcResult WriteOnOpenedProject(object project, IEnumerable<MotorRequest> motors, string sourceScratchDir)
        {
            var result = new MotorFcResult();
            var list = (motors ?? Enumerable.Empty<MotorRequest>()).ToList();
            if (list.Count == 0)
            {
                result.Success = true;
                result.Diagnostic = "No motors to map.";
                return result;
            }

            try
            {
                foreach (var group in list.Where(m => !string.IsNullOrWhiteSpace(m.Station)).GroupBy(m => m.Station))
                {
                    var station = group.Key;
                    var fcName = DefaultFcNameFormat.Replace("{station}", station);
                    var sclText = BuildSclSource(fcName, station, group.ToList());
                    if (SclSourceImporter.Import(project, fcName, sclText, sourceScratchDir, out var sclPath, out var warning))
                    {
                        result.GeneratedFcs.Add(new MotorFcEntry { FcName = fcName, Station = station, SclPath = sclPath, MotorCount = group.Count() });
                    }
                    else
                    {
                        result.Warnings.Add(warning ?? $"导入 {fcName} 失败");
                    }
                }

                result.Success = true;
                result.Diagnostic = "电机 FC 骨架已生成。";
            }
            catch (Exception ex)
            {
                result.Success = false;
                var b = ex.GetBaseException();
                result.Diagnostic = b.GetType().Name + ": " + b.Message + "\n" + b.StackTrace;
            }

            return result;
        }

        private static string BuildSclSource(string fcName, string station, List<MotorRequest> motors)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"FUNCTION \"{fcName}\" : Void");
            sb.AppendLine("{ S7_Optimized_Access := 'TRUE' }");
            sb.AppendLine("VERSION : 0.1");
            sb.AppendLine();
            sb.AppendLine("BEGIN");
            sb.AppendLine($"// 工位 {station} 电机调用骨架，工具自动生成。");
            sb.AppendLine("// 请按照标准程序的电机块在此处填入实际调用，参数已在下方注释。");
            sb.AppendLine();
            foreach (var m in motors)
            {
                sb.AppendLine($"// === 电机 {m.Name} ({m.Type}) ===");
                sb.AppendLine($"//   运行输出(RunOutput)  : {m.RunOutput}");
                sb.AppendLine($"//   故障输入(FaultInput) : {m.FaultInput}");
                sb.AppendLine($"//   关联设备(Device)     : {m.Device}");
                sb.AppendLine($"//   逻辑块(LogicBlock)   : {m.LogicBlock}");
                sb.AppendLine();
            }
            sb.AppendLine("    ; // TODO: 在此填入电机块调用");
            sb.AppendLine("END_FUNCTION");
            return sb.ToString();
        }
    }

    public class MotorFcResult
    {
        public bool Success { get; set; }
        public string Diagnostic { get; set; }
        public List<MotorFcEntry> GeneratedFcs { get; set; } = new List<MotorFcEntry>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class MotorFcEntry
    {
        public string FcName { get; set; }
        public string Station { get; set; }
        public string SclPath { get; set; }
        public int MotorCount { get; set; }
    }
}
