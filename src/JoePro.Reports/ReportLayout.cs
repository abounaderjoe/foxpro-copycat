using JoePro.Core;
using JoePro.Data;
using JoePro.Documents.Reports;
using JoePro.Language;
using JoePro.Runtime;
using JoePro.Runtime.Builtins;

namespace JoePro.Reports;

/// <summary>Hooks for object-assisted reporting (a VFP 9 ReportListener, or host code).</summary>
public interface IReportListener
{
    void BeforeReport();
    void AfterReport();
    void BeforeBand(ReportBand band);
    void AfterBand(ReportBand band);
    /// <summary>Called for each field before it is laid out; may change the text.</summary>
    string EvaluateContents(int objectId, ReportField field, string text);
    /// <summary>Called for each item before it is added to the page; false leaves it out.</summary>
    bool Render(int objectId, PageItem item);
    /// <summary>The page number changed (a new page started).</summary>
    void PageStarted(int pageNo);
}

public sealed class LayoutOptions
{
    /// <summary>Folder of the report file (pictures with relative names are found there).</summary>
    public string? BaseDirectory { get; init; }
    public string? Heading { get; init; }
    public bool Plain { get; init; }
    public bool SummaryOnly { get; init; }
    public IReportListener? Listener { get; init; }
    /// <summary>The page count from a first pass (for _PAGETOTAL); 0 when unknown.</summary>
    public int PageTotal { get; init; }
}

/// <summary>
/// The layout engine: runs a report over records and produces pages. It implements the VFP band model — title,
/// page and column headers and footers, nested data groups (new page/column, reprint header, reset page number,
/// minimum space), detail header/footer and target-alias detail bands, summary — with calculated fields and report
/// variables (reset by report, page, column or group), Print When, suppression of repeated values, stretching
/// fields with floating objects below them, "remove line if blank", and multiple columns down or across.
/// </summary>
public sealed class ReportLayout
{
    private const double Pt = 72.0;
    private readonly ReportDocument _doc;
    private readonly Interpreter _rt;
    private readonly LayoutOptions _opt;
    private readonly RenderedReport _result;
    private readonly Dictionary<string, Expr> _parsed = new();
    private readonly Dictionary<ReportObject, int> _ids = new(ReferenceEqualityComparer.Instance);

    // Calculations
    private readonly Dictionary<ReportField, Accumulator> _fieldCalcs = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ReportVariable, Accumulator> _varCalcs = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ReportVariable, Value> _varValues = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ReportObject, string> _lastPrinted = new(ReferenceEqualityComparer.Instance);

    // Page state
    private ReportPage? _page;
    private int _pageNo;
    private bool _resetPageNumber;
    private double _y, _columnTop;
    private int _column;
    private double _rowHeight; // across columns
    private bool _inPageBand;
    private Value[] _groupKeys = [];

    public ReportLayout(ReportDocument doc, Interpreter rt, LayoutOptions? options = null)
    {
        _doc = doc;
        _rt = rt;
        _opt = options ?? new LayoutOptions();
        _result = new RenderedReport { Document = doc };
        var id = 0;
        foreach (var b in doc.Bands)
            foreach (var o in b.Objects) _ids[o] = id++;
        foreach (var f in doc.Bands.SelectMany(b => b.Objects).OfType<ReportField>().Where(f => f.Calculate != CalcType.None))
            _fieldCalcs[f] = new Accumulator(f.Calculate);
        foreach (var v in doc.Variables) _varCalcs[v] = new Accumulator(v.Calculate);
    }

    public int ObjectId(ReportObject o) => _ids.TryGetValue(o, out var i) ? i : -1;

    private double PaperW => _doc.PaperWidth * Pt;
    private double PaperH => _doc.PaperHeight * Pt;
    private double LeftX => _doc.LeftMargin * Pt;
    private double TopY => _doc.TopMargin * Pt;
    private double BottomY => PaperH - _doc.BottomMargin * Pt;
    private double ColumnW => _doc.EffectiveColumnWidth * Pt;
    private double ColumnX(int c) => LeftX + c * (ColumnW + _doc.ColumnSpacing * Pt);

