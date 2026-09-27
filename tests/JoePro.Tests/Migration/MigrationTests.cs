using System.Text;
using JoePro.Core;
using JoePro.Legacy.Formats;
using JoePro.Migration;
using JoePro.Runtime;

namespace JoePro.Tests.Migration;

public class MigrationTests
{
    private readonly string _dir = TestPaths.TempDir();

    [Fact]
    public void Imports_real_table_with_memo_and_deleted_rows()
    {
        var report = new MigrationReport();
        var importer = new LegacyImporter(report);
        var target = importer.ImportTable(TestPaths.Corpus("dbfread", "memotest.dbf"), Path.Combine(_dir, "out"));
        Assert.True(File.Exists(target));
        var t = Assert.Single(report.Tables);
        Assert.Equal(3, t.RowsImported);
        Assert.Equal(1, t.DeletedRows);
        Assert.True(t.RowsVerified);
        Assert.Contains(report.Findings, f => f.Rule == "DATA.TABLE.IMPORTED" && f.Status == FindingStatus.Converted);
        Assert.Contains(report.Findings, f => f.Rule == "DATA.CODEPAGE.UNMARKED");

        var output = new StringWriter();
        var rt = new Interpreter(new TextWriterOutput(output), Path.Combine(_dir, "out"));
        rt.ExecuteCommand("USE memotest\nGO 2\n? ALLTRIM(name), memo, DTOS(birthdate), DELETED()\nGO 3\n? DELETED()");
        Assert.Equal("Bob Bob memo 19801112 .F.\n.T.", output.ToString().Replace("\r\n", "\n"));
        rt.Session.Dispose();
    }

    [Fact]
    public void Imports_database_with_long_names_procedures_relations_and_views()
    {
        var legacy = Path.Combine(_dir, "legacy");
        Directory.CreateDirectory(legacy);
        // Table bound to the database: truncated field names in the DBF, long names in the DBC.
        DbfWriter.Write(Path.Combine(legacy, "customers.dbf"),
            [new FieldDef("CUSTOMER_I", 'I'), new FieldDef("COMPANY_NA", 'C', 30)],
            [(false, [Value.Number(1), Value.String("Acme")]), (false, [Value.Number(2), Value.String("Globex")])],
            dbcBacklink: "shop.dbc");
        var procs = "PROCEDURE NextId\nLPARAMETERS cTable\nlcCmd = 'SELECT ' + cTable\n&lcCmd\nRETURN 1\nENDPROC";
        DbfWriter.Write(Path.Combine(legacy, "shop.dbc"),
            [new FieldDef("OBJECTID", 'I'), new FieldDef("PARENTID", 'I'), new FieldDef("OBJECTTYPE", 'C', 10), new FieldDef("OBJECTNAME", 'C', 128),
             new FieldDef("PROPERTY", 'M') { Binary = true }, new FieldDef("CODE", 'M'), new FieldDef("RIINFO", 'C', 6)],
            [
                (false, [Value.Number(1), Value.Number(1), Value.String("Database"), Value.String("Database"), Value.String(""), Value.String(""), Value.String("")]),
                (false, [Value.Number(2), Value.Number(1), Value.String("Database"), Value.String("StoredProceduresSource"), Value.String(""), Value.String(procs), Value.String("")]),
                (false, [Value.Number(3), Value.Number(1), Value.String("Table"), Value.String("customers"), Value.String("\u0001\0\0\0customers.dbf\0"), Value.String(""), Value.String("")]),
                (false, [Value.Number(4), Value.Number(3), Value.String("Field"), Value.String("customer_id"), Value.String(""), Value.String(""), Value.String("")]),
                (false, [Value.Number(5), Value.Number(3), Value.String("Field"), Value.String("company_name"), Value.String(""), Value.String(""), Value.String("")]),
                (false, [Value.Number(6), Value.Number(3), Value.String("Relation"), Value.String("Relation 1"), Value.String(""), Value.String(""), Value.String("")]),
                (false, [Value.Number(7), Value.Number(1), Value.String("View"), Value.String("big_customers"), Value.String("\u0002SELECT * FROM customers WHERE customer_id > 1\0"), Value.String(""), Value.String("")]),
            ]);

        var report = new MigrationReport();
        new LegacyImporter(report).ImportFolder(legacy, Path.Combine(_dir, "out"));

        Assert.Contains(report.Findings, f => f.Rule == "DATA.DBC.PROCEDURES" && f.Status == FindingStatus.Converted);
        Assert.Contains(report.Findings, f => f.Rule == "CODE.MACRO.DYNAMIC" && f.Source.Line == 4);
        Assert.Contains(report.Findings, f => f.Rule == "DATA.DBC.RELATION" && f.Status == FindingStatus.NeedsReview);
        var view = Assert.Single(report.Findings, f => f.Rule == "DATA.DBC.VIEW");
        Assert.Equal("SELECT * FROM customers WHERE customer_id > 1", view.Source.Snippet);
        Assert.DoesNotContain(report.Findings, f => f.Rule == "DATA.DBC.ORPHAN");

        var output = new StringWriter();
        var rt = new Interpreter(new TextWriterOutput(output), Path.Combine(_dir, "out"));
        rt.ExecuteCommand("OPEN DATABASE shop\nUSE customers\nGO 2\n? customer_id, ALLTRIM(company_name)\n? NextId('customers')");
        Assert.Equal("         2 Globex\n         1", output.ToString().Replace("\r\n", "\n"));
        rt.Session.Dispose();
    }

