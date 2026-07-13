using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using TiaAutomation.Core.Models;

namespace TiaAutomation.Openness
{
    /// <summary>
    /// 写 FB22 气缸块对应的工位 DB 与 InstanceDB。每工位生成：
    ///   - 5 个 GlobalDB（"工位I"/"工位Q"/"气缸参数1"/"气缸参数2"/"报警D"）
    ///   - 1 个 FB22 InstanceDB
    /// 通过 Openness XML 导入实现，兼容 V14+。
    /// </summary>
    public class DbWriter
    {
        public DbWriteResult WriteStationCylinders(string projectPath, IEnumerable<StationCylinderPlan> plans, string xmlScratchDir, string opennessAssemblyPath = null)
        {
            var result = new DbWriteResult { ProjectPath = projectPath };
            var stations = (plans ?? Enumerable.Empty<StationCylinderPlan>()).ToList();
            if (stations.Count == 0)
            {
                result.Success = true;
                result.Diagnostic = "No stationCylinders to write.";
                return result;
            }

            try
            {
                using (var session = new TiaPortalSession(opennessAssemblyPath))
                {
                    if (!session.IsAvailable(out var diagnostic))
                    {
                        result.Diagnostic = diagnostic;
                        return result;
                    }

                    var project = session.OpenProject(Path.GetFullPath(projectPath));
                    var inner = WriteOnOpenedProject(project, stations, xmlScratchDir);
                    session.SaveProject(project);
                    return inner;
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Diagnostic = ex.GetBaseException().Message;
                return result;
            }
        }

        public DbWriteResult WriteOnOpenedProject(object project, IEnumerable<StationCylinderPlan> plans, string xmlScratchDir)
        {
            var result = new DbWriteResult();
            var stations = (plans ?? Enumerable.Empty<StationCylinderPlan>()).ToList();
            if (stations.Count == 0)
            {
                result.Success = true;
                result.Diagnostic = "No stationCylinders to write.";
                return result;
            }

            xmlScratchDir = Path.GetFullPath(xmlScratchDir);
            Directory.CreateDirectory(xmlScratchDir);

            try
            {
                var plcSoftware = PlcSoftwareLocator.FindFirstPlcSoftware(project);
                if (plcSoftware == null)
                {
                    result.Diagnostic = "No PLC software container was found.";
                    return result;
                }

                var blockGroup = OpennessReflection.ReadProperty(plcSoftware, "BlockGroup");
                var blocks = OpennessReflection.ReadProperty(blockGroup, "Blocks");
                if (blocks == null)
                {
                    result.Diagnostic = "BlockGroup.Blocks missing.";
                    return result;
                }

                foreach (var sc in stations)
                {
                    WriteStation(blockGroup, blocks, sc, xmlScratchDir, result);
                }

                result.Success = true;
                result.Diagnostic = "Station cylinder DBs written.";
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Diagnostic = ex.GetBaseException().Message;
            }

            return result;
        }

        private void WriteStation(object blockGroup, object blocks, StationCylinderPlan sc, string xmlScratchDir, DbWriteResult result)
        {
            // 5 GlobalDB based on UDTs
            EnsureGlobalDbFromUdt(blockGroup, blocks, sc.StationIDb, "工位I", sc, xmlScratchDir, result);
            EnsureGlobalDbFromUdt(blockGroup, blocks, sc.StationQDb, "工位Q", sc, xmlScratchDir, result);
            EnsureGlobalDbFromUdt(blockGroup, blocks, sc.AlarmDb, "报警D", sc, xmlScratchDir, result);

            EnsureCylinderParamDb(blockGroup, blocks, sc.Param1Db, "气缸参数1", sc, isParam1: true, xmlScratchDir: xmlScratchDir, result: result);
            EnsureCylinderParamDb(blockGroup, blocks, sc.Param2Db, "气缸参数2", sc, isParam1: false, xmlScratchDir: xmlScratchDir, result: result);

            // FB22 InstanceDB
            EnsureInstanceDb(blockGroup, blocks, sc.InstanceDb, sc.CylinderFb ?? "气缸块", sc, xmlScratchDir, result);
        }

        private static void EnsureGlobalDbFromUdt(object blockGroup, object blocks, string dbName, string udtName, StationCylinderPlan sc, string xmlScratchDir, DbWriteResult result)
        {
            if (FindBlockByName(blockGroup, dbName) != null)
            {
                result.ExistingBlocks.Add(new DbCreated { Name = dbName, Type = "GlobalDB", TypeOf = udtName, Station = sc.StationId });
                return;
            }

            var xml = BuildUdtBackedGlobalDbXml(dbName, udtName, null);
            var xmlPath = WriteScratch(xmlScratchDir, dbName, xml);
            ImportXml(blocks, xmlPath, dbName, udtName, sc.StationId, "GlobalDB", result);        }

        private static void EnsureCylinderParamDb(object blockGroup, object blocks, string dbName, string udtName, StationCylinderPlan sc, bool isParam1, string xmlScratchDir, DbWriteResult result)
        {
            if (FindBlockByName(blockGroup, dbName) != null)
            {
                result.ExistingBlocks.Add(new DbCreated { Name = dbName, Type = "GlobalDB", TypeOf = udtName, Station = sc.StationId });
                return;
            }

            // 暂不通过 XML Subelement Path 写初值（V20 schema 要求嵌套，可靠性差）。
            // 创建空的 UDT-DB；初值由生成的 IO 映射 FC 顶部赋值。
            var xml = BuildUdtBackedGlobalDbXml(dbName, udtName, null);
            var xmlPath = WriteScratch(xmlScratchDir, dbName, xml);
            ImportXml(blocks, xmlPath, dbName, udtName, sc.StationId, "GlobalDB", result);        }

        private static void EnsureInstanceDb(object blockGroup, object blocks, string dbName, string fbName, StationCylinderPlan sc, string xmlScratchDir, DbWriteResult result)
        {
            if (FindBlockByName(blockGroup, dbName) != null)
            {
                result.ExistingBlocks.Add(new DbCreated { Name = dbName, Type = "InstanceDB", TypeOf = fbName, Station = sc.StationId });
                return;
            }

            var xml = BuildInstanceDbXml(dbName, fbName);
            var xmlPath = WriteScratch(xmlScratchDir, dbName, xml);
            ImportXml(blocks, xmlPath, dbName, fbName, sc.StationId, "InstanceDB", result);
        }

        private static void ImportXml(object blocks, string xmlPath, string blockName, string typeOf, string stationId, string blockType, DbWriteResult result)
        {
            var info = new FileInfo(xmlPath);
            var assembly = blocks.GetType().Assembly;
            var importOptionsType = assembly.GetType("Siemens.Engineering.ImportOptions", false);

            var importMethod = blocks.GetType().GetMethods()
                .FirstOrDefault(m => m.Name == "Import" && m.GetParameters().Length == 2
                    && typeof(FileInfo).IsAssignableFrom(m.GetParameters()[0].ParameterType)
                    && m.GetParameters()[1].ParameterType.IsEnum);
            if (importMethod == null)
            {
                result.Warnings.Add($"无法导入 {blockName}：未找到 Blocks.Import(FileInfo, ImportOptions)。");
                return;
            }

            var optionsEnumType = importMethod.GetParameters()[1].ParameterType;
            var optionValue = TryEnumValue(optionsEnumType, "Override")
                ?? TryEnumValue(optionsEnumType, "OverwriteAll")
                ?? TryEnumValue(optionsEnumType, "None")
                ?? Enum.GetValues(optionsEnumType).GetValue(0);

            try
            {
                importMethod.Invoke(blocks, new object[] { info, optionValue });
                result.CreatedBlocks.Add(new DbCreated
                {
                    Name = blockName,
                    Type = blockType,
                    TypeOf = typeOf,
                    Station = stationId,
                    XmlPath = xmlPath
                });
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"导入 {blockName} 失败：{ex.GetBaseException().Message}");
            }
        }

