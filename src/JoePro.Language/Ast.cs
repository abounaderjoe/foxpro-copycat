using JoePro.Core;

namespace JoePro.Language;

// ======================================================================================
// Expressions
// ======================================================================================

public abstract record Expr;

public sealed record LiteralExpr(Value Value) : Expr;
/// <summary>A bare name: memory variable, field of the current work area, or constant-like identifier.</summary>
public sealed record NameExpr(string Name) : Expr;
/// <summary>m.name: always a memory variable.</summary>
public sealed record MemVarExpr(string Name) : Expr;
/// <summary>target.name: object property, or alias.field when target names a work area.</summary>
public sealed record MemberExpr(Expr Target, string Name) : Expr;
/// <summary>alias->field.</summary>
public sealed record AliasFieldExpr(string Alias, string Field) : Expr;
/// <summary>name(args): a function call, or an array element when an array named <see cref="Name"/> exists.</summary>
public sealed record CallExpr(string Name, List<Expr> Args) : Expr;
/// <summary>name[args]: array element.</summary>
public sealed record IndexExpr(Expr Target, List<Expr> Args) : Expr;
/// <summary>target.name(args): method call, or array property element.</summary>
public sealed record MethodCallExpr(Expr Target, string Name, List<Expr> Args) : Expr;
public sealed record UnaryExpr(string Op, Expr Operand) : Expr;
public sealed record BinaryExpr(string Op, Expr Left, Expr Right) : Expr;
/// <summary>@name: pass by reference.</summary>
public sealed record ByRefExpr(string Name) : Expr;
/// <summary>An omitted argument, as in FUNC(a, , c).</summary>
public sealed record EmptyArgExpr : Expr;
/// <summary>The THIS/THISFORM/THISFORMSET/PARENT keywords.</summary>
public sealed record SpecialObjectExpr(string Which) : Expr;
/// <summary>A SQL sub-select used as an expression (IN (SELECT…), EXISTS).</summary>
public sealed record SubqueryExpr(SqlSelect Query) : Expr;
/// <summary>A column list comparison target for IN: x IN (a, b, c).</summary>
public sealed record InListExpr(Expr Value, List<Expr> Items, bool Not) : Expr;
public sealed record BetweenExpr(Expr Value, Expr Low, Expr High, bool Not) : Expr;
public sealed record LikeExpr(Expr Value, Expr Pattern, bool Not) : Expr;
public sealed record IsNullExpr(Expr Value, bool Not) : Expr;
public sealed record ExistsExpr(SqlSelect Query) : Expr;

// ======================================================================================
// Statements
// ======================================================================================

public abstract record Stmt
{
    public int Line { get; init; }
}

public sealed record AssignStmt(Expr Target, Expr Value) : Stmt;
public sealed record StoreStmt(Expr Value, List<Expr> Targets) : Stmt;
public sealed record ExprStmt(Expr Expr) : Stmt;
public sealed record PrintStmt(bool NewLine, List<Expr> Items) : Stmt;
public sealed record VarDecl(string Name, List<Expr>? Dims, string? AsType);
public sealed record DeclareStmt(string Scope, List<VarDecl> Vars) : Stmt; // LOCAL, PRIVATE, PUBLIC, DIMENSION
public sealed record PrivateAllStmt(string? Like, bool Except) : Stmt;
public sealed record IfStmt(Expr Cond, List<Stmt> Then, List<Stmt>? Else) : Stmt;
public sealed record CaseClause(Expr Cond, List<Stmt> Body);
public sealed record DoCaseStmt(List<CaseClause> Cases, List<Stmt>? Otherwise) : Stmt;
public sealed record DoWhileStmt(Expr Cond, List<Stmt> Body) : Stmt;
public sealed record ForStmt(string Var, Expr From, Expr To, Expr? Step, List<Stmt> Body) : Stmt;
public sealed record ForEachStmt(string Var, Expr Collection, List<Stmt> Body) : Stmt;
public sealed record ScanStmt(Scope Scope, List<Stmt> Body) : Stmt;
public sealed record ExitStmt : Stmt;
public sealed record LoopStmt : Stmt;
public sealed record ReturnStmt(Expr? Value, bool ToMaster = false) : Stmt;
public sealed record DoStmt(Expr Target, string? InFile, List<Expr> Args) : Stmt;
public sealed record ParametersStmt(List<string> Names, bool Local) : Stmt;
public sealed record TryStmt(List<Stmt> Body, string? CatchVar, Expr? When, List<Stmt>? Catch, List<Stmt>? Finally) : Stmt;
public sealed record ThrowStmt(Expr? Value) : Stmt;
public sealed record ErrorStmt(List<Expr> Args) : Stmt;
public sealed record WithStmt(Expr Target, List<Stmt> Body) : Stmt;
public sealed record TextStmt(string? ToVar, bool Additive, bool NoShow, bool Merge, bool Pretext, List<string> Lines) : Stmt;
public sealed record ReleaseStmt(List<string> Names, bool All, string? Like, bool Except) : Stmt;
public sealed record OnErrorStmt(string? Command) : Stmt;
/// <summary>A statement whose text contains &amp;macros: re-parsed after substitution at run time.</summary>
public sealed record MacroStmt(string Text) : Stmt;
public sealed record NoOpStmt(string Verb) : Stmt;

