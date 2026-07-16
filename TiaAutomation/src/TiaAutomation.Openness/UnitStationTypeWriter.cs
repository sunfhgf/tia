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
    public class UnitStationTypeWriter
    {
        private const int MaxCylinders = 16;
        private const int MaxSensors = 16;

        public UnitStationTypeWriteResult WriteOnOpenedProject(
            object project, ProjectSettings settings, string scratchDirectory)
        {
            var result = new UnitStationTypeWriteResult();
            var configured = Flatten(settings).ToList();
            if (configured.Count == 0)
            {
                result.Success = true;
                result.Diagnostic = "No unit stations were configured.";
                return result;
            }

            Validate(configured, result);
            if (result.Errors.Count > 0) return Complete(result);

            try
            {
                var plcSoftware = PlcSoftwareLocator.FindFirstPlcSoftware(project);
                if (plcSoftware == null)
                {
                    result.Errors.Add("PLC software was not found.");
                    return Complete(result);
                }

                var typeGroup = OpennessReflection.ReadProperty(plcSoftware, "TypeGroup");
                scratchDirectory = Path.GetFullPath(scratchDirectory);
                Directory.CreateDirectory(scratchDirectory);

                foreach (var item in configured)
                {
                    var baseName = NormalizeDataTypeName(item.Settings.DataTypeName);
                    var inputName = baseName + "I";
                    var outputName = baseName + "Q";
                    var inputType = FindType(typeGroup, inputName);
                    var outputType = FindType(typeGroup, outputName);
                    if (inputType == null || outputType == null)
                    {
                        result.Errors.Add($"{item.UnitName} / {item.Settings.Name}: data type pair {inputName}, {outputName} was not found.");
                        continue;
                    }

                    var prefix = MakeFileSafe($"{item.UnitName}_{baseName}");
                    var inputXml = Path.Combine(scratchDirectory, prefix + "I.xml");
                    var outputXml = Path.Combine(scratchDirectory, prefix + "Q.xml");
                    ExportType(inputType, inputXml);
                    ExportType(outputType, outputXml);
                    UpdateInputType(inputXml, item.Settings.CylinderNames, item.Settings.SensorNames);
                    UpdateOutputType(outputXml, item.Settings.CylinderNames);
                    ImportType(inputType, inputXml);
                    ImportType(outputType, outputXml);

                    result.UpdatedStations.Add(new UnitStationTypeUpdate
                    {
                        UnitName = item.UnitName,
                        StationName = item.Settings.Name,
                        InputDataType = inputName,
                        OutputDataType = outputName,
                        CylinderCount = CleanNames(item.Settings.CylinderNames).Count,
                        SensorCount = CleanNames(item.Settings.SensorNames).Count
                    });
                }

                if (result.Errors.Count == 0)
                {
                    CompilePlcSoftware(plcSoftware, result);
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add(ex.GetBaseException().Message);
            }

            return Complete(result);
        }

        private static IEnumerable<ConfiguredStation> Flatten(ProjectSettings settings)
        {
            var units = settings?.UnitStations ?? new List<List<UnitStationSettings>>();
            var unitCount = Math.Max(1, settings?.UnitCount ?? 1);
            for (var unitIndex = 0; unitIndex < Math.Min(unitCount, units.Count); unitIndex++)
            {
                foreach (var station in units[unitIndex] ?? new List<UnitStationSettings>())
                {
                    if (station == null || string.IsNullOrWhiteSpace(station.DataTypeName)) continue;
                    yield return new ConfiguredStation
                    {
                        UnitName = unitIndex == 0 ? "设备1" : $"设备1_{unitIndex}",
                        Settings = station
                    };
                }
            }
        }

        private static void Validate(List<ConfiguredStation> configured, UnitStationTypeWriteResult result)
        {
            var usedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in configured)
            {
                var label = $"{item.UnitName} / {item.Settings.Name ?? "未命名工位"}";
                var baseName = NormalizeDataTypeName(item.Settings.DataTypeName);
                if (!IsSupportedDataType(baseName))
                {
                    result.Errors.Add($"{label}: invalid station data type '{item.Settings.DataTypeName}'.");
                }
                else if (!usedTypes.Add(baseName))
                {
                    result.Errors.Add($"{label}: data type pair {baseName}I/{baseName}Q is already assigned to another station.");
                }

                var cylinders = CleanNames(item.Settings.CylinderNames);
                var sensors = CleanNames(item.Settings.SensorNames);
                if (cylinders.Count > MaxCylinders) result.Errors.Add($"{label}: cylinder count cannot exceed {MaxCylinders}.");
                if (sensors.Count > MaxSensors) result.Errors.Add($"{label}: sensor count cannot exceed {MaxSensors}.");
                AddDuplicateErrors(label, "cylinder", cylinders, result);
                AddDuplicateErrors(label, "sensor", sensors, result);
            }
        }

        private static void AddDuplicateErrors(
            string label, string kind, List<string> names, UnitStationTypeWriteResult result)
        {
            var duplicate = names.GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1)?.Key;
            if (!string.IsNullOrWhiteSpace(duplicate))
            {
                result.Errors.Add($"{label}: duplicate {kind} name '{duplicate}'.");
            }
        }

        private static string NormalizeDataTypeName(string value)
        {
            var name = (value ?? string.Empty).Trim();
            if (name.EndsWith("I", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("Q", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - 1);
            }
            return name;
        }

        private static bool IsSupportedDataType(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || !name.StartsWith("工位", StringComparison.Ordinal)) return false;
            return int.TryParse(name.Substring(2), out var index) && index >= 1 && index <= 9;
        }

        private static List<string> CleanNames(IEnumerable<string> names)
        {
            return (names ?? Enumerable.Empty<string>())
                .Select(name => (name ?? string.Empty).Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();
        }

        private static object FindType(object group, string name)
        {
            if (group == null) return null;
            foreach (var type in OpennessReflection.ReadEnumerableProperty(group, "Types") ?? new object[0])
            {
                if (string.Equals(OpennessReflection.ReadProperty(type, "Name") as string, name, StringComparison.OrdinalIgnoreCase))
                {
                    return type;
                }
            }
            foreach (var child in OpennessReflection.ReadEnumerableProperty(group, "Groups") ?? new object[0])
            {
                var found = FindType(child, name);
                if (found != null) return found;
            }
            return null;
        }

        private static void CompilePlcSoftware(object plcSoftware, UnitStationTypeWriteResult result)
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

        private static void ExportType(object plcType, string xmlPath)
        {
            if (File.Exists(xmlPath)) File.Delete(xmlPath);
            var method = plcType.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(candidate =>
                {
                    var parameters = candidate.GetParameters();
                    return candidate.Name == "Export" && parameters.Length == 2
                        && typeof(FileInfo).IsAssignableFrom(parameters[0].ParameterType)
                        && parameters[1].ParameterType.IsEnum;
                });
            if (method == null) throw new InvalidOperationException("PLC data type XML export is unavailable.");
            var optionType = method.GetParameters()[1].ParameterType;
            var option = EnumValue(optionType, "WithReadOnly") ?? Enum.GetValues(optionType).GetValue(0);
            method.Invoke(plcType, new object[] { new FileInfo(xmlPath), option });
        }

        private static void ImportType(object plcType, string xmlPath)
        {
            var parentGroup = OpennessReflection.ReadProperty(plcType, "Parent");
            var types = OpennessReflection.ReadProperty(parentGroup, "Types");
            var method = types?.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(candidate =>
                {
                    var parameters = candidate.GetParameters();
                    return candidate.Name == "Import" && parameters.Length == 2
                        && typeof(FileInfo).IsAssignableFrom(parameters[0].ParameterType)
                        && parameters[1].ParameterType.IsEnum;
                });
            if (method == null) throw new InvalidOperationException("PLC data type XML import is unavailable.");
            var optionType = method.GetParameters()[1].ParameterType;
            var option = EnumValue(optionType, "Override")
                ?? EnumValue(optionType, "OverwriteAll")
                ?? Enum.GetValues(optionType).GetValue(0);
            method.Invoke(types, new object[] { new FileInfo(xmlPath), option });
        }

        private static object EnumValue(Type enumType, string name)
        {
            return Enum.IsDefined(enumType, name) ? Enum.Parse(enumType, name) : null;
        }

        private static void UpdateInputType(
            string xmlPath, IEnumerable<string> cylinderNames, IEnumerable<string> sensorNames)
        {
            var document = XDocument.Load(xmlPath, LoadOptions.PreserveWhitespace);
            var members = TopLevelMembers(document);
            if (members.Count < MaxCylinders * 2)
            {
                throw new InvalidOperationException($"Input data type in {Path.GetFileName(xmlPath)} has fewer than 32 cylinder members.");
            }

            var cylinders = CleanNames(cylinderNames);
            for (var index = 0; index < MaxCylinders; index++)
            {
                var name = index < cylinders.Count ? cylinders[index] : "气缸";
                members[index * 2].SetAttributeValue("Name", index < cylinders.Count ? name + "感应出" : $"气缸感应出_{index + 1}");
                members[index * 2 + 1].SetAttributeValue("Name", index < cylinders.Count ? name + "感应回" : $"气缸感应回_{index + 1}");
            }

            var sensorMembers = members
                .FirstOrDefault(member => string.Equals((string)member.Attribute("Datatype"), "Struct", StringComparison.OrdinalIgnoreCase)
                    && string.Equals((string)member.Attribute("Name"), "感应", StringComparison.OrdinalIgnoreCase))
                ?.Elements().Where(element => element.Name.LocalName == "Member").ToList();
            if (sensorMembers == null || sensorMembers.Count < MaxSensors)
            {
                sensorMembers = members.Skip(MaxCylinders * 2).Take(MaxSensors).ToList();
            }
            if (sensorMembers.Count < MaxSensors)
            {
                throw new InvalidOperationException($"Input data type in {Path.GetFileName(xmlPath)} has fewer than 16 sensor members.");
            }

            var sensors = CleanNames(sensorNames);
            for (var index = 0; index < MaxSensors; index++)
            {
                sensorMembers[index].SetAttributeValue("Name", index < sensors.Count ? sensors[index] : $"感应{index + 1}");
            }
            document.Save(xmlPath, SaveOptions.DisableFormatting);
        }

        private static void UpdateOutputType(string xmlPath, IEnumerable<string> cylinderNames)
        {
            var document = XDocument.Load(xmlPath, LoadOptions.PreserveWhitespace);
            var members = TopLevelMembers(document);
            if (members.Count < MaxCylinders * 2)
            {
                throw new InvalidOperationException($"Output data type in {Path.GetFileName(xmlPath)} has fewer than 32 cylinder members.");
            }

            var cylinders = CleanNames(cylinderNames);
            for (var index = 0; index < MaxCylinders; index++)
            {
                var name = index < cylinders.Count ? cylinders[index] : "气缸";
                members[index * 2].SetAttributeValue("Name", index < cylinders.Count ? name + "出" : $"气缸出_{index + 1}");
                members[index * 2 + 1].SetAttributeValue("Name", index < cylinders.Count ? name + "回" : $"气缸回_{index + 1}");
            }
            document.Save(xmlPath, SaveOptions.DisableFormatting);
        }

        private static List<XElement> TopLevelMembers(XDocument document)
        {
            var section = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Section"
                && string.Equals((string)element.Attribute("Name"), "None", StringComparison.OrdinalIgnoreCase));
            if (section == null) throw new InvalidOperationException("PLC data type member section was not found.");
            return section.Elements().Where(element => element.Name.LocalName == "Member").ToList();
        }

        private static string MakeFileSafe(string name)
        {
            foreach (var character in Path.GetInvalidFileNameChars()) name = name.Replace(character, '_');
            return name;
        }

        private static UnitStationTypeWriteResult Complete(UnitStationTypeWriteResult result)
        {
            result.Success = result.Errors.Count == 0;
            result.Diagnostic = result.Success
                ? $"Configured station data types for {result.UpdatedStations.Count} station(s)."
                : "Station data type configuration failed: " + string.Join("; ", result.Errors);
            return result;
        }

        private class ConfiguredStation
        {
            public string UnitName { get; set; }
            public UnitStationSettings Settings { get; set; }
        }
    }

    public class UnitStationTypeWriteResult
    {
        public bool Success { get; set; }
        public string Diagnostic { get; set; }
        public int CompilationErrorCount { get; set; }
        public int CompilationWarningCount { get; set; }
        public List<UnitStationTypeUpdate> UpdatedStations { get; set; } = new List<UnitStationTypeUpdate>();
        public List<string> Errors { get; set; } = new List<string>();
    }

    public class UnitStationTypeUpdate
    {
        public string UnitName { get; set; }
        public string StationName { get; set; }
        public string InputDataType { get; set; }
        public string OutputDataType { get; set; }
        public int CylinderCount { get; set; }
        public int SensorCount { get; set; }
    }
}
