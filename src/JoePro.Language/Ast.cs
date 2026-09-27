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
/// <summary>A date literal containing a macro ({^&amp;cYear-01-01}); expanded and parsed when evaluated.</summary>
public sealed record DateMacroExpr(string Text) : Expr;
/// <summary>CAST(expression AS type[(width[, decimals])] [NULL | NOT NULL]).</summary>
public sealed record CastExpr(Expr Value, char Type, int Width, int Decimals, bool? Nullable) : Expr;
/// <summary>?name or ?(expression) in a view or pass-through query: a parameter evaluated when the query runs.</summary>
public sealed record ViewParamExpr(Expr Inner) : Expr;

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
public sealed record VarDecl(string Name, List<Expr>? Dims, string? AsType)
{
    /// <summary>DIMENSION obj.aProp[…]: the object property that holds the array.</summary>
    public MemberExpr? Target { get; init; }
    /// <summary>PUBLIC (cName): the variable name is the value of this expression.</summary>
    public Expr? NameExpr { get; init; }
}
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
public sealed record DoFormStmt(Expr Form, List<Expr> Args, string? NameVar, bool Linked, bool NoShow, string? ToVar) : Stmt;
public sealed record ParametersStmt(List<string> Names, bool Local) : Stmt;
public sealed record TryStmt(List<Stmt> Body, string? CatchVar, Expr? When, List<Stmt>? Catch, List<Stmt>? Finally) : Stmt
{
    /// <summary>Second and later CATCH clauses (the first is CatchVar/When/Catch); tried in order.</summary>
    public List<CatchClause> MoreCatches { get; init; } = new();
}
public sealed record CatchClause(string? Var, Expr? When, List<Stmt> Body);
/// <summary>\text (new line first) and \\text: textmerge output.</summary>
public sealed record TextOutStmt(string Text, bool NewLine) : Stmt;
public sealed record ThrowStmt(Expr? Value) : Stmt;
public sealed record ErrorStmt(List<Expr> Args) : Stmt;
public sealed record WithStmt(Expr Target, List<Stmt> Body) : Stmt;
public sealed record TextStmt(string? ToVar, bool Additive, bool NoShow, bool Merge, bool Pretext, List<string> Lines) : Stmt
{
    /// <summary>TEXT TO obj.Property / .Property: an assignable target other than a plain variable.</summary>
    public Expr? ToTarget { get; init; }
}
public sealed record ReleaseStmt(List<string> Names, bool All, string? Like, bool Except) : Stmt
{
    public List<MemberExpr> Members { get; init; } = new();
}
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

