using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TiaAutomation.Core.Models;

namespace TiaAutomation.Openness
{
    /// <summary>
    /// 生成 FC_{station}_IO映射：物理 PLC tag ↔ 工位 GlobalDB 位 + 气缸参数初值。
    /// </summary>
    public class MappingFcWriter
    {
        public const string DefaultFcNameFormat = "FC_{station}_IO映射";

        public MappingFcResult WriteOnOpenedProject(object project, IEnumerable<StationCylinderPlan> stations, string sourceScratchDir)
        {
            var result = new MappingFcResult();
            var list = (stations ?? Enumerable.Empty<StationCylinderPlan>()).ToList();
            if (list.Count == 0)
            {
                result.Success = true;
                result.Diagnostic = "No stationCylinders to map.";
                return result;
            }

            try
            {
                foreach (var sc in list)
                {
                    var fcName = DefaultFcNameFormat.Replace("{station}", sc.StationId);
                    var sclText = BuildSclSource(sc, fcName);
                    if (SclSourceImporter.Import(project, fcName, sclText, sourceScratchDir, out var sclPath, out var warning))
                    {
                        result.GeneratedFcs.Add(new MappingFcEntry { FcName = fcName, Station = sc.StationId, SclPath = sclPath });
                    }
                    else
                    {
                        result.Warnings.Add(warning ?? $"导入 {fcName} 失败");
                    }
                }

                result.Success = true;
                result.Diagnostic = "IO 映射 FC 已生成。";
            }
            catch (Exception ex)
            {
                result.Success = false;
                var b = ex.GetBaseException();
                result.Diagnostic = b.GetType().Name + ": " + b.Message + "\n" + b.StackTrace;
            }

            return result;
        }

        private static string BuildSclSource(StationCylinderPlan sc, string fcName)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"FUNCTION \"{fcName}\" : Void");
            sb.AppendLine("{ S7_Optimized_Access := 'TRUE' }");
            sb.AppendLine("VERSION : 0.1");
            sb.AppendLine();
            sb.AppendLine("BEGIN");
            sb.AppendLine($"// 工位 {sc.StationId} ({sc.StationName}) 物理 IO -> 工位 DB 位 映射 + 气缸参数初值");
            sb.AppendLine("// 工具自动生成，勿手工编辑。重新执行 apply 会覆盖此 FC。");
            sb.AppendLine();
            sb.AppendLine("// === 气缸参数初值（每周期赋值；如需仅首次赋值，可包裹 IF 首扫描位 THEN ... END_IF） ===");
            foreach (var c in sc.Cylinders ?? new List<StationCylinder>())
            {
                int i = c.Index - 1;
                if (c.SettleTimeMs > 0)
                {
                    sb.AppendLine($"\"{sc.Param1Db}\".data.\"到位时间\"[{i}] := {TiaTimeLiteral(c.SettleTimeMs)};");
                }
                sb.AppendLine($"\"{sc.Param1Db}\".data.\"屏蔽\"[{i}] := {(c.Shield ? "TRUE" : "FALSE")};");
                if (c.AlarmTimeMs > 0)
                {
                    sb.AppendLine($"\"{sc.Param2Db}\".data.\"报错时间\"[{i}] := {TiaTimeLiteral(c.AlarmTimeMs)};");
                }
                sb.AppendLine($"\"{sc.Param2Db}\".data.\"安全\"[{i}] := {(c.Safe ? "TRUE" : "FALSE")};");
            }
            sb.AppendLine();
            sb.AppendLine("// === 物理 IO -> 工位 DB 位 ===");
            foreach (var c in sc.Cylinders ?? new List<StationCylinder>())
            {
                if (!string.IsNullOrWhiteSpace(c.ExtendIo))
                {
                    sb.AppendLine($"\"{sc.StationIDb}\".data.\"气缸感应出_{c.Index}\" := \"{c.ExtendIo}\";");
                }
                if (!string.IsNullOrWhiteSpace(c.RetractIo))
                {
                    sb.AppendLine($"\"{sc.StationIDb}\".data.\"气缸感应回_{c.Index}\" := \"{c.RetractIo}\";");
                }
                if (!string.IsNullOrWhiteSpace(c.ExtendOut))
                {
                    sb.AppendLine($"\"{c.ExtendOut}\" := \"{sc.StationQDb}\".data.\"气缸出_{c.Index}\";");
                }
                if (!string.IsNullOrWhiteSpace(c.RetractOut))
                {
                    sb.AppendLine($"\"{c.RetractOut}\" := \"{sc.StationQDb}\".data.\"气缸回_{c.Index}\";");
                }
            }
            sb.AppendLine("END_FUNCTION");
            return sb.ToString();
        }

        private static string TiaTimeLiteral(int ms)
        {
            if (ms < 1000) return $"T#{ms}ms";
            int s = ms / 1000;
            int rem = ms % 1000;
            if (rem == 0) return $"T#{s}s";
            return $"T#{s}s_{rem}ms";
        }
    }

    public class MappingFcResult
    {
        public bool Success { get; set; }
        public string Diagnostic { get; set; }
        public List<MappingFcEntry> GeneratedFcs { get; set; } = new List<MappingFcEntry>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class MappingFcEntry
    {
        public string FcName { get; set; }
        public string Station { get; set; }
        public string SclPath { get; set; }
    }
}
