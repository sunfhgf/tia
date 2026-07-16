using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TiaAutomation.Core.Models;

namespace TiaAutomation.Openness
{
    public class UnitFolderWriter
    {
        public UnitFolderWriteResult WriteOnOpenedProject(object project, ProjectSettings settings)
        {
            var result = new UnitFolderWriteResult
            {
                RequestedUnitCount = Math.Max(1, settings?.UnitCount ?? 1),
                SourceFolder = "设备1"
            };

            if (result.RequestedUnitCount <= 1)
            {
                result.Success = true;
                result.Diagnostic = "One unit requested; no folder copies are needed.";
                return result;
            }

            object temporaryFolder = null;
            try
            {
                var plcSoftware = PlcSoftwareLocator.FindFirstPlcSoftware(project);
                var blockGroup = OpennessReflection.ReadProperty(plcSoftware, "BlockGroup");
                var groups = OpennessReflection.ReadProperty(blockGroup, "Groups");
                if (groups == null)
                {
                    result.Errors.Add("PLC program block group composition was not found.");
                    return Complete(result);
                }

                var sourceGroup = OpennessReflection.FindNamedChild(groups, result.SourceFolder);
                if (sourceGroup == null)
                {
                    result.Errors.Add($"Source program folder '{result.SourceFolder}' was not found.");
                    return Complete(result);
                }

                var projectLibrary = OpennessReflection.ReadProperty(project, "ProjectLibrary");
                var masterCopyRoot = OpennessReflection.ReadProperty(projectLibrary, "MasterCopyFolder");
                var folders = OpennessReflection.ReadProperty(masterCopyRoot, "Folders");
                var createFolder = FindMethod(folders, "Create", 1);
                if (createFolder == null)
                {
                    result.Errors.Add("Project library master-copy folder creation is unavailable.");
                    return Complete(result);
                }

                temporaryFolder = createFolder.Invoke(folders, new object[] { "TIA_AUTO_UNIT_" + Guid.NewGuid().ToString("N") });
                var masterCopies = OpennessReflection.ReadProperty(temporaryFolder, "MasterCopies");
                var createMasterCopy = FindMethod(masterCopies, "Create", 1);
                if (createMasterCopy == null)
                {
                    result.Errors.Add("Project library master-copy creation is unavailable.");
                    return Complete(result);
                }

                var masterCopy = createMasterCopy.Invoke(masterCopies, new[] { sourceGroup });
                var createFrom = groups.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method => method.Name == "CreateFrom" && method.GetParameters().Length == 2);
                if (createFrom == null)
                {
                    result.Errors.Add("PLC program folder CreateFrom(master copy) is unavailable.");
                    return Complete(result);
                }
                var copyModeType = createFrom.GetParameters()[1].ParameterType;
                var renameMode = Enum.Parse(copyModeType, "Rename");

                var desiredCopyCount = result.RequestedUnitCount - 1;
                var existingCopies = ((System.Collections.IEnumerable)groups).Cast<object>()
                    .Select(group => OpennessReflection.ReadProperty(group, "Name") as string)
                    .Where(name => !string.IsNullOrWhiteSpace(name)
                        && name.StartsWith(result.SourceFolder + "_", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                result.ExistingFolders.AddRange(existingCopies);

                while (existingCopies.Count + result.CreatedFolders.Count < desiredCopyCount)
                {
                    var created = createFrom.Invoke(groups, new[] { masterCopy, renameMode });
                    var createdName = OpennessReflection.ReadProperty(created, "Name") as string;
                    if (string.IsNullOrWhiteSpace(createdName)
                        || string.Equals(createdName, result.SourceFolder, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Errors.Add("TIA did not return a valid name for the copied unit folder.");
                        break;
                    }
                    if (existingCopies.Contains(createdName, StringComparer.OrdinalIgnoreCase)
                        || result.CreatedFolders.Contains(createdName, StringComparer.OrdinalIgnoreCase))
                    {
                        result.Errors.Add($"TIA returned duplicate copied folder name '{createdName}'.");
                        break;
                    }
                    result.CreatedFolders.Add(createdName);
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add(ex.GetBaseException().Message);
            }
            finally
            {
                TryDelete(temporaryFolder, result);
            }

            return Complete(result);
        }

        private static MethodInfo FindMethod(object target, string name, int parameterCount)
        {
            return target?.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(method => method.Name == name && method.GetParameters().Length == parameterCount);
        }

        private static void TryDelete(object target, UnitFolderWriteResult result)
        {
            if (target == null) return;
            try
            {
                FindMethod(target, "Delete", 0)?.Invoke(target, null);
            }
            catch (Exception ex)
            {
                result.Warnings.Add("Temporary master-copy cleanup failed: " + ex.GetBaseException().Message);
            }
        }

        private static UnitFolderWriteResult Complete(UnitFolderWriteResult result)
        {
            result.Success = result.Errors.Count == 0;
            result.Diagnostic = result.Success
                ? $"Unit folders configured: {result.CreatedFolders.Count} created, {result.ExistingFolders.Count} already existed."
                : "Unit folder configuration failed.";
            return result;
        }
    }

    public class UnitFolderWriteResult
    {
        public bool Success { get; set; }
        public string Diagnostic { get; set; }
        public int RequestedUnitCount { get; set; }
        public string SourceFolder { get; set; }
        public List<string> CreatedFolders { get; set; } = new List<string>();
        public List<string> ExistingFolders { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
    }
}
