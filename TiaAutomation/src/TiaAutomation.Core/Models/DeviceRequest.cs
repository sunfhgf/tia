using System.Collections.Generic;

namespace TiaAutomation.Core.Models
{
    public class DeviceRequest
    {
        public string Name { get; set; }
        public string DeviceType { get; set; }
        public string ProfinetName { get; set; }
        public string IpAddress { get; set; }
        public int? InputStart { get; set; }
        public int? OutputStart { get; set; }
        public string VendorName { get; set; }
        public string VendorId { get; set; }
        public string DeviceId { get; set; }
        public string GsdFileName { get; set; }
        public string GsdFilePath { get; set; }
        public string AccessPointId { get; set; }
        public string OrderNumber { get; set; }
        public string ModuleIdentNumber { get; set; }
        public string MainFamily { get; set; }
        public string ProductFamily { get; set; }
        public List<DeviceModuleRequest> Modules { get; set; } = new List<DeviceModuleRequest>();
    }

    public class DeviceModuleRequest
    {
        public string Name { get; set; }
        public string ModuleId { get; set; }
        public string ModuleIdentNumber { get; set; }
        public int Slot { get; set; }
        public string SubmoduleId { get; set; }
        public string SubmoduleName { get; set; }
        public string SubmoduleIdentNumber { get; set; }
        public int Subslot { get; set; }
        public int? InputStart { get; set; }
        public int? OutputStart { get; set; }
    }
}
