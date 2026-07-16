using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using TiaAutomation.Core.Models;

namespace TiaAutomation.Openness
{
    public class UnitServoWriter
    {
        private const string SourceFolderName = "设备1";
        private static readonly string[] AxisLineOrder =
        {
            "系统", "精度", "HW", "安全+", "安全-", "导程", "转速", "通讯"
        };

        public UnitServoWriteResult WriteOnOpenedProject(
            object project, ProjectSettings settings, IEnumerable<DeviceRequest> devices, string scratchDirectory)
        {
            var result = new UnitServoWriteResult();
            var counts = settings?.UnitServoCounts ?? new List<int?>();
            var assignments = settings?.UnitServoDeviceNames ?? new List<List<string>>();
            var servoDevices = (devices ?? Enumerable.Empty<DeviceRequest>()).Where(IsServoDevice).ToList();
            var requestedUnits = Math.Max(1, settings?.UnitCount ?? 1);
            if (!counts.Take(requestedUnits).Any(value => value.HasValue))
            {
                result.Success = true;
                result.Diagnostic = "No unit servo counts were configured.";
                return result;
            }

            try
            {
                var plcSoftware = PlcSoftwareLocator.FindFirstPlcSoftware(project);
                CompilePlcSoftware(plcSoftware, result);
                var rootBlockGroup = OpennessReflection.ReadProperty(plcSoftware, "BlockGroup");
                var rootGroups = OpennessReflection.ReadProperty(rootBlockGroup, "Groups") as IEnumerable;
                if (rootGroups == null)
                {
                    result.Errors.Add("PLC program block groups were not found.");
                    return Complete(result);
                }

                var unitGroups = rootGroups.Cast<object>()
                    .Where(group => IsUnitFolder(OpennessReflection.ReadProperty(group, "Name") as string))
                    .OrderBy(group => UnitFolderIndex(OpennessReflection.ReadProperty(group, "Name") as string))
                    .ToList();

                scratchDirectory = Path.GetFullPath(scratchDirectory);
                Directory.CreateDirectory(scratchDirectory);
                var axisOffset = 0;
                var claimedServoNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (var unitIndex = 0; unitIndex < requestedUnits; unitIndex++)
                {
                    if (unitIndex >= counts.Count || !counts[unitIndex].HasValue) continue;
                    var servoCount = counts[unitIndex].Value;
                    if (servoCount < 0 || servoCount > 16)
                    {
                        result.Errors.Add($"Unit {unitIndex + 1}: servo count {servoCount} must be between 0 and 16.");
                        continue;
                    }
                    var hasAssignmentEntry = unitIndex < assignments.Count;
                    var selectedNames = hasAssignmentEntry
                        ? (assignments[unitIndex] ?? new List<string>()).Where(name => !string.IsNullOrWhiteSpace(name)).ToList()
                        : new List<string>();
                    List<int> selectedDeviceIndexes = null;
                    if (hasAssignmentEntry)
                    {
                        if (selectedNames.Count != servoCount)
                        {
                            result.Errors.Add($"Unit {unitIndex + 1}: selected {selectedNames.Count} servo devices for {servoCount} axes.");
                            continue;
                        }
                        selectedDeviceIndexes = new List<int>();
                        var invalidSelection = false;
                        foreach (var selectedName in selectedNames)
                        {
                            var deviceIndex = servoDevices.FindIndex(device =>
                                string.Equals(device.Name, selectedName, StringComparison.OrdinalIgnoreCase));
                            if (deviceIndex < 0)
                            {
                                result.Errors.Add($"Unit {unitIndex + 1}: configured servo device '{selectedName}' was not found.");
                                invalidSelection = true;
                                continue;
                            }
                            if (claimedServoNames.Contains(selectedName)
                                || selectedNames.Count(name => string.Equals(name, selectedName, StringComparison.OrdinalIgnoreCase)) > 1)
                            {
                                result.Errors.Add($"Servo device '{selectedName}' is assigned more than once.");
                                invalidSelection = true;
                                continue;
                            }
                            selectedDeviceIndexes.Add(deviceIndex);
                        }
                        if (invalidSelection) continue;
                        foreach (var selectedName in selectedNames) claimedServoNames.Add(selectedName);
                    }
                    if (unitIndex >= unitGroups.Count)
                    {
                        result.Errors.Add($"Unit {unitIndex + 1}: TIA program folder was not found.");
                        continue;
                    }

                    var unitGroup = unitGroups[unitIndex];
                    var unitName = OpennessReflection.ReadProperty(unitGroup, "Name") as string ?? $"Unit {unitIndex + 1}";
                    var block = FindServoLogicBlock(unitGroup);
                    if (block == null)
                    {
                        result.Errors.Add($"{unitName}: servo logic block was not found.");
                        continue;
                    }

                    var blockName = OpennessReflection.ReadProperty(block, "Name") as string ?? "伺服逻辑";
                    var xmlPath = Path.Combine(scratchDirectory, MakeFileSafe(unitName + "_" + blockName) + ".xml");
                    ExportBlock(block, xmlPath);
                    UpdateAxisInitialization(xmlPath, servoCount, axisOffset, selectedDeviceIndexes);
                    ImportBlock(block, xmlPath);
                    result.UpdatedUnits.Add(new UnitServoUpdate
                    {
                        UnitName = unitName,
                        BlockName = blockName,
                        ServoCount = servoCount,
                        DeviceNames = selectedNames
                    });
                    axisOffset += servoCount;
                }
                CompilePlcSoftware(plcSoftware, result);
            }
            catch (Exception ex)
            {
                result.Errors.Add(ex.GetBaseException().Message);
            }

