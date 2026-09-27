using JoePro.Core;
using JoePro.Documents.Menus;
using JoePro.Legacy.Formats;

namespace JoePro.Tests.Documents;

public class MenuDocumentTests
{
    public static MenuDocument Sample()
    {
        var doc = new MenuDocument { Setup = "PUBLIC gcLog\ngcLog = \"\"", Cleanup = "gcLog = gcLog + \"ready;\"" };
        var file = new MenuNode { Prompt = "\\<File", KeyName = "ALT+F" };
        file.Items.Add(new MenuNode { Prompt = "\\<New", KeyName = "CTRL+N", KeyText = "Ctrl+N", Command = "gcLog = gcLog + \"new;\"" });
        file.Items.Add(new MenuNode { Prompt = "\\-" });
        var recent = new MenuNode { Prompt = "\\<Recent", Name = "recent" };
        recent.Items.Add(new MenuNode { Prompt = "One", Command = "gcLog = gcLog + \"one;\"" });
        file.Items.Add(recent);
        file.Items.Add(new MenuNode { Prompt = "E\\<xit", SkipFor = "glBusy", Procedure = "gcLog = gcLog + \"exit;\"\ngcLog = gcLog + PROMPT() + \";\"" });
        doc.Items.Add(file);
        var edit = new MenuNode { Prompt = "\\<Edit" };
        edit.Items.Add(new MenuNode { SystemBar = "_MED_COPY" });
        edit.Items.Add(new MenuNode { Prompt = "Paste \\<Special", SystemBar = "_MED_PSPEC" });
        doc.Items.Add(edit);
        doc.Items.Add(new MenuNode { Prompt = "\\<Help", Command = "gcLog = gcLog + \"help;\"", Message = "Get help" });
        return doc;
    }

    [Fact]
    public void Round_trips_canonically()
    {
        var text = MenuSerializer.Write(Sample());
        Assert.StartsWith("# Joe Pro menu v1\n", text);
        Assert.Contains("      - bar: _med_copy\n", text);
        Assert.Contains("        procedure: |\n", text);
        Assert.Equal(text, MenuSerializer.Write(MenuSerializer.Parse(text)));
        var ex = Assert.Throws<FormatException>(() => MenuSerializer.Parse("items:\n  - prompt: x\n    command: a\n    procedure: b\n"));
        Assert.Contains("one action", ex.Message);
    }

    [Fact]
    public void Generates_mpr_code()
    {
        var code = MenuGenerator.Generate(Sample(), "main.jpmenu");
        Assert.Contains("SET SYSMENU TO\n", code);
        Assert.Contains("DEFINE PAD _file OF _MSYSMENU PROMPT \"\\<File\" KEY ALT+F, \"\"\n", code);
        Assert.Contains("ON PAD _file OF _MSYSMENU ACTIVATE POPUP file\n", code);
        Assert.Contains("DEFINE BAR 1 OF file PROMPT \"\\<New\" KEY CTRL+N, \"Ctrl+N\"\n", code);
        Assert.Contains("ON BAR 3 OF file ACTIVATE POPUP recent\n", code);
        Assert.Contains("DEFINE BAR 4 OF file PROMPT \"E\\<xit\" SKIP FOR glBusy\n", code);
        Assert.Contains("ON SELECTION BAR 4 OF file DO _main0001\n", code);
        Assert.Contains("DEFINE BAR _MED_COPY OF edit\n", code);
        Assert.Contains("ON SELECTION PAD _help OF _MSYSMENU gcLog = gcLog + \"help;\"\n", code);
        Assert.Contains("PROCEDURE _main0001\n", code);
        JoePro.Language.Parser.ParseProgram(code, "MAIN"); // the generated code compiles
    }

    private static readonly FieldDef[] MnxFields =
    [
        new("OBJTYPE", 'N', 2), new("OBJCODE", 'N', 2), new("NAME", 'M'), new("PROMPT", 'M'), new("COMMAND", 'M'), new("MESSAGE", 'M'),
        new("PROCEDURE", 'M'), new("SETUP", 'M'), new("CLEANUP", 'M'), new("MARK", 'C', 1), new("KEYNAME", 'M'), new("KEYLABEL", 'M'),
        new("SKIPFOR", 'M'), new("NUMITEMS", 'N', 2), new("LEVELNAME", 'C', 10), new("ITEMNUM", 'C', 3), new("COMMENT", 'M'), new("LOCATION", 'N', 2),
    ];

