using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TiaAutomation.Core.Models;

namespace TiaAutomation.Openness
{
    public class CylinderLogicWriter
    {
        private const string BlockName = "气缸逻辑";
        private static readonly XNamespace StructuredTextNamespace =
            "http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4";

        public CylinderLogicWriteResult WriteOnOpenedProject(
            object project, ProjectSettings settings, IEnumerable<IoCommentRequest> ioComments,
            IEnumerable<IoPoint> ioPoints, string scratchDirectory)
        {
            var result = new CylinderLogicWriteResult();
            var stations = FlattenStations(settings).OrderBy(item => item.ArrayIndex).ToList();
            if (stations.Count == 0)
            {
                result.Success = true;
                result.Diagnostic = "No cylinder logic stations were configured.";
                return result;
            }

            try
            {
                var plcSoftware = PlcSoftwareLocator.FindFirstPlcSoftware(project);
                var block = FindBlock(OpennessReflection.ReadProperty(plcSoftware, "BlockGroup"), BlockName);
                if (block == null)
                {
                    result.Errors.Add($"PLC block '{BlockName}' was not found.");
                    return Complete(result);
                }

                scratchDirectory = Path.GetFullPath(scratchDirectory);
                Directory.CreateDirectory(scratchDirectory);
                var xmlPath = Path.Combine(scratchDirectory, BlockName + ".xml");
                ExportBlock(block, xmlPath);
                RewriteCylinderLogic(xmlPath, stations, BuildSignals(ioComments, ioPoints), result);
                ImportBlock(block, xmlPath);
                CompilePlcSoftware(plcSoftware, result);
                result.BlockName = BlockName;
            }
            catch (Exception ex)
            {
                result.Errors.Add(ex.GetBaseException().Message);
            }

            return Complete(result);
        }

        private static IEnumerable<ConfiguredStation> FlattenStations(ProjectSettings settings)
        {
            var units = settings?.UnitStations ?? new List<List<UnitStationSettings>>();
            var unitCount = Math.Max(1, settings?.UnitCount ?? 1);
            for (var unitIndex = 0; unitIndex < Math.Min(unitCount, units.Count); unitIndex++)
            {
                foreach (var station in units[unitIndex] ?? new List<UnitStationSettings>())
                {
                    var match = Regex.Match(station?.DataTypeName ?? string.Empty, @"工位\s*([1-9])");
                    if (!match.Success) continue;
                    yield return new ConfiguredStation
                    {
                        UnitName = unitIndex == 0 ? "设备1" : $"设备1_{unitIndex}",
                        ArrayIndex = int.Parse(match.Groups[1].Value) - 1,
                        Settings = station
                    };
                }
            }
        }

        private static void RewriteCylinderLogic(
            string xmlPath, List<ConfiguredStation> stations, List<SafetySignal> signals,
            CylinderLogicWriteResult result)
        {
            var document = XDocument.Load(xmlPath, LoadOptions.PreserveWhitespace);
            var stationCount = stations.Max(item => item.ArrayIndex) + 1;
            ResizeCylinderInstanceArray(document, stationCount);

            var structuredTexts = document.Descendants()
                .Where(element => element.Name.LocalName == "StructuredText").ToList();
            var mainNetwork = structuredTexts.FirstOrDefault(element => HasComponent(element, "气缸块")
                && element.Descendants().Any(child => child.Name.LocalName == "Token" && (string)child.Attribute("Text") == "FOR"));
            if (mainNetwork == null) throw new InvalidOperationException("Cylinder logic main network was not found.");
            UpdateStationCount(mainNetwork, stationCount, result);

            var safetyNetwork = structuredTexts.FirstOrDefault(element => HasComponent(element, "参数2")
                && (HasComponent(element, "互锁安全") || HasComponent(element, "安全")));
            if (safetyNetwork == null) throw new InvalidOperationException("Cylinder logic safety network was not found.");
            var safetyMember = HasComponent(safetyNetwork, "互锁安全") ? "互锁安全" : "安全";
            var builder = new StructuredTextBuilder();

            foreach (var station in stations)
            {
                var cylinders = CleanNames(station.Settings.CylinderNames);
                builder.Comment(station.Settings.Name ?? $"工位{station.ArrayIndex + 1}");
                builder.AssignCylinderCount(station.ArrayIndex, cylinders.Count, "单工位中气缸数量最大16");
                var extendConditions = station.Settings.CylinderExtendSafetyConditions ?? new List<string>();
                var retractConditions = station.Settings.CylinderRetractSafetyConditions ?? new List<string>();
                var configuredConditions = 0;

                for (var cylinderIndex = 0; cylinderIndex < cylinders.Count; cylinderIndex++)
                {
                    var extend = ResolveConditions(At(extendConditions, cylinderIndex), signals, station, cylinders[cylinderIndex], "出", result);
                    var retract = ResolveConditions(At(retractConditions, cylinderIndex), signals, station, cylinders[cylinderIndex], "回", result);
                    builder.AssignSafety(station.ArrayIndex, cylinderIndex * 2, safetyMember, extend, cylinders[cylinderIndex] + "出");
                    builder.AssignSafety(station.ArrayIndex, cylinderIndex * 2 + 1, safetyMember, retract, cylinders[cylinderIndex] + "回");
                    configuredConditions += extend.Count + retract.Count;
                }
                builder.BlankLine();
                result.UpdatedStations.Add(new CylinderLogicStationUpdate
                {
                    UnitName = station.UnitName,
                    StationName = station.Settings.Name,
                    StationNumber = station.ArrayIndex + 1,
                    ArrayIndex = station.ArrayIndex,
                    CylinderCount = cylinders.Count,
                    SafetyConditionCount = configuredConditions
                });
            }

            safetyNetwork.ReplaceNodes(builder.Root.Elements());
            RenumberUids(safetyNetwork);
            result.StationCount = stationCount;
            result.SafetyMemberName = safetyMember;
            document.Save(xmlPath, SaveOptions.DisableFormatting);
        }

