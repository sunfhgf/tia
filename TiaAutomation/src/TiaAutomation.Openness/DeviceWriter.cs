using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TiaAutomation.Core.Gsd;
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
                var ungroupedDevicesGroup = OpennessReflection.ReadProperty(project, "UngroupedDevicesGroup");
                var deviceComposition = OpennessReflection.ReadProperty(ungroupedDevicesGroup, "Devices")
                    ?? OpennessReflection.ReadProperty(project, "Devices");
                if (deviceComposition == null)
                {
                    result.Diagnostic = "Project.UngroupedDevicesGroup.Devices is not available.";
                    return result;
                }
                result.Attempts.Add($"Device target group: {Describe(ungroupedDevicesGroup)}");
                var knownDevices = CollectKnownDevices(project, deviceComposition);

                var hardwareCatalog = OpennessReflection.ReadProperty(tiaPortal, "HardwareCatalog");
                if (hardwareCatalog == null)
                {
                    result.Diagnostic = "TIA HardwareCatalog is not available.";
                    return result;
                }

                var targetIoSystem = FindPreferredIoSystem(project, result);
                if (targetIoSystem == null)
                {
                    result.Attempts.Add("No PLC IO-System found; created devices may stay unassigned in network view.");
                }

                var claimedDevices = new HashSet<object>(new ReferenceEqualityComparer());
                foreach (var request in list)
                {
                    CreateOrUpdateDevice(knownDevices, deviceComposition, hardwareCatalog, request, result, targetIoSystem, claimedDevices);
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

        private static void CreateOrUpdateDevice(List<object> knownDevices, object targetDeviceComposition, object hardwareCatalog, DeviceRequest request, DeviceWriteResult result, object targetIoSystem, HashSet<object> claimedDevices)
        {
            var exactName = FindDevice(knownDevices, request.Name);
            var existing = FindBestExistingDevice(knownDevices, request, targetIoSystem, claimedDevices, result);
            if (existing != null)
            {
                claimedDevices.Add(existing);
                var oldName = ReadString(existing, "Name") ?? "<unnamed>";
                result.SkippedDevices.Add(string.Equals(oldName, request.Name, StringComparison.OrdinalIgnoreCase)
                    ? request.Name + " already exists; normalized as PLC IO device"
                    : $"{oldName} matched configured GSD; reusing as {request.Name}");

                if (exactName != null
                    && !ReferenceEquals(exactName, existing)
                    && DeviceMatchesRequest(exactName, request)
                    && !IsConnectedToIoSystem(exactName, targetIoSystem))
                {
                    TryDeleteStaleDuplicate(exactName, request.Name, result);
                }

                TryApplyNetworkSettings(existing, request, result, targetIoSystem);
                TryConfigureModules(existing, request, hardwareCatalog, result);
                return;
            }

            var candidates = FindCatalogCandidates(hardwareCatalog, request, result);
            if (candidates.Count == 0)
            {
                result.Errors.Add($"{request.Name}: no HardwareCatalog entry found for {request.DeviceType}");
                return;
            }

            var createMethod = targetDeviceComposition.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
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
                    var created = createMethod.Invoke(targetDeviceComposition, new object[] { typeIdentifier, request.Name, request.Name });
                    result.CreatedDevices.Add(new CreatedDeviceInfo
                    {
                        Name = request.Name,
                        TypeIdentifier = typeIdentifier,
                        CatalogTypeName = ReadString(candidate, "TypeName"),
                        ArticleNumber = ReadString(candidate, "ArticleNumber")
                    });
                    knownDevices.Add(created);
                    claimedDevices.Add(created);
                    TryApplyNetworkSettings(created, request, result, targetIoSystem);
                    TryConfigureModules(created, request, hardwareCatalog, result);
                    return;
                }
                catch (Exception ex)
                {
                    result.Attempts.Add($"{request.Name}: create failed with {ShortCatalog(candidate)} - {ex.GetBaseException().Message}");
                }
            }

            result.Errors.Add($"{request.Name}: all HardwareCatalog create attempts failed. First candidates: " + string.Join(" | ", candidates.Take(5).Select(ShortCatalog)));
        }

        private static object FindBestExistingDevice(IEnumerable<object> knownDevices, DeviceRequest request, object targetIoSystem, HashSet<object> claimedDevices, DeviceWriteResult result)
        {
            var devices = knownDevices
                .Where(device => !claimedDevices.Contains(device))
                .Select(device => new
                {
                    Device = device,
                    ExactName = string.Equals(ReadString(device, "Name"), request.Name, StringComparison.OrdinalIgnoreCase),
                    ModelMatch = DeviceMatchesRequest(device, request),
                    Connected = IsConnectedToIoSystem(device, targetIoSystem)
                })
                .Where(x => x.ModelMatch || x.ExactName)
                .OrderByDescending(x => x.ModelMatch && x.Connected)
                .ThenByDescending(x => x.ExactName && x.ModelMatch)
                .ThenByDescending(x => x.Connected)
                .ThenByDescending(x => x.ExactName)
                .ToList();

            foreach (var candidate in devices)
            {
                result.Attempts.Add($"{request.Name}: existing candidate {Describe(candidate.Device)}, modelMatch={candidate.ModelMatch}, connected={candidate.Connected}, exactName={candidate.ExactName}");
            }

            var best = devices.FirstOrDefault();
            if (best == null) return null;
            if (!best.ModelMatch && best.ExactName)
            {
                result.Errors.Add($"{request.Name}: an existing device has the requested name but a different GSD model.");
                return null;
            }
            return best.Device;
        }

        private static bool IsConnectedToIoSystem(object device, object targetIoSystem)
        {
            if (device == null) return false;
            foreach (var item in FlattenDeviceItems(device).Prepend(device).Distinct(new ReferenceEqualityComparer()))
            {
                var networkInterface = OpennessReflection.InvokeGenericGetService(item, "Siemens.Engineering.HW.Features.NetworkInterface");
                if (networkInterface == null) continue;
                foreach (var connector in (OpennessReflection.ReadEnumerableProperty(networkInterface, "IoConnectors") ?? new object[0]).Cast<object>())
                {
                    var current = OpennessReflection.ReadProperty(connector, "ConnectedToIoSystem");
                    if (current != null && (targetIoSystem == null || SameEngineeringObject(current, targetIoSystem))) return true;
                }
            }
            return false;
        }

        private static void TryDeleteStaleDuplicate(object device, string deviceName, DeviceWriteResult result)
        {
            try
            {
                var delete = device.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "Delete" && m.GetParameters().Length == 0);
                if (delete == null)
                {
                    result.Attempts.Add($"{deviceName}: stale duplicate {Describe(device)} has no Delete method");
                    return;
                }
                var oldName = ReadString(device, "Name") ?? "<unnamed>";
                delete.Invoke(device, null);
                result.ChangedObjects.Add($"{deviceName}: removed stale unassigned duplicate {oldName}");
            }
            catch (Exception ex)
            {
                result.Attempts.Add($"{deviceName}: deleting stale duplicate {Describe(device)} failed - {ex.GetBaseException().Message}");
            }
        }

        private static bool DeviceMatchesRequest(object device, DeviceRequest request)
        {
            var text = DeviceSearchText(device);
            if (string.IsNullOrWhiteSpace(text)) return false;

            if (ContainsTerm(text, request.OrderNumber)) return true;
            if (ContainsTerm(text, request.AccessPointId)) return true;
            if (ContainsTerm(text, request.DeviceId)) return true;
            if (ContainsTerm(text, request.GsdFileName)) return true;
            if (!string.IsNullOrWhiteSpace(request.GsdFileName)
                && ContainsTerm(text, request.GsdFileName.Replace(".xml", string.Empty).Replace("GSDML-", string.Empty).Replace("gsdml-", string.Empty))) return true;

            var typeParts = (request.DeviceType ?? string.Empty)
                .Split(new[] { '/', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length >= 5)
                .ToList();
            return typeParts.Count > 0 && typeParts.Count(p => ContainsTerm(text, p)) >= Math.Min(2, typeParts.Count);
        }

        private static string DeviceSearchText(object device)
        {
            var parts = new List<string>();
            AddIf(parts, ReadString(device, "Name"));
            AddIf(parts, ReadString(device, "TypeIdentifier"));
            foreach (var item in FlattenDeviceItems(device).Prepend(device).Distinct(new ReferenceEqualityComparer()))
            {
                AddIf(parts, ReadString(item, "Name"));
                AddIf(parts, ReadString(item, "TypeIdentifier"));
                var gsd = OpennessReflection.InvokeGenericGetService(item, "Siemens.Engineering.HW.Features.GsdDeviceItem");
                if (gsd == null) continue;
                AddIf(parts, ReadString(gsd, "GsdId"));
                AddIf(parts, ReadString(gsd, "GsdName"));
                AddIf(parts, ReadString(gsd, "GsdType"));
            }
            return string.Join(" ", parts).ToLowerInvariant();
        }

        private static bool ContainsTerm(string text, string term)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(term)) return false;
            var normalized = term.Trim().ToLowerInvariant();
            if (text.Contains(normalized)) return true;
            if (normalized.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                return text.Contains(normalized.Substring(0, normalized.Length - 4));
            }
            return false;
        }

        private static void AddIf(List<string> list, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) list.Add(value);
        }
        private static object FindDevice(IEnumerable<object> knownDevices, string name)
        {
            return knownDevices.FirstOrDefault(device =>
                string.Equals(ReadString(device, "Name"), name, StringComparison.OrdinalIgnoreCase));
        }

        private static List<object> CollectKnownDevices(object project, object targetDeviceComposition)
        {
            var devices = new List<object>();
            var seen = new HashSet<object>(new ReferenceEqualityComparer());

            AddDevices(OpennessReflection.ReadProperty(project, "Devices"));
            AddDevices(targetDeviceComposition);
            AddGroups(OpennessReflection.ReadProperty(project, "DeviceGroups"));
            return devices;

            void AddDevices(object composition)
            {
                foreach (var device in composition as IEnumerable ?? new object[0])
                {
                    if (device != null && seen.Add(device)) devices.Add(device);
                }
            }

            void AddGroups(object groups)
            {
                foreach (var group in groups as IEnumerable ?? new object[0])
                {
                    AddDevices(OpennessReflection.ReadProperty(group, "Devices"));
                    AddGroups(OpennessReflection.ReadProperty(group, "Groups"));
                }
            }
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

        private static void TryApplyNetworkSettings(object device, DeviceRequest request, DeviceWriteResult result, object targetIoSystem)
        {
            if (device == null) return;
            SynchronizeDeviceNames(device, request, result);

            var networkInterfaces = 0;
            foreach (var item in FlattenDeviceItems(device).Prepend(device).Distinct(new ReferenceEqualityComparer()))
            {
                TrySetNetworkValue(item, "PnDeviceName", request.ProfinetName, result, request.Name);
                TrySetNetworkValue(item, "ProfinetDeviceName", request.ProfinetName, result, request.Name);
                TrySetNetworkValue(item, "NameOfStation", request.ProfinetName, result, request.Name);

                var networkInterface = OpennessReflection.InvokeGenericGetService(item, "Siemens.Engineering.HW.Features.NetworkInterface");
                if (networkInterface == null) continue;
                networkInterfaces++;
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
                TryConnectToControllerIoSystem(networkInterface, targetIoSystem, request.Name, result);
            }
            if (networkInterfaces == 0)
            {
                result.Errors.Add($"{request.Name}: no NetworkInterface found; cannot connect to PLC IO-System.");
            }
            else if (targetIoSystem != null && !IsConnectedToIoSystem(device, targetIoSystem))
            {
                result.Errors.Add($"{request.Name}: device remains unassigned after connecting to PLC IO-System.");
            }
            else if (targetIoSystem != null)
            {
                result.ChangedObjects.Add($"{request.Name}: verified as distributed IO under PLC IO-System {ReadString(targetIoSystem, "Name")}");
            }
        }

        private static void SynchronizeDeviceNames(object device, DeviceRequest request, DeviceWriteResult result)
        {
            TrySetString(device, "Name", request.Name, result, request.Name);

            var mainItem = (OpennessReflection.ReadEnumerableProperty(device, "DeviceItems") ?? new object[0])
                .Cast<object>()
                .FirstOrDefault();
            if (mainItem == null)
            {
                result.Attempts.Add($"{request.Name}: device has no main DeviceItem to rename");
                return;
            }

            var renamedItems = new HashSet<object>(new ReferenceEqualityComparer());
            TrySetString(mainItem, "Name", request.Name, result, request.Name);
            renamedItems.Add(mainItem);

            foreach (var item in FlattenDeviceItems(device))
            {
                var networkInterface = OpennessReflection.InvokeGenericGetService(item, "Siemens.Engineering.HW.Features.NetworkInterface");
                if (networkInterface == null) continue;

                var stationItem = item;
                var parent = OpennessReflection.ReadProperty(item, "Parent");
                if (parent != null && parent.GetType().FullName.IndexOf("DeviceItem", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    stationItem = parent;
                }

                if (renamedItems.Add(stationItem))
                {
                    var oldStationName = ReadString(stationItem, "Name") ?? "<unnamed>";
                    TrySetString(stationItem, "Name", request.Name, result, request.Name);
                    result.ChangedObjects.Add($"{request.Name}: network-view station name {oldStationName} -> {request.Name}");
                }
                result.Attempts.Add($"{request.Name}: NetworkInterface owner {Describe(item)}, station item {Describe(stationItem)}");
            }

            result.Attempts.Add($"{request.Name}: synchronized TIA device, main item and network-view station names");
        }

        private static void TryConfigureModules(object device, DeviceRequest request, object hardwareCatalog, DeviceWriteResult result)
        {
            var modules = BuildEffectiveModuleRequests(request, result);
            if (modules.Count == 0) return;

            foreach (var moduleRequest in modules.OrderBy(x => x.Slot))
            {
                if (moduleRequest.Slot <= 0 || string.IsNullOrWhiteSpace(moduleRequest.ModuleId))
                {
                    result.Errors.Add($"{request.Name}: invalid module configuration '{moduleRequest.Name}' at slot {moduleRequest.Slot}.");
                    continue;
                }

                var moduleItem = FindConfiguredItem(FlattenDeviceItems(device), moduleRequest.ModuleId, moduleRequest.ModuleIdentNumber, moduleRequest.Slot);
                if (moduleItem == null)
                {
                    var occupiedModule = FindOccupiedHardwareItem(FlattenDeviceItems(device), moduleRequest.Slot, false);
                    if (occupiedModule != null && !TryDeletePluggedItem(occupiedModule, request.Name, result))
                    {
                        result.Errors.Add($"{request.Name}: slot {moduleRequest.Slot} is occupied by {Describe(occupiedModule)} and cannot be replaced.");
                        continue;
                    }
                    var candidates = FindHardwareItemCandidates(hardwareCatalog, moduleRequest.ModuleId, moduleRequest.ModuleIdentNumber, moduleRequest.Name, false, request.Name, result);
                    moduleItem = TryPlugNew(
                        FlattenDeviceItems(device), candidates,
                        BuildHardwareItemName(moduleRequest.Name, moduleRequest.ModuleId, moduleRequest.Slot),
                        moduleRequest.Slot, request.Name, result);
                }
                else
                {
                    result.Attempts.Add($"{request.Name}: module {Describe(moduleItem)} already exists in slot {moduleRequest.Slot}");
                }

                if (moduleItem == null)
                {
                    result.Errors.Add($"{request.Name}: cannot plug module {moduleRequest.Name ?? moduleRequest.ModuleId} into slot {moduleRequest.Slot}.");
                    continue;
                }

                ApplyStartAddresses(moduleItem, moduleRequest.InputStart, moduleRequest.OutputStart, request.Name, result);

                if (string.IsNullOrWhiteSpace(moduleRequest.SubmoduleId)) continue;
                var subslot = moduleRequest.Subslot > 0 ? moduleRequest.Subslot : 1;
                var submoduleItem = FindConfiguredItem(
                    FlattenDeviceItems(device), moduleRequest.SubmoduleId, moduleRequest.SubmoduleIdentNumber, subslot);
                if (submoduleItem == null)
                {
                    var occupiedSubmodule = FindOccupiedHardwareItem(FlattenDeviceItems(device), subslot, true);
                    if (occupiedSubmodule != null && !TryDeletePluggedItem(occupiedSubmodule, request.Name, result))
                    {
                        result.Errors.Add($"{request.Name}: subslot {subslot} is occupied by {Describe(occupiedSubmodule)} and cannot be replaced.");
                        continue;
                    }
                    var candidates = FindHardwareItemCandidates(
                        hardwareCatalog, moduleRequest.SubmoduleId, moduleRequest.SubmoduleIdentNumber,
                        moduleRequest.SubmoduleName, true, request.Name, result);
                    submoduleItem = TryPlugNew(
                        new[] { moduleItem }.Concat(FlattenDeviceItems(device)), candidates,
                        BuildHardwareItemName(moduleRequest.SubmoduleName, moduleRequest.SubmoduleId, subslot),
                        subslot, request.Name, result);
                }
                else
                {
                    result.Attempts.Add($"{request.Name}: submodule {Describe(submoduleItem)} already exists in subslot {subslot}");
                }

                if (submoduleItem == null)
                {
                    result.Errors.Add($"{request.Name}: cannot plug submodule {moduleRequest.SubmoduleName ?? moduleRequest.SubmoduleId} into subslot {subslot}.");
                    continue;
                }

                ApplyStartAddresses(submoduleItem, moduleRequest.InputStart, moduleRequest.OutputStart, request.Name, result);
            }
        }

        private static List<DeviceModuleRequest> BuildEffectiveModuleRequests(DeviceRequest request, DeviceWriteResult result)
        {
            var configured = (request.Modules ?? new List<DeviceModuleRequest>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.ModuleId))
                .ToList();
            if (configured.Count > 0 || !IsServoRequest(request)) return configured;
            if (string.IsNullOrWhiteSpace(request.GsdFilePath) || !File.Exists(request.GsdFilePath)) return configured;

            try
            {
                var scan = new GsdCatalogScanner().Scan(Path.GetDirectoryName(request.GsdFilePath));
                var device = scan.Devices.FirstOrDefault(x =>
                    string.Equals(x.FileName, Path.GetFileName(request.GsdFilePath), StringComparison.OrdinalIgnoreCase));
                var accessPoint = device?.AccessPoints.FirstOrDefault(x =>
                        string.Equals(x.Id, request.AccessPointId, StringComparison.OrdinalIgnoreCase))
                    ?? device?.AccessPoints.FirstOrDefault();
                foreach (var module in accessPoint?.Modules ?? new List<GsdModuleInfo>())
                {
                    var telegram111 = (module.Submodules ?? new List<GsdSubmoduleInfo>()).FirstOrDefault(x =>
                        ContainsTelegram111(x.Id) || ContainsTelegram111(x.Name));
                    if (telegram111 == null) continue;

                    configured.Add(new DeviceModuleRequest
                    {
                        Name = module.Name,
                        ModuleId = module.Id,
                        ModuleIdentNumber = module.ModuleIdentNumber,
                        Slot = FirstPosition(module.AllowedInSlots, 1),
                        SubmoduleId = telegram111.Id,
                        SubmoduleName = telegram111.Name,
                        SubmoduleIdentNumber = telegram111.SubmoduleIdentNumber,
                        Subslot = FirstPosition(telegram111.AllowedInSubslots, 2),
                        InputStart = request.InputStart ?? 0,
                        OutputStart = request.OutputStart ?? 0
                    });
                    result.ChangedObjects.Add($"{request.Name}: defaulted servo to {telegram111.Name} in slot {configured[0].Slot}/{configured[0].Subslot}");
                    break;
                }
            }
            catch (Exception ex)
            {
                result.Attempts.Add($"{request.Name}: loading default telegram 111 from GSD failed - {ex.GetBaseException().Message}");
            }
            return configured;
        }

        private static bool IsServoRequest(DeviceRequest request)
        {
            var text = string.Join(" ", new[]
            {
                request.MainFamily, request.ProductFamily, request.DeviceType, request.OrderNumber, request.GsdFileName
            }).ToLowerInvariant();
            return text.Contains("drives") || text.Contains("servo") || text.Contains("sinamics")
                || text.Contains("v90") || text.Contains("s200") || text.Contains("sv660");
        }

        private static bool ContainsTelegram111(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(value, @"(^|\D)111(\D|$)");
        }

        private static int FirstPosition(string value, int fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            var match = System.Text.RegularExpressions.Regex.Match(value, @"\d+");
            return match.Success && int.TryParse(match.Value, out var parsed) ? parsed : fallback;
        }

        private static object FindConfiguredItem(IEnumerable<object> items, string id, string identNumber, int position)
        {
            return items.FirstOrDefault(item =>
            {
                var itemPosition = OpennessReflection.ReadProperty(item, "PositionNumber");
                if (!(itemPosition is int value) || value != position) return false;
                var identity = string.Join(" ", ReadString(item, "TypeIdentifier"), ReadString(item, "Name"));
                return ContainsIdentity(identity, id) || ContainsIdentity(identity, identNumber);
            });
        }

        private static object FindOccupiedHardwareItem(IEnumerable<object> items, int position, bool submodule)
        {
            var marker = submodule ? "/SM/" : "/M/";
            var itemList = items.ToList();
            if (submodule)
            {
                return itemList.FirstOrDefault(item =>
                {
                    var name = ReadString(item, "Name") ?? string.Empty;
                    return name.IndexOf("Telegram", StringComparison.OrdinalIgnoreCase) >= 0 || name.Contains("报文");
                });
            }

            return itemList.FirstOrDefault(item =>
            {
                var itemPosition = OpennessReflection.ReadProperty(item, "PositionNumber");
                var typeIdentifier = ReadString(item, "TypeIdentifier") ?? string.Empty;
                return itemPosition is int value && value == position
                    && typeIdentifier.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;
            });
        }

        private static bool TryDeletePluggedItem(object item, string deviceName, DeviceWriteResult result)
        {
            try
            {
                var delete = item.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "Delete" && m.GetParameters().Length == 0);
                if (delete == null) return false;
                var description = Describe(item);
                delete.Invoke(item, null);
                result.ChangedObjects.Add($"{deviceName}: removed occupied hardware item {description}");
                return true;
            }
            catch (Exception ex)
            {
                result.Attempts.Add($"{deviceName}: deleting occupied hardware item {Describe(item)} failed - {ex.GetBaseException().Message}");
                return false;
            }
        }
        private static bool ContainsIdentity(string text, string identity)
        {
            return !string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(identity)
                && text.IndexOf(identity, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static List<object> FindHardwareItemCandidates(
            object hardwareCatalog, string id, string identNumber, string name, bool submodule,
            string deviceName, DeviceWriteResult result)
        {
            var found = new List<object>();
            foreach (var term in new[] { id, identNumber, name }.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                foreach (var entry in CatalogFind(hardwareCatalog, term))
                {
                    var typeIdentifier = ReadString(entry, "TypeIdentifier");
                    if (string.IsNullOrWhiteSpace(typeIdentifier)) continue;
                    if (found.Any(x => string.Equals(ReadString(x, "TypeIdentifier"), typeIdentifier, StringComparison.OrdinalIgnoreCase))) continue;
                    found.Add(entry);
                }
            }

            var marker = submodule ? "/SM/" : "/M/";
            var ranked = found
                .Select(entry => new
                {
                    Entry = entry,
                    TypeIdentifier = ReadString(entry, "TypeIdentifier") ?? string.Empty,
                    Score = ScoreHardwareItem(entry, id, identNumber, name, marker)
                })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Select(x => x.Entry)
                .ToList();
            result.Attempts.Add($"{deviceName}: hardware catalog {marker.Trim('/')} search {id} -> {ranked.Count} candidates");
            return ranked;
        }

        private static int ScoreHardwareItem(object entry, string id, string identNumber, string name, string marker)
        {
            var text = string.Join(" ", new[]
            {
                ReadString(entry, "TypeIdentifier"), ReadString(entry, "TypeName"),
                ReadString(entry, "ArticleNumber"), ReadString(entry, "Description")
            }).ToLowerInvariant();
            var score = text.Contains(marker.ToLowerInvariant()) ? 50 : 0;
            if (ContainsIdentity(text, id)) score += 100;
            if (ContainsIdentity(text, identNumber)) score += 60;
            if (ContainsIdentity(text, name)) score += 30;
            return score;
        }

        private static object TryPlugNew(
            IEnumerable<object> parents, IEnumerable<object> catalogCandidates,
            string itemName, int position, string deviceName, DeviceWriteResult result)
        {
            foreach (var candidate in catalogCandidates.Take(20))
            {
                var typeIdentifier = ReadString(candidate, "TypeIdentifier");
                if (string.IsNullOrWhiteSpace(typeIdentifier)) continue;
                foreach (var parent in parents.Distinct(new ReferenceEqualityComparer()))
                {
                    var canPlugNew = parent.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "CanPlugNew" && m.GetParameters().Length == 3);
                    var plugNew = parent.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "PlugNew" && m.GetParameters().Length == 3);
                    if (canPlugNew == null || plugNew == null) continue;

                    try
                    {
                        var canPlug = (bool)canPlugNew.Invoke(parent, new object[] { typeIdentifier, itemName, position });
                        if (!canPlug) continue;
                        var plugged = plugNew.Invoke(parent, new object[] { typeIdentifier, itemName, position });
                        result.ChangedObjects.Add($"{deviceName}: plugged {itemName} at {Describe(parent)} position {position}");
                        return plugged;
                    }
                    catch (Exception ex)
                    {
                        result.Attempts.Add($"{deviceName}: PlugNew {itemName} at {Describe(parent)} position {position} failed - {ex.GetBaseException().Message}");
                    }
                }
            }
            return null;
        }

        private static string BuildHardwareItemName(string name, string id, int position)
        {
            var baseName = string.IsNullOrWhiteSpace(name) ? id : name;
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "Module";
            baseName = System.Text.RegularExpressions.Regex.Replace(baseName, @"[^\p{L}\p{Nd}_-]+", "_").Trim('_');
            if (baseName.Length > 48) baseName = baseName.Substring(0, 48).TrimEnd('_');
            return baseName + "_" + position;
        }

        private static void ApplyStartAddresses(
            object item, int? inputStart, int? outputStart,
            string deviceName, DeviceWriteResult result)
        {
            foreach (var current in FlattenDeviceItems(item).Prepend(item).Distinct(new ReferenceEqualityComparer()))
            {
                foreach (var address in OpennessReflection.ReadEnumerableProperty(current, "Addresses") ?? new object[0])
                {
                    var ioType = OpennessReflection.ReadProperty(address, "IoType")?.ToString() ?? string.Empty;
                    int? start = null;
                    if (ioType.IndexOf("Input", StringComparison.OrdinalIgnoreCase) >= 0) start = inputStart;
                    if (ioType.IndexOf("Output", StringComparison.OrdinalIgnoreCase) >= 0) start = outputStart;
                    if (!start.HasValue) continue;
                    try
                    {
                        var property = address.GetType().GetProperty("StartAddress", BindingFlags.Public | BindingFlags.Instance);
                        if (property == null || !property.CanWrite) continue;
                        property.SetValue(address, start.Value, null);
                        result.ChangedObjects.Add($"{deviceName}: {Describe(current)} {ioType} start address = {start.Value}");
                    }
                    catch (Exception ex)
                    {
                        result.Attempts.Add($"{deviceName}: setting {ioType} start address on {Describe(current)} failed - {ex.GetBaseException().Message}");
                    }
                }
            }
        }
        private static object FindPreferredIoSystem(object project, DeviceWriteResult result)
        {
            var devices = (OpennessReflection.ReadEnumerableProperty(project, "Devices") ?? new object[0]).Cast<object>().ToList();
            foreach (var device in devices)
            {
                foreach (var item in FlattenDeviceItems(device).Prepend(device).Distinct(new ReferenceEqualityComparer()))
                {
                    var networkInterface = OpennessReflection.InvokeGenericGetService(item, "Siemens.Engineering.HW.Features.NetworkInterface");
                    if (networkInterface == null) continue;

                    var controllers = (OpennessReflection.ReadEnumerableProperty(networkInterface, "IoControllers") ?? new object[0]).Cast<object>().ToList();
                    if (controllers.Count == 0) continue;

                    foreach (var controller in controllers)
                    {
                        var ioSystem = OpennessReflection.ReadProperty(controller, "IoSystem");
                        if (ioSystem != null)
                        {
                            result.Attempts.Add($"PLC IO-System found: {Describe(ioSystem)} on {Describe(item)}");
                            return ioSystem;
                        }
                    }

                    foreach (var controller in controllers)
                    {
                        var created = TryCreateIoSystem(controller, BuildIoSystemName(device, item), result);
                        if (created != null)
                        {
                            result.ChangedObjects.Add($"PLC IO-System created: {Describe(created)}");
                            return created;
                        }
                    }
                }
            }
            return null;
        }

        private static string BuildIoSystemName(object device, object item)
        {
            var name = ReadString(device, "Name") ?? ReadString(item, "Name") ?? "PLC";
            return name + ".PROFINET IO-System";
        }

        private static object TryCreateIoSystem(object ioController, string name, DeviceWriteResult result)
        {
            try
            {
                var method = ioController.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "CreateIoSystem" && m.GetParameters().Length == 1);
                if (method == null) return null;
                var created = method.Invoke(ioController, new object[] { name });
                result.Attempts.Add($"CreateIoSystem '{name}' -> {Describe(created)}");
                return created;
            }
            catch (Exception ex)
            {
                result.Attempts.Add($"CreateIoSystem '{name}' failed - {ex.GetBaseException().Message}");
                return null;
            }
        }

        private static bool TryConnectToControllerIoSystem(object networkInterface, object targetIoSystem, string deviceName, DeviceWriteResult result)
        {
            if (networkInterface == null || targetIoSystem == null) return false;
            var connectedAny = false;
            var targetSubnet = OpennessReflection.ReadProperty(targetIoSystem, "Subnet");
            if (targetSubnet != null)
            {
                foreach (var node in (OpennessReflection.ReadEnumerableProperty(networkInterface, "Nodes") ?? new object[0]).Cast<object>())
                {
                    connectedAny |= TryConnectNodeToSubnet(node, targetSubnet, deviceName, result);
                }
            }
            else
            {
                result.Attempts.Add($"{deviceName}: target IO-System has no subnet.");
            }

            var connectors = (OpennessReflection.ReadEnumerableProperty(networkInterface, "IoConnectors") ?? new object[0]).Cast<object>().ToList();
            if (connectors.Count == 0)
            {
                result.Attempts.Add($"{deviceName}: NetworkInterface has no IoConnector; not an IO device interface.");
                return connectedAny;
            }

            foreach (var connector in connectors)
            {
                connectedAny |= TryConnectIoConnector(connector, targetIoSystem, deviceName, result);
            }
            return connectedAny;
        }

        private static bool TryConnectNodeToSubnet(object node, object subnet, string deviceName, DeviceWriteResult result)
        {
            if (node == null || subnet == null) return false;
            try
            {
                var current = OpennessReflection.ReadProperty(node, "ConnectedSubnet");
                if (SameEngineeringObject(current, subnet))
                {
                    result.Attempts.Add($"{deviceName}: {Describe(node)} already connected to subnet {ReadString(subnet, "Name")}");
                    return false;
                }

                if (current != null)
                {
                    var disconnect = node.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "DisconnectFromSubnet" && m.GetParameters().Length == 0);
                    disconnect?.Invoke(node, null);
                }

                var connect = node.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "ConnectToSubnet" && m.GetParameters().Length == 1);
                if (connect == null)
                {
                    result.Attempts.Add($"{deviceName}: ConnectToSubnet method not found on {Describe(node)}");
                    return false;
                }

                connect.Invoke(node, new[] { subnet });
                result.ChangedObjects.Add($"{deviceName}: {Describe(node)} connected to subnet {ReadString(subnet, "Name")}");
                return true;
            }
            catch (Exception ex)
            {
                result.Attempts.Add($"{deviceName}: ConnectToSubnet failed on {Describe(node)} - {ex.GetBaseException().Message}");
                return false;
            }
        }

        private static bool TryConnectIoConnector(object connector, object ioSystem, string deviceName, DeviceWriteResult result)
        {
            if (connector == null || ioSystem == null) return false;
            try
            {
                var current = OpennessReflection.ReadProperty(connector, "ConnectedToIoSystem");
                if (SameEngineeringObject(current, ioSystem))
                {
                    result.Attempts.Add($"{deviceName}: {Describe(connector)} already connected to IO-System {ReadString(ioSystem, "Name")}");
                    return false;
                }

                if (current != null)
                {
                    var disconnect = connector.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "DisconnectFromIoSystem" && m.GetParameters().Length == 0);
                    disconnect?.Invoke(connector, null);
                }

                var connect = connector.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "ConnectToIoSystem" && m.GetParameters().Length == 1);
                if (connect == null)
                {
                    result.Attempts.Add($"{deviceName}: ConnectToIoSystem method not found on {Describe(connector)}");
                    return false;
                }

                connect.Invoke(connector, new[] { ioSystem });
                result.ChangedObjects.Add($"{deviceName}: connected to PLC IO-System {ReadString(ioSystem, "Name")}");
                return true;
            }
            catch (Exception ex)
            {
                result.Attempts.Add($"{deviceName}: ConnectToIoSystem failed on {Describe(connector)} - {ex.GetBaseException().Message}");
                return false;
            }
        }

        private static bool SameEngineeringObject(object left, object right)
        {
            if (left == null || right == null) return false;
            if (ReferenceEquals(left, right)) return true;
            try { if (left.Equals(right)) return true; } catch { }
            var leftName = ReadString(left, "Name");
            var rightName = ReadString(right, "Name");
            return !string.IsNullOrWhiteSpace(leftName)
                && string.Equals(leftName, rightName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.GetType().FullName, right.GetType().FullName, StringComparison.OrdinalIgnoreCase);
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

