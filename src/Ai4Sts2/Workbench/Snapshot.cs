using System.Diagnostics;
using MegaCrit.Sts2.Core.Models;

namespace Ai4Sts2.Workbench;

public sealed class Snapshot
{
    private readonly List<(object Target, Copier Copier, object?[] Values)> _objects = [];
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
                var element = array.GetType().GetElementType()!;
                if (element.IsPrimitive || element.IsEnum)
                {
                    continue;
                }
                foreach (var item in array)
                {
                    Consider(item, seen, stack);
                }
                continue;
            }
            var c = Copier.For(obj.GetType());
            var values = new object?[c.Fields.Length];
            c.Capture(obj, values);
            foreach (var value in values)
            {
                Consider(value, seen, stack);
            }
            snap._objects.Add((obj, c, values));
            snap.Fields += values.Length;
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
        foreach (var (target, copier, values) in _objects)
        {
            copier.Restore!(target, values);
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

    private static void Walk(object boxed, Copier copier, object?[] buffer, HashSet<object> seen, Stack<object> stack)
    {
        copier.Capture(boxed, buffer);
        foreach (var value in buffer)
        {
            Consider(value, seen, stack);
        }
    }

    private enum Shape
    {
        Leaf,
        Struct,
        Model,
        Reference,
    }

    private static readonly Dictionary<Type, (Shape Shape, Copier? Copier, object?[]? Buffer)> _shapes = [];

    private static (Shape Shape, Copier? Copier, object?[]? Buffer) ShapeOf(Type t)
    {
        if (_shapes.TryGetValue(t, out var shape))
        {
            return shape;
        }
        if (t.IsPrimitive || t.IsEnum || t == typeof(string) || typeof(Delegate).IsAssignableFrom(t) || Skip(t))
        {
            shape = (Shape.Leaf, null, null);
        }
        else if (t.IsValueType)
        {
            var copier = Copier.For(t);
            shape = (Shape.Struct, copier, new object?[copier.Fields.Length]);
        }
        else
        {
            shape = (typeof(AbstractModel).IsAssignableFrom(t) ? Shape.Model : Shape.Reference, null, null);
        }
        _shapes[t] = shape;
        return shape;
    }

    private static void Consider(object? value, HashSet<object> seen, Stack<object> stack)
    {
        if (value is null)
        {
            return;
        }
        var (shape, copier, buffer) = ShapeOf(value.GetType());
        if (shape == Shape.Leaf || (shape == Shape.Model && ((AbstractModel)value).IsCanonical))
        {
            return;
        }
        if (shape == Shape.Struct)
        {
            Walk(value, copier!, buffer!, seen, stack);
            return;
        }
        if (seen.Add(value))
        {
            stack.Push(value);
        }
    }
}
