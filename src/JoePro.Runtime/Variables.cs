using JoePro.Core;
using JoePro.Language;

namespace JoePro.Runtime;

/// <summary>A FoxPro array: one or two dimensions, 1-based, resizable. New elements are .F.</summary>
public sealed class VfpArray
{
    private Value[] _data;

    public VfpArray(int rows, int cols = 0)
    {
        if (rows < 1 || cols < 0) throw VfpException.InvalidSubscript();
        Rows = rows;
        Cols = cols;
        _data = Enumerable.Repeat(Value.False, rows * Math.Max(1, cols)).ToArray();
    }

    public int Rows { get; private set; }
    /// <summary>0 for a one-dimensional array.</summary>
    public int Cols { get; private set; }
    public int Length => _data.Length;
    public bool TwoDimensional => Cols > 0;

    public Value this[int index]
    {
        get => index >= 1 && index <= _data.Length ? _data[index - 1] : throw VfpException.InvalidSubscript();
        set
        {
            if (index < 1 || index > _data.Length) throw VfpException.InvalidSubscript();
            _data[index - 1] = value;
        }
    }

    public int Index(int row, int col)
    {
        if (!TwoDimensional)
        {
            // VFP allows a(1, n) style access on 1-D arrays only for column 1.
            if (col != 1) throw VfpException.InvalidSubscript();
            return row;
        }
        if (row < 1 || row > Rows || col < 1 || col > Cols) throw VfpException.InvalidSubscript();
        return (row - 1) * Cols + col;
    }

    public Value this[int row, int col]
    {
        get => this[Index(row, col)];
        set => this[Index(row, col)] = value;
    }

    /// <summary>DIMENSION on an existing array keeps existing elements in storage order.</summary>
    public void Redimension(int rows, int cols)
    {
        if (rows < 1 || cols < 0) throw VfpException.InvalidSubscript();
        var n = rows * Math.Max(1, cols);
        var old = _data;
        _data = new Value[n];
        for (int i = 0; i < n; i++) _data[i] = i < old.Length ? old[i] : Value.False;
        Rows = rows;
        Cols = cols;
    }

    public void Fill(Value v) => Array.Fill(_data, v);
    public Value[] Raw => _data;

    public (int Row, int Col) Subscript(int index) =>
        TwoDimensional ? ((index - 1) / Cols + 1, (index - 1) % Cols + 1) : (index, 1);

    public VfpArray Clone()
    {
        var a = new VfpArray(Rows, Cols);
        _data.CopyTo(a._data, 0);
        return a;
    }
}

/// <summary>A memory variable: a scalar value or an array.</summary>
public sealed class Variable
{
    public Variable(string name, Value value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }
    public Value Value { get; set; }
    public VfpArray? Array { get; set; }
    public bool IsArray => Array != null;
}

/// <summary>A procedure or method activation.</summary>
public sealed class Frame
{
    public Frame(string program, Frame? parent)
    {
        Program = program;
        Parent = parent;
        Level = parent == null ? 1 : parent.Level + 1;
    }

    public string Program { get; }
    public Frame? Parent { get; }
    public int Level { get; }
    public int Line { get; set; }
    public Dictionary<string, Variable> Locals { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Variable> Privates { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Names made PRIVATE by PRIVATE ALL (LIKE/EXCEPT) in this frame.</summary>
    public List<(string Pattern, bool Except)>? PrivateAll { get; set; }
    public VfpObject? This { get; set; }
    /// <summary>The class level whose method is executing (for DODEFAULT).</summary>
    public ClassInfo? MethodClass { get; set; }
    public string? MethodName { get; set; }
    public Stack<object?> WithStack { get; } = new();
    public Value ReturnValue { get; set; } = Value.True;
    public int ParameterCount { get; set; }
    public bool NoDefault { get; set; }
    public ProgramUnit? Unit { get; set; }
}
