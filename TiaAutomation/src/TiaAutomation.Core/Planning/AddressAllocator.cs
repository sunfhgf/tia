using System;
using System.Text.RegularExpressions;

namespace TiaAutomation.Core.Planning
{
    public class AddressAllocator
    {
        private static readonly Regex BoolAddress = new Regex("^[IQ](\\d+)\\.([0-7])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public bool TryParseBoolAddress(string address, out string area, out int byteOffset, out int bitOffset)
        {
            area = null;
            byteOffset = 0;
            bitOffset = 0;

            if (string.IsNullOrWhiteSpace(address))
            {
                return false;
            }

            var match = BoolAddress.Match(address.Trim());
            if (!match.Success)
            {
                return false;
            }

            area = match.Value.Substring(0, 1).ToUpperInvariant();
            byteOffset = int.Parse(match.Groups[1].Value);
            bitOffset = int.Parse(match.Groups[2].Value);
            return true;
        }

        public string NormalizeBoolAddress(string address)
        {
            if (!TryParseBoolAddress(address, out var area, out var byteOffset, out var bitOffset))
            {
                return address;
            }

            return $"{area}{byteOffset}.{bitOffset}";
        }
    }
}
