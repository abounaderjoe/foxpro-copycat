using JoePro.Core;

namespace JoePro.Data;

/// <summary>A record whose fields an expression can read (the current row, or a row being written).</summary>
public interface IRecord
{
    Table Table { get; }
    int RecNo { get; }
    bool Deleted { get; }
    Value Get(int fieldIndex);
}

/// <summary>A FoxPro expression compiled by the language runtime (index keys, FOR clauses, filters).</summary>
public interface ICompiledExpression
{
    string Source { get; }
    /// <summary>Evaluates with field names resolved against <paramref name="record"/> first.</summary>
    Value Evaluate(IRecord record);
}

/// <summary>
/// Supplied by the runtime so the data engine can evaluate FoxPro expressions without
/// depending on the language implementation.
/// </summary>
public interface IExpressionHost
{
    ICompiledExpression Compile(string expression);
}

/// <summary>An in-memory row snapshot.</summary>
public sealed class RowData : IRecord
{
    public RowData(Table table, int recNo, bool deleted, Value[] values, long rowVersion = 0)
    {
        Table = table;
        RecNo = recNo;
        Deleted = deleted;
        Values = values;
        RowVersion = rowVersion;
    }

    public Table Table { get; }
    public int RecNo { get; }
    public bool Deleted { get; set; }
    public Value[] Values { get; }
    public long RowVersion { get; set; }
    public Value Get(int fieldIndex) => Values[fieldIndex];
    public RowData Clone() => new(Table, RecNo, Deleted, (Value[])Values.Clone(), RowVersion);
}
