using System.Reflection;
using JoePro.Core;

namespace JoePro.Runtime;

/// <summary>
/// A late-bound external object: a COM automation server (Windows: CREATEOBJECT("Excel.Application"))
/// or a .NET object (Joe Pro extension: CREATEOBJECT("net:System.Text.StringBuilder")).
/// Properties and methods are resolved by name at run time, like IDispatch.
/// </summary>
public sealed class ClrObjectProxy
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

    public ClrObjectProxy(object target) => Target = target;

    public object Target { get; }
    public bool IsCom => Target.GetType().IsCOMObject;
    public override string ToString() => $"({Target.GetType().Name})";

    public static ClrObjectProxy Create(string className)
    {
        if (className.StartsWith("net:", StringComparison.OrdinalIgnoreCase))
        {
            var typeName = className[4..];
            var type = Type.GetType(typeName, throwOnError: false, ignoreCase: true)
                       ?? AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(typeName, false, true)).FirstOrDefault(t => t != null)
                       ?? throw new VfpException(1733, $"Class definition {className.ToUpperInvariant()} is not found.", className);
            return new ClrObjectProxy(Activator.CreateInstance(type) ?? throw VfpException.InvalidArgument());
        }
        if (!OperatingSystem.IsWindows())
            throw new VfpException(1733, $"Class definition {className.ToUpperInvariant()} is not found. COM automation is available on Windows only.", className);
        var com = Type.GetTypeFromProgID(className, throwOnError: false)
                  ?? throw new VfpException(1733, $"Class definition {className.ToUpperInvariant()} is not found.", className);
        try { return new ClrObjectProxy(Activator.CreateInstance(com)!); }
        catch (Exception ex) { throw new VfpException(1426, $"OLE error: {ex.Message}"); }
    }

    public Value Get(string name)
    {
        try
        {
            if (IsCom) return ToValue(Target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, Target, null));
            var p = Target.GetType().GetProperty(name, Flags);
            if (p != null) return ToValue(p.GetValue(Target));
            var f = Target.GetType().GetField(name, Flags);
            if (f != null) return ToValue(f.GetValue(Target));
        }
        catch (TargetInvocationException ex) { throw OleError(ex.InnerException ?? ex); }
        catch (Exception ex) when (ex is MissingMemberException or System.Runtime.InteropServices.COMException) { throw OleError(ex); }
        throw VfpObject.PropertyNotFound(name);
    }

    public void Set(string name, Value value)
    {
        try
        {
            if (IsCom)
            {
                Target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, Target, [ToObject(value, null)]);
                return;
            }
            var p = Target.GetType().GetProperty(name, Flags) ?? throw VfpObject.PropertyNotFound(name);
            p.SetValue(Target, ToObject(value, p.PropertyType));
        }
        catch (TargetInvocationException ex) { throw OleError(ex.InnerException ?? ex); }
    }

    public Value Call(string name, IReadOnlyList<Value> args)
    {
        try
        {
            if (IsCom)
                return ToValue(Target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, Target, args.Select(a => ToObject(a, null)).ToArray()));
            foreach (var m in Target.GetType().GetMethods(Flags).Where(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && m.GetParameters().Length == args.Count))
            {
                var ps = m.GetParameters();
                var converted = new object?[args.Count];
                bool ok = true;
                for (int i = 0; i < args.Count && ok; i++)
                {
                    try { converted[i] = ToObject(args[i], ps[i].ParameterType); }
                    catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or VfpException) { ok = false; }
                }
                if (ok) return ToValue(m.Invoke(Target, converted));
            }
            // Indexed properties read like methods, as in COM: oList.Item(1).
            if (args.Count > 0)
                foreach (var p in Target.GetType().GetProperties(Flags).Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.GetIndexParameters().Length == args.Count))
                {
                    var ps = p.GetIndexParameters();
                    try { return ToValue(p.GetValue(Target, args.Select((a, i) => ToObject(a, ps[i].ParameterType)).ToArray())); }
                    catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException) { }
                }
        }
        catch (TargetInvocationException ex) { throw OleError(ex.InnerException ?? ex); }
        catch (Exception ex) when (ex is MissingMemberException or System.Runtime.InteropServices.COMException) { throw OleError(ex); }
        throw new VfpException(1734, $"Property {name.ToUpperInvariant()} is not found.", name);
    }

    private static VfpException OleError(Exception ex) => new(1429, $"OLE IDispatch exception: {ex.Message}");

    public static Value ToValue(object? o) => o switch
    {
        null or DBNull => Value.Null,
        string s => Value.String(s),
        char c => Value.String(c.ToString()),
        bool b => Value.Logical(b),
        byte or sbyte or short or ushort or int or uint or long => Value.Number(Convert.ToDouble(o), 0),
        ulong ul => Value.Number(ul, 0),
        float f => Value.Number(f),
        double d => Value.Number(d),
        decimal m => Value.Currency(m),
        DateTime dt => Value.DateTimeOf(dt),
        DateOnly d => Value.DateOf(d),
        byte[] bytes => Value.Binary(bytes),
        Value v => v,
        _ => Value.Object(new ClrObjectProxy(o)),
    };

    public static object? ToObject(Value v, Type? target)
    {
        object? raw = v.Kind switch
        {
            ValueKind.Null => null,
            ValueKind.Character => v.AsString,
            ValueKind.Logical => v.AsBool,
            ValueKind.Number => v.Decimals == 0 && Math.Abs(v.AsNumber) < int.MaxValue ? (int)v.AsNumber : v.AsNumber,
            ValueKind.Currency => v.AsCurrency,
            ValueKind.Date => Julian.ToDate(v.JulianDay).ToDateTime(TimeOnly.MinValue),
            ValueKind.DateTime => Julian.ToDateTime(v.JulianMs),
            ValueKind.Binary => v.AsBinary,
            ValueKind.Object => v.AsObject is ClrObjectProxy p ? p.Target : v.AsObject,
            _ => null,
        };
        if (target == null || raw == null || target.IsInstanceOfType(raw)) return raw;
        var t = Nullable.GetUnderlyingType(target) ?? target;
        if (t == typeof(object)) return raw;
        if (t.IsEnum) return Enum.ToObject(t, Convert.ToInt64(raw));
        if (raw is IConvertible) return Convert.ChangeType(raw, t, System.Globalization.CultureInfo.InvariantCulture);
        throw new InvalidCastException();
    }
}
