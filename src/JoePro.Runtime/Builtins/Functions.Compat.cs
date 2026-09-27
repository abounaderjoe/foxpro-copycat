using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using JoePro.Core;
using JoePro.Data;
using JoePro.Language;

namespace JoePro.Runtime.Builtins;

/// <summary>
/// The remaining VFP 9 functions from the coverage sweep: environment and stack information, index and relation
/// information, file dates, the dialogs (through host hooks), font metrics and XML cursors.
/// </summary>
public static partial class Library
{
    private static readonly Dictionary<int, int> ResultSets = new();

    private static void RegisterCompat()
    {
        // ---- Characters (strings are Unicode, so the double-byte variants are the same functions) ----
        Add("ATLINE", c => NullOr(c, () => N(LineOf(c.Str(1), c.Str(0), ignoreCase: false, last: false))));
        Add("ATCLINE", c => NullOr(c, () => N(LineOf(c.Str(1), c.Str(0), ignoreCase: true, last: false))));
        Add("RATLINE", c => NullOr(c, () => N(LineOf(c.Str(1), c.Str(0), ignoreCase: false, last: true))));
        Add("CPCONVERT", c =>
        {
            // Values are Unicode text once read, so converting between code pages keeps the characters as they are.
            CodePage(c.Int(0));
            CodePage(c.Int(1));
            return c[2];
        });
        Add("OEMTOANSI", c => c[0]);
        Add("CREATEBINARY", c => c[0].Kind == ValueKind.Binary ? c[0] : Value.Binary(Encoding.Latin1.GetBytes(c.Str(0))));
        Add("NORMALIZE", c => S(Normalize(c.Str(0))));
        Add("DISPLAYPATH", c =>
        {
            var path = c.Str(0);
            var max = c.Int(1);
            if (path.Length <= max || max <= 0) return S(path);
            var name = FileName(path);
            var drive = path.Length >= 3 && path[1] == ':' ? path[..3] : path.StartsWith("\\\\") || path.StartsWith("/") ? path[..1] : "";
            var room = max - drive.Length - name.Length - 4;
            if (room < 0) return S("..." + name[Math.Max(0, name.Length - (max - 3))..]);
            return S(drive + "...\\" + name);
        });

        // ---- Environment ----
        Add("HOME", c =>
        {
            var baseDir = AddSeparator(AppContext.BaseDirectory);
            return S(c.Int(0, 0) switch
            {
                1 => baseDir,
                2 => AddSeparator(Path.Combine(baseDir, "samples")),
                4 => AddSeparator(Path.Combine(baseDir, "graphics")),
                5 => AddSeparator(Path.Combine(baseDir, "sdk")),
                6 => AddSeparator(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Joe Pro")),
                7 => AddSeparator(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Joe Pro")),
                _ => baseDir,
            });
        });
        Add("SYSMETRIC", c => N(SysMetric(c.Rt, c.Int(0))));
        Add("MEMORY", _ => N(Math.Floor(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0)));
        Add("DISKSPACE", c =>
        {
            var dir = c.Has(0) && c.Str(0).Trim().Length > 0 ? Path.Combine(c.Options.Default_, c.Str(0).Trim()) : c.Options.Default_;
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir)) ?? dir);
                return N(c.Int(1, 3) switch { 1 => drive.TotalSize, 2 => drive.TotalFreeSpace, _ => drive.AvailableFreeSpace });
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return N(-1); }
        });
        Add("DRIVETYPE", c =>
        {
            try
            {
                var d = new DriveInfo(c.Str(0).Trim());
                if (!d.IsReady && d.DriveType == DriveType.NoRootDirectory) return N(1);
                return N(d.DriveType switch { DriveType.Removable => 2, DriveType.Fixed => 3, DriveType.Network => 4, DriveType.CDRom => 5, DriveType.Ram => 6, DriveType.NoRootDirectory => 1, _ => 3 });
            }
            catch (ArgumentException) { return N(1); }
        });
        Add("FKMAX", _ => N(12));
        Add("FKLABEL", c => S(c.Int(0) is var n && n >= 1 && n <= 12 ? "F" + n : ""));
        Add("ISCOLOR", _ => Value.True);
        Add("ISMOUSE", _ => Value.True);
        Add("ISHOSTED", _ => Value.False);
        Add("PRINTSTATUS", _ => Value.True);
        Add("PRTINFO", c => N(c.Int(0) switch { 1 => 1, 2 => 1, 3 => 2794, 4 => 2159, 5 => 100, 6 => 1, 7 => 7, 8 => 1, 9 => 300, 10 => 1, 11 => 1, 12 => 300, 13 => 1, _ => -1 }));
        Add("GETPRINTER", c => S(c.Rt.FileDialog?.Invoke("printer", "", "", "") ?? ""));
        Add("APRINTERS", c => N(0));
        Add("ANETRESOURCES", c => N(0));
        Add("ALANGUAGE", c =>
        {
            var names = c.Int(1) switch
            {
                1 => VfpCatalog.Commands.ToList(),
                2 => Names.Where(n => !n.StartsWith("__")).Select(n => n.ToUpperInvariant()).OrderBy(n => n, StringComparer.Ordinal).ToList(),
                3 => BaseClasses.Names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                4 => ["dbc_Activate", "dbc_AfterAddTable", "dbc_AfterCloseTable", "dbc_AfterCreateTable", "dbc_AfterDropTable", "dbc_AfterOpenTable",
                      "dbc_AfterRenameTable", "dbc_BeforeAddTable", "dbc_BeforeCloseTable", "dbc_BeforeCreateTable", "dbc_BeforeDropTable",
                      "dbc_BeforeOpenTable", "dbc_BeforeRenameTable", "dbc_CloseData", "dbc_Deactivate", "dbc_OpenData"],
                _ => throw VfpException.InvalidArgument(),
            };
            var arr = c.NewArray(0, names.Count, 0);
            for (int i = 0; i < names.Count; i++) arr[i + 1] = S(names[i]);
            return N(names.Count);
        });

        // ---- Program state ----
        Add("ASTACKINFO", c =>
        {
            var frames = new List<Frame>();
            for (var f = c.Rt.CurrentFrame; f != null; f = f.Parent) frames.Add(f);
            frames.Reverse();
            var arr = c.NewArray(0, frames.Count, 6);
            for (int i = 0; i < frames.Count; i++)
            {
                var f = frames[i];
                var file = f.Unit?.File ?? "";
                arr[i + 1, 1] = N(i + 1);
                arr[i + 1, 2] = S(file.ToUpperInvariant());
                arr[i + 1, 3] = S(f.Program.ToLowerInvariant());
                arr[i + 1, 4] = S(file.ToUpperInvariant());
                arr[i + 1, 5] = N(f.Line);
                arr[i + 1, 6] = S(SourceLine(file, f.Line));
            }
            return N(frames.Count);
        });
        Add("APROCINFO", c =>
        {
            var path = c.Rt.Session.ResolvePath(c.Str(1).Trim(), ".prg");
            if (!File.Exists(path)) throw VfpException.FileNotFound(c.Str(1));
            var rows = ProcInfo(File.ReadAllLines(path), c.Int(2, 0));
            if (rows.Count == 0) return N(0);
            var arr = c.NewArray(0, rows.Count, 4);
            for (int i = 0; i < rows.Count; i++)
            {
                arr[i + 1, 1] = S(rows[i].Name);
                arr[i + 1, 2] = N(rows[i].Line);
                arr[i + 1, 3] = S(rows[i].Type);
                arr[i + 1, 4] = N(rows[i].Indent);
            }
            return N(rows.Count);
        });
        Add("ASESSIONS", c =>
        {
            var ids = c.Rt.Sessions.Select(s => s.Id).Append(c.Rt.Session.Id).Distinct().Order().ToList();
            var arr = c.NewArray(0, ids.Count, 0);
            for (int i = 0; i < ids.Count; i++) arr[i + 1] = N(ids[i]);
            return N(ids.Count);
        });
        Add("ADATABASES", c =>
        {
            var dbs = c.Rt.Session.OpenDatabases.ToList();
            if (dbs.Count == 0) return N(0);
            var arr = c.NewArray(0, dbs.Count, 2);
            for (int i = 0; i < dbs.Count; i++) { arr[i + 1, 1] = S(dbs[i].Name.ToUpperInvariant()); arr[i + 1, 2] = S(dbs[i].Path.ToUpperInvariant()); }
            return N(dbs.Count);
        });
        Add("SETFLDSTATE", c =>
        {
            var wa = c.Area(2);
            var v = c[0];
            wa.SetFieldState(v.Kind == ValueKind.Number && v.AsNumber == 0 ? -1 : FieldArg(c, 0, wa), c.Int(1));
            return Value.True;
        });
        Add("OBJTOCLIENT", c =>
        {
            // The position relative to the form: the object's own Top/Left plus those of each container between it and the form.
            var o = Obj(c, 0);
            double Num(VfpObject x, string p) => c.Rt.GetProperty(x, p) is { Kind: ValueKind.Number } v ? v.AsNumber : 0;
            var what = c.Int(1);
            if (what is 3 or 4) return N(Num(o, what == 3 ? "Width" : "Height"));
            if (what is not (1 or 2)) throw VfpException.InvalidArgument();
            var prop = what == 1 ? "Top" : "Left";
            double pos = Num(o, prop);
            for (var p = o.Parent; p != null && !p.Class.IsA("Form") && !p.Class.IsA("FormSet") && !p.Class.IsA("Toolbar"); p = p.Parent)
            {
                if (p.Class.IsA("Page"))
                {
                    // A page sits inside its pageframe below the tabs.
                    var pf = p.Parent;
                    if (pf != null)
                    {
                        pos += what == 1 && (c.Rt.GetProperty(pf, "Tabs") is not { Kind: ValueKind.Logical } tabs || tabs.AsBool) ? 25 : 0;
                    }
                    continue;
                }
                pos += Num(p, prop);
            }
            return N(pos);
        });
        Add("GETAUTOINCVALUE", _ => Table.LastAutoIncValue is { } v ? N(v) : Value.Null);
        Add("FERROR", _ => N(_ferror));
        Add("ASELOBJ", c =>
        {
            // Objects selected in a designer. Designers run in the IDE's own process, so programs see no selection
            // (ASELOBJ(a, 1) returns the container, which is also none outside a designer).
            return N(0);
        });
        Add("AGETCLASS", c =>
        {
            var chosen = c.Rt.FileDialog?.Invoke("class", "jpclass;vcx", c.Str(3, "Class"), c.Str(1, ""));
            if (string.IsNullOrEmpty(chosen)) return Value.False;
            var parts = chosen.Split('|');
            var arr = c.NewArray(0, 2, 0);
            arr[1] = S(parts[0]);
            arr[2] = S(parts.Length > 1 ? parts[1] : "");
            return Value.True;
        });
        Add("AGETFILEVERSION", c =>
        {
            var path = c.Rt.Session.ResolvePath(c.Str(1).Trim(), "");
            if (!File.Exists(path)) return N(0);
            FileVersionInfo info;
            try { info = FileVersionInfo.GetVersionInfo(path); }
            catch (FileNotFoundException) { return N(0); }
            if (info.FileVersion == null && info.ProductVersion == null) return N(0);
            string[] items = [info.Comments ?? "", info.CompanyName ?? "", info.FileDescription ?? "", info.FileVersion ?? "", info.InternalName ?? "",
                info.LegalCopyright ?? "", info.LegalTrademarks ?? "", info.OriginalFilename ?? "", info.PrivateBuild ?? "", info.ProductName ?? "",
                info.ProductVersion ?? "", info.SpecialBuild ?? "", "", info.Language ?? "", ""];
            var arr = c.NewArray(0, items.Length, 0);
            for (int i = 0; i < items.Length; i++) arr[i + 1] = S(items[i]);
            return N(items.Length);
        });

        // ---- Tables, indexes and relations ----
        Add("FILTER", c => { var wa = c.Area(0); return S(wa.InUse ? wa.Filter?.Source ?? "" : ""); });
        Add("RELATION", c =>
        {
            var wa = c.Area(1);
            var n = c.Int(0);
            return S(n >= 1 && n <= wa.Relations.Count ? wa.Relations[n - 1].Expr.Source : "");
        });
        Add("TARGET", c =>
        {
            var wa = c.Area(1);
            var n = c.Int(0);
            return S(n >= 1 && n <= wa.Relations.Count ? wa.Relations[n - 1].Child.Alias : "");
        });
        Add("UNIQUE", c =>
        {
            int ni = c.Has(0) && c[0].Kind == ValueKind.Character ? 1 : 0;
            var wa = c.Area(ni + 1);
            var tag = c.Has(ni) ? wa.Table.Schema.Tags.ElementAtOrDefault(c.Int(ni) - 1) : wa.Order;
            return L(tag?.Kind == TagKind.Unique);
        });
        Add("IDXCOLLATE", c =>
        {
            int ni = c.Has(0) && c[0].Kind == ValueKind.Character ? 1 : 0;
            var wa = c.Area(ni + 1);
            var tag = wa.InUse ? wa.Table.Schema.Tags.ElementAtOrDefault(c.Int(ni) - 1) : null;
            return S(tag?.Collation ?? "");
        });
        Add("CDX", c =>
        {
            // One structural index per table holds all its tags.
            var wa = c.Area(1);
            return S(c.Int(0) == 1 && wa.InUse && wa.Table.Schema.Tags.Count > 0 ? Path.ChangeExtension(wa.Source, ".CDX").ToUpperInvariant() : "");
        });
        Add("ATAGINFO", c =>
        {
            var wa = c.Area(2);
            if (!wa.InUse) return N(0);
            var tags = wa.Table.Schema.Tags;
            if (tags.Count == 0) return N(0);
            var arr = c.NewArray(0, tags.Count, 6);
            for (int i = 0; i < tags.Count; i++)
            {
                var t = tags[i];
                arr[i + 1, 1] = S(t.Name.ToUpperInvariant());
                arr[i + 1, 2] = S(t.Kind switch { TagKind.Primary => "PRIMARY", TagKind.Candidate => "CANDIDATE", TagKind.Unique => "UNIQUE", _ => "REGULAR" });
                arr[i + 1, 3] = S(t.Expression);
                arr[i + 1, 4] = S(t.ForExpression ?? "");
                arr[i + 1, 5] = S(t.Descending ? "DESCENDING" : "ASCENDING");
                arr[i + 1, 6] = S(t.Collation);
            }
            return N(tags.Count);
        });
        Add("KEYMATCH", c =>
        {
            var wa = c.Area(2);
            TagDef? tag = null;
            if (c.Has(1)) tag = wa.Table.Schema.Tags.ElementAtOrDefault(c.Int(1) - 1) ?? throw VfpException.InvalidArgument();
            tag ??= wa.Order ?? throw new VfpException(26, "Table has no index order set.");
            var (rec, eof, found) = (wa.RecNo, wa.Eof, wa.Found);
            var hit = wa.Seek(c[0], tag);
            if (eof) wa.GoBottom();
            if (eof) wa.Skip();
            else wa.Go(rec);
            wa.Found = found;
            return L(hit);
        });
        Add("HEADER", c =>
        {
            var wa = c.Area(0);
            // The size a VFP table header of this structure has: 32 bytes, 32 per field, a terminator and the DBC backlink.
            return N(wa.InUse ? 32 + 32 * wa.Table.Fields.Count + 1 + 263 : 0);
        });
        Add("LUPDATE", c =>
        {
            var wa = c.Area(0);
            if (!wa.InUse) return Value.EmptyDate;
            var path = wa.Table.Store.Path;
            return Value.DateOf(DateOnly.FromDateTime(!wa.Table.Store.IsRemote && File.Exists(path) ? File.GetLastWriteTime(path) : DateTime.Today));
        });
        Add(["ISTRANSACTABLE", "MAKETRANSACTABLE"], c => L(c.Area(0).InUse));
        Add("ISMEMOFETCHED", _ => Value.True);
        Add("GETCURSORADAPTER", c =>
        {
            var wa = c.Area(0);
            return wa.ViewState is ViewCursor { Adapter: { } ca } ? Value.Object(ca) : Value.Null;
        });
        Add("SETRESULTSET", c =>
        {
            var wa = c.Area(0);
            lock (ResultSets) ResultSets[c.Rt.Session.Id] = wa.Number;
            return N(wa.Number);
        });
        Add("GETRESULTSET", c => { lock (ResultSets) return N(ResultSets.GetValueOrDefault(c.Rt.Session.Id)); });
        Add("CLEARRESULTSET", c =>
        {
            lock (ResultSets) return N(ResultSets.Remove(c.Rt.Session.Id, out var n) ? n : 0);
        });

        // ---- Files ----
        Add("FDATE", c =>
        {
            var path = c.Rt.Session.ResolvePath(c.Str(0).Trim(), "");
            if (!File.Exists(path)) throw VfpException.FileNotFound(c.Str(0));
            var t = File.GetLastWriteTime(path);
            return c.Int(1, 0) == 1 ? Value.DateTimeOf(new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second)) : Value.DateOf(DateOnly.FromDateTime(t));
        });
        Add("FTIME", c =>
        {
            var path = c.Rt.Session.ResolvePath(c.Str(0).Trim(), "");
            if (!File.Exists(path)) throw VfpException.FileNotFound(c.Str(0));
            return S(File.GetLastWriteTime(path).ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        });
        Add("LOCFILE", c =>
        {
            var name = c.Str(0).Trim();
            if (FindOnPath(c.Rt, name) is { } found) return S(found.ToUpperInvariant());
            var chosen = c.Rt.FileDialog?.Invoke("open", c.Str(1, ""), c.Str(2, "Locate " + FileName(name)), name);
            if (string.IsNullOrEmpty(chosen)) throw VfpException.FileNotFound(name);
            return S(chosen.ToUpperInvariant());
        });
        Add("GETFILE", c => S(c.Rt.FileDialog?.Invoke("open", c.Str(0, ""), c.Str(1, "Open"), "") ?? ""));
        Add("PUTFILE", c => S(c.Rt.FileDialog?.Invoke("save", c.Str(2, ""), c.Str(0, "Save As"), c.Str(1, "")) ?? ""));
        Add("GETPICT", c => S(c.Rt.FileDialog?.Invoke("picture", c.Str(0, "bmp;jpg;gif;png;ico"), c.Str(1, "Picture"), "") ?? ""));
        Add("GETDIR", c =>
        {
            var dir = c.Rt.FileDialog?.Invoke("folder", "", c.Str(1, "Select Directory"), c.Str(0, c.Options.Default_));
            return S(string.IsNullOrEmpty(dir) ? "" : AddSeparator(dir).ToUpperInvariant());
        });
        Add("GETCP", c => N(c.Int(0, 1252)));
        Add("GETCOLOR", c => N(c.Rt.ColorDialog?.Invoke(c.Int(0, 0)) ?? -1));
        Add("GETFONT", c => S(c.Rt.FontDialog?.Invoke(c.Str(0, "Arial"), c.Int(1, 9), c.Str(2, "N")) ?? ""));

        // ---- Fonts ----
        Add("AFONT", c =>
        {
            if (!c.Has(1))
            {
                var arr = c.NewArray(0, StandardFonts.Length, 0);
                for (int i = 0; i < StandardFonts.Length; i++) arr[i + 1] = S(StandardFonts[i]);
                return Value.True;
            }
            var known = StandardFonts.Contains(c.Str(1).Trim(), StringComparer.OrdinalIgnoreCase);
            if (c.Has(2)) return L(known && c.Num(2) > 0);
            if (!known) return Value.False;
            // Scalable fonts: VFP returns a single element of -1.
            var sizes = c.NewArray(0, 1, 0);
            sizes[1] = N(-1);
            return Value.True;
        });
        Add("FONTMETRIC", c =>
        {
            var size = c.Has(2) ? c.Num(2) : 9;
            var px = size * 96 / 72;
            double height = Math.Round(px * 1.15), ascent = Math.Round(px * 0.92), descent = height - ascent;
            return N(c.Int(0) switch
            {
                1 => height, 2 => ascent, 3 => descent, 4 => Math.Round(px * 0.15), 5 => 0, 6 => Math.Round(px * 0.45),
                7 => Math.Round(px * 1.1), 8 => 400, 9 => 0, 10 => 0, 11 => 96, 12 => 96, 13 => 32, 14 => 255, 15 => 31,
                16 => 32, 17 => 0, 18 => 0, 19 => 0, 20 => 0, _ => 0,
            });
        });
        Add("TXTWIDTH", c =>
        {
            // In units of the font's average character width: wide and narrow characters count more and less.
            var text = c[0].Kind == ValueKind.Character ? c.Str(0) : TransformDefault(c[0], c.Options);
            var mono = c.Has(1) && c.Str(1).Contains("Courier", StringComparison.OrdinalIgnoreCase);
            return N(mono ? text.Length : Math.Round(text.Sum(CharWidth), 3), 3);
        });

        // ---- XML ----
        Add("CURSORTOXML", c => CursorToXml(c));
        Add("XMLTOCURSOR", c => XmlToCursor(c));
    }

    private static readonly string[] StandardFonts =
        ["Arial", "Arial Black", "Calibri", "Cambria", "Comic Sans MS", "Consolas", "Courier New", "Georgia", "Lucida Console",
         "MS Sans Serif", "Segoe UI", "Tahoma", "Times New Roman", "Trebuchet MS", "Verdana", "Wingdings"];

    private static double CharWidth(char ch) => ch switch
    {
        'i' or 'l' or 'j' or '\'' or '.' or ',' or ':' or ';' or '!' or '|' => 0.5,
        'f' or 't' or 'r' or 'I' or ' ' or '(' or ')' or '[' or ']' => 0.7,
        'm' or 'w' or 'M' or 'W' => 1.6,
        >= 'A' and <= 'Z' => 1.25,
        _ => 1.0,
    };

    private static string AddSeparator(string dir) =>
        dir.EndsWith(Path.DirectorySeparatorChar) || dir.EndsWith('/') || dir.EndsWith('\\') ? dir : dir + Path.DirectorySeparatorChar;

    private static void CodePage(int cp)
    {
        if (cp is < 0 or > 65535) throw VfpException.InvalidArgument();
    }

    /// <summary>The 1-based line of the first (or last) line containing <paramref name="search"/>; 0 if none.</summary>
    private static int LineOf(string text, string search, bool ignoreCase, bool last)
    {
        if (search.Length == 0) return 0;
        var lines = SplitLines(text);
        var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (last)
        {
            for (int i = lines.Length - 1; i >= 0; i--) if (lines[i].Contains(search, cmp)) return i + 1;
        }
        else
        {
            for (int i = 0; i < lines.Length; i++) if (lines[i].Contains(search, cmp)) return i + 1;
        }
        return 0;
    }

    /// <summary>NORMALIZE(): upper-cases names outside string literals and turns -> into '.'.</summary>
    private static string Normalize(string expr)
    {
        Parser.ParseExpression(expr); // syntax errors are reported, as in VFP
        var sb = new StringBuilder();
        char? quote = null;
        for (int i = 0; i < expr.Length; i++)
        {
            var ch = expr[i];
            if (quote != null)
            {
                sb.Append(ch);
                if (ch == quote) quote = null;
                continue;
            }
            if (ch is '"' or '\'') { quote = ch; sb.Append(ch); continue; }
            if (ch == '[' && (sb.Length == 0 || !char.IsLetterOrDigit(sb[^1]) && sb[^1] != ')' && sb[^1] != '_')) { quote = ']'; sb.Append(ch); continue; }
            if (ch == '-' && i + 1 < expr.Length && expr[i + 1] == '>') { sb.Append('.'); i++; continue; }
            sb.Append(char.ToUpperInvariant(ch));
        }
        return sb.ToString().Trim();
    }

    private static string SourceLine(string file, int line)
    {
        try
        {
            if (file.Length == 0 || !File.Exists(file) || line < 1) return "";
            return File.ReadLines(file).Skip(line - 1).FirstOrDefault()?.Trim() ?? "";
        }
        catch (IOException) { return ""; }
    }

    private static readonly Regex ProcPattern = new(@"^(\s*)(?:(?:PROTECTED|HIDDEN)\s+)?(PROC(?:E(?:D(?:U(?:R(?:E)?)?)?)?)?|FUNC(?:T(?:I(?:O(?:N)?)?)?)?)\s+([\w.]+)", RegexOptions.IgnoreCase);
    private static readonly Regex ClassPattern = new(@"^(\s*)DEFINE\s+CLASS\s+(\w+)", RegexOptions.IgnoreCase);
    private static readonly Regex DefinePattern = new(@"^(\s*)#DEFINE\s+(\w+)", RegexOptions.IgnoreCase);
    private static readonly Regex EndClassPattern = new(@"^\s*ENDD(?:E(?:F(?:I(?:N(?:E)?)?)?)?)?\b", RegexOptions.IgnoreCase);

    /// <summary>APROCINFO() rows. nType: 0 all, 1 procedures and functions, 2 classes, 3 methods, 4 #DEFINEs.</summary>
    private static List<(string Name, int Line, string Type, int Indent)> ProcInfo(string[] lines, int type)
    {
        var rows = new List<(string, int, string, int)>();
        string? cls = null;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            Match m;
            if ((m = ClassPattern.Match(line)).Success)
            {
                cls = m.Groups[2].Value;
                if (type is 0 or 2) rows.Add((cls, i + 1, "Class", m.Groups[1].Length));
            }
            else if (cls != null && EndClassPattern.IsMatch(line)) cls = null;
            else if ((m = DefinePattern.Match(line)).Success)
            {
                if (type is 0 or 4) rows.Add((m.Groups[2].Value, i + 1, "Define", m.Groups[1].Length));
            }
            else if ((m = ProcPattern.Match(line)).Success)
            {
                if (cls != null) { if (type is 0 or 3) rows.Add((cls + "." + m.Groups[3].Value, i + 1, "Method", m.Groups[1].Length)); }
                else if (type is 0 or 1) rows.Add((m.Groups[3].Value, i + 1, "Procedure", m.Groups[1].Length));
            }
        }
        return rows;
    }

    /// <summary>LOCFILE's search: the name as given, SET DEFAULT, then each SET PATH folder.</summary>
    private static string? FindOnPath(Interpreter rt, string name)
    {
        var candidates = new List<string> { Path.Combine(rt.Options.Default_, name) };
        candidates.AddRange(rt.Options.Path.Select(p => Path.Combine(Path.IsPathRooted(p) ? p : Path.Combine(rt.Options.Default_, p), FileName(name))));
        foreach (var c in candidates)
        {
            var found = File.Exists(c) ? c : DataSession.FindIgnoringCase(c);
            if (found != null) return Path.GetFullPath(found);
        }
        return null;
    }

    private static double SysMetric(Interpreter rt, int n) => n switch
    {
        1 => rt.ScreenSize.Width,
        2 => rt.ScreenSize.Height,
        3 or 4 => 4,           // sizable window border
        5 or 6 => 1,           // fixed border
        7 or 8 => 1,           // dialog frame
        9 => 23,               // caption height
        10 => 17,              // horizontal scroll bar height
        11 => 17,              // vertical scroll bar width
        12 or 13 => 32,        // icon
        14 or 15 => 32,        // cursor
        16 => 1,               // single-line border
        17 => 20,              // menu bar height
        18 or 19 => rt.ScreenSize.Width, // maximized window
        20 => rt.ScreenSize.Height,
        21 or 22 => 136, 23 => 39, // minimum window size
        24 or 25 => 18,        // title bar button
        26 => 0,               // mouse buttons swapped
        27 => 0, 28 => 0, 29 => 0, // DBCS, pen, mouse wheel
        30 => 3, 31 => 1,      // mouse buttons, mouse present
        32 => 0, 33 => 0, 34 => 1,
        _ => 0,
    };

    // ---- XML --------------------------------------------------------------------------------------

    /// <summary>
    /// CURSORTOXML(alias|area, cOutput [, nFormat [, nFlags [, nRecords [, cSchema]]]]): nFormat 1 element-centric (default),
    /// 2 attribute-centric, 3 raw; nFlags 512 writes cOutput as a file (otherwise the XML is stored in the variable named
    /// cOutput); cSchema "1" includes an inline XSD. Returns the number of records.
    /// </summary>
    private static Value CursorToXml(CallContext c)
    {
        var wa = c.Rt.ResolveWorkArea(c[0].Kind == ValueKind.Number ? c[0] : Value.String(c.Str(0).Trim()));
        if (!wa.InUse) throw VfpException.NoTableOpen();
        var format = c.Int(2, 1);
        var flags = c.Int(3, 0);
        var max = c.Int(4, 0);
        var schema = c.Has(5) ? c.Str(5).Trim() : "";
        var alias = wa.Alias.ToLowerInvariant();
        var fields = wa.Table.Fields;

        var root = new XElement("VFPData");
        if (schema == "1") root.Add(InlineSchema(alias, fields));
        var saved = (wa.RecNo, wa.Eof);
        int count = 0;
        wa.GoTop();
        while (!wa.Eof && (max <= 0 || count < max))
        {
            var row = new XElement(alias);
            for (int i = 0; i < fields.Count; i++)
            {
                var v = wa.Get(i);
                if (v.IsNull) continue;
                var name = fields[i].Name.ToLowerInvariant();
                var text = XmlText(v);
                if (format == 2) row.SetAttributeValue(name, text);
                else row.Add(new XElement(name, text));
            }
            root.Add(row);
            count++;
            wa.Skip();
        }
        if (saved.Eof) { wa.GoBottom(); if (!wa.Eof) wa.Skip(); }
        else wa.Go(saved.RecNo);

        var xml = "<?xml version = \"1.0\" encoding=\"Windows-1252\" standalone=\"yes\"?>\r\n" + root.ToString(format == 3 ? SaveOptions.DisableFormatting : SaveOptions.None).Replace("\n", "\r\n").Replace("\r\r", "\r");
        var output = c.Str(1).Trim();
        if ((flags & 512) != 0) File.WriteAllText(Path.Combine(c.Options.Default_, output), xml, Encoding.Latin1);
        else c.Rt.SetVariable(output, S(xml));
        return N(count);
    }

    private static string XmlText(Value v) => v.Kind switch
    {
        ValueKind.Character => v.AsString.TrimEnd(' '),
        ValueKind.Number => v.AsNumber.ToString(v.Decimals > 0 ? "F" + v.Decimals : "R", CultureInfo.InvariantCulture),
        ValueKind.Currency => v.AsCurrency.ToString("F4", CultureInfo.InvariantCulture),
        ValueKind.Logical => v.AsBool ? "true" : "false",
        ValueKind.Date => v.JulianDay == 0 ? "" : Julian.ToDate(v.JulianDay).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ValueKind.DateTime => v.JulianMs == 0 ? "" : Julian.ToDateTime(v.JulianMs).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        ValueKind.Binary => Convert.ToBase64String(v.AsBinary),
        _ => v.ToString() ?? "",
    };

    private static readonly XNamespace Xsd = "http://www.w3.org/2001/XMLSchema";

    private static XElement InlineSchema(string alias, IReadOnlyList<FieldDef> fields)
    {
        var seq = new XElement(Xsd + "sequence");
        foreach (var f in fields)
        {
            var el = new XElement(Xsd + "element", new XAttribute("name", f.Name.ToLowerInvariant()), new XAttribute("minOccurs", "0"));
            if (f.Nullable) el.Add(new XAttribute("nillable", "true"));
            XElement Restrict(string baseType, params XElement[] facets) =>
                new(Xsd + "simpleType", new XElement(Xsd + "restriction", new XAttribute("base", "xsd:" + baseType), facets));
            switch (char.ToUpperInvariant(f.Type))
            {
                case 'C' or 'V': el.Add(Restrict("string", new XElement(Xsd + "maxLength", new XAttribute("value", f.Width)))); break;
                case 'M': el.Add(Restrict("string", new XElement(Xsd + "maxLength", new XAttribute("value", 2147483647)))); break;
                case 'N' or 'F':
                    el.Add(Restrict("decimal", new XElement(Xsd + "totalDigits", new XAttribute("value", f.Width)),
                        new XElement(Xsd + "fractionDigits", new XAttribute("value", f.Decimals)))); break;
                case 'Y': el.Add(Restrict("decimal", new XElement(Xsd + "totalDigits", new XAttribute("value", 19)), new XElement(Xsd + "fractionDigits", new XAttribute("value", 4)))); break;
                case 'I': el.Add(new XAttribute("type", "xsd:int")); break;
                case 'B': el.Add(new XAttribute("type", "xsd:double")); break;
                case 'L': el.Add(new XAttribute("type", "xsd:boolean")); break;
                case 'D': el.Add(new XAttribute("type", "xsd:date")); break;
                case 'T': el.Add(new XAttribute("type", "xsd:dateTime")); break;
                default: el.Add(new XAttribute("type", "xsd:base64Binary")); break;
            }
            seq.Add(el);
        }
        return new XElement(Xsd + "schema", new XAttribute("id", "VFPData"), new XAttribute(XNamespace.Xmlns + "xsd", Xsd.NamespaceName),
            new XElement(Xsd + "element", new XAttribute("name", "VFPData"),
                new XElement(Xsd + "complexType", new XElement(Xsd + "choice", new XAttribute("maxOccurs", "unbounded"),
                    new XElement(Xsd + "element", new XAttribute("name", alias), new XElement(Xsd + "complexType", seq))))));
    }

    /// <summary>
    /// XMLTOCURSOR(cXML|cFile [, cCursor [, nFlags]]): nFlags 512 reads cXML as a file name. Field types come from an
    /// inline schema when there is one, otherwise from the data. Returns the number of records.
    /// </summary>
    private static Value XmlToCursor(CallContext c)
    {
        var src = c.Str(0);
        var flags = c.Int(2, 0);
        if ((flags & 512) != 0 || (!src.TrimStart().StartsWith('<') && File.Exists(c.Rt.Session.ResolvePath(src.Trim(), ""))))
            src = File.ReadAllText(c.Rt.Session.ResolvePath(src.Trim(), ""));
        XDocument doc;
        try { doc = XDocument.Parse(src.Trim()); }
        catch (XmlException ex) { throw new VfpException(2034, "XML parse error: " + ex.Message); }
        var root = doc.Root ?? throw new VfpException(2034, "XML parse error: no root element.");
        var schemaEl = root.Element(Xsd + "schema");
        var rows = root.Elements().Where(e => e.Name != Xsd + "schema").ToList();
        var cursorName = c.Has(1) && c.Str(1).Trim().Length > 0 ? c.Str(1).Trim() : rows.FirstOrDefault()?.Name.LocalName ?? "xmlresult";

        var fields = new List<FieldDef>();
        if (schemaEl != null)
        {
            var seq = schemaEl.Descendants(Xsd + "sequence").LastOrDefault();
            foreach (var el in seq?.Elements(Xsd + "element") ?? [])
                fields.Add(FieldFromSchema(el));
        }
        if (fields.Count == 0)
        {
            var names = new List<string>();
            foreach (var r in rows)
                foreach (var n in r.Elements().Select(e => e.Name.LocalName).Concat(r.Attributes().Select(a => a.Name.LocalName)))
                    if (!names.Contains(n, StringComparer.OrdinalIgnoreCase)) names.Add(n);
            foreach (var n in names) fields.Add(InferField(n, rows.Select(r => CellText(r, n)).Where(t => t != null).Select(t => t!).ToList()));
        }
        if (fields.Count == 0) throw new VfpException(2034, "XML parse error: the document has no fields.");

        var existing = c.Rt.Session.FindAlias(cursorName);
        if (existing != null) existing.Close();
        var area = existing?.Number ?? (c.Rt.Session.Current.InUse ? c.Rt.Session.FreeArea() : c.Rt.Session.CurrentAreaNumber);
        var wa = c.Rt.Session.CreateCursor(new TableSchema(cursorName, fields), area);
        var tfields = wa.Table.Fields;
        foreach (var r in rows)
        {
            var vals = new Value[tfields.Count];
            for (int i = 0; i < tfields.Count; i++)
            {
                var text = CellText(r, tfields[i].Name);
                vals[i] = text == null ? (tfields[i].Nullable ? Value.Null : tfields[i].BlankValue()) : ParseXmlValue(tfields[i], text);
            }
            wa.Table.Append(vals, false);
        }
        c.Rt.Session.Select(wa.Number);
        wa.GoTop();
        return N(rows.Count);
    }

    private static string? CellText(XElement row, string name) =>
        row.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value
        ?? row.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static FieldDef FieldFromSchema(XElement el)
    {
        var name = (string?)el.Attribute("name") ?? throw new VfpException(2034, "XML schema element has no name.");
        var nullable = (string?)el.Attribute("nillable") == "true";
        var type = ((string?)el.Attribute("type"))?.Split(':').Last();
        var restriction = el.Descendants(Xsd + "restriction").FirstOrDefault();
        int Facet(string f, int fallback) => int.TryParse((string?)restriction?.Element(Xsd + f)?.Attribute("value"), out var v) ? v : fallback;
        if (restriction != null) type = ((string?)restriction.Attribute("base"))?.Split(':').Last();
        FieldDef f = type switch
        {
            "string" => Facet("maxLength", 255) is var len && len > 254 ? new FieldDef(name, 'M') : new FieldDef(name, 'C', Math.Max(1, len)),
            "decimal" => Facet("fractionDigits", 0) == 4 && Facet("totalDigits", 0) == 19 ? new FieldDef(name, 'Y') : new FieldDef(name, 'N', Facet("totalDigits", 20), Facet("fractionDigits", 0)),
            "int" or "integer" or "short" or "long" => new FieldDef(name, 'I'),
            "double" or "float" => new FieldDef(name, 'B'),
            "boolean" => new FieldDef(name, 'L'),
            "date" => new FieldDef(name, 'D'),
            "dateTime" => new FieldDef(name, 'T'),
            "base64Binary" => new FieldDef(name, 'W'),
            _ => new FieldDef(name, 'M'),
        };
        return f with { Nullable = nullable };
    }

    private static FieldDef InferField(string name, List<string> values)
    {
        var nonEmpty = values.Where(v => v.Length > 0).ToList();
        if (nonEmpty.Count > 0 && nonEmpty.All(v => v is "true" or "false")) return new FieldDef(name, 'L');
        if (nonEmpty.Count > 0 && nonEmpty.All(v => Regex.IsMatch(v, @"^\d{4}-\d{2}-\d{2}$"))) return new FieldDef(name, 'D');
        if (nonEmpty.Count > 0 && nonEmpty.All(v => Regex.IsMatch(v, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?$"))) return new FieldDef(name, 'T');
        if (nonEmpty.Count > 0 && nonEmpty.All(v => Regex.IsMatch(v, @"^-?\d+(\.\d+)?$")))
        {
            var dec = nonEmpty.Max(v => v.Contains('.') ? v.Length - v.IndexOf('.') - 1 : 0);
            var width = nonEmpty.Max(v => v.Length) + (dec == 0 ? 0 : 0);
            return new FieldDef(name, 'N', Math.Min(20, Math.Max(width, dec + 2)), dec);
        }
        var max = values.Count == 0 ? 1 : Math.Max(1, values.Max(v => v.Length));
        return max > 254 ? new FieldDef(name, 'M') : new FieldDef(name, 'C', max);
    }

    private static Value ParseXmlValue(FieldDef f, string text)
    {
        if (text.Length == 0) return f.BlankValue();
        switch (char.ToUpperInvariant(f.Type))
        {
            case 'N' or 'F' or 'I' or 'B':
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? Value.Number(d, f.Decimals) : f.BlankValue();
            case 'Y':
                return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? Value.Currency(m) : f.BlankValue();
            case 'L': return Value.Logical(text is "true" or "1" or "T" or ".T.");
            case 'D':
                return DateOnly.TryParse(text[..Math.Min(10, text.Length)], CultureInfo.InvariantCulture, out var dt) ? Value.DateOf(dt) : f.BlankValue();
            case 'T':
                return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? Value.DateTimeOf(t) : f.BlankValue();
            case 'W' or 'Q' or 'G':
                try { return Value.Binary(Convert.FromBase64String(text)); } catch (FormatException) { return f.BlankValue(); }
            default: return Value.String(text);
        }
    }
}
