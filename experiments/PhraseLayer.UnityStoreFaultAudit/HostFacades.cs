using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.Json;

// Test facades, not Unity runtime substitutes. Only synthetic valid snapshot DTOs are exercised.
namespace UnityEngine
{
    public static class Application
    {
        public static string persistentDataPath => throw new InvalidOperationException("Host audit requires an explicit temporary path.");
    }
    public static class JsonUtility
    {
        public static string ToJson(object value, bool prettyPrint) => JsonSerializer.Serialize(value, value.GetType(),
            new JsonSerializerOptions { IncludeFields = true, WriteIndented = prettyPrint });
        public static T FromJson<T>(string json)
        {
            using var document = JsonDocument.Parse(json);
            return (T)Read(typeof(T), document.RootElement);
        }
        private static object Read(Type type, JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Null) return null;
            if (type == typeof(string)) return value.GetString();
            if (type == typeof(double)) return value.GetDouble();
            if (type == typeof(int)) return value.GetInt32();
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                var list = (IList)Activator.CreateInstance(type);
                foreach (var item in value.EnumerateArray()) list.Add(Read(type.GetGenericArguments()[0], item));
                return list;
            }
            var result = Activator.CreateInstance(type, nonPublic: true);
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
                if (value.TryGetProperty(field.Name, out var item)) field.SetValue(result, Read(field.FieldType, item));
            return result;
        }
    }
}
namespace PhraseLayer.Unity
{
    internal sealed class InjectedFileFailure : Exception { }
    // The linked production source resolves unqualified File calls to this namespace-local test facade.
    // Each nonfaulting call delegates to the real host filesystem. No production source rewrite is performed.
    internal static class File
    {
        private static bool armed;
        private static int? faultPoint;
        private static string timing;
        private static int mutationCount;
        internal static List<string> Trace { get; } = new List<string>();
        internal static bool FaultInjected { get; private set; }
        internal static void Arm(int? point, string phase)
        { armed = true; faultPoint = point; timing = phase; mutationCount = 0; FaultInjected = false; Trace.Clear(); }
        internal static void Disarm() { armed = false; }
        public static bool Exists(string path) => System.IO.File.Exists(path);
        public static string ReadAllText(string path, Encoding encoding) => System.IO.File.ReadAllText(path, encoding);
        public static void WriteAllText(string path, string text, Encoding encoding) =>
            Mutate("write-temp", () => System.IO.File.WriteAllText(path, text, encoding));
        public static void Delete(string path) => Mutate("delete-backup", () => System.IO.File.Delete(path));
        public static void Move(string source, string destination) => Mutate(
            source.EndsWith(".tmp", StringComparison.Ordinal) ? "promote-temp" : source.EndsWith(".bak", StringComparison.Ordinal) ? "restore-backup" : "primary-to-backup",
            () => System.IO.File.Move(source, destination));
        private static void Mutate(string operation, Action action)
        {
            if (!armed) { action(); return; }
            mutationCount++;
            Trace.Add(operation);
            var inject = !FaultInjected && faultPoint == mutationCount;
            if (inject && timing == "before") { FaultInjected = true; throw new InjectedFileFailure(); }
            action();
            if (inject && timing == "after") { FaultInjected = true; throw new InjectedFileFailure(); }
        }
    }
}
