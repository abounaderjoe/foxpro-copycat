using System.Globalization;
using System.Text;
using JoePro.Core;

namespace JoePro.Runtime.Builtins;

public static partial class Library
{
    private static Value NullOr(CallContext c, Func<Value> f)
    {
        for (int i = 0; i < c.Count; i++) if (c.Has(i) && c[i].IsNull) return Value.Null;
        return f();
    }

    private static string TrimChars(string s, CallContext c, bool left, bool right)
    {
        if (c.Count <= 1)
        {
            if (left) s = s.TrimStart(' ');
            if (right) s = s.TrimEnd(' ');
            return s;
        }
        // VFP 9: ALLTRIM(s, nFlags, cChar1, cChar2, …) — flag 1 = case-insensitive
        var ci = c.Has(1) && (c.Int(1) & 1) != 0;
        var chars = Enumerable.Range(2, c.Count - 2).Select(c.Str).Where(x => x.Length > 0).ToList();
        if (chars.Count == 0) chars.Add(" ");
        var cmp = ci ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var ch in chars)
            {
                if (left && s.StartsWith(ch, cmp)) { s = s[ch.Length..]; changed = true; }
                if (right && s.EndsWith(ch, cmp)) { s = s[..^ch.Length]; changed = true; }
            }
        }
        return s;
    }

    private static int NthIndex(string s, string find, int n, bool ci, bool fromRight = false)
    {
        if (find.Length == 0 || n < 1) return -1;
        var cmp = ci ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (fromRight)
        {
            int pos = s.Length;
            for (int k = 0; k < n; k++)
            {
                if (pos <= 0) return -1;
                pos = s.LastIndexOf(find, pos - 1, cmp);
                if (pos < 0) return -1;
            }
            return pos;
        }
        int idx = -1;
        for (int k = 0; k < n; k++)
        {
            idx = s.IndexOf(find, idx + 1, cmp);
            if (idx < 0) return -1;
        }
        return idx;
    }

    private static string Pad(CallContext c, int mode)
    {
        var v = c[0];
        var s = v.Kind == ValueKind.Character ? v.AsString : TransformDefault(v, c.Options);
        var n = c.Int(1);
        var ch = c.Has(2) ? c.Str(2) : " ";
        if (ch.Length == 0) ch = " ";
        if (n <= 0) return "";
        if (s.Length >= n) return s[..n];
        var total = n - s.Length;
        return mode switch
        {
            0 => new string(ch[0], total) + s,
            1 => s + new string(ch[0], total),
            _ => new string(ch[0], total / 2) + s + new string(ch[0], total - total / 2),
        };
    }

    private static readonly char[] DefaultWordDelims = [' ', '\t', '\r', '\n'];

    private static string[] Words(CallContext c, int delimArg)
    {
        var delims = c.Has(delimArg) ? c.Str(delimArg).ToCharArray() : DefaultWordDelims;
        return c.Str(0).Split(delims, StringSplitOptions.RemoveEmptyEntries);
    }

    private static void RegisterStrings()
    {
        Add("ALLTRIM", c => NullOr(c, () => S(TrimChars(c.Str(0), c, true, true))));
        Add("LTRIM", c => NullOr(c, () => S(TrimChars(c.Str(0), c, true, false))));
        Add(["RTRIM", "TRIM"], c => NullOr(c, () => S(TrimChars(c.Str(0), c, false, true))));
        Add("UPPER", c => NullOr(c, () => S(c.Str(0).ToUpperInvariant())));
        Add("LOWER", c => NullOr(c, () => S(c.Str(0).ToLowerInvariant())));
        Add("PROPER", c => NullOr(c, () =>
        {
            var s = c.Str(0).ToLowerInvariant().ToCharArray();
            bool start = true;
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsLetter(s[i])) { if (start) s[i] = char.ToUpperInvariant(s[i]); start = false; }
                else start = char.IsWhiteSpace(s[i]);
            }
            return S(new string(s));
        }));
        Add("LEFT", c => NullOr(c, () => { var s = c.Str(0); var n = c.Int(1); return S(n <= 0 ? "" : s[..Math.Min(n, s.Length)]); }));
        Add("RIGHT", c => NullOr(c, () => { var s = c.Str(0); var n = c.Int(1); return S(n <= 0 ? "" : s[Math.Max(0, s.Length - n)..]); }));
        Add(["SUBSTR", "SUBSTRC"], c => NullOr(c, () =>
        {
            var s = c.Str(0);
            var start = c.Int(1);
            if (start < 1 || start > s.Length) return Value.EmptyString;
            var len = c.Has(2) ? c.Int(2) : s.Length - start + 1;
            if (len <= 0) return Value.EmptyString;
            return S(s.Substring(start - 1, Math.Min(len, s.Length - start + 1)));
        }));
        Add(["LEN", "LENC"], c =>
        {
            var v = c[0];
            if (v.IsNull) return Value.Null;
            return v.Kind == ValueKind.Binary ? N(v.AsBinary.Length) : N(c.Str(0).Length);
        });
        Add(["AT", "AT_C"], c => NullOr(c, () => N(NthIndex(c.Str(1), c.Str(0), c.Int(2, 1), false) + 1)));
        Add(["ATC", "ATCC"], c => NullOr(c, () => N(NthIndex(c.Str(1), c.Str(0), c.Int(2, 1), true) + 1)));
        Add(["RAT", "RATC"], c => NullOr(c, () => N(NthIndex(c.Str(1), c.Str(0), c.Int(2, 1), false, fromRight: true) + 1)));
        Add("OCCURS", c => NullOr(c, () =>
        {
            var find = c.Str(0);
            var s = c.Str(1);
            if (find.Length == 0) return N(0);
            int n = 0, i = 0;
            while ((i = s.IndexOf(find, i, StringComparison.Ordinal)) >= 0) { n++; i += find.Length; }
            return N(n);
        }));
        Add("STRTRAN", c => NullOr(c, () =>
        {
            var s = c.Str(0);
            var find = c.Str(1);
            var repl = c.Has(2) ? c.Str(2) : "";
            if (find.Length == 0) return S(s);
            int startOcc = c.Int(3, 1), count = c.Has(4) ? c.Int(4) : int.MaxValue;
            var flags = c.Int(5, 0);
            var cmp = (flags & 1) != 0 ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var sb = new StringBuilder();
            int pos = 0, occ = 0, done = 0;
            while (true)
            {
                var i = s.IndexOf(find, pos, cmp);
                if (i < 0) break;
                occ++;
                sb.Append(s, pos, i - pos);
                if (occ >= startOcc && done < count)
                {
                    sb.Append(repl);
                    done++;
                }
                else sb.Append(s, i, find.Length);
                pos = i + find.Length;
            }
            sb.Append(s, pos, s.Length - pos);
            return S(sb.ToString());
        }));
        Add("STUFF", c => NullOr(c, () =>
        {
            var s = c.Str(0);
            var start = Math.Clamp(c.Int(1), 1, s.Length + 1);
            var len = Math.Clamp(c.Int(2), 0, s.Length - start + 1);
            return S(s[..(start - 1)] + c.Str(3) + s[(start - 1 + len)..]);
        }));
        Add("REPLICATE", c => NullOr(c, () => S(c.Int(1) <= 0 ? "" : string.Concat(Enumerable.Repeat(c.Str(0), c.Int(1))))));
        Add("SPACE", c => S(new string(' ', Math.Max(0, c.Int(0)))));
        Add("PADL", c => NullOr(c, () => S(Pad(c, 0))));
        Add("PADR", c => NullOr(c, () => S(Pad(c, 1))));
        Add("PADC", c => NullOr(c, () => S(Pad(c, 2))));
        Add("CHR", c => { var n = c.Int(0); if (n < 0 || n > 0xFFFF) throw VfpException.InvalidArgument(); return S(((char)n).ToString()); });
        Add("ASC", c => NullOr(c, () => { var s = c.Str(0); return N(s.Length == 0 ? 0 : s[0]); }));
        Add("CHRTRAN", c => NullOr(c, () =>
        {
            var s = c.Str(0);
            var from = c.Str(1);
            var to = c.Str(2);
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                var i = from.IndexOf(ch);
                if (i < 0) sb.Append(ch);
                else if (i < to.Length) sb.Append(to[i]);
            }
            return S(sb.ToString());
        }));
        Add("GETWORDCOUNT", c => NullOr(c, () => N(Words(c, 1).Length)));
        Add("GETWORDNUM", c => NullOr(c, () =>
        {
            var w = Words(c, 2);
            var n = c.Int(1);
            return S(n >= 1 && n <= w.Length ? w[n - 1] : "");
        }));
        Add("STREXTRACT", c => NullOr(c, () =>
        {
            var s = c.Str(0);
            var begin = c.Str(1);
            var end = c.Has(2) ? c.Str(2) : "";
            var occ = c.Int(3, 1);
            var flags = c.Int(4, 0);
            bool ci = (flags & 1) != 0, endOptional = (flags & 2) != 0, include = (flags & 4) != 0;
            var cmp = ci ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            int start;
            if (begin.Length == 0) start = 0;
            else
            {
                var bi = NthIndex(s, begin, occ, ci);
                if (bi < 0) return Value.EmptyString;
                start = bi + begin.Length;
            }
            if (end.Length == 0) return S(include ? begin + s[start..] : s[start..]);
            var ei = s.IndexOf(end, start, cmp);
            if (ei < 0) return endOptional ? S(include ? begin + s[start..] : s[start..]) : Value.EmptyString;
            return S(include ? begin + s[start..ei] + end : s[start..ei]);
        }));
        Add("LIKE", c => NullOr(c, () => L(LikeMatch(c.Str(0).TrimEnd(), c.Str(1).TrimEnd()))));
        Add("LIKEC", c => NullOr(c, () => L(LikeMatch(c.Str(0).TrimEnd(), c.Str(1).TrimEnd()))));
        Add("ISDIGIT", c => { var s = c.Str(0); return L(s.Length > 0 && char.IsDigit(s[0])); });
        Add("ISALPHA", c => { var s = c.Str(0); return L(s.Length > 0 && char.IsLetter(s[0])); });
        Add("ISUPPER", c => { var s = c.Str(0); return L(s.Length > 0 && char.IsUpper(s[0])); });
        Add("ISLOWER", c => { var s = c.Str(0); return L(s.Length > 0 && char.IsLower(s[0])); });
        Add("ISLEADBYTE", _ => Value.False);
        Add("TEXTMERGE", c => S(c.Rt.TextMerge(c.Str(0))));
        Add("MLINE", c =>
        {
            var lines = SplitLines(c.Str(0));
            var n = c.Int(1);
            return S(n >= 1 && n <= lines.Length ? lines[n - 1] : "");
        });
        Add("MEMLINES", c => N(c[0].IsEmpty ? 0 : SplitLines(c.Str(0)).Length));
        Add("SOUNDEX", c => S(Soundex(c.Str(0))));
        Add("DIFFERENCE", c =>
        {
            var a = Soundex(c.Str(0));
            var b = Soundex(c.Str(1));
            return N(a.Zip(b).Count(p => p.First == p.Second));
        });
        Add("STRCONV", c =>
        {
            var v = c[0];
            var mode = c.Int(1);
            switch (mode)
            {
                case 13: return S(Convert.ToBase64String(v.Kind == ValueKind.Binary ? v.AsBinary : Encoding.Latin1.GetBytes(v.AsString)));
                case 14: return S(Encoding.Latin1.GetString(Convert.FromBase64String(v.AsString)));
                case 15: return S(Convert.ToHexString(v.Kind == ValueKind.Binary ? v.AsBinary : Encoding.Latin1.GetBytes(v.AsString)));
                case 16: return S(Encoding.Latin1.GetString(Convert.FromHexString(v.AsString)));
                case 9: return S(Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(v.AsString)));
                case 11: return S(Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(v.AsString)));
                case 7: return S(v.AsString.ToLowerInvariant());
                case 8: return S(v.AsString.ToUpperInvariant());
                default: return v; // Unicode strings need no DBCS conversion
            }
        });
        Add("STR", c => NullOr(c, () => S(Str(c.Num(0), c.Int(1, 10), c.Int(2, 0), c.Options))));
        Add("VAL", c =>
        {
            var v = c[0];
            if (v.IsNull) return Value.Null;
            var s = c.Str(0).TrimStart();
            int i = 0;
            if (i < s.Length && s[i] is '+' or '-') i++;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            int decimals = 0;
            if (i < s.Length && s[i] == c.Options.Point)
            {
                int ds = ++i;
                while (i < s.Length && char.IsDigit(s[i])) i++;
                decimals = i - ds;
            }
            if (i < s.Length && s[i] is 'e' or 'E')
            {
                int save = i++;
                if (i < s.Length && s[i] is '+' or '-') i++;
                if (i < s.Length && char.IsDigit(s[i])) { while (i < s.Length && char.IsDigit(s[i])) i++; }
                else i = save;
            }
            var num = s[..i].Replace(c.Options.Point, '.');
            var d = double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 0;
            return N(d, Math.Max(c.Options.Decimals, decimals));
        });
        Add("TRANSFORM", c =>
        {
            var v = c[0];
            if (!c.Has(1)) return S(TransformDefault(v, c.Options));
            return S(Transform(v, c.Str(1), c.Options));
        });
        Add("CTOBIN", c =>
        {
            var b = c[0].Kind == ValueKind.Binary ? c[0].AsBinary : Encoding.Latin1.GetBytes(c.Str(0));
            var flags = c.Has(1) ? c.Str(1).ToUpperInvariant() : "4S";
            if (flags.Contains('R')) b = b.Reverse().ToArray();
            return b.Length switch
            {
                1 => N((sbyte)b[0]),
                2 => N(BitConverter.ToInt16(b)),
                8 when flags.Contains('B') => N(BitConverter.ToDouble(b), 4),
                8 => N(BitConverter.ToInt64(b)),
                _ => N(BitConverter.ToInt32(b.Concat(new byte[4]).Take(4).ToArray())),
            };
        });
        Add("BINTOC", c =>
        {
            var n = c.Num(0);
            var flags = c.Has(1) && c[1].Kind == ValueKind.Character ? c.Str(1).ToUpperInvariant() : (c.Has(1) ? c.Int(1).ToString() : "4");
            byte[] b = flags.Contains('8') || flags.Contains('B') ? BitConverter.GetBytes(flags.Contains('B') ? n : (long)n)
                : flags.Contains('2') ? BitConverter.GetBytes((short)n)
                : flags.Contains('1') ? [(byte)(sbyte)n]
                : BitConverter.GetBytes((int)n);
            if (flags.Contains('R')) b = b.Reverse().ToArray();
            return Value.Binary(b);
        });
    }

    internal static string[] SplitLines(string s) =>
        s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static string Soundex(string s)
    {
        s = new string(s.ToUpperInvariant().Where(char.IsLetter).ToArray());
        if (s.Length == 0) return "0000";
        const string codes = "01230120022455012623010202";
        var sb = new StringBuilder().Append(s[0]);
        var last = codes[s[0] - 'A'];
        foreach (var ch in s.Skip(1))
        {
            if (ch < 'A' || ch > 'Z') continue;
            var code = codes[ch - 'A'];
            if (code != '0' && code != last) sb.Append(code);
            if (ch != 'H' && ch != 'W') last = code;
            if (sb.Length == 4) break;
        }
        return sb.ToString().PadRight(4, '0');
    }
}