        private static void ResizeCylinderInstanceArray(XDocument document, int stationCount)
        {
            var member = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Member"
                && string.Equals((string)element.Attribute("Name"), "气缸块", StringComparison.OrdinalIgnoreCase));
            if (member == null) throw new InvalidOperationException("Cylinder FB instance array was not found.");
            var datatype = (string)member.Attribute("Datatype") ?? string.Empty;
            var updated = Regex.Replace(datatype, @"Array\s*\[\s*\d+\s*\.\.\s*\d+\s*\]", $"Array[0..{stationCount - 1}]", RegexOptions.IgnoreCase);
            if (string.Equals(updated, datatype, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Cylinder FB instance array datatype could not be resized.");
            }
            member.SetAttributeValue("Datatype", updated);
        }

        private static void UpdateStationCount(
            XElement mainNetwork, int stationCount, CylinderLogicWriteResult result)
        {
            var lines = SplitLines(mainNetwork).ToList();
            var countLine = lines.FirstOrDefault(line => HasComponent(line, "工位数量"));
            if (countLine != null)
            {
                var constant = Constants(countLine).LastOrDefault();
                if (constant == null) throw new InvalidOperationException("Station-count assignment constant was not found.");
                constant.Value = stationCount.ToString();
                result.StationCountVariableUpdated = true;
            }

            var forLine = lines.FirstOrDefault(line => line.SelectMany(element => element.DescendantsAndSelf())
                .Any(element => element.Name.LocalName == "Token" && (string)element.Attribute("Text") == "FOR"));
            if (forLine != null)
            {
                var constants = Constants(forLine);
                if (!HasComponent(forLine, "工位数量") && constants.Count >= 2)
                {
                    constants[1].Value = (stationCount - 1).ToString();
                    result.LoopBoundUpdated = true;
                }
            }
            RenumberUids(mainNetwork);
        }

        private static List<SafetySignal> BuildSignals(
            IEnumerable<IoCommentRequest> comments, IEnumerable<IoPoint> points)
        {
            var signals = new Dictionary<string, SafetySignal>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in comments ?? Enumerable.Empty<IoCommentRequest>())
            {
                AddSignal(signals, item.Address, null, item.Comment);
            }
            foreach (var point in points ?? Enumerable.Empty<IoPoint>())
            {
                AddSignal(signals, point.Address, point.Tag, point.Comment);
            }
            return signals.Values.ToList();
        }

