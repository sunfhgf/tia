using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TiaAutomation.Core.Models;

namespace TiaAutomation.Openness
{
    public class TagWriter
    {
        public TiaWriteResult WritePlcTags(string projectPath, IEnumerable<IoPoint> tags, string tagTableName = "TIA_AUTO_IO", string opennessAssemblyPath = null)
        {
            var result = new TiaWriteResult
            {
                ProjectPath = projectPath,
                TagTableName = tagTableName
            };

            try
            {
                using (var session = new TiaPortalSession(opennessAssemblyPath))
                {
                    if (!session.IsAvailable(out var diagnostic))
                    {
                        result.Success = false;
                        result.Diagnostic = diagnostic;
                        return result;
                    }

                    var project = session.OpenProject(System.IO.Path.GetFullPath(projectPath));
                    var inner = WriteOnOpenedProject(project, tags, tagTableName);
                    inner.ProjectPath = projectPath;
                    session.SaveProject(project);
                    return inner;
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Diagnostic = ex.GetBaseException().Message;
            }

            return result;
        }

        public TiaWriteResult WriteOnOpenedProject(object project, IEnumerable<IoPoint> tags, string tagTableName = "TIA_AUTO_IO")
        {
            var result = new TiaWriteResult { TagTableName = tagTableName };
            try
            {
                var plcSoftware = PlcSoftwareLocator.FindFirstPlcSoftware(project);
                if (plcSoftware == null)
                {
                    result.Success = false;
                    result.Diagnostic = "No PLC software container was found in the copied project.";
                    return result;
                }

                var tagTableGroup = OpennessReflection.ReadProperty(plcSoftware, "TagTableGroup");
                if (tagTableGroup == null)
                {
                    result.Success = false;
                    result.Diagnostic = "PLC software TagTableGroup was not found.";
                    return result;
                }

                var tagTable = FindOrCreateTagTable(tagTableGroup, tagTableName, result);
                if (tagTable == null)
                {
                    result.Success = false;
                    result.Diagnostic = "Could not create or locate PLC tag table.";
                    return result;
                }

                foreach (var tag in (tags ?? Enumerable.Empty<IoPoint>()).Where(t => string.Equals(t.DataType, "Bool", StringComparison.OrdinalIgnoreCase)))
                {
                    WriteTag(tagTable, tag, result);
                }

                result.Success = true;
                result.Diagnostic = "PLC tags written.";
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Diagnostic = ex.GetBaseException().Message;
            }
            return result;
        }

        private static object FindOrCreateTagTable(object tagTableGroup, string tagTableName, TiaWriteResult result)
        {
            var tagTables = OpennessReflection.ReadProperty(tagTableGroup, "TagTables") ?? tagTableGroup;
            var existing = OpennessReflection.FindNamedChild(tagTables, tagTableName);
            if (existing != null)
            {
                return existing;
            }

            var createMethod = tagTables.GetType().GetMethods()
                .FirstOrDefault(m => m.Name == "Create" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
            if (createMethod == null)
            {
                result.Warnings.Add("Could not find TagTables.Create(string). Check installed TIA Openness API version.");
                return null;
            }

            return createMethod.Invoke(tagTables, new object[] { tagTableName });
        }

        private static void WriteTag(object tagTable, IoPoint point, TiaWriteResult result)
        {
            var tags = OpennessReflection.ReadProperty(tagTable, "Tags") ?? tagTable;
            if (OpennessReflection.FindNamedChild(tags, point.Tag) != null)
            {
                result.ExistingTags.Add(point.Tag);
                return;
            }

            var createMethods = tags.GetType().GetMethods().Where(m => m.Name == "Create").ToList();
            foreach (var method in createMethods)
            {
                var parameters = method.GetParameters();
                try
                {
                    if (parameters.Length == 3)
                    {
                        method.Invoke(tags, new object[] { point.Tag, point.DataType ?? "Bool", point.Address });
                        TrySetComment(OpennessReflection.FindNamedChild(tags, point.Tag), point.Comment);
                        result.CreatedTags.Add(point.Tag);
                        return;
                    }

                    if (parameters.Length == 4)
                    {
                        method.Invoke(tags, new object[] { point.Tag, point.DataType ?? "Bool", point.Address, point.Comment ?? string.Empty });
                        result.CreatedTags.Add(point.Tag);
                        return;
                    }
                }
                catch
                {
                    // Try the next overload; Openness overloads differ by version and object type.
                }
            }

            result.SkippedTags.Add(point.Tag);
            result.Warnings.Add($"Could not create tag {point.Tag}; no compatible Tags.Create overload accepted the arguments.");
        }

        private static void TrySetComment(object target, string comment)
        {
            if (target == null || string.IsNullOrWhiteSpace(comment))
            {
                return;
            }

            foreach (var propertyName in new[] { "Comment", "CommentText" })
            {
                var property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (property != null && property.CanWrite)
                {
                    property.SetValue(target, comment, null);
                    return;
                }
            }
        }
    }
}
