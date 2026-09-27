namespace JoePro.Documents.Reports;

/// <summary>A report object by position: its band's index in <see cref="ReportDocument.Bands"/> and its index in that band.</summary>
public readonly record struct ObjectRef(int Band, int Index);

/// <summary>
/// Editing operations on a report or label layout for the Report and Label Designers. Every change is one undo
/// step; undo restores canonical text snapshots, so it cannot drift. Objects are addressed by position
/// (<see cref="ObjectRef"/>) because undo replaces the object instances.
/// </summary>
public sealed class ReportDesignSession
{
    private readonly List<(string Name, string Text)> _undo = new();
    private readonly List<(string Name, string Text)> _redo = new();
    private string _savedText;
    private int _depth;
    private string? _start;
    private string _name = "";

    public ReportDesignSession(ReportDocument document)
    {
        Document = document;
        _savedText = Text;
    }

    public ReportDocument Document { get; private set; }
    public string Text => ReportSerializer.Write(Document);
    public bool IsDirty => Text != _savedText;
    public void MarkSaved() => _savedText = Text;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoName => _undo.Count > 0 ? _undo[^1].Name : null;

    /// <summary>Raised after every change, undo and redo.</summary>
    public event Action? Changed;

    public void Transaction(string name, Action body)
    {
        if (_depth++ == 0) { _start = Text; _name = name; }
        try { body(); }
        finally
        {
            if (--_depth == 0)
            {
                var before = _start!;
                _start = null;
                if (before != Text)
                {
                    _undo.Add((_name, before));
                    _redo.Clear();
                    Changed?.Invoke();
                }
            }
        }
    }