            return Complete(result);
        }

        private static bool IsServoDevice(DeviceRequest device)
        {
            var text = string.Join(" ", new[]
            {
                device?.MainFamily, device?.ProductFamily, device?.DeviceType, device?.OrderNumber
            }.Where(value => !string.IsNullOrWhiteSpace(value))).ToLowerInvariant();
            return text.Contains("drives") || text.Contains("servo") || text.Contains("sinamics")
                || text.Contains("v90") || text.Contains("s200") || text.Contains("sv660");
        }

        private static void CompilePlcSoftware(object plcSoftware, UnitServoWriteResult result)
        {
            var compilable = OpennessReflection.InvokeGenericGetService(
                plcSoftware, "Siemens.Engineering.Compiler.ICompilable");
            var compile = compilable?.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(method => method.Name == "Compile" && method.GetParameters().Length == 0);
            if (compile == null) throw new InvalidOperationException("PLC software compile service is unavailable.");
            var compileResult = compile.Invoke(compilable, null);
            result.CompilationErrorCount = ReadInt(compileResult, "ErrorCount");
            result.CompilationWarningCount = ReadInt(compileResult, "WarningCount");
        }

        private static int ReadInt(object target, string propertyName)
        {
            var value = OpennessReflection.ReadProperty(target, propertyName);
            return value is int number ? number : 0;
        }

        private static bool IsUnitFolder(string name)
        {
            return string.Equals(name, SourceFolderName, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(name)
                    && name.StartsWith(SourceFolderName + "_", StringComparison.OrdinalIgnoreCase));
        }

        private static int UnitFolderIndex(string name)
        {
            if (string.Equals(name, SourceFolderName, StringComparison.OrdinalIgnoreCase)) return 0;
            var suffix = name?.Substring((SourceFolderName + "_").Length);
            return int.TryParse(suffix, out var value) ? value : int.MaxValue;
        }

        private static object FindServoLogicBlock(object group)
        {
            foreach (var block in OpennessReflection.ReadEnumerableProperty(group, "Blocks") ?? new object[0])
            {
                var name = OpennessReflection.ReadProperty(block, "Name") as string;
                if (!string.IsNullOrWhiteSpace(name)
                    && name.StartsWith("伺服逻辑", StringComparison.OrdinalIgnoreCase)) return block;
            }
            foreach (var child in OpennessReflection.ReadEnumerableProperty(group, "Groups") ?? new object[0])
            {
                var found = FindServoLogicBlock(child);
                if (found != null) return found;
            }
            return null;
        }

