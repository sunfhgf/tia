using System;
using System.Collections.Generic;
using System.Linq;
using TiaAutomation.Core.Models;

namespace TiaAutomation.Openness
{
    /// <summary>
    /// 一次 Openness 会话内顺序写入：PLC 硬件设置 + PLC tags + 工位 DB + IO 映射 FC + 伺服/电机/报警 FC 骨架。
    /// 避免 TIA 工程 2 分钟独占锁。
    /// </summary>
    public class TiaProjectWriter
    {
        public CombinedWriteResult WriteAll(string projectPath, IEnumerable<IoPoint> tags, string tagTableName, IEnumerable<StationCylinderPlan> stations, IEnumerable<ServoRequest> servos, IEnumerable<MotorRequest> motors, IEnumerable<AlarmRequest> alarms, string xmlScratchDir, string opennessAssemblyPath = null)
        {
            return WriteAll(projectPath, null, tags, tagTableName, stations, servos, motors, alarms, xmlScratchDir, opennessAssemblyPath);
        }

        public CombinedWriteResult WriteAll(string projectPath, ProjectSettings projectSettings, IEnumerable<IoPoint> tags, string tagTableName, IEnumerable<StationCylinderPlan> stations, IEnumerable<ServoRequest> servos, IEnumerable<MotorRequest> motors, IEnumerable<AlarmRequest> alarms, string xmlScratchDir, string opennessAssemblyPath = null)
        {
            return WriteAll(projectPath, projectSettings, null, tags, tagTableName, stations, servos, motors, alarms, xmlScratchDir, opennessAssemblyPath);
        }

        public CombinedWriteResult WriteAll(string projectPath, ProjectSettings projectSettings, IEnumerable<DeviceRequest> devices, IEnumerable<IoPoint> tags, string tagTableName, IEnumerable<StationCylinderPlan> stations, IEnumerable<ServoRequest> servos, IEnumerable<MotorRequest> motors, IEnumerable<AlarmRequest> alarms, string xmlScratchDir, string opennessAssemblyPath = null)
        {
            var combined = new CombinedWriteResult { ProjectPath = projectPath };

            using (var session = new TiaPortalSession(opennessAssemblyPath))
            {
                if (!session.IsAvailable(out var diag))
                {
                    combined.Diagnostic = diag;
                    return combined;
                }

                object project;
                try
                {
                    project = session.OpenProject(System.IO.Path.GetFullPath(projectPath));
                }
                catch (Exception ex)
                {
                    combined.Diagnostic = ex.GetBaseException().Message;
                    return combined;
                }

                var sclDir = System.IO.Path.Combine(xmlScratchDir, "scl");

                var deviceList = (devices ?? Enumerable.Empty<DeviceRequest>()).ToList();
                var tagList = (tags ?? Enumerable.Empty<IoPoint>()).ToList();
                var stationList = (stations ?? Enumerable.Empty<StationCylinderPlan>()).ToList();
                var servoList = (servos ?? Enumerable.Empty<ServoRequest>()).ToList();
                var motorList = (motors ?? Enumerable.Empty<MotorRequest>()).ToList();
                var alarmList = (alarms ?? Enumerable.Empty<AlarmRequest>()).ToList();

                combined.PlcHardwareResult = new PlcHardwareWriter().WriteOnOpenedProject(project, projectSettings);
                if (deviceList.Count > 0) combined.DeviceWriteResult = new DeviceWriter().WriteOnOpenedProject(session.Portal, project, deviceList);
                if (tagList.Count > 0) combined.TagWriteResult = new TagWriter().WriteOnOpenedProject(project, tagList, tagTableName);
                if (stationList.Count > 0) combined.DbWriteResult = new DbWriter().WriteOnOpenedProject(project, stationList, xmlScratchDir);
                if (stationList.Count > 0) combined.MappingFcResult = new MappingFcWriter().WriteOnOpenedProject(project, stationList, sclDir);
                if (servoList.Count > 0) combined.ServoFcResult = new ServoFcWriter().WriteOnOpenedProject(project, servoList, sclDir);
                if (motorList.Count > 0) combined.MotorFcResult = new MotorFcWriter().WriteOnOpenedProject(project, motorList, sclDir);
                if (alarmList.Count > 0) combined.AlarmFcResult = new AlarmFcWriter().WriteOnOpenedProject(project, alarmList, sclDir);

                try
                {
                    session.SaveProject(project);
                    combined.Saved = true;
                }
                catch (Exception ex)
                {
                    combined.Diagnostic = "Save failed: " + ex.GetBaseException().Message;
                    return combined;
                }
            }

            combined.Diagnostic = "Project saved.";
            return combined;
        }
    }

    public class CombinedWriteResult
    {
        public string ProjectPath { get; set; }
        public bool Saved { get; set; }
        public string Diagnostic { get; set; }
        public PlcHardwareWriteResult PlcHardwareResult { get; set; }
        public DeviceWriteResult DeviceWriteResult { get; set; }
        public TiaWriteResult TagWriteResult { get; set; }
        public DbWriteResult DbWriteResult { get; set; }
        public MappingFcResult MappingFcResult { get; set; }
        public ServoFcResult ServoFcResult { get; set; }
        public MotorFcResult MotorFcResult { get; set; }
        public AlarmFcResult AlarmFcResult { get; set; }
    }
}



