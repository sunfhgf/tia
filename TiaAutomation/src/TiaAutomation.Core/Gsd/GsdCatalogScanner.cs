using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace TiaAutomation.Core.Gsd
{
    public class GsdCatalogScanner
    {
        public GsdScanResult Scan(string gsdDirectory)
        {
            var result = new GsdScanResult();

            if (string.IsNullOrWhiteSpace(gsdDirectory) || !Directory.Exists(gsdDirectory))
            {
                result.Warnings.Add($"GSD directory not found: {gsdDirectory}");
                return result;
            }

            foreach (var file in Directory.EnumerateFiles(gsdDirectory, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".baiduyun.uploading.cfg", StringComparison.OrdinalIgnoreCase))
                {
                    result.IgnoredFiles.Add(file);
                    continue;
                }

                if (!file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    var document = XDocument.Load(file);
                    var root = document.Root;
                    if (root == null) continue;

                    var ns = root.Name.Namespace;
                    var identity = document.Descendants(ns + "DeviceIdentity").FirstOrDefault();
                    var function = document.Descendants(ns + "DeviceFunction").FirstOrDefault();
                    var family = function?.Element(ns + "Family");
                    var vendorName = identity?.Element(ns + "VendorName")?.Attribute("Value")?.Value;
                    var texts = BuildPrimaryTextMap(document, ns);
                    var modulesById = document.Descendants(ns + "ModuleItem")
                        .Where(x => x.Attribute("ID") != null)
                        .GroupBy(x => x.Attribute("ID").Value, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
                    var submodulesById = document.Descendants(ns + "SubmoduleItem")
                        .Where(x => x.Attribute("ID") != null)
                        .GroupBy(x => x.Attribute("ID").Value, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

                    var info = new GsdDeviceInfo
                    {
                        FilePath = file,
                        FileName = Path.GetFileName(file),
                        VendorName = vendorName,
                        VendorId = identity?.Attribute("VendorID")?.Value,
                        DeviceId = identity?.Attribute("DeviceID")?.Value,
                        MainFamily = family?.Attribute("MainFamily")?.Value,
                        ProductFamily = family?.Attribute("ProductFamily")?.Value
                    };

                    foreach (var dap in document.Descendants(ns + "DeviceAccessPointItem"))
                    {
                        var moduleInfo = dap.Element(ns + "ModuleInfo");
                        var accessPoint = new GsdAccessPointInfo
                        {
                            Id = dap.Attribute("ID")?.Value,
                            DnsCompatibleName = dap.Attribute("DNS_CompatibleName")?.Value,
                            ModuleIdentNumber = dap.Attribute("ModuleIdentNumber")?.Value,
                            OrderNumber = moduleInfo?.Element(ns + "OrderNumber")?.Attribute("Value")?.Value,
                            HardwareRelease = moduleInfo?.Element(ns + "HardwareRelease")?.Attribute("Value")?.Value,
                            SoftwareRelease = moduleInfo?.Element(ns + "SoftwareRelease")?.Attribute("Value")?.Value
                        };

                        var moduleRefs = dap.Element(ns + "UseableModules")?.Elements(ns + "ModuleItemRef")
                            ?? Enumerable.Empty<XElement>();
                        foreach (var moduleRef in moduleRefs)
                        {
                            var target = moduleRef.Attribute("ModuleItemTarget")?.Value;
                            if (string.IsNullOrWhiteSpace(target) || !modulesById.TryGetValue(target, out var module)) continue;
                            accessPoint.Modules.Add(ParseModule(module, moduleRef, submodulesById, texts, ns));
                        }

                        info.AccessPoints.Add(accessPoint);
                    }

                    result.Devices.Add(info);
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Failed to parse GSDML '{file}': {ex.Message}");
                }
            }

            return result;
        }

        private static GsdModuleInfo ParseModule(
            XElement module,
            XElement moduleRef,
            IDictionary<string, XElement> submodulesById,
            IDictionary<string, string> texts,
            XNamespace ns)
        {
            var moduleInfo = module.Element(ns + "ModuleInfo");
            var parsed = new GsdModuleInfo
            {
                Id = module.Attribute("ID")?.Value,
                Name = ResolveName(moduleInfo, texts, ns, module.Attribute("ID")?.Value),
                ModuleIdentNumber = module.Attribute("ModuleIdentNumber")?.Value,
                OrderNumber = moduleInfo?.Element(ns + "OrderNumber")?.Attribute("Value")?.Value,
                AllowedInSlots = moduleRef.Attribute("AllowedInSlots")?.Value,
                InputLength = CalculateIoLength(module, "Input", ns),
                OutputLength = CalculateIoLength(module, "Output", ns)
            };

            var submoduleRefs = module.Element(ns + "UseableSubmodules")?.Elements(ns + "SubmoduleItemRef")
                ?? Enumerable.Empty<XElement>();
            foreach (var submoduleRef in submoduleRefs)
            {
                var target = submoduleRef.Attribute("SubmoduleItemTarget")?.Value;
                if (string.IsNullOrWhiteSpace(target) || !submodulesById.TryGetValue(target, out var submodule)) continue;
                var submoduleInfo = submodule.Element(ns + "ModuleInfo");
                parsed.Submodules.Add(new GsdSubmoduleInfo
                {
                    Id = target,
                    Name = ResolveName(submoduleInfo, texts, ns, target),
                    SubmoduleIdentNumber = submodule.Attribute("SubmoduleIdentNumber")?.Value,
                    AllowedInSubslots = submoduleRef.Attribute("AllowedInSubslots")?.Value,
                    InputLength = CalculateIoLength(submodule, "Input", ns),
                    OutputLength = CalculateIoLength(submodule, "Output", ns)
                });
            }

            return parsed;
        }

        private static Dictionary<string, string> BuildPrimaryTextMap(XDocument document, XNamespace ns)
        {
            var primaryLanguage = document.Descendants(ns + "PrimaryLanguage").FirstOrDefault();
            var textElements = primaryLanguage?.Elements(ns + "Text") ?? document.Descendants(ns + "Text");
            return textElements
                .Where(x => x.Attribute("TextId") != null && x.Attribute("Value") != null)
                .GroupBy(x => x.Attribute("TextId").Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First().Attribute("Value").Value, StringComparer.OrdinalIgnoreCase);
        }

        private static string ResolveName(XElement moduleInfo, IDictionary<string, string> texts, XNamespace ns, string fallback)
        {
            var textId = moduleInfo?.Element(ns + "Name")?.Attribute("TextId")?.Value;
            if (!string.IsNullOrWhiteSpace(textId) && texts.TryGetValue(textId, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
            return fallback ?? string.Empty;
        }

        private static int CalculateIoLength(XElement element, string direction, XNamespace ns)
        {
            var bits = 0;
            foreach (var ioData in element.DescendantsAndSelf(ns + "IOData"))
            {
                var data = ioData.Element(ns + direction);
                if (data == null) continue;
                foreach (var item in data.Descendants(ns + "DataItem"))
                {
                    bits += DataItemBitLength(item);
                }
            }
            return (bits + 7) / 8;
        }

        private static int DataItemBitLength(XElement item)
        {
            var dataType = item.Attribute("DataType")?.Value ?? string.Empty;
            var length = 1;
            int.TryParse(item.Attribute("Length")?.Value, out length);
            if (length <= 0) length = 1;

            switch (dataType.ToLowerInvariant())
            {
                case "bit":
                case "boolean": return length;
                case "integer8":
                case "unsigned8":
                case "char": return 8 * length;
                case "integer16":
                case "unsigned16": return 16 * length;
                case "integer32":
                case "unsigned32":
                case "float32": return 32 * length;
                case "integer64":
                case "unsigned64":
                case "float64": return 64 * length;
                case "octetstring":
                case "visiblestring": return 8 * length;
                default: return 8 * length;
            }
        }
    }

    public class GsdScanResult
    {
        public List<GsdDeviceInfo> Devices { get; set; } = new List<GsdDeviceInfo>();
        public List<string> IgnoredFiles { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }
}