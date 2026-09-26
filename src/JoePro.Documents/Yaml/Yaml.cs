using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace JoePro.Documents.Yaml;

/// <summary>
/// A strict YAML subset for Joe Pro documents (reports, labels, menus, projects, queries): block mappings and
/// sequences, plain / double-quoted / single-quoted scalars, literal block scalars (|, |-) and comments. No
/// anchors, tags, flow collections (except empty [] and {}) or multi-document streams. The writer is canonical:
/// two-space indentation, LF line endings, keys in the order the document model adds them.
/// </summary>
public abstract class YamlNode
{
    public int Line { get; init; }
}

public sealed class YamlScalar : YamlNode
{
    public YamlScalar(string value, bool quoted = false) { Value = value; Quoted = quoted; }
    public string Value { get; }
    /// <summary>The value was written in quotes (so "true" or "12" in quotes are strings).</summary>
    public bool Quoted { get; }
    public override string ToString() => Value;
}

public sealed class YamlSeq : YamlNode
{
    public List<YamlNode> Items { get; } = new();
    public void Add(YamlNode node) => Items.Add(node);
}

public sealed class YamlMap : YamlNode
{
    private readonly List<KeyValuePair<string, YamlNode>> _entries = new();
    public IReadOnlyList<KeyValuePair<string, YamlNode>> Entries => _entries;
    public int Count => _entries.Count;

    public YamlNode? this[string key] => _entries.FirstOrDefault(e => e.Key == key).Value;

    public void Add(string key, YamlNode value)
    {
        if (_entries.Any(e => e.Key == key)) throw new FormatException($"Line {value.Line}: duplicate key '{key}'.");
        _entries.Add(new(key, value));
    }

    // ---- Writing helpers (only non-default values are written) ----
    public YamlMap Set(string key, string? value, string? defaultValue = null)
    {
        if (value != null && value != defaultValue) Add(key, new YamlScalar(value, quoted: true));
        return this;
    }

    public YamlMap Set(string key, double value, double defaultValue = double.NaN)
    {
        if (!(value == defaultValue)) Add(key, new YamlScalar(YamlText.Number(value)));
        return this;
    }

    public YamlMap Set(string key, int value, int? defaultValue = null)
    {
        if (value != defaultValue) Add(key, new YamlScalar(value.ToString(CultureInfo.InvariantCulture)));
        return this;
    }

    public YamlMap Set(string key, bool value, bool defaultValue = false)
    {
        if (value != defaultValue) Add(key, new YamlScalar(value ? "true" : "false"));
        return this;
    }

    /// <summary>A keyword value (an enum name in camelCase): written plain.</summary>
    public YamlMap SetWord(string key, string value, string? defaultValue = null)
    {
        if (value != defaultValue) Add(key, new YamlScalar(value));
        return this;
    }

    public YamlMap SetNode(string key, YamlNode? node)
    {
        if (node is YamlMap { Count: 0 } or YamlSeq { Items.Count: 0 } or null) return this;
        Add(key, node);
        return this;
    }

    // ---- Reading helpers ----
    private static FormatException Error(YamlNode node, string message) => new($"Line {node.Line}: {message}");

    public string? Str(string key) => this[key] switch
    {
        null => null,
        YamlScalar s => s.Value,
        var n => throw Error(n, $"'{key}' should be a single value."),
    };

    public string Str(string key, string fallback) => Str(key) ?? fallback;

    public double Num(string key, double fallback)
    {
        if (this[key] is not YamlScalar s) return fallback;
        return double.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : throw Error(s, $"'{key}' should be a number.");
    }

    public int Int(string key, int fallback) => (int)Math.Round(Num(key, fallback));

    public bool Bool(string key, bool fallback = false)
    {
        if (this[key] is not YamlScalar s) return fallback;
        return s.Value switch { "true" => true, "false" => false, _ => throw Error(s, $"'{key}' should be true or false.") };
    }

    public YamlMap? Map(string key) => this[key] switch
    {
        null => null,
        YamlMap m => m,
        YamlScalar { Value: "{}" } => new YamlMap(),
        var n => throw Error(n, $"'{key}' should be a mapping."),
    };

    public IEnumerable<YamlNode> Seq(string key) => this[key] switch
    {
        null => [],
        YamlSeq s => s.Items,
        YamlScalar { Value: "[]" } => [],
        var n => throw Error(n, $"'{key}' should be a list."),
    };

    /// <summary>Rejects keys a reader does not know, so typos are reported instead of silently ignored.</summary>
    public void CheckKeys(params string[] known)
    {
        foreach (var (k, v) in _entries)
            if (!known.Contains(k)) throw Error(v, $"unknown key '{k}'.");
    }
}