        private static void ExportBlock(object block, string xmlPath)
        {
            if (File.Exists(xmlPath)) File.Delete(xmlPath);
            var method = block.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(candidate => candidate.Name == "Export")
                .FirstOrDefault(candidate =>
                {
                    var parameters = candidate.GetParameters();
                    return parameters.Length == 2
                        && typeof(FileInfo).IsAssignableFrom(parameters[0].ParameterType)
                        && parameters[1].ParameterType.IsEnum;
                });
            if (method == null) throw new InvalidOperationException("PLC block XML export is unavailable.");
            var optionType = method.GetParameters()[1].ParameterType;
            var option = EnumValue(optionType, "WithReadOnly") ?? Enum.GetValues(optionType).GetValue(0);
            method.Invoke(block, new object[] { new FileInfo(xmlPath), option });
        }

        private static void ImportBlock(object block, string xmlPath)
        {
            var parentGroup = OpennessReflection.ReadProperty(block, "Parent");
            var blocks = OpennessReflection.ReadProperty(parentGroup, "Blocks");
            var method = blocks?.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(candidate =>
                {
                    var parameters = candidate.GetParameters();
                    return candidate.Name == "Import" && parameters.Length == 2
                        && typeof(FileInfo).IsAssignableFrom(parameters[0].ParameterType)
                        && parameters[1].ParameterType.IsEnum;
                });
            if (method == null) throw new InvalidOperationException("PLC block XML import is unavailable.");
            var optionType = method.GetParameters()[1].ParameterType;
            var option = EnumValue(optionType, "Override")
                ?? EnumValue(optionType, "OverwriteAll")
                ?? Enum.GetValues(optionType).GetValue(0);
            method.Invoke(blocks, new object[] { new FileInfo(xmlPath), option });
        }

        private static object EnumValue(Type enumType, string name)
        {
            return Enum.IsDefined(enumType, name) ? Enum.Parse(enumType, name) : null;
        }