    private ReportBand? Band(BandKind k, int index = 0) => _doc.Bands.FirstOrDefault(b => b.Kind == k && (index == 0 || b.Index == index));
    private double H(BandKind k) => (Band(k)?.Height ?? 0) * Pt;
    /// <summary>The lowest point detail-type bands can reach on this page (above the column and page footers).</summary>
    private double ContentBottom => BottomY - H(BandKind.PageFooter) - H(BandKind.ColumnFooter);
    private bool Across => _doc.Columns > 1 && _doc.ColumnOrder == ColumnOrder.Across;

    // ================================================================================
    // Expressions
    // ================================================================================

    private Value Eval(string expression)
    {
        if (!_parsed.TryGetValue(expression, out var e))
        {
            try { e = Parser.ParseExpression(expression); }
            catch (CompileException ex) { throw new VfpException(10, $"Report expression '{expression}': {ex.Message}"); }
            _parsed[expression] = e;
        }
        return _rt.Evaluate(e);
    }

    private bool True(string? expression) => expression == null || Eval(expression) is { Kind: ValueKind.Logical } v && v.AsBool;

    private string Text(Value v, string? format)
    {
        if (v.IsNull) return ".NULL.";
        if (!string.IsNullOrWhiteSpace(format)) return Library.Transform(v, format, _rt.Options);
        return v.Kind switch
        {
            ValueKind.Character => v.AsString.TrimEnd(),
            ValueKind.Number or ValueKind.Currency => Library.TransformDefault(v, _rt.Options).Trim(),
            ValueKind.Binary => "",
            ValueKind.Object => "(Object)",
            _ => Formatter.ToDisplay(v, _rt.Options).Trim(),
        };
    }

    // ================================================================================
    // Calculations
    // ================================================================================

    private static bool ResetsAt(ResetScope scope, int group, ResetScope at, int atGroup) =>
        scope == at && (at != ResetScope.Group || group >= atGroup);

    /// <summary>Resets the calculations of a scope (for groups: group <paramref name="group"/> and the groups inside it).</summary>
    private void Reset(ResetScope scope, int group = 0)
    {
        foreach (var (f, acc) in _fieldCalcs)
            if (ResetsAt(f.Reset, f.ResetGroup, scope, group)) acc.Reset();
        foreach (var v in _doc.Variables)
        {
            if (!ResetsAt(v.Reset, v.ResetGroup, scope, group) && !(scope == ResetScope.Report)) continue;
            _varCalcs[v].Reset();
            var initial = v.Initial != null ? Eval(v.Initial) : v.Calculate == CalcType.None ? Value.EmptyString : Value.Number(0);
            _varValues[v] = initial;
            _rt.SetVariable(v.Name, initial);
        }
    }

    /// <summary>Adds the current record to every calculation (before its detail band prints, as VFP does).</summary>
    private void Accumulate()
    {
        foreach (var v in _doc.Variables)
        {
            Value value;
            if (v.Calculate == CalcType.None) value = Eval(v.Value);
            else
            {
                _varCalcs[v].Add(Eval(v.Value));
                value = _varCalcs[v].Result(v.Initial != null ? Eval(v.Initial) : Value.Number(0));
            }
            _varValues[v] = value;
            _rt.SetVariable(v.Name, value);
        }
        foreach (var (f, acc) in _fieldCalcs) acc.Add(Eval(f.Expression));
    }

    // ================================================================================
    // Running
    // ================================================================================

