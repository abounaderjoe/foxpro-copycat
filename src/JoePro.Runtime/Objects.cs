using JoePro.Core;
using JoePro.Language;

namespace JoePro.Runtime;

/// <summary>A class in the runtime: a native base class or a user class defined with DEFINE CLASS.</summary>
public sealed class ClassInfo
{
    public ClassInfo(string name, string baseClass, ClassInfo? parent, ClassDef? def, ProgramUnit? unit)
    {
        Name = name;
        BaseClass = baseClass;
        Parent = parent;
        Definition = def;
        Unit = unit;
    }

    public string Name { get; }
    public string BaseClass { get; }
    public ClassInfo? Parent { get; }
    public ClassDef? Definition { get; }
    public ProgramUnit? Unit { get; }
    public string? Library { get; init; }

    /// <summary>Finds a user-defined method starting at this class and walking up the hierarchy.</summary>
    public (ProcedureDef Method, ClassInfo Owner)? FindMethod(string name)
    {
        for (var c = this; c != null; c = c.Parent)
            if (c.Definition != null && c.Definition.Methods.TryGetValue(name, out var m)) return (m, c);
        return null;
    }

    public bool IsA(string className)
    {
        for (var c = this; c != null; c = c.Parent)
            if (string.Equals(c.Name, className, StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(BaseClass, className, StringComparison.OrdinalIgnoreCase);
    }

    public IEnumerable<ClassInfo> Hierarchy()
    {
        for (var c = this; c != null; c = c.Parent) yield return c;
    }
}

/// <summary>An object instance. Properties (including member objects) are stored case-insensitively.</summary>
public sealed class VfpObject
{
    private static int _nextId;

    public VfpObject(ClassInfo cls)
    {
        Class = cls;
        Id = Interlocked.Increment(ref _nextId);
    }

    public int Id { get; }
    public ClassInfo Class { get; }
    public Dictionary<string, Variable> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<VfpObject> Members { get; } = new();
    public VfpObject? Parent { get; set; }
    public bool Released { get; set; }
    /// <summary>Items of a Collection object.</summary>
    public List<(string? Key, Value Value)>? Items { get; set; }
    /// <summary>Host-specific state (for example, the UI control rendering a visual object).</summary>
    public object? Native { get; set; }
    /// <summary>Items added with AddItem() to a ListBox or ComboBox (RowSourceType 0).</summary>
    public List<string>? ListItems { get; set; }
    /// <summary>ADD OBJECT … NOINIT: Init is not fired.</summary>
    public bool SkipInit { get; set; }

    public string Name => Properties.TryGetValue("Name", out var v) && v.Value.Kind == ValueKind.Character ? v.Value.AsString : Class.Name;

    public Variable? FindProperty(string name) => Properties.TryGetValue(name, out var v) ? v : null;

    public void Set(string name, Value value)
    {
        if (Properties.TryGetValue(name, out var v)) v.Value = value;
        else Properties[name] = new Variable(name, value);
    }

    public Value Get(string name) => Properties.TryGetValue(name, out var v) ? v.Value : throw PropertyNotFound(name);

    public static VfpException PropertyNotFound(string name) =>
        new(1734, $"Property {name.ToUpperInvariant()} is not found.", name);

    public override string ToString() => $"({Class.Name} {Name})";
}

/// <summary>The native base classes and their default properties.</summary>
public static class BaseClasses
{
    private static readonly string[] Common = ["Name", "Class", "BaseClass", "ParentClass", "ClassLibrary", "Comment", "Tag"];
    private static readonly string[] Visual = ["Left", "Top", "Width", "Height", "Visible", "Enabled", "ToolTipText", "Anchor",
        "ForeColor", "BackColor", "FontName", "FontSize", "FontBold", "FontItalic", "TabIndex", "TabStop", "MousePointer", "HelpContextID"];

    private static readonly Dictionary<string, (bool Visual, string[] Props)> Catalog = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Custom"] = (false, ["Picture"]),
        ["Session"] = (false, ["DataSession", "DataSessionId"]),
        ["Collection"] = (false, ["Count", "KeySort"]),
        ["Empty"] = (false, []),
        ["Exception"] = (false, ["ErrorNo", "Message", "LineNo", "LineContents", "Procedure", "Details", "StackLevel", "UserValue"]),
        ["Timer"] = (false, ["Interval", "Enabled"]),
        ["DataEnvironment"] = (false, ["AutoOpenTables", "AutoCloseTables", "InitialSelectedAlias", "DataSource", "DataSourceType"]),
        ["Cursor"] = (false, ["Alias", "CursorSource", "Database", "Filter", "Order", "ReadOnly", "BufferModeOverride", "Exclusive", "NoDataOnLoad"]),
        ["Relation"] = (false, ["ParentAlias", "ChildAlias", "ChildOrder", "RelationalExpr", "OneToMany"]),
        ["CursorAdapter"] = (false, ["Alias", "SelectCmd", "DataSourceType", "DataSource", "Tables", "KeyFieldList", "UpdatableFieldList", "UpdateNameList",
            "BufferModeOverride", "SendUpdates", "WhereType", "UpdateType", "CursorSchema", "UseCursorSchema", "FetchSize", "MaxRecords", "CursorStatus",
            "AllowUpdate", "AllowInsert", "AllowDelete", "FetchMemo", "NoData", "UpdateCmd", "InsertCmd", "DeleteCmd", "UseTransactions"]),
        ["ReportListener"] = (false, ["ListenerType", "OutputType", "PageNo", "PageTotal", "QuietMode"]),
        ["ProjectHook"] = (false, ["Visible"]),
        ["Form"] = (true, ["Caption", "AutoCenter", "BorderStyle", "ControlBox", "Closable", "MaxButton", "MinButton", "WindowType", "WindowState",
            "DataSession", "DataSessionId", "ShowWindow", "Desktop", "Icon", "KeyPreview", "Movable", "ActiveControl", "ControlCount", "Controls",
            "Picture", "ScrollBars", "TitleBar", "AlwaysOnTop", "BufferMode", "ShowTips", "LockScreen"]),
        ["FormSet"] = (true, ["FormCount", "DataSession", "WindowType"]),
        ["Toolbar"] = (true, ["Caption", "ControlBox", "Movable", "Sizable", "DockPosition", "Docked", "ControlCount", "Controls", "KeyPreview", "ShowWindow"]),
        ["Label"] = (true, ["Caption", "AutoSize", "Alignment", "WordWrap", "BackStyle", "BorderStyle"]),
        ["TextBox"] = (true, ["Value", "ControlSource", "InputMask", "Format", "ReadOnly", "MaxLength", "PasswordChar", "Alignment", "SelStart", "SelLength", "SelText", "BorderStyle", "SpecialEffect"]),
        ["EditBox"] = (true, ["Value", "ControlSource", "ReadOnly", "ScrollBars", "SelStart", "SelLength", "SelText", "MaxLength", "AllowTabs"]),
        ["Spinner"] = (true, ["Value", "ControlSource", "Increment", "KeyboardHighValue", "KeyboardLowValue", "SpinnerHighValue", "SpinnerLowValue", "InputMask"]),
        ["CommandButton"] = (true, ["Caption", "Default", "Cancel", "Picture", "WordWrap", "Style", "Themes"]),
        ["CommandGroup"] = (true, ["ButtonCount", "Buttons", "Value", "ControlSource", "AutoSize", "BorderStyle"]),
        ["OptionButton"] = (true, ["Caption", "Value", "Alignment", "AutoSize", "Style"]),
        ["OptionGroup"] = (true, ["ButtonCount", "Buttons", "Value", "ControlSource", "AutoSize", "BorderStyle"]),
        ["CheckBox"] = (true, ["Caption", "Value", "ControlSource", "Alignment", "AutoSize", "Style"]),
        ["ComboBox"] = (true, ["Value", "ControlSource", "RowSource", "RowSourceType", "BoundColumn", "ColumnCount", "ColumnWidths", "DisplayValue", "ListCount", "ListIndex", "Style", "Sorted", "IncrementalSearch"]),
        ["ListBox"] = (true, ["Value", "ControlSource", "RowSource", "RowSourceType", "BoundColumn", "ColumnCount", "ColumnWidths", "ListCount", "ListIndex", "MultiSelect", "Sorted", "IncrementalSearch"]),
        ["Grid"] = (true, ["RecordSource", "RecordSourceType", "ColumnCount", "Columns", "ReadOnly", "DeleteMark", "RecordMark", "GridLines", "HeaderHeight", "RowHeight", "AllowAddNew", "ActiveColumn", "ActiveRow", "LinkMaster", "ChildOrder", "RelationalExpr", "HighlightStyle", "ScrollBars"]),
        ["Column"] = (true, ["ControlSource", "CurrentControl", "ReadOnly", "Sparse", "ColumnOrder", "DynamicBackColor", "DynamicForeColor", "DynamicFontBold", "DynamicFontItalic", "DynamicAlignment", "DynamicFontName", "DynamicFontSize", "DynamicCurrentControl", "InputMask", "Format", "Alignment", "Resizable", "Movable"]),
        ["Header"] = (true, ["Caption", "Alignment", "Picture"]),
        ["Shape"] = (true, ["Curvature", "FillStyle", "FillColor", "BorderStyle", "BorderWidth", "BorderColor", "BackStyle", "SpecialEffect"]),
        ["Line"] = (true, ["LineSlant", "BorderStyle", "BorderWidth", "BorderColor"]),
        ["Image"] = (true, ["Picture", "Stretch", "BackStyle", "BorderStyle"]),
        ["PageFrame"] = (true, ["PageCount", "Pages", "ActivePage", "Tabs", "TabStyle", "TabStretch", "TabOrientation"]),
        ["Page"] = (true, ["Caption", "PageOrder", "ControlCount", "Controls", "Picture"]),
        ["Container"] = (true, ["ControlCount", "Controls", "BorderWidth", "BackStyle", "SpecialEffect", "Picture"]),
        ["Control"] = (true, ["ControlCount", "Controls", "BorderWidth", "BackStyle", "SpecialEffect"]),
        ["Separator"] = (true, ["Style"]),
        ["Hyperlink"] = (false, []),
        ["OLEControl"] = (true, ["OLEClass", "Object"]),
        ["OLEBoundControl"] = (true, ["ControlSource", "OLEClass"]),
    };

    /// <summary>True for a property every object of the base class has (as opposed to a custom property added by a class).</summary>
    public static bool IsNativeProperty(string baseClass, string property)
    {
        if (Common.Contains(property, StringComparer.OrdinalIgnoreCase)) return true;
        if (!Catalog.TryGetValue(baseClass, out var c)) return false;
        return (c.Visual && Visual.Contains(property, StringComparer.OrdinalIgnoreCase)) || c.Props.Contains(property, StringComparer.OrdinalIgnoreCase);
    }

    public static bool Exists(string name) => Catalog.ContainsKey(name);
    public static bool IsVisual(string name) => Catalog.TryGetValue(name, out var c) && c.Visual;
    public static IEnumerable<string> Names => Catalog.Keys;

    public static string Canonical(string name) => Catalog.Keys.First(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Default value for a native property.</summary>
    public static Value DefaultValue(string prop) => prop.ToUpperInvariant() switch
    {
        "LEFT" or "TOP" or "TABINDEX" or "MOUSEPOINTER" or "HELPCONTEXTID" or "ALIGNMENT" or "BORDERSTYLE" or "BACKSTYLE"
            or "ANCHOR" or "SELSTART" or "SELLENGTH" or "MAXLENGTH" or "LISTINDEX" or "LISTCOUNT" or "CURVATURE" or "FILLSTYLE"
            or "WINDOWSTATE" or "WINDOWTYPE" or "SHOWWINDOW" or "SPECIALEFFECT" or "INTERVAL" or "COUNT" or "KEYSORT"
            or "CONTROLCOUNT" or "FORMCOUNT" or "PAGEORDER" or "ERRORNO" or "LINENO" or "STACKLEVEL" or "ROWSOURCETYPE"
            or "RECORDSOURCETYPE" or "COLUMNCOUNT" or "ACTIVEPAGE" or "BORDERWIDTH" or "STYLE"
            or "DATASESSIONID" or "BUFFERMODE" or "BUFFERMODEOVERRIDE" or "SCROLLBARS" or "DELETEMARK" or "GRIDLINES" => Value.Number(0),
        "WIDTH" => Value.Number(100),
        "HEIGHT" => Value.Number(25),
        "FONTSIZE" => Value.Number(9),
        "DATASESSION" or "BOUNDCOLUMN" or "INCREMENT" => Value.Number(1),
        "PAGECOUNT" or "BUTTONCOUNT" => Value.Number(2),
        "ROWHEIGHT" or "HEADERHEIGHT" => Value.Number(19),
        "SPINNERHIGHVALUE" or "KEYBOARDHIGHVALUE" => Value.Number(2147483647),
        "SPINNERLOWVALUE" or "KEYBOARDLOWVALUE" => Value.Number(-2147483647),
        "FORECOLOR" or "BORDERCOLOR" => Value.Number(0),
        "BACKCOLOR" => Value.Number(15790320),
        "VISIBLE" or "ENABLED" or "TABSTOP" or "CONTROLBOX" or "CLOSABLE" or "MAXBUTTON" or "MINBUTTON" or "MOVABLE" or "TITLEBAR"
            or "AUTOOPENTABLES" or "AUTOCLOSETABLES" or "THEMES" or "SHOWTIPS" or "RECORDMARK" or "ALLOWTABS" => Value.True,
        "FONTNAME" => Value.String("Segoe UI"),
        "VALUE" or "USERVALUE" => Value.EmptyString,
        _ => prop.Equals("Caption", StringComparison.OrdinalIgnoreCase) ? Value.EmptyString : Value.EmptyString,
    };

    public static void InitializeNative(VfpObject o, string baseClass)
    {
        var (visual, props) = Catalog[baseClass];
        foreach (var p in Common) o.Set(p, Value.EmptyString);
        if (visual) foreach (var p in Visual) o.Set(p, DefaultValue(p));
        foreach (var p in props) o.Set(p, DefaultValue(p));
        o.Set("BaseClass", Value.String(Canonical(baseClass)));
        if (baseClass.Equals("Collection", StringComparison.OrdinalIgnoreCase)) o.Items = new();
        if (baseClass.Equals("Empty", StringComparison.OrdinalIgnoreCase)) o.Properties.Clear();
        if (visual) o.Set("Visible", Value.Logical(!baseClass.Equals("Form", StringComparison.OrdinalIgnoreCase) && !baseClass.Equals("FormSet", StringComparison.OrdinalIgnoreCase)));
        switch (baseClass.ToUpperInvariant())
        {
            case "FORM":
                o.Set("Width", Value.Number(375));
                o.Set("Height", Value.Number(250));
                o.Set("Caption", Value.String("Form1"));
                o.Set("MaxButton", Value.True);
                break;
            case "SPINNER" or "OPTIONGROUP" or "COMMANDGROUP": o.Set("Value", Value.Number(baseClass.Equals("Spinner", StringComparison.OrdinalIgnoreCase) ? 0 : 1)); break;
            case "CHECKBOX" or "OPTIONBUTTON": o.Set("Value", Value.Number(0)); break;
            case "LABEL": o.Set("Caption", Value.String("Label1")); o.Set("BackStyle", Value.Number(1)); break;
            case "COMMANDBUTTON": o.Set("Caption", Value.String("Command1")); o.Set("Height", Value.Number(27)); o.Set("Width", Value.Number(84)); break;
            case "PAGEFRAME": o.Set("Width", Value.Number(241)); o.Set("Height", Value.Number(169)); break;
            case "TOOLBAR": o.Set("Caption", Value.String("Toolbar1")); o.Set("DockPosition", Value.Number(-1)); o.Set("Docked", Value.False); o.Set("Visible", Value.False); break;
            case "GRID": o.Set("ColumnCount", Value.Number(-1)); o.Set("Width", Value.Number(320)); o.Set("Height", Value.Number(200)); break;
            case "CURSORADAPTER":
                o.Set("BufferModeOverride", Value.Number(5));
                o.Set("WhereType", Value.Number(3));
                o.Set("UpdateType", Value.Number(1));
                o.Set("FetchSize", Value.Number(100));
                o.Set("MaxRecords", Value.Number(-1));
                o.Set("CursorStatus", Value.Number(0));
                foreach (var p in new[] { "AllowUpdate", "AllowInsert", "AllowDelete", "FetchMemo", "UseTransactions" }) o.Set(p, Value.True);
                foreach (var p in new[] { "SendUpdates", "UseCursorSchema", "NoData" }) o.Set(p, Value.False);
                break;
        }
    }
}
