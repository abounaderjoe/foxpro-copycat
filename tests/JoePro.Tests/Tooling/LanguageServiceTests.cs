using JoePro.Runtime;
using JoePro.Tooling;

namespace JoePro.Tests.Tooling;

public class LanguageServiceTests
{
    private const string Source = """
        LOCAL lnTotal, laItems[3]
        lnTotal = CalcTotal(5)
        oInv = CREATEOBJECT("Invoice")
        ? oInv.GetTotal()
        FUNCTION CalcTotal(nQty)
           RETURN nQty * 2
        ENDFUNC
        DEFINE CLASS Invoice AS Custom
           Total = 0
           ADD OBJECT oLines AS Collection
           PROCEDURE GetTotal
              RETURN This.Total
           ENDPROC
        ENDDEFINE
        """;

    [Fact]
    public void Reports_syntax_errors_with_line_numbers()
    {
        var svc = new LanguageService();
        var d = Assert.Single(svc.Diagnostics("x = 1\nIF x\n  y = (1 +\nENDIF"));
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Equal(3, d.Line);
        Assert.Equal(3, d.Column);
    }

    [Fact]
    public void Flags_unknown_functions_and_the_exact_off_gotcha()
    {
        var svc = new LanguageService();
        var diags = svc.Diagnostics("x = Mystery(1)\ny = ALLTRIM('a')\nIF x <> \"\"\nENDIF");
        Assert.Contains(diags, d => d.Code == "JP1001" && d.Line == 1 && d.Column == 5);
        Assert.DoesNotContain(diags, d => d.Message.Contains("ALLTRIM"));
        Assert.Contains(diags, d => d.Code == "JP2001" && d.Line == 3);
        Assert.Empty(svc.Diagnostics(Source).Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.DoesNotContain(svc.Diagnostics(Source), d => d.Message.Contains("laItems") || d.Message.Contains("CalcTotal"));
    }

    [Fact]
    public void Completes_functions_procedures_variables_and_keywords()
    {
        var svc = new LanguageService();
        var items = svc.Complete(Source + "\n? ALLT", 15, 7);
        Assert.Contains(items, i => i.Label == "ALLTRIM" && i.Kind == CompletionKind.Function && i.Detail!.StartsWith("ALLTRIM(cExpression"));
        items = svc.Complete(Source + "\nx = Calc", 15, 9);
        Assert.Contains(items, i => i.Label == "CalcTotal");
        items = svc.Complete(Source + "\nx = lnT", 15, 8);
        Assert.Contains(items, i => i.Label == "lnTotal" && i.Kind == CompletionKind.Variable);
        items = svc.Complete("SCA", 1, 4);
        Assert.Contains(items, i => i.Label == "SCAN" && i.Kind == CompletionKind.Keyword);
    }

    [Fact]
    public void Completes_members_of_this_inside_a_class()
    {
        var svc = new LanguageService();
        var text = Source.Replace("RETURN This.Total", "RETURN This.");
        var line = LanguageService.SplitLines(text).ToList().FindIndex(l => l.Contains("RETURN This.")) + 1;
        var col = LanguageService.SplitLines(text)[line - 1].Length + 1;
        var items = svc.Complete(text, line, col);
        Assert.Contains(items, i => i.Label == "Total" && i.Kind == CompletionKind.Property);
        Assert.Contains(items, i => i.Label == "oLines");
        Assert.Contains(items, i => i.Label == "GetTotal" && i.Kind == CompletionKind.Method);
        Assert.Contains(items, i => i.Label == "AddProperty");
    }

    [Fact]
    public void Live_session_supplies_fields_and_values()
    {
        var dir = TestPaths.TempDir();
        var rt = new Interpreter(new TextWriterOutput(TextWriter.Null), dir);
        rt.ExecuteCommand("SET TALK OFF\nCREATE TABLE cust (name C(10), balance Y)\nINSERT INTO cust VALUES ('Ann', 12.5)\nnLimit = 100");
        var svc = new LanguageService(rt);
        var items = svc.Complete("? cust.", 1, 8);
        Assert.Contains(items, i => i.Label == "name" && i.Kind == CompletionKind.Field && i.Detail == "CUST.NAME C(10)");
        Assert.Contains("12.5000", svc.Hover("? cust.balance", 1, 10));
        Assert.Contains("= 100", svc.Hover("? nLimit", 1, 4));
        rt.Session.Dispose();
    }

    [Fact]
    public void Hover_and_definition_for_procedures_and_builtins()
    {
        var svc = new LanguageService();
        Assert.Equal("(function) CalcTotal(nQty)", svc.Hover(Source, 2, 13));
        Assert.StartsWith("CREATEOBJECT(cClassName", svc.Hover(Source, 3, 10));
        Assert.StartsWith("(class) Invoice AS Custom", svc.Hover(Source, 8, 15));
        var def = svc.Definition(Source, 2, 13, "main.prg");
        Assert.Equal(5, def!.Line);
        def = svc.Definition(Source, 4, 10, "main.prg");
        Assert.Equal(11, def!.Line);
    }

    [Fact]
    public void Definition_searches_workspace_files()
    {
        var dir = TestPaths.TempDir();
        var lib = Path.Combine(dir, "lib.prg");
        File.WriteAllText(lib, "* helpers\nPROCEDURE SharedHelper\n   RETURN 1\nENDPROC");
        var svc = new LanguageService(null, () => [lib]);
        var loc = svc.Definition("x = SharedHelper()", 1, 6, Path.Combine(dir, "main.prg"));
        Assert.Equal(lib, loc!.File);
        Assert.Equal(2, loc.Line);
        Assert.DoesNotContain(svc.Diagnostics("x = SharedHelper()"), d => d.Code == "JP1001");
    }

    [Fact]
    public void Document_symbols_include_classes_and_methods()
    {
        var syms = new LanguageService().Symbols(Source);
        Assert.Contains(syms, s => s.Name == "CalcTotal" && s.Kind == SymbolKind.Function && s.Line == 5 && s.EndLine == 7);
        var cls = Assert.Single(syms, s => s.Kind == SymbolKind.Class);
        Assert.Equal("Invoice", cls.Name);
        Assert.Contains(cls.Children, c => c.Name == "GetTotal" && c.Kind == SymbolKind.Method);
        Assert.Contains(cls.Children, c => c.Name == "Total" && c.Kind == SymbolKind.Property);
    }

    [Fact]
    public void Finds_references_in_code_but_not_in_strings_or_comments()
    {
        var svc = new LanguageService();
        const string text = "x = CalcTotal(1)\n* CalcTotal in a comment\n? \"CalcTotal\" && CalcTotal again\nFUNCTION CalcTotal(n)\n  RETURN n\n";
        var refs = svc.References(text, 1, 6, null, includeWorkspace: false);
        Assert.Equal([(1, 5), (4, 10)], refs.Select(r => (r.Line, r.Column)).ToArray());
    }

    [Fact]
    public void Rename_refuses_built_ins_and_invalid_names()
    {
        var svc = new LanguageService();
        Assert.Equal(2, svc.RenameTargets(Source, 2, 13, "ComputeTotal", null)!.Count);
        Assert.Null(svc.RenameTargets(Source, 3, 9, "MakeObject", null)); // CREATEOBJECT
        Assert.Null(svc.RenameTargets(Source, 2, 13, "1bad", null));
    }

    [Fact]
    public void Signature_help_tracks_the_active_parameter()
    {
        var svc = new LanguageService();
        var sig = svc.SignatureHelp("? SUBSTR(cName, 2, ", 1, 20)!;
        Assert.StartsWith("SUBSTR(", sig.Label);
        Assert.Equal(2, sig.ActiveParameter);
        var user = svc.SignatureHelp(Source + "\nx = CalcTotal(", 15, 15)!;
        Assert.Equal("CalcTotal(nQty)", user.Label);
        Assert.Equal(0, user.ActiveParameter);
    }
}
