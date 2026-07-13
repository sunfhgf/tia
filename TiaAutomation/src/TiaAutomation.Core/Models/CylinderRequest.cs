namespace TiaAutomation.Core.Models
{
    public class CylinderRequest
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Station { get; set; }
        public string Type { get; set; }
        public string ExtendFeedback { get; set; }
        public string RetractFeedback { get; set; }
        public string ExtendOutput { get; set; }
        public string RetractOutput { get; set; }
        public int ExtendDelayMs { get; set; }
        public int RetractDelayMs { get; set; }
        public int ShieldDelayMs { get; set; }
        public int AlarmTimeMs { get; set; }
        public CylinderModeSettings Mode { get; set; } = new CylinderModeSettings();
    }
}
