using System.Text;
using System.Text.RegularExpressions;
using JoePro.Core;
using JoePro.Legacy.Formats;

namespace JoePro.Documents;

public enum FindingStatus
{
    /// <summary>Converted as-is (reported only for context).</summary>
    Info,
    /// <summary>Converted with a change that keeps behavior (colors to RGB(), paths, file extensions).</summary>
    Changed,
    /// <summary>Converted, but a person should check it.</summary>
    NeedsReview,
    /// <summary>Not supported by Joe Pro; kept in the file but will not work as in VFP.</summary>
    Unsupported,
}

public sealed record ConversionFinding(FindingStatus Status, string Object, string Message);

public sealed class ConversionResult
{
    public required ClassFile File { get; init; }
    public List<ConversionFinding> Findings { get; } = new();
    public string SourcePath { get; init; } = "";
}

/// <summary>
/// Converts VFP designer tables to canonical DEFINE CLASS documents: forms (.SCX/.SCT) to .jpform and class
/// libraries (.VCX/.VCT) to .jpclass. Inheritance across libraries is preserved (not flattened): a class
/// based on "base.vcx" becomes "AS … OF base.jpclass".
/// </summary>
public static class LegacyFormConverter
{
    public static ConversionResult Convert(string path) =>
        Path.GetExtension(path).Equals(".vcx", StringComparison.OrdinalIgnoreCase) ? ConvertClassLibrary(path) : ConvertForm(path);

    // ---- Rows -------------------------------------------------------------------------------

    private sealed class Row
    {
        public required Dictionary<string, string> Columns { get; init; }
        public string this[string name] => Columns.TryGetValue(name, out var v) ? v : "";
        public string Platform => this["PLATFORM"].Trim();
        public string UniqueId => this["UNIQUEID"].Trim();
        public string Class => this["CLASS"].Trim();
        public string ClassLoc => this["CLASSLOC"].Trim();
        public string BaseClass => this["BASECLASS"].Trim();
        public string ObjName => this["OBJNAME"].Trim();
        public string Parent => this["PARENT"].Trim();
        public string Properties => this["PROPERTIES"];
        public string ProtectedList => this["PROTECTED"];
        public string Methods => this["METHODS"];
        public string Reserved1 => this["RESERVED1"].Trim();
        public string Reserved2 => this["RESERVED2"].Trim();
        public string Reserved3 => this["RESERVED3"];
        public string Reserved6 => this["RESERVED6"].Trim();
        public string Reserved8 => this["RESERVED8"].Trim();
        public string Ole2 => this["OLE2"];
    }

