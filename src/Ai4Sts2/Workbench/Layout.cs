using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Ai4Sts2.Workbench;

public sealed class SnapWriter
{
    public byte[] Data { get; private set; } = new byte[1 << 16];

    public int Pos { get; set; }

    public List<object?> Refs { get; } = new(4096);

    public void Reserve(int bytes)
    {
        if (Pos + bytes > Data.Length)
        {
            var grown = new byte[Math.Max(Data.Length * 2, Pos + bytes)];
            Array.Copy(Data, grown, Pos);
            Data = grown;
        }
    }
}

public sealed class SnapReader
{
    public byte[] Data { get; init; } = [];

    public int Pos { get; set; }

    public List<object?> Refs { get; init; } = [];

    public int RefPos { get; set; }

    public Dictionary<object, object>? Remap { get; init; }
}

public static class SnapIo
{
    public static void W1(SnapWriter w, byte v)
    {
        w.Reserve(1);
        w.Data[w.Pos++] = v;
    }

    public static void W2(SnapWriter w, short v)
    {
        w.Reserve(2);
        Unsafe.WriteUnaligned(ref w.Data[w.Pos], v);
        w.Pos += 2;
    }

    public static void W4(SnapWriter w, int v)
    {
        w.Reserve(4);
        Unsafe.WriteUnaligned(ref w.Data[w.Pos], v);
        w.Pos += 4;
    }

    public static void W8(SnapWriter w, long v)
    {
        w.Reserve(8);
        Unsafe.WriteUnaligned(ref w.Data[w.Pos], v);
        w.Pos += 8;
    }

    public static void WF(SnapWriter w, float v)
    {
        w.Reserve(4);
        Unsafe.WriteUnaligned(ref w.Data[w.Pos], v);
        w.Pos += 4;
    }

    public static void WD(SnapWriter w, double v)
    {
        w.Reserve(8);
        Unsafe.WriteUnaligned(ref w.Data[w.Pos], v);
        w.Pos += 8;
    }

    public static void WM(SnapWriter w, decimal v)
    {
        w.Reserve(16);
        Unsafe.WriteUnaligned(ref w.Data[w.Pos], v);
        w.Pos += 16;
    }

    public static void WR(SnapWriter w, object? v) => w.Refs.Add(v);

    public static byte R1(SnapReader r) => r.Data[r.Pos++];

    public static short R2(SnapReader r)
    {
        var v = Unsafe.ReadUnaligned<short>(ref r.Data[r.Pos]);
        r.Pos += 2;
        return v;
    }

    public static int R4(SnapReader r)
    {
        var v = Unsafe.ReadUnaligned<int>(ref r.Data[r.Pos]);
        r.Pos += 4;
        return v;
    }

    public static long R8(SnapReader r)
    {
        var v = Unsafe.ReadUnaligned<long>(ref r.Data[r.Pos]);
        r.Pos += 8;
        return v;
    }

    public static float RF(SnapReader r)
    {
        var v = Unsafe.ReadUnaligned<float>(ref r.Data[r.Pos]);
        r.Pos += 4;
        return v;
    }

    public static double RD(SnapReader r)
    {
        var v = Unsafe.ReadUnaligned<double>(ref r.Data[r.Pos]);
        r.Pos += 8;
        return v;
    }

    public static decimal RM(SnapReader r)
    {
        var v = Unsafe.ReadUnaligned<decimal>(ref r.Data[r.Pos]);
        r.Pos += 16;
        return v;
    }

    public static object? RR(SnapReader r)
    {
        var value = r.Refs[r.RefPos++];
        return value is not null && r.Remap is { } remap && remap.TryGetValue(value, out var replacement)
            ? replacement
            : value;
    }
}

public sealed class Layout
{
    private const BindingFlags Declared =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<Type, Layout> _cache = [];
    private static readonly Dictionary<Type, (DynamicMethod Capture, DynamicMethod Restore)> _structs = [];

    public Type Type { get; }

    public int Fields { get; }

    public int Bytes { get; }

    public int Refs { get; }

    public Action<object, SnapWriter> Capture { get; }

    public Action<object, SnapReader> Restore { get; }

    public Action<Array, SnapWriter>? CaptureArray { get; }

    private Layout(Type type)
    {
        Type = type;
        var fields = FieldsOf(type);
        Fields = fields.Length;
        var (bytes, refs) = Measure(type);
        Bytes = bytes;
        Refs = refs;
        if (type.IsValueType)
        {
            Capture = EmitBoxed(type).CreateDelegate<Action<object, SnapWriter>>();
            Restore = static (_, _) => { };
            CaptureArray = EmitArray(type).CreateDelegate<Action<Array, SnapWriter>>();
        }
        else
        {
            Capture = EmitObject(type, fields, true).CreateDelegate<Action<object, SnapWriter>>();
            Restore = EmitObject(type, fields, false).CreateDelegate<Action<object, SnapReader>>();
        }
    }

