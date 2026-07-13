using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TiaAutomation.Core.Models;

namespace TiaAutomation.Openness
{
    public class DeviceWriter
    {
        public DeviceWriteResult WriteOnOpenedProject(object tiaPortal, object project, IEnumerable<DeviceRequest> devices)
        {
            var result = new DeviceWriteResult();
            var list = (devices ?? Enumerable.Empty<DeviceRequest>()).Where(d => !string.IsNullOrWhiteSpace(d.Name)).ToList();
            if (list.Count == 0)
            {
                result.Success = true;
                result.Diagnostic = "No devices configured.";
                return result;
            }

            try
            {
                var deviceComposition = OpennessReflection.ReadProperty(project, "Devices");
                if (deviceComposition == null)
                {
                    result.Diagnostic = "Project.Devices is not available.";
                    return result;
                }

                var hardwareCatalog = OpennessReflection.ReadProperty(tiaPortal, "HardwareCatalog");
                if (hardwareCatalog == null)
                {
                    result.Diagnostic = "TIA HardwareCatalog is not available.";
                    return result;
                }

                foreach (var request in list)
                {
                    CreateOrUpdateDevice(deviceComposition, hardwareCatalog, request, result);
                }

                result.Success = result.Errors.Count == 0;
                result.Diagnostic = result.Success ? "Configured hardware devices." : "Some hardware devices could not be created.";
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Diagnostic = ex.GetBaseException().Message;
            }

            return result;
        }

        private static void CreateOrUpdateDevice(object deviceComposition, object hardwareCatalog, DeviceRequest request, DeviceWriteResult result)
        {
            var existing = FindDevice(deviceComposition, request.Name);
            if (existing != null)
            {
                result.SkippedDevices.Add(request.Name + " already exists");
                TryApplyNetworkSettings(existing, request, result);
                return;
            }

            var candidates = FindCatalogCandidates(hardwareCatalog, request, result);
            if (candidates.Count == 0)
            {
                result.Errors.Add($"{request.Name}: no HardwareCatalog entry found for {request.DeviceType}");
                return;
            }

            var createMethod = deviceComposition.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "CreateWithItem" && m.GetParameters().Length == 3);
            if (createMethod == null)
            {
                result.Errors.Add($"{request.Name}: Devices.CreateWithItem method was not found.");
                return;
            }

            foreach (var candidate in candidates.Take(25))
            {
                var typeIdentifier = ReadString(candidate, "TypeIdentifier");
                if (string.IsNullOrWhiteSpace(typeIdentifier)) continue;

                try
                {
                    result.Attempts.Add($"{request.Name}: CreateWithItem {ShortCatalog(candidate)}");
                    var created = createMethod.Invoke(deviceComposition, new object[] { typeIdentifier, request.Name, request.Name });
                    result.CreatedDevices.Add(new CreatedDeviceInfo
                    {
                        Name = request.Name,
                        TypeIdentifier = typeIdentifier,
                        CatalogTypeName = ReadString(candidate, "TypeName"),
                        ArticleNumber = ReadString(candidate, "ArticleNumber")
                    });
                    TryApplyNetworkSettings(created, request, result);
                    return;
                }
                catch (Exception ex)
                {
                    result.Attempts.Add($"{request.Name}: create failed with {ShortCatalog(candidate)} - {ex.GetBaseException().Message}");
                }
            }

            result.Errors.Add($"{request.Name}: all HardwareCatalog create attempts failed. First candidates: " + string.Join(" | ", candidates.Take(5).Select(ShortCatalog)));
        }

        private static object FindDevice(object deviceComposition, string name)
        {
            var find = deviceComposition.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "Find" && m.GetParameters().Length == 1);
            if (find != null)
            {
                try { return find.Invoke(deviceComposition, new object[] { name }); } catch { }
            }

