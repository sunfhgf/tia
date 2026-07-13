using System.Collections.Generic;

namespace TiaAutomation.Core.Models
{
    /// <summary>
    /// FB22 气缸块 标准化的 1 个工位实例配置：
    /// - 1 个 FB22 InstanceDB
    /// - 5 个基于 UDT 的工位 GlobalDB（工位I / 工位Q / 气缸参数1 / 气缸参数2 / 报警D）
    /// - 1..16 路气缸的物理 IO 与参数
    /// </summary>
    public class StationCylinderPlan
    {
        public string StationId { get; set; }
        public string StationName { get; set; }
        public string CylinderFb { get; set; } = "气缸块";
        public string InstanceDb { get; set; }
        public string StationIDb { get; set; }
        public string StationQDb { get; set; }
        public string Param1Db { get; set; }
        public string Param2Db { get; set; }
        public string AlarmDb { get; set; }
        public string SystemDb { get; set; }
        public List<StationCylinder> Cylinders { get; set; } = new List<StationCylinder>();
    }

    public class StationCylinder
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public string ExtendIo { get; set; }
        public string RetractIo { get; set; }
        public string ExtendOut { get; set; }
        public string RetractOut { get; set; }
        public int AlarmTimeMs { get; set; }
        public int SettleTimeMs { get; set; }
        public bool Shield { get; set; }
        public bool Safe { get; set; } = true;
    }
}
