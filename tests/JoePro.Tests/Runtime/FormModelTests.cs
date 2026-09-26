namespace JoePro.Tests.Runtime;

/// <summary>Object-model behavior that forms rely on (no UI attached).</summary>
public class FormModelTests : RuntimeHarness
{
    [Fact]
    public void Form_event_order_follows_vfp()
    {
        var o = Run("""
            PUBLIC gcLog
            gcLog = ""
            oForm = CREATEOBJECT("F")
            ? gcLog
            oForm.Release()
            ? gcLog
            DEFINE CLASS F AS Form
               ADD OBJECT cnt AS MyContainer
               PROCEDURE Load
                  gcLog = gcLog + "F.Load "
               ENDPROC
               PROCEDURE Init
                  gcLog = gcLog + "F.Init "
               ENDPROC
               PROCEDURE Destroy
                  gcLog = gcLog + "F.Destroy "
               ENDPROC
               PROCEDURE Unload
                  gcLog = gcLog + "F.Unload"
               ENDPROC
               PROCEDURE cnt.txt.Init
                  gcLog = gcLog + "txt.Init(form code) "
               ENDPROC
            ENDDEFINE
            DEFINE CLASS MyContainer AS Container
               ADD OBJECT txt AS TextBox
               PROCEDURE Init
                  gcLog = gcLog + "cnt.Init "
               ENDPROC
               PROCEDURE txt.Init
                  gcLog = gcLog + "txt.Init(container code) "
               ENDPROC
            ENDDEFINE
            """);
        Assert.Equal("F.Load txt.Init(form code) cnt.Init F.Init \nF.Load txt.Init(form code) cnt.Init F.Init F.Destroy F.Unload", o);
    }

    [Fact]
    public void Dodefault_from_form_code_runs_the_member_class_method()
    {
        var o = Run("""
            oForm = CREATEOBJECT("F")
            ? oForm.btn.Describe()
            DEFINE CLASS MyButton AS CommandButton
               PROCEDURE Describe
                  RETURN "class"
               ENDPROC
            ENDDEFINE
            DEFINE CLASS F AS Form
               ADD OBJECT btn AS MyButton
               PROCEDURE btn.Describe
                  RETURN "form+" + DODEFAULT()
               ENDPROC
            ENDDEFINE
            """);
        Assert.Equal("form+class", o);
    }

    [Fact]
    public void Groups_page_frames_and_grids_get_implicit_children()
    {
        var o = Run("""
            oForm = CREATEOBJECT("F")
            ? oForm.opg.Option3.Caption, oForm.pgf.Page2.Caption, oForm.cmg.Command1.Caption
            ? oForm.grd.Column2.Header1.Caption, oForm.pgf.Page2.txt.Value
            oForm.pgf.PageCount = 4
            ? oForm.pgf.Page4.Caption
            DEFINE CLASS F AS Form
               ADD OBJECT opg AS OptionGroup WITH ButtonCount = 3
               ADD OBJECT pgf AS PageFrame
               ADD OBJECT pgf.Page2.txt AS TextBox WITH Value = "x"
               ADD OBJECT cmg AS CommandGroup
               ADD OBJECT grd AS Grid WITH ColumnCount = 2
            ENDDEFINE
            """);
        Assert.Equal("Option3 Page2 Command1\nHeader1 x\nPage4", o);
    }

    [Fact]
    public void Do_form_without_ui_creates_the_form_object()
    {
        File.WriteAllText(Path.Combine(Dir, "about.jpform"), "DEFINE CLASS frmAbout AS Form\n   Caption = 'About'\nENDDEFINE");
        var o = Run("DO FORM about NAME oAbout\n? oAbout.Caption, oAbout.BaseClass");
        Assert.Equal("About Form", o);
        Assert.Contains(StatusMessages, m => m.Contains("no UI runtime"));
    }
}
