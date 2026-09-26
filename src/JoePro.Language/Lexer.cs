using System.Globalization;
using System.Text;
using JoePro.Core;

namespace JoePro.Language;

public enum TokenKind { Ident, Number, String, Date, Op, Macro, Logical, Null }

public sealed record Token(TokenKind Kind, string Text, int Column)
{
    public double Number { get; init; }
    public int Decimals { get; init; }
    public bool IsCurrency { get; init; }
    public bool IsOp(string op) => Kind == TokenKind.Op && Text == op;
    public override string ToString() => Kind == TokenKind.String ? $"\"{Text}\"" : Text;
}

/// <summary>A logical source line (continuations joined, comments removed) and its tokens.</summary>
public sealed class SourceLine
{
    public required int Number { get; init; }
    public required string Text { get; init; }
    public required List<Token> Tokens { get; init; }
    /// <summary>For TEXT…ENDTEXT: the raw lines between the delimiters.</summary>
    public List<string>? TextBlock { get; init; }
    public string? File { get; init; }
    public bool HasMacro => Tokens.Any(t => t.Kind == TokenKind.Macro);
}

/// <summary>
/// Turns FoxPro source into logical lines: joins ';' continuations, strips '*', NOTE and '&amp;&amp;'
/// comments, captures TEXT…ENDTEXT blocks and applies #DEFINE / #IF / #INCLUDE preprocessing.
/// </summary>
public static class Preprocessor
{
    public static List<SourceLine> Prepare(string source, string? file = null, Func<string, string?>? includeResolver = null,
        Dictionary<string, List<Token>>? defines = null)
    {
        defines ??= new(StringComparer.OrdinalIgnoreCase);
        var physical = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var result = new List<SourceLine>();
        var condStack = new Stack<(bool Active, bool Taken)>();
        bool Active() => condStack.All(c => c.Active);

        for (int i = 0; i < physical.Length; i++)
        {
            int lineNo = i + 1;
            var sb = new StringBuilder();
            var line = StripInlineComment(physical[i]);
            // Continuation: a line ending with ';' continues on the next line.
            while (line.TrimEnd().EndsWith(';') && i + 1 < physical.Length)
            {
                var t = line.TrimEnd();
                sb.Append(t, 0, t.Length - 1).Append(' ');
                line = StripInlineComment(physical[++i]);
            }
            sb.Append(line);
            var text = sb.ToString().Trim();
            if (text.Length == 0) continue;

            if (text.StartsWith('#'))
            {
                HandleDirective(text, lineNo, condStack, defines, result, includeResolver, Active());
                continue;
            }
            if (!Active()) continue;
            if (text.StartsWith('*') || IsWord(text, "NOTE", 4)) continue;

            if (IsWord(text, "TEXT", 4) && !text.StartsWith("TEXTMERGE", StringComparison.OrdinalIgnoreCase) && !IsWord(text, "TEXTMERGE", 5))
            {
                var block = new List<string>();
                int j = i + 1;
                for (; j < physical.Length; j++)
                {
                    if (IsWord(physical[j].Trim(), "ENDTEXT", 4)) break;
                    block.Add(physical[j]);
                }
                result.Add(new SourceLine { Number = lineNo, Text = text, Tokens = Lexer.Lex(text, lineNo, defines), TextBlock = block, File = file });
                i = j;
                continue;
            }
            result.Add(new SourceLine { Number = lineNo, Text = text, Tokens = Lexer.Lex(text, lineNo, defines), File = file });
        }
        return result;
    }

    private static bool IsWord(string text, string word, int minLen)
    {
        int n = 0;
        while (n < text.Length && (char.IsLetterOrDigit(text[n]) || text[n] == '_')) n++;
        if (n < minLen || n > word.Length) return false;
        return string.Compare(text, 0, word, 0, n, StringComparison.OrdinalIgnoreCase) == 0;
    }

