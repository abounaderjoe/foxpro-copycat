using System.Text;
using System.Text.RegularExpressions;

namespace JoePro.Documents;

/// <summary>
/// Reads .jpform/.jpclass files. Accepts the canonical subset the writer produces plus common hand-written
/// variations (keyword abbreviations, quoted object names, several properties per line, comments), so any
/// file can be loaded and then saved canonically. Comments outside method bodies are not kept.
/// </summary>
public static partial class ClassFileReader
{
    public static ClassFile Load(string path)
    {
        var kind = path.EndsWith(".jpclass", StringComparison.OrdinalIgnoreCase) ? ClassFileKind.ClassLibrary : ClassFileKind.Form;
        return Parse(File.ReadAllText(path), kind);
    }

    public static ClassFile Parse(string text, ClassFileKind? kind = null)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var file = new ClassFile
        {
            Kind = kind ?? (lines.FirstOrDefault()?.Contains("class library", StringComparison.OrdinalIgnoreCase) == true ? ClassFileKind.ClassLibrary : ClassFileKind.Form),
        };
        int i = 0;
        while (i < lines.Length)
        {
            var logical = ReadLogical(lines, ref i, out var startLine);
            if (logical == null) continue;
            var inc = Regex.Match(logical, @"^#INCL(?:U|UD|UDE)?\s+(.+)$", RegexOptions.IgnoreCase);
            if (inc.Success)
            {
                file.Includes.Add(Unquote(inc.Groups[1].Value));
                continue;
            }
            var dc = DefineClassRx().Match(logical);
            if (!dc.Success) throw new FormatException($"Line {startLine}: expected DEFINE CLASS.");
            var cls = new ClassDocument
            {
                Name = dc.Groups["name"].Value,
                ParentClass = dc.Groups["parent"].Value,
                ParentLibrary = dc.Groups["lib"].Success ? Unquote(dc.Groups["lib"].Value.Trim()) : null,
                OlePublic = dc.Groups["ole"].Success,
            };
            file.Classes.Add(cls);
            ReadClassBody(lines, ref i, cls, startLine);
        }
        return file;
    }

    private static void ReadClassBody(string[] lines, ref int i, ClassDocument cls, int classLine)
    {
        while (true)
        {
            if (i >= lines.Length) throw new FormatException($"Line {classLine}: missing ENDDEFINE for class {cls.Name}.");
            var raw = lines[i];
            var meta = MetaRx().Match(raw);
            if (meta.Success)
            {
                i++;
                var text = ClassFileWriter.UnescapeMeta(meta.Groups["text"].Value.TrimEnd());
                switch (meta.Groups["key"].Value.ToUpperInvariant())
                {
                    case "DESCRIPTION": cls.Description = text; break;
                    case "ICON": cls.Icon = text; break;
                    case "CONTAINERICON": cls.ContainerIcon = text; break;
                    default: cls.MemberDescriptions[meta.Groups["member"].Value] = text; break;
                }
                continue;
            }
            var proc = ProcRx().Match(raw);
            if (proc.Success)
            {
                i++;
                var method = new MethodDocument
                {
                    Name = proc.Groups["name"].Value,
                    Visibility = proc.Groups["vis"].Success ? proc.Groups["vis"].Value.ToUpperInvariant() : null,
                    IsFunction = proc.Groups["kw"].Value.StartsWith("F", StringComparison.OrdinalIgnoreCase),
                    Parameters = proc.Groups["params"].Success ? proc.Groups["params"].Value.Trim() : null,
                };
                var body = new List<string>();
                bool inText = false;
                while (i < lines.Length)
                {
                    if (TextBlocks.Track(lines[i], ref inText)) { body.Add(lines[i]); i++; continue; }
                    if (EndProcRx().IsMatch(lines[i])) { i++; break; }
                    if (ProcRx().IsMatch(lines[i]) || EndDefineRx().IsMatch(lines[i])) break;
                    body.Add(lines[i]);
                    i++;
                }
                while (body.Count > 0 && body[^1].Trim().Length == 0) body.RemoveAt(body.Count - 1);
                while (body.Count > 0 && body[0].Trim().Length == 0) body.RemoveAt(0);
                method.Body.AddRange(Dedent(body));
                cls.Methods.RemoveAll(m => m.Name.Equals(method.Name, StringComparison.OrdinalIgnoreCase));
                cls.Methods.Add(method); // a method's visibility stays on its PROCEDURE line, not in the PROTECTED/HIDDEN lists
                continue;
            }
            var logical = ReadLogical(lines, ref i, out var lineNo);
            if (logical == null) continue;
            if (EndDefineRx().IsMatch(logical)) return;

            var ao = AddObjectRx().Match(logical);
            if (ao.Success)
            {
                var member = new MemberDocument
                {
                    Path = Unquote(ao.Groups["name"].Value),
                    Class = Unquote(ao.Groups["class"].Value),
                    ClassLibrary = ao.Groups["lib"].Success ? Unquote(ao.Groups["lib"].Value.Trim()) : null,
                    Protected = ao.Groups["prot"].Success,
                    NoInit = ao.Groups["noinit"].Success,
                };
                if (ao.Groups["with"].Success)
                    foreach (var (name, value) in SplitAssignments(ao.Groups["with"].Value, lineNo)) member.Properties[name] = value;
                cls.Members.Add(member);
                continue;
            }
            var vis = VisibilityRx().Match(logical);
            if (vis.Success)
            {
                var list = vis.Groups[1].Value.StartsWith("P", StringComparison.OrdinalIgnoreCase) ? cls.Protected : cls.Hidden;
                foreach (var n in vis.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (!list.Contains(n, StringComparer.OrdinalIgnoreCase)) list.Add(n);
                continue;
            }
            var dim = DimensionRx().Match(logical);
            if (dim.Success)
            {
                foreach (var part in SplitTopLevel(dim.Groups[1].Value))
                {
                    var a = ArrayRx().Match(part.Trim());
                    if (!a.Success) throw new FormatException($"Line {lineNo}: invalid DIMENSION '{part.Trim()}'.");
                    cls.Arrays.RemoveAll(x => x.Name.Equals(a.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
                    cls.Arrays.Add((a.Groups[1].Value, Regex.Replace(a.Groups[2].Value, @"\s+", "")));
                }
                continue;
            }
            foreach (var (name, value) in SplitAssignments(logical, lineNo)) cls.Properties[name] = value;
        }
    }

    /// <summary>The next logical line (joining ';' continuations), skipping blank and comment lines. Null at a skipped line.</summary>
    private static string? ReadLogical(string[] lines, ref int i, out int startLine)
    {
        startLine = i + 1;
        var sb = new StringBuilder();
        while (i < lines.Length)
        {
            var line = StripComment(lines[i]).TrimEnd();
            i++;
            if (sb.Length == 0)
            {
                var t = line.TrimStart();
                if (t.Length == 0 || t.StartsWith('*') || Regex.IsMatch(t, @"^NOTE\b", RegexOptions.IgnoreCase)) return null;
            }
            if (line.EndsWith(';'))
            {
                sb.Append(line[..^1].Trim()).Append(' ');
                continue;
            }
            sb.Append(line.Trim());
            break;
        }
        return sb.ToString().Trim();
    }

    /// <summary>Removes a trailing &amp;&amp; comment that is not inside a string.</summary>
    private static string StripComment(string line)
    {
        char quote = '\0';
        for (int k = 0; k < line.Length - 1; k++)
        {
            var c = line[k];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c == '&' && line[k + 1] == '&') return line[..k];
        }
        return line;
    }

    /// <summary>"a = 1, b.c = "x, y"" → [(a, 1), (b.c, "x, y")].</summary>
    internal static IEnumerable<(string Name, string Value)> SplitAssignments(string text, int lineNo)
    {
        foreach (var part in SplitTopLevel(text))
        {
            var p = part.Trim();
            if (p.Length == 0) continue;
            var eq = AssignmentRx().Match(p);
            if (!eq.Success) throw new FormatException($"Line {lineNo}: expected 'property = value' but found '{p}'.");
            yield return (eq.Groups[1].Value, eq.Groups[2].Value.Trim());
        }
    }

    /// <summary>Splits on commas outside strings, parentheses and brackets.</summary>
    internal static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        char quote = '\0';
        for (int k = 0; k < text.Length; k++)
        {
            var c = text[k];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            switch (c)
            {
                case '"' or '\'': quote = c; break;
                case '(' or '[' or '{': depth++; break;
                case ')' or ']' or '}': depth--; break;
                case ',' when depth == 0:
                    parts.Add(text[start..k]);
                    start = k + 1;
                    break;
            }
        }
        parts.Add(text[start..]);
        return parts;
    }

    /// <summary>Removes the leading whitespace common to all non-blank lines and trailing whitespace.</summary>
    public static List<string> Dedent(IReadOnlyList<string> lines)
    {
        string? prefix = null;
        foreach (var l in lines)
        {
            if (l.Trim().Length == 0) continue;
            var lead = l[..(l.Length - l.TrimStart().Length)];
            if (prefix == null) prefix = lead;
            else
            {
                int n = 0;
                while (n < prefix.Length && n < lead.Length && prefix[n] == lead[n]) n++;
                prefix = prefix[..n];
            }
        }
        prefix ??= "";
        return lines.Select(l => l.Trim().Length == 0 ? "" : l[prefix.Length..].TrimEnd()).ToList();
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'') || (s[0] == '[' && s[^1] == ']')) ? s[1..^1] : s;
    }

    [GeneratedRegex(@"^\s*DEFINE\s+CLASS\s+(?<name>\w+)\s+AS\s+(?<parent>\w+)(?:\s+OF\s+(?<lib>(?:""[^""]*""|'[^']*'|\S+)))?(?<ole>\s+OLEPUBLIC)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DefineClassRx();

    [GeneratedRegex(@"^\s*\*--\s(?<key>Description|Icon|ContainerIcon|Member\s+(?<member>\w+)):\s?(?<text>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex MetaRx();

    [GeneratedRegex(@"^\s*ENDD(?:E|EF|EFI|EFIN|EFINE)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex EndDefineRx();

    [GeneratedRegex(@"^\s*(?:(?<vis>PROTECTED|HIDDEN)\s+)?(?<kw>PROC(?:E|ED|EDU|EDUR|EDURE)?|FUNC(?:T|TI|TIO|TION)?)\s+(?<name>[\w\.]+)\s*(?:\((?<params>[^)]*)\))?\s*(?:&&.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ProcRx();

    [GeneratedRegex(@"^\s*END(?:P|PR|PRO|PROC|F|FU|FUN|FUNC)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EndProcRx();

    [GeneratedRegex(@"^ADD\s+OBJECT\s+(?:(?<prot>PROTECTED)\s+)?(?<name>'[^']+'|""[^""]+""|[\w\.]+)\s+AS\s+(?<class>\w+)(?:\s+OF\s+(?<lib>(?:""[^""]*""|'[^']*'|[^\s]+)))?(?:\s+(?<noinit>NOINIT))?(?:\s+WITH\s+(?<with>.*))?$", RegexOptions.IgnoreCase)]
    private static partial Regex AddObjectRx();

    [GeneratedRegex(@"^(PROTECTED|HIDDEN)\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex VisibilityRx();

    [GeneratedRegex(@"^(?:DIME(?:N|NS|NSI|NSIO|NSION)?|DECL(?:A|AR|ARE)?)\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DimensionRx();

    [GeneratedRegex(@"^(\w+)\s*[\[\(]\s*([^\]\)]+)\s*[\]\)]$")]
    private static partial Regex ArrayRx();

    [GeneratedRegex(@"^([\w\.]+)\s*=(?!=)\s*(.*)$", RegexOptions.Singleline)]
    private static partial Regex AssignmentRx();
}
