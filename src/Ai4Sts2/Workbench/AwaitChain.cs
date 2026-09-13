using System.Reflection;
using System.Runtime.CompilerServices;

namespace Ai4Sts2.Workbench;

public static class AwaitChain
{
    private const BindingFlags Any =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static List<string> Describe(Task? task, int limit = 40)
    {
        var lines = new List<string>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var current = task;
        while (current is not null && lines.Count < limit && seen.Add(current))
        {
            lines.Add($"{current.GetType().Name} status={current.Status} id={current.Id}");
            if (current.Exception?.InnerException is { } fault)
            {
                var text = fault.ToString().ReplaceLineEndings(" ");
                lines.Add("  fault: " + text[..Math.Min(1500, text.Length)]);
            }
            var machine = StateMachineOf(current);
            if (machine is null)
            {
                break;
            }
            var type = machine.GetType();
            var state = type.GetField("<>1__state", Any)?.GetValue(machine);
            lines.Add($"  in {Pretty(type)} state={state}");
            Task? next = null;
            foreach (var field in type.GetFields(Any))
            {
                if (!field.Name.StartsWith("<>u__", StringComparison.Ordinal))
                {
                    continue;
                }
                var awaiter = field.GetValue(machine);
                if (awaiter is null)
                {
                    continue;
                }
                var inner = AwaitedTask(awaiter);
                if (inner is not null && !inner.IsCompleted)
                {
                    lines.Add(
                        $"  awaiting {field.Name}: {awaiter.GetType().Name} -> {inner.GetType().Name} status={inner.Status}"
                    );
                    next = inner;
                }
            }
            current = next;
        }
        return lines;
    }

    private static object? StateMachineOf(Task task)
    {
        var field = typeof(Task).GetField("m_stateObject", Any);
        var boxType = task.GetType();
        if (boxType.IsGenericType && boxType.Name.StartsWith("AsyncStateMachineBox", StringComparison.Ordinal))
        {
            return boxType.GetField("StateMachine", Any)?.GetValue(task);
        }
        var stateObject = field?.GetValue(task);
        return stateObject is IAsyncStateMachine machine ? machine : null;
    }

    private static Task? AwaitedTask(object awaiter)
    {
        var type = awaiter.GetType();
        var field = type.GetField("m_task", Any) ?? type.GetField("_task", Any);
        if (field?.GetValue(awaiter) is Task task)
        {
            return task;
        }
        foreach (var f in type.GetFields(Any))
        {
            if (f.GetValue(awaiter) is Task nested)
            {
                return nested;
            }
        }
        return null;
    }

    private static string Pretty(Type type)
    {
        var name = type.FullName ?? type.Name;
        return name.Replace("MegaCrit.Sts2.Core.", "", StringComparison.Ordinal);
    }
}
