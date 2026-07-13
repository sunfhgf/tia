using System.Collections.Generic;

namespace TiaAutomation.Core.Models
{
    public class AutomationPlan
    {
        public string ProjectName { get; set; }
        public ProjectSettings Project { get; set; }
        public bool CanApply { get; set; }
        public List<DeviceRequest> DevicesToCreate { get; set; } = new List<DeviceRequest>();
        public List<IoPoint> TagsToCreate { get; set; } = new List<IoPoint>();
        public List<CylinderRequest> CylinderMappings { get; set; } = new List<CylinderRequest>();
        public List<ServoRequest> ServoMappings { get; set; } = new List<ServoRequest>();
        public List<MotorRequest> MotorMappings { get; set; } = new List<MotorRequest>();
        public List<StationRequest> StationPlans { get; set; } = new List<StationRequest>();
        public List<AlarmRequest> AlarmPlans { get; set; } = new List<AlarmRequest>();
        public List<StationCylinderPlan> StationCylinderPlans { get; set; } = new List<StationCylinderPlan>();
        public List<string> ManualTasks { get; set; } = new List<string>();
        public List<ValidationIssue> Issues { get; set; } = new List<ValidationIssue>();
        public List<string> IgnoredFiles { get; set; } = new List<string>();
        public List<string> Notes { get; set; } = new List<string>();
    }
}
