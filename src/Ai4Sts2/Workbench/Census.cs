using System.Collections;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Runs;

namespace Ai4Sts2.Workbench;

public sealed record CensusType(string Name, int Instances, int Fields, int Bytes, string Category);

public sealed record CensusEdge(string Type, string Field, string Target, int Count);

public sealed record CensusStatic(string Type, string Field, string ValueType, int? Count);

public sealed record CensusReport(
    int Objects,
    int Types,
    int Fields,
    int Bytes,
    IReadOnlyList<CensusType> ByType,
    IReadOnlyList<CensusEdge> Engine,
    IReadOnlyList<CensusEdge> Runtime,
    IReadOnlyList<CensusEdge> Delegates,
    IReadOnlyList<CensusStatic> Statics,
    IReadOnlyList<string> Roots
);

public static class Census
{
    private const BindingFlags Declared =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private static readonly Assembly _game = typeof(CombatManager).Assembly;

    public static CensusReport Run()
    {
        var roots = new Dictionary<string, object?>
        {
            ["CombatManager.Instance"] = CombatManager.Instance,
            ["RunManager.Instance"] = RunManager.Instance,
        };
        var walker = new Walker();
        foreach (var (name, root) in roots)
        {
            if (root is not null)
            {
                walker.Push(root, name);
            }
        }
        walker.Drain();
        var byType = walker
            .Types.Values.Select(t => new CensusType(t.Name, t.Instances, t.FieldCount, t.Bytes, Category(t.Type)))
            .OrderByDescending(t => t.Instances)
            .ToList();
        return new CensusReport(
            walker.Objects,
            walker.Types.Count,
            walker.Types.Values.Sum(t => t.FieldCount * t.Instances),
            walker.Types.Values.Sum(t => t.Bytes * t.Instances),
            byType,
            Edges(walker.Engine),
            Edges(walker.Runtime),
            Edges(walker.Delegates),
            Statics(),
            roots.Keys.ToList()
        );
    }

    private static List<CensusEdge> Edges(Dictionary<(string, string, string), int> map) =>
        map.Select(kv => new CensusEdge(kv.Key.Item1, kv.Key.Item2, kv.Key.Item3, kv.Value))
            .OrderByDescending(e => e.Count)
            .ToList();

    private static List<CensusStatic> Statics()
    {
        var list = new List<CensusStatic>();
        foreach (var type in _game.GetTypes())
        {
            if (
                type.IsEnum
                || type.IsGenericTypeDefinition
                || !(type.Namespace ?? "").StartsWith("MegaCrit.Sts2.Core", StringComparison.Ordinal)
            )
            {
                continue;
            }
            FieldInfo[] fields;
            try
            {
                fields = type.GetFields(
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly
                );
            }
            catch (TypeLoadException)
            {
                continue;
            }
            foreach (var field in fields)
            {
                if (
                    field.IsLiteral
                    || field.FieldType.IsPrimitive
                    || field.FieldType.IsEnum
                    || field.FieldType == typeof(string)
                )
                {
                    continue;
                }
                object? value;
                try
                {
                    value = field.GetValue(null);
                }
                catch (Exception)
                {
                    continue;
                }
                if (value is null or Delegate or Type)
                {
                    continue;
                }
                var valueType = value.GetType();
                if (
                    IsEngine(valueType)
                    || valueType.Namespace?.StartsWith("System.Reflection", StringComparison.Ordinal) == true
                )
                {
                    continue;
                }
                int? count = value is ICollection c ? c.Count : null;
                if (valueType.IsValueType && count is null)
                {
                    continue;
                }
                list.Add(new CensusStatic(type.FullName ?? type.Name, field.Name, Short(valueType), count));
            }
        }
        return list.OrderBy(s => s.Type).ThenBy(s => s.Field).ToList();
    }

    private static bool IsEngine(Type t) =>
        typeof(Godot.GodotObject).IsAssignableFrom(t)
        || (t.Namespace ?? "").StartsWith("Godot", StringComparison.Ordinal);

    private static bool IsRuntime(Type t)
    {
        var ns = t.Namespace ?? "";
        return typeof(Task).IsAssignableFrom(t)
            || typeof(CancellationTokenSource).IsAssignableFrom(t)
            || ns.StartsWith("System.Threading", StringComparison.Ordinal)
            || ns.StartsWith("System.Reflection", StringComparison.Ordinal)
            || t == typeof(Type)
            || ns.StartsWith("System.Runtime", StringComparison.Ordinal);
    }

    private static string Category(Type t)
    {
        return t.Assembly == _game ? "game"
            : IsEngine(t) ? "engine"
            : t.Namespace?.StartsWith("System", StringComparison.Ordinal) == true ? "bcl"
            : "mod";
    }

    private static string Short(Type t)
    {
        if (!t.IsGenericType)
        {
            return t.FullName ?? t.Name;
        }
        var name = t.GetGenericTypeDefinition().FullName ?? t.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        return (tick < 0 ? name : name[..tick]) + "<" + string.Join(",", t.GetGenericArguments().Select(Short)) + ">";
    }