    private static List<Row> ReadRows(string path)
    {
        using var table = DbfTable.Open(path);
        var names = table.Fields.Select(f => f.Name.ToUpperInvariant()).ToList();
        var rows = new List<Row>();
        foreach (var rec in table.Records())
        {
            if (rec.Deleted) continue;
            var cols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < names.Count; i++)
                cols[names[i]] = rec.Values[i].Kind == ValueKind.Character ? rec.Values[i].AsString : rec.Values[i].Kind == ValueKind.Binary ? "" : rec.Values[i].ToString() ?? "";
            rows.Add(new Row { Columns = cols });
        }
        return rows;
    }

    // ---- Forms ------------------------------------------------------------------------------

    public static ConversionResult ConvertForm(string scxPath)
    {
        var rows = ReadRows(scxPath).Where(r => !r.Platform.Equals("COMMENT", StringComparison.OrdinalIgnoreCase)).ToList();
        var file = new ClassFile { Kind = ClassFileKind.Form };
        var result = new ConversionResult { File = file, SourcePath = scxPath };
        var formSet = rows.FirstOrDefault(r => r.BaseClass.Equals("formset", StringComparison.OrdinalIgnoreCase));
        var top = formSet ?? rows.FirstOrDefault(r => r.BaseClass.Equals("form", StringComparison.OrdinalIgnoreCase))
            ?? throw new FormatException($"{Path.GetFileName(scxPath)} has no form record.");

        var cls = new ClassDocument { Name = ClassNameFromFile(scxPath) };
        file.Classes.Add(cls);
        ApplyClassHeader(cls, top, scxPath, result);
        if (!top.ObjName.Equals(cls.Name, StringComparison.OrdinalIgnoreCase)) cls.Properties["Name"] = Literal.Quote(top.ObjName);
        ApplyObjectRow(cls, "", top, result, isClassRow: true);
        if (!string.IsNullOrEmpty(top.Reserved8)) AddInclude(file, top.Reserved8, result);

        // Everything else hangs below the top object; the data environment is a member named after its row.
        var prefix = top.ObjName;
        foreach (var row in rows)
        {
            if (ReferenceEquals(row, top)) continue;
            if (row.Reserved1.Equals("RESERVED", StringComparison.OrdinalIgnoreCase)) continue;
            string path;
            if (row.Parent.Length == 0) path = row.ObjName; // data environment of a form without a formset
            else if (row.Parent.Equals(prefix, StringComparison.OrdinalIgnoreCase)) path = row.ObjName;
            else if (row.Parent.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)) path = row.Parent[(prefix.Length + 1)..] + "." + row.ObjName;
            else path = row.Parent + "." + row.ObjName; // e.g. Dataenvironment.Cursor1
            AddMember(cls, path, row, scxPath, result);
        }
        return result;
    }

    // ---- Class libraries -------------------------------------------------------------------

    public static ConversionResult ConvertClassLibrary(string vcxPath)
    {
        var rows = ReadRows(vcxPath);
        var file = new ClassFile { Kind = ClassFileKind.ClassLibrary };
        var result = new ConversionResult { File = file, SourcePath = vcxPath };
        var headers = rows.Where(r => r.Reserved1.Equals("Class", StringComparison.OrdinalIgnoreCase)
                                      && !r.Platform.Equals("COMMENT", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var header in headers)
        {
            var cls = new ClassDocument { Name = header.ObjName };
            file.Classes.Add(cls);
            ApplyClassHeader(cls, header, vcxPath, result);
            if (header.Reserved2.Contains("OLEPUBLIC", StringComparison.OrdinalIgnoreCase)) cls.OlePublic = true;
            ApplyObjectRow(cls, "", header, result, isClassRow: true);
            if (!string.IsNullOrEmpty(header.Reserved8)) AddInclude(file, header.Reserved8, result);
            foreach (var row in rows)
            {
                if (ReferenceEquals(row, header) || row.Platform.Equals("COMMENT", StringComparison.OrdinalIgnoreCase)) continue;
                if (row.Reserved1.Length > 0) continue;
                string path;
                if (row.Parent.Equals(cls.Name, StringComparison.OrdinalIgnoreCase)) path = row.ObjName;
                else if (row.Parent.StartsWith(cls.Name + ".", StringComparison.OrdinalIgnoreCase)) path = row.Parent[(cls.Name.Length + 1)..] + "." + row.ObjName;
                else continue;
                AddMember(cls, path, row, vcxPath, result);
            }
        }
        if (headers.Count == 0) result.Findings.Add(new(FindingStatus.NeedsReview, Path.GetFileName(vcxPath), "The class library contains no classes."));
        return result;
    }

    // ---- Shared -----------------------------------------------------------------------------

    private static void ApplyClassHeader(ClassDocument cls, Row row, string sourcePath, ConversionResult result)
    {
        var isBase = row.ClassLoc.Length == 0;
        cls.ParentClass = isBase ? CanonicalBaseClass(row.Class.Length > 0 ? row.Class : row.BaseClass) : row.Class;
        if (!isBase)
        {
            var lib = ConvertLibraryPath(row.ClassLoc, cls.Name, result);
            // A parent class in the same library needs no OF clause.
            if (!Path.GetFileNameWithoutExtension(row.ClassLoc).Equals(Path.GetFileNameWithoutExtension(sourcePath), StringComparison.OrdinalIgnoreCase)
                || !Path.GetExtension(sourcePath).Equals(".vcx", StringComparison.OrdinalIgnoreCase))
                cls.ParentLibrary = lib;
        }
        if (row.Reserved6.Equals("Foxels", StringComparison.OrdinalIgnoreCase))
            result.Findings.Add(new(FindingStatus.NeedsReview, cls.Name, "Uses ScaleMode Foxels; coordinates were kept as they are and are interpreted as pixels."));
    }

    private static void AddMember(ClassDocument cls, string path, Row row, string sourcePath, ConversionResult result)
    {
        var isBase = row.ClassLoc.Length == 0;
        var member = new MemberDocument
        {
            Path = path,
            Class = isBase ? CanonicalBaseClass(row.Class.Length > 0 ? row.Class : row.BaseClass) : row.Class,
            ClassLibrary = isBase ? null : ConvertLibraryPath(row.ClassLoc, path, result),
        };
        cls.Members.Add(member);
        var baseClass = row.BaseClass.ToLowerInvariant();
        if (baseClass is "olecontrol" or "oleboundcontrol")
        {
            var ole = OleClassName(row.Ole2);
            result.Findings.Add(new(FindingStatus.Unsupported, path,
                $"ActiveX control{(ole != null ? " (" + ole + ")" : "")}: ActiveX hosting is Windows-only and not implemented yet. The object is kept so its code still compiles."));
            if (ole != null && !member.Properties.Contains("OLEClass")) member.Properties["OLEClass"] = Literal.Quote(ole);
        }
        ApplyObjectRow(cls, path, row, result, isClassRow: false, member);
    }

    /// <summary>Properties, custom members, visibility and methods of one row.</summary>
    private static void ApplyObjectRow(ClassDocument cls, string path, Row row, ConversionResult result, bool isClassRow, MemberDocument? member = null)
    {
        var target = isClassRow ? cls.Properties : member!.Properties;
        var display = path.Length == 0 ? cls.Name : path;
        foreach (var (name, raw) in ParseProperties(row.Properties, display, result))
        {
            if (name.Equals("Name", StringComparison.OrdinalIgnoreCase) && !isClassRow) continue; // implied by ADD OBJECT
            if (name.Equals("DoCreate", StringComparison.OrdinalIgnoreCase)) continue;             // designer bookkeeping
            target[name] = ConvertValue(name, raw, display, result);
        }
        if (isClassRow)
        {
            foreach (var line in Lines(row.Reserved3))
            {
                var text = line.Trim();
                if (text.Length == 0) continue;
                var token = text.Split((char[]?)null, 2)[0];
                if (token.StartsWith('*'))
                {
                    var m = token[1..];
                    if (cls.FindMethod(m) == null && !Regex.IsMatch(row.Methods, $@"(?im)^\s*PROC\w*\s+{Regex.Escape(m)}\s*$"))
                        cls.Methods.Add(new MethodDocument { Name = m }); // declared custom method with no code yet
                }
                else if (token.StartsWith('^'))
                {
                    var a = Regex.Match(token[1..], @"^(\w+)\[(\d+)\s*,\s*(\d+)\]");
                    if (!a.Success) a = Regex.Match(token[1..], @"^(\w+)\[(\d+)\]()");
                    if (a.Success)
                    {
                        var rows = Math.Max(1, int.Parse(a.Groups[2].Value));
                        var colsText = a.Groups[3].Value is { Length: > 0 } c && int.Parse(c) > 0 ? "," + c : "";
                        cls.Arrays.Add((a.Groups[1].Value, rows + colsText));
                        cls.Properties.Remove(a.Groups[1].Value);
                    }
                }
                else if (!cls.Properties.Contains(token) && Regex.IsMatch(token, @"^\w+$"))
                    cls.Properties[token] = ".F."; // new property that kept its default value
            }
            foreach (var line in Lines(row.ProtectedList))
            {
                var n = line.Trim();
                if (n.Length == 0) continue;
                if (n.EndsWith('^')) cls.Hidden.Add(n[..^1]); else cls.Protected.Add(n);
            }
        }
        foreach (var method in ParseMethods(row.Methods))
        {
            method.Name = path.Length == 0 ? method.Name : path + "." + method.Name;
            if (isClassRow && cls.Protected.Contains(method.Name, StringComparer.OrdinalIgnoreCase)) method.Visibility = "PROTECTED";
            if (isClassRow && cls.Hidden.Contains(method.Name, StringComparer.OrdinalIgnoreCase)) method.Visibility = "HIDDEN";
            cls.Methods.RemoveAll(m => m.Name.Equals(method.Name, StringComparison.OrdinalIgnoreCase));
            cls.Methods.Add(method);
        }
        if (isClassRow)
        {
            // Visibility of methods is written on the PROCEDURE line; keep only properties in the lists.
            cls.Protected.RemoveAll(n => cls.FindMethod(n) is { Visibility: "PROTECTED" });
            cls.Hidden.RemoveAll(n => cls.FindMethod(n) is { Visibility: "HIDDEN" });
        }
    }

    /// <summary>"Name = value" lines. Lines that do not start a new assignment continue the previous value.</summary>
    private static List<(string Name, string Value)> ParseProperties(string memo, string obj, ConversionResult result)
    {
        var list = new List<(string, string)>();
        foreach (var line in Lines(memo))
        {
            if (line.Trim().Length == 0) continue;
            var m = Regex.Match(line, @"^\s*([A-Za-z_][\w\.]*)\s*=\s?(.*)$");
            if (m.Success) list.Add((m.Groups[1].Value, m.Groups[2].Value));
            else if (list.Count > 0)
            {
                list[^1] = (list[^1].Item1, list[^1].Item2 + "\n" + line);
                // TODO(oracle): confirm how VFP stores property values longer than 255 characters.
            }
            else result.Findings.Add(new(FindingStatus.NeedsReview, obj, $"Unrecognized property line skipped: {line.Trim()}"));
        }
        return list;
    }

    private static readonly Regex ColorValue = new(@"^\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*$");
    private static readonly HashSet<string> PathProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "Picture", "Icon", "MouseIcon", "DragIcon", "DownPicture", "DisabledPicture", "PictureSelectionDisplay",
    };

    /// <summary>Turns a designer property value into DEFINE CLASS expression text.</summary>
    public static string ConvertValue(string name, string raw, string obj, ConversionResult result)
    {
        var v = raw.Trim();
        var leaf = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
        if (v.Contains('\n'))
        {
            result.Findings.Add(new(FindingStatus.Changed, obj, $"{name}: multi-line value written as a concatenation with CHR(13)+CHR(10)."));
            return string.Join(" + CHR(13) + CHR(10) + ", v.Replace("\r", "").Split('\n').Select(Literal.Quote));
        }
        if (v.StartsWith('=')) return v[1..].Trim(); // an expression entered in the property sheet
        if (leaf.EndsWith("Color", StringComparison.OrdinalIgnoreCase) && ColorValue.Match(v) is { Success: true } c)
            return $"RGB({c.Groups[1].Value},{c.Groups[2].Value},{c.Groups[3].Value})";
        if (PathProperties.Contains(leaf))
        {
            var text = Literal.TryParseString(v, out var s) ? s : v;
            if (text.Contains('\\'))
            {
                result.Findings.Add(new(FindingStatus.Changed, obj, $"{name}: path separators changed to '/'."));
                text = text.Replace('\\', '/');
            }
            return Literal.Quote(text);
        }
        if (leaf.Equals("CursorSource", StringComparison.OrdinalIgnoreCase) || leaf.Equals("Database", StringComparison.OrdinalIgnoreCase))
        {
            var text = Literal.TryParseString(v, out var s) ? s : v;
            var converted = Regex.Replace(text.Replace('\\', '/'), @"\.dbc$", ".jpdb", RegexOptions.IgnoreCase);
            converted = Regex.Replace(converted, @"\.dbf$", ".jpt", RegexOptions.IgnoreCase);
            if (converted != text) result.Findings.Add(new(FindingStatus.Changed, obj, $"{name}: now refers to the imported Joe Pro file ({converted})."));
            return Literal.Quote(converted);
        }
        if (Literal.TryParse(v) != null || Literal.IsDateLiteral(v)) return v;
        if (v.Length == 0) return "\"\"";
        return Literal.Quote(v); // unquoted text (captions, file names)
    }

    /// <summary>PROCEDURE … ENDPROC blocks of a METHODS memo.</summary>
    private static List<MethodDocument> ParseMethods(string memo)
    {
        var list = new List<MethodDocument>();
        MethodDocument? current = null;
        var body = new List<string>();
        void Flush()
        {
            if (current == null) return;
            while (body.Count > 0 && body[^1].Trim().Length == 0) body.RemoveAt(body.Count - 1);
            while (body.Count > 0 && body[0].Trim().Length == 0) body.RemoveAt(0);
            current.Body.AddRange(ClassFileReader.Dedent(body));
            list.Add(current);
            current = null;
            body.Clear();
        }
        // VFP writes the PROCEDURE/ENDPROC lines itself: uppercase, at column 0. Code inside TEXT … ENDTEXT
        // (generated programs) can contain its own "procedure" lines, so those blocks are skipped.
        bool inText = false;
        foreach (var line in Lines(memo))
        {
            if (current != null && TextBlocks.Track(line, ref inText)) { body.Add(line); continue; }
            var start = Regex.Match(line, @"^(PROCEDURE|FUNCTION) ([\w\.]+)\s*$");
            if (start.Success)
            {
                Flush();
                current = new MethodDocument { Name = start.Groups[2].Value, IsFunction = start.Groups[1].Value == "FUNCTION" };
                continue;
            }
            if (Regex.IsMatch(line, @"^END(PROC|FUNC)\s*$")) { Flush(); continue; }
            if (current != null) body.Add(line);
        }
        Flush();
        return list;
    }

    private static string ConvertLibraryPath(string classLoc, string obj, ConversionResult result)
    {
        var p = classLoc.Replace('\\', '/');
        if (Regex.IsMatch(p, @"^[A-Za-z]:/") || p.StartsWith("//"))
        {
            result.Findings.Add(new(FindingStatus.NeedsReview, obj, $"Class library path '{classLoc}' is absolute; only the file name was kept."));
            p = Path.GetFileName(p);
        }
        return Regex.Replace(p, @"\.vcx$", ".jpclass", RegexOptions.IgnoreCase);
    }

    private static void AddInclude(ClassFile file, string include, ConversionResult result)
    {
        var p = include.Replace('\\', '/');
        if (!file.Includes.Contains(p, StringComparer.OrdinalIgnoreCase)) file.Includes.Add(p);
    }

    private static string? OleClassName(string ole2)
    {
        var m = Regex.Match(ole2, @"OLEObject\s*=\s*(.+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static IEnumerable<string> Lines(string memo) => memo.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static string ClassNameFromFile(string path)
    {
        var n = Regex.Replace(Path.GetFileNameWithoutExtension(path), @"[^\w]", "_");
        return char.IsDigit(n[0]) ? "_" + n : n;
    }

    private static readonly string[] BaseClassNames =
    [
        "CheckBox", "Collection", "Column", "ComboBox", "CommandButton", "CommandGroup", "Container", "Control", "Cursor",
        "CursorAdapter", "Custom", "DataEnvironment", "EditBox", "Empty", "Exception", "Form", "FormSet", "Grid", "Header",
        "Hyperlink", "Image", "Label", "Line", "ListBox", "OLEBoundControl", "OLEControl", "OptionButton", "OptionGroup", "Page",
        "PageFrame", "ProjectHook", "Relation", "ReportListener", "Separator", "Session", "Shape", "Spinner", "TextBox", "Timer",
        "Toolbar", "XMLAdapter", "XMLField", "XMLTable",
    ];

    public static string CanonicalBaseClass(string name) =>
        BaseClassNames.FirstOrDefault(b => b.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name;
}