    private delegate void StructIo<T, TIo>(ref T value, TIo io);

    private static DynamicMethod EmitArray(Type type)
    {
        var arrayType = type.MakeArrayType();
        var method = new DynamicMethod(
            "capa_" + type.Name,
            typeof(void),
            [typeof(Array), typeof(SnapWriter)],
            typeof(Layout).Module,
            true
        );
        var il = method.GetILGenerator();
        var arr = il.DeclareLocal(arrayType);
        var i = il.DeclareLocal(typeof(int));
        var loop = il.DefineLabel();
        var check = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, arrayType);
        il.Emit(OpCodes.Stloc, arr);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, i);
        il.Emit(OpCodes.Br, check);
        il.MarkLabel(loop);
        il.Emit(OpCodes.Ldloc, arr);
        il.Emit(OpCodes.Ldloc, i);
        il.Emit(OpCodes.Ldelema, type);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, StructMethods(type).Capture);
        il.Emit(OpCodes.Ldloc, i);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, i);
        il.MarkLabel(check);
        il.Emit(OpCodes.Ldloc, i);
        il.Emit(OpCodes.Ldloc, arr);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Blt, loop);
        il.Emit(OpCodes.Ret);
        return method;
    }

    private static DynamicMethod EmitBoxed(Type type)
    {
        var method = new DynamicMethod(
            "capb_" + type.Name,
            typeof(void),
            [typeof(object), typeof(SnapWriter)],
            typeof(Layout).Module,
            true
        );
        var il = method.GetILGenerator();
        var local = il.DeclareLocal(type);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Unbox_Any, type);
        il.Emit(OpCodes.Stloc, local);
        il.Emit(OpCodes.Ldloca, local);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, StructMethods(type).Capture);
        il.Emit(OpCodes.Ret);
        return method;
    }

    public static Layout For(Type type)
    {
        if (!_cache.TryGetValue(type, out var layout))
        {
            layout = new Layout(type);
            _cache[type] = layout;
        }
        return layout;
    }

    private static FieldInfo[] FieldsOf(Type type)
    {
        var list = new List<FieldInfo>();
        for (var t = type; t is not null && t != typeof(object) && t != typeof(ValueType); t = t.BaseType)
        {
            list.AddRange(
                t.GetFields(Declared)
                    .Where(f =>
                        (Snapshot.IsSource(f.FieldType) || !Snapshot.Skip(f.FieldType))
                        && !f.FieldType.IsPointer
                        && !f.FieldType.IsByRefLike
                    )
            );
        }
        return list.ToArray();
    }

    private static (int Bytes, int Refs) Measure(Type type)
    {
        var bytes = 0;
        var refs = 0;
        foreach (var f in FieldsOf(type))
        {
            var ft = f.FieldType;
            if (!ft.IsValueType)
            {
                refs++;
            }
            else if (ft.IsPrimitive || ft.IsEnum || ft == typeof(decimal))
            {
                bytes += PrimitiveSize(ft.IsEnum ? Enum.GetUnderlyingType(ft) : ft);
            }
            else
            {
                var (b, r) = Measure(ft);
                bytes += b;
                refs += r;
            }
        }
        return (bytes, refs);
    }

    private static int PrimitiveSize(Type t) =>
        t == typeof(decimal) ? 16
        : t == typeof(bool) || t == typeof(byte) || t == typeof(sbyte) ? 1
        : t == typeof(short) || t == typeof(ushort) || t == typeof(char) ? 2
        : t == typeof(int) || t == typeof(uint) || t == typeof(float) ? 4
        : 8;

    private static DynamicMethod EmitObject(Type type, FieldInfo[] fields, bool capture)
    {
        var io = capture ? typeof(SnapWriter) : typeof(SnapReader);
        var method = new DynamicMethod(
            (capture ? "cap_" : "res_") + type.Name,
            typeof(void),
            [typeof(object), io],
            typeof(Layout).Module,
            true
        );
        var il = method.GetILGenerator();
        var self = il.DeclareLocal(type);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, type);
        il.Emit(OpCodes.Stloc, self);
        foreach (var field in fields)
        {
            EmitField(il, field, capture, () => il.Emit(OpCodes.Ldloc, self));
        }
        il.Emit(OpCodes.Ret);
        return method;
    }

    private static (DynamicMethod Capture, DynamicMethod Restore) StructMethods(Type type)
    {
        if (_structs.TryGetValue(type, out var pair))
        {
            return pair;
        }
        var fields = FieldsOf(type);
        var cap = new DynamicMethod(
            "caps_" + type.Name,
            typeof(void),
            [type.MakeByRefType(), typeof(SnapWriter)],
            typeof(Layout).Module,
            true
        );
        var res = new DynamicMethod(
            "ress_" + type.Name,
            typeof(void),
            [type.MakeByRefType(), typeof(SnapReader)],
            typeof(Layout).Module,
            true
        );
        pair = (cap, res);
        _structs[type] = pair;
        foreach (var (method, capture) in new[] { (cap, true), (res, false) })
        {
            var il = method.GetILGenerator();
            foreach (var field in fields)
            {
                EmitField(il, field, capture, () => il.Emit(OpCodes.Ldarg_0));
            }
            il.Emit(OpCodes.Ret);
        }
        _ = cap.CreateDelegate(typeof(StructIo<,>).MakeGenericType(type, typeof(SnapWriter)));
        _ = res.CreateDelegate(typeof(StructIo<,>).MakeGenericType(type, typeof(SnapReader)));
        return pair;
    }

    private static void EmitField(ILGenerator il, FieldInfo field, bool capture, Action loadOwner)
    {
        var ft = field.FieldType;
        var prim = ft.IsEnum ? Enum.GetUnderlyingType(ft) : ft;
        if (!ft.IsValueType)
        {
            if (capture)
            {
                il.Emit(OpCodes.Ldarg_1);
                loadOwner();
                il.Emit(OpCodes.Ldfld, field);
                il.Emit(OpCodes.Call, typeof(SnapIo).GetMethod(nameof(SnapIo.WR))!);
            }
            else
            {
                loadOwner();
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Call, typeof(SnapIo).GetMethod(nameof(SnapIo.RR))!);
                il.Emit(OpCodes.Castclass, ft);
                il.Emit(OpCodes.Stfld, field);
            }
            return;
        }
        if (prim.IsPrimitive || prim == typeof(decimal))
        {
            var (w, r) = IoMethods(prim);
            if (capture)
            {
                il.Emit(OpCodes.Ldarg_1);
                loadOwner();
                il.Emit(OpCodes.Ldfld, field);
                Widen(il, prim);
                il.Emit(OpCodes.Call, w);
            }
            else
            {
                loadOwner();
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Call, r);
                Narrow(il, prim);
                il.Emit(OpCodes.Stfld, field);
            }
            return;
        }
        var (cap, res) = StructMethods(ft);
        loadOwner();
        il.Emit(OpCodes.Ldflda, field);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, capture ? cap : res);
    }

    private static void Widen(ILGenerator il, Type prim)
    {
        if (prim == typeof(bool) || prim == typeof(sbyte))
        {
            il.Emit(OpCodes.Conv_U1);
        }
        else if (prim == typeof(ushort) || prim == typeof(char))
        {
            il.Emit(OpCodes.Conv_I2);
        }
        else if (prim == typeof(uint))
        {
            il.Emit(OpCodes.Conv_I4);
        }
        else if (prim == typeof(ulong) || prim == typeof(nint) || prim == typeof(nuint))
        {
            il.Emit(OpCodes.Conv_I8);
        }
    }

    private static void Narrow(ILGenerator il, Type prim)
    {
        if (prim == typeof(sbyte))
        {
            il.Emit(OpCodes.Conv_I1);
        }
        else if (prim == typeof(ushort) || prim == typeof(char))
        {
            il.Emit(OpCodes.Conv_U2);
        }
        else if (prim == typeof(uint))
        {
            il.Emit(OpCodes.Conv_U4);
        }
        else if (prim == typeof(ulong))
        {
            il.Emit(OpCodes.Conv_U8);
        }
        else if (prim == typeof(nint))
        {
            il.Emit(OpCodes.Conv_I);
        }
        else if (prim == typeof(nuint))
        {
            il.Emit(OpCodes.Conv_U);
        }
    }

    private static (MethodInfo Write, MethodInfo Read) IoMethods(Type prim)
    {
        var (w, r) = PrimitiveSize(prim) switch
        {
            1 => (nameof(SnapIo.W1), nameof(SnapIo.R1)),
            2 => (nameof(SnapIo.W2), nameof(SnapIo.R2)),
            4 => prim == typeof(float)
                ? (nameof(SnapIo.WF), nameof(SnapIo.RF))
                : (nameof(SnapIo.W4), nameof(SnapIo.R4)),
            16 => (nameof(SnapIo.WM), nameof(SnapIo.RM)),
            _ => prim == typeof(double)
                ? (nameof(SnapIo.WD), nameof(SnapIo.RD))
                : (nameof(SnapIo.W8), nameof(SnapIo.R8)),
        };
        return (typeof(SnapIo).GetMethod(w)!, typeof(SnapIo).GetMethod(r)!);
    }
}