        private static void UpdateAxisInitialization(
            string xmlPath, int servoCount, int axisOffset, IReadOnlyList<int> selectedDeviceIndexes)
        {
            var document = XDocument.Load(xmlPath, LoadOptions.PreserveWhitespace);
            var structuredText = document.Descendants()
                .Where(element => element.Name.LocalName == "StructuredText")
                .FirstOrDefault(element => HasComponent(element, "轴数") && HasComponent(element, "DiagnosePN"));
            if (structuredText == null)
            {
                throw new InvalidOperationException("Servo initialization SCL network was not found.");
            }

            var lines = SplitLines(structuredText).ToList();
            var axisCountTemplate = lines.FirstOrDefault(line => HasComponent(line, "轴数"));
            if (axisCountTemplate == null) throw new InvalidOperationException("Axis-count assignment was not found.");

            var templates = AxisLineOrder.ToDictionary(
                name => name,
                name => lines.FirstOrDefault(line => HasComponent(line, name) && HasFirstConstant(line, "0")),
                StringComparer.OrdinalIgnoreCase);
            var missing = templates.Where(pair => pair.Value == null).Select(pair => pair.Key).ToList();
            if (missing.Count > 0)
            {
                throw new InvalidOperationException("Axis template lines are missing: " + string.Join(", ", missing));
            }

            var baseHardwareId = LastInteger(templates["HW"]) ?? 280;
            var output = new List<XElement>();
            var countLine = CloneLine(axisCountTemplate);
            SetLastConstant(countLine, servoCount);
            output.AddRange(countLine);

            for (var axis = 0; axis < servoCount; axis++)
            {
                foreach (var name in AxisLineOrder)
                {
                    var line = CloneLine(templates[name]);
                    SetFirstConstant(line, axis);
                    if (string.Equals(name, "HW", StringComparison.OrdinalIgnoreCase))
                    {
                        var deviceIndex = selectedDeviceIndexes != null ? selectedDeviceIndexes[axis] : axisOffset + axis;
                        SetLastConstant(line, baseHardwareId + deviceIndex);
                    }
                    else if (string.Equals(name, "通讯", StringComparison.OrdinalIgnoreCase))
                    {
                        var deviceIndex = selectedDeviceIndexes != null ? selectedDeviceIndexes[axis] : axisOffset + axis;
                        SetLastConstant(line, deviceIndex + 1);
                    }
                    output.AddRange(line);
                }
            }

            structuredText.ReplaceNodes(output);
            var uid = 21;
            foreach (var element in structuredText.DescendantsAndSelf())
            {
                var attribute = element.Attribute("UId");
                if (attribute != null) attribute.Value = (uid++).ToString();
            }
            document.Save(xmlPath, SaveOptions.DisableFormatting);
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

        private static List<XElement> CloneLine(IEnumerable<XElement> line)
        {
            return line.Select(element => new XElement(element)).ToList();
        }

        private static bool HasComponent(IEnumerable<XElement> line, string name)
        {
            return line.SelectMany(element => element.DescendantsAndSelf())
                .Any(element => element.Name.LocalName == "Component"
                    && string.Equals((string)element.Attribute("Name"), name, StringComparison.OrdinalIgnoreCase));
        }

        private static bool HasComponent(XElement element, string name)
        {
            return element.DescendantsAndSelf().Any(child => child.Name.LocalName == "Component"
                && string.Equals((string)child.Attribute("Name"), name, StringComparison.OrdinalIgnoreCase));
        }

        private static bool HasFirstConstant(IEnumerable<XElement> line, string value)
        {
            return string.Equals(Constants(line).FirstOrDefault()?.Value, value, StringComparison.Ordinal);
        }

        private static List<XElement> Constants(IEnumerable<XElement> line)
        {
            return line.SelectMany(element => element.DescendantsAndSelf())
                .Where(element => element.Name.LocalName == "ConstantValue")
                .ToList();
        }

        private static int? LastInteger(IEnumerable<XElement> line)
        {
            return int.TryParse(Constants(line).LastOrDefault()?.Value, out var value) ? value : (int?)null;
        }

        private static void SetFirstConstant(IEnumerable<XElement> line, int value)
        {
            var constant = Constants(line).FirstOrDefault();
            if (constant == null) throw new InvalidOperationException("Array index constant was not found.");
            constant.Value = value.ToString();
        }

        private static void SetLastConstant(IEnumerable<XElement> line, int value)
        {
            var constant = Constants(line).LastOrDefault();
            if (constant == null) throw new InvalidOperationException("Assignment constant was not found.");
            constant.Value = value.ToString();
        }

        private static string MakeFileSafe(string name)
        {
            foreach (var character in Path.GetInvalidFileNameChars()) name = name.Replace(character, '_');
            return name;
        }

        private static UnitServoWriteResult Complete(UnitServoWriteResult result)
        {
            result.Success = result.Errors.Count == 0;
            result.Diagnostic = result.Success
                ? $"Unit servo initialization configured for {result.UpdatedUnits.Count} unit(s)."
                : "Unit servo initialization failed.";
            return result;
        }
    }

    public class UnitServoWriteResult
    {
        public bool Success { get; set; }
        public string Diagnostic { get; set; }
        public int CompilationErrorCount { get; set; }
        public int CompilationWarningCount { get; set; }
        public List<UnitServoUpdate> UpdatedUnits { get; set; } = new List<UnitServoUpdate>();
        public List<string> Errors { get; set; } = new List<string>();
    }

    public class UnitServoUpdate
    {
        public string UnitName { get; set; }
        public string BlockName { get; set; }
        public int ServoCount { get; set; }
        public List<string> DeviceNames { get; set; } = new List<string>();
    }
}