    public static (bool, Value[]) MnxRow(int type, int code, string name = "", string prompt = "", string command = "", string procedure = "",
        int items = 0, string level = "", string key = "", string keyLabel = "", string skip = "", string setup = "", string cleanup = "")
    {
        var v = MnxFields.Select(f => f.Type switch { 'N' => Value.Number(0), _ => Value.String("") }).ToArray();
        void Set(string c, Value x) => v[Array.FindIndex(MnxFields, f => f.Name == c)] = x;
        Set("OBJTYPE", Value.Number(type)); Set("OBJCODE", Value.Number(code)); Set("NAME", Value.String(name)); Set("PROMPT", Value.String(prompt));
        Set("COMMAND", Value.String(command)); Set("PROCEDURE", Value.String(procedure.Replace("\n", "\r\n"))); Set("NUMITEMS", Value.Number(items));
        Set("LEVELNAME", Value.String(level)); Set("KEYNAME", Value.String(key)); Set("KEYLABEL", Value.String(keyLabel)); Set("SKIPFOR", Value.String(skip));
        Set("SETUP", Value.String(setup)); Set("CLEANUP", Value.String(cleanup));
        return (false, v);
    }

    public static void WriteSampleMnx(string path) => DbfWriter.Write(path, MnxFields,
    [
        MnxRow(1, 22, setup: "PUBLIC gcLog\r\ngcLog = \"\""),
        MnxRow(2, 77, "_MSYSMENU", items: 2, level: "_MSYSMENU"),
        MnxRow(3, 77, "_file", "\\<File", items: 0, level: "_MSYSMENU", key: "ALT+F"),
        MnxRow(3, 67, "_quit", "\\<Quit", "gcLog = gcLog + \"quit;\"", level: "_MSYSMENU"),
        MnxRow(2, 77, "file", items: 3, level: "file"),
        MnxRow(3, 80, "", "\\<Open", procedure: "gcLog = gcLog + \"open;\"", level: "file", key: "CTRL+O", keyLabel: "Ctrl+O"),
        MnxRow(3, 78, "_MED_COPY", "\\<Copy", level: "file"),
        MnxRow(3, 77, "", "\\<More", level: "file"),
        MnxRow(2, 77, "more", items: 1, level: "more"),
        MnxRow(3, 67, "", "Deep", "gcLog = gcLog + \"deep;\"", level: "more", skip: ".F."),
    ]);

    [Fact]
    public void Converts_mnx_levels_depth_first()
    {
        var dir = Path.Combine(Path.GetTempPath(), "joepro-mnx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "main.mnx");
        WriteSampleMnx(path);
        var c = LegacyMenuConverter.Convert(path);
        Assert.Empty(c.Findings);
        var doc = c.Document;
        Assert.Equal(["File", "Quit"], doc.Items.Select(i => i.Caption));
        Assert.Equal("_file", doc.Items[0].Name);
        var file = doc.Items[0];
        Assert.Equal(["Open", "Copy", "More"], file.Items.Select(i => i.Caption));
        Assert.Equal(("CTRL+O", "gcLog = gcLog + \"open;\""), (file.Items[0].KeyName, file.Items[0].Procedure));
        Assert.Equal("_MED_COPY", file.Items[1].SystemBar);
        Assert.Equal("Deep", file.Items[2].Items.Single().Caption);
        Assert.Equal("more", file.Items[2].Name);
        Assert.Contains("PUBLIC gcLog", doc.Setup);
        Directory.Delete(dir, true);
    }
}

public class MenuDesignSessionTests
{
    [Fact]
    public void Insert_move_indent_outdent_and_undo()
    {
        var s = new MenuDesignSession(new MenuDocument());
        s.Insert([], 0, new MenuNode { Prompt = "File" });
        s.Insert([], 1, new MenuNode { Prompt = "Open", Command = "x" });
        s.Insert([], 2, new MenuNode { Prompt = "Close" });
        var open = s.Indent([1]);
        Assert.Equal([0, 0], open);
        Assert.Null(s.Document.Items[0].Command);
        var close = s.Indent([1]);
        Assert.Equal([0, 1], close);
        Assert.Equal(["Open", "Close"], s.Document.Items[0].Items.Select(i => i.Prompt));
        Assert.Equal([0, 0], s.Move([0, 1], -1));
        Assert.Equal("Close", s.Document.Items[0].Items[0].Prompt);
        Assert.Equal([1], s.Outdent([0, 0]));
        Assert.Equal(["File", "Close"], s.Document.Items.Select(i => i.Prompt));
        s.Undo(); s.Undo();
        Assert.Equal(["Open", "Close"], s.Document.Items[0].Items.Select(i => i.Prompt));
        var quick = MenuDesignSession.QuickMenu();
        Assert.Equal(["File", "Edit", "Window", "Help"], quick.Items.Select(i => i.Caption));
        Assert.Contains(quick.Items[1].Items, i => i.SystemBar == "_MED_PASTE");
        JoePro.Language.Parser.ParseProgram(MenuGenerator.Generate(quick, "quick.jpmenu"), "Q");
    }
}