        private static void AddSignal(
            IDictionary<string, SafetySignal> signals, string address, string tag, string comment)
        {
            var normalized = NormalizeAddress(address);
            if (normalized == null) return;
            if (!signals.TryGetValue(normalized, out var signal))
            {
                signal = new SafetySignal
                {
                    Address = normalized,
                    TagName = string.IsNullOrWhiteSpace(tag) ? normalized.TrimStart('%').Replace('.', '_') : tag.Trim()
                };
                signals[normalized] = signal;
            }
            if (!string.IsNullOrWhiteSpace(tag)) signal.TagName = tag.Trim();
            if (!string.IsNullOrWhiteSpace(comment)) signal.Comment = comment.Trim();
        }

        private static string NormalizeAddress(string address)
        {
            var match = Regex.Match(address ?? string.Empty, @"%?\s*([IQiq])\s*(\d+)\s*\.\s*(\d+)");
            return match.Success
                ? $"%{match.Groups[1].Value.ToUpperInvariant()}{int.Parse(match.Groups[2].Value)}.{int.Parse(match.Groups[3].Value)}"
                : null;
        }

        private static List<string> ResolveConditions(
            string text, List<SafetySignal> signals, ConfiguredStation station,
            string cylinderName, string action, CylinderLogicWriteResult result)
        {
            var resolved = new List<string>();
            foreach (var raw in Regex.Split(text ?? string.Empty, @"[,，;；\r\n]+|\s+(?:AND|&&)\s+", RegexOptions.IgnoreCase))
            {
                var value = raw.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(value)) continue;
                var address = NormalizeAddress(value);
                var matches = signals.Where(signal =>
                    string.Equals(signal.Address, address, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(signal.TagName, value, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(signal.Comment, value, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 0)
                {
                    matches = signals.Where(signal => !string.IsNullOrWhiteSpace(signal.Comment)
                        && signal.Comment.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                }
                if (matches.Count == 1)
                {
                    if (!resolved.Contains(matches[0].TagName, StringComparer.OrdinalIgnoreCase)) resolved.Add(matches[0].TagName);
                }
                else
                {
                    result.Warnings.Add(matches.Count == 0
                        ? $"{station.Settings.Name} / {cylinderName}{action}: safety condition '{value}' was not found."
                        : $"{station.Settings.Name} / {cylinderName}{action}: safety condition '{value}' matched more than one IO comment.");
                }
            }
            return resolved;
        }

        private static string At(IReadOnlyList<string> values, int index)
        {
            return index < values.Count ? values[index] : null;
        }

        private static List<string> CleanNames(IEnumerable<string> names)
        {
            return (names ?? Enumerable.Empty<string>()).Select(name => (name ?? string.Empty).Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name)).Take(16).ToList();
        }

        private static IEnumerable<List<XElement>> SplitLines(XElement structuredText)
        {
            var current = new List<XElement>();
            foreach (var element in structuredText.Elements())
            {
                current.Add(element);
                if (element.Name.LocalName != "NewLine") continue;
                yield return current;
                current = new List<XElement>();
            }
            if (current.Count > 0) yield return current;
        }

        private static bool HasComponent(IEnumerable<XElement> line, string name)
        {
            return line.SelectMany(element => element.DescendantsAndSelf()).Any(element => element.Name.LocalName == "Component"
                && string.Equals((string)element.Attribute("Name"), name, StringComparison.OrdinalIgnoreCase));
        }

        private static bool HasComponent(XElement element, string name)
        {
            return element.DescendantsAndSelf().Any(child => child.Name.LocalName == "Component"
                && string.Equals((string)child.Attribute("Name"), name, StringComparison.OrdinalIgnoreCase));
        }

        private static List<XElement> Constants(IEnumerable<XElement> line)
        {
            return line.SelectMany(element => element.DescendantsAndSelf())
                .Where(element => element.Name.LocalName == "ConstantValue").ToList();
        }

        private static void RenumberUids(XElement root)
        {
            var uid = 21;
            foreach (var element in root.DescendantsAndSelf())
            {
                var attribute = element.Attribute("UId");
                if (attribute != null) attribute.Value = (uid++).ToString();
            }
        }

        private static object FindBlock(object group, string name)
        {
            if (group == null) return null;
            foreach (var block in OpennessReflection.ReadEnumerableProperty(group, "Blocks") ?? new object[0])
            {
                if (string.Equals(OpennessReflection.ReadProperty(block, "Name") as string, name, StringComparison.OrdinalIgnoreCase)) return block;
            }
            foreach (var child in OpennessReflection.ReadEnumerableProperty(group, "Groups") ?? new object[0])
            {
                var found = FindBlock(child, name);
                if (found != null) return found;
            }
            return null;
        }

        private static void ExportBlock(object block, string xmlPath)
        {
            if (File.Exists(xmlPath)) File.Delete(xmlPath);
            var method = block.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(candidate => candidate.Name == "Export" && candidate.GetParameters().Length == 2
                    && typeof(FileInfo).IsAssignableFrom(candidate.GetParameters()[0].ParameterType)
                    && candidate.GetParameters()[1].ParameterType.IsEnum);
            if (method == null) throw new InvalidOperationException("Cylinder logic XML export is unavailable.");
            var optionType = method.GetParameters()[1].ParameterType;
            var option = EnumValue(optionType, "WithReadOnly") ?? Enum.GetValues(optionType).GetValue(0);
            method.Invoke(block, new object[] { new FileInfo(xmlPath), option });
        }

        private static void ImportBlock(object block, string xmlPath)
        {
            var parentGroup = OpennessReflection.ReadProperty(block, "Parent");
            var blocks = OpennessReflection.ReadProperty(parentGroup, "Blocks");
            var method = blocks?.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(candidate => candidate.Name == "Import" && candidate.GetParameters().Length == 2
                    && typeof(FileInfo).IsAssignableFrom(candidate.GetParameters()[0].ParameterType)
                    && candidate.GetParameters()[1].ParameterType.IsEnum);
            if (method == null) throw new InvalidOperationException("Cylinder logic XML import is unavailable.");
            var optionType = method.GetParameters()[1].ParameterType;
            var option = EnumValue(optionType, "Override") ?? EnumValue(optionType, "OverwriteAll")
                ?? Enum.GetValues(optionType).GetValue(0);
            method.Invoke(blocks, new object[] { new FileInfo(xmlPath), option });
        }

        private static object EnumValue(Type enumType, string name)
        {
            return Enum.IsDefined(enumType, name) ? Enum.Parse(enumType, name) : null;
        }

        private static void CompilePlcSoftware(object plcSoftware, CylinderLogicWriteResult result)
        {
            var compilable = OpennessReflection.InvokeGenericGetService(plcSoftware, "Siemens.Engineering.Compiler.ICompilable");
            var compile = compilable?.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(method => method.Name == "Compile" && method.GetParameters().Length == 0);
            if (compile == null) return;
            var compileResult = compile.Invoke(compilable, null);
            result.CompilationErrorCount = ReadInt(compileResult, "ErrorCount");
            result.CompilationWarningCount = ReadInt(compileResult, "WarningCount");
        }

        private static int ReadInt(object target, string propertyName)
        {
            var value = OpennessReflection.ReadProperty(target, propertyName);
            return value is int number ? number : 0;
        }

        private static CylinderLogicWriteResult Complete(CylinderLogicWriteResult result)
        {
            result.Success = result.Errors.Count == 0;
            result.Diagnostic = result.Success
                ? $"Cylinder logic configured for {result.UpdatedStations.Count} station(s)."
                : "Cylinder logic configuration failed: " + string.Join("; ", result.Errors);
            return result;
        }

        private class ConfiguredStation
        {
            public string UnitName { get; set; }
            public int ArrayIndex { get; set; }
            public UnitStationSettings Settings { get; set; }
        }

        private class SafetySignal
        {
            public string Address { get; set; }
            public string TagName { get; set; }
            public string Comment { get; set; }
        }

        private class StructuredTextBuilder
        {
            private int _uid = 21;
            public XElement Root { get; } = new XElement(StructuredTextNamespace + "StructuredText");

            public void Comment(string text)
            {
                Root.Add(Element("LineComment", Element("Text", CleanComment(text))), Element("NewLine"));
            }

            public void BlankLine()
            {
                Root.Add(Element("NewLine"));
            }

            public void AssignCylinderCount(int stationIndex, int cylinderCount, string comment)
            {
                Assign(LocalCylinderPath(stationIndex, "I气缸数量"), Integer(cylinderCount), comment, false);
            }

            public void AssignSafety(
                int stationIndex, int actionIndex, string safetyMember,
                IReadOnlyList<string> conditions, string comment)
            {
                var right = new List<XElement> { Element("Token", new XAttribute("Text", "NOT")), Element("Blank") };
                if (conditions.Count == 0)
                {
                    right.Add(Boolean(true));
                }
                else
                {
                    right.Add(Element("Token", new XAttribute("Text", "(")));
                    for (var index = 0; index < conditions.Count; index++)
                    {
                        if (index > 0)
                        {
                            right.Add(Element("Blank"));
                            right.Add(Element("Token", new XAttribute("Text", "AND")));
                            right.Add(Element("Blank"));
                        }
                        right.Add(GlobalTag(conditions[index]));
                    }
                    right.Add(Element("Token", new XAttribute("Text", ")")));
                }
                Assign(GlobalSafetyPath(stationIndex, safetyMember, actionIndex), right, comment, true);
            }

            private void Assign(XElement left, object right, string comment, bool rightIsList)
            {
                Root.Add(left, Element("Blank"), Element("Token", new XAttribute("Text", ":=")), Element("Blank"));
                if (rightIsList) Root.Add((IEnumerable<XElement>)right); else Root.Add((XElement)right);
                Root.Add(Element("Token", new XAttribute("Text", ";")), Element("Blank", new XAttribute("Num", "2")),
                    Element("LineComment", Element("Text", CleanComment(comment))), Element("NewLine"));
            }

            private XElement LocalCylinderPath(int stationIndex, string member)
            {
                return Element("Access", new XAttribute("Scope", "LocalVariable"),
                    Element("Symbol", ArrayComponent("气缸块", stationIndex), Token("."), Component(member, false)));
            }

            private XElement GlobalSafetyPath(int stationIndex, string safetyMember, int actionIndex)
            {
                return Element("Access", new XAttribute("Scope", "GlobalVariable"),
                    Element("Symbol", Component("IO", true), Token("."), ArrayComponent("参数2", stationIndex),
                        Token("."), ArrayComponent(safetyMember, actionIndex)));
            }

            private XElement GlobalTag(string tagName)
            {
                return Element("Access", new XAttribute("Scope", "GlobalVariable"),
                    Element("Symbol", Component(tagName, true)));
            }

            private XElement ArrayComponent(string name, int index)
            {
                return Element("Component", new XAttribute("Name", name), Token("["), Integer(index, "DInt"), Token("]"));
            }

            private XElement Integer(int value, string type = "Int")
            {
                return Element("Access", new XAttribute("Scope", "LiteralConstant"),
                    Element("Constant", Element("ConstantType", new XAttribute("Informative", "true"), type),
                        Element("ConstantValue", value.ToString())));
            }

            private XElement Boolean(bool value)
            {
                return Element("Access", new XAttribute("Scope", "LiteralConstant"),
                    Element("Constant", Element("ConstantType", new XAttribute("Informative", "true"), "Bool"),
                        Element("ConstantValue", value ? "TRUE" : "FALSE"),
                        Element("StringAttribute", new XAttribute("Name", "Format"), new XAttribute("Informative", "true"), "Bool")));
            }

            private XElement Component(string name, bool quoted)
            {
                var component = Element("Component", new XAttribute("Name", name));
                if (quoted) component.Add(Element("BooleanAttribute", new XAttribute("Name", "HasQuotes"), "true"));
                return component;
            }

            private XElement Token(string text)
            {
                return Element("Token", new XAttribute("Text", text));
            }

            private XElement Element(string name, params object[] content)
            {
                var element = new XElement(StructuredTextNamespace + name, content);
                element.SetAttributeValue("UId", _uid++);
                return element;
            }

            private static string CleanComment(string value)
            {
                return Regex.Replace(value ?? string.Empty, @"[\r\n]+", " ").Trim();
            }
        }
    }

    public class CylinderLogicWriteResult
    {
        public bool Success { get; set; }
        public string Diagnostic { get; set; }
        public string BlockName { get; set; }
        public int StationCount { get; set; }
        public bool StationCountVariableUpdated { get; set; }
        public bool LoopBoundUpdated { get; set; }
        public string SafetyMemberName { get; set; }
        public int CompilationErrorCount { get; set; }
        public int CompilationWarningCount { get; set; }
        public List<CylinderLogicStationUpdate> UpdatedStations { get; set; } = new List<CylinderLogicStationUpdate>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
    }

    public class CylinderLogicStationUpdate
    {
        public string UnitName { get; set; }
        public string StationName { get; set; }
        public int StationNumber { get; set; }
        public int ArrayIndex { get; set; }
        public int CylinderCount { get; set; }
        public int SafetyConditionCount { get; set; }
    }
}