public sealed record UseStmt(Expr? Table, Expr? In, Expr? Alias, bool Again, bool? Exclusive, Expr? Order, bool NoUpdate, bool NoData = false) : Stmt;
public sealed record SelectAreaStmt(Expr Area) : Stmt;
public sealed record GoStmt(string Where, Expr? RecNo, Expr? In) : Stmt; // TOP, BOTTOM, RECORD
public sealed record SkipStmt(Expr? Count, Expr? In) : Stmt;
public sealed record SeekStmt(Expr Key, Expr? Order, Expr? In) : Stmt;
public sealed record LocateStmt(Scope Scope) : Stmt;
public sealed record ContinueStmt : Stmt;
public sealed record ReplaceStmt(List<(Expr Field, Expr Value, bool Additive)> Items, Scope Scope) : Stmt;
public sealed record AppendBlankStmt(Expr? In) : Stmt
{
    /// <summary>Written as the xBase INSERT [BLANK] [BEFORE] command.</summary>
    public bool FromInsert { get; init; }
}
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
    Expr? Default, Expr? Check, string? Error, bool AutoInc, long AutoIncNext, int AutoIncStep)
{
    /// <summary>A column name given as an expression: ADD COLUMN (cName) M.</summary>
    public Expr? NameExpr { get; init; }
}
public sealed record CreateTableStmt(Expr Name, bool Cursor, bool Free, List<FieldSpec> Fields, string? FromArray) : Stmt;
public sealed record AlterTableStmt(Expr Name, string Action, FieldSpec? Field, string? DropField, string? RenameFrom, string? RenameTo) : Stmt
{
    public Expr? DropExpr { get; init; }
    public Expr? RenameFromExpr { get; init; }
    public Expr? RenameToExpr { get; init; }
}
/// <summary>ADD TABLE name [NAME longname]: moves a free table (or imports a .dbf) into the current database.</summary>
public sealed record AddTableStmt(Expr Name, Expr? LongName) : Stmt;
/// <summary>REPORT FORM / LABEL FORM file [clauses]. Name is null for "?" (choose a file).</summary>
public sealed record ReportFormStmt(bool Label, Expr? Name, Scope Scope) : Stmt
{
    public bool Environment { get; init; }
    public Expr? Heading { get; init; }
    public bool NoConsole { get; init; }
    public bool Plain { get; init; }
    public Expr? RangeFrom { get; init; }
    public Expr? RangeTo { get; init; }
    public bool Preview { get; init; }
    public bool NoWait { get; init; }
    public bool ToPrinter { get; init; }
    public bool Prompt { get; init; }
    public Expr? ToFile { get; init; }
    public bool Ascii { get; init; }
    public bool Summary { get; init; }
    public bool Sample { get; init; }
    public bool Object { get; init; }
    public Expr? Listener { get; init; }
    public Expr? ObjectType { get; init; }
    public string? NameVar { get; init; }
}
/// <summary>REMOVE TABLE name [DELETE]: takes a table out of the current database (as a free table unless DELETE).</summary>
public sealed record RemoveTableStmt(Expr Name, bool Delete) : Stmt;
public sealed record CreateDatabaseStmt(Expr Name) : Stmt;
/// <summary>APPEND PROCEDURES FROM file [OVERWRITE] / COPY PROCEDURES TO file [ADDITIVE]: the current database's stored procedures.</summary>
public sealed record ProceduresFileStmt(bool Append, Expr File, bool Replace) : Stmt;
/// <summary>CREATE TRIGGER ON table FOR DELETE|INSERT|UPDATE AS expr, or DELETE TRIGGER (Expression null).</summary>
public sealed record TriggerStmt(Expr Table, string Kind, string? Expression) : Stmt;
/// <summary>
/// ALTER TABLE clauses that change rules and keys rather than fields: SET CHECK / DROP CHECK (table rule), ADD/DROP
/// PRIMARY KEY, ADD/DROP UNIQUE, ADD/DROP FOREIGN KEY (persistent relations, with ON UPDATE/DELETE/INSERT rules),
/// and ALTER COLUMN name SET DEFAULT / DROP DEFAULT / SET CHECK / DROP CHECK / NULL / NOT NULL.
/// Expressions are kept as text, the way the database stores them.
/// </summary>
public sealed record AlterTableRuleStmt(Expr Table, string Action) : Stmt
{
    public string? Column { get; init; }
    public string? Expression { get; init; }
    public string? ForExpression { get; init; }
    public string? ErrorText { get; init; }
    public string? Tag { get; init; }
    public string? References { get; init; }
    public string? ReferencesTag { get; init; }
    public bool Save { get; init; }
    public string? RiUpdate { get; init; }
    public string? RiDelete { get; init; }
    public string? RiInsert { get; init; }
}
public sealed record OpenDatabaseStmt(Expr Name, bool Exclusive) : Stmt;
public sealed record SetDatabaseStmt(Expr? Name) : Stmt;
public sealed record ListStmt(bool Display, List<Expr>? Fields, Scope Scope, bool Structure, bool Off) : Stmt;
public sealed record AggregateStmt(string Kind, List<Expr> Exprs, List<Expr> To, Scope Scope, string? ToArray) : Stmt; // COUNT, SUM, AVERAGE, CALCULATE
public sealed record ScatterStmt(bool Memvar, string? Name, List<string>? Fields, bool Blank, bool Memo, string? ToArray, bool Additive) : Stmt;
public sealed record GatherStmt(bool Memvar, string? Name, List<string>? Fields, bool Memo, string? FromArray) : Stmt;
public sealed record TransactionStmt(string Kind) : Stmt; // BEGIN, END, ROLLBACK
public sealed record BrowseStmt(List<Expr>? Fields, Scope Scope) : Stmt;
public sealed record WaitStmt(Expr? Message, string? ToVar, bool Window, bool NoWait, Expr? Timeout, bool Clear) : Stmt;
public sealed record CopyToStmt(Expr Target, string? Type, List<string>? Fields, Scope Scope, bool Structure, bool Array) : Stmt
{
    /// <summary>COPY STRUCTURE EXTENDED: a table with one record per field.</summary>
    public bool Extended { get; init; }
}
/// <summary>Several statements produced by one command (ALTER TABLE with several clauses).</summary>
public sealed record BlockStmt(List<Stmt> Stmts) : Stmt;
/// <summary>BLANK [FIELDS list] [scope] [IN alias]: resets fields to blank values.</summary>
public sealed record BlankStmt(List<string>? Fields, Scope Scope) : Stmt;
/// <summary>COPY FILE source TO destination (wildcards allowed in the source).</summary>
public sealed record CopyFileStmt(Expr Source, Expr Destination) : Stmt;
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
/// <summary>CREATE SQL VIEW name [REMOTE] [CONNECTION conn [SHARE]] AS select. The SELECT text is kept verbatim.</summary>
public sealed record CreateViewStmt(Expr Name, bool Remote, Expr? Connection, bool Share, string Sql) : Stmt;
/// <summary>CREATE CONNECTION name [DATASOURCE …] [USERID …] [PASSWORD …] [DATABASE …] | [CONNSTRING …].</summary>
public sealed record CreateConnectionStmt(Expr Name, Expr? DataSource, Expr? UserId, Expr? Password, Expr? Database, Expr? ConnectString) : Stmt;
/// <summary>DELETE VIEW / DELETE CONNECTION / DROP VIEW (Kind is VIEW or CONNECTION).</summary>
public sealed record DeleteDbObjectStmt(string Kind, Expr Name) : Stmt;
/// <summary>RENAME VIEW / RENAME CONNECTION.</summary>
public sealed record RenameDbObjectStmt(string Kind, Expr From, Expr To) : Stmt;
public sealed record SqlInsertStmt(Expr Table, List<string>? Columns, List<Expr>? Values, SqlSelect? Select, string? FromMemvar, string? FromName, string? FromArray) : Stmt;
public sealed record SqlUpdateStmt(Expr Table, List<(string Column, Expr Value)> Sets, Expr? Where) : Stmt
{
    /// <summary>VFP 9 UPDATE … FROM: extra sources joined to the target.</summary>
    public List<SqlSource>? From { get; init; }
    public List<SqlJoin>? Joins { get; init; }
}
public sealed record SqlDeleteStmt(Expr Table, Expr? Where) : Stmt
{
    /// <summary>VFP 9 DELETE … FROM with several sources (or an aliased source); Table names the target.</summary>
    public List<SqlSource>? From { get; init; }
    public List<SqlJoin>? Joins { get; init; }
}

