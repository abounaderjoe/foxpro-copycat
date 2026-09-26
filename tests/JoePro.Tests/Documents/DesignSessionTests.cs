using JoePro.Documents;
using JoePro.Documents.Design;

namespace JoePro.Tests.Documents;

public class DesignSessionTests
{
    private static DesignSession NewSession()
    {
        var s = new DesignSession(DesignSession.NewForm("frmOrders"));
        var pgf = s.AddObject("", "PageFrame", properties: [new("PageCount", "2")]);
        s.AddObject(pgf + ".Page1", "TextBox", properties: [new("Left", "10"), new("Top", "20")]);
        return s;
    }

    [Fact]
    public void Adds_objects_with_vfp_default_names()
    {
        var s = NewSession();
        Assert.Equal(["Pageframe1", "Pageframe1.Page1.Text1"], s.Class.Members.Select(m => m.Path));
        Assert.Equal("Command1", s.AddObject("", "CommandButton").Split('.')[^1]);
        Assert.Equal("Command2", s.AddObject("", "CommandButton"));
    }

    [Fact]
    public void Properties_of_implicit_children_are_stored_on_the_declared_ancestor()
    {
        var s = NewSession();
        s.SetProperty("Pageframe1.Page2", "Caption", "\"Details\"");
        Assert.Equal("\"Details\"", s.Class.FindMember("Pageframe1")!.Properties["Page2.Caption"]);
        Assert.Equal("\"Details\"", s.GetProperty("Pageframe1.Page2", "Caption"));
        s.SetProperty("", "Caption", "\"Orders\"");
        Assert.Equal("\"Orders\"", s.Class.Properties["Caption"]);
        s.SetProperty("Pageframe1.Page2", "Caption", null);
        Assert.Null(s.GetProperty("Pageframe1.Page2", "Caption"));
    }

    [Fact]
    public void Undo_and_redo_restore_exact_documents()
    {
        var s = NewSession();
        var before = s.Text;
        s.SetProperties("Move", [("Pageframe1.Page1.Text1", "Left", "30"), ("Pageframe1.Page1.Text1", "Top", "40")]);
        var after = s.Text;
        Assert.Equal("Move", s.UndoName);
        s.Undo();
        Assert.Equal(before, s.Text);
        s.Redo();
        Assert.Equal(after, s.Text);
        s.Transaction("Nothing", () => { });
        Assert.Equal("Move", s.UndoName); // an empty transaction is not an undo step
    }

    [Fact]
    public void Rename_moves_children_and_method_code()
    {
        var s = NewSession();
        s.SetMethod("Pageframe1.Page1.Text1", "Valid", "RETURN !EMPTY(THIS.Value)");
        s.RenameObject("Pageframe1", "pgfMain");
        Assert.Equal(["pgfMain", "pgfMain.Page1.Text1"], s.Class.Members.Select(m => m.Path));
        Assert.Equal("RETURN !EMPTY(THIS.Value)", s.GetMethod("pgfMain.Page1.Text1", "Valid"));
        Assert.Throws<ArgumentException>(() => s.RenameObject("pgfMain", "1bad"));
    }

    [Fact]
    public void Remove_takes_children_code_and_implicit_child_properties()
    {
        var s = NewSession();
        s.SetMethod("Pageframe1.Page1.Text1", "Click", "? 1");
        s.SetProperty("Pageframe1.Page1", "Caption", "\"One\"");
        s.RemoveObject("Pageframe1.Page1.Text1");
        Assert.Equal(["Pageframe1"], s.Class.Members.Select(m => m.Path));
        Assert.Empty(s.Class.Methods);
        s.RemoveObject("Pageframe1.Page1");
        Assert.Null(s.Class.FindMember("Pageframe1")!.Properties["Page1.Caption"]);
    }

    [Fact]
    public void Z_order_and_clipboard()
    {
        var s = NewSession();
        s.AddObject("", "Label");
        s.ChangeZOrder("Pageframe1", toFront: true);
        Assert.Equal(["Label1", "Pageframe1", "Pageframe1.Page1.Text1"], s.Class.Members.Select(m => m.Path));
        s.ChangeZOrder("Pageframe1", toFront: false);
        Assert.Equal("Pageframe1", s.Class.Members[0].Path);

        s.SetMethod("Pageframe1.Page1.Text1", "Valid", "RETURN .T.");
        var clip = s.Copy(["Pageframe1.Page1.Text1"]);
        var pasted = s.Paste(clip, "Pageframe1.Page1");
        Assert.Equal(["Pageframe1.Page1.Text2"], pasted);
        Assert.Equal("18", s.Class.FindMember("Pageframe1.Page1.Text2")!.Properties["Left"]);
        Assert.Equal("RETURN .T.", s.GetMethod("Pageframe1.Page1.Text2", "Valid"));
    }

    [Fact]
    public void Dirty_tracking_and_class_members()
    {
        var s = NewSession();
        s.MarkSaved();
        Assert.False(s.IsDirty);
        s.AddProperty("nCount", "0", "PROTECTED");
        s.AddMethod("Recalc", "HIDDEN");
        Assert.True(s.IsDirty);
        Assert.Contains("nCount", s.Class.Protected);
        Assert.Equal("HIDDEN", s.Class.FindMethod("Recalc")!.Visibility);
        s.Undo(); s.Undo();
        Assert.False(s.IsDirty);
    }

    [Theory]
    [InlineData("Customers", "C", "\"Customers\"")]
    [InlineData("say \"hi\"", "C", "'say \"hi\"'")]
    [InlineData("120", "N", "120")]
    [InlineData("1.5", "N", "1.5")]
    [InlineData("t", "L", ".T.")]
    [InlineData(".F.", "L", ".F.")]
    [InlineData("=DATE() + 1", "D", "DATE() + 1")]
    [InlineData("2024-03-01", "D", "{^2024-03-01}")]
    [InlineData("12abc", "N", "\"12abc\"")]
    public void Property_sheet_input_follows_vfp_rules(string input, string type, string expected)
    {
        var current = type switch
        {
            "C" => JoePro.Core.Value.String("x"),
            "N" => JoePro.Core.Value.Number(0),
            "L" => JoePro.Core.Value.False,
            _ => JoePro.Core.Value.EmptyDate,
        };
        Assert.Equal(expected, PropertyInput.ToExpression(input, current));
    }

    [Fact]
    public void Property_sheet_shows_expressions_with_an_equals_sign()
    {
        Assert.Equal("Customers", PropertyInput.ToDisplay("\"Customers\""));
        Assert.Equal("120", PropertyInput.ToDisplay("120"));
        Assert.Equal("RGB(255,0,0)", PropertyInput.ToDisplay("RGB(255,0,0)")); // a color literal
        Assert.Equal("=DATE()", PropertyInput.ToDisplay("DATE()"));
    }

    [Fact]
    public void Colors_are_shown_and_typed_as_red_green_blue()
    {
        Assert.Equal("255,0,0", PropertyInput.ToDisplay("RGB(255,0,0)", "BackColor"));
        Assert.Equal("240,240,240", PropertyInput.ToDisplay("15790320", "BackColor"));
        Assert.Equal("RGB(0,128,255)", PropertyInput.ToExpression("0, 128, 255", JoePro.Core.Value.Number(0), "ForeColor"));
        Assert.Equal("255", PropertyInput.ToExpression("255", JoePro.Core.Value.Number(0), "ForeColor"));
        Assert.Equal("\"1,2,3\"", PropertyInput.ToExpression("1,2,3", JoePro.Core.Value.String(""), "Caption"));
    }
}
