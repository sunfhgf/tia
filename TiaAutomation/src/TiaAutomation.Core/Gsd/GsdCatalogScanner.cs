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

                if (!file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var document = XDocument.Load(file);
                    var root = document.Root;
                    if (root == null)
                    {
                        continue;
                    }

                    var ns = root.Name.Namespace;
                    var identity = document.Descendants(ns + "DeviceIdentity").FirstOrDefault();
                    var function = document.Descendants(ns + "DeviceFunction").FirstOrDefault();
                    var family = function?.Element(ns + "Family");
                    var vendorName = identity?.Element(ns + "VendorName")?.Attribute("Value")?.Value;

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
                        info.AccessPoints.Add(new GsdAccessPointInfo
                        {
                            Id = dap.Attribute("ID")?.Value,
                            DnsCompatibleName = dap.Attribute("DNS_CompatibleName")?.Value,
                            ModuleIdentNumber = dap.Attribute("ModuleIdentNumber")?.Value,
                            OrderNumber = moduleInfo?.Element(ns + "OrderNumber")?.Attribute("Value")?.Value,
                            HardwareRelease = moduleInfo?.Element(ns + "HardwareRelease")?.Attribute("Value")?.Value,
                            SoftwareRelease = moduleInfo?.Element(ns + "SoftwareRelease")?.Attribute("Value")?.Value
                        });
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
    }

    public class GsdScanResult
    {
        public List<GsdDeviceInfo> Devices { get; set; } = new List<GsdDeviceInfo>();
        public List<string> IgnoredFiles { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }
}