    private sealed class TypeStat(Type type)
    {
        public Type Type { get; } = type;

        public string Name { get; } = Short(type);

        public int Instances { get; set; }

        public int FieldCount { get; set; }

        public int Bytes { get; set; }
    }

    private sealed class Walker
    {
        private readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);
        private readonly Stack<(object Obj, string Path)> _stack = new();
        private readonly Dictionary<Type, List<FieldInfo>> _fieldCache = [];

        public Dictionary<Type, TypeStat> Types { get; } = [];

        public Dictionary<(string, string, string), int> Engine { get; } = [];

        public Dictionary<(string, string, string), int> Runtime { get; } = [];

        public Dictionary<(string, string, string), int> Delegates { get; } = [];

        public int Objects { get; private set; }

        public void Push(object obj, string path)
        {
            if (_seen.Add(obj))
            {
                _stack.Push((obj, path));
            }
        }

        public void Drain()
        {
            while (_stack.Count > 0)
            {
                var (obj, path) = _stack.Pop();
                Visit(obj, path);
            }
        }

        private void Visit(object obj, string path)
        {
            var type = obj.GetType();
            if (
                obj is string
                || type.IsPrimitive
                || type.IsEnum
                || IsEngine(type)
                || IsRuntime(type)
                || obj is Delegate
            )
            {
                return;
            }
            Objects++;
            if (!Types.TryGetValue(type, out var stat))
            {
                stat = new TypeStat(type);
                var fields = FieldsOf(type);
                stat.FieldCount = fields.Count;
                stat.Bytes = fields.Sum(f => SizeOf(f.FieldType));
                Types[type] = stat;
            }
            stat.Instances++;
            if (obj is Array array)
            {
                if (!type.GetElementType()!.IsValueType || !type.GetElementType()!.IsPrimitive)
                {
                    foreach (var item in array)
                    {
                        Consider(item, type, "[]", path);
                    }
                }
                return;
            }
            if (obj is IDictionary dict)
            {
                foreach (DictionaryEntry entry in dict)
                {
                    Consider(entry.Key, type, "[key]", path);
                    Consider(entry.Value, type, "[value]", path);
                }
                return;
            }
            if (
                obj is IEnumerable enumerable
                && type.Namespace?.StartsWith("System.Collections", StringComparison.Ordinal) == true
            )
            {
                foreach (var item in enumerable)
                {
                    Consider(item, type, "[]", path);
                }
                return;
            }
            VisitFields(obj, type, path);
        }

        private void VisitFields(object obj, Type type, string path)
        {
            foreach (var field in FieldsOf(type))
            {
                object? value;
                try
                {
                    value = field.GetValue(obj);
                }
                catch (Exception)
                {
                    continue;
                }
                Consider(value, type, field.Name, path);
            }
        }

        private void Consider(object? value, Type owner, string field, string path)
        {
            if (value is null)
            {
                return;
            }
            var vt = value.GetType();
            if (vt.IsPrimitive || vt.IsEnum || value is string)
            {
                return;
            }
            if (value is Delegate d)
            {
                Bump(Delegates, owner, field, d.Target is null ? "static" : Short(d.Target.GetType()));
                return;
            }
            if (IsEngine(vt))
            {
                Bump(Engine, owner, field, Short(vt));
                return;
            }
            if (IsRuntime(vt))
            {
                Bump(Runtime, owner, field, Short(vt));
                return;
            }
            if (vt.IsValueType)
            {
                VisitFields(value, vt, path + "." + field);
                return;
            }
            Push(value, path + "." + field);
        }

        private static void Bump(Dictionary<(string, string, string), int> map, Type owner, string field, string target)
        {
            var key = (Short(owner), field, target);
            map[key] = map.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        private List<FieldInfo> FieldsOf(Type type)
        {
            if (_fieldCache.TryGetValue(type, out var cached))
            {
                return cached;
            }
            var list = new List<FieldInfo>();
            for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
            {
                list.AddRange(t.GetFields(Declared));
            }
            _fieldCache[type] = list;
            return list;
        }

        private static int SizeOf(Type t)
        {
            if (!t.IsValueType)
            {
                return 8;
            }
            if (t.IsEnum)
            {
                return 4;
            }
            if (t.IsPrimitive)
            {
                return t == typeof(bool) || t == typeof(byte) || t == typeof(sbyte) ? 1
                    : t == typeof(short) || t == typeof(ushort) || t == typeof(char) ? 2
                    : t == typeof(int) || t == typeof(uint) || t == typeof(float) ? 4
                    : 8;
            }
            if (t == typeof(decimal))
            {
                return 16;
            }
            try
            {
                return t.GetFields(Declared).Sum(f => SizeOf(f.FieldType));
            }
            catch (TypeLoadException)
            {
                return 8;
            }
        }
    }
}
