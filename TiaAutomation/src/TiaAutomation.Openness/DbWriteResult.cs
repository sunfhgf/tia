using System.Collections.Generic;

namespace TiaAutomation.Openness
{
    public class DbWriteResult
    {
        public bool Success { get; set; }
        public string Diagnostic { get; set; }
        public string ProjectPath { get; set; }
        public List<DbCreated> CreatedBlocks { get; set; } = new List<DbCreated>();
        public List<DbCreated> ExistingBlocks { get; set; } = new List<DbCreated>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class DbCreated
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string TypeOf { get; set; }
        public int? Number { get; set; }
        public string Station { get; set; }
        public string XmlPath { get; set; }
    }
}