// ======================================================================================
// Program structure
// ======================================================================================

public sealed record ProcedureDef(string Name, List<string> Parameters, bool LocalParameters, List<Stmt> Body, int Line, string? Visibility = null)
{
    public bool IsFunction { get; init; }
}

public sealed record MemberDef(string Name, Expr? Value, List<Expr>? Dims, string? Visibility);
public sealed record AddObjectDef(string Name, string Class, List<(string Prop, Expr Value)> Properties, bool NoInit, bool Protected)
{
    /// <summary>ADD OBJECT … AS class OF library.</summary>
    public string? ClassLib { get; init; }
}

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

// ---- Menus (DEFINE MENU/PAD/POPUP/BAR, ON PAD/BAR/SELECTION, ACTIVATE, …) ----------------------------------

/// <summary>A menu, pad, popup or bar name: an identifier, or (expr) / &amp;macro evaluated when the command runs.</summary>
public sealed record MenuName(string? Text, Expr? Expr = null);

/// <summary>Common options of DEFINE PAD and DEFINE BAR.</summary>
public sealed record MenuItemOptions
{
    public MenuName? Before { get; init; }
    public MenuName? After { get; init; }
    public string? KeyName { get; init; }
    public Expr? KeyText { get; init; }
    public Expr? Mark { get; init; }
    /// <summary>SKIP FOR expression text (evaluated each time the menu is shown); "" for plain SKIP.</summary>
    public string? SkipFor { get; init; }
    public Expr? Message { get; init; }
    public Expr? Picture { get; init; }
    public string? PictRes { get; init; }
}

public sealed record DefineMenuStmt(MenuName Name, bool Bar, Expr? Message) : Stmt;
public sealed record DefinePadStmt(MenuName Name, MenuName Menu, Expr Prompt, MenuItemOptions Options) : Stmt;
public sealed record DefinePopupStmt(MenuName Name) : Stmt
{
    public bool Shortcut { get; init; }
    public bool Relative { get; init; }
    public bool Margin { get; init; }
    public bool MultiSelect { get; init; }
    public Expr? Title { get; init; }
    public Expr? Message { get; init; }
    /// <summary>PROMPT FIELD expr / FILES [LIKE skeleton] / STRUCTURE: the bars come from data.</summary>
    public string? PromptKind { get; init; }
    public Expr? PromptExpr { get; init; }
}
/// <summary>DEFINE BAR n | SystemBar OF popup PROMPT … (Bar is the number text or the system bar name).</summary>
public sealed record DefineBarStmt(MenuName Bar, MenuName Popup, Expr? Prompt, MenuItemOptions Options) : Stmt;
/// <summary>
/// ON PAD/BAR … ACTIVATE POPUP|MENU name, and ON SELECTION PAD/BAR/POPUP/MENU … [command].
/// Kind is PAD, BAR, SELECTION PAD, SELECTION BAR, SELECTION POPUP or SELECTION MENU.
/// </summary>
public sealed record OnMenuStmt(string Kind, MenuName Name, MenuName? Of, string? ActivatePopup, string? ActivateMenu, string? Command, bool All = false) : Stmt;
public sealed record ActivateMenuStmt(bool Popup, MenuName Name, bool NoWait, Expr? Item) : Stmt;
/// <summary>DEACTIVATE / HIDE / SHOW / RELEASE of MENU(S), POPUP(S), PAD, BAR.</summary>
public sealed record MenuControlStmt(string Verb, string Kind, List<MenuName> Names, bool All, MenuName? Of, bool Extended) : Stmt;
/// <summary>SET MARK OF … TO lExpr / SET SKIP OF … lExpr.</summary>
public sealed record SetMenuFlagStmt(string Flag, string Kind, MenuName? Name, MenuName? Of, Expr Value) : Stmt;
