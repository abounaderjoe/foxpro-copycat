using System.Text.RegularExpressions;

namespace JoePro.Documents;

/// <summary>Tracks TEXT … ENDTEXT blocks in method code, whose lines are data, not statements.</summary>
internal static partial class TextBlocks
{
    /// <summary>Returns true if <paramref name="line"/> belongs to a TEXT block (including its TEXT and ENDTEXT lines).</summary>
    public static bool Track(string line, ref bool inText)
    {
        if (inText)
        {
            if (EndText().IsMatch(line)) inText = false;
            return true;
        }
        if (StartText().IsMatch(line)) { inText = true; return true; }
        return false;
    }

    [GeneratedRegex(@"^\s*TEXT(\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex StartText();

    [GeneratedRegex(@"^\s*ENDT(E|EX|EXT)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex EndText();
}