    /// <summary>Lays out the report over the given records of the current work area (all of them when null).</summary>
    public RenderedReport Run(WorkArea? area, IReadOnlyList<int>? records)
    {
        var recs = records ?? (area is { InUse: true } ? _rt.RecordsInScope(area, new Scope("ALL")) : []);
        _rt.SetVariable("_PAGENO", Value.Number(0));
        _rt.SetVariable("_PAGETOTAL", Value.Number(_opt.PageTotal));
        foreach (var v in _doc.Variables) _rt.SetVariable(v.Name, Value.Number(0));
        _opt.Listener?.BeforeReport();
        Reset(ResetScope.Report);
        _groupKeys = new Value[_doc.Groups.Count];

        var title = Band(BandKind.Title);
        if (title != null && _doc.TitleOnNewPage)
        {
            StartPage(withHeaders: false);
            Place(title, fullWidth: true);
            _page = null; // the title page has no page footer
        }
        StartPage(withHeaders: true, title: _doc.TitleOnNewPage ? null : title);

        if (recs.Count == 0 && area is not { InUse: true })
        {
            // No table: the detail band prints once (reports of variables or constants).
            Accumulate();
            PrintDetail();
        }
        for (int i = 0; i < recs.Count; i++)
        {
            area!.Go(recs[i]);
            var keys = _doc.Groups.Select(g => Eval(g.Expression)).ToArray();
            var changed = i == 0 ? 0 : FirstChanged(_groupKeys, keys);
            if (i > 0 && changed >= 0 && changed < keys.Length)
            {
                // Footers print with the last record of the group still current.
                area.Go(recs[i - 1]);
                PrintGroupFooters(changed);
                area.Go(recs[i]);
            }
            _groupKeys = keys;
            if (i == 0) PrintGroupHeaders(0, first: true);
            else if (changed >= 0 && changed < keys.Length) PrintGroupHeaders(changed, first: false);
            Accumulate();
            if (!_opt.SummaryOnly) PrintDetail(area);
        }
        if (recs.Count > 0) PrintGroupFooters(0);

        var summary = Band(BandKind.Summary);
        if (summary != null)
        {
            FlushRow();
            if (_doc.SummaryOnNewPage)
            {
                FinishPage();
                StartPage(withHeaders: _doc.SummaryWithPageHeader);
                if (!_doc.SummaryWithPageHeader) _suppressFooter = true;
            }
            Place(summary, fullWidth: true, flowing: true);
        }
        FinishPage();
        _opt.Listener?.AfterReport();
        foreach (var v in _doc.Variables.Where(v => v.Release)) _rt.ExecuteCommand("RELEASE " + v.Name);
        var fonts = _doc.Bands.SelectMany(b => b.Objects).OfType<ReportTextObject>().Select(t => t.FontName ?? _doc.FontName).Append(_doc.FontName).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fonts)
        {
            var used = Fonts.Resolve(new FontSpec(f, 10)).Used;
            if (!used.Equals(f, StringComparison.OrdinalIgnoreCase)) _result.FontSubstitutions[f] = used;
        }
        return _result;
    }

    private bool _suppressFooter;

    private static int FirstChanged(Value[] before, Value[] after)
    {
        for (int g = 0; g < after.Length; g++)
            if (!SameKey(before[g], after[g])) return g;
        return after.Length;
    }

    private static bool SameKey(Value a, Value b)
    {
        if (a.Kind != b.Kind) return false;
        return a.Kind switch
        {
            ValueKind.Character => a.AsString.TrimEnd() == b.AsString.TrimEnd(),
            ValueKind.Number or ValueKind.Currency => a.AsNumber == b.AsNumber,
            ValueKind.Logical => a.AsBool == b.AsBool,
            ValueKind.Date or ValueKind.DateTime => a.JulianMs == b.JulianMs,
            ValueKind.Null => true,
            _ => Equals(a, b),
        };
    }

    /// <summary>Group headers from group <paramref name="from"/> (0-based) inward.</summary>
    private void PrintGroupHeaders(int from, bool first)
    {
        for (int g = from; g < _doc.Groups.Count; g++)
        {
            var group = _doc.Groups[g];
            var header = Band(BandKind.GroupHeader, g + 1);
            if (!first || g > from)
            {
                if (group.NewPage && !PageIsEmpty) { FinishPage(); if (group.ResetPageNumber) _resetPageNumber = true; StartPage(withHeaders: true, reprintGroups: g); }
                else if (group.NewColumn && !PageIsEmpty) NextColumn(reprintGroups: g);
            }
            else if (group.ResetPageNumber && !first) _resetPageNumber = true;
            if (group.MinSpace > 0 && _y + group.MinSpace * Pt > ContentBottom && !PageIsEmpty)
            {
                FinishPage();
                StartPage(withHeaders: true, reprintGroups: g);
            }
            Reset(ResetScope.Group, g + 1);
            _lastPrinted.Clear();
            if (header != null) Place(header, flowing: true, keepWithNext: true);
            if (Band(BandKind.DetailHeader) is { TargetAlias: null } dh && g == _doc.Groups.Count - 1) Place(dh, flowing: true);
        }
        if (_doc.Groups.Count == 0 && first && Band(BandKind.DetailHeader) is { TargetAlias: null } dh0) Place(dh0, flowing: true);
    }

    /// <summary>Group footers from the innermost group out to group <paramref name="to"/> (0-based).</summary>
    private void PrintGroupFooters(int to)
    {
        if (Band(BandKind.DetailFooter) is { TargetAlias: null } df && (_doc.Groups.Count == 0 ? to == 0 : true)) Place(df, flowing: true);
        for (int g = _doc.Groups.Count - 1; g >= to; g--)
            if (Band(BandKind.GroupFooter, g + 1) is { } footer)
            {
                FlushRow();
                Place(footer, flowing: true);
            }
        FlushRow();
    }

    private bool PageIsEmpty => _page == null || _y <= _columnTop + 0.01;

    /// <summary>The detail band(s) for the current record; a detail band with a target alias prints once per related child record.</summary>
    private void PrintDetail(WorkArea? area = null)
    {
        foreach (var detail in _doc.Bands.Where(b => b.Kind == BandKind.Detail).OrderBy(b => b.Index))
        {
            if (detail.TargetAlias is not { Length: > 0 } alias || area == null)
            {
                Place(detail, flowing: true, across: Across);
                continue;
            }
            var child = _rt.Session.FindAlias(alias) ?? throw new VfpException(13, $"Alias '{alias.ToUpperInvariant()}' is not found (target alias of detail band {detail.Index}).");
            var header = Band(BandKind.DetailHeader, detail.Index);
            var footer = Band(BandKind.DetailFooter, detail.Index);
            if (header != null) Place(header, flowing: true);
            foreach (var _ in ChildRecords(area, child)) Place(detail, flowing: true, across: Across);
            FlushRow();
            if (footer != null) Place(footer, flowing: true);
        }
    }

    /// <summary>The records of <paramref name="child"/> related to the current parent record (through SET RELATION).</summary>
    private IEnumerable<int> ChildRecords(WorkArea parent, WorkArea child)
    {
        var relation = parent.Relations.FirstOrDefault(r => r.Child == child);
        if (relation.Child == null)
        {
            // No relation: every record of the target alias.
            foreach (var r in _rt.RecordsInScope(child, new Scope("ALL"))) { child.Go(r); yield return r; }
            yield break;
        }
        var key = relation.Expr.Evaluate(parent);
        if (child.Eof) yield break;
        if (child.Order == null) { yield return child.RecNo; yield break; }
        var list = new List<int>();
        while (!child.Eof && SameKey(Normalize(_rt.EvalInArea(child, child.Order.Expression)), Normalize(key)))
        {
            list.Add(child.RecNo);
            child.Skip();
        }
        foreach (var r in list) { child.Go(r); yield return r; }
    }

    private static Value Normalize(Value v) => v.Kind == ValueKind.Character ? Value.String(v.AsString.TrimEnd()) : v;

    // ================================================================================
    // Pages and columns
    // ================================================================================

    private void StartPage(bool withHeaders, ReportBand? title = null, int reprintGroups = -1)
    {
        _page = new ReportPage { Width = PaperW, Height = PaperH };
        _pageNo = _resetPageNumber || _pageNo == 0 ? 1 : _pageNo + 1;
        _resetPageNumber = false;
        _page.Number = _pageNo;
        _result.Pages.Add(_page);
        _rt.SetVariable("_PAGENO", Value.Number(_pageNo));
        _opt.Listener?.PageStarted(_pageNo);
        _y = TopY;
        _column = 0;
        _rowHeight = 0;
        _lastPrinted.Clear();
        Reset(ResetScope.Page);
        Reset(ResetScope.Column);
        if (_opt.Heading is { Length: > 0 } heading)
        {
            var font = new FontSpec(_doc.FontName, _doc.FontSize, Bold: true);
            var (lh, asc) = Fonts.Metrics(font);
            AddItem(-1, new TextItem { X = LeftX, Y = _y, W = _doc.PrintableWidth * Pt, H = lh, Lines = [heading], Font = font, LineHeight = lh, Ascent = asc, Align = TextAlign.Center });
            _y += lh * 1.5;
        }
        if (title != null) Place(title, fullWidth: true);
        if (withHeaders && !(_opt.Plain && _result.Pages.Count > 1) && Band(BandKind.PageHeader) is { } ph)
        {
            _inPageBand = true;
            Place(ph, fullWidth: true);
            _inPageBand = false;
        }
        _columnTop = _y;
        if (withHeaders && Band(BandKind.ColumnHeader) is { } ch) Place(ch);
        _columnTop = Math.Max(_columnTop, _y);
        if (reprintGroups >= 0) ReprintGroupHeaders(reprintGroups);
    }

    /// <summary>After a page or column break inside groups: group headers marked "reprint on each page" print again.</summary>
    private void ReprintGroupHeaders(int below)
    {
        for (int g = 0; g < Math.Min(below, _doc.Groups.Count); g++)
            if (_doc.Groups[g].ReprintHeader && Band(BandKind.GroupHeader, g + 1) is { } header) Place(header, flowing: false);
    }

    private void FinishPage()
    {
        if (_page == null) return;
        FlushRow();
        if (Band(BandKind.ColumnFooter) is { } cf)
        {
            var columns = Across ? 1 : _column + 1;
            for (int c = 0; c < columns; c++)
            {
                var saved = (_y, _column);
                _column = c;
                _y = BottomY - H(BandKind.PageFooter) - H(BandKind.ColumnFooter);
                Place(cf);
                (_y, _column) = saved;
            }
        }
        if (Band(BandKind.PageFooter) is { } pf && !_suppressFooter)
        {
            _y = BottomY - H(BandKind.PageFooter);
            _inPageBand = true;
            Place(pf, fullWidth: true);
            _inPageBand = false;
        }
        _suppressFooter = false;
        _page = null;
    }

    private void NextColumn(int reprintGroups)
    {
        if (_doc.Columns > 1 && !Across && _column < _doc.Columns - 1)
        {
            _column++;
            _y = _columnTop;
            Reset(ResetScope.Column);
            _lastPrinted.Clear();
            if (Band(BandKind.ColumnHeader) is { } ch)
            {
                // Column headers sit just under the page header.
                var saved = _y;
                _y = _columnTop - H(BandKind.ColumnHeader);
                Place(ch);
                _y = Math.Max(saved, _y);
            }
            ReprintGroupHeaders(reprintGroups);
            return;
        }
        FinishPage();
        StartPage(withHeaders: true, reprintGroups: reprintGroups);
    }

    /// <summary>Ends a row of labels printed across.</summary>
    private void FlushRow()
    {
        if (!Across || _column == 0 && _rowHeight == 0) return;
        _y += _rowHeight;
        _column = 0;
        _rowHeight = 0;
    }

    // ================================================================================
    // Bands
    // ================================================================================

    private sealed record Placed(ReportObject Obj, double Top, double Height, List<string>? Lines, bool Printed, bool Blank);

    /// <summary>
    /// Lays out one band at the current position: evaluates its objects, stretches fields, floats the objects below
    /// them, removes blank lines, starts a new page or column when it does not fit, and advances.
    /// </summary>
    private void Place(ReportBand band, bool fullWidth = false, bool flowing = false, bool across = false, bool keepWithNext = false)
    {
        if (!across) FlushRow();
        if (band.OnEntry != null) Eval(band.OnEntry);
        _opt.Listener?.BeforeBand(band);
        var (placed, height) = Measure(band);
        if (flowing && _page != null && !_inPageBand)
        {
            var need = height + (keepWithNext ? NextDetailHeight() : 0);
            if (_y + need > ContentBottom + 0.01 && !PageIsEmpty)
            {
                if (across && _column > 0) FlushRow();
                if (_y + need > ContentBottom + 0.01 && !PageIsEmpty)
                {
                    NextColumn(reprintGroups: _doc.Groups.Count);
                    // Values that depend on the page (like _PAGENO) are evaluated again on the new page.
                    (placed, height) = Measure(band);
                }
            }
        }
        var x = fullWidth || (_doc.Columns == 1) ? LeftX : ColumnX(_column);
        if (across) x = ColumnX(_column);
        foreach (var p in placed.Where(p => p.Printed))
            Emit(p, x, _y);
        if (across)
        {
            _rowHeight = Math.Max(_rowHeight, height);
            _column++;
            if (_column >= _doc.Columns) FlushRow();
        }
        else _y += height;
        _opt.Listener?.AfterBand(band);
        if (band.OnExit != null) Eval(band.OnExit);
    }

    private double NextDetailHeight() => (Band(BandKind.Detail)?.Height ?? 0) * Pt;

    private (List<Placed> Items, double Height) Measure(ReportBand band)
    {
        var bandH = band.Height * Pt;
        var list = new List<Placed>();
        foreach (var o in band.Objects.OrderBy(o => o.Top).ThenBy(o => o.Left))
        {
            var top = o.Top * Pt;
            var h = o.Height * Pt;
            var printed = True(o.PrintWhen);
            List<string>? lines = null;
            var blank = false;
            if (printed && o is ReportTextObject t)
            {
                var text = t switch
                {
                    ReportLabel l => l.Text,
                    ReportField f => FieldText(f),
                    _ => "",
                };
                if (!o.PrintRepeated)
                {
                    if (_lastPrinted.TryGetValue(o, out var last) && last == text) printed = false;
                    else _lastPrinted[o] = text;
                }
                blank = text.Trim().Length == 0;
                var font = FontOf(t);
                var (lh, _) = Fonts.Metrics(font);
                lines = Fonts.Wrap(font, text, o.Width * Pt);
                if (o.Stretch) h = Math.Max(h, lines.Count * lh);
                else
                {
                    var fit = Math.Max(1, (int)Math.Floor((h + 0.5) / lh));
                    if (lines.Count > fit) lines = lines.Take(fit).ToList();
                }
            }
            if (!printed) blank = true;
            list.Add(new Placed(o, top, h, lines, printed, blank));
        }
        // Floating: an object moves down by the growth of the objects above it; removed blank lines pull it up.
        var result = new List<Placed>();
        double maxGrowth = 0;
        foreach (var p in list)
        {
            double shift = 0;
            if (p.Obj.Float != FloatMode.Top)
                foreach (var a in result)
                {
                    var origBottom = a.Obj.Top * Pt + a.Obj.Height * Pt;
                    if (origBottom <= p.Obj.Top * Pt + 0.5) shift = Math.Max(shift, a.Top + a.Height - origBottom);
                }
            var removed = RemovedAbove(list, p);
            var placedItem = p with { Top = p.Top + shift - removed };
            result.Add(placedItem);
            maxGrowth = Math.Max(maxGrowth, placedItem.Top + placedItem.Height - (p.Obj.Top * Pt + p.Obj.Height * Pt));
        }
        var totalRemoved = list.Where(p => IsRemoved(list, p)).Sum(p => p.Obj.Height * Pt);
        var height = band.ConstantHeight ? bandH : Math.Max(0, bandH + Math.Max(0, maxGrowth) - totalRemoved);
        if (!band.ConstantHeight) height = Math.Max(height, result.Where(r => r.Printed).Select(r => r.Top + r.Height).DefaultIfEmpty(0).Max());
        var growth = height - bandH;
        for (int i = 0; i < result.Count; i++)
        {
            var p = result[i];
            if (p.Obj.Float == FloatMode.Bottom) result[i] = p with { Top = p.Obj.Top * Pt + growth };
            if (p.Obj is ReportLine { Vertical: true } or ReportShape && p.Obj.Stretch && growth > 0) result[i] = result[i] with { Height = p.Height + growth };
        }
        return (result, height);
    }

    private static bool IsRemoved(List<Placed> all, Placed p)
    {
        if (!p.Obj.RemoveLineIfBlank || !p.Blank) return false;
        var top = p.Obj.Top;
        var bottom = p.Obj.Top + p.Obj.Height;
        // Only when nothing else printed shares the line.
        return !all.Any(o => !ReferenceEquals(o, p) && !o.Blank && o.Obj.Top < bottom && o.Obj.Top + o.Obj.Height > top);
    }

    private static double RemovedAbove(List<Placed> all, Placed p) =>
        all.Where(o => !ReferenceEquals(o, p) && o.Obj.Top + o.Obj.Height <= p.Obj.Top + 0.0001 && IsRemoved(all, o)).Sum(o => o.Obj.Height * Pt);

    private string FieldText(ReportField f)
    {
        var value = f.Calculate != CalcType.None ? _fieldCalcs[f].Result(Value.Number(0)) : Eval(f.Expression);
        var text = Text(value, f.Format);
        // Pictures pad numbers to their width; the alignment places them instead.
        text = f.Align == TextAlign.Left ? text.TrimEnd() : text.Trim();
        return _opt.Listener?.EvaluateContents(ObjectId(f), f, text) ?? text;
    }

    private FontSpec FontOf(ReportTextObject t) =>
        new(t.FontName ?? _doc.FontName, t.FontSize ?? _doc.FontSize, t.Bold, t.Italic, t.Underline, t.Strikeout);

    private void Emit(Placed p, double x, double y)
    {
        var o = p.Obj;
        var id = ObjectId(o);
        var ix = x + o.Left * Pt;
        var iy = y + p.Top;
        var w = o.Width * Pt;
        switch (o)
        {
            case ReportTextObject t:
            {
                if (p.Lines == null) return;
                var font = FontOf(t);
                var (lh, asc) = Fonts.Metrics(font);
                AddItem(id, new TextItem
                {
                    X = ix, Y = iy, W = w, H = p.Height, Lines = p.Lines, Font = font, LineHeight = lh, Ascent = asc,
                    Color = t.ForeColor ?? 0, Back = t.BackColor, Align = t.Align,
                });
                break;
            }
            case ReportLine l:
                AddItem(id, new LineItem { X = ix, Y = iy, W = l.Vertical ? 0 : w, H = l.Vertical ? p.Height : 0, Vertical = l.Vertical, PenWidth = l.PenWidth, Style = l.PenStyle, Color = l.Color ?? 0 });
                break;
            case ReportShape s:
                AddItem(id, new RectItem { X = ix, Y = iy, W = w, H = p.Height, Curvature = s.Curvature, PenWidth = s.PenWidth, Style = s.PenStyle, LineColor = s.PenStyle == PenStyle.None ? null : s.LineColor ?? 0, FillColor = s.FillColor });
                break;
            case ReportPicture pic:
                if (PictureData(pic) is { } data)
                    AddItem(id, new ImageItem { X = ix, Y = iy, W = w, H = p.Height, Data = data, Scale = pic.Scale });
                break;
        }
    }

    private void AddItem(int id, PageItem item)
    {
        item.ObjectId = id;
        if (_page == null) return;
        if (_opt.Listener != null && id >= 0 && !_opt.Listener.Render(id, item)) return;
        _page.Items.Add(item);
    }

    private byte[]? PictureData(ReportPicture pic)
    {
        try
        {
            Value v = pic.Source switch
            {
                PictureSource.File => Value.String(pic.Value),
                _ => Eval(pic.Value),
            };
            if (v.Kind == ValueKind.Binary) return v.AsBinary.Length > 0 ? v.AsBinary : null;
            if (v.Kind != ValueKind.Character) return null;
            var name = v.AsString.Trim().Replace('\\', Path.DirectorySeparatorChar);
            if (name.Length == 0) return null;
            var candidates = new List<string>();
            if (Path.IsPathRooted(name)) candidates.Add(name);
            if (_opt.BaseDirectory != null) candidates.Add(Path.Combine(_opt.BaseDirectory, name));
            candidates.Add(Path.Combine(_rt.Options.Default_, name));
            foreach (var c in candidates)
                if (DataSession.FindIgnoringCase(c) is { } found) return File.ReadAllBytes(found);
            return null;
        }
        catch (VfpException) { return null; }
    }
}
