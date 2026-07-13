using System;
using System.Collections;
using System.IO;

namespace TiaAutomation.Openness
{
    public class ProjectInspector
    {
        public ProjectInventory Inspect(string projectPath, string opennessAssemblyPath = null)
        {
            var inventory = new ProjectInventory
            {
                ProjectPath = projectPath,
                ProjectName = Path.GetFileNameWithoutExtension(projectPath)
            };

            if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
            {
                inventory.Status = "ProjectNotFound";
                inventory.Diagnostic = $"Project file not found: {projectPath}";
                return inventory;
            }

            try
            {
                using (var session = new TiaPortalSession(opennessAssemblyPath))
                {
                    if (!session.IsAvailable(out var diagnostic))
                    {
                        inventory.Status = "OpennessUnavailable";
                        inventory.Diagnostic = diagnostic;
                        return inventory;
                    }

                    var project = session.OpenProject(Path.GetFullPath(projectPath));
                    inventory.Status = "Opened";
                    inventory.ProjectName = OpennessReflection.ReadProperty(project, "Name") as string ?? inventory.ProjectName;
                    ReadDevices(project, inventory);
                }
            }
            catch (Exception ex)
            {
                inventory.Status = "OpenFailed";
                inventory.Diagnostic = ex.GetBaseException().Message;
            }

            return inventory;
        }

        private static void ReadDevices(object project, ProjectInventory inventory)
        {
            var devices = OpennessReflection.ReadEnumerableProperty(project, "Devices");
            if (devices == null)
            {
                return;
            }

            foreach (var device in devices)
            {
                var name = OpennessReflection.ReadProperty(device, "Name") as string ?? device.ToString();
                inventory.Devices.Add(name);
                ReadDeviceItems(device, inventory, name);
            }
        }

        private static void ReadDeviceItems(object device, ProjectInventory inventory, string prefix)
        {
            var items = OpennessReflection.ReadEnumerableProperty(device, "DeviceItems");
            if (items == null)
            {
                return;
            }

            foreach (var item in items)
            {
                var name = OpennessReflection.ReadProperty(item, "Name") as string ?? item.ToString();
                var fullName = prefix + "/" + name;
                inventory.Devices.Add(fullName);

                var software = GetSoftwareContainer(item);
                if (IsPlcSoftware(software))
                {
                    var plc = new PlcInventory { Name = fullName };
                    ReadTagTables(software, plc, inventory);
                    ReadBlocks(software, plc, inventory);
                    inventory.Plcs.Add(plc);
                }

                ReadDeviceItems(item, inventory, fullName);
            }
        }

        private static object GetSoftwareContainer(object deviceItem)
        {
            var container = OpennessReflection.InvokeGenericGetService(deviceItem, "Siemens.Engineering.HW.Features.SoftwareContainer");
            if (container != null)
            {
                return OpennessReflection.ReadProperty(container, "Software");
            }
            return null;
        }

        private static bool IsPlcSoftware(object software)
        {
            return software != null
                && OpennessReflection.ReadProperty(software, "TagTableGroup") != null
                && OpennessReflection.ReadProperty(software, "BlockGroup") != null;
        }

        private static void ReadTagTables(object plcSoftware, PlcInventory plc, ProjectInventory inventory)
        {
            var rootGroup = OpennessReflection.ReadProperty(plcSoftware, "TagTableGroup");
            WalkTagTableGroup(rootGroup, plc.Name, plc, inventory);
        }

        private static void WalkTagTableGroup(object group, string path, PlcInventory plc, ProjectInventory inventory)
        {
            if (group == null)
            {
                return;
            }

            foreach (var table in OpennessReflection.ReadEnumerableProperty(group, "TagTables") ?? new object[0])
            {
                var name = OpennessReflection.ReadProperty(table, "Name") as string;
                var tags = OpennessReflection.ReadEnumerableProperty(table, "Tags");
                var info = new TagTableInfo
                {
                    Name = name,
                    Path = path + "/" + name,
                    TagCount = OpennessReflection.CountEnumerable(tags)
                };
                plc.TagTables.Add(info);
                inventory.TagTables.Add(info.Path);
            }

            foreach (var sub in OpennessReflection.ReadEnumerableProperty(group, "Groups") ?? new object[0])
            {
                var subName = OpennessReflection.ReadProperty(sub, "Name") as string ?? "(group)";
                WalkTagTableGroup(sub, path + "/" + subName, plc, inventory);
            }
        }

        private static void ReadBlocks(object plcSoftware, PlcInventory plc, ProjectInventory inventory)
        {
            var rootGroup = OpennessReflection.ReadProperty(plcSoftware, "BlockGroup");
            WalkBlockGroup(rootGroup, plc.Name, plc, inventory);
        }

        private static void WalkBlockGroup(object group, string path, PlcInventory plc, ProjectInventory inventory)
        {
            if (group == null)
            {
                return;
            }

            foreach (var block in OpennessReflection.ReadEnumerableProperty(group, "Blocks") ?? new object[0])
            {
                var info = new BlockInfo
                {
                    Name = OpennessReflection.ReadProperty(block, "Name") as string,
                    Type = block.GetType().Name,
                    Number = OpennessReflection.ReadProperty(block, "Number") as int?,
                    ProgrammingLanguage = OpennessReflection.ReadProperty(block, "ProgrammingLanguage")?.ToString()
                };
                info.Path = path + "/" + info.Name;
                plc.Blocks.Add(info);
                inventory.Blocks.Add(info.Path);
            }

            foreach (var sub in OpennessReflection.ReadEnumerableProperty(group, "Groups") ?? new object[0])
            {
                var subName = OpennessReflection.ReadProperty(sub, "Name") as string ?? "(group)";
                WalkBlockGroup(sub, path + "/" + subName, plc, inventory);
            }
        }
    }
}
