using System.Collections.Generic;

namespace TiaAutomation.Core.Models
{
    public class StationRequest
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string LogicBlock { get; set; }
        public List<string> SafetyConditions { get; set; } = new List<string>();
        public List<string> Devices { get; set; } = new List<string>();
    }
}