    [Fact]
    public void Analyzes_programs_and_reports_unconverted_artifacts()
    {
        var legacy = Path.Combine(_dir, "app");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "main.prg"), """
            DECLARE INTEGER GetTickCount IN kernel32
            oXl = CREATEOBJECT("Excel.Application")
            DO FORM customer
            @ 1, 1 SAY "Hello"
            x = SYS(9999)
            """);
        File.WriteAllText(Path.Combine(legacy, "clean.prg"), "RETURN 1 + 1");
        File.WriteAllText(Path.Combine(legacy, "broken.prg"), "IF x\n? 1");
        File.WriteAllBytes(Path.Combine(legacy, "customer.scx"), [0x30]);

        var report = new MigrationReport();
        new LegacyImporter(report).ImportFolder(legacy, Path.Combine(_dir, "out"));

        Assert.Contains(report.Findings, f => f.Rule == "CODE.DLL.DECLARE" && f.Status == FindingStatus.Unsupported);
        Assert.Contains(report.Findings, f => f.Rule == "CODE.COM.AUTOMATION");
        Assert.Contains(report.Findings, f => f.Rule == "CODE.SCREEN.SAYGET");
        Assert.Contains(report.Findings, f => f.Rule == "CODE.SYS.FUNCTION");
        Assert.Contains(report.Findings, f => f.Rule == "CODE.COMPILES" && f.Source.File == "clean.prg");
        Assert.Contains(report.Findings, f => f.Rule == "CODE.COMPILE" && f.Status == FindingStatus.Failed && f.Source.File == "broken.prg");
        // An unreadable form is reported as failed and its original is kept.
        Assert.Contains(report.Findings, f => f.Rule == "FORM.READ" && f.Status == FindingStatus.Failed && f.Source.File == "customer.scx");
        Assert.True(File.Exists(Path.Combine(_dir, "out", "legacy-originals", "customer.scx")));

        report.Save(Path.Combine(_dir, "out"));
        var json = File.ReadAllText(Path.Combine(_dir, "out", "migration-report.json"));
        Assert.Contains("\"status\": \"Unsupported\"", json);
        var roundTrip = MigrationReport.FromJson(json);
        Assert.Equal(report.Findings.Count, roundTrip.Findings.Count);
        var html = File.ReadAllText(Path.Combine(_dir, "out", "migration-report.html"));
        Assert.Contains("<title>Migration Report</title>", html);
        Assert.Contains("CODE.DLL.DECLARE", html);
    }

    [Fact]
    public void Import_command_opens_the_converted_table()
    {
        MigrationCommands.Register();
        File.Copy(TestPaths.Corpus("dbfread", "memotest.dbf"), Path.Combine(_dir, "memotest.dbf"));
        File.Copy(TestPaths.Corpus("dbfread", "memotest.FPT"), Path.Combine(_dir, "memotest.FPT"));
        var output = new StringWriter();
        var rt = new Interpreter(new TextWriterOutput(output), _dir);
        rt.ExecuteCommand("USE memotest.dbf\nIMPORT FOXPRO memotest.dbf TO converted\n? ALIAS(), RECCOUNT(), CURSORGETPROP('SourceType')");
        Assert.Equal("MEMOTEST          3          3", output.ToString());
        Assert.True(File.Exists(Path.Combine(_dir, "converted", "migration-report.html")));
        rt.Session.Dispose();
    }

    [Fact]
    public void Class_libraries_convert_to_jpclass_files_with_report_findings()
    {
        var legacy = Path.Combine(_dir, "legacy2");
        Directory.CreateDirectory(Path.Combine(legacy, "libs"));
        foreach (var f in new[] { "cprogres.vcx", "cprogres.VCT" })
            File.Copy(TestPaths.Corpus("foxunit", f), Path.Combine(legacy, "libs", f));
        File.WriteAllText(Path.Combine(legacy, "main.prg"), "SET CLASSLIB TO libs\\cprogres\noBar = CREATEOBJECT(\"cprogressbar\")\n? oBar.SetValue(25)");

        var report = new MigrationReport();
        var output = Path.Combine(_dir, "out2");
        new LegacyImporter(report).ImportFolder(legacy, output);

        var jpclass = Path.Combine(output, "libs", "cprogres.jpclass");
        Assert.True(File.Exists(jpclass));
        Assert.True(File.Exists(Path.Combine(output, "main.prg")));
        Assert.Contains(report.Findings, f => f.Rule == "CLASSLIB.CONVERTED" && f.Source.File == Path.Combine("libs", "cprogres.vcx"));
        var text = File.ReadAllText(jpclass);
        Assert.StartsWith("*-- Joe Pro class library v1", text);
        Assert.Contains("BackColor = RGB(64,0,128)", text); // designer colors become RGB() expressions

        // The converted program runs against the converted library.
        var rt = new JoePro.Runtime.Interpreter(new JoePro.Runtime.TextWriterOutput(new StringWriter()), output);
        try
        {
            rt.ExecuteCommand("SET CLASSLIB TO libs/cprogres.jpclass");
            Assert.Equal(".T.", JoePro.Core.Formatter.ToDisplay(rt.Evaluate("CREATEOBJECT('cprogressbar').SetValue(25)"), rt.Options));
        }
        finally { rt.Session.Dispose(); }
    }

    [Fact]
    public void Reports_and_labels_convert_and_run_after_import()
    {
        var legacy = Path.Combine(_dir, "legacy3");
        Directory.CreateDirectory(legacy);
        new JoePro.Tests.Documents.Frx().Band(1, 0.3).Band(4, 0.2).Label(1, "Items", 0, 0, 1).Field(4, "ALLTRIM(items.name)", 0, 0, 2).Save(Path.Combine(legacy, "items.frx"));
        new JoePro.Tests.Documents.Frx(columns: 2, columnWidth: 30000).Band(4, 1).Field(4, "items.name", 0.1, 0.1, 2).Save(Path.Combine(legacy, "tags.lbx"));
        var report = new MigrationReport();
        var output = Path.Combine(_dir, "out3");
        new LegacyImporter(report).ImportFolder(legacy, output);
        Assert.True(File.Exists(Path.Combine(output, "items.jpreport")));
        Assert.True(File.Exists(Path.Combine(output, "tags.jplabel")));
        Assert.Contains(report.Findings, f => f.Rule == "REPORT.CONVERTED");
        Assert.Contains(report.Findings, f => f.Rule == "LABEL.CONVERTED");
        Assert.DoesNotContain(report.Findings, f => f.Rule == "ARTIFACT.NOT_YET_CONVERTED" && f.Category is "frx" or "lbx");

        var text = new StringWriter();
        var rt = new JoePro.Runtime.Interpreter(new JoePro.Runtime.TextWriterOutput(text), output);
        JoePro.Reports.ReportEngine.Attach(rt);
        try
        {
            rt.ExecuteCommand("CREATE CURSOR items (name C(10))\nINSERT INTO items VALUES ('Bolt')\nREPORT FORM items");
            Assert.Contains("Bolt", text.ToString());
        }
        finally { rt.Session.Dispose(); }
    }

    [Fact]
    public void Projects_convert_with_on_disk_spelling_and_build_after_import()
    {
        var legacy = Path.Combine(_dir, "legacy4");
        Directory.CreateDirectory(Path.Combine(legacy, "Lib"));
        Directory.CreateDirectory(Path.Combine(legacy, "Style"));
        File.WriteAllText(Path.Combine(legacy, "main.prg"), "#INCLUDE foxpro.h\nPUBLIC gnOut\ngnOut = MB_YESNO + Helper()\n");
        File.WriteAllText(Path.Combine(legacy, "Lib", "Helper.prg"), "FUNCTION Helper\nRETURN 10\n");
        File.WriteAllText(Path.Combine(legacy, "Style", "app.css"), "body { color: navy; }\n");
        FieldDef[] fields = [new("NAME", 'M'), new("TYPE", 'C', 1), new("EXCLUDE", 'L'), new("MAINPROG", 'L'), new("OUTFILE", 'M'), new("COMMENTS", 'M')];
        (bool, Value[]) Row(string name, string type, bool main = false) =>
            (false, [Value.String(name + "\0"), Value.String(type), Value.Logical(false), Value.Logical(main), Value.String(""), Value.String("")]);
        // A PJX stores lower-case names, whatever the folders are called on disk.
        DbfWriter.Write(Path.Combine(legacy, "demo.pjx"), fields,
        [
            Row("c:\\apps\\demo\\demo.pjx", "H"),
            Row("main.prg", "P", main: true),
            Row("lib\\helper.prg", "P"),
            Row("style\\app.css", "T"),
            Row("..\\readme.txt", "T"),
        ]);
        var report = new MigrationReport();
        var output = Path.Combine(_dir, "out4");
        new LegacyImporter(report).ImportFolder(legacy, output);
        var project = JoePro.Documents.Projects.ProjectDocument.Load(Path.Combine(output, "demo.jpproj"));
        Assert.Equal(["../readme.txt", "Lib/Helper.prg", "Style/app.css", "main.prg"], project.Files.Select(f => f.Path).Order(StringComparer.Ordinal));
        Assert.True(File.Exists(Path.Combine(output, "Style", "app.css"))); // every file the application ships is copied
        Assert.Contains(report.Findings, f => f.Rule == "PROJECT.CONVERTED");
        Assert.Contains(report.Findings, f => f.Rule == "PROJECT.FILE.MISSING" && f.Source.Object == "../readme.txt");
        Assert.DoesNotContain(report.Findings, f => f.Rule == "CODE.COMPILE");

        var rt = new Interpreter(new TextWriterOutput(new StringWriter()), output);
        try
        {
            rt.ExecuteCommand("BUILD APP dist/demo FROM demo");
            Assert.True(rt.LastBuild!.Succeeded, string.Join("; ", rt.LastBuild.Errors));
            Assert.Equal("../readme.txt", Assert.Single(rt.LastBuild.Warnings).File);   // a missing text file does not stop the build
            Assert.Contains("Warning: ../readme.txt", File.ReadAllText(Path.Combine(output, "dist", "demo.err")));
            rt.ExecuteCommand("DO dist/demo.jpapp");
            Assert.Equal(14, rt.Evaluate("gnOut").AsNumber);
        }
        finally { rt.Session.Dispose(); }
    }

    [Fact]
    public void Folder_scan_progress_and_cancellation()
    {
        var legacy = Path.Combine(_dir, "legacy5");
        Directory.CreateDirectory(Path.Combine(legacy, ".git"));
        File.WriteAllText(Path.Combine(legacy, ".git", "config"), "");
        File.WriteAllText(Path.Combine(legacy, "a.prg"), "x = 1\n");
        File.WriteAllText(Path.Combine(legacy, "b.prg"), "x = 2\n");
        File.WriteAllText(Path.Combine(legacy, "b.fxp"), "compiled");
        File.WriteAllText(Path.Combine(legacy, "logo.png"), "png");
        var scan = MigrationScan.Scan(legacy);
        Assert.Equal((3, 2, 1), (scan.Files - 1, scan.Count("Programs"), scan.Count("Other files")));   // .git is skipped; .fxp is a companion
        Assert.Empty(scan.Projects);

        var steps = new List<MigrationProgress>();
        var importer = new LegacyImporter(new MigrationReport());
        importer.Progress += steps.Add;
        importer.ImportFolder(legacy, Path.Combine(_dir, "out5"));
        Assert.Equal(["Analyzing", "Analyzing", "Done"], steps.Select(p => p.Step));
        Assert.Equal([(0, 2), (1, 2), (2, 2)], steps.Select(p => (p.Done, p.Total)));
        Assert.False(Directory.Exists(Path.Combine(_dir, "out5", ".git")));
        Assert.False(File.Exists(Path.Combine(_dir, "out5", "b.fxp")));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = new LegacyImporter(new MigrationReport()) { Cancellation = cts.Token };
        Assert.Throws<OperationCanceledException>(() => cancelled.ImportFolder(legacy, Path.Combine(_dir, "out6")));
    }
}
