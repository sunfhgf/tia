using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace TiaAutomation.Core.Planning
{
    public class NamingService
    {
        private static readonly Regex ProfinetName = new Regex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.Compiled);
        private static readonly Regex InvalidProfinetChars = new Regex("[^a-z0-9-]+", RegexOptions.Compiled);
        private static readonly Regex DuplicateHyphens = new Regex("-+", RegexOptions.Compiled);

        public bool IsValidProfinetName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.Split('.').All(part => ProfinetName.IsMatch(part));
        }

        public string NormalizeProfinetName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            var parts = value.Trim().ToLowerInvariant()
                .Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => DuplicateHyphens.Replace(InvalidProfinetChars.Replace(part, "-"), "-").Trim('-'))
                .Where(part => !string.IsNullOrWhiteSpace(part));

            return string.Join(".", parts);
        }

        public bool IsReasonableTagName(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsWhiteSpace);
        }
    }
}
