using JoePro.Core;
using JoePro.Legacy.Formats;

namespace JoePro.Tests.Runtime;

/// <summary>Legacy .vcx and .scx files run directly: they are converted in memory when loaded.</summary>
public class LegacyFormTests : RuntimeHarness
{
    private void CopyCorpus(params string[] names)
    {
        foreach (var n in names) File.Copy(TestPaths.Corpus("foxunit", n), Path.Combine(Dir, n), overwrite: true);
    }

    [Fact]
    public void Set_classlib_to_a_real_vcx_and_use_its_classes()
    {
        CopyCorpus("cprogres.vcx", "cprogres.VCT");
        var o = Run("""
            SET CLASSLIB TO cprogres.vcx
            oForm = CREATEOBJECT("Form")
            oForm.AddObject("oBar", "cprogressbar", 0, 100, .F.)
            ? oForm.oBar.SetValue(50), oForm.oBar.lblPercent.Caption, oForm.oBar.shpBar.Width, oForm.oBar.BackStyle
            TRY
              ? oForm.oBar.nMax
            CATCH TO oErr
              ? oErr.ErrorNo
            ENDTRY
            """);
        Assert.Equal(".T. 50%        112          1\n\n      1734", o);
        Assert.Contains(StatusMessages, m => m.Contains("cprogres.vcx was converted in memory"));
    }

    private static readonly FieldDef[] ScxFields =
    [
        new("PLATFORM", 'C', 8), new("UNIQUEID", 'C', 10), new("TIMESTAMP", 'N', 10), new("CLASS", 'M'), new("CLASSLOC", 'M'),
        new("BASECLASS", 'M'), new("OBJNAME", 'M'), new("PARENT", 'M'), new("PROPERTIES", 'M'), new("PROTECTED", 'M'),
        new("METHODS", 'M'), new("OBJCODE", 'M'), new("OLE", 'M'), new("OLE2", 'M'), new("RESERVED1", 'M'), new("RESERVED2", 'M'),
        new("RESERVED3", 'M'), new("RESERVED4", 'M'), new("RESERVED5", 'M'), new("RESERVED6", 'M'), new("RESERVED7", 'M'),
        new("RESERVED8", 'M'), new("USER", 'M'),
    ];

    /// <summary>A row with the given columns; the rest are empty.</summary>
    private static (bool, Value[]) Row(string platform, string cls = "", string baseClass = "", string name = "", string parent = "",
        string props = "", string methods = "", string reserved1 = "", string reserved3 = "", string prot = "")
    {
        var values = ScxFields.Select(f => f.Type == 'M' ? Value.String("") : f.Type == 'N' ? Value.Number(0) : Value.String("")).ToArray();
        void Set(string col, string v) => values[Array.FindIndex(ScxFields, f => f.Name == col)] = Value.String(v.Replace("\n", "\r\n"));
        Set("PLATFORM", platform); Set("CLASS", cls); Set("BASECLASS", baseClass); Set("OBJNAME", name); Set("PARENT", parent);
        Set("PROPERTIES", props); Set("METHODS", methods); Set("RESERVED1", reserved1); Set("RESERVED3", reserved3); Set("PROTECTED", prot);
        return (false, values);
    }

    [Fact]
    public void Do_form_runs_a_legacy_scx_with_its_data_environment()
    {
        DbfWriter.Write(Path.Combine(Dir, "cust.scx"), ScxFields,
        [
            Row("COMMENT"),
            Row("WINDOWS", "dataenvironment", "dataenvironment", "Dataenvironment", "",
                "Top = 0\nLeft = 0\nName = \"Dataenvironment\"",
                "PROCEDURE BeforeOpenTables\ngcLog = gcLog + \"de;\"\nENDPROC"),
            Row("WINDOWS", "cursor", "cursor", "Cursor1", "Dataenvironment",
                "Alias = \"cust\"\nCursorSource = customer.dbf\nName = \"Cursor1\""),
            Row("WINDOWS", "form", "form", "Form1", "",
                "Caption = \"Customers\"\nDoCreate = .T.\nWidth = 300\nncount = 5\nName = \"Form1\"",
                "PROCEDURE Load\ngcLog = gcLog + \"load:\" + TRANSFORM(USED(\"cust\"))\nENDPROC\nPROCEDURE Init\ngcLog = gcLog + \";init\"\nENDPROC\nPROCEDURE Unload\ngcLog = gcLog + \";unload:\" + TRANSFORM(USED(\"cust\"))\nENDPROC",
                reserved3: "ncount Count of things\n*refreshall Refreshes everything\n"),
            Row("WINDOWS", "textbox", "textbox", "txtName", "Form1",
                "ControlSource = \"cust.name\"\nBackColor = 255,0,0\nLeft = 10\nName = \"txtName\"",
                "PROCEDURE Init\ngcLog = gcLog + \";txt\"\nENDPROC"),
            Row("WINDOWS", "pageframe", "pageframe", "Pageframe1", "Form1",
                "ErasePage = .T.\nPageCount = 2\nPage1.Caption = \"First\"\nPage2.Caption = \"Second\"\nName = \"Pageframe1\""),
            Row("WINDOWS", "label", "label", "Label1", "Form1.Pageframe1.Page1", "Caption = \"On page\"\nName = \"Label1\""),
            Row("COMMENT", reserved1: "RESERVED"),
        ]);
        var o = Run("""
            CREATE TABLE customer (name C(10))
            INSERT INTO customer VALUES ("Alpha")
            USE
            PUBLIC gcLog
            gcLog = ""
            DO FORM cust.scx NAME oF NOSHOW
            ? gcLog
            ? oF.Caption, oF.nCount, oF.Name, oF.txtName.BackColor, EVALUATE(oF.txtName.ControlSource)
            ? oF.Pageframe1.Page2.Caption, oF.Pageframe1.Page1.Label1.Caption, PEMSTATUS(oF, "RefreshAll", 5)
            oF.Release()
            ? gcLog
            """);
        Assert.Equal("de;load:.T.;txt;init\nCustomers          5 Form1        255 Alpha     \nSecond On page .T.\nde;load:.T.;txt;init;unload:.T.", o);
        Run("? USED(\"cust\")");
        Assert.Equal(".F.", Out.ToString().Trim());
    }
}
