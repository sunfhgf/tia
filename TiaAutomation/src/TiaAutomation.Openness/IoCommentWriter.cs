using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using TiaAutomation.Core.Models;

namespace TiaAutomation.Openness
{
    public class IoCommentWriter
    {
        public IoCommentWriteResult WriteOnOpenedProject(object project, IEnumerable<IoCommentRequest> comments)
        {
            var result = new IoCommentWriteResult();
            var requested = (comments ?? Enumerable.Empty<IoCommentRequest>())
                .Where(item => !string.IsNullOrWhiteSpace(item.Address) && !string.IsNullOrWhiteSpace(item.Comment))
                .GroupBy(item => NormalizeAddress(item.Address), StringComparer.OrdinalIgnoreCase)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .ToDictionary(group => group.Key, group => group.Last().Comment.Trim(), StringComparer.OrdinalIgnoreCase);
            if (requested.Count == 0)
            {
                result.Success = true;
                result.Diagnostic = "No IO comments configured.";
                return result;
            }

            try
            {
                var plcSoftware = PlcSoftwareLocator.FindFirstPlcSoftware(project);
                var tagTableGroup = OpennessReflection.ReadProperty(plcSoftware, "TagTableGroup");
                if (tagTableGroup == null)
                {
                    result.Errors.Add("PLC tag table group was not found.");
                    return Complete(result, requested.Keys);
                }

                var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                WalkTagTables(tagTableGroup, tag =>
                {
                    var address = NormalizeAddress(OpennessReflection.ReadProperty(tag, "LogicalAddress") as string);
                    if (string.IsNullOrWhiteSpace(address) || !requested.TryGetValue(address, out var comment)) return;
                    if (!SetMultilingualComment(tag, comment))
                    {
                        result.Errors.Add($"{address}: tag comment language item was not writable.");
                        return;
                    }
                    matched.Add(address);
                    result.UpdatedTags.Add(new IoCommentUpdate
                    {
                        Address = "%" + address,
                        TagName = OpennessReflection.ReadProperty(tag, "Name") as string,
                        Comment = comment
                    });
                });

                foreach (var address in requested.Keys.Where(address => !matched.Contains(address)))
                {
                    result.MissingAddresses.Add("%" + address);
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add(ex.GetBaseException().Message);
            }

            return Complete(result, requested.Keys);
        }

        private static void WalkTagTables(object group, Action<object> action)
        {
            foreach (var table in OpennessReflection.ReadEnumerableProperty(group, "TagTables") ?? new object[0])
            {
                foreach (var tag in OpennessReflection.ReadEnumerableProperty(table, "Tags") ?? new object[0]) action(tag);
            }
            foreach (var child in OpennessReflection.ReadEnumerableProperty(group, "Groups") ?? new object[0])
            {
                WalkTagTables(child, action);
            }
        }

        private static bool SetMultilingualComment(object tag, string comment)
        {
            var multilingual = OpennessReflection.ReadProperty(tag, "Comment");
            var items = OpennessReflection.ReadProperty(multilingual, "Items") as IEnumerable;
            if (items == null) return false;
            var updated = false;
            foreach (var item in items)
            {
                var text = item.GetType().GetProperty("Text", BindingFlags.Public | BindingFlags.Instance);
                if (text == null || !text.CanWrite) continue;
                text.SetValue(item, comment, null);
                updated = true;
            }
            return updated;
        }

        public static string NormalizeAddress(string address)
        {
            var match = Regex.Match(address ?? string.Empty, @"%?\s*([IQ])\s*(\d+)\s*\.\s*(\d+)", RegexOptions.IgnoreCase);
            return match.Success
                ? $"{match.Groups[1].Value.ToUpperInvariant()}{int.Parse(match.Groups[2].Value)}.{int.Parse(match.Groups[3].Value)}"
                : null;
        }

        private static IoCommentWriteResult Complete(IoCommentWriteResult result, IEnumerable<string> requested)
        {
            result.Success = result.Errors.Count == 0;
            result.Diagnostic = result.Success
                ? $"IO comments updated: {result.UpdatedTags.Count}; missing addresses: {result.MissingAddresses.Count}."
                : "IO comment update failed.";
            return result;
        }
    }

    public class IoCommentWriteResult
    {
        public bool Success { get; set; }
        public string Diagnostic { get; set; }
        public List<IoCommentUpdate> UpdatedTags { get; set; } = new List<IoCommentUpdate>();
        public List<string> MissingAddresses { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
    }

    public class IoCommentUpdate
    {
        public string Address { get; set; }
        public string TagName { get; set; }
        public string Comment { get; set; }
    }
}
