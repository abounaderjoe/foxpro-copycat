using System.Xml;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace JoePro.Ide;

/// <summary>FoxPro syntax highlighting for the code editor and the Command Window (light and dark palettes).</summary>
public static class FoxProHighlighting
{
    private static IHighlightingDefinition? _light, _dark;

    private static readonly string[] Keywords =
    [
        "IF", "ELSE", "ENDIF", "DO", "WHILE", "ENDDO", "CASE", "OTHERWISE", "ENDCASE", "FOR", "EACH", "TO", "STEP", "ENDFOR", "NEXT",
        "SCAN", "ENDSCAN", "EXIT", "LOOP", "RETURN", "PROCEDURE", "ENDPROC", "FUNCTION", "ENDFUNC", "PARAMETERS", "LPARAMETERS",
        "LOCAL", "PRIVATE", "PUBLIC", "DIMENSION", "DECLARE", "STORE", "TRY", "CATCH", "FINALLY", "ENDTRY", "THROW", "WITH", "ENDWITH",
        "DEFINE", "CLASS", "AS", "OF", "ENDDEFINE", "ADD", "OBJECT", "PROTECTED", "HIDDEN", "NODEFAULT", "TEXT", "ENDTEXT",
        "USE", "SELECT", "GO", "GOTO", "TOP", "BOTTOM", "SKIP", "SEEK", "LOCATE", "CONTINUE", "REPLACE", "APPEND", "BLANK", "DELETE",
        "RECALL", "PACK", "ZAP", "INDEX", "ON", "TAG", "SET", "ORDER", "FILTER", "RELATION", "INTO", "CLOSE", "TABLES", "DATABASES",
        "CREATE", "TABLE", "CURSOR", "DATABASE", "OPEN", "ALTER", "INSERT", "VALUES", "UPDATE", "FROM", "WHERE", "GROUP", "BY", "HAVING",
        "UNION", "ALL", "DISTINCT", "DESC", "ASC", "JOIN", "INNER", "LEFT", "RIGHT", "OUTER", "FULL", "IN", "NOT", "AND", "OR", "LIKE", "BETWEEN", "IS",
        "NULL", "BEGIN", "END", "TRANSACTION", "ROLLBACK", "LIST", "DISPLAY", "BROWSE", "COUNT", "SUM", "AVERAGE", "CALCULATE",
        "SCATTER", "GATHER", "MEMVAR", "COPY", "IMPORT", "FOXPRO", "READ", "EVENTS", "CLEAR", "WAIT", "WINDOW", "NOWAIT",
        "RELEASE", "QUIT", "CANCEL", "FORM", "MODIFY", "COMMAND", "ERROR", "AGAIN", "ALIAS", "EXCLUSIVE", "SHARED", "ADDITIVE",
        "THIS", "THISFORM", "THISFORMSET", "DESCENDING", "ASCENDING", "CANDIDATE", "UNIQUE", "PRIMARY", "KEY", "DEFAULT", "CHECK",
    ];

    public static IHighlightingDefinition Get(bool dark)
    {
        if (dark) return _dark ??= Load(true);
        return _light ??= Load(false);
    }

    private static IHighlightingDefinition Load(bool dark)
    {
        var (comment, str, number, keyword, literal, preproc) = dark
            ? ("#6A9955", "#CE9178", "#B5CEA8", "#569CD6", "#4FC1FF", "#C586C0")
            : ("#008000", "#A31515", "#098658", "#0000FF", "#0070C1", "#AF00DB");
        var words = string.Join("", Keywords.Select(k => $"<Word>{k}</Word>"));
        var xshd = $$"""
            <SyntaxDefinition name="FoxPro" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Comment" foreground="{{comment}}" fontStyle="italic" />
              <Color name="String" foreground="{{str}}" />
              <Color name="Number" foreground="{{number}}" />
              <Color name="Keyword" foreground="{{keyword}}" fontWeight="bold" />
              <Color name="Literal" foreground="{{literal}}" />
              <Color name="Preprocessor" foreground="{{preproc}}" />
              <RuleSet ignoreCase="true">
                <Span color="Comment" begin="^\s*(\*|NOTE\b)" />
                <Span color="Comment" begin="&amp;&amp;" />
                <Span color="Preprocessor" begin="^\s*\#" />
                <Span color="String" begin="&quot;" end="&quot;" />
                <Span color="String" begin="'" end="'" />
                <Span color="String" begin="(?&lt;![\w\)\]])\[" end="\]" />
                <Span color="Literal" begin="\{" end="\}" />
                <Keywords color="Keyword">{{words}}</Keywords>
                <Rule color="Literal">\.(T|F|Y|N|NULL|AND|OR|NOT)\.</Rule>
                <Rule color="Number">\b0x[0-9a-fA-F]+\b|\$?\b\d+(\.\d+)?([eE][+-]?\d+)?\b</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """;
        using var reader = XmlReader.Create(new StringReader(xshd));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}
