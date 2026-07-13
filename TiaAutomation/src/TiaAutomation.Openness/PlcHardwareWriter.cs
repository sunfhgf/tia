using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Linq;
using System.Reflection;
using TiaAutomation.Core.Models;

namespace TiaAutomation.Openness
{
    public class PlcHardwareWriter
    {
        public PlcHardwareWriteResult WritePlcHardware(string projectPath, ProjectSettings settings, string opennessAssemblyPath = null)
        {
            var result = new PlcHardwareWriteResult { ProjectPath = projectPath };
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
                    var inner = WriteOnOpenedProject(project, settings);
                    inner.ProjectPath = projectPath;
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

        public PlcHardwareWriteResult WriteOnOpenedProject(object project, ProjectSettings settings)
        {
            var result = new PlcHardwareWriteResult();
            var plcName = settings?.PlcName?.Trim();
            var plcIp = settings?.PlcIpAddress?.Trim();

            if (string.IsNullOrWhiteSpace(plcName) && string.IsNullOrWhiteSpace(plcIp))
            {
                result.Success = true;
                result.Diagnostic = "No PLC name or IP configured.";
                return result;
            }

            try
            {
                var target = FindFirstPlcDevice(project, result);
                if (target == null)
                {
                    result.Diagnostic = "No PLC device was found in the project.";
                    return result;
                }

                result.Attempts.Add("PLC root: " + Describe(target.Device));
                result.Attempts.Add("PLC item: " + Describe(target.PlcItem));

                if (!string.IsNullOrWhiteSpace(plcName))
                {
                    TrySetName(target.Device, plcName, result, "PLC device");
                    if (target.PlcItem != null && !ReferenceEquals(target.PlcItem, target.Device))
                    {
                        TrySetName(target.PlcItem, plcName, result, "PLC device item");
                    }
                }

                if (!string.IsNullOrWhiteSpace(plcIp))
                {
                    var ipWritten = false;
                    foreach (var candidate in target.ItemsForNetwork.Distinct(new ReferenceEqualityComparer()))
                    {
                        ipWritten |= TrySetIpOnNetworkInterface(candidate, plcIp, result);
                    }

                    if (ipWritten)
                    {
                        result.IpAddress = plcIp;
                    }
                    else
                    {
                        result.Warnings.Add("未找到可写入 IP 的 NetworkInterface/Node，PLC IP 未修改。生成日志中的 attempts 会列出已检查对象。 ");
                    }
                }

                result.Success = result.NameChanged || result.IpChanged;
                result.Diagnostic = result.Success ? "PLC hardware settings written." : "PLC hardware settings were not changed.";
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Diagnostic = ex.GetBaseException().Message;
            }

            return result;
        }

        private static PlcDeviceTarget FindFirstPlcDevice(object project, PlcHardwareWriteResult result)
        {
            foreach (var device in OpennessReflection.ReadEnumerableProperty(project, "Devices") ?? new object[0])
            {
                result.Attempts.Add("Inspect device: " + Describe(device));
                var target = FindInDeviceItems(device, device, OpennessReflection.ReadEnumerableProperty(device, "DeviceItems"), result);
                if (target != null) return target;
            }
            return null;
        }

        private static PlcDeviceTarget FindInDeviceItems(object rootDevice, object item, IEnumerable items, PlcHardwareWriteResult result)
        {
            var allItems = new List<object>();
            if (rootDevice != null) allItems.Add(rootDevice);
            if (item != null && !ReferenceEquals(item, rootDevice)) allItems.Add(item);

            if (items == null) return null;

            foreach (var child in items)
            {
                allItems.Add(child);
                result.Attempts.Add("Inspect item: " + Describe(child));
                var software = GetSoftwareContainer(child, result);
                if (IsPlcSoftware(software))
                {
                    result.Attempts.Add("PLC software found on: " + Describe(child));
                    allItems.AddRange(FlattenDeviceItems(child));
                    return new PlcDeviceTarget { Device = rootDevice, PlcItem = child, ItemsForNetwork = allItems };
                }

                var nested = FindInDeviceItems(rootDevice, child, OpennessReflection.ReadEnumerableProperty(child, "DeviceItems"), result);
                if (nested != null)
                {
                    nested.ItemsForNetwork.Insert(0, child);
                    return nested;
                }
            }

            return null;
        }

        private static IEnumerable<object> FlattenDeviceItems(object item)
        {
            foreach (var child in OpennessReflection.ReadEnumerableProperty(item, "DeviceItems") ?? new object[0])
            {
                yield return child;
                foreach (var nested in FlattenDeviceItems(child)) yield return nested;
            }
        }

        private static object GetSoftwareContainer(object deviceItem, PlcHardwareWriteResult result)
        {
            var container = OpennessReflection.InvokeGenericGetService(deviceItem, "Siemens.Engineering.HW.Features.SoftwareContainer");
            if (container != null)
            {
                result.Attempts.Add("SoftwareContainer service found on: " + Describe(deviceItem));
                return OpennessReflection.ReadProperty(container, "Software");
            }

            var fallback = OpennessReflection.ReadProperty(deviceItem, "SoftwareContainer");
            if (fallback != null)
            {
                result.Attempts.Add("SoftwareContainer property found on: " + Describe(deviceItem));
                return OpennessReflection.ReadProperty(fallback, "Software");
            }

            var software = OpennessReflection.ReadProperty(deviceItem, "Software");
            if (software != null)
            {
                result.Attempts.Add("Software property found on: " + Describe(deviceItem));
            }
            return software;
        }

        private static bool IsPlcSoftware(object software)
        {
            return software != null
                && OpennessReflection.ReadProperty(software, "TagTableGroup") != null
                && OpennessReflection.ReadProperty(software, "BlockGroup") != null;
        }

        private static void TrySetName(object target, string name, PlcHardwareWriteResult result, string label)
        {
            if (target == null) return;
            var before = OpennessReflection.ReadProperty(target, "Name") as string;
            if (string.Equals(before, name, StringComparison.OrdinalIgnoreCase))
            {
                result.NameChanged = true;
                result.PlcName = name;
                result.Attempts.Add($"{label} already named {name}.");
                return;
            }

            if (TrySetProperty(target, "Name", name, result, label) || TrySetAttribute(target, "Name", name, result, label))
            {
                result.NameChanged = true;
                result.PlcName = name;
                result.ChangedObjects.Add($"{label}: {before} -> {name}");
            }
            else
            {
                result.Warnings.Add($"无法修改 {label} 名称：{before ?? target.GetType().Name}");
            }
        }

        private static bool TrySetIpOnNetworkInterface(object item, string ip, PlcHardwareWriteResult result)
        {
            var networkInterface = OpennessReflection.InvokeGenericGetService(item, "Siemens.Engineering.HW.Features.NetworkInterface");
            if (networkInterface == null)
            {
                result.Attempts.Add("No NetworkInterface on: " + Describe(item));
                return false;
            }

            result.Attempts.Add("NetworkInterface found on: " + Describe(item));
            result.Attempts.Add("NetworkInterface type: " + networkInterface.GetType().FullName);
            AddAttributeInfo(result, networkInterface, "NetworkInterface");
            var wrote = false;
            var nodes = (OpennessReflection.ReadEnumerableProperty(networkInterface, "Nodes") ?? new object[0]).Cast<object>().ToList();
            result.Attempts.Add("NetworkInterface nodes: " + nodes.Count);
            foreach (var node in nodes)
            {
                result.Attempts.Add("Node: " + Describe(node) + " writable=" + WritablePropertyList(node));
                AddAttributeInfo(result, node, "NetworkInterface.Node");
                wrote |= TrySetIpOnObject(node, ip, result, "NetworkInterface.Node");
            }

            result.Attempts.Add("NetworkInterface writable=" + WritablePropertyList(networkInterface));
            wrote |= TrySetIpOnObject(networkInterface, ip, result, "NetworkInterface");
            return wrote;
        }

        private static bool TrySetIpOnObject(object target, string ip, PlcHardwareWriteResult result, string label)
        {
            if (target == null) return false;
            foreach (var name in IpAttributeNames())
            {
                var before = OpennessReflection.ReadProperty(target, name)?.ToString();
                foreach (var value in IpValues(ip))
                {
                    if (TrySetProperty(target, name, value, result, label) || TrySetAttribute(target, name, value, result, label))
                    {
                        result.IpChanged = true;
                        result.IpAddress = ip;
                        result.ChangedObjects.Add($"{label}.{name}: {before ?? "<unknown>"} -> {ip}");
                        return true;
                    }
                }
            }
            return false;
        }

        private static IEnumerable<string> IpAttributeNames()
        {
            return new[]
            {
                "Address", "IpAddress", "IPAddress", "IpSuiteAddress", "IpAddressSuite",
                "PnIpAddress", "PNIPAddress", "IpProtocolAddress", "IPProtocolAddress",
                "InterfaceAddress", "StationAddress"
            };
        }

        private static IEnumerable<object> IpValues(string ip)
        {
            yield return ip;
            if (IPAddress.TryParse(ip, out var parsed))
            {
                yield return parsed;
                yield return parsed.GetAddressBytes();
            }
        }

        private static bool TrySetProperty(object target, string propertyName, object value, PlcHardwareWriteResult result, string label)
        {
            try
            {
                var p = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (p == null)
                {
                    result.Attempts.Add($"{label}.{propertyName}: property missing on {target.GetType().Name}");
                    return false;
                }
                if (!p.CanWrite)
                {
                    result.Attempts.Add($"{label}.{propertyName}: property read-only on {target.GetType().Name}");
                    return false;
                }
                p.SetValue(target, value, null);
                result.Attempts.Add($"{label}.{propertyName}: property set ok");
                return true;
            }
            catch (Exception ex)
            {
                result.Attempts.Add($"{label}.{propertyName}: property set failed - {ex.GetBaseException().Message}");
                return false;
            }
        }

        private static bool TrySetAttribute(object target, string attributeName, object value, PlcHardwareWriteResult result, string label)
        {
            try
            {
                var method = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "SetAttribute" && m.GetParameters().Length == 2);
                if (method == null)
                {
                    result.Attempts.Add($"{label}.{attributeName}: SetAttribute missing on {target.GetType().Name}");
                    return false;
                }
                method.Invoke(target, new[] { attributeName, value });
                result.Attempts.Add($"{label}.{attributeName}: SetAttribute ok");
                return true;
            }
            catch (Exception ex)
            {
                result.Attempts.Add($"{label}.{attributeName}: SetAttribute failed - {ex.GetBaseException().Message}");
                return false;
            }
        }

