using System.Collections.Generic;

namespace TiaAutomation.Core.Gsd
{
    public class GsdDeviceInfo
    {
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public string VendorName { get; set; }
        public string VendorId { get; set; }
        public string DeviceId { get; set; }
        public string MainFamily { get; set; }
        public string ProductFamily { get; set; }
        public List<GsdAccessPointInfo> AccessPoints { get; set; } = new List<GsdAccessPointInfo>();
    }

    public class GsdAccessPointInfo
    {
        public string Id { get; set; }
        public string DnsCompatibleName { get; set; }
        public string ModuleIdentNumber { get; set; }
        public string OrderNumber { get; set; }
        public string HardwareRelease { get; set; }
        public string SoftwareRelease { get; set; }
    }
}