        private static object FindBlockByName(object blockGroup, string name)
        {
            if (blockGroup == null || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            foreach (var b in OpennessReflection.ReadEnumerableProperty(blockGroup, "Blocks") ?? new object[0])
            {
                if (string.Equals(OpennessReflection.ReadProperty(b, "Name") as string, name, StringComparison.OrdinalIgnoreCase))
                {
                    return b;
                }
            }

            foreach (var sub in OpennessReflection.ReadEnumerableProperty(blockGroup, "Groups") ?? new object[0])
            {
                var found = FindBlockByName(sub, name);
                if (found != null)
                {
                    return found;
                }
            }
            return null;
        }

        private static object TryEnumValue(Type enumType, string name)
        {
            try
            {
                return Enum.IsDefined(enumType, name) ? Enum.Parse(enumType, name) : null;
            }
            catch { return null; }
        }

        private static string WriteScratch(string dir, string fileBase, string xml)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                fileBase = fileBase.Replace(c, '_');
            }
            var path = Path.GetFullPath(Path.Combine(dir, fileBase + ".xml"));
            File.WriteAllText(path, xml, new UTF8Encoding(true));
            return path;
        }

        private static string TiaTimeLiteral(int ms)
        {
            // T#3s, T#500ms, T#1m30s 等。简化：小于 1000ms 用 ms，否则用 s + 余 ms。
            if (ms < 1000) return $"T#{ms}ms";
            int s = ms / 1000;
            int rem = ms % 1000;
            if (rem == 0) return $"T#{s}s";
            return $"T#{s}s_{rem}ms";
        }

