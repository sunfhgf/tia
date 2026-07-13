using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TiaAutomation.Core.Models;

namespace TiaAutomation.Openness
{
    /// <summary>
    /// 按工位生成 FC_{station}_报警：注释每条报警来源、文本、等级，
    /// 占位 SCL 等用户填入实际报警逻辑（写报警 DB 位等）。
    /// </summary>
    public class AlarmFcWriter
    {
        public const string DefaultFcNameFormat = "FC_{station}_报警";

        public AlarmFcResult WriteOnOpenedProject(object project, IEnumerable<AlarmRequest> alarms, string sourceScratchDir)
        {
            var result = new AlarmFcResult();
            var list = (alarms ?? Enumerable.Empty<AlarmRequest>()).ToList();
            if (list.Count == 0)
            {
                result.Success = true;
                result.Diagnostic = "No alarms to map.";
                return result;
            }

            try
            {
                foreach (var group in list.Where(a => !string.IsNullOrWhiteSpace(a.Station)).GroupBy(a => a.Station))
                {
                    var station = group.Key;
                    var fcName = DefaultFcNameFormat.Replace("{station}", station);
                    var sclText = BuildSclSource(fcName, station, group.ToList());
                    if (SclSourceImporter.Import(project, fcName, sclText, sourceScratchDir, out var sclPath, out var warning))
                    {
                        result.GeneratedFcs.Add(new AlarmFcEntry { FcName = fcName, Station = station, SclPath = sclPath, AlarmCount = group.Count() });
                    }
                    else
                    {
                        result.Warnings.Add(warning ?? $"导入 {fcName} 失败");
                    }
                }

                result.Success = true;
                result.Diagnostic = "报警 FC 骨架已生成。";
            }
            catch (Exception ex)
            {
                result.Success = false;
                var b = ex.GetBaseException();
                result.Diagnostic = b.GetType().Name + ": " + b.Message + "\n" + b.StackTrace;
            }

            return result;
        }

        private static string BuildSclSource(string fcName, string station, List<AlarmRequest> alarms)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"FUNCTION \"{fcName}\" : Void");
            sb.AppendLine("{ S7_Optimized_Access := 'TRUE' }");
            sb.AppendLine("VERSION : 0.1");
            sb.AppendLine();
            sb.AppendLine("BEGIN");
            sb.AppendLine($"// 工位 {station} 报警逻辑骨架，工具自动生成。");
            sb.AppendLine("// 请按工程标准把每条报警源连接到工位报警 DB 的相应位。");
            sb.AppendLine();
            for (int i = 0; i < alarms.Count; i++)
            {
                var a = alarms[i];
                sb.AppendLine($"// === 报警 #{i + 1}：{a.SourceType} {a.Source} ===");
                sb.AppendLine($"//   等级(Level) : {a.Level}");
                sb.AppendLine($"//   文本(Text)  : {a.Text}");
                sb.AppendLine();
            }
            sb.AppendLine("    ; // TODO: 在此填入报警赋值（如 \"DB_{station}_报警\".data.\"位名\" := <来源条件>;）");
            sb.AppendLine("END_FUNCTION");
            return sb.ToString();
        }
    }

    public class AlarmFcResult
    {
        public bool Success { get; set; }
        public string Diagnostic { get; set; }
        public List<AlarmFcEntry> GeneratedFcs { get; set; } = new List<AlarmFcEntry>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class AlarmFcEntry
    {
        public string FcName { get; set; }
        public string Station { get; set; }
        public string SclPath { get; set; }
        public int AlarmCount { get; set; }
    }
}
