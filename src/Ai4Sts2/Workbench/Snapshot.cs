using System.Diagnostics;
using System.Reflection;

namespace Ai4Sts2.Workbench;

public sealed class Snapshot
{
    private const BindingFlags Declared =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<Type, FieldInfo[]> _fieldCache = [];

    private readonly List<(object Target, FieldInfo[] Fields, object?[] Values)> _objects = [];
    private readonly List<(Array Target, Array Copy)> _arrays = [];

    public int Objects => _objects.Count;

    public int Arrays => _arrays.Count;

    public int Fields { get; private set; }

    public TimeSpan Elapsed { get; private set; }

    public static Snapshot Take(IEnumerable<object> roots)
    {
        var sw = Stopwatch.StartNew();
        var snap = new Snapshot();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<object>();
        foreach (var root in roots)
        {
            if (seen.Add(root))
            {
                stack.Push(root);
            }
        }
        while (stack.Count > 0)
        {
            var obj = stack.Pop();
            if (obj is Array array)
            {
                snap._arrays.Add((array, (Array)array.Clone()));
                if (!array.GetType().GetElementType()!.IsPrimitive)
                {
                    foreach (var item in array)
                    {
                        Consider(item, seen, stack);
                    }
                }
                continue;
            }
            var fields = FieldsOf(obj.GetType());
            var values = new object?[fields.Length];
            for (var i = 0; i < fields.Length; i++)
            {
                var value = fields[i].GetValue(obj);
                values[i] = value;
                Consider(value, seen, stack);
            }
            snap._objects.Add((obj, fields, values));
            snap.Fields += fields.Length;
        }
        snap.Elapsed = sw.Elapsed;
        return snap;
    }

    public TimeSpan Restore()
    {
        var sw = Stopwatch.StartNew();
        foreach (var (target, copy) in _arrays)
        {
            Array.Copy(copy, target, copy.Length);
        }
        foreach (var (target, fields, values) in _objects)
        {
            for (var i = 0; i < fields.Length; i++)
            {
                if (Skip(fields[i].FieldType))
                {
                    continue;
                }
                fields[i].SetValue(target, values[i]);
            }
        }
        return sw.Elapsed;
    }

    public static bool Skip(Type t)
    {
        var ns = t.Namespace ?? "";
        return typeof(Task).IsAssignableFrom(t)
            || typeof(CancellationTokenSource).IsAssignableFrom(t)
            || t == typeof(Type)
            || ns.StartsWith("System.Threading", StringComparison.Ordinal)
            || ns.StartsWith("System.Reflection", StringComparison.Ordinal)
            || ns.StartsWith("System.Runtime", StringComparison.Ordinal)
            || typeof(Godot.GodotObject).IsAssignableFrom(t);
    }

    private static void Consider(object? value, HashSet<object> seen, Stack<object> stack)
    {
        if (value is null)
        {
            return;
        }
        var t = value.GetType();
        if (t.IsPrimitive || t.IsEnum || value is string || value is Delegate || Skip(t))
        {
            return;
        }
        if (t.IsValueType)
        {
            foreach (var field in FieldsOf(t))
            {
                Consider(field.GetValue(value), seen, stack);
            }
            return;
        }
        if (seen.Add(value))
        {
            stack.Push(value);
        }
    }

    private static FieldInfo[] FieldsOf(Type type)
    {
        lock (_fieldCache)
        {
            if (_fieldCache.TryGetValue(type, out var cached))
            {
                return cached;
            }
            var list = new List<FieldInfo>();
            for (var t = type; t is not null && t != typeof(object) && t != typeof(ValueType); t = t.BaseType)
            {
                list.AddRange(t.GetFields(Declared));
            }
            var fields = list.ToArray();
            _fieldCache[type] = fields;
            return fields;
        }
    }
}
