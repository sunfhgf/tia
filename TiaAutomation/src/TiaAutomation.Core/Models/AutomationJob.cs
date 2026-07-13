using System.Collections.Generic;

namespace TiaAutomation.Core.Models
{
    public class AutomationJob
    {
        public string ProjectName { get; set; }
        public ProjectSettings Project { get; set; }
        public string TemplateProject { get; set; }
        public string NetworkProjectDirectory { get; set; }
        public string NetworkPreparationReport { get; set; }
        public List<DeviceRequest> Devices { get; set; } = new List<DeviceRequest>();
        public List<IoPoint> IoPoints { get; set; } = new List<IoPoint>();
        public List<CylinderRequest> Cylinders { get; set; } = new List<CylinderRequest>();
        public List<ServoRequest> Servos { get; set; } = new List<ServoRequest>();
        public List<MotorRequest> Motors { get; set; } = new List<MotorRequest>();
        public List<StationRequest> Stations { get; set; } = new List<StationRequest>();
        public List<AlarmRequest> Alarms { get; set; } = new List<AlarmRequest>();
        public List<StationCylinderPlan> StationCylinders { get; set; } = new List<StationCylinderPlan>();
    }
}


