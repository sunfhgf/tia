namespace TiaAutomation.Core.Models
{
    public class MotorRequest
    {
        public string Name { get; set; }
        public string Station { get; set; }
        public string Device { get; set; }
        public string Type { get; set; }
        public string RunOutput { get; set; }
        public string FaultInput { get; set; }
        public string LogicBlock { get; set; }
    }
}
