using System.Reflection;
using System.Reflection.Emit;

namespace Ai4Sts2.Workbench;

public sealed class Copier
{
    private const BindingFlags Declared =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<Type, Copier> _cache = [];

    public Type Type { get; }

    public FieldInfo[] Fields { get; }

    public Action<object, object?[]> Capture { get; }

    public Action<object, object?[]>? Restore { get; }

    public bool Opaque { get; }

    private Copier(Type type)
    {
        Type = type;
        var list = new List<FieldInfo>();
        for (var t = type; t is not null && t != typeof(object) && t != typeof(ValueType); t = t.BaseType)
        {
            list.AddRange(
                t.GetFields(Declared)
                    .Where(f => !Snapshot.Skip(f.FieldType) && !f.FieldType.IsPointer && !f.FieldType.IsByRefLike)
            );
        }
        Fields = list.ToArray();
        Opaque = Fields.Any(f => f.FieldType == typeof(object) || f.FieldType.IsInterface);
        Capture = EmitCapture(type, Fields);
        Restore = type.IsValueType ? null : EmitRestore(type, Fields);
    }

    public static Copier For(Type type)
    {
        lock (_cache)
        {
            if (!_cache.TryGetValue(type, out var copier))
            {
                copier = new Copier(type);
                _cache[type] = copier;
            }
            return copier;
        }
    }

    private static Action<object, object?[]> EmitCapture(Type type, FieldInfo[] fields)
    {
        var method = new DynamicMethod(
            "capture_" + type.Name,
            typeof(void),
            [typeof(object), typeof(object[])],
            typeof(Copier).Module,
            true
        );
        var il = method.GetILGenerator();
        for (var i = 0; i < fields.Length; i++)
        {
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldc_I4, i);
            il.Emit(OpCodes.Ldarg_0);
            if (type.IsValueType)
            {
                il.Emit(OpCodes.Unbox_Any, type);
            }
            else
            {
                il.Emit(OpCodes.Castclass, type);
            }
            il.Emit(OpCodes.Ldfld, fields[i]);
            if (fields[i].FieldType.IsValueType)
            {
                il.Emit(OpCodes.Box, fields[i].FieldType);
            }
            il.Emit(OpCodes.Stelem_Ref);
        }
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate<Action<object, object?[]>>();
    }

    private static Action<object, object?[]> EmitRestore(Type type, FieldInfo[] fields)
    {
        var method = new DynamicMethod(
            "restore_" + type.Name,
            typeof(void),
            [typeof(object), typeof(object[])],
            typeof(Copier).Module,
            true
        );
        var il = method.GetILGenerator();
        for (var i = 0; i < fields.Length; i++)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, type);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldc_I4, i);
            il.Emit(OpCodes.Ldelem_Ref);
            if (fields[i].FieldType.IsValueType)
            {
                il.Emit(OpCodes.Unbox_Any, fields[i].FieldType);
            }
            else
            {
                il.Emit(OpCodes.Castclass, fields[i].FieldType);
            }
            il.Emit(OpCodes.Stfld, fields[i]);
        }
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate<Action<object, object?[]>>();
    }
}