/// <summary>Record scope for xBase commands: ALL | NEXT n | RECORD n | REST, FOR, WHILE, IN.</summary>
public sealed record Scope(string Kind = "DEFAULT", Expr? Count = null, Expr? For = null, Expr? While = null, Expr? In = null)
{
    public static readonly Scope Default = new();
}

// ---- Data commands ---------------------------------------------------------------------

public sealed record UseStmt(Expr? Table, Expr? In, Expr? Alias, bool Again, bool? Exclusive, Expr? Order, bool NoUpdate) : Stmt;
public sealed record SelectAreaStmt(Expr Area) : Stmt;
public sealed record GoStmt(string Where, Expr? RecNo, Expr? In) : Stmt; // TOP, BOTTOM, RECORD
public sealed record SkipStmt(Expr? Count, Expr? In) : Stmt;
public sealed record SeekStmt(Expr Key, Expr? Order, Expr? In) : Stmt;
public sealed record LocateStmt(Scope Scope) : Stmt;
public sealed record ContinueStmt : Stmt;
public sealed record ReplaceStmt(List<(Expr Field, Expr Value, bool Additive)> Items, Scope Scope) : Stmt;
public sealed record AppendBlankStmt(Expr? In) : Stmt;
public sealed record AppendFromStmt(Expr Source, Expr? For, string? Type, List<string>? Fields) : Stmt;
public sealed record DeleteStmt(Scope Scope, bool Recall) : Stmt;
public sealed record PackStmt(Expr? In) : Stmt;
public sealed record ZapStmt(Expr? In) : Stmt;
public sealed record IndexStmt(Expr Key, string Tag, Expr? For, bool Descending, string Kind, bool Additive) : Stmt;
public sealed record ReindexStmt : Stmt;
public sealed record DeleteTagStmt(List<string> Tags, bool All) : Stmt;
public sealed record SetOrderStmt(Expr? Tag, Expr? In, bool? Descending) : Stmt;
public sealed record SetFilterStmt(Expr? Filter, Expr? In) : Stmt;
public sealed record SetRelationStmt(List<(Expr Expr, Expr Into)> Relations, bool Additive, bool Off, Expr? OffInto) : Stmt;
public sealed record SetStmt(string Option, string? Value, Expr? Expr, List<Token> Raw) : Stmt;
public sealed record CloseStmt(string What) : Stmt; // TABLES, DATABASES, ALL, INDEXES
public sealed record FieldSpec(string Name, char Type, int Width, int Decimals, bool Null, bool NotNull, bool PrimaryKey, bool Unique,
    Expr? Default, Expr? Check, string? Error, bool AutoInc, long AutoIncNext, int AutoIncStep);
