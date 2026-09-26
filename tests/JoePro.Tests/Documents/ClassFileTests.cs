using JoePro.Documents;

namespace JoePro.Tests.Documents;

public class ClassFileTests
{
    private static ClassFile Sample()
    {
        var file = new ClassFile { Kind = ClassFileKind.Form };
        var cls = new ClassDocument { Name = "frmCustomer", ParentClass = "Form" };
        cls.Properties["Width"] = "640";
        cls.Properties["Caption"] = "\"Customers\"";
        cls.Properties["DataSession"] = "2";
        var txt = new MemberDocument { Path = "txtName", Class = "TextBox" };
        txt.Properties["Top"] = "16";
        txt.Properties["Left"] = "12";
        txt.Properties["ControlSource"] = "\"customer.name\"";
        cls.Members.Add(txt);
        cls.Members.Add(new MemberDocument { Path = "cmdOk", Class = "CommandButton" });
        cls.Methods.Add(new MethodDocument { Name = "txtName.Valid", Code = "RETURN !EMPTY(THIS.Value)" });
        cls.Methods.Add(new MethodDocument { Name = "Init", Code = "THIS.Caption = \"Hello\"\nIF .T.\n    ? 1\nENDIF" });
        file.Classes.Add(cls);
        return file;
    }

    [Fact]
    public void Writes_the_canonical_format()
    {
        Assert.Equal("""
            *-- Joe Pro form v1

            DEFINE CLASS frmCustomer AS Form
                Caption = "Customers"
                DataSession = 2
                Width = 640

                ADD OBJECT txtName AS TextBox WITH ;
                    ControlSource = "customer.name", ;
                    Left = 12, ;
                    Top = 16

                ADD OBJECT cmdOk AS CommandButton

                PROCEDURE Init
                    THIS.Caption = "Hello"
                    IF .T.
                        ? 1
                    ENDIF
                ENDPROC

                PROCEDURE txtName.Valid
                    RETURN !EMPTY(THIS.Value)
                ENDPROC
            ENDDEFINE

            """.Replace("\r\n", "\n"), ClassFileWriter.Write(Sample()));
    }

    [Fact]
    public void Resaving_is_byte_identical_and_one_property_is_one_line()
    {
        var text = ClassFileWriter.Write(Sample());
        var reloaded = ClassFileReader.Parse(text);
        Assert.Equal(text, ClassFileWriter.Write(reloaded));

        reloaded.Classes[0].FindMember("txtName")!.Properties["Left"] = "24";
        var changed = ClassFileWriter.Write(reloaded).Split('\n');
        var original = text.Split('\n');
        Assert.Equal(original.Length, changed.Length);
        var diffs = original.Zip(changed).Where(p => p.First != p.Second).ToList();
        var d = Assert.Single(diffs);
        Assert.Equal("        Left = 24, ;", d.Second);
    }

    [Fact]
    public void Reads_hand_written_and_foxbin2prg_style_files()
    {
        var file = ClassFileReader.Parse("""
            * a comment
            DEFINE CLASS gf_help AS gf_baseform OF "..\lib\gofishui.vcx"
                Caption = "Help", Height = 270   && two on one line
                DIMENSION aItems[3,2]
                PROTECTED nSecret
                ADD OBJECT 'Label1' AS label WITH ;
                    Caption = "A, B", ;
                    Top = 5
                PROC Init
                    TEXT TO lcCode NOSHOW
                    PROCEDURE Generated
                    ENDTEXT
                ENDP
                HIDDEN PROCEDURE Secret(a, b)
                    RETURN a + b
                ENDPROC
            ENDDEFINE
            """);
        var cls = Assert.Single(file.Classes);
        Assert.Equal("..\\lib\\gofishui.vcx", cls.ParentLibrary);
        Assert.Equal("270", cls.Properties["Height"]);
        Assert.Equal(("aItems", "3,2"), Assert.Single(cls.Arrays));
        Assert.Equal("\"A, B\"", cls.FindMember("Label1")!.Properties["Caption"]);
        Assert.Equal(["TEXT TO lcCode NOSHOW", "PROCEDURE Generated", "ENDTEXT"], cls.FindMethod("Init")!.Body);
        var secret = cls.FindMethod("Secret")!;
        Assert.Equal("HIDDEN", secret.Visibility);
        Assert.Equal("a, b", secret.Parameters);
        Assert.Equal(["nSecret"], cls.Protected);
    }