    private static void HandleDirective(string text, int lineNo, Stack<(bool Active, bool Taken)> cond,
        Dictionary<string, List<Token>> defines, List<SourceLine> result, Func<string, string?>? includeResolver, bool active)
    {
        var parts = text[1..].TrimStart().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        var directive = parts.Length > 0 ? parts[0].ToUpperInvariant() : "";
        var rest = parts.Length > 1 ? parts[1].Trim() : "";
        switch (directive)
        {
            case "DEFINE":
                if (!active) return;
                var dp = rest.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                if (dp.Length == 0) throw new CompileException("Syntax error in #DEFINE.", lineNo);
                defines[dp[0]] = dp.Length > 1 ? Lexer.Lex(StripInlineComment(dp[1]).Trim(), lineNo, defines) : [];
                break;
            case "UNDEF":
                if (active) defines.Remove(rest);
                break;
            case "IFDEF":
                cond.Push(Taken(active && defines.ContainsKey(rest)));
                break;
            case "IFNDEF":
                cond.Push(Taken(active && !defines.ContainsKey(rest)));
                break;
            case "IF":
                cond.Push(Taken(active && EvalCondition(rest, defines)));
                break;
            case "ELIF":
            {
                var (_, taken) = cond.Pop();
                var outer = cond.All(c => c.Active);
                var now = outer && !taken && EvalCondition(rest, defines);
                cond.Push((now, taken || now));
                break;
            }
            case "ELSE":
            {
                var (_, taken) = cond.Pop();
                cond.Push((cond.All(c => c.Active) && !taken, true));
                break;
            }
            case "ENDIF":
                if (cond.Count == 0) throw new CompileException("#ENDIF without #IF.", lineNo);
                cond.Pop();
                break;
            case "INCLUDE":
                if (!active) return;
                var name = rest.Trim('"', '\'', '[', ']');
                var content = includeResolver?.Invoke(name) ?? throw new CompileException($"Include file '{name}' was not found.", lineNo);
                // Only definitions are imported from header files, as in VFP.
                foreach (var l in Prepare(content, name, includeResolver, defines)) _ = l;
                break;
            default:
                throw new CompileException($"Unknown preprocessor directive #{directive}.", lineNo);
        }

        static (bool, bool) Taken(bool b) => (b, b);
    }

    private static bool EvalCondition(string expr, Dictionary<string, List<Token>> defines)
    {
        var toks = Lexer.Lex(expr, 0, defines);
        if (toks.Count == 1)
        {
            var t = toks[0];
            if (t.Kind == TokenKind.Number) return t.Number != 0;
            if (t.Kind == TokenKind.Logical) return t.Text == ".T.";
            if (t.Kind == TokenKind.Ident) return false;
        }
        // DEFINED(name) and simple comparisons of constants.
        if (toks.Count == 4 && toks[0].Kind == TokenKind.Ident && toks[0].Text.Equals("DEFINED", StringComparison.OrdinalIgnoreCase))
            return defines.ContainsKey(toks[2].Text);
        if (toks.Count == 3 && toks[0].Kind == TokenKind.Number && toks[2].Kind == TokenKind.Number)
        {
            var (a, b) = (toks[0].Number, toks[2].Number);
            return toks[1].Text switch { "=" or "==" => a == b, "<>" or "!=" or "#" => a != b, "<" => a < b, ">" => a > b, "<=" => a <= b, ">=" => a >= b, _ => false };
        }
        return false;
    }

    /// <summary>Removes a trailing &amp;&amp; comment that is not inside a string literal.</summary>
    public static string StripInlineComment(string line)
    {
        char quote = '\0';
        for (int i = 0; i < line.Length - 1; i++)
        {
            var c = line[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') quote = c;
            else if (c == '&' && line[i + 1] == '&') return line[..i];
        }
        return line;
    }
}

public sealed class CompileException : VfpException
{
    public int Line { get; }
    public CompileException(string message, int line) : base(ErrorCodes.SyntaxError, message) => Line = line;
}

public static class Lexer
{
    private static readonly string[] Ops = ["**", "==", "<>", "!=", "<=", ">=", "->", "::", "??",
        "+", "-", "*", "/", "%", "^", "=", "#", "<", ">", "$", "(", ")", ",", ".", "!", ":", "@", "?", "[", "]", ";", "\\\\", "\\", "~", "|"];

    public static List<Token> Lex(string text, int line, Dictionary<string, List<Token>>? defines = null)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            int start = i;

