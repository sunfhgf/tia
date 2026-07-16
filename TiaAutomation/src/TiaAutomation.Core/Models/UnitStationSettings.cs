using System.Collections.Generic;

namespace TiaAutomation.Core.Models
{
    public class UnitStationSettings
    {
        public string Name { get; set; }
        public string DataTypeName { get; set; }
        public List<string> CylinderNames { get; set; } = new List<string>();
        public List<int?> CylinderValveMasterIndexes { get; set; } = new List<int?>();
        public List<string> CylinderExtendSafetyConditions { get; set; } = new List<string>();
        public List<string> CylinderRetractSafetyConditions { get; set; } = new List<string>();
        public List<string> SensorNames { get; set; } = new List<string>();
    }
}
