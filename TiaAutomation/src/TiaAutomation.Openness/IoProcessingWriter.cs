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
    public class IoProcessingWriter
    {
        private const string BlockName = "IO处理";
        private static readonly XNamespace StructuredTextNamespace =
            "http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4";

        public IoProcessingWriteResult WriteOnOpenedProject(
            object project, ProjectSettings settings, IEnumerable<IoCommentRequest> ioComments,
            IEnumerable<IoPoint> ioPoints, string scratchDirectory)
        {
            var result = new IoProcessingWriteResult();
            var stations = FlattenStations(settings).ToList();
            if (stations.Count == 0)
            {
                result.Success = true;
                result.Diagnostic = "No station IO mappings were configured.";
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

                var signals = BuildSignals(ioComments, ioPoints);
                var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                scratchDirectory = Path.GetFullPath(scratchDirectory);
                Directory.CreateDirectory(scratchDirectory);
                var xmlPath = Path.Combine(scratchDirectory, BlockName + ".xml");
                ExportBlock(block, xmlPath);
                RewriteNetworks(xmlPath, stations, signals, claimed, result);
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
                    if (station == null || !TryStationIndex(station.DataTypeName, out var stationIndex)) continue;
                    yield return new ConfiguredStation
                    {
                        UnitName = unitIndex == 0 ? "设备1" : $"设备1_{unitIndex}",
                        ArrayIndex = stationIndex,
                        Settings = station
                    };
                }
            }
        }

        private static bool TryStationIndex(string dataTypeName, out int index)
        {
            var match = Regex.Match(dataTypeName ?? string.Empty, @"工位\s*([1-9])", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var number))
            {
                index = number - 1;
                return true;
            }
            index = -1;
            return false;
        }

        private static List<IoSignal> BuildSignals(
            IEnumerable<IoCommentRequest> comments, IEnumerable<IoPoint> points)
        {
            var byAddress = new Dictionary<string, IoSignal>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in comments ?? Enumerable.Empty<IoCommentRequest>())
            {
                AddSignal(byAddress, item.Address, null, item.Comment);
            }
            foreach (var point in points ?? Enumerable.Empty<IoPoint>())
            {
                AddSignal(byAddress, point.Address, point.Tag, point.Comment);
            }
            return byAddress.Values.Where(signal => !string.IsNullOrWhiteSpace(signal.Comment)).ToList();
        }

        private static void AddSignal(
            IDictionary<string, IoSignal> byAddress, string address, string tag, string comment)
        {
            var normalized = NormalizeAddress(address);
            if (normalized == null) return;
            if (!byAddress.TryGetValue(normalized, out var signal))
            {
                signal = new IoSignal
                {
                    Address = normalized,
                    TagName = string.IsNullOrWhiteSpace(tag) ? TagNameFromAddress(normalized) : tag.Trim(),
                    IsInput = normalized.StartsWith("%I", StringComparison.OrdinalIgnoreCase)
                };
                byAddress[normalized] = signal;
            }
            if (!string.IsNullOrWhiteSpace(comment)) signal.Comment = comment.Trim();
            if (!string.IsNullOrWhiteSpace(tag)) signal.TagName = tag.Trim();
        }

        private static string NormalizeAddress(string address)
        {
            var match = Regex.Match(address ?? string.Empty, @"%?\s*([IQiq])\s*(\d+)\s*\.\s*(\d+)");
            return match.Success
                ? $"%{match.Groups[1].Value.ToUpperInvariant()}{int.Parse(match.Groups[2].Value)}.{int.Parse(match.Groups[3].Value)}"
                : null;
        }

        private static string TagNameFromAddress(string address)
        {
            return address.TrimStart('%').Replace('.', '_');
        }

        private static void RewriteNetworks(
            string xmlPath, List<ConfiguredStation> stations, List<IoSignal> signals,
            HashSet<string> claimed, IoProcessingWriteResult result)
        {
            var document = XDocument.Load(xmlPath, LoadOptions.PreserveWhitespace);
            var objectList = document.Descendants().FirstOrDefault(element =>
                element.Name.LocalName == "SW.Blocks.FC")?.Elements()
                .FirstOrDefault(element => element.Name.LocalName == "ObjectList");
            if (objectList == null) throw new InvalidOperationException("IO processing FC object list was not found.");

            var existingUnits = objectList.Elements()
                .Where(element => element.Name.LocalName == "SW.Blocks.CompileUnit").ToList();
            var template = existingUnits.FirstOrDefault();
            if (template == null) throw new InvalidOperationException("IO processing FC has no network template.");
            var insertionPoint = existingUnits.LastOrDefault();
            var generatedUnits = new List<XElement>();
            var objectId = 1000;

            foreach (var station in stations.OrderBy(item => item.ArrayIndex))
            {
                var network = new XElement(template);
                foreach (var attribute in network.DescendantsAndSelf().Attributes("ID"))
                {
                    attribute.Value = (objectId++).ToString("X");
                }

                var title = network.Descendants().FirstOrDefault(element =>
                    element.Name.LocalName == "MultilingualText" &&
                    string.Equals((string)element.Attribute("CompositionName"), "Title", StringComparison.OrdinalIgnoreCase));
                var titleText = title?.Descendants().FirstOrDefault(element => element.Name.LocalName == "Text");
                if (titleText != null) titleText.Value = station.Settings.Name ?? $"工位{station.ArrayIndex + 1}";

                var structuredText = BuildStationNetwork(station, signals, claimed, result);
                var networkSource = network.Descendants().FirstOrDefault(element => element.Name.LocalName == "NetworkSource");
                if (networkSource == null) throw new InvalidOperationException("IO processing network source was not found.");
                networkSource.ReplaceNodes(structuredText);
                var language = network.Descendants().FirstOrDefault(element => element.Name.LocalName == "ProgrammingLanguage");
                if (language != null) language.Value = "SCL";
                generatedUnits.Add(network);
            }

            insertionPoint.AddAfterSelf(generatedUnits);
            foreach (var unit in existingUnits) unit.Remove();
            document.Save(xmlPath, SaveOptions.DisableFormatting);
        }

        private static XElement BuildStationNetwork(
            ConfiguredStation station, List<IoSignal> signals, HashSet<string> claimed,
            IoProcessingWriteResult result)
        {
            var builder = new StructuredTextBuilder();
            var update = new IoProcessingStationUpdate
            {
                UnitName = station.UnitName,
                StationName = station.Settings.Name,
                StationNumber = station.ArrayIndex + 1,
                ArrayIndex = station.ArrayIndex
            };
            var cylinders = CleanNames(station.Settings.CylinderNames);
            var masters = station.Settings.CylinderValveMasterIndexes ?? new List<int?>();

            builder.Comment("气缸输出");
            for (var index = 0; index < cylinders.Count; index++)
            {
                var master = ResolveMasterIndex(masters, index);
                if (master != index)
                {
                    builder.Assign(
                        builder.IoPath("手动气缸", station.ArrayIndex, $"气缸出_{index + 1}"),
                        builder.IoPath("手动气缸", station.ArrayIndex, $"气缸出_{master + 1}"),
                        $"{cylinders[index]}共用{cylinders[master]}电磁阀");
                    builder.Assign(
                        builder.IoPath("手动气缸", station.ArrayIndex, $"气缸回_{index + 1}"),
                        builder.IoPath("手动气缸", station.ArrayIndex, $"气缸回_{master + 1}"),
                        $"{cylinders[index]}共用{cylinders[master]}电磁阀");
                    update.ManualLinks += 2;
                    continue;
                }

                MapPhysicalOutput(builder, station, signals, claimed, cylinders[index], index, true, update, result);
                MapPhysicalOutput(builder, station, signals, claimed, cylinders[index], index, false, update, result);
            }

            builder.BlankLine();
            builder.Comment("气缸限位输入");
            for (var index = 0; index < cylinders.Count; index++)
            {
                MapPhysicalInput(builder, station, signals, claimed, cylinders[index], index, true, update, result);
                MapPhysicalInput(builder, station, signals, claimed, cylinders[index], index, false, update, result);
            }

            var sensors = CleanNames(station.Settings.SensorNames);
            if (sensors.Count > 0)
            {
                builder.BlankLine();
                builder.Comment("工位感应输入");
            }
            for (var index = 0; index < sensors.Count; index++)
            {
                var signal = FindBestSignal(signals, claimed, sensors[index], station.Settings.Name, true, null, false);
                if (signal == null)
                {
                    result.Warnings.Add($"{station.Settings.Name}: sensor '{sensors[index]}' input was not matched by IO comments.");
                    continue;
                }
                claimed.Add(signal.Address);
                builder.Assign(
                    builder.IoPath("工位I", station.ArrayIndex, "感应", $"感应{index + 1}"),
                    builder.GlobalTag(signal.TagName), signal.Comment);
                update.SensorInputs++;
                result.Lines.Add(Line(station, "SensorInput", signal, $"工位I[{station.ArrayIndex}].感应.感应{index + 1}"));
            }

            result.UpdatedStations.Add(update);
            return builder.Root;
        }

        private static int ResolveMasterIndex(List<int?> masters, int index)
        {
            if (index >= masters.Count || !masters[index].HasValue) return index;
            var oneBased = masters[index].Value;
            return oneBased >= 1 && oneBased <= index ? oneBased - 1 : index;
        }

        private static void MapPhysicalOutput(
            StructuredTextBuilder builder, ConfiguredStation station, List<IoSignal> signals,
            HashSet<string> claimed, string cylinderName, int cylinderIndex, bool extend,
            IoProcessingStationUpdate update, IoProcessingWriteResult result)
        {
            var signal = FindBestSignal(signals, claimed, cylinderName, station.Settings.Name, false, extend, true);
            var member = extend ? $"气缸出_{cylinderIndex + 1}" : $"气缸回_{cylinderIndex + 1}";
            if (signal == null)
            {
                result.Warnings.Add($"{station.Settings.Name}: cylinder '{cylinderName}' {(extend ? "extend" : "retract")} output was not matched by IO comments.");
                return;
            }
            claimed.Add(signal.Address);
            builder.Assign(builder.GlobalTag(signal.TagName), builder.IoPath("工位Q", station.ArrayIndex, member), signal.Comment);
            update.Outputs++;
            result.Lines.Add(Line(station, extend ? "ExtendOutput" : "RetractOutput", signal, $"工位Q[{station.ArrayIndex}].{member}"));
        }

        private static void MapPhysicalInput(
            StructuredTextBuilder builder, ConfiguredStation station, List<IoSignal> signals,
            HashSet<string> claimed, string cylinderName, int cylinderIndex, bool extend,
            IoProcessingStationUpdate update, IoProcessingWriteResult result)
        {
            var signal = FindBestSignal(signals, claimed, cylinderName, station.Settings.Name, true, extend, true);
            var member = extend ? $"气缸感应出_{cylinderIndex + 1}" : $"气缸感应回_{cylinderIndex + 1}";
            if (signal == null)
            {
                result.Warnings.Add($"{station.Settings.Name}: cylinder '{cylinderName}' {(extend ? "extend" : "retract")} input was not matched by IO comments.");
                return;
            }
            claimed.Add(signal.Address);
            builder.Assign(builder.IoPath("工位I", station.ArrayIndex, member), builder.GlobalTag(signal.TagName), signal.Comment);
            update.Inputs++;
            result.Lines.Add(Line(station, extend ? "ExtendInput" : "RetractInput", signal, $"工位I[{station.ArrayIndex}].{member}"));
        }

        private static IoProcessingLine Line(
            ConfiguredStation station, string kind, IoSignal signal, string target)
        {
            return new IoProcessingLine
            {
                StationName = station.Settings.Name,
                Kind = kind,
                TagName = signal.TagName,
                Address = signal.Address,
                Target = target,
                Comment = signal.Comment
            };
        }

        private static IoSignal FindBestSignal(
            IEnumerable<IoSignal> signals, HashSet<string> claimed, string itemName,
            string stationName, bool input, bool? extend, bool allowNumberlessName)
        {
            return signals.Where(signal => signal.IsInput == input && !claimed.Contains(signal.Address))
                .Select(signal => new
                {
                    Signal = signal,
                    Score = MatchScore(signal.Comment, itemName, stationName, extend, allowNumberlessName)
                })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Signal.Address, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Signal)
                .FirstOrDefault();
        }

        private static int MatchScore(
            string comment, string itemName, string stationName, bool? extend, bool allowNumberlessName)
        {
            var compactComment = Compact(comment);
            var compactName = Compact(itemName);
            if (string.IsNullOrWhiteSpace(compactName)) return 0;
            var score = compactComment.Contains(compactName) ? 100 : 0;
            if (score == 0 && allowNumberlessName)
            {
                var numberless = Regex.Replace(compactName, @"\d+$", string.Empty);
                if (numberless.Length >= 2 && compactComment.Contains(numberless)) score = 70;
            }
            if (score == 0) return 0;
            if (extend.HasValue && !DirectionMatches(compactComment, extend.Value)) return 0;
            var compactStation = Compact(stationName);
            if (!string.IsNullOrWhiteSpace(compactStation) && compactComment.Contains(compactStation)) score += 20;
            return score;
        }

        private static bool DirectionMatches(string comment, bool extend)
        {
            var extendWords = new[] { "夹紧", "伸出", "顶升", "上升", "打开", "吸气", "前进", "出" };
            var retractWords = new[] { "松开", "缩回", "下降", "关闭", "吹气", "后退", "回" };
            var expected = extend ? extendWords : retractWords;
            var opposite = extend ? retractWords : extendWords;
            var expectedIndex = expected.Select(word => comment.LastIndexOf(word, StringComparison.Ordinal)).Max();
            var oppositeIndex = opposite.Select(word => comment.LastIndexOf(word, StringComparison.Ordinal)).Max();
            return expectedIndex >= 0 && expectedIndex >= oppositeIndex;
        }

        private static string Compact(string value)
        {
            return Regex.Replace(value ?? string.Empty, @"[\s\p{P}\p{S}]", string.Empty)
                .Replace("通道", string.Empty)
                .Replace("限位", string.Empty);
        }

        private static List<string> CleanNames(IEnumerable<string> names)
        {
            return (names ?? Enumerable.Empty<string>()).Select(name => (name ?? string.Empty).Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name)).Take(16).ToList();
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
            if (method == null) throw new InvalidOperationException("IO processing FC XML export is unavailable.");
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
            if (method == null) throw new InvalidOperationException("IO processing FC XML import is unavailable.");
            var optionType = method.GetParameters()[1].ParameterType;
            var option = EnumValue(optionType, "Override") ?? EnumValue(optionType, "OverwriteAll")
                ?? Enum.GetValues(optionType).GetValue(0);
            method.Invoke(blocks, new object[] { new FileInfo(xmlPath), option });
        }

        private static object EnumValue(Type enumType, string name)
        {
            return Enum.IsDefined(enumType, name) ? Enum.Parse(enumType, name) : null;
        }

        private static void CompilePlcSoftware(object plcSoftware, IoProcessingWriteResult result)
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

        private static IoProcessingWriteResult Complete(IoProcessingWriteResult result)
        {
            result.Success = result.Errors.Count == 0;
            result.Diagnostic = result.Success
                ? $"IO processing FC configured for {result.UpdatedStations.Count} station(s)."
                : "IO processing FC configuration failed: " + string.Join("; ", result.Errors);
            return result;
        }

        private class ConfiguredStation
        {
            public string UnitName { get; set; }
            public int ArrayIndex { get; set; }
            public UnitStationSettings Settings { get; set; }
        }

        private class IoSignal
        {
            public string Address { get; set; }
            public string TagName { get; set; }
            public string Comment { get; set; }
            public bool IsInput { get; set; }
        }

        private class StructuredTextBuilder
        {
            private int _uid = 21;
            public XElement Root { get; } = new XElement(StructuredTextNamespace + "StructuredText");

            public void Assign(XElement left, XElement right, string comment)
            {
                Root.Add(left, Element("Blank"), Element("Token", new XAttribute("Text", ":=")),
                    Element("Blank"), right, Element("Token", new XAttribute("Text", ";")));
                if (!string.IsNullOrWhiteSpace(comment))
                {
                    Root.Add(Element("Blank", new XAttribute("Num", "2")),
                        Element("LineComment", Element("Text", CleanComment(comment))));
                }
                Root.Add(Element("NewLine"));
            }

            public void Comment(string text)
            {
                Root.Add(Element("LineComment", Element("Text", CleanComment(text))), Element("NewLine"));
            }

            public void BlankLine()
            {
                Root.Add(Element("NewLine"));
            }

            public XElement GlobalTag(string tagName)
            {
                return Element("Access", new XAttribute("Scope", "GlobalVariable"),
                    Element("Symbol", Component(tagName, true)));
            }

            public XElement IoPath(string arrayName, int arrayIndex, params string[] members)
            {
                var symbol = Element("Symbol", Component("IO", true), Token("."), ArrayComponent(arrayName, arrayIndex));
                foreach (var member in members)
                {
                    symbol.Add(Token("."), Component(member, false));
                }
                return Element("Access", new XAttribute("Scope", "GlobalVariable"), symbol);
            }

            private XElement ArrayComponent(string name, int index)
            {
                return Element("Component", new XAttribute("Name", name), Token("["),
                    Element("Access", new XAttribute("Scope", "LiteralConstant"),
                        Element("Constant", Element("ConstantType", new XAttribute("Informative", "true"), "DInt"),
                            Element("ConstantValue", index.ToString()))), Token("]"));
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

    public class IoProcessingWriteResult
    {
        public bool Success { get; set; }
        public string Diagnostic { get; set; }
        public string BlockName { get; set; }
        public int CompilationErrorCount { get; set; }
        public int CompilationWarningCount { get; set; }
        public List<IoProcessingStationUpdate> UpdatedStations { get; set; } = new List<IoProcessingStationUpdate>();
        public List<IoProcessingLine> Lines { get; set; } = new List<IoProcessingLine>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
    }

    public class IoProcessingStationUpdate
    {
        public string UnitName { get; set; }
        public string StationName { get; set; }
        public int StationNumber { get; set; }
        public int ArrayIndex { get; set; }
        public int Outputs { get; set; }
        public int Inputs { get; set; }
        public int SensorInputs { get; set; }
        public int ManualLinks { get; set; }
    }

    public class IoProcessingLine
    {
        public string StationName { get; set; }
        public string Kind { get; set; }
        public string TagName { get; set; }
        public string Address { get; set; }
        public string Target { get; set; }
        public string Comment { get; set; }
    }
}