        private static void AddAttributeInfo(PlcHardwareWriteResult result, object target, string label)
        {
            try
            {
                var method = target?.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetAttributeInfos" && m.GetParameters().Length == 0);
                var infos = method?.Invoke(target, null) as IEnumerable;
                if (infos == null) return;

                var parts = new List<string>();
                foreach (var info in infos)
                {
                    var name = OpennessReflection.ReadProperty(info, "Name")?.ToString();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (!LooksRelevantAttribute(name)) continue;

                    var access = OpennessReflection.ReadProperty(info, "AccessMode")?.ToString();
                    var supported = OpennessReflection.ReadProperty(info, "SupportedTypes") as IEnumerable;
                    var types = new List<string>();
                    if (supported != null)
                    {
                        foreach (var type in supported)
                        {
                            types.Add((type as Type)?.Name ?? type?.ToString());
                        }
                    }
                    parts.Add($"{name}[{access}:{string.Join("/", types.Take(4))}]");
                }

                if (parts.Count > 0)
                {
                    result.Attempts.Add($"Attributes {label}: " + string.Join(", ", parts.Take(30)));
                }
            }
            catch (Exception ex)
            {
                result.Attempts.Add($"Attributes {label}: failed - {ex.GetBaseException().Message}");
            }
        }

        private static bool LooksRelevantAttribute(string name)
        {
            return name.IndexOf("Address", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Ip", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("IP", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Pn", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Name", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        private static string Describe(object target)
        {
            if (target == null) return "<null>";
            var name = OpennessReflection.ReadProperty(target, "Name") as string;
            return $"{target.GetType().FullName} Name={name ?? "<none>"}";
        }

        private static string WritablePropertyList(object target)
        {
            if (target == null) return "<null>";
            return string.Join(",", target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite)
                .Select(p => p.Name)
                .Take(20));
        }

        private class PlcDeviceTarget
        {
            public object Device { get; set; }
            public object PlcItem { get; set; }
            public List<object> ItemsForNetwork { get; set; } = new List<object>();
        }

        private class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }

    public class PlcHardwareWriteResult
    {
        public string ProjectPath { get; set; }
        public bool Success { get; set; }
        public string Diagnostic { get; set; }
        public bool NameChanged { get; set; }
        public bool IpChanged { get; set; }
        public string PlcName { get; set; }
        public string IpAddress { get; set; }
        public List<string> ChangedObjects { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Attempts { get; set; } = new List<string>();
    }
}



