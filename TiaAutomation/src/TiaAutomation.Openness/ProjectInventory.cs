using System.Collections.Generic;

namespace TiaAutomation.Openness
{
    public class ProjectInventory
    {
        public string ProjectPath { get; set; }
        public string ProjectName { get; set; }
        public string Status { get; set; }
        public string Diagnostic { get; set; }
        public List<string> Devices { get; set; } = new List<string>();
        public List<PlcInventory> Plcs { get; set; } = new List<PlcInventory>();
        public List<string> Blocks { get; set; } = new List<string>();
        public List<string> TagTables { get; set; } = new List<string>();
    }

    public class PlcInventory
    {
        public string Name { get; set; }
        public List<TagTableInfo> TagTables { get; set; } = new List<TagTableInfo>();
        public List<BlockInfo> Blocks { get; set; } = new List<BlockInfo>();
    }

    public class TagTableInfo
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public int TagCount { get; set; }
    }

    public class BlockInfo
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Type { get; set; }
        public int? Number { get; set; }
        public string ProgrammingLanguage { get; set; }
    }
}
