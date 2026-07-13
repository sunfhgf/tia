using System.Collections.Generic;

namespace TiaAutomation.Openness
{
    public class TiaWriteResult
    {
        public bool Success { get; set; }
        public string ProjectPath { get; set; }
        public string TagTableName { get; set; }
        public string Diagnostic { get; set; }
        public List<string> CreatedTags { get; set; } = new List<string>();
        public List<string> ExistingTags { get; set; } = new List<string>();
        public List<string> SkippedTags { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }
}
