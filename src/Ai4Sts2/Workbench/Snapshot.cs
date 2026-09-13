using System.Diagnostics;
using MegaCrit.Sts2.Core.Models;

namespace Ai4Sts2.Workbench;

public sealed class Snapshot
{
    private enum Shape
    {
        Leaf,
        Source,
        Model,
        Reference,
    }

    private static readonly Dictionary<Type, (Shape Shape, Layout? Layout)> _shapes = [];

    [ThreadStatic]
    private static HashSet<object>? _seen;

    [ThreadStatic]
    private static Stack<object>? _stack;

    [ThreadStatic]
    private static Stack<Snapshot>? _pool;

    private static readonly Dictionary<Type, Func<object, bool>> _completed = [];

    private readonly List<(object Target, Layout Layout, int DataPos, int RefPos)> _objects = new(1024);
    private readonly List<(Array Target, Array Copy)> _arrays = new(256);
    private readonly List<(object Source, bool WasCompleted)> _sources = [];
    private readonly SnapWriter _writer = new();
    private int _arrayCursor;

    public int Objects => _objects.Count;

    public int Arrays => _arrays.Count;

    public int Fields { get; private set; }

    public int Bytes => _writer.Pos;

    public TimeSpan Elapsed { get; private set; }

    public static Snapshot Take(IEnumerable<object> roots)
    {
        var sw = Stopwatch.StartNew();
        var pool = _pool ??= new Stack<Snapshot>();
        var snap = pool.Count > 0 ? pool.Pop() : new Snapshot();
        snap.Reset();
        var seen = _seen ??= new HashSet<object>(4096, ReferenceEqualityComparer.Instance);
        var stack = _stack ??= new Stack<object>(256);
        seen.Clear();
        stack.Clear();
        foreach (var root in roots)
        {
            if (seen.Add(root))
            {
                stack.Push(root);
            }
        }
        var w = snap._writer;
        while (stack.Count > 0)
        {
            var obj = stack.Pop();
            if (obj is Array array)
            {
                snap.Remember(array);
                var element = array.GetType().GetElementType()!;
                if (element.IsPrimitive || element.IsEnum)
                {
                    continue;
                }
                if (element.IsValueType)
                {
                    var layout = Layout.For(element);
                    if (layout.Refs == 0)
                    {
                        continue;
                    }
                    var start = w.Refs.Count;
                    layout.CaptureArray!(array, w);
                    snap.Discover(w, start, seen, stack);
                    w.Refs.RemoveRange(start, w.Refs.Count - start);
                    continue;
                }
                if (array is object?[] items)
                {
                    for (var i = 0; i < items.Length; i++)
                    {
                        snap.Consider(items[i], seen, stack);
                    }
                    continue;
                }
                foreach (var item in array)
                {
                    snap.Consider(item, seen, stack);
                }
                continue;
            }
            var l = ShapeOf(obj.GetType()).Layout ?? Layout.For(obj.GetType());
            var refStart = w.Refs.Count;
            snap._objects.Add((obj, l, w.Pos, refStart));
            l.Capture(obj, w);
            snap.Fields += l.Fields;
            snap.Discover(w, refStart, seen, stack);
        }
        snap._arrays.RemoveRange(snap._arrayCursor, snap._arrays.Count - snap._arrayCursor);
        snap.Elapsed = sw.Elapsed;
        return snap;
    }

    public void Release()
    {
        var pool = _pool ??= new Stack<Snapshot>();
        if (pool.Count < 4096)
        {
            pool.Push(this);
        }
    }

    public static bool IsSource(Type t) =>
        t == typeof(TaskCompletionSource)
        || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(TaskCompletionSource<>));

    private static bool IsCompleted(object source)
    {
        var type = source.GetType();
        if (!_completed.TryGetValue(type, out var probe))
        {
            var property = type.GetProperty("Task")!;
            probe = o => ((Task)property.GetValue(o)!).IsCompleted;
            _completed[type] = probe;
        }
        return probe(source);
    }

    private void Reset()
    {
        _sources.Clear();
        _objects.Clear();
        _writer.Pos = 0;
        _writer.Refs.Clear();
        _arrayCursor = 0;
        Fields = 0;
    }

    private void Remember(Array array)
    {
        var i = _arrayCursor++;
        if (i < _arrays.Count)
        {
            var copy = _arrays[i].Copy;
            if (copy.Length == array.Length && copy.GetType() == array.GetType())
            {
                Array.Copy(array, copy, array.Length);
                _arrays[i] = (array, copy);
                return;
            }
            _arrays[i] = (array, (Array)array.Clone());
            return;
        }
        _arrays.Add((array, (Array)array.Clone()));
    }

    public int Replaced { get; private set; }

    public TimeSpan Restore()
    {
        var sw = Stopwatch.StartNew();
        Dictionary<object, object>? remap = null;
        foreach (var (source, wasCompleted) in _sources)
        {
            if (!wasCompleted && IsCompleted(source))
            {
                remap ??= new Dictionary<object, object>(ReferenceEqualityComparer.Instance);
                remap[source] = Activator.CreateInstance(source.GetType())!;
            }
        }
        Replaced = remap?.Count ?? 0;
        foreach (var (target, copy) in _arrays)
        {
            Array.Copy(copy, target, copy.Length);
            if (remap is not null && target is object?[] items)
            {
                for (var i = 0; i < items.Length; i++)
                {
                    if (items[i] is { } item && remap.TryGetValue(item, out var replacement))
                    {
                        items[i] = replacement;
                    }
                }
            }
        }
        var reader = new SnapReader
        {
            Data = _writer.Data,
            Refs = _writer.Refs,
            Remap = remap,
        };
        foreach (var (target, layout, dataPos, refPos) in _objects)
        {
            reader.Pos = dataPos;
            reader.RefPos = refPos;
            layout.Restore(target, reader);
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

    private void Discover(SnapWriter w, int start, HashSet<object> seen, Stack<object> stack)
    {
        var refs = w.Refs;
        for (var i = start; i < refs.Count; i++)
        {
            Consider(refs[i], seen, stack);
        }
    }

    private static (Shape Shape, Layout? Layout) ShapeOf(Type t)
    {
        if (_shapes.TryGetValue(t, out var entry))
        {
            return entry;
        }
        var shape =
            IsSource(t) ? Shape.Source
            : t == typeof(string) || typeof(Delegate).IsAssignableFrom(t) || Skip(t) ? Shape.Leaf
            : typeof(AbstractModel).IsAssignableFrom(t) ? Shape.Model
            : Shape.Reference;
        entry = (shape, shape is Shape.Leaf or Shape.Source || t.IsArray ? null : Layout.For(t));
        _shapes[t] = entry;
        return entry;
    }

    private void Consider(object? value, HashSet<object> seen, Stack<object> stack)
    {
        if (value is null or string)
        {
            return;
        }
        var (shape, _) = ShapeOf(value.GetType());
        if (shape == Shape.Source)
        {
            if (seen.Add(value))
            {
                _sources.Add((value, IsCompleted(value)));
            }
            return;
        }
        if (shape == Shape.Leaf || (shape == Shape.Model && ((AbstractModel)value).IsCanonical))
        {
            return;
        }
        if (seen.Add(value))
        {
            stack.Push(value);
        }
    }
}