    [Theory]
    [InlineData("cprogres.vcx")]
    [InlineData("sfsplitter.vcx")]
    [InlineData("fxu.vcx")]
    public void Real_class_libraries_convert_like_foxbin2prg_describes_them(string library)
    {
        var path = TestPaths.Corpus("foxunit", library);
        var result = LegacyFormConverter.ConvertClassLibrary(path);
        var reference = File.ReadAllText(Path.ChangeExtension(path, ".vc2"), System.Text.Encoding.Latin1);

        // Every class, with the same parent class (FoxBin2Prg also sorts classes by name).
        var expected = System.Text.RegularExpressions.Regex.Matches(reference, @"(?m)^DEFINE CLASS (\w+) AS (\w+)")
            .Select(m => (m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value.ToLowerInvariant())).ToList();
        Assert.Equal(expected.Order(), result.File.Classes.Select(c => (c.Name.ToLowerInvariant(), c.ParentClass.ToLowerInvariant())).Order());

        // Every method of every class (FoxBin2Prg writes the code of each object after the class header).
        var blocks = System.Text.RegularExpressions.Regex.Split(reference, @"(?m)^DEFINE CLASS ").Skip(1).ToList();
        for (int i = 0; i < blocks.Count; i++)
        {
            var refMethods = System.Text.RegularExpressions.Regex.Matches(blocks[i], @"(?m)^\t(?:PROTECTED |HIDDEN )?PROCEDURE ([\w\.]+)")
                .Select(m => m.Groups[1].Value.ToLowerInvariant()).Order().ToList();
            var className = System.Text.RegularExpressions.Regex.Match(blocks[i], @"^(\w+)").Groups[1].Value;
            var ours = result.File.Find(className)!.Methods.Where(m => m.Body.Count > 0 || refMethods.Contains(m.Name.ToLowerInvariant()))
                .Select(m => m.Name.ToLowerInvariant()).Order().ToList();
            Assert.Equal(refMethods, ours);
        }

        // The output is valid Joe Pro source and re-saves identically.
        var text = ClassFileWriter.Write(result.File);
        JoePro.Language.Parser.ParseProgram(text, "x", null, _ => "");
        Assert.Equal(text, ClassFileWriter.Write(ClassFileReader.Parse(text, ClassFileKind.ClassLibrary)));
    }

    [Fact]
    public void Designer_values_become_expressions()
    {
        var r = new ConversionResult { File = new ClassFile() };
        Assert.Equal("RGB(64,0,128)", LegacyFormConverter.ConvertValue("BackColor", "64,0,128", "x", r));
        Assert.Equal("\"../images/logo.gif\"", LegacyFormConverter.ConvertValue("Picture", @"..\images\logo.gif", "x", r));
        Assert.Equal("\"1.1.000\"", LegacyFormConverter.ConvertValue("version", "1.1.000", "x", r));
        Assert.Equal("DATE()", LegacyFormConverter.ConvertValue("Value", "=DATE()", "x", r));
        Assert.Equal("{}", LegacyFormConverter.ConvertValue("Value", "{}", "x", r));
        Assert.Equal("\"(none)\"", LegacyFormConverter.ConvertValue("Tag", "(none)", "x", r));
        Assert.Equal("\"../data/sales.jpdb\"", LegacyFormConverter.ConvertValue("Database", @"..\data\sales.dbc", "x", r));
    }
}
