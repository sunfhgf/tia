using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace TiaAutomation.Openness
{
    public class TiaPortalSession : IDisposable
    {
        private readonly string _opennessAssemblyPath;
        private object _tiaPortal;
        private Assembly _engineeringAssembly;

        public object Portal => _tiaPortal;

        public TiaPortalSession(string opennessAssemblyPath = null)
        {
            _opennessAssemblyPath = opennessAssemblyPath;
        }

        public bool IsAvailable(out string diagnostic)
        {
            diagnostic = null;
            var path = ResolveAssemblyPath();
            if (!File.Exists(path))
            {
                diagnostic = $"Siemens.Engineering.dll not found. Expected: {path}";
                return false;
            }

            return true;
        }

        public object OpenProject(string projectPath)
        {
            var assemblyPath = ResolveAssemblyPath();
            _engineeringAssembly = Assembly.LoadFrom(assemblyPath);

            var tiaPortalType = _engineeringAssembly.GetType("Siemens.Engineering.TiaPortal", true);

            // Prefer attaching to a running TIA Portal instance before starting a new one.
            var processesProperty = tiaPortalType.GetProperty("Processes", BindingFlags.Public | BindingFlags.Static);
            if (processesProperty != null)
            {
                var procs = processesProperty.GetValue(null, null) as System.Collections.IEnumerable;
                if (procs != null)
                {
                    foreach (var proc in procs)
                    {
                        var attach = proc.GetType().GetMethod("Attach", Type.EmptyTypes);
                        if (attach == null) continue;
                        try
                        {
                            _tiaPortal = attach.Invoke(proc, null);
                            break;
                        }
                        catch
                        {
                            // 缁х画灏濊瘯涓嬩竴涓垨鍥為€€鍒版柊鍚姩
                        }
                    }
                }
            }

            if (_tiaPortal == null)
            {
                var modeType = _engineeringAssembly.GetType("Siemens.Engineering.TiaPortalMode", true);
                var withoutUserInterface = Enum.Parse(modeType, "WithoutUserInterface");
                _tiaPortal = Activator.CreateInstance(tiaPortalType, withoutUserInterface);
            }

            var projectsProperty = tiaPortalType.GetProperty("Projects");
            var projects = projectsProperty.GetValue(_tiaPortal, null);
            var openMethod = projects.GetType().GetMethods().FirstOrDefault(m => m.Name == "Open" && m.GetParameters().Length == 1);
            if (openMethod == null)
            {
                throw new InvalidOperationException("Could not find TIA Openness Projects.Open method.");
            }

            return openMethod.Invoke(projects, new object[] { new FileInfo(projectPath) });
        }

        public void SaveProject(object project)
        {
            var saveMethod = project.GetType().GetMethod("Save", Type.EmptyTypes);
            if (saveMethod == null)
            {
                throw new InvalidOperationException("Could not find TIA Openness Project.Save method.");
            }

            saveMethod.Invoke(project, null);
        }

        public void Dispose()
        {
            if (_tiaPortal is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        private string ResolveAssemblyPath()
        {
            if (!string.IsNullOrWhiteSpace(_opennessAssemblyPath))
            {
                return _opennessAssemblyPath;
            }

            return @"E:\SOFT\TIA\Portal V20\PublicAPI\V20\Siemens.Engineering.dll";
        }
    }
}


