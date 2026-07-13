namespace TiaAutomation.Core.Models
{
    public class ServoRequest
    {
        public string Name { get; set; }
        public string Station { get; set; }
        public string Device { get; set; }
        public string AxisName { get; set; }
        public string Telegram { get; set; }
        public int? HardwareId { get; set; }
        public int? TelegramAddress { get; set; }
        public string LogicBlock { get; set; }
    }
}
