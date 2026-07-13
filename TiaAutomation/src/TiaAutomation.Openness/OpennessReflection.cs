using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace TiaAutomation.Openness
{
    internal static class OpennessReflection
    {
        public static object ReadProperty(object target, string propertyName)
        {
            if (target == null) return null;
            try
            {
                var p = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (p != null) return p.GetValue(target, null);
            }
            catch (System.Reflection.AmbiguousMatchException)
            {
                // 在派生类与基类都声明了同名属性，使用基于声明类型的查找
                var props = target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.Name == propertyName).ToList();
                if (props.Count > 0)
                {
                    // 选择最派生（DeclaringType.IsSubclassOf 最深）的那个
                    var chosen = props.OrderByDescending(p => DepthOf(p.DeclaringType)).First();
                    return chosen.GetValue(target, null);
                }
            }
            return null;
        }

        private static int DepthOf(Type t)
        {
            int d = 0;
            while (t != null && t.BaseType != null) { d++; t = t.BaseType; }
            return d;
        }

        public static IEnumerable ReadEnumerableProperty(object target, string propertyName)
        {
            return ReadProperty(target, propertyName) as IEnumerable;
        }

        public static object FindNamedChild(object collection, string name)
        {
            if (collection is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (string.Equals(ReadProperty(item, "Name") as string, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return item;
                    }
                }
            }
            return null;
        }

        public static int CountEnumerable(object enumerable)
        {
            if (enumerable is ICollection collection)
            {
                return collection.Count;
            }

            if (enumerable is IEnumerable items)
            {
                var count = 0;
                foreach (var _ in items)
                {
                    count++;
                }
                return count;
            }

            return 0;
        }

        public static object InvokeGenericGetService(object target, string serviceTypeFullName)
        {
            if (target == null)
            {
                return null;
            }

            var serviceType = target.GetType().Assembly.GetType(serviceTypeFullName, false)
                ?? AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a =>
                    {
                        try { return a.GetType(serviceTypeFullName, false); }
                        catch { return null; }
                    })
                    .FirstOrDefault(t => t != null);
            if (serviceType == null)
            {
                return null;
            }

            var method = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            if (method == null)
            {
                return null;
            }

            try
            {
                return method.MakeGenericMethod(serviceType).Invoke(target, null);
            }
            catch
            {
                return null;
            }
        }
    }

    internal static class PlcSoftwareLocator
    {
        public static object FindFirstPlcSoftware(object project)
        {
            var devices = OpennessReflection.ReadEnumerableProperty(project, "Devices");
            if (devices == null)
            {
                return null;
            }

            foreach (var device in devices)
            {
                var software = FindInDeviceItems(OpennessReflection.ReadEnumerableProperty(device, "DeviceItems"));
                if (software != null)
                {
                    return software;
                }
            }

            return null;
        }

        private static object FindInDeviceItems(IEnumerable items)
        {
            if (items == null)
            {
                return null;
            }

            foreach (var item in items)
            {
                var software = GetSoftwareContainer(item);
                if (IsPlcSoftware(software))
                {
                    return software;
                }

                var nested = FindInDeviceItems(OpennessReflection.ReadEnumerableProperty(item, "DeviceItems"));
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        private static bool IsPlcSoftware(object software)
        {
            return software != null
                && OpennessReflection.ReadProperty(software, "TagTableGroup") != null
                && OpennessReflection.ReadProperty(software, "BlockGroup") != null;
        }

        private static object GetSoftwareContainer(object deviceItem)
        {
            var container = OpennessReflection.InvokeGenericGetService(deviceItem, "Siemens.Engineering.HW.Features.SoftwareContainer");
            if (container != null)
            {
                var inner = OpennessReflection.ReadProperty(container, "Software");
                if (inner != null)
                {
                    return inner;
                }
            }

            var fallback = OpennessReflection.ReadProperty(deviceItem, "SoftwareContainer");
            if (fallback != null)
            {
                var inner = OpennessReflection.ReadProperty(fallback, "Software");
                if (inner != null)
                {
                    return inner;
                }
            }

            return OpennessReflection.ReadProperty(deviceItem, "Software");
        }
    }
}

