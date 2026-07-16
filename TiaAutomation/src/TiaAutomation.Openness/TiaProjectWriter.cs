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
            return WriteAll(projectPath, projectSettings, devices, null, tags, tagTableName, stations, servos, motors, alarms, xmlScratchDir, opennessAssemblyPath);
        }

        public CombinedWriteResult WriteAll(string projectPath, ProjectSettings projectSettings, IEnumerable<DeviceRequest> devices, IEnumerable<IoCommentRequest> ioComments, IEnumerable<IoPoint> tags, string tagTableName, IEnumerable<StationCylinderPlan> stations, IEnumerable<ServoRequest> servos, IEnumerable<MotorRequest> motors, IEnumerable<AlarmRequest> alarms, string xmlScratchDir, string opennessAssemblyPath = null)
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
                var ioCommentList = (ioComments ?? Enumerable.Empty<IoCommentRequest>()).ToList();
                var tagList = (tags ?? Enumerable.Empty<IoPoint>()).ToList();
                var stationList = (stations ?? Enumerable.Empty<StationCylinderPlan>()).ToList();
                var servoList = (servos ?? Enumerable.Empty<ServoRequest>()).ToList();
                var motorList = (motors ?? Enumerable.Empty<MotorRequest>()).ToList();
                var alarmList = (alarms ?? Enumerable.Empty<AlarmRequest>()).ToList();

                combined.PlcHardwareResult = new PlcHardwareWriter().WriteOnOpenedProject(project, projectSettings);
                if ((projectSettings?.UnitCount ?? 1) > 1) combined.UnitFolderResult = new UnitFolderWriter().WriteOnOpenedProject(project, projectSettings);
                if ((projectSettings?.UnitServoCounts ?? new List<int?>()).Any(value => value.HasValue))
                {
                    combined.UnitServoResult = new UnitServoWriter().WriteOnOpenedProject(
                        project, projectSettings, deviceList, System.IO.Path.Combine(xmlScratchDir, "unit-servos"));
                }
                if ((projectSettings?.UnitStations ?? new List<List<UnitStationSettings>>())
                    .Any(unit => (unit ?? new List<UnitStationSettings>()).Any(station => !string.IsNullOrWhiteSpace(station?.DataTypeName))))
                {
                    combined.UnitStationTypeResult = new UnitStationTypeWriter().WriteOnOpenedProject(
                        project, projectSettings, System.IO.Path.Combine(xmlScratchDir, "unit-stations"));
                }
                if (deviceList.Count > 0) combined.DeviceWriteResult = new DeviceWriter().WriteOnOpenedProject(session.Portal, project, deviceList);
                if (tagList.Count > 0) combined.TagWriteResult = new TagWriter().WriteOnOpenedProject(project, tagList, tagTableName);
                if (ioCommentList.Count > 0) combined.IoCommentResult = new IoCommentWriter().WriteOnOpenedProject(project, ioCommentList);
                if ((projectSettings?.UnitStations ?? new List<List<UnitStationSettings>>())
                    .Any(unit => (unit ?? new List<UnitStationSettings>()).Any(station => !string.IsNullOrWhiteSpace(station?.DataTypeName))))
                {
                    combined.IoProcessingResult = new IoProcessingWriter().WriteOnOpenedProject(
                        project, projectSettings, ioCommentList, tagList,
                        System.IO.Path.Combine(xmlScratchDir, "io-processing"));
                    combined.CylinderLogicResult = new CylinderLogicWriter().WriteOnOpenedProject(
                        project, projectSettings, ioCommentList, tagList,
                        System.IO.Path.Combine(xmlScratchDir, "cylinder-logic"));
                }
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
        public UnitFolderWriteResult UnitFolderResult { get; set; }
        public UnitServoWriteResult UnitServoResult { get; set; }
        public UnitStationTypeWriteResult UnitStationTypeResult { get; set; }
        public DeviceWriteResult DeviceWriteResult { get; set; }
        public TiaWriteResult TagWriteResult { get; set; }
        public IoCommentWriteResult IoCommentResult { get; set; }
        public IoProcessingWriteResult IoProcessingResult { get; set; }
        public CylinderLogicWriteResult CylinderLogicResult { get; set; }
        public DbWriteResult DbWriteResult { get; set; }
        public MappingFcResult MappingFcResult { get; set; }
        public ServoFcResult ServoFcResult { get; set; }
        public MotorFcResult MotorFcResult { get; set; }
        public AlarmFcResult AlarmFcResult { get; set; }
    }
}