public static class YamlText
{
    public static string Number(double d)
    {
        var r = Math.Round(d, 4);
        if (r == 0) r = 0; // no "-0"
        return r.ToString("0.####", CultureInfo.InvariantCulture);
    }

    // ================================================================================
    // Writer
    // ================================================================================

    public static string Write(YamlMap root, string? headerComment = null)
    {
        var sb = new StringBuilder();
        if (headerComment != null) sb.Append("# ").Append(headerComment).Append('\n');
        WriteMap(sb, root, 0);
        return sb.ToString();
    }

    private static void Indent(StringBuilder sb, int n) => sb.Append(' ', n);

    private static void WriteMap(StringBuilder sb, YamlMap map, int indent, bool firstInline = false)
    {
        var first = true;
        foreach (var (key, value) in map.Entries)
        {
            if (!(first && firstInline)) Indent(sb, indent);
            first = false;
            sb.Append(Key(key)).Append(':');
            WriteValue(sb, value, indent);
        }
    }

    private static void WriteValue(StringBuilder sb, YamlNode value, int indent)
    {
        switch (value)
        {
            case YamlScalar s when s.Quoted && s.Value.Contains('\n') && CanBlock(s.Value):
            {
                sb.Append(s.Value.EndsWith('\n') ? " |" : " |-").Append('\n');
                var body = s.Value.EndsWith('\n') ? s.Value[..^1] : s.Value;
                foreach (var line in body.Split('\n'))
                {
                    if (line.Length > 0) Indent(sb, indent + 2);
                    sb.Append(line).Append('\n');
                }
                break;
            }
            case YamlScalar s:
                sb.Append(' ').Append(s.Quoted ? QuoteIfNeeded(s.Value) : s.Value).Append('\n');
                break;
            case YamlMap { Count: 0 }:
                sb.Append(" {}\n");
                break;
            case YamlMap m:
                sb.Append('\n');
                WriteMap(sb, m, indent + 2);
                break;
            case YamlSeq { Items.Count: 0 }:
                sb.Append(" []\n");
                break;
            case YamlSeq seq:
                sb.Append('\n');
                WriteSeq(sb, seq, indent + 2);
                break;
        }
    }

    private static void WriteSeq(StringBuilder sb, YamlSeq seq, int indent)
    {
        foreach (var item in seq.Items)
        {
            Indent(sb, indent);
            sb.Append('-');
            switch (item)
            {
                case YamlMap { Count: > 0 } m:
                    sb.Append(' ');
                    WriteMap(sb, m, indent + 2, firstInline: true);
                    break;
                default:
                    WriteValue(sb, item, indent);
                    break;
            }
        }
    }

    /// <summary>A literal block keeps the text exactly: no tabs, no trailing blanks, no leading blank on the first line, no CR.</summary>
    private static bool CanBlock(string s) =>
        !s.Contains('\r') && !s.Contains('\t') && !s.StartsWith(' ') && !s.EndsWith("\n\n") && s.Split('\n').All(l => l.Length == 0 || l[^1] != ' ');

    private static readonly Regex PlainKey = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private static string Key(string key) => PlainKey.IsMatch(key) ? key : Quote(key);

