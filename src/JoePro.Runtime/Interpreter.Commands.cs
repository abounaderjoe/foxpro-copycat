using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using JoePro.Core;
using JoePro.Data;
using JoePro.Language;
using JoePro.Legacy.Formats;
using JoePro.Runtime.Sync;

namespace JoePro.Runtime;

/// <summary>
/// The xBase commands added by the command coverage sweep: SORT, TOTAL, JOIN, REPLACE FROM ARRAY, memo files,
/// SAVE TO/RESTORE FROM, ACCEPT/INPUT/GETEXPR, ON KEY LABEL/SHUTDOWN/ESCAPE, KEYBOARD, RUN, DIR, TYPE and the
/// LIST/DISPLAY variants.
/// </summary>
public sealed partial class Interpreter
{
    /// <summary>Host hook for ACCEPT and INPUT: prompt → the line typed, or null when there is no console.</summary>
    public Func<string, string?>? ReadLine { get; set; }

    /// <summary>ON KEY LABEL handlers: key label (upper case, e.g. "CTRL+F2") → command.</summary>
    public Dictionary<string, string> KeyLabels { get; } = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<Dictionary<string, string>> _keyLabelStack = new();
    /// <summary>ON SHUTDOWN, ON ESCAPE, ON KEY (any key), ON PAGE and ON READERROR commands.</summary>
    private readonly Dictionary<string, string> _onCommands = new(StringComparer.OrdinalIgnoreCase);
    private bool _shuttingDown;

    /// <summary>Characters stuffed by KEYBOARD, read by INKEY(), ACCEPT and INPUT.</summary>
    public Queue<int> KeyboardBuffer { get; } = new();
    public int LastKey { get; internal set; }

    /// <summary>ON(cCommand [, cKeyLabel]): the command set for an ON event.</summary>
    internal string OnCommand(string what, string? keyLabel)
    {
        what = what.Trim().ToUpperInvariant();
        if (what == "ERROR") return OnErrorCommand ?? "";
        if (what == "KEY" && keyLabel != null) return KeyLabels.GetValueOrDefault(NormalizeKeyLabel(keyLabel)) ?? "";
        return _onCommands.GetValueOrDefault(what) ?? "";
    }

    /// <summary>Runs the ON KEY LABEL command for a key the UI host saw; true if one ran.</summary>
    public bool RunKeyLabel(string label)
    {
        if (!KeyLabels.TryGetValue(NormalizeKeyLabel(label), out var cmd)) return false;
        ExecuteCommand(cmd);
        return true;
    }

