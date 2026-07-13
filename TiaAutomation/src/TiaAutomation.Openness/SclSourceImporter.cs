using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace TiaAutomation.Openness
{
    /// <summary>
    /// 通用 SCL 外部源导入：写 .scl 文件 → 通过 ExternalSources.CreateFromFile 导入
    /// → 调 GenerateBlocksFromSource 生成 FC/FB。供 MappingFcWriter / ServoFcWriter
    /// / MotorFcWriter / AlarmFcWriter 等共用，避免反射代码重复。
    /// </summary>
    internal static class SclSourceImporter
    {
        public static bool Import(object project, string fcName, string sclText, string sourceScratchDir, out string sclPath, out string warning)
        {
            sclPath = null;
            warning = null;
            sourceScratchDir = Path.GetFullPath(sourceScratchDir);
            Directory.CreateDirectory(sourceScratchDir);

            var plcSoftware = PlcSoftwareLocator.FindFirstPlcSoftware(project);
            if (plcSoftware == null) { warning = "No PLC software container."; return false; }

            var externalSourceGroup = OpennessReflection.ReadProperty(plcSoftware, "ExternalSourceGroup");
            if (externalSourceGroup == null) { warning = "PlcSoftware.ExternalSourceGroup not found."; return false; }

            var sources = OpennessReflection.ReadProperty(externalSourceGroup, "ExternalSources");
            if (sources == null) { warning = "ExternalSources collection missing."; return false; }

            sclPath = Path.GetFullPath(Path.Combine(sourceScratchDir, fcName + ".scl"));
            File.WriteAllText(sclPath, sclText, new UTF8Encoding(true));

            object src;
            try
            {
                src = CreateExternalSource(sources, fcName, sclPath);
            }
            catch (Exception ex)
            {
                warning = $"创建外部源 {fcName} 失败：{ex.GetBaseException().Message}";
                return false;
            }

            if (src == null) { warning = $"创建外部源 {fcName} 返回 null"; return false; }

            var method = src.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "GenerateBlocksFromSource" && m.GetParameters().Length == 0);
            if (method == null) { warning = "GenerateBlocksFromSource 不可用。"; return false; }

            try
            {
                method.Invoke(src, null);
                return true;
            }
            catch (Exception ex)
            {
                warning = $"GenerateBlocksFromSource 失败：{ex.GetBaseException().Message}";
                return false;
            }
        }

        private static object CreateExternalSource(object sources, string fcName, string sclPath)
        {
            var existing = OpennessReflection.FindNamedChild(sources, fcName + ".scl");
            if (existing != null)
            {
                var deleteMethod = existing.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "Delete" && m.GetParameters().Length == 0);
                deleteMethod?.Invoke(existing, null);
            }

            var createMethod = sources.GetType().GetMethods()
                .FirstOrDefault(m => m.Name == "CreateFromFile"
                    && m.GetParameters().Length == 2
                    && m.GetParameters()[0].ParameterType == typeof(string)
                    && m.GetParameters()[1].ParameterType == typeof(string));
            if (createMethod == null)
            {
                throw new InvalidOperationException("ExternalSources.CreateFromFile(string, string) 不可用。");
            }

            return createMethod.Invoke(sources, new object[] { fcName + ".scl", sclPath });
        }
    }
}
