using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using TiaAutomation.Core.Gsd;
using TiaAutomation.Core.Models;

namespace TiaAutomation.Core.Planning
{
    public class AutomationPlanner
    {
        private readonly AddressAllocator _addressAllocator = new AddressAllocator();
        private readonly NamingService _namingService = new NamingService();

        public AutomationPlan Plan(AutomationJob job, DeviceCatalog catalog, GsdScanResult gsdScan)
        {
            var plan = new AutomationPlan
            {
                ProjectName = ResolveProjectName(job),
                Project = job?.Project,
                DevicesToCreate = job?.Devices ?? new List<DeviceRequest>(),
                TagsToCreate = job?.IoPoints ?? new List<IoPoint>(),
                CylinderMappings = job?.Cylinders ?? new List<CylinderRequest>(),
                ServoMappings = job?.Servos ?? new List<ServoRequest>(),
                MotorMappings = job?.Motors ?? new List<MotorRequest>(),
                StationPlans = job?.Stations ?? new List<StationRequest>(),
                AlarmPlans = job?.Alarms ?? new List<AlarmRequest>(),
                StationCylinderPlans = job?.StationCylinders ?? new List<StationCylinderPlan>(),
                IgnoredFiles = gsdScan?.IgnoredFiles ?? new List<string>()
            };

            if (job == null)
            {
                plan.Issues.Add(ValidationIssue.Error("JOB_MISSING", "Automation job is empty."));
                plan.CanApply = false;
                return plan;
            }

            ValidateDevices(job, catalog, gsdScan, plan);
            ValidateIo(job, plan);
            ValidateCylinders(job, plan);
            ValidateStations(job, plan);
            ValidateServos(job, plan);
            ValidateMotors(job, plan);
            ValidateAlarms(job, plan);
            ValidateStationCylinders(job, plan);
            AddManualTasks(job, plan);

            if (gsdScan != null)
            {
                foreach (var warning in gsdScan.Warnings)
                {
                    plan.Issues.Add(ValidationIssue.Warning("GSD_WARNING", warning));
                }
            }

            plan.Notes.Add("Apply generates plans, tag CSV, DB/FC sources, and writes supported PLC settings. Hardware device creation remains a controlled step.");
            plan.CanApply = !plan.Issues.Any(issue => issue.Severity == "Error");
            return plan;
        }

        public AutomationJob LoadJob(string path)
        {
            return LoadJson<AutomationJob>(path);
        }

        public DeviceCatalog LoadCatalog(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return new DeviceCatalog();
            }

            return LoadJson<DeviceCatalog>(path) ?? new DeviceCatalog();
        }

        private void ValidateDevices(AutomationJob job, DeviceCatalog catalog, GsdScanResult gsdScan, AutomationPlan plan)
        {
            foreach (var group in job.Devices.GroupBy(d => d.Name).Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1))
            {
                plan.Issues.Add(ValidationIssue.Error("DUPLICATE_DEVICE_NAME", $"Duplicate device name: {group.Key}", group.Key));
            }