    private static readonly Regex SafePlain = new(@"^[A-Za-z_][A-Za-z0-9_ .\-/()]*$", RegexOptions.Compiled);
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "true", "false", "null", "yes", "no", "on", "off", "y", "n", "~" };

    /// <summary>Strings are written plain when that cannot be read back as something else; otherwise double-quoted.</summary>
    public static string QuoteIfNeeded(string s) =>
        s.Length > 0 && SafePlain.IsMatch(s) && !s.EndsWith(' ') && !Reserved.Contains(s) && !s.Contains(" #") && !s.Contains(": ") ? s : Quote(s);

    public static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    // ================================================================================
    // Reader
    // ================================================================================

    private sealed record SourceLine(int Number, int Indent, string Text);

    public static YamlMap Parse(string text)
    {
        var lines = new List<SourceLine>();
        var raw = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < raw.Length; i++)
        {
            var l = raw[i];
            if (l.Contains('\t') && l.TrimStart(' ').StartsWith('\t')) throw new FormatException($"Line {i + 1}: tabs cannot be used for indentation.");
            lines.Add(new SourceLine(i + 1, l.Length - l.TrimStart(' ').Length, l));
        }
        int pos = 0;
        SkipBlank(lines, ref pos);
        if (pos >= lines.Count) return new YamlMap();
        if (lines[pos].Indent != 0) throw new FormatException($"Line {lines[pos].Number}: the document must start at column 1.");
        var node = ParseBlock(lines, ref pos, 0);
        SkipBlank(lines, ref pos);
        if (pos < lines.Count) throw new FormatException($"Line {lines[pos].Number}: unexpected indentation.");
        return node as YamlMap ?? throw new FormatException("The document must be a mapping.");
    }

    private static bool IsBlank(SourceLine l)
    {
        var t = l.Text.Trim();
        return t.Length == 0 || t.StartsWith('#');
    }

    private static void SkipBlank(List<SourceLine> lines, ref int pos)
    {
        while (pos < lines.Count && IsBlank(lines[pos])) pos++;
    }

    private static YamlNode ParseBlock(List<SourceLine> lines, ref int pos, int indent)
    {
        SkipBlank(lines, ref pos);
        var first = lines[pos];
        var t = first.Text.Trim();
        return t == "-" || t.StartsWith("- ") ? ParseSeq(lines, ref pos, first.Indent) : ParseMap(lines, ref pos, first.Indent);
    }

    private static YamlMap ParseMap(List<SourceLine> lines, ref int pos, int indent)
    {
        var map = new YamlMap { Line = lines[pos].Number };
        while (true)
        {
            SkipBlank(lines, ref pos);
            if (pos >= lines.Count) break;
            var line = lines[pos];
            if (line.Indent < indent) break;
            if (line.Indent > indent) throw new FormatException($"Line {line.Number}: unexpected indentation.");
            ParseEntry(lines, ref pos, indent, line.Text[indent..], map, line.Number);
        }
        return map;
    }

    /// <summary>Parses "key: value" (text starting at the key); nested content is indented more than <paramref name="indent"/>.</summary>
    private static void ParseEntry(List<SourceLine> lines, ref int pos, int indent, string text, YamlMap map, int lineNo)
    {
        var (key, rest) = SplitKey(text, lineNo);
        pos++;
        rest = StripComment(rest).Trim();
        if (rest.Length == 0)
        {
            // Nested block: deeper indentation, or a sequence at the same indentation ("key:\n- item").
            SkipBlank(lines, ref pos);
            if (pos < lines.Count && (lines[pos].Indent > indent || (lines[pos].Indent == indent && lines[pos].Text.Trim() is var tt && (tt == "-" || tt.StartsWith("- ")))))
                map.Add(key, ParseBlock(lines, ref pos, indent));
            else map.Add(key, new YamlScalar("", quoted: false) { Line = lineNo });
            return;
        }
        if (rest is "|" or "|-" or "|+")
        {
            map.Add(key, ParseLiteral(lines, ref pos, indent, rest, lineNo));
            return;
        }
        map.Add(key, ParseScalar(rest, lineNo));
    }

    private static YamlSeq ParseSeq(List<SourceLine> lines, ref int pos, int indent)
    {
        var seq = new YamlSeq { Line = lines[pos].Number };
        while (true)
        {
            SkipBlank(lines, ref pos);
            if (pos >= lines.Count) break;
            var line = lines[pos];
            if (line.Indent < indent) break;
            var t = line.Text[line.Indent..];
            if (line.Indent > indent || !(t == "-" || t.StartsWith("- "))) throw new FormatException($"Line {line.Number}: expected '- ' at this indentation.");
            var after = t.Length > 1 ? t[2..] : "";
            var itemIndent = indent + 2 + (after.Length - after.TrimStart(' ').Length);
            after = after.TrimStart(' ');
            if (StripComment(after).Trim().Length == 0)
            {
                pos++;
                seq.Add(ParseBlock(lines, ref pos, indent + 1));
                continue;
            }
            if (LooksLikeKey(after))
            {
                // A mapping whose first entry is on the dash line.
                var map = new YamlMap { Line = line.Number };
                ParseEntry(lines, ref pos, itemIndent, after, map, line.Number);
                while (true)
                {
                    SkipBlank(lines, ref pos);
                    if (pos >= lines.Count || lines[pos].Indent != itemIndent) break;
                    ParseEntry(lines, ref pos, itemIndent, lines[pos].Text[itemIndent..], map, lines[pos].Number);
                }
                if (pos < lines.Count && !IsBlank(lines[pos]) && lines[pos].Indent > itemIndent)
                    throw new FormatException($"Line {lines[pos].Number}: unexpected indentation.");
                seq.Add(map);
                continue;
            }
            pos++;
            var rest = StripComment(after).Trim();
            seq.Add(rest is "|" or "|-" or "|+" ? ParseLiteral(lines, ref pos, indent, rest, line.Number) : ParseScalar(rest, line.Number));
        }
        return seq;
    }

    private static YamlScalar ParseLiteral(List<SourceLine> lines, ref int pos, int indent, string header, int lineNo)
    {
        var body = new List<string>();
        int? blockIndent = null;
        while (pos < lines.Count)
        {
            var l = lines[pos];
            if (l.Text.Trim().Length == 0) { body.Add(""); pos++; continue; }
            if (l.Indent <= indent) break;
            blockIndent ??= l.Indent;
            if (l.Indent < blockIndent) break;
            body.Add(l.Text[blockIndent.Value..]);
            pos++;
        }
        // Trailing blank lines belong to what follows (they are not part of the value).
        var trailing = 0;
        while (body.Count > 0 && body[^1].Length == 0) { body.RemoveAt(body.Count - 1); trailing++; }
        var value = string.Join("\n", body);
        if (header == "|" && body.Count > 0) value += "\n";
        if (header == "|+") value += new string('\n', trailing + (body.Count > 0 ? 1 : 0));
        return new YamlScalar(value, quoted: true) { Line = lineNo };
    }

    private static bool LooksLikeKey(string text)
    {
        if (text.StartsWith('"') || text.StartsWith('\''))
        {
            var close = QuotedEnd(text);
            return close > 0 && close + 1 < text.Length && text[close + 1] == ':';
        }
        var m = Regex.Match(text, @"^[A-Za-z_][A-Za-z0-9_]*:(\s|$)");
        return m.Success;
    }

    private static int QuotedEnd(string text)
    {
        var q = text[0];
        for (int i = 1; i < text.Length; i++)
        {
            if (q == '"' && text[i] == '\\') { i++; continue; }
            if (text[i] == q)
            {
                if (q == '\'' && i + 1 < text.Length && text[i + 1] == '\'') { i++; continue; }
                return i;
            }
        }
        return -1;
    }

    private static (string Key, string Value) SplitKey(string text, int lineNo)
    {
        if (text.StartsWith('"') || text.StartsWith('\''))
        {
            var close = QuotedEnd(text);
            if (close < 0 || close + 1 >= text.Length || text[close + 1] != ':') throw new FormatException($"Line {lineNo}: expected 'key: value'.");
            return (((YamlScalar)ParseScalar(text[..(close + 1)], lineNo)).Value, text[(close + 2)..]);
        }
        var m = Regex.Match(text, @"^([A-Za-z_][A-Za-z0-9_]*):(?:\s(.*)|$)");
        if (!m.Success) throw new FormatException($"Line {lineNo}: expected 'key: value' but found '{text.Trim()}'.");
        return (m.Groups[1].Value, m.Groups[2].Value);
    }

    /// <summary>Removes a " # comment" that is outside quotes.</summary>
    private static string StripComment(string text)
    {
        if (text.StartsWith('"') || text.StartsWith('\''))
        {
            var close = QuotedEnd(text);
            if (close < 0) return text;
            var after = text[(close + 1)..];
            var hash = after.IndexOf(" #", StringComparison.Ordinal);
            return hash >= 0 ? text[..(close + 1)] + after[..hash] : text;
        }
        if (text.StartsWith('#')) return "";
        var i = text.IndexOf(" #", StringComparison.Ordinal);
        return i >= 0 ? text[..i] : text;
    }

    private static YamlNode ParseScalar(string text, int lineNo)
    {
        text = text.Trim();
        if (text == "[]") return new YamlSeq { Line = lineNo };
        if (text == "{}") return new YamlMap { Line = lineNo };
        if (text.StartsWith('[') || text.StartsWith('{')) throw new FormatException($"Line {lineNo}: flow collections are not supported; use block style.");
        if (text.StartsWith('&') || text.StartsWith('*') || text.StartsWith('!')) throw new FormatException($"Line {lineNo}: anchors, aliases and tags are not supported.");
        if (text.StartsWith('"'))
        {
            var close = QuotedEnd(text);
            if (close != text.Length - 1) throw new FormatException($"Line {lineNo}: unterminated or malformed string.");
            var sb = new StringBuilder();
            for (int i = 1; i < close; i++)
            {
                var c = text[i];
                if (c != '\\') { sb.Append(c); continue; }
                var e = text[++i];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case '0': sb.Append('\0'); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'u': sb.Append((char)Convert.ToInt32(text.Substring(i + 1, 4), 16)); i += 4; break;
                    default: throw new FormatException($"Line {lineNo}: unknown escape \\{e}.");
                }
            }
            return new YamlScalar(sb.ToString(), quoted: true) { Line = lineNo };
        }
        if (text.StartsWith('\''))
        {
            var close = QuotedEnd(text);
            if (close != text.Length - 1) throw new FormatException($"Line {lineNo}: unterminated or malformed string.");
            return new YamlScalar(text[1..close].Replace("''", "'"), quoted: true) { Line = lineNo };
        }
        return new YamlScalar(text) { Line = lineNo };
    }
}
