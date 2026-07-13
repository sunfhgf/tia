using System.Collections.Generic;

namespace TiaAutomation.Core.Models
{
    public class DeviceCatalog
    {
        public List<DeviceCatalogEntry> Devices { get; set; } = new List<DeviceCatalogEntry>();
    }

    public class DeviceCatalogEntry
    {
        public string Type { get; set; }
        public string DisplayName { get; set; }
        public string VendorName { get; set; }
        public string VendorId { get; set; }
        public string DeviceId { get; set; }
        public string GsdFileContains { get; set; }
        public string Notes { get; set; }
    }
}
