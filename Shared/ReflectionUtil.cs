using System;
using System.Collections.Generic;
using System.Reflection;
using SteamP2PFriends.Shared;

namespace SteamP2PFriends.Shared
{
    /// <summary>
    /// 缓存式反射工具（对齐原版 SteamP2PFriends ReflectionUtil.cs）。
    ///
    /// </summary>
    internal static class ReflectionUtil
    {
        private static readonly Dictionary<string, MethodInfo> methodCache = new Dictionary<string, MethodInfo>();
        private static readonly Dictionary<string, FieldInfo> fieldCache = new Dictionary<string, FieldInfo>();

        public static void SetStaticField(Type type, string fieldName, object value)
        {
            GetStaticFieldInfo(type, fieldName).SetValue(null, value);
        }

        /// <summary>
        /// Set a static field, or a static property (including auto-props with internal setters).
        /// 用于设置 vanilla Provider 的 auto-property（如 timeLastPacketWasReceivedFromServer、pings）。
        /// </summary>
        public static void SetStaticMember(Type type, string memberName, object value)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

            FieldInfo field = type.GetField(memberName, flags);
            if (field != null) { field.SetValue(null, value); return; }

            // Compiler auto-property backing field, e.g. <timeLastPacketWasReceivedFromServer>k__BackingField
            FieldInfo backing = type.GetField("<" + memberName + ">k__BackingField", flags);
            if (backing != null) { backing.SetValue(null, value); return; }

            PropertyInfo property = type.GetProperty(memberName, flags);
            if (property != null && property.CanWrite)
            {
                property.SetValue(null, value, null);
                return;
            }

            if (property != null)
            {
                MethodInfo setter = property.GetSetMethod(true);
                if (setter != null)
                {
                    setter.Invoke(null, new[] { value });
                    return;
                }
            }

            RoleLogger.Warn("[Shared]", $"SetStaticMember: {type.Name}.{memberName} 既不是字段也不是可写属性");
        }

        public static object GetStaticField(Type type, string fieldName)
        {
            return GetStaticFieldInfo(type, fieldName).GetValue(null);
        }

        public static FieldInfo GetStaticFieldInfo(Type type, string fieldName)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            string key = type.FullName + "::field::" + fieldName;
            if (fieldCache.TryGetValue(key, out FieldInfo cached))
            {
                return cached;
            }

            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            FieldInfo field = type.GetField(fieldName, flags)
                ?? type.GetField("<" + fieldName + ">k__BackingField", flags);
            if (field == null)
            {
                RoleLogger.Warn("[Shared]", $"GetStaticFieldInfo: {type.FullName}.{fieldName} 未找到");
                throw new MissingFieldException(type.FullName, fieldName);
            }

            fieldCache[key] = field;
            return field;
        }

        /// <summary>
        /// Invoke a static method by name. Logs InnerException on TargetInvocationException.
        /// </summary>
        public static object InvokeStatic(Type type, string methodName, params object[] args)
        {
            args = args ?? Array.Empty<object>();
            Type[] argTypes = new Type[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                argTypes[i] = args[i] != null ? args[i].GetType() : typeof(object);
            }

            try
            {
                MethodInfo method = GetStaticMethod(type, methodName, argTypes);
                return method.Invoke(null, args);
            }
            catch (Exception ex)
            {
                // TargetInvocationException.Message 是无用的"Exception has been thrown by the target of an invocation."
                // 必须打 InnerException 才能看到真实错误
                string inner = ex.InnerException != null ? ex.InnerException.ToString() : ex.ToString();
                RoleLogger.Warn("[Shared]", $"InvokeStatic {type.Name}.{methodName} crash: {inner}");
                return null;
            }
        }

        public static MethodInfo GetStaticMethod(Type type, string methodName)
        {
            return GetStaticMethod(type, methodName, Type.EmptyTypes);
        }

        public static MethodInfo GetStaticMethod(Type type, string methodName, Type[] parameterTypes)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            parameterTypes = parameterTypes ?? Type.EmptyTypes;
            string key = type.FullName + "::method::" + methodName + "(" + parameterTypes.Length + ")";
            if (methodCache.TryGetValue(key, out MethodInfo cached))
            {
                return cached;
            }

            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            MethodInfo method = type.GetMethod(methodName, flags, null, parameterTypes, null);
            if (method == null && parameterTypes.Length == 0)
            {
                method = type.GetMethod(methodName, flags);
            }

            if (method == null)
            {
                // Fallback: match by name and parameter count (handles bool boxed as object, etc.)
                foreach (MethodInfo candidate in type.GetMethods(flags))
                {
                    if (candidate.Name != methodName) continue;
                    ParameterInfo[] parameters = candidate.GetParameters();
                    if (parameters.Length != parameterTypes.Length) continue;
                    method = candidate;
                    break;
                }
            }

            if (method == null)
            {
                RoleLogger.Warn("[Shared]", $"GetStaticMethod: {type.FullName}.{methodName} 未找到");
                throw new MissingMethodException(type.FullName, methodName);
            }

            methodCache[key] = method;
            return method;
        }

        public static void InvokeInstance(object instance, string methodName, params object[] args)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));

            Type type = instance.GetType();
            string key = type.FullName + "::instance::" + methodName;
            if (!methodCache.TryGetValue(key, out MethodInfo method))
            {
                method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (method == null)
                {
                    RoleLogger.Warn("[Shared]", $"InvokeInstance: {type.FullName}.{methodName} 未找到");
                    throw new MissingMethodException(type.FullName, methodName);
                }
                methodCache[key] = method;
            }

            method.Invoke(instance, args ?? Array.Empty<object>());
        }

        public static Type FindType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;

            Type type = Type.GetType(fullName, false);
            if (type != null) return type;

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    type = assembly.GetType(fullName, false);
                    if (type != null) return type;
                }
                catch
                {
                    // Ignore dynamic/reflection-only assemblies.
                }
            }

            return null;
        }

        public static MethodInfo FindStaticMethod(string typeFullName, string methodName)
        {
            Type type = FindType(typeFullName);
            if (type == null) return null;

            try { return GetStaticMethod(type, methodName); }
            catch (MissingMethodException) { return null; }
        }
    }
}