            if (char.IsLetter(c) || c == '_')
            {
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                var word = text[start..i];
                if (defines != null && defines.TryGetValue(word, out var repl))
                    tokens.AddRange(repl.Select(t => t with { Column = start }));
                else
                    tokens.Add(new Token(TokenKind.Ident, word, start));
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1]) && !PrevIsOperand(tokens)))
            {
                tokens.Add(LexNumber(text, ref i));
                continue;
            }

            if (c == '$' && i + 1 < text.Length && (char.IsDigit(text[i + 1]) || text[i + 1] == '.') && !PrevIsOperand(tokens))
            {
                i++;
                var money = LexNumber(text, ref i);
                tokens.Add(money with { Column = start, IsCurrency = true });
                continue;
            }

            if (c == '.' && TryDotWord(text, i, out var dotWord))
            {
                i += dotWord.Length;
                var w = dotWord.ToUpperInvariant();
                tokens.Add(w switch
                {
                    ".T." or ".Y." => new Token(TokenKind.Logical, ".T.", start),
                    ".F." or ".N." => new Token(TokenKind.Logical, ".F.", start),
                    ".NULL." => new Token(TokenKind.Null, ".NULL.", start),
                    ".AND." => new Token(TokenKind.Op, "AND", start),
                    ".OR." => new Token(TokenKind.Op, "OR", start),
                    _ => new Token(TokenKind.Op, "NOT", start),
                });
                continue;
            }

            if (c is '"' or '\'' || (c == '[' && !PrevIsOperand(tokens)))
            {
                var close = c == '[' ? ']' : c;
                var end = text.IndexOf(close, i + 1);
                if (end < 0) throw new CompileException("Missing closing string delimiter.", line);
                tokens.Add(new Token(TokenKind.String, text[(i + 1)..end], start));
                i = end + 1;
                continue;
            }

            if (c == '{')
            {
                var end = text.IndexOf('}', i + 1);
                if (end < 0) throw new CompileException("Missing '}' in date literal.", line);
                tokens.Add(new Token(TokenKind.Date, text[(i + 1)..end].Trim(), start));
                i = end + 1;
                continue;
            }

            if (c == '&' && i + 1 < text.Length && (char.IsLetter(text[i + 1]) || text[i + 1] == '_'))
            {
                i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                var name = text[(start + 1)..i];
                if (i < text.Length && text[i] == '.') i++; // optional macro terminator
                tokens.Add(new Token(TokenKind.Macro, name, start));
                continue;
            }

            var op = Ops.FirstOrDefault(o => string.CompareOrdinal(text, i, o, 0, o.Length) == 0);
            if (op == null) throw new CompileException($"Unexpected character '{c}'.", line);
            // A '[' after an operand is a subscript; normalize to the same token either way.
            tokens.Add(new Token(TokenKind.Op, op, start));
            i += op.Length;
        }
        return tokens;
    }

    private static bool PrevIsOperand(List<Token> tokens)
    {
        if (tokens.Count == 0) return false;
        var p = tokens[^1];
        // After a command verb at the start of a line ("USE [my file]") '[' opens a string.
        if (p.Kind == TokenKind.Ident) return tokens.Count > 1 || !IsStringCommand(p.Text);
        return p.Kind is TokenKind.Number or TokenKind.String || p.IsOp(")") || p.IsOp("]");
    }

    private static readonly string[] StringCommands =
    [
        "USE", "DO", "SET", "WAIT", "ERROR", "CD", "CHDIR", "MD", "MKDIR", "RD", "RMDIR", "RUN", "COPY", "APPEND", "IMPORT",
        "EXPORT", "OPEN", "CREATE", "MODIFY", "REPORT", "LABEL", "SEEK", "ERASE", "DELETE", "RENAME", "KEYBOARD", "RETURN",
        "THROW", "STORE", "INSERT", "REPLACE", "CASE", "IF", "WHILE", "LOCATE", "RESTORE", "SAVE", "COMPILE", "BROWSE",
    ];

    private static bool IsStringCommand(string word) =>
        StringCommands.Any(k => word.Length >= Math.Min(4, k.Length) && word.Length <= k.Length &&
                                k.StartsWith(word, StringComparison.OrdinalIgnoreCase));

    private static bool TryDotWord(string text, int i, out string word)
    {
        foreach (var w in new[] { ".T.", ".F.", ".Y.", ".N.", ".NULL.", ".AND.", ".OR.", ".NOT." })
        {
            if (string.Compare(text, i, w, 0, w.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                word = text.Substring(i, w.Length);
                return true;
            }
        }
        word = "";
        return false;
    }

    private static Token LexNumber(string text, ref int i)
    {
        int start = i;
        if (text[i] == '0' && i + 1 < text.Length && (text[i + 1] is 'x' or 'X'))
        {
            i += 2;
            while (i < text.Length && Uri.IsHexDigit(text[i])) i++;
            var hex = text[(start + 2)..i];
            return new Token(TokenKind.Number, text[start..i], start) { Number = hex.Length == 0 ? 0 : Convert.ToInt64(hex, 16) };
        }
        while (i < text.Length && char.IsDigit(text[i])) i++;
        int decimals = 0;
        if (i < text.Length && text[i] == '.' && !(i + 1 < text.Length && char.IsLetter(text[i + 1]) && TryDotWord(text, i, out _)))
        {
            i++;
            int ds = i;
            while (i < text.Length && char.IsDigit(text[i])) i++;
            decimals = i - ds;
        }
        if (i < text.Length && (text[i] is 'e' or 'E') && i + 1 < text.Length && (char.IsDigit(text[i + 1]) || text[i + 1] is '+' or '-'))
        {
            i++;
            if (text[i] is '+' or '-') i++;
            while (i < text.Length && char.IsDigit(text[i])) i++;
        }
        var s = text[start..i];
        return new Token(TokenKind.Number, s, start)
        {
            Number = double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture),
            Decimals = decimals,
        };
    }
}
