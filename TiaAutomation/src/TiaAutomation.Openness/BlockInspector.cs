using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

namespace TiaAutomation.Openness
{
    public class BlockInspector
    {
        public BlockInspectionResult InspectBlock(string projectPath, string blockName, string exportDirectory, string opennessAssemblyPath = null)
        {
            var results = InspectBlocks(projectPath, new[] { blockName }, exportDirectory, opennessAssemblyPath);
            return results.Count > 0 ? results[0] : new BlockInspectionResult { ProjectPath = projectPath, BlockName = blockName, Diagnostic = "No result returned." };
        }

        public List<BlockInspectionResult> InspectBlocks(string projectPath, IEnumerable<string> blockNames, string exportDirectory, string opennessAssemblyPath = null)
        {
            return InspectBlocks(projectPath, blockNames, exportDirectory, opennessAssemblyPath, false);
        }

        public List<BlockInspectionResult> InspectBlocks(string projectPath, IEnumerable<string> blockNames, string exportDirectory, string opennessAssemblyPath, bool reuseExistingXml)
        {
            var results = new List<BlockInspectionResult>();
            var names = (blockNames ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            if (names.Count == 0)
            {
                return results;
            }

            // If every requested block already has an XML on disk, skip opening TIA Portal.
            if (reuseExistingXml)
            {
                Directory.CreateDirectory(exportDirectory);
                var allCached = names.All(n => File.Exists(Path.Combine(exportDirectory, MakeFileSafe(n) + ".xml")));
                if (allCached)
                {
                    foreach (var name in names)
                    {
                        var result = new BlockInspectionResult
                        {
                            ProjectPath = projectPath,
                            BlockName = name,
                            ExportXmlPath = Path.Combine(exportDirectory, MakeFileSafe(name) + ".xml")
                        };
                        try
                        {
                            ParseInterfaceFromXml(result.ExportXmlPath, result);
                            ParseHeaderFromXml(result.ExportXmlPath, result);
                            result.Success = true;
                            result.Diagnostic = "Parsed from cached XML.";
                        }
                        catch (Exception ex)
                        {
                            result.Diagnostic = ex.GetBaseException().Message;
                        }
                        results.Add(result);
                    }
                    return results;
                }
            }

            using (var session = new TiaPortalSession(opennessAssemblyPath))
            {
                if (!session.IsAvailable(out var diagnostic))
                {
                    foreach (var name in names)
                    {
                        results.Add(new BlockInspectionResult { ProjectPath = projectPath, BlockName = name, Diagnostic = diagnostic });
                    }
                    return results;
                }

                object project;
                try
                {
                    project = session.OpenProject(Path.GetFullPath(projectPath));
                }
                catch (Exception ex)
                {
                    foreach (var name in names)
                    {
                        results.Add(new BlockInspectionResult { ProjectPath = projectPath, BlockName = name, Diagnostic = ex.GetBaseException().Message });
                    }
                    return results;
                }

                var plcSoftware = PlcSoftwareLocator.FindFirstPlcSoftware(project);
                Directory.CreateDirectory(exportDirectory);

                foreach (var name in names)
                {
                    var result = new BlockInspectionResult { ProjectPath = projectPath, BlockName = name };
                    try
                    {
                        if (plcSoftware == null)
                        {
                            result.Diagnostic = "No PLC software container was found.";
                            results.Add(result);
                            continue;
                        }

                        var match = FindBlock(OpennessReflection.ReadProperty(plcSoftware, "BlockGroup"), name)
                            ?? FindType(OpennessReflection.ReadProperty(plcSoftware, "TypeGroup"), name);
                        if (match == null)
                        {
                            result.Diagnostic = $"Block or PLC data type '{name}' was not found in PLC.";
                            results.Add(result);
                            continue;
                        }

                        PopulateBlockMetadata(match, result);
                        var safe = MakeFileSafe(result.BlockName ?? name);
                        var xmlPath = Path.Combine(exportDirectory, safe + ".xml");
                        if (!ExportBlockToXml(match, xmlPath, result))
                        {
                            results.Add(result);
                            continue;
                        }

                        result.ExportXmlPath = xmlPath;
                        ParseInterfaceFromXml(xmlPath, result);
                        if (string.Equals(result.BlockType, "FB", StringComparison.OrdinalIgnoreCase))
                        {
                            CollectInstanceDbs(plcSoftware, result);
                        }

                        result.Success = true;
                        result.Diagnostic = "Block inspected and exported.";
                    }
                    catch (Exception ex)
                    {
                        result.Success = false;
                        result.Diagnostic = ex.GetBaseException().Message;
                    }

                    results.Add(result);
                }
            }

            return results;
        }

        private static object FindBlock(object group, string name)
        {
            if (group == null)
            {
                return null;
            }

            foreach (var block in OpennessReflection.ReadEnumerableProperty(group, "Blocks") ?? new object[0])
            {
                if (string.Equals(OpennessReflection.ReadProperty(block, "Name") as string, name, StringComparison.OrdinalIgnoreCase))
                {
                    return block;
                }
            }

            foreach (var sub in OpennessReflection.ReadEnumerableProperty(group, "Groups") ?? new object[0])
            {
                var found = FindBlock(sub, name);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static object FindType(object group, string name)
        {
            if (group == null)
            {
                return null;
            }

            foreach (var type in OpennessReflection.ReadEnumerableProperty(group, "Types") ?? new object[0])
            {
                if (string.Equals(OpennessReflection.ReadProperty(type, "Name") as string, name, StringComparison.OrdinalIgnoreCase))
                {
                    return type;
                }
            }

            foreach (var sub in OpennessReflection.ReadEnumerableProperty(group, "Groups") ?? new object[0])
            {
                var found = FindType(sub, name);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static void PopulateBlockMetadata(object block, BlockInspectionResult result)
        {
            result.BlockName = OpennessReflection.ReadProperty(block, "Name") as string ?? result.BlockName;
            result.BlockType = block.GetType().Name;
            result.Number = OpennessReflection.ReadProperty(block, "Number") as int?;
            result.ProgrammingLanguage = OpennessReflection.ReadProperty(block, "ProgrammingLanguage")?.ToString();
        }

        private static bool ExportBlockToXml(object block, string xmlPath, BlockInspectionResult result)
        {
            if (File.Exists(xmlPath))
            {
                File.Delete(xmlPath);
            }

            var assembly = block.GetType().Assembly;
            var fileInfoArg = new FileInfo(xmlPath);

            var exportMethods = block.GetType().GetMethods()
                .Where(m => m.Name == "Export" && !m.IsGenericMethodDefinition)
                .ToList();

            foreach (var method in exportMethods)
            {
                var parameters = method.GetParameters();
                if (parameters.Length == 0)
                {
                    continue;
                }

                if (!typeof(FileInfo).IsAssignableFrom(parameters[0].ParameterType))
                {
                    continue;
                }

                object[] args;
                if (parameters.Length == 1)
                {
                    args = new object[] { fileInfoArg };
                }
                else if (parameters.Length == 2 && parameters[1].ParameterType.IsEnum)
                {
                    var enumType = parameters[1].ParameterType;
                    var preferred = TryGetEnumValue(enumType, "WithReadOnly")
                        ?? TryGetEnumValue(enumType, "None")
                        ?? Enum.GetValues(enumType).GetValue(0);
                    args = new object[] { fileInfoArg, preferred };
                }
                else
                {
                    continue;
                }

                try
                {
                    method.Invoke(block, args);
                    if (File.Exists(xmlPath))
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    result.Diagnostic = ex.GetBaseException().Message;
                }
            }

            if (string.IsNullOrEmpty(result.Diagnostic))
            {
                result.Diagnostic = "Block.Export(FileInfo[, ExportOptions]) not invokable on this Openness version.";
            }
            return false;
        }

        private static object TryGetEnumValue(Type enumType, string name)
        {
            try
            {
                return Enum.IsDefined(enumType, name) ? Enum.Parse(enumType, name) : null;
            }
            catch
            {
                return null;
            }
        }

        private static string MakeFileSafe(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        private static void ParseInterfaceFromXml(string xmlPath, BlockInspectionResult result)
        {
            var doc = XDocument.Load(xmlPath);
            var sections = doc.Descendants().Where(e => e.Name.LocalName == "Section").ToList();
            foreach (var section in sections)
            {
                var sectionName = (string)section.Attribute("Name");
                var bucket = ResolveBucket(sectionName, result);
                foreach (var member in section.Elements().Where(e => e.Name.LocalName == "Member"))
                {
                    bucket.Add(ParseMember(member));
                }
            }
        }

        private static List<BlockMember> ResolveBucket(string sectionName, BlockInspectionResult result)
        {
            switch (sectionName)
            {
                case "Input": return result.Inputs;
                case "Output": return result.Outputs;
                case "InOut": return result.InOuts;
                case "Static": return result.Statics;
                case "Temp": return result.Temps;
                case "Constant": return result.Constants;
                case "Return": return result.Returns;
                // UDT/PlcStruct exports use Section Name="None"; treat as Members and stash in Statics.
                default: return result.Statics;
            }
        }

        private static BlockMember ParseMember(XElement element)
        {
            var member = new BlockMember
            {
                Name = (string)element.Attribute("Name"),
                DataType = (string)element.Attribute("Datatype"),
                Default = element.Elements().FirstOrDefault(e => e.Name.LocalName == "StartValue")?.Value
            };

            var commentElement = element.Elements().FirstOrDefault(e => e.Name.LocalName == "Comment");
            if (commentElement != null)
            {
                var multi = commentElement.Elements().FirstOrDefault();
                member.Comment = multi != null ? multi.Value : commentElement.Value;
            }

            var children = element.Elements().Where(e => e.Name.LocalName == "Member").ToList();
            if (children.Count > 0)
            {
                member.IsStruct = true;
                member.Members.AddRange(children.Select(ParseMember));
            }

            return member;
        }

        private static void ParseHeaderFromXml(string xmlPath, BlockInspectionResult result)
        {
            var doc = XDocument.Load(xmlPath);
            var rootEntity = doc.Descendants().FirstOrDefault(e => e.Name.LocalName.StartsWith("SW."));
            if (rootEntity == null)
            {
                return;
            }

            // SW.Blocks.FB / SW.Blocks.FC / SW.Blocks.GlobalDB / SW.Types.PlcStruct etc.
            var typeName = rootEntity.Name.LocalName.Split('.').Last();
            result.BlockType = typeName;

            var attrList = rootEntity.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
            if (attrList != null)
            {
                foreach (var child in attrList.Elements())
                {
                    if (child.Name.LocalName == "Number" && int.TryParse(child.Value, out var n))
                    {
                        result.Number = n;
                    }
                    else if (child.Name.LocalName == "ProgrammingLanguage")
                    {
                        result.ProgrammingLanguage = child.Value;
                    }
                    else if (child.Name.LocalName == "Name" && string.IsNullOrWhiteSpace(result.BlockName))
                    {
                        result.BlockName = child.Value;
                    }
                }
            }
        }

        private static void CollectInstanceDbs(object plcSoftware, BlockInspectionResult result)
        {
            var rootGroup = OpennessReflection.ReadProperty(plcSoftware, "BlockGroup");
            WalkBlocks(rootGroup, block =>
            {
                if (string.Equals(block.GetType().Name, "InstanceDB", StringComparison.OrdinalIgnoreCase))
                {
                    var instanceOf = OpennessReflection.ReadProperty(block, "InstanceOfName") as string
                        ?? OpennessReflection.ReadProperty(block, "InstanceOf") as string;
                    if (string.Equals(instanceOf, result.BlockName, StringComparison.OrdinalIgnoreCase))
                    {
                        var name = OpennessReflection.ReadProperty(block, "Name") as string;
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            result.InstanceDbs.Add(name);
                        }
                    }
                }
            });
        }

        private static void WalkBlocks(object group, Action<object> action)
        {
            if (group == null)
            {
                return;
            }

            foreach (var block in OpennessReflection.ReadEnumerableProperty(group, "Blocks") ?? new object[0])
            {
                action(block);
            }

            foreach (var sub in OpennessReflection.ReadEnumerableProperty(group, "Groups") ?? new object[0])
            {
                WalkBlocks(sub, action);
            }
        }
    }
}