            foreach (var device in deviceComposition as IEnumerable ?? new object[0])
            {
                if (string.Equals(ReadString(device, "Name"), name, StringComparison.OrdinalIgnoreCase)) return device;
            }
            return null;
        }

        private static List<object> FindCatalogCandidates(object hardwareCatalog, DeviceRequest request, DeviceWriteResult result)
        {
            var terms = BuildSearchTerms(request).Distinct(StringComparer.OrdinalIgnoreCase).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            var found = new List<object>();
            foreach (var term in terms)
            {
                foreach (var entry in CatalogFind(hardwareCatalog, term))
                {
                    if (entry == null) continue;
                    if (found.Any(x => string.Equals(ReadString(x, "TypeIdentifier"), ReadString(entry, "TypeIdentifier"), StringComparison.OrdinalIgnoreCase))) continue;
                    found.Add(entry);
                }
                result.Attempts.Add($"{request.Name}: catalog search '{term}' -> {found.Count} total candidates");
                if (found.Count >= 25) break;
            }

            var ranked = found
                .Select(entry => new { Entry = entry, Score = ScoreCatalogEntry(entry, request) })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => ReadString(x.Entry, "TypeName"))
                .Select(x => x.Entry)
                .ToList();

            foreach (var entry in ranked.Take(10))
            {
                result.CatalogCandidates.Add(ShortCatalog(entry));
            }
            return ranked;
        }

        private static IEnumerable<string> BuildSearchTerms(DeviceRequest request)
        {
            yield return request.OrderNumber;
            yield return request.AccessPointId;
            yield return request.ModuleIdentNumber;
            yield return request.DeviceId;
            yield return request.DeviceType;
            if (!string.IsNullOrWhiteSpace(request.GsdFileName))
            {
                yield return request.GsdFileName;
                yield return request.GsdFileName.Replace(".xml", string.Empty).Replace("GSDML-", string.Empty).Replace("gsdml-", string.Empty);
            }
            foreach (var part in (request.DeviceType ?? string.Empty).Split(new[] { '/', ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.Length >= 3) yield return part.Trim();
            }
        }

        private static IEnumerable<object> CatalogFind(object hardwareCatalog, string filter)
        {
            var find = hardwareCatalog.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "Find" && m.GetParameters().Length == 1);
            if (find == null) yield break;

            IEnumerable rows = null;
            try { rows = find.Invoke(hardwareCatalog, new object[] { filter }) as IEnumerable; }
            catch { }
            if (rows == null) yield break;
            foreach (var row in rows) yield return row;
        }

        private static int ScoreCatalogEntry(object entry, DeviceRequest request)
        {
            var text = string.Join(" ", new[]
            {
                ReadString(entry, "ArticleNumber"), ReadString(entry, "CatalogPath"), ReadString(entry, "Description"),
                ReadString(entry, "TypeIdentifier"), ReadString(entry, "TypeIdentifierNormalized"), ReadString(entry, "TypeName"), ReadString(entry, "Version")
            }).ToLowerInvariant();

            var score = 0;
            AddScore(request.OrderNumber, 80);
            AddScore(request.AccessPointId, 45);
            AddScore(request.ModuleIdentNumber, 40);
            AddScore(request.DeviceId, 35);
            AddScore(request.GsdFileName, 30);
            AddScore(request.DeviceType, 20);
            foreach (var part in (request.DeviceType ?? string.Empty).Split(new[] { '/', ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                AddScore(part.Trim(), part.Length >= 5 ? 25 : 8);
            }
            if (text.Contains("gsd")) score += 10;
            if (text.Contains("device")) score += 5;
            return score;

            void AddScore(string value, int points)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                var normalized = value.ToLowerInvariant();
                if (text.Contains(normalized)) score += points;
                if (normalized.EndsWith(".xml") && text.Contains(normalized.Replace(".xml", string.Empty))) score += points / 2;
            }
        }

        private static void TryApplyNetworkSettings(object device, DeviceRequest request, DeviceWriteResult result)
        {
            if (device == null) return;
            TrySetString(device, "Name", request.Name, result, request.Name);
            foreach (var item in FlattenDeviceItems(device).Prepend(device).Distinct(new ReferenceEqualityComparer()))
            {
                var networkInterface = OpennessReflection.InvokeGenericGetService(item, "Siemens.Engineering.HW.Features.NetworkInterface");
                if (networkInterface == null) continue;
                result.Attempts.Add($"{request.Name}: NetworkInterface found on {Describe(item)}");
                TrySetNetworkValue(networkInterface, "PnDeviceName", request.ProfinetName, result, request.Name);
                TrySetNetworkValue(networkInterface, "ProfinetDeviceName", request.ProfinetName, result, request.Name);
                TrySetNetworkValue(networkInterface, "NameOfStation", request.ProfinetName, result, request.Name);
                TrySetNetworkValue(networkInterface, "Address", request.IpAddress, result, request.Name);
                TrySetNetworkValue(networkInterface, "IpAddress", request.IpAddress, result, request.Name);
                foreach (var node in (OpennessReflection.ReadEnumerableProperty(networkInterface, "Nodes") ?? new object[0]).Cast<object>())
                {
                    TrySetNetworkValue(node, "PnDeviceName", request.ProfinetName, result, request.Name);
                    TrySetNetworkValue(node, "ProfinetDeviceName", request.ProfinetName, result, request.Name);
                    TrySetNetworkValue(node, "NameOfStation", request.ProfinetName, result, request.Name);
                    TrySetNetworkValue(node, "Address", request.IpAddress, result, request.Name);
                    TrySetNetworkValue(node, "IpAddress", request.IpAddress, result, request.Name);
                }
            }
        }

        private static IEnumerable<object> FlattenDeviceItems(object item)
        {
            foreach (var child in OpennessReflection.ReadEnumerableProperty(item, "DeviceItems") ?? new object[0])
            {
                yield return child;
                foreach (var nested in FlattenDeviceItems(child)) yield return nested;
            }
        }

        private static bool TrySetNetworkValue(object target, string attribute, string value, DeviceWriteResult result, string deviceName)
        {
            if (target == null || string.IsNullOrWhiteSpace(value)) return false;
            if (TrySetString(target, attribute, value, result, deviceName)) return true;
            return false;
        }

        private static bool TrySetString(object target, string name, string value, DeviceWriteResult result, string deviceName)
        {
            if (target == null || string.IsNullOrWhiteSpace(value)) return false;
            try
            {
                var prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(target, value, null);
                    result.ChangedObjects.Add($"{deviceName}: {Describe(target)}.{name} = {value}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                result.Attempts.Add($"{deviceName}: property {name} failed on {Describe(target)} - {ex.GetBaseException().Message}");
            }

            try
            {
                var method = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "SetAttribute" && m.GetParameters().Length == 2);
                if (method == null) return false;
                method.Invoke(target, new object[] { name, value });
                result.ChangedObjects.Add($"{deviceName}: {Describe(target)}.{name} = {value}");
                return true;
            }
            catch (Exception ex)
            {
                result.Attempts.Add($"{deviceName}: attribute {name} failed on {Describe(target)} - {ex.GetBaseException().Message}");
                return false;
            }
        }

        private static string ReadString(object target, string propertyName)
        {
            return OpennessReflection.ReadProperty(target, propertyName)?.ToString();
        }

        private static string ShortCatalog(object entry)
        {
            return $"{ReadString(entry, "TypeName")} | Article={ReadString(entry, "ArticleNumber")} | Version={ReadString(entry, "Version")} | TypeId={ReadString(entry, "TypeIdentifier")}";
        }

        private static string Describe(object target)
        {
            if (target == null) return "<null>";
            return target.GetType().Name + "(" + (ReadString(target, "Name") ?? "") + ")";
        }

        private class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }

    public class DeviceWriteResult
    {
        public bool Success { get; set; }
        public string Diagnostic { get; set; }
        public List<CreatedDeviceInfo> CreatedDevices { get; set; } = new List<CreatedDeviceInfo>();
        public List<string> SkippedDevices { get; set; } = new List<string>();
        public List<string> ChangedObjects { get; set; } = new List<string>();
        public List<string> CatalogCandidates { get; set; } = new List<string>();
        public List<string> Attempts { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
    }

    public class CreatedDeviceInfo
    {
        public string Name { get; set; }
        public string TypeIdentifier { get; set; }
        public string CatalogTypeName { get; set; }
        public string ArticleNumber { get; set; }
    }
}