            foreach (var group in job.Devices.GroupBy(d => d.ProfinetName).Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1))
            {
                plan.Issues.Add(ValidationIssue.Error("DUPLICATE_PROFINET_NAME", $"Duplicate PROFINET name: {group.Key}", group.Key));
            }

            foreach (var group in job.Devices.GroupBy(d => d.IpAddress).Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1))
            {
                plan.Issues.Add(ValidationIssue.Error("DUPLICATE_IP", $"Duplicate IP address: {group.Key}", group.Key));
            }

            foreach (var device in job.Devices)
            {
                if (string.IsNullOrWhiteSpace(device.Name))
                {
                    plan.Issues.Add(ValidationIssue.Error("DEVICE_NAME_MISSING", "Device name is required."));
                }

                var normalizedProfinetName = _namingService.NormalizeProfinetName(device.ProfinetName);
                if (!string.Equals(device.ProfinetName, normalizedProfinetName, System.StringComparison.Ordinal))
                {
                    device.ProfinetName = normalizedProfinetName;
                    plan.Issues.Add(ValidationIssue.Warning("PROFINET_NAME_NORMALIZED", $"PROFINET name normalized to: {normalizedProfinetName}", device.Name));
                }

                if (!_namingService.IsValidProfinetName(device.ProfinetName))
                {
                    plan.Issues.Add(ValidationIssue.Error("INVALID_PROFINET_NAME", $"Invalid PROFINET name: {device.ProfinetName}", device.Name));
                }

                var entry = catalog.Devices.FirstOrDefault(d => d.Type == device.DeviceType);
                if (entry == null)
                {
                    if (!HasGsdReference(device))
                    {
                        plan.Issues.Add(ValidationIssue.Error("UNSUPPORTED_DEVICE_TYPE", $"Unsupported device type: {device.DeviceType}", device.Name));
                        continue;
                    }

                    if (!MatchesSelectedGsd(device, gsdScan))
                    {
                        plan.Issues.Add(ValidationIssue.Warning("GSD_NOT_FOUND", $"No matching installed GSDML metadata found for {device.DeviceType}.", device.Name));
                    }
                    continue;
                }

                var matchedGsd = gsdScan?.Devices.Any(g =>
                    Matches(entry.VendorName, g.VendorName) &&
                    Matches(entry.VendorId, g.VendorId) &&
                    Matches(entry.DeviceId, g.DeviceId) &&
                    (string.IsNullOrWhiteSpace(entry.GsdFileContains) || g.FileName.ToLowerInvariant().Contains(entry.GsdFileContains.ToLowerInvariant()))) == true;

                if (!matchedGsd)
                {
                    plan.Issues.Add(ValidationIssue.Warning("GSD_NOT_FOUND", $"No matching GSDML metadata found for {device.DeviceType}.", device.Name));
                }
            }
        }

        private void ValidateIo(AutomationJob job, AutomationPlan plan)
        {
            var devices = new HashSet<string>(job.Devices.Select(d => d.Name));
            var addresses = new HashSet<string>();

            foreach (var group in job.IoPoints.GroupBy(p => p.Tag).Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1))
            {
                plan.Issues.Add(ValidationIssue.Error("DUPLICATE_TAG", $"Duplicate tag name: {group.Key}", group.Key));
            }

            foreach (var point in job.IoPoints)
            {
                if (!devices.Contains(point.Device))
                {
                    plan.Issues.Add(ValidationIssue.Error("IO_DEVICE_MISSING", $"IO point references unknown device: {point.Device}", point.Tag));
                }

                if (!_namingService.IsReasonableTagName(point.Tag))
                {
                    plan.Issues.Add(ValidationIssue.Error("INVALID_TAG", $"Invalid tag name: {point.Tag}", point.Tag));
                }

                if (!string.Equals(point.DataType, "Bool", System.StringComparison.OrdinalIgnoreCase))
                {
                    plan.Issues.Add(ValidationIssue.Warning("NON_BOOL_IO", $"Only Bool IO is fully validated in the MVP: {point.Tag}", point.Tag));
                }

                if (!_addressAllocator.TryParseBoolAddress(point.Address, out _, out _, out _))
                {
                    plan.Issues.Add(ValidationIssue.Error("INVALID_ADDRESS", $"Invalid Bool IO address: {point.Address}", point.Tag));
                    continue;
                }

                var normalized = _addressAllocator.NormalizeBoolAddress(point.Address);
                if (!addresses.Add(normalized))
                {
                    plan.Issues.Add(ValidationIssue.Error("DUPLICATE_ADDRESS", $"Duplicate IO address: {normalized}", point.Tag));
                }
            }
        }

        private void ValidateCylinders(AutomationJob job, AutomationPlan plan)
        {
            var tags = new HashSet<string>(job.IoPoints.Select(p => p.Tag));
            var stations = new HashSet<string>(job.Stations.Select(s => s.Id));

            foreach (var cylinder in job.Cylinders)
            {
                if (!string.IsNullOrWhiteSpace(cylinder.Station) && !stations.Contains(cylinder.Station))
                {
                    plan.Issues.Add(ValidationIssue.Warning("CYLINDER_STATION_UNKNOWN", $"Cylinder {cylinder.Id} references unknown station: {cylinder.Station}", cylinder.Id));
                }

                RequireTag(tags, cylinder.ExtendFeedback, cylinder.Id, "extend feedback", plan);
                RequireTag(tags, cylinder.RetractFeedback, cylinder.Id, "retract feedback", plan);
                RequireTag(tags, cylinder.ExtendOutput, cylinder.Id, "extend output", plan);

                if (string.Equals(cylinder.Type, "DoubleSolenoid", System.StringComparison.OrdinalIgnoreCase))
                {
                    RequireTag(tags, cylinder.RetractOutput, cylinder.Id, "retract output", plan);
                }

                if (cylinder.Mode != null && cylinder.Mode.Shield && cylinder.ShieldDelayMs >= cylinder.AlarmTimeMs && cylinder.AlarmTimeMs > 0)
                {
                    plan.Issues.Add(ValidationIssue.Warning("SHIELD_DELAY_TOO_LONG", $"Cylinder {cylinder.Id} shield delay should be shorter than alarm time.", cylinder.Id));
                }
            }
        }

        private void ValidateStations(AutomationJob job, AutomationPlan plan)
        {
            foreach (var group in job.Stations.GroupBy(s => s.Id).Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1))
            {
                plan.Issues.Add(ValidationIssue.Error("DUPLICATE_STATION", $"Duplicate station id: {group.Key}", group.Key));
            }

            var devices = new HashSet<string>(job.Devices.Select(d => d.Name));
            foreach (var station in job.Stations)
            {
                if (string.IsNullOrWhiteSpace(station.Id))
                {
                    plan.Issues.Add(ValidationIssue.Error("STATION_ID_MISSING", "Station id is required."));
                }

                foreach (var device in station.Devices ?? new List<string>())
                {
                    if (!devices.Contains(device))
                    {
                        plan.Issues.Add(ValidationIssue.Warning("STATION_DEVICE_UNKNOWN", $"Station {station.Id} references unknown device: {device}", station.Id));
                    }
                }
            }
        }

        private void ValidateServos(AutomationJob job, AutomationPlan plan)
        {
            var devices = new HashSet<string>(job.Devices.Select(d => d.Name));
            var stations = new HashSet<string>(job.Stations.Select(s => s.Id));

            foreach (var servo in job.Servos)
            {
                if (!string.IsNullOrWhiteSpace(servo.Device) && !devices.Contains(servo.Device))
                {
                    plan.Issues.Add(ValidationIssue.Warning("SERVO_DEVICE_UNKNOWN", $"Servo {servo.Name} references unknown device: {servo.Device}", servo.Name));
                }

                if (!string.IsNullOrWhiteSpace(servo.Station) && !stations.Contains(servo.Station))
                {
                    plan.Issues.Add(ValidationIssue.Warning("SERVO_STATION_UNKNOWN", $"Servo {servo.Name} references unknown station: {servo.Station}", servo.Name));
                }

                if (string.IsNullOrWhiteSpace(servo.Telegram))
                {
                    plan.Issues.Add(ValidationIssue.Warning("SERVO_TELEGRAM_MISSING", $"Servo {servo.Name} has no telegram configured.", servo.Name));
                }
            }
        }

        private void ValidateMotors(AutomationJob job, AutomationPlan plan)
        {
            var tags = new HashSet<string>(job.IoPoints.Select(p => p.Tag));
            var stations = new HashSet<string>(job.Stations.Select(s => s.Id));

            foreach (var motor in job.Motors)
            {
                if (!string.IsNullOrWhiteSpace(motor.Station) && !stations.Contains(motor.Station))
                {
                    plan.Issues.Add(ValidationIssue.Warning("MOTOR_STATION_UNKNOWN", $"Motor {motor.Name} references unknown station: {motor.Station}", motor.Name));
                }

                if (!string.IsNullOrWhiteSpace(motor.RunOutput) && !tags.Contains(motor.RunOutput))
                {
                    plan.Issues.Add(ValidationIssue.Warning("MOTOR_RUN_TAG_MISSING", $"Motor {motor.Name} run output tag is not in ioPoints: {motor.RunOutput}", motor.Name));
                }

                if (!string.IsNullOrWhiteSpace(motor.FaultInput) && !tags.Contains(motor.FaultInput))
                {
                    plan.Issues.Add(ValidationIssue.Warning("MOTOR_FAULT_TAG_MISSING", $"Motor {motor.Name} fault input tag is not in ioPoints: {motor.FaultInput}", motor.Name));
                }
            }
        }

        private void ValidateAlarms(AutomationJob job, AutomationPlan plan)
        {
            var stations = new HashSet<string>(job.Stations.Select(s => s.Id));

            foreach (var alarm in job.Alarms)
            {
                if (!string.IsNullOrWhiteSpace(alarm.Station) && !stations.Contains(alarm.Station))
                {
                    plan.Issues.Add(ValidationIssue.Warning("ALARM_STATION_UNKNOWN", $"Alarm references unknown station: {alarm.Station}", alarm.Source));
                }

                if (string.IsNullOrWhiteSpace(alarm.Text))
                {
                    plan.Issues.Add(ValidationIssue.Warning("ALARM_TEXT_MISSING", $"Alarm text is missing for source: {alarm.Source}", alarm.Source));
                }
            }
        }

        private void ValidateStationCylinders(AutomationJob job, AutomationPlan plan)
        {
            var stationIds = new HashSet<string>(job.Stations.Select(s => s.Id), System.StringComparer.OrdinalIgnoreCase);

            foreach (var group in job.StationCylinders.GroupBy(s => s.StationId, System.StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1))
            {
                plan.Issues.Add(ValidationIssue.Error("DUPLICATE_STATION_CYL", $"Duplicate stationCylinders entry for station: {group.Key}", group.Key));
            }

            foreach (var sc in job.StationCylinders)
            {
                if (string.IsNullOrWhiteSpace(sc.StationId))
                {
                    plan.Issues.Add(ValidationIssue.Error("STATION_CYL_ID_MISSING", "stationCylinders entry needs stationId."));
                    continue;
                }
                if (stationIds.Count > 0 && !stationIds.Contains(sc.StationId))
                {
                    plan.Issues.Add(ValidationIssue.Warning("STATION_CYL_STATION_UNKNOWN", $"stationCylinders references unknown station: {sc.StationId}", sc.StationId));
                }

                foreach (var blockField in new[] { sc.InstanceDb, sc.StationIDb, sc.StationQDb, sc.Param1Db, sc.Param2Db, sc.AlarmDb })
                {
                    if (string.IsNullOrWhiteSpace(blockField))
                    {
                        plan.Issues.Add(ValidationIssue.Error("STATION_CYL_DB_NAME_MISSING", $"Station {sc.StationId} is missing one of instanceDb/station I DB/station Q DB/param DB/alarm DB.", sc.StationId));
                        break;
                    }
                }

                var seenIndex = new HashSet<int>();
                var ioTags = new HashSet<string>(job.IoPoints.Select(p => p.Tag), System.StringComparer.OrdinalIgnoreCase);
                foreach (var c in sc.Cylinders ?? new List<StationCylinder>())
                {
                    if (c.Index < 1 || c.Index > 16)
                    {
                        plan.Issues.Add(ValidationIssue.Error("CYL_INDEX_OUT_OF_RANGE", $"Station {sc.StationId} 姘旂几 index {c.Index} 蹇呴』鍦?1..16 涔嬪唴", sc.StationId));
                        continue;
                    }
                    if (!seenIndex.Add(c.Index))
                    {
                        plan.Issues.Add(ValidationIssue.Error("CYL_INDEX_DUP", $"Station {sc.StationId} 姘旂几 index {c.Index} 閲嶅", sc.StationId));
                    }
                    if (c.AlarmTimeMs > 0 && c.SettleTimeMs >= c.AlarmTimeMs)
                    {
                        plan.Issues.Add(ValidationIssue.Warning("CYL_SETTLE_GE_ALARM", $"Station {sc.StationId} cylinder {c.Index} settle time should be shorter than alarm time.", sc.StationId));
                    }
                    foreach (var pair in new[] {
                        new { Field = "extendIo", Value = c.ExtendIo },
                        new { Field = "retractIo", Value = c.RetractIo },
                        new { Field = "extendOut", Value = c.ExtendOut },
                        new { Field = "retractOut", Value = c.RetractOut } })
                    {
                        if (string.IsNullOrWhiteSpace(pair.Value)) continue;
                        if (ioTags.Count > 0 && !ioTags.Contains(pair.Value))
                        {
                            plan.Issues.Add(ValidationIssue.Error("STATION_CYL_TAG_MISSING", $"Station {sc.StationId} 姘旂几{c.Index} 鐨?{pair.Field} 鏍囩 '{pair.Value}' 鍦?ioPoints 涓笉瀛樺湪", sc.StationId));
                        }
                    }
                }
            }
        }

        private void AddManualTasks(AutomationJob job, AutomationPlan plan)
        {
            plan.ManualTasks.Add("Confirm GSD files are installed in TIA Portal. The tool scans installed GSDML metadata but does not install GSD files automatically.");
            plan.ManualTasks.Add("Verify hardware devices, PROFINET names, IP addresses, and I/Q start addresses in TIA Portal before download.");
            plan.ManualTasks.Add("Verify servo telegrams, hardware identifiers, and telegram addresses against the real hardware configuration.");
            plan.ManualTasks.Add("Confirm station safety interlocks and alarm display rules.");

            foreach (var station in job.Stations)
            {
                plan.ManualTasks.Add($"Station {station.Id}({station.Name}): check logic block {station.LogicBlock} conditions and comments.");
            }
        }
        private static string ResolveProjectName(AutomationJob job)
        {
            if (job == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(job.ProjectName))
            {
                return job.ProjectName;
            }

            return job.Project?.BuildProjectName();
        }

        private static void RequireTag(HashSet<string> tags, string tag, string cylinderId, string role, AutomationPlan plan)
        {
            if (string.IsNullOrWhiteSpace(tag) || !tags.Contains(tag))
            {
                plan.Issues.Add(ValidationIssue.Error("CYLINDER_TAG_MISSING", $"Cylinder {cylinderId} references missing {role} tag: {tag}", cylinderId));
            }
        }

        private static bool HasGsdReference(DeviceRequest device)
        {
            return !string.IsNullOrWhiteSpace(device?.VendorId)
                || !string.IsNullOrWhiteSpace(device?.DeviceId)
                || !string.IsNullOrWhiteSpace(device?.GsdFileName)
                || !string.IsNullOrWhiteSpace(device?.OrderNumber);
        }

        private static bool MatchesSelectedGsd(DeviceRequest device, GsdScanResult gsdScan)
        {
            if (device == null || gsdScan?.Devices == null) return false;
            return gsdScan.Devices.Any(g =>
                Matches(device.VendorName, g.VendorName) &&
                Matches(device.VendorId, g.VendorId) &&
                Matches(device.DeviceId, g.DeviceId) &&
                (string.IsNullOrWhiteSpace(device.GsdFileName) || string.Equals(device.GsdFileName, g.FileName, System.StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(device.OrderNumber) || g.AccessPoints.Any(a => string.Equals(device.OrderNumber, a.OrderNumber, System.StringComparison.OrdinalIgnoreCase))));
        }
        private static bool Matches(string expected, string actual)
        {
            return string.IsNullOrWhiteSpace(expected) || string.Equals(expected, actual, System.StringComparison.OrdinalIgnoreCase);
        }

        private static T LoadJson<T>(string path)
        {
            var serializer = new JavaScriptSerializer();
            return serializer.Deserialize<T>(File.ReadAllText(path));
        }
    }
}



