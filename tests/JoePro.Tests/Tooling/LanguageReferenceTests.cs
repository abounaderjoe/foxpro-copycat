using JoePro.Runtime.Builtins;
using JoePro.Tooling;

namespace JoePro.Tests.Tooling;

public class LanguageReferenceTests
{
    [Fact]
    public void Every_builtin_function_has_a_signature_and_description()
    {
        var missing = Library.Names.Where(n => !n.StartsWith("__") && !FunctionDocs.Functions.ContainsKey(n)).Order().ToList();
        Assert.True(missing.Count == 0, "No docs for: " + string.Join(", ", missing));
        Assert.All(FunctionDocs.Functions.Values, d => Assert.False(string.IsNullOrWhiteSpace(d.Summary)));
    }

    [Fact]
    public void Reference_lists_every_function_and_the_command_syntax()
    {
        var md = LanguageReference.Markdown();
        Assert.Contains("`XMLTOCURSOR(eExpression \\| cXMLFile [, cCursorName [, nFlags]])` | Numeric |", md);
        Assert.Contains("| SORT | SORT TO TableName ON FieldName1", md);
        Assert.Equal(Library.Names.Count(n => !n.StartsWith("__")), md.Split('\n').Count(l => l.StartsWith("| `")));
    }
}