        // -------- XML builders --------

        private const string XmlNs = "";

        private static string BuildUdtBackedGlobalDbXml(string dbName, string udtName, IList<KeyValuePair<string, string>> subelements)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<Document>");
            sb.AppendLine("  <Engineering version=\"V20\" />");
            sb.AppendLine("  <SW.Blocks.GlobalDB ID=\"0\">");
            sb.AppendLine("    <AttributeList>");
            sb.AppendLine("      <Interface><Sections xmlns=\"http://www.siemens.com/automation/Openness/SW/Interface/v5\">");
            sb.AppendLine("        <Section Name=\"Static\">");
            if (subelements != null && subelements.Count > 0)
            {
                sb.AppendLine($"          <Member Name=\"data\" Datatype=\"&quot;{Escape(udtName)}&quot;\">");
                foreach (var kv in subelements)
                {
                    sb.AppendLine($"            <Subelement Path=\"{EscapeAttr(kv.Key)}\">");
                    sb.AppendLine($"              <StartValue>{Escape(kv.Value)}</StartValue>");
                    sb.AppendLine("            </Subelement>");
                }
                sb.AppendLine("          </Member>");
            }
            else
            {
                sb.AppendLine($"          <Member Name=\"data\" Datatype=\"&quot;{Escape(udtName)}&quot;\" />");
            }
            sb.AppendLine("        </Section>");
            sb.AppendLine("      </Sections></Interface>");
            sb.AppendLine("      <MemoryLayout>Optimized</MemoryLayout>");
            sb.AppendLine($"      <Name>{Escape(dbName)}</Name>");
            sb.AppendLine("      <Namespace />");
            sb.AppendLine("      <Number>0</Number>");
            sb.AppendLine("      <ProgrammingLanguage>DB</ProgrammingLanguage>");
            sb.AppendLine("    </AttributeList>");
            sb.AppendLine("    <ObjectList />");
            sb.AppendLine("  </SW.Blocks.GlobalDB>");
            sb.AppendLine("</Document>");
            return sb.ToString();
        }

        private static string BuildInstanceDbXml(string dbName, string fbName)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<Document>");
            sb.AppendLine("  <Engineering version=\"V20\" />");
            sb.AppendLine("  <SW.Blocks.InstanceDB ID=\"0\">");
            sb.AppendLine("    <AttributeList>");
            sb.AppendLine($"      <InstanceOfName>{Escape(fbName)}</InstanceOfName>");
            sb.AppendLine("      <InstanceOfType>FB</InstanceOfType>");
            sb.AppendLine("      <MemoryLayout ReadOnly=\"true\">Optimized</MemoryLayout>");
            sb.AppendLine($"      <Name>{Escape(dbName)}</Name>");
            sb.AppendLine("      <Namespace />");
            sb.AppendLine("      <Number>0</Number>");
            sb.AppendLine("      <ProgrammingLanguage>DB</ProgrammingLanguage>");
            sb.AppendLine("    </AttributeList>");
            sb.AppendLine("    <ObjectList />");
            sb.AppendLine("  </SW.Blocks.InstanceDB>");
            sb.AppendLine("</Document>");
            return sb.ToString();
        }

        private static string Escape(string s)
        {
            return new XText(s ?? string.Empty).ToString();
        }

        private static string EscapeAttr(string s)
        {
            if (s == null) return string.Empty;
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }
    }
}
