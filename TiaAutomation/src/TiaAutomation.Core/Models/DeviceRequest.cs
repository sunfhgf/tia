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
    }
}
