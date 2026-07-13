namespace TiaAutomation.Core.Models
{
    public class AlarmRequest
    {
        public string Station { get; set; }
        public string Source { get; set; }
        public string SourceType { get; set; }
        public string Text { get; set; }
        public string Level { get; set; }
    }
}
