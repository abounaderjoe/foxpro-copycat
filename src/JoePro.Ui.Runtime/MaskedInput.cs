using System.Text;
using JoePro.Core;
using JoePro.Runtime.Builtins;

namespace JoePro.Ui.Runtime;

/// <summary>
/// InputMask and Format for text boxes and spinners, as in VFP. Format codes apply to the whole value
/// (! uppercase, K select on entry, R mask characters are not stored, T trim, Z blank when zero, $ currency,
/// D/E date formats, L leading zeros); InputMask characters apply per position (9 digit, # digit/sign/space,
/// A letter, N letter or digit, X any, ! uppercase, L/Y logical, other characters are literals).
/// </summary>
public sealed class MaskedInput
{
    public MaskedInput(string? format, string? mask)
    {
        var f = (format ?? "").Trim();
        var m = mask ?? "";
        // A Format may carry its own picture after a blank ("@R 999-9999"), as TRANSFORM does.
        if (f.StartsWith('@')) f = f[1..];
        var sp = f.IndexOf(' ');
        if (sp >= 0)
        {
            if (m.Length == 0) m = f[(sp + 1)..];
            f = f[..sp];
        }
        Codes = f.ToUpperInvariant();
        Mask = m;
    }

    public string Codes { get; }
    public string Mask { get; }
    public bool IsEmpty => Codes.Length == 0 && Mask.Length == 0;
    public bool Uppercase => Codes.Contains('!');
    public bool SelectOnEntry => Codes.Contains('K');
    public bool RawValue => Codes.Contains('R');

    public static bool IsSlot(char m) => m is '9' or '#' or 'A' or 'N' or 'X' or '!' or 'L' or 'Y' or '$' or '*';

    private static bool Accepts(char slot, char ch) => slot switch
    {
        '9' => char.IsDigit(ch),
        '#' => char.IsDigit(ch) || ch is ' ' or '+' or '-' or '.',
        'A' => char.IsLetter(ch),
        'N' => char.IsLetterOrDigit(ch),
        'L' => "TtFfYyNn".Contains(ch),
        'Y' => "YyNn".Contains(ch),
        _ => true,
    };

    /// <summary>
    /// Applies a character mask to typed text: characters that do not fit their position are dropped and literal
    /// characters are inserted. Text beyond the mask is cut off.
    /// </summary>
    public string ApplyCharacterMask(string text)
    {
        if (Mask.Length == 0) return Uppercase ? text.ToUpperInvariant() : text;
        var sb = new StringBuilder();
        int j = 0;
        for (int i = 0; i < Mask.Length; i++)
        {
            var m = Mask[i];
            if (!IsSlot(m))
            {
                if (j >= text.Length) break;
                sb.Append(m);
                if (text[j] == m) j++;
                continue;
            }
            while (j < text.Length && !Accepts(m, text[j])) j++;
            if (j >= text.Length) break;
            var ch = text[j++];
            sb.Append(m == '!' || Uppercase || m is 'L' or 'Y' ? char.ToUpperInvariant(ch) : ch);
        }
        return sb.ToString();
    }

    /// <summary>The value stored for masked text: with the R format code the mask's literal characters are left out.</summary>
    public string StoredText(string masked)
    {
        if (!RawValue || Mask.Length == 0) return masked;
        var sb = new StringBuilder();
        for (int i = 0; i < masked.Length; i++)
            if (i >= Mask.Length || IsSlot(Mask[i])) sb.Append(masked[i]);
        return sb.ToString();
    }

    /// <summary>Stored text back to the masked form shown while editing (re-inserting literals for the R format code).</summary>
    public string EditText(string stored)
    {
        if (!RawValue || Mask.Length == 0) return stored;
        var sb = new StringBuilder();
        int j = 0;
        foreach (var m in Mask)
        {
            if (j >= stored.Length) break;
            sb.Append(IsSlot(m) ? stored[j++] : m);
        }
        return sb.ToString();
    }

    /// <summary>Numeric input keeps digits, one decimal point and a leading sign.</summary>
    public static string FilterNumber(string text, char point)
    {
        var sb = new StringBuilder();
        bool dot = false;
        foreach (var ch in text)
        {
            if (char.IsDigit(ch)) sb.Append(ch);
            else if ((ch == point || ch == '.') && !dot) { sb.Append(point); dot = true; }
            else if (ch is '-' or '+' && sb.Length == 0) sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>How the value is displayed when the control does not have the focus.</summary>
    public string Display(Value v, SetOptions o)
    {
        var picture = (Codes.Length > 0 ? "@" + Codes.Replace("K", "").Replace("R", "") + (Mask.Length > 0 ? " " : "") : "") + Mask;
        if (picture == "@") picture = "";
        if (v.Kind == ValueKind.Character && RawValue && Mask.Length > 0) return EditText(v.AsString.TrimEnd());
        if (picture.Length == 0) return v.Kind == ValueKind.Character ? v.AsString.TrimEnd() : Library.TransformDefault(v, o);
        var text = Library.Transform(v, picture, o);
        return v.Kind == ValueKind.Character ? text.TrimEnd() : text;
    }
}