    /// <summary>Any change to the document as one undo step.</summary>
    public void Update(string name, Action<ReportDocument> change) => Transaction(name, () => change(Document));

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var (name, text) = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add((name, Text));
        Restore(text);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var (name, text) = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add((name, Text));
        Restore(text);
    }

    private void Restore(string text)
    {
        Document = ReportSerializer.Parse(text, Document.Kind);
        Changed?.Invoke();
    }

    // ---- Objects ---------------------------------------------------------------------------------

    public ReportObject? Get(ObjectRef r) =>
        r.Band >= 0 && r.Band < Document.Bands.Count && r.Index >= 0 && r.Index < Document.Bands[r.Band].Objects.Count ? Document.Bands[r.Band].Objects[r.Index] : null;

    public IEnumerable<ObjectRef> AllObjects() =>
        Document.Bands.SelectMany((b, bi) => b.Objects.Select((_, oi) => new ObjectRef(bi, oi)));

    public ObjectRef AddObject(int band, ReportObject o)
    {
        var r = new ObjectRef(band, Document.Bands[band].Objects.Count);
        Transaction($"Add {Describe(o)}", () => Document.Bands[band].Objects.Add(o));
        return r;
    }

    public void RemoveObjects(IEnumerable<ObjectRef> refs)
    {
        var list = refs.Distinct().OrderByDescending(r => r.Band).ThenByDescending(r => r.Index).ToList();
        if (list.Count == 0) return;
        Transaction("Delete", () =>
        {
            foreach (var r in list)
                if (Get(r) != null) Document.Bands[r.Band].Objects.RemoveAt(r.Index);
        });
    }

    /// <summary>Moves objects by an offset; an object whose new top falls in another band moves into that band.</summary>
    public List<ObjectRef> MoveObjects(IReadOnlyList<ObjectRef> refs, double dx, double dy, string name = "Move")
    {
        var result = new List<ObjectRef>();
        Transaction(name, () =>
        {
            var moving = refs.Select(r => (Ref: r, Obj: Get(r)!)).Where(x => x.Obj != null).ToList();
            foreach (var (r, o) in moving)
            {
                o.Left = Math.Round(Math.Max(0, o.Left + dx), 4);
                var absTop = BandTop(r.Band) + o.Top + dy;
                var target = BandAt(absTop);
                o.Top = Math.Round(Math.Max(0, absTop - BandTop(target)), 4);
                if (target != r.Band)
                {
                    Document.Bands[r.Band].Objects.Remove(o);
                    Document.Bands[target].Objects.Add(o);
                }
            }
            foreach (var (_, o) in moving)
                for (int b = 0; b < Document.Bands.Count; b++)
                    if (Document.Bands[b].Objects.IndexOf(o) is var i and >= 0) result.Add(new ObjectRef(b, i));
        });
        return result;
    }

    /// <summary>The top of a band in the designer's stacked layout (bands one below the other, in inches).</summary>
    public double BandTop(int band)
    {
        double top = 0;
        for (int i = 0; i < band; i++) top += Document.Bands[i].Height;
        return top;
    }

    /// <summary>The band at a height in the stacked layout (the last band for anything below it).</summary>
    public int BandAt(double y)
    {
        double top = 0;
        for (int i = 0; i < Document.Bands.Count; i++)
        {
            if (y < top + Document.Bands[i].Height) return i;
            top += Document.Bands[i].Height;
        }
        return Math.Max(0, Document.Bands.Count - 1);
    }

    public void SetBounds(ObjectRef r, double left, double top, double width, double height) =>
        Transaction("Resize", () =>
        {
            var o = Get(r)!;
            o.Left = Math.Round(Math.Max(0, left), 4);
            o.Top = Math.Round(Math.Max(0, top), 4);
            o.Width = Math.Round(Math.Max(0, width), 4);
            o.Height = Math.Round(Math.Max(0, height), 4);
        });

    public void SetBandHeight(int band, double height) =>
        Transaction("Band height", () => Document.Bands[band].Height = Math.Round(Math.Max(0, height), 4));

    /// <summary>Brings objects to the front (drawn last) or sends them to the back.</summary>
    public void ZOrder(IEnumerable<ObjectRef> refs, bool toFront) =>
        Transaction(toFront ? "Bring to front" : "Send to back", () =>
        {
            foreach (var g in refs.Select(r => (r.Band, Obj: Get(r))).Where(x => x.Obj != null).GroupBy(x => x.Band))
            {
                var objects = Document.Bands[g.Key].Objects;
                foreach (var (_, o) in g) objects.Remove(o!);
                if (toFront) objects.AddRange(g.Select(x => x.Obj!));
                else objects.InsertRange(0, g.Select(x => x.Obj!));
            }
        });

    /// <summary>Copies objects as a small report document (for the clipboard).</summary>
    public string Copy(IEnumerable<ObjectRef> refs)
    {
        var clip = new ReportDocument { Kind = Document.Kind };
        var band = new ReportBand { Kind = BandKind.Detail, Index = 1 };
        foreach (var r in refs) if (Get(r) is { } o) band.Objects.Add(o.Clone());
        clip.Bands.Add(band);
        return ReportSerializer.Write(clip);
    }

    public List<ObjectRef> Paste(string clipboard, int band, double offset = 0.125)
    {
        var added = new List<ObjectRef>();
        ReportDocument clip;
        try { clip = ReportSerializer.Parse(clipboard); }
        catch (FormatException) { return added; }
        Transaction("Paste", () =>
        {
            foreach (var o in clip.Bands.SelectMany(b => b.Objects))
            {
                o.Left += offset;
                o.Top = Math.Min(o.Top + offset, Math.Max(0, Document.Bands[band].Height - o.Height));
                Document.Bands[band].Objects.Add(o);
                added.Add(new ObjectRef(band, Document.Bands[band].Objects.Count - 1));
            }
        });
        return added;
    }

    // ---- Bands, groups and variables ---------------------------------------------------------------

    /// <summary>Title and summary bands on or off (Report → Title/Summary).</summary>
    public void SetTitleSummary(bool title, bool summary, bool titleOnNewPage = false, bool summaryOnNewPage = false) =>
        Transaction("Title/Summary", () =>
        {
            Toggle(BandKind.Title, title, 0.5);
            Toggle(BandKind.Summary, summary, 0.5);
            Document.TitleOnNewPage = title && titleOnNewPage;
            Document.SummaryOnNewPage = summary && summaryOnNewPage;
        });

    public void SetColumnBands(bool on) =>
        Transaction("Column bands", () => { Toggle(BandKind.ColumnHeader, on, 0.25); Toggle(BandKind.ColumnFooter, on, 0.25); });

    private void Toggle(BandKind kind, bool on, double height)
    {
        var existing = Document.Band(kind);
        if (on && existing == null) Document.Bands.Add(new ReportBand { Kind = kind, Height = height });
        if (!on && existing != null) Document.Bands.Remove(existing);
        Document.SortBands();
    }

    /// <summary>Adds a data group (innermost) with its header and footer bands. Returns its 1-based number.</summary>
    public int AddGroup(string expression, bool newPage = false, bool reprintHeader = false)
    {
        var n = Document.Groups.Count + 1;
        Transaction("Add group", () =>
        {
            Document.Groups.Add(new ReportGroup { Expression = expression, NewPage = newPage, ReprintHeader = reprintHeader });
            Document.Bands.Add(new ReportBand { Kind = BandKind.GroupHeader, Index = n, Height = 0.25 });
            Document.Bands.Add(new ReportBand { Kind = BandKind.GroupFooter, Index = n, Height = 0.25 });
            Document.SortBands();
        });
        return n;
    }

    /// <summary>Removes a group and its bands; later groups and resets that referred to them are renumbered.</summary>
    public void RemoveGroup(int number) =>
        Transaction("Remove group", () =>
        {
            Document.Groups.RemoveAt(number - 1);
            Document.Bands.RemoveAll(b => b.Kind is BandKind.GroupHeader or BandKind.GroupFooter && b.Index == number);
            foreach (var b in Document.Bands.Where(b => b.Kind is BandKind.GroupHeader or BandKind.GroupFooter && b.Index > number)) b.Index--;
            void Fix(ReportField f)
            {
                if (f.Reset != ResetScope.Group) return;
                if (f.ResetGroup == number) { f.Reset = ResetScope.Report; f.ResetGroup = 0; }
                else if (f.ResetGroup > number) f.ResetGroup--;
            }
            foreach (var f in Document.Bands.SelectMany(b => b.Objects).OfType<ReportField>()) Fix(f);
            foreach (var v in Document.Variables.Where(v => v.Reset == ResetScope.Group))
            {
                if (v.ResetGroup == number) { v.Reset = ResetScope.Report; v.ResetGroup = 0; }
                else if (v.ResetGroup > number) v.ResetGroup--;
            }
            Document.SortBands();
        });

    /// <summary>Swaps a group with the one inside it (Data Grouping → move down).</summary>
    public void MoveGroupInward(int number)
    {
        if (number < 1 || number >= Document.Groups.Count) return;
        Transaction("Reorder groups", () =>
        {
            (Document.Groups[number - 1], Document.Groups[number]) = (Document.Groups[number], Document.Groups[number - 1]);
            int Swap(int g) => g == number ? number + 1 : g == number + 1 ? number : g;
            foreach (var b in Document.Bands.Where(b => b.Kind is BandKind.GroupHeader or BandKind.GroupFooter)) b.Index = Swap(b.Index);
            foreach (var f in Document.Bands.SelectMany(b => b.Objects).OfType<ReportField>().Where(f => f.Reset == ResetScope.Group)) f.ResetGroup = Swap(f.ResetGroup);
            foreach (var v in Document.Variables.Where(v => v.Reset == ResetScope.Group)) v.ResetGroup = Swap(v.ResetGroup);
            Document.SortBands();
        });
    }

    public static string Describe(ReportObject o) => o switch
    {
        ReportLabel l => $"label \"{l.Text}\"",
        ReportField f => $"field {f.Expression}",
        ReportLine => "line",
        ReportShape => "shape",
        ReportPicture => "picture",
        _ => "object",
    };

    // ---- Quick report ----------------------------------------------------------------------------

    /// <summary>
    /// Report → Quick Report: a columnar layout (field names as headings in the page header, one row per record) or a
    /// form layout (one field per line with its name), with the date and page number in the page footer.
    /// </summary>
    public static ReportDocument QuickReport(string alias, IReadOnlyList<(string Name, char Type, int Width)> fields, bool columnar = true, string? title = null)
    {
        var r = ReportDocument.NewReport();
        var header = r.Band(BandKind.PageHeader)!;
        var detail = r.Band(BandKind.Detail)!;
        var footer = r.Band(BandKind.PageFooter)!;
        const double row = 0.1875;
        double WidthOf(char type, int width) => type switch
        {
            'D' => 0.9, 'T' => 1.6, 'L' => 0.4, 'M' => 2.5, 'N' or 'F' or 'I' or 'Y' or 'B' => Math.Max(0.6, Math.Min(1.4, width * 0.085)),
            _ => Math.Max(0.4, Math.Min(3, width * 0.085)),
        };
        string Heading(string name) => char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant();
        TextAlign AlignOf(char type) => type is 'N' or 'F' or 'I' or 'Y' or 'B' ? TextAlign.Right : TextAlign.Left;
        var y = 0.0;
        if (title != null)
        {
            header.Objects.Add(new ReportLabel { Text = title, Left = 0, Top = 0, Width = 5, Height = 0.3, FontSize = 14, Bold = true });
            y = 0.35;
        }
        if (columnar)
        {
            var x = 0.0;
            foreach (var (name, type, width) in fields)
            {
                var w = WidthOf(type, width);
                if (x + w > r.PrintableWidth) break;
                header.Objects.Add(new ReportLabel { Text = Heading(name), Left = x, Top = y, Width = w, Height = row, Bold = true, Align = AlignOf(type) });
                detail.Objects.Add(new ReportField { Expression = $"{alias}.{name.ToLowerInvariant()}", Left = x, Top = 0, Width = w, Height = row, Align = AlignOf(type), Stretch = type == 'M' });
                x += w + 0.125;
            }
            header.Objects.Add(new ReportLine { Left = 0, Top = y + row + 0.02, Width = Math.Min(r.PrintableWidth, Math.Max(0.5, x - 0.125)) });
            header.Height = y + row + 0.08;
            detail.Height = row + 0.02;
        }
        else
        {
            var labelW = Math.Min(1.5, 0.12 + fields.Select(f => f.Name.Length).DefaultIfEmpty(4).Max() * 0.08);
            header.Height = Math.Max(0.25, y + 0.05);
            var top = 0.0;
            foreach (var (name, type, width) in fields)
            {
                detail.Objects.Add(new ReportLabel { Text = Heading(name) + ":", Left = 0, Top = top, Width = labelW, Height = row, Bold = true });
                detail.Objects.Add(new ReportField { Expression = $"{alias}.{name.ToLowerInvariant()}", Left = labelW + 0.1, Top = top, Width = WidthOf(type, width), Height = row, Stretch = type == 'M' });
                top += row + 0.03;
            }
            detail.Height = top + 0.15;
        }
        footer.Objects.Add(new ReportField { Expression = "DATE()", Left = 0, Top = 0.15, Width = 1.2, Height = row });
        footer.Objects.Add(new ReportField { Expression = "\"Page \" + TRANSFORM(_PAGENO)", Left = r.PrintableWidth - 1.2, Top = 0.15, Width = 1.2, Height = row, Align = TextAlign.Right });
        return r;
    }
}
