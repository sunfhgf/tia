using System.Collections.Generic;

namespace TiaAutomation.Openness
{
    public class BlockInspectionResult
    {
        public bool Success { get; set; }
        public string Diagnostic { get; set; }
        public string ProjectPath { get; set; }
        public string BlockName { get; set; }
        public string BlockPath { get; set; }
        public string BlockType { get; set; }
        public int? Number { get; set; }
        public string ProgrammingLanguage { get; set; }
        public string ExportXmlPath { get; set; }
        public List<BlockMember> Inputs { get; set; } = new List<BlockMember>();
        public List<BlockMember> Outputs { get; set; } = new List<BlockMember>();
        public List<BlockMember> InOuts { get; set; } = new List<BlockMember>();
        public List<BlockMember> Statics { get; set; } = new List<BlockMember>();
        public List<BlockMember> Temps { get; set; } = new List<BlockMember>();
        public List<BlockMember> Constants { get; set; } = new List<BlockMember>();
        public List<BlockMember> Returns { get; set; } = new List<BlockMember>();
        public List<string> InstanceDbs { get; set; } = new List<string>();
    }

    public class BlockMember
    {
        public string Name { get; set; }
        public string DataType { get; set; }
        public string Comment { get; set; }
        public string Default { get; set; }
        public bool IsStruct { get; set; }
        public List<BlockMember> Members { get; set; } = new List<BlockMember>();
    }
}