    public static string NormalizeKeyLabel(string label) =>
        string.Join("+", label.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.ToUpperInvariant() switch { "CONTROL" => "CTRL", "ALTERNATE" => "ALT", var x => x }));

    /// <summary>QUIT with ON SHUTDOWN set: runs the handler instead; quitting goes ahead only if the handler QUITs.</summary>
    private bool RunShutdownHandler()
    {
        if (_shuttingDown || !_onCommands.TryGetValue("SHUTDOWN", out var cmd)) return false;
        _shuttingDown = true;
        try { ExecuteCommand(cmd); }
        finally { _shuttingDown = false; }
        return true;
    }

    private void ExecOnCommand(string rest)
    {
        var words = rest.Trim();
        var sp = words.IndexOf(' ');
        var evt = (sp < 0 ? words : words[..sp]).ToUpperInvariant();
        var cmd = sp < 0 ? "" : words[(sp + 1)..].Trim();
        if (evt == "KEY" && cmd.StartsWith("LABEL", StringComparison.OrdinalIgnoreCase))
        {
            var after = cmd[5..].Trim();
            var ksp = after.IndexOf(' ');
            var label = NormalizeKeyLabel(ksp < 0 ? after : after[..ksp]);
            var command = ksp < 0 ? "" : after[(ksp + 1)..].Trim();
            if (command.Length == 0) KeyLabels.Remove(label);
            else KeyLabels[label] = command;
            Ui?.KeyLabelsChanged();
            return;
        }
        if (evt == "KEY" && cmd.StartsWith('='))
        {
            // ON KEY = nKey: the legacy form for a key code; kept like ON KEY LABEL under the code.
            var m = System.Text.RegularExpressions.Regex.Match(cmd, @"^=\s*(\d+)\s*(.*)$");
            if (m.Success)
            {
                if (m.Groups[2].Value.Length == 0) KeyLabels.Remove("#" + m.Groups[1].Value);
                else KeyLabels["#" + m.Groups[1].Value] = m.Groups[2].Value;
            }
            return;
        }
        if (cmd.Length == 0) _onCommands.Remove(evt);
        else _onCommands[evt] = cmd;
    }

    private void PushPopKey(bool push, string rest)
    {
        if (push)
        {
            _keyLabelStack.Push(new Dictionary<string, string>(KeyLabels, StringComparer.OrdinalIgnoreCase));
            if (rest.Trim().Equals("CLEAR", StringComparison.OrdinalIgnoreCase)) KeyLabels.Clear();
        }
        else
        {
            KeyLabels.Clear();
            if (rest.Trim().Equals("ALL", StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<string, string>? first = null;
                while (_keyLabelStack.Count > 0) first = _keyLabelStack.Pop();
                if (first != null) foreach (var kv in first) KeyLabels[kv.Key] = kv.Value;
            }
            else if (_keyLabelStack.Count > 0) foreach (var kv in _keyLabelStack.Pop()) KeyLabels[kv.Key] = kv.Value;
        }
        Ui?.KeyLabelsChanged();
    }

    /// <summary>KEYBOARD cText [PLAIN] [CLEAR]: puts characters in the keyboard buffer ({key label} names are kept as label codes).</summary>
    private void ExecKeyboard(string rest)
    {
        var m = System.Text.RegularExpressions.Regex.Match(rest.Trim(), @"^(.*?)(\s+PLAIN)?(\s+CLEAR)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Groups[3].Success) KeyboardBuffer.Clear();
        var expr = m.Groups[1].Value.Trim();
        if (expr.Length == 0) return;
        var text = Eval(Parser.ParseExpression(expr)).AsString;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '{' && text.IndexOf('}', i) is var close && close > i)
            {
                var label = text[(i + 1)..close].ToUpperInvariant();
                KeyboardBuffer.Enqueue(label switch { "ENTER" => 13, "TAB" => 9, "ESC" or "ESCAPE" => 27, "BACKSPACE" => 127, "SPACEBAR" => 32, _ => 0 });
                i = close;
            }
            else KeyboardBuffer.Enqueue(text[i]);
        }
    }

    /// <summary>INKEY(): the next key in the keyboard buffer, or 0.</summary>
    internal int InKey()
    {
        if (KeyboardBuffer.Count == 0) return 0;
        LastKey = KeyboardBuffer.Dequeue();
        return LastKey;
    }

    private string ReadInputLine(string prompt)
    {
        if (KeyboardBuffer.Count > 0)
        {
            var sb = new StringBuilder();
            while (KeyboardBuffer.Count > 0)
            {
                var k = KeyboardBuffer.Dequeue();
                LastKey = k;
                if (k == 13) break;
                sb.Append((char)k);
            }
            return sb.ToString();
        }
        return ReadLine?.Invoke(prompt) ?? "";
    }

    private bool ExecCommandStmt(Stmt s)
    {
        switch (s)
        {
            case SortStmt so: ExecSort(so); return true;
            case TotalStmt tt: ExecTotal(tt); return true;
            case JoinStmt js: ExecJoin(js); return true;
            case ReplaceFromArrayStmt ra: ExecReplaceFromArray(ra); return true;
            case MemoFileStmt mf: ExecMemoFile(mf); return true;
            case InputStmt inp:
            {
                var prompt = inp.Prompt != null ? Formatter.ToDisplay(Eval(inp.Prompt), Options) : "";
                if (prompt.Length > 0) Output.Write(prompt);
                var line = ReadInputLine(prompt);
                Value v;
                if (inp.Accept) v = Value.String(line);
                else if (line.Trim().Length == 0) return true; // INPUT with nothing typed leaves the variable alone
                else v = Eval(Parser.ParseExpression(line));
                SetVariable(inp.Var, v);
                return true;
            }
            case GetExprStmt ge:
                SetVariable(ge.Var, ge.Default != null ? Eval(ge.Default) : Value.EmptyString);
                return true;
            case MemVarFileStmt mv:
                if (mv.Save) SaveMemVars(mv);
                else RestoreMemVars(mv);
                return true;
            default:
                return false;
        }
    }

    // ---- SORT, TOTAL, JOIN --------------------------------------------------------------------

    /// <summary>Writes rows to a new free table (or a .dbf when the name says so) at <paramref name="target"/>.</summary>
    private void WriteOutputTable(string target, IReadOnlyList<FieldDef> fields, IEnumerable<(bool Deleted, Value[] Values)> rows)
    {
        var list = rows.ToList();
        if (Path.GetExtension(target).Equals(".dbf", StringComparison.OrdinalIgnoreCase))
        {
            DbfWriter.Write(Path.Combine(Options.Default_, target), fields.Select(f => f with { AutoIncNext = null }).ToList(), list);
            return;
        }
        var path = Session.ResolvePath(target, Store.FreeTableExtension);
        if (Session.OpenWorkAreas().Any(w => !w.IsCursor && w.InUse && w.Table.Store.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            throw new VfpException(3, "File is in use.");
        Session.ReleaseStore(path);
        if (File.Exists(path)) File.Delete(path);
        using var other = new DataSession(Options.Clone(), this);
        var t = other.CreateTable(new TableSchema(Path.GetFileNameWithoutExtension(path), fields.Select(f => f with { AutoIncNext = null })), free: true, path: path);
        t.Store.Batch(() => { foreach (var (del, vals) in list) t.Append(vals, del); });
        other.ReleaseStore(path);
    }

    private void ExecSort(SortStmt so)
    {
        var wa = Session.Current;
        if (!wa.InUse) throw VfpException.NoTableOpen();
        var idx = FieldIndexes(wa, so.Fields, memo: true).ToList();
        var keyIdx = so.Keys.Select(k => (Index: wa.FieldIndex(k.Field) is var i && i >= 0 ? i : throw VfpException.FieldNotFound(k.Field), k.Descending, k.IgnoreCase)).ToList();
        if (keyIdx.Any(k => wa.Table.Fields[k.Index].Type is 'M' or 'G' or 'W')) throw new VfpException(ErrorCodes.InvalidArgument, "Cannot sort on a memo or general field.");
        var rows = new List<(bool Deleted, Value[] All, Value[] Keys)>();
        ForEachInScope(wa, so.Scope, "ALL", () => rows.Add((wa.Deleted, idx.Select(i => wa.Get(i)).ToArray(), keyIdx.Select(k => wa.Get(k.Index)).ToArray())));
        var mode = StringCompareMode.Padded;
        rows.Sort((a, b) =>
        {
            for (int k = 0; k < keyIdx.Count; k++)
            {
                Value x = a.Keys[k], y = b.Keys[k];
                if (keyIdx[k].IgnoreCase && x.Kind == ValueKind.Character) { x = Value.String(x.AsString.ToUpperInvariant()); y = Value.String(y.AsString.ToUpperInvariant()); }
                var c = VfpCompare.Compare(x, y, mode);
                if (c != 0) return keyIdx[k].Descending ? -c : c;
            }
            return 0;
        });
        WriteOutputTable(NameValue(so.Target), idx.Select(i => wa.Table.Fields[i]).ToList(), rows.Select(r => (r.Deleted, r.All)));
        Talk($"{rows.Count} records sorted.");
    }

    private void ExecTotal(TotalStmt tt)
    {
        var wa = Session.Current;
        if (!wa.InUse) throw VfpException.NoTableOpen();
        var fields = wa.Table.Fields;
        var keyIndex = wa.FieldIndex(tt.On);
        if (keyIndex < 0) throw VfpException.FieldNotFound(tt.On);
        var outIdx = Enumerable.Range(0, fields.Count).Where(i => fields[i].Type is not ('M' or 'G' or 'W')).ToList();
        var sumIdx = (tt.Fields != null ? tt.Fields.Select(f => wa.FieldIndex(f) is var i && i >= 0 ? i : throw VfpException.FieldNotFound(f))
            : outIdx.Where(i => fields[i].Type is 'N' or 'F' or 'I' or 'B' or 'Y')).ToHashSet();
        var groups = new List<Value[]>();
        Value? lastKey = null;
        Value[]? current = null;
        ForEachInScope(wa, tt.Scope, "ALL", () =>
        {
            var key = wa.Get(keyIndex);
            if (current == null || lastKey is not { } lk || VfpCompare.Compare(lk, key, StringCompareMode.Padded) != 0)
            {
                current = outIdx.Select(i => wa.Get(i)).ToArray();
                groups.Add(current);
                lastKey = key;
                return;
            }
            for (int j = 0; j < outIdx.Count; j++)
            {
                if (!sumIdx.Contains(outIdx[j])) continue;
                var v = wa.Get(outIdx[j]);
                if (v.IsNull) continue;
                var acc = current[j];
                current[j] = acc.Kind == ValueKind.Currency || v.Kind == ValueKind.Currency
                    ? Value.Currency((acc.IsNull ? 0 : (decimal)acc.AsNumber) + (decimal)v.AsNumber)
                    : Value.Number((acc.IsNull ? 0 : acc.AsNumber) + v.AsNumber, Math.Max(acc.Decimals, v.Decimals));
            }
        });
        WriteOutputTable(NameValue(tt.Target), outIdx.Select(i => fields[i]).ToList(), groups.Select(g => (false, g)));
        Talk($"{groups.Count} records generated.");
    }

    private void ExecJoin(JoinStmt js)
    {
        var a = Session.Current;
        if (!a.InUse) throw VfpException.NoTableOpen();
        var b = ResolveWorkArea(Eval(js.With));
        if (!b.InUse) throw VfpException.NoTableOpen();
        // Output fields: the listed expressions, or every field of the current table then the other table's new names.
        var cols = new List<(string Name, Expr Expr)>();
        if (js.Fields != null)
        {
            foreach (var f in js.Fields)
            {
                var name = f is MemberExpr me ? me.Name : f is NameExpr ne ? ne.Name : ExprPrinter.Print(f);
                cols.Add((name, f));
            }
        }
        else
        {
            foreach (var f in a.Table.Fields.Where(f => f.Type is not ('M' or 'G' or 'W'))) cols.Add((f.Name, new MemberExpr(new NameExpr(a.Alias), f.Name)));
            foreach (var f in b.Table.Fields.Where(f => f.Type is not ('M' or 'G' or 'W') && !cols.Any(c => c.Name.Equals(f.Name, StringComparison.OrdinalIgnoreCase))))
                cols.Add((f.Name, new MemberExpr(new NameExpr(b.Alias), f.Name)));
        }
        var saved = Session.CurrentAreaNumber;
        var rows = new List<Value[]>();
        var (aRec, aEof, bRec, bEof) = (a.RecNo, a.Eof, b.RecNo, b.Eof);
        try
        {
            a.GoTop();
            while (!a.Eof)
            {
                b.GoTop();
                while (!b.Eof)
                {
                    Session.Select(a.Number);
                    if (js.For == null || Truthy(Eval(js.For))) rows.Add(cols.Select(c => Eval(c.Expr)).ToArray());
                    b.Skip();
                }
                a.Skip();
            }
        }
        finally
        {
            Session.Select(saved);
            RestorePosition(a, aRec, aEof);
            RestorePosition(b, bRec, bEof);
        }
        var fieldsOut = cols.Select((c, i) => FieldForColumn(c.Name, c.Expr, a, b, rows.Select(r => r[i]))).ToList();
        WriteOutputTable(NameValue(js.Target), fieldsOut, rows.Select(r => (false, r)));
        Talk($"{rows.Count} records joined.");
    }

    private static void RestorePosition(WorkArea wa, int recNo, bool eof)
    {
        if (eof || recNo <= 0) { wa.GoBottom(); if (!wa.Eof) wa.Skip(); }
        else wa.Go(recNo);
    }

    private static FieldDef FieldForColumn(string name, Expr e, WorkArea a, WorkArea b, IEnumerable<Value> values)
    {
        if (e is MemberExpr { Target: NameExpr ne } me)
        {
            var wa = ne.Name.Equals(b.Alias, StringComparison.OrdinalIgnoreCase) ? b : a;
            var i = wa.FieldIndex(me.Name);
            if (i >= 0) return wa.Table.Fields[i] with { Name = name, AutoIncNext = null };
        }
        var sample = values.FirstOrDefault(v => !v.IsNull);
        return sample.Kind switch
        {
            ValueKind.Number => new FieldDef(name, 'N', 18, Math.Max(0, sample.Decimals)),
            ValueKind.Currency => new FieldDef(name, 'Y'),
            ValueKind.Logical => new FieldDef(name, 'L'),
            ValueKind.Date => new FieldDef(name, 'D'),
            ValueKind.DateTime => new FieldDef(name, 'T'),
            _ => new FieldDef(name, 'C', Math.Max(1, Math.Min(254, values.Select(v => v.Kind == ValueKind.Character ? v.AsString.Length : 10).DefaultIfEmpty(10).Max()))),
        };
    }

    // ---- REPLACE FROM ARRAY and memo files ------------------------------------------------------

    private void ExecReplaceFromArray(ReplaceFromArrayStmt ra)
    {
        var wa = AreaOf(ra.Scope.In);
        if (!wa.InUse) throw VfpException.NoTableOpen();
        var arr = FindVariable(ra.Array)?.Array ?? throw new VfpException(232, $"'{ra.Array.ToUpperInvariant()}' is not an array.", ra.Array);
        var idx = FieldIndexes(wa, ra.Fields, memo: false).ToList();
        int row = 1;
        ForEachInScope(wa, ra.Scope, "NEXT1", () =>
        {
            var assignments = new List<(int, Value)>();
            for (int j = 0; j < idx.Count; j++)
            {
                Value v;
                if (arr.TwoDimensional)
                {
                    if (row > arr.Rows || j >= arr.Cols) break;
                    v = arr[row, j + 1];
                }
                else
                {
                    if (j >= arr.Length) break;
                    v = arr[j + 1];
                }
                assignments.Add((idx[j], v));
            }
            if (assignments.Count > 0) wa.Replace(assignments);
            if (arr.TwoDimensional) row++;
        });
    }

    private void ExecMemoFile(MemoFileStmt mf)
    {
        var wa = Session.Current;
        if (!wa.InUse) throw VfpException.NoTableOpen();
        var i = wa.FieldIndex(mf.Field);
        if (i < 0) throw VfpException.FieldNotFound(mf.Field);
        if (wa.Table.Fields[i].Type is not ('M' or 'G' or 'W')) throw new VfpException(ErrorCodes.DataTypeMismatch, $"Field {mf.Field.ToUpperInvariant()} is not a memo field.");
        var name = NameValue(mf.File);
        if (mf.Append)
        {
            var path = Session.ResolvePath(name, ".txt");
            if (!File.Exists(path)) throw VfpException.FileNotFound(name);
            var text = File.ReadAllText(path);
            var current = wa.Get(i);
            wa.Replace(i, Value.String(mf.Overwrite || current.IsNull ? text : current.AsString + text));
        }
        else
        {
            var path = Path.Combine(Options.Default_, Path.HasExtension(name) ? name : name + ".txt");
            var v = wa.Get(i);
            var text = v.IsNull ? "" : v.Kind == ValueKind.Binary ? Encoding.Latin1.GetString(v.AsBinary) : v.AsString;
            if (mf.Additive) File.AppendAllText(path, text);
            else File.WriteAllText(path, text);
        }
    }

    // ---- SAVE TO / RESTORE FROM -----------------------------------------------------------------

    private sealed record MemEntry(string Name, bool Public, string? Value, int Rows, int Cols, List<string>? Elements);

    private void SaveMemVars(MemVarFileStmt mv)
    {
        var entries = new List<MemEntry>();
        var publics = new HashSet<string>(_publics.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var v in VisibleVariables())
        {
            if (v.Name.StartsWith('_')) continue; // system variables
            if (mv.Skeleton != null)
            {
                var hit = mv.Skeleton.Split(',', StringSplitOptions.TrimEntries).Any(p => Builtins.Library.LikeMatch(p.ToUpperInvariant(), v.Name.ToUpperInvariant()));
                if (hit == mv.Except) continue;
            }
            if (v.Array != null)
            {
                if (v.Array.Raw.Any(x => x.Kind == ValueKind.Object)) continue;
                entries.Add(new MemEntry(v.Name.ToUpperInvariant(), publics.Contains(v.Name), null, v.Array.Rows, v.Array.Cols, v.Array.Raw.Select(SyncRow.Canon).ToList()));
            }
            else if (v.Value.Kind != ValueKind.Object)
                entries.Add(new MemEntry(v.Name.ToUpperInvariant(), publics.Contains(v.Name), SyncRow.Canon(v.Value), 0, 0, null));
        }
        var json = JsonSerializer.Serialize(new { format = "Joe Pro memory variables v1", variables = entries }, new JsonSerializerOptions { WriteIndented = true });
        if (mv.MemoField != null)
        {
            var wa = Session.Current;
            var i = wa.FieldIndex(mv.MemoField);
            if (i < 0) throw VfpException.FieldNotFound(mv.MemoField);
            wa.Replace(i, Value.String(json));
            return;
        }
        var name = NameValue(mv.File!);
        File.WriteAllText(Path.Combine(Options.Default_, Path.HasExtension(name) ? name : name + ".mem"), json);
    }

    private void RestoreMemVars(MemVarFileStmt mv)
    {
        string json;
        if (mv.MemoField != null)
        {
            var i = Session.Current.FieldIndex(mv.MemoField);
            if (i < 0) throw VfpException.FieldNotFound(mv.MemoField);
            json = Session.Current.Get(i).AsString;
        }
        else
        {
            var name = NameValue(mv.File!);
            var path = Session.ResolvePath(name, ".mem");
            if (!File.Exists(path)) throw VfpException.FileNotFound(name);
            json = File.ReadAllText(path);
        }
        if (!json.TrimStart().StartsWith('{'))
            throw VfpException.NotSupported("RESTORE FROM a Visual FoxPro binary .MEM file (save the variables again with Joe Pro)");
        using var doc = JsonDocument.Parse(json);
        if (!mv.Additive)
        {
            foreach (var v in _frame.Privates.Keys.ToList()) Release(v);
            foreach (var v in _publics.Keys.Where(k => !k.StartsWith('_')).ToList()) Release(v);
        }
        foreach (var e in doc.RootElement.GetProperty("variables").EnumerateArray())
        {
            var name = e.GetProperty("Name").GetString()!;
            var isPublic = e.GetProperty("Public").GetBoolean();
            var target = isPublic ? Declare("PUBLIC", name) : FindVariable(name) ?? Declare("PRIVATE", name);
            if (e.GetProperty("Elements").ValueKind == JsonValueKind.Array)
            {
                var rows = e.GetProperty("Rows").GetInt32();
                var cols = e.GetProperty("Cols").GetInt32();
                var arr = new VfpArray(rows, cols);
                int k = 0;
                foreach (var el in e.GetProperty("Elements").EnumerateArray())
                {
                    if (k < arr.Raw.Length) arr.Raw[k] = SyncRow.FromCanon(el.GetString()!);
                    k++;
                }
                target.Array = arr;
            }
            else
            {
                target.Array = null;
                target.Value = SyncRow.FromCanon(e.GetProperty("Value").GetString()!);
            }
        }
    }

    // ---- Files, databases and the operating system -------------------------------------------

    private void ExecDir(string rest)
    {
        var words = rest.Trim();
        string? toFile = null;
        var m = System.Text.RegularExpressions.Regex.Match(words, @"\s*\bTO\s+FILE\s+(\S+)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) { toFile = m.Groups[1].Value; words = words[..m.Index].Trim(); }
        words = System.Text.RegularExpressions.Regex.Replace(words, @"^(ON\s+\S+|LIKE)\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        var sb = new StringBuilder();
        if (words.Length == 0)
        {
            // DIR with no skeleton: the tables in the current folder.
            sb.AppendLine("Tables                       # Records       Last Update     Size");
            long total = 0;
            var files = Directory.GetFiles(Options.Default_, "*" + Store.FreeTableExtension).Order(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var f in files)
            {
                var info = new FileInfo(f);
                total += info.Length;
                string records;
                try { using var s = new DataSession(Options.Clone(), this); var t = s.StoreOf(f).OpenTable(Path.GetFileNameWithoutExtension(f)); records = t.RecordCount.ToString(CultureInfo.InvariantCulture); s.ReleaseStore(f); }
                catch (VfpException) { records = "?"; }
                sb.AppendLine($"{Path.GetFileName(f).ToUpperInvariant(),-28} {records,9}       {info.LastWriteTime:MM/dd/yy} {info.Length,12}");
            }
            sb.AppendLine();
            sb.AppendLine($"{total,10} bytes in {files.Count} files.");
        }
        else
        {
            var full = Path.Combine(Options.Default_, words.Trim('"', '\''));
            var dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full) ?? Options.Default_;
            var pattern = Directory.Exists(full) ? "*" : Path.GetFileName(full);
            if (pattern == "*.*") pattern = "*";
            var files = Directory.Exists(dir) ? Directory.GetFiles(dir, pattern).Select(f => new FileInfo(f)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList() : [];
            var names = files.Select(f => f.Name.ToUpperInvariant()).ToList();
            for (int i = 0; i < names.Count; i += 4)
                sb.AppendLine(string.Concat(names.Skip(i).Take(4).Select(n => n.PadRight(20))).TrimEnd());
            sb.AppendLine();
            sb.AppendLine($"{files.Sum(f => f.Length),10} bytes in {files.Count} files.");
        }
        if (Path.GetPathRoot(Options.Default_) is { } root)
        {
            try { sb.AppendLine($"{new DriveInfo(root).AvailableFreeSpace,10} bytes remaining on drive."); }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
        }
        Emit(sb.ToString().TrimEnd('\r', '\n'), toFile);
    }

    private void ExecType(string rest)
    {
        var words = rest.Trim();
        string? toFile = null;
        var m = System.Text.RegularExpressions.Regex.Match(words, @"\s*\bTO\s+FILE\s+(\S+)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) { toFile = m.Groups[1].Value; words = words[..m.Index].Trim(); }
        bool number = false;
        var nm = System.Text.RegularExpressions.Regex.Match(words, @"\s+NUMBER\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (nm.Success) { number = true; words = words[..nm.Index].Trim(); }
        words = System.Text.RegularExpressions.Regex.Replace(words, @"\s+(AUTO|WRAP|TO\s+PRINTER(\s+PROMPT)?)\b.*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        var name = words.StartsWith('(') ? Eval(Parser.ParseExpression(words)).AsString.Trim() : words.Trim('"', '\'');
        var path = Session.ResolvePath(name, "");
        if (!File.Exists(path)) throw VfpException.FileNotFound(name);
        var lines = File.ReadAllLines(path);
        var text = string.Join("\n", number ? lines.Select((l, i) => $"{i + 1,5}: {l}") : lines);
        Emit(text, toFile);
    }

    private void Emit(string text, string? toFile)
    {
        if (toFile != null) File.WriteAllText(Path.Combine(Options.Default_, Path.HasExtension(toFile) ? toFile : toFile + ".txt"), text.Replace("\n", Environment.NewLine));
        else foreach (var line in text.Split('\n')) WriteLine(line.TrimEnd('\r'));
    }

    /// <summary>RUN [/N] command (also !): runs an operating-system command; without /N waits and shows its output.</summary>
    private void ExecRun(string rest)
    {
        var cmd = rest.Trim();
        bool noWait = false;
        var m = System.Text.RegularExpressions.Regex.Match(cmd, @"^/N\d*\s+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) { noWait = true; cmd = cmd[m.Length..]; }
        if (cmd.Length == 0) return;
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c " + cmd)
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", cmd } };
        psi.WorkingDirectory = Options.Default_;
        psi.UseShellExecute = false;
        if (noWait)
        {
            try { Process.Start(psi); }
            catch (System.ComponentModel.Win32Exception ex) { throw new VfpException(1405, "RUN|! command failed: " + ex.Message); }
            return;
        }
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        try
        {
            using var p = Process.Start(psi) ?? throw new VfpException(1405, "RUN|! command failed.");
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            var text = (output.Result + error.Result).TrimEnd('\r', '\n');
            if (text.Length > 0) Emit(text.Replace("\r\n", "\n"), null);
        }
        catch (System.ComponentModel.Win32Exception ex) { throw new VfpException(1405, "RUN|! command failed: " + ex.Message); }
    }

    private string DatabasePathArg(string rest)
    {
        var name = rest.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(w => !w.Equals("DELETETABLES", StringComparison.OrdinalIgnoreCase) && !w.Equals("RECYCLE", StringComparison.OrdinalIgnoreCase))
            ?? Session.CurrentDatabase?.Path ?? throw new VfpException(1520, "No database is open or set as the current database.");
        name = name.Trim('"', '\'');
        if (name.StartsWith('(')) name = Eval(Parser.ParseExpression(name)).AsString.Trim();
        var path = Session.ResolvePath(name, ".jpdb");
        if (!File.Exists(path)) throw VfpException.FileNotFound(name);
        return path;
    }

    private void DeleteDatabaseCommand(string rest)
    {
        var path = DatabasePathArg(rest);
        if (Session.OpenDatabases.Any(d => d.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            throw new VfpException(1709, "Database object is being used by someone else.");
        Session.ReleaseStore(path);
        File.Delete(path);
        foreach (var extra in new[] { path + "-wal", path + "-shm" }) if (File.Exists(extra)) File.Delete(extra);
    }

    private void PackDatabaseCommand()
    {
        var db = RequireDatabase();
        if (db.IsRemote) throw new VfpException(1705, "PACK DATABASE is run on the Data Server (joepro-server check/backup).");
        if (Session.OpenWorkAreas().Any(w => !w.IsCursor && w.Table.Store == db)) throw new VfpException(1705, "File access is denied: close the database's tables first.");
        db.Link.Execute("VACUUM", []);
    }

    private void CompileDatabaseCommand(string rest)
    {
        var path = DatabasePathArg(rest);
        var db = Session.OpenDatabases.FirstOrDefault(d => d.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) ?? Session.OpenDatabase(path);
        Parser.ParseProgram(db.StoredProcedures, db.Name + " stored procedures");
    }

    private void DropTableCommand(string rest)
    {
        var words = rest.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) throw VfpException.Syntax("DROP TABLE needs a table name.");
        var name = words[0].Trim('"', '\'');
        if (name.StartsWith('(')) name = Eval(Parser.ParseExpression(name)).AsString.Trim();
        if (Session.CurrentDatabase is { } db && db.HasTable(name))
        {
            ExecRemoveTable(new RemoveTableStmt(new LiteralExpr(Value.String(name)), true));
            return;
        }
        // A free table: the file goes.
        var path = Session.ResolvePath(name, Store.FreeTableExtension);
        if (!File.Exists(path)) throw VfpException.FileNotFound(name);
        foreach (var wa in Session.OpenWorkAreas().Where(w => !w.IsCursor && w.Table.Store.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList()) wa.Close();
        Session.ReleaseStore(path);
        File.Delete(path);
    }

    private void EraseCommand(string rest)
    {
        var words = System.Text.RegularExpressions.Regex.Replace(rest.Trim(), @"\s+RECYCLE\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        if (words.Length == 0 || words == "?") return;
        var name = words.StartsWith('(') ? Eval(Parser.ParseExpression(words)).AsString.Trim() : words.Trim('"', '\'');
        var full = Path.Combine(Options.Default_, name);
        var dir = Path.GetDirectoryName(full) ?? Options.Default_;
        var pattern = Path.GetFileName(full);
        if (pattern.IndexOfAny(['*', '?']) >= 0)
        {
            if (Directory.Exists(dir)) foreach (var f in Directory.GetFiles(dir, pattern == "*.*" ? "*" : pattern)) File.Delete(f);
            return;
        }
        var p = Session.ResolvePath(name, "");
        if (!File.Exists(p)) throw VfpException.FileNotFound(name);
        File.Delete(p);
    }

    /// <summary>RELEASE CLASSLIB name|ALIAS alias, RELEASE PROCEDURE file, RELEASE LIBRARY file.</summary>
    private void ReleaseLibraryCommand(string rest)
    {
        var words = rest.Trim();
        var sp = words.IndexOf(' ');
        var kind = (sp < 0 ? words : words[..sp]).ToUpperInvariant();
        var names = (sp < 0 ? "" : words[(sp + 1)..]).Trim();
        bool byAlias = false;
        if (names.StartsWith("ALIAS ", StringComparison.OrdinalIgnoreCase)) { byAlias = true; names = names[6..].Trim(); }
        foreach (var raw in names.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var stem = Path.GetFileNameWithoutExtension(raw.Trim('"', '\''));
            bool Matches(ProgramUnit u) => (u.File != null && Path.GetFileNameWithoutExtension(u.File).Equals(stem, StringComparison.OrdinalIgnoreCase)) || u.Name.Equals(stem, StringComparison.OrdinalIgnoreCase);
            if (kind.StartsWith("CLASS")) _classLibraries.RemoveAll(u => Matches(u) || (byAlias && u.Name.Equals(stem, StringComparison.OrdinalIgnoreCase)));
            else if (kind.StartsWith("PROC")) _procedureFiles.RemoveAll(Matches);
        }
    }

    // ---- LIST / DISPLAY variants ----------------------------------------------------------------

    private void ListCommand(bool display, string rest)
    {
        var words = rest.Trim();
        string? toFile = null;
        var tm = System.Text.RegularExpressions.Regex.Match(words, @"\s*\bTO\s+FILE\s+(\S+)(\s+ADDITIVE)?\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (tm.Success) { toFile = tm.Groups[1].Value; words = words[..tm.Index].Trim(); }
        words = System.Text.RegularExpressions.Regex.Replace(words, @"\s+(NOCONSOLE|TO\s+PRINTER(\s+PROMPT)?)\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        var sp = words.IndexOf(' ');
        var what = (sp < 0 ? words : words[..sp]).ToUpperInvariant();
        var arg = sp < 0 ? "" : words[(sp + 1)..].Trim();
        string? like = arg.StartsWith("LIKE ", StringComparison.OrdinalIgnoreCase) ? arg[5..].Trim() : null;
        var sb = new StringBuilder();
        switch (what)
        {
            case "MEMORY":
            {
                var publics = new HashSet<string>(_publics.Keys, StringComparer.OrdinalIgnoreCase);
                var locals = new HashSet<string>(_frame.Locals.Keys, StringComparer.OrdinalIgnoreCase);
                var vars = VisibleVariables().Where(v => like == null || Builtins.Library.LikeMatch(like.ToUpperInvariant(), v.Name.ToUpperInvariant())).OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var v in vars)
                {
                    var scope = publics.Contains(v.Name) ? "Pub" : locals.Contains(v.Name) ? "Local" : "Priv";
                    if (v.Array != null)
                    {
                        sb.AppendLine($"{v.Name.ToUpperInvariant(),-14} {scope,-6} A");
                        for (int i = 1; i <= v.Array.Length; i++)
                        {
                            var (r, c) = v.Array.Subscript(i);
                            var sub = v.Array.TwoDimensional ? $"( {r}, {c})" : $"( {i})";
                            sb.AppendLine($"  {sub,-12} {"",-6} {v.Array[i].VarType} {MemDisplay(v.Array[i])}");
                        }
                    }
                    else sb.AppendLine($"{v.Name.ToUpperInvariant(),-14} {scope,-6} {v.Value.VarType} {MemDisplay(v.Value)}");
                }
                sb.AppendLine();
                sb.AppendLine($"{vars.Count} variables defined");
                break;
            }
            case "STATUS":
            {
                sb.AppendLine($"Processor is {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
                sb.AppendLine($"Currently selected table: {(Session.Current.InUse ? Session.Current.Alias : "none")}");
                foreach (var wa in Session.OpenWorkAreas())
                {
                    sb.AppendLine($"Select area: {wa.Number,5}, Table in Use: {wa.Source}   Alias: {wa.Alias}{(wa.ReadOnly ? " (read-only)" : "")}{(wa.Exclusive ? " Exclusive" : "")}");
                    foreach (var t in wa.Table.Schema.Tags)
                        sb.AppendLine($"            Index tag: {t.Name.ToUpperInvariant(),-10} Key: {t.Expression}{(t.ForExpression != null ? " For: " + t.ForExpression : "")}{(wa.Order == t ? "  (master)" : "")}");
                    if (wa.Filter != null) sb.AppendLine($"            Filter:    {wa.Filter.Source}");
                    foreach (var (e, child) in wa.Relations) sb.AppendLine($"            Related into: {child.Alias}  Relation: {e.Source}");
                }
                sb.AppendLine();
                sb.AppendLine($"Default directory: {Options.Default_}");
                if (Options.Path.Count > 0) sb.AppendLine($"Search path: {string.Join(";", Options.Path)}");
                if (Session.CurrentDatabase is { } cdb) sb.AppendLine($"Current database: {cdb.Name.ToUpperInvariant()}");
                sb.AppendLine($"Procedure files: {string.Join(", ", _procedureFiles.Select(u => Path.GetFileName(u.File ?? u.Name)))}");
                sb.AppendLine($"Class libraries: {string.Join(", ", _classLibraries.Select(u => Path.GetFileName(u.File ?? u.Name)))}");
                sb.AppendLine($"On Error: {OnErrorCommand ?? ""}");
                foreach (var kv in _onCommands) sb.AppendLine($"On {kv.Key.ToLowerInvariant()}: {kv.Value}");
                foreach (var kv in KeyLabels) sb.AppendLine($"On key label {kv.Key}: {kv.Value}");
                sb.AppendLine($"Deleted {OnOff(Options.Deleted)}   Exact {OnOff(Options.Exact)}   Talk {OnOff(Options.Talk)}   Century {OnOff(Options.Century)}");
                break;
            }
            case "OBJECTS":
            {
                foreach (var v in VisibleVariables().Where(v => v.Value.Kind == ValueKind.Object && v.Value.AsObject is VfpObject && (like == null || Builtins.Library.LikeMatch(like.ToUpperInvariant(), v.Name.ToUpperInvariant()))))
                {
                    var o = (VfpObject)v.Value.AsObject;
                    sb.AppendLine($"Object: {v.Name.ToUpperInvariant()}   Class: {o.Class.Name.ToUpperInvariant()}");
                    sb.AppendLine("  Properties:");
                    foreach (var p in o.Properties.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                        sb.AppendLine($"    {p.Key.ToUpperInvariant(),-20} {p.Value.Value.VarType}  {MemDisplay(p.Value.Value)}");
                    if (o.Members.Count > 0) sb.AppendLine("  Member objects: " + string.Join(", ", o.Members.Select(m => m.Name.ToUpperInvariant())));
                }
                break;
            }
            case "DATABASE" or "TABLES" or "VIEWS" or "CONNECTIONS" or "PROCEDURES":
            {
                var db = RequireDatabase();
                if (what == "DATABASE") sb.AppendLine($"Database: {db.Path}");
                if (what is "DATABASE" or "TABLES")
                {
                    sb.AppendLine("Tables in database " + db.Name.ToUpperInvariant());
                    foreach (var t in db.TableNames().Order(StringComparer.OrdinalIgnoreCase)) sb.AppendLine("  " + t.ToUpperInvariant());
                }
                if (what is "DATABASE" or "VIEWS")
                {
                    sb.AppendLine("Views in database " + db.Name.ToUpperInvariant());
                    foreach (var v in db.ObjectNames(DbObjectStore.ViewKind).Order(StringComparer.OrdinalIgnoreCase))
                        sb.AppendLine($"  {v.ToUpperInvariant(),-24} {(db.GetView(v)?.Remote == true ? "remote" : "local")}");
                }
                if (what is "DATABASE" or "CONNECTIONS")
                {
                    sb.AppendLine("Connections in database " + db.Name.ToUpperInvariant());
                    foreach (var cn in db.ObjectNames(DbObjectStore.ConnectionKind).Order(StringComparer.OrdinalIgnoreCase)) sb.AppendLine("  " + cn.ToUpperInvariant());
                }
                if (what is "DATABASE" or "PROCEDURES")
                {
                    sb.AppendLine("Stored procedures in database " + db.Name.ToUpperInvariant());
                    sb.Append(db.StoredProcedures);
                }
                break;
            }
            case "FILES":
                ExecDir(arg.Length > 0 ? System.Text.RegularExpressions.Regex.Replace(arg, @"^(ON\s+\S+\s*)?(LIKE\s+)?", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase) + (toFile != null ? " TO FILE " + toFile : "") : "*.*" + (toFile != null ? " TO FILE " + toFile : ""));
                return;
            case "DLLS":
                Notify($"{(display ? "DISPLAY" : "LIST")} DLLS is not supported: Win32 DLL declarations are not available in the cross-platform runtime.");
                return;
            default:
                throw VfpException.Syntax($"Unrecognized phrase/keyword in {(display ? "DISPLAY" : "LIST")}: {what}");
        }
        Emit(sb.ToString().TrimEnd('\r', '\n'), toFile);
    }

    private string MemDisplay(Value v) => v.Kind switch
    {
        ValueKind.Character => "\"" + v.AsString + "\"",
        ValueKind.Object => "(object)",
        _ => Formatter.ToDisplay(v, Options).Trim(),
    };

    private static string OnOff(bool b) => b ? "ON" : "OFF";

    /// <summary>CREATE name FROM structure-extended-table: a new table with the structure described by the rows.</summary>
    private bool CreateFromCommand(string rest)
    {
        var m = System.Text.RegularExpressions.Regex.Match(rest.Trim(), @"^(?<name>\S+)\s+FROM\s+(?<src>\S+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        var srcPath = Session.ResolvePath(m.Groups["src"].Value.Trim('"', '\''), Store.FreeTableExtension);
        if (!File.Exists(srcPath)) throw VfpException.FileNotFound(m.Groups["src"].Value);
        var fields = new List<FieldDef>();
        using (var s = new DataSession(Options.Clone(), this))
        {
            var t = s.StoreOf(srcPath).OpenTable(Path.GetFileNameWithoutExtension(srcPath));
            int Col(string n) => t.Schema.FieldIndex(n);
            foreach (var r in t.Scan(null, forward: true))
            {
                if (r.Deleted) continue;
                var name = r.Values[Col("FIELD_NAME")].AsString.Trim();
                var type = r.Values[Col("FIELD_TYPE")].AsString.Trim();
                var f = new FieldDef(name, type.Length > 0 ? type[0] : 'C', (int)r.Values[Col("FIELD_LEN")].AsNumber, (int)r.Values[Col("FIELD_DEC")].AsNumber);
                if (Col("FIELD_NULL") >= 0) f = f with { Nullable = r.Values[Col("FIELD_NULL")].AsBool };
                fields.Add(f);
            }
            s.ReleaseStore(srcPath);
        }
        var target = m.Groups["name"].Value.Trim('"', '\'');
        var schema = new TableSchema(Path.GetFileNameWithoutExtension(target), fields);
        var area = Session.CurrentAreaNumber;
        Session.Current.Close();
        if (Session.CurrentDatabase != null && !Path.HasExtension(target)) Session.CurrentDatabase.CreateTable(schema, this);
        else
        {
            var path = Session.ResolvePath(target, Store.FreeTableExtension);
            Session.CreateTable(schema, free: true, path);
        }
        Session.Use(target, area);
        return true;
    }

    /// <summary>MODIFY WINDOW SCREEN / ZOOM WINDOW SCREEN: the main window's properties.</summary>
    private bool ScreenWindowCommand(string verb, string rest)
    {
        var m = System.Text.RegularExpressions.Regex.Match(rest.Trim(), @"^WIND\w*\s+SCREEN\b\s*(?<rest>.*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        var screen = ResolveSpecial("_SCREEN");
        var clauses = m.Groups["rest"].Value;
        if (verb == "ZOOM")
        {
            var state = clauses.ToUpperInvariant() switch { var c when c.StartsWith("MAX") => 2, var c when c.StartsWith("MIN") => 1, _ => 0 };
            screen.Set("WindowState", Value.Number(state));
        }
        else
        {
            var tm = System.Text.RegularExpressions.Regex.Match(clauses, @"\bTITLE\s+(?<t>""[^""]*""|'[^']*'|\([^)]*\)|\S+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (tm.Success) screen.Set("Caption", Eval(Parser.ParseExpression(tm.Groups["t"].Value)));
            if (System.Text.RegularExpressions.Regex.IsMatch(clauses, @"^\s*$")) { screen.Set("Caption", Value.String("Joe Pro")); screen.Set("WindowState", Value.Number(0)); }
        }
        Ui?.PropertyChanged(screen, "Caption");
        return true;
    }
}
