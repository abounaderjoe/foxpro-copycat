namespace JoePro.Documents.Menus;

/// <summary>
/// Editing operations for the Menu Designer. Items are addressed by path (indices from the top level down); every
/// change is one undo step restored from canonical text.
/// </summary>
public sealed class MenuDesignSession
{
    private readonly List<string> _undo = new();
    private readonly List<string> _redo = new();
    private string _saved;
    private int _depth;
    private string? _start;

    public MenuDesignSession(MenuDocument doc)
    {
        Document = doc;
        _saved = Text;
    }

    public MenuDocument Document { get; private set; }
    public string Text => MenuSerializer.Write(Document);
    public bool IsDirty => Text != _saved;
    public void MarkSaved() => _saved = Text;
    public bool CanUndo => _undo.Count > 0;
    public event Action? Changed;

    public void Transaction(Action body)
    {
        if (_depth++ == 0) _start = Text;
        try { body(); }
        finally
        {
            if (--_depth == 0 && _start != Text)
            {
                _undo.Add(_start!);
                _redo.Clear();
                Changed?.Invoke();
            }
        }
    }

    public void Update(Action<MenuDocument> change) => Transaction(() => change(Document));

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Add(Text);
        Document = MenuSerializer.Parse(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(Text);
        Document = MenuSerializer.Parse(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        Changed?.Invoke();
    }

    private List<MenuNode> ListOf(IReadOnlyList<int> parentPath)
    {
        var list = Document.Items;
        foreach (var i in parentPath) list = list[i].Items;
        return list;
    }

    public MenuNode? Get(IReadOnlyList<int> path)
    {
        if (path.Count == 0) return null;
        var list = Document.Items;
        MenuNode? node = null;
        foreach (var i in path)
        {
            if (i < 0 || i >= list.Count) return null;
            node = list[i];
            list = node.Items;
        }
        return node;
    }

    private static int[] Parent(IReadOnlyList<int> path) => path.Take(path.Count - 1).ToArray();

    /// <summary>Inserts an item before position <paramref name="index"/> of a level (at the end when index is past it). Returns its path.</summary>
    public int[] Insert(IReadOnlyList<int> parentPath, int index, MenuNode node)
    {
        var list = ListOf(parentPath);
        index = Math.Clamp(index, 0, list.Count);
        Transaction(() => list.Insert(index, node));
        return [.. parentPath, index];
    }

    public void Remove(IReadOnlyList<int> path)
    {
        if (Get(path) == null) return;
        Transaction(() => ListOf(Parent(path)).RemoveAt(path[^1]));
    }

    /// <summary>Moves an item up (-1) or down (+1) within its level. Returns its new path.</summary>
    public int[] Move(IReadOnlyList<int> path, int delta)
    {
        var list = ListOf(Parent(path));
        var i = path[^1];
        var j = i + delta;
        if (j < 0 || j >= list.Count) return path.ToArray();
        Transaction(() => (list[i], list[j]) = (list[j], list[i]));
        return [.. Parent(path), j];
    }

    /// <summary>Indent: the item becomes the last item of the submenu of the item above it. Returns its new path.</summary>
    public int[] Indent(IReadOnlyList<int> path)
    {
        var i = path[^1];
        if (i == 0) return path.ToArray();
        var list = ListOf(Parent(path));
        var above = list[i - 1];
        if (above.IsSeparator || above.SystemBar != null) return path.ToArray();
        var node = list[i];
        Transaction(() =>
        {
            list.RemoveAt(i);
            above.Command = null;
            above.Procedure = null;
            above.Items.Add(node);
        });
        return [.. Parent(path), i - 1, above.Items.Count - 1];
    }

    /// <summary>Outdent: the item moves out of its submenu to just after its parent item. Returns its new path.</summary>
    public int[] Outdent(IReadOnlyList<int> path)
    {
        if (path.Count < 2) return path.ToArray();
        var parentPath = Parent(path);
        var node = Get(path)!;
        var grand = Parent(parentPath);
        Transaction(() =>
        {
            ListOf(parentPath).RemoveAt(path[^1]);
            ListOf(grand).Insert(parentPath[^1] + 1, node);
        });
        return [.. grand, parentPath[^1] + 1];
    }

    /// <summary>Quick Menu: a menu bar with the standard File, Edit, Window and Help menus built from system bars.</summary>
    public static MenuDocument QuickMenu()
    {
        MenuNode Pad(string prompt, string name, params string[] bars)
        {
            var pad = new MenuNode { Prompt = prompt, Name = name };
            foreach (var b in bars) pad.Items.Add(b == "-" ? new MenuNode { Prompt = "\\-" } : new MenuNode { SystemBar = b });
            return pad;
        }
        var doc = new MenuDocument();
        doc.Items.Add(Pad("\\<File", "_msm_file", "_MFI_OPEN", "_MFI_CLOSE", "-", "_MFI_SAVE", "_MFI_SAVAS", "-", "_MFI_PRINT", "-", "_MFI_QUIT"));
        doc.Items.Add(Pad("\\<Edit", "_msm_edit", "_MED_UNDO", "_MED_REDO", "-", "_MED_CUT", "_MED_COPY", "_MED_PASTE", "_MED_CLEAR", "-", "_MED_SLCTA", "_MED_FIND"));
        doc.Items.Add(Pad("\\<Window", "_msm_windo", "_MWI_ARRAN", "_MWI_CMD"));
        doc.Items.Add(Pad("\\<Help", "_msm_systm", "_MST_HELP", "-", "_MST_ABOUT"));
        return doc;
    }
}
