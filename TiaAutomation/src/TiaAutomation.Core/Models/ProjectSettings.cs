namespace TiaAutomation.Core.Models
{
    public class ProjectSettings
    {
        public string Code { get; set; }
        public string Station { get; set; }
        public string VersionDate { get; set; }
        public string PlcName { get; set; }
        public string PlcIpAddress { get; set; }
        public string NameFormat { get; set; } = "{code}.{station}.V{date}";

        public string BuildProjectName()
        {
            var name = NameFormat ?? "{code}.{station}.V{date}";
            return name.Replace("{code}", Code ?? string.Empty)
                .Replace("{station}", Station ?? string.Empty)
                .Replace("{date}", VersionDate ?? string.Empty);
        }
    }
}