public sealed record CreateTableStmt(Expr Name, bool Cursor, bool Free, List<FieldSpec> Fields, string? FromArray) : Stmt;
public sealed record AlterTableStmt(Expr Name, string Action, FieldSpec? Field, string? DropField, string? RenameFrom, string? RenameTo) : Stmt;
public sealed record CreateDatabaseStmt(Expr Name) : Stmt;
public sealed record OpenDatabaseStmt(Expr Name, bool Exclusive) : Stmt;
public sealed record SetDatabaseStmt(Expr? Name) : Stmt;
public sealed record ListStmt(bool Display, List<Expr>? Fields, Scope Scope, bool Structure, bool Off) : Stmt;
public sealed record AggregateStmt(string Kind, List<Expr> Exprs, List<Expr> To, Scope Scope, string? ToArray) : Stmt; // COUNT, SUM, AVERAGE, CALCULATE
public sealed record ScatterStmt(bool Memvar, string? Name, List<string>? Fields, bool Blank, bool Memo, string? ToArray, bool Additive) : Stmt;
public sealed record GatherStmt(bool Memvar, string? Name, List<string>? Fields, bool Memo, string? FromArray) : Stmt;
public sealed record TransactionStmt(string Kind) : Stmt; // BEGIN, END, ROLLBACK
public sealed record BrowseStmt(List<Expr>? Fields, Scope Scope) : Stmt;
public sealed record WaitStmt(Expr? Message, string? ToVar, bool Window, bool NoWait, Expr? Timeout, bool Clear) : Stmt;
public sealed record CopyToStmt(Expr Target, string? Type, List<string>? Fields, Scope Scope, bool Structure, bool Array) : Stmt;
public sealed record ImportStmt(Expr Source, Expr? To, bool Database) : Stmt;
public sealed record UpdateTableStmt(bool All, bool Force, Expr? In) : Stmt;
public sealed record DefineClassStmt(ClassDef Class) : Stmt;
public sealed record ReadEventsStmt(bool Clear) : Stmt;
public sealed record QuitStmt(bool Cancel) : Stmt;
public sealed record ClearStmt(string? What) : Stmt;
public sealed record ChdirStmt(Expr Path) : Stmt;
public sealed record SetProcedureStmt(List<Expr> Files, bool Additive, bool Clear) : Stmt;
public sealed record CompileStmt(Expr File) : Stmt;

// ---- SQL -----------------------------------------------------------------------------

public sealed record SqlColumn(Expr Expr, string? Alias, bool Star, string? StarAlias);
public sealed record SqlJoin(string Kind, SqlSource Source, Expr? On);
public sealed record SqlSource(Expr? Table, string? Alias, SqlSelect? Derived);
public sealed record SqlOrder(Expr Expr, bool Desc, int? Position);
public sealed record SqlSelect(
    bool Distinct, Expr? Top, bool TopPercent, List<SqlColumn> Columns,
    List<SqlSource> From, List<SqlJoin> Joins, Expr? Where,
    List<Expr> GroupBy, Expr? Having, List<(SqlSelect Query, bool All)> Unions,
    List<SqlOrder> OrderBy, string? IntoKind, string? IntoName, bool ReadWrite, bool NoFilter);
public sealed record SqlSelectStmt(SqlSelect Query) : Stmt;
public sealed record SqlInsertStmt(Expr Table, List<string>? Columns, List<Expr>? Values, SqlSelect? Select, string? FromMemvar, string? FromName, string? FromArray) : Stmt;
public sealed record SqlUpdateStmt(Expr Table, List<(string Column, Expr Value)> Sets, Expr? Where) : Stmt;
public sealed record SqlDeleteStmt(Expr Table, Expr? Where) : Stmt;

// ======================================================================================
// Program structure
// ======================================================================================

public sealed record ProcedureDef(string Name, List<string> Parameters, bool LocalParameters, List<Stmt> Body, int Line, string? Visibility = null)
{
    public bool IsFunction { get; init; }
}

public sealed record MemberDef(string Name, Expr? Value, List<Expr>? Dims, string? Visibility);
public sealed record AddObjectDef(string Name, string Class, List<(string Prop, Expr Value)> Properties, bool NoInit, bool Protected);

public sealed record ClassDef(string Name, string Parent, string? ParentLib, List<MemberDef> Members, List<AddObjectDef> Objects,
    Dictionary<string, ProcedureDef> Methods, int Line)
{
    public HashSet<string> Protected { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Hidden { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ProgramUnit
{
    public string Name { get; init; } = "";
    public string? File { get; init; }
    public List<Stmt> Main { get; } = new();
    public Dictionary<string, ProcedureDef> Procedures { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ClassDef> Classes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> MainParameters { get; set; } = new();
    public bool MainLocalParameters { get; set; }
}
