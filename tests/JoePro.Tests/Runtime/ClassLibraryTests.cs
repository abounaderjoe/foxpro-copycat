using JoePro.Documents;

namespace JoePro.Tests.Runtime;

/// <summary>CREATE CLASSLIB, CREATE CLASS, ADD/RENAME/REMOVE CLASS and AVCXCLASSES on .jpclass libraries.</summary>
public class ClassLibraryTests : RuntimeHarness
{
    [Fact]
    public void Create_class_writes_the_library_and_the_class_is_usable_at_once()
    {
        var o = Run("""
            CREATE CLASSLIB mylib
            CREATE CLASS cusBase OF mylib AS Custom
            CREATE CLASS cusChild OF mylib AS cusBase
            SET CLASSLIB TO mylib
            o = CREATEOBJECT("cusChild")
            ? o.ParentClass, o.BaseClass, o.ClassLibrary == ""
            """);
        Assert.Equal("cusBase Custom .F.", o.Trim().Replace("cusbase", "cusBase", StringComparison.OrdinalIgnoreCase));
        var lib = ClassLibrary.Load(Path.Combine(Dir, "mylib.jpclass"));
        Assert.Equal(["cusBase", "cusChild"], lib.Classes.Select(c => c.Name).Order().ToArray());
        Assert.Equal("cusBase", lib.Find("cusChild")!.ParentClass);
        Assert.Null(lib.Find("cusChild")!.ParentLibrary);
    }

    [Fact]
    public void Create_class_from_another_library_references_it()
    {
        Run("""
            CREATE CLASS cntBase OF base AS Container
            CREATE CLASS cntAddress OF ui AS cntBase FROM base
            """);
        var ui = ClassLibrary.Load(Path.Combine(Dir, "ui.jpclass"));
        Assert.Equal("base.jpclass", ui.Find("cntAddress")!.ParentLibrary);
        var o = Run("""
            o = NEWOBJECT("cntAddress", "ui")
            ? o.BaseClass
            """);
        Assert.Equal("Container", o.Trim());
    }

    [Fact]
    public void Rename_add_and_remove_class()
    {
        Run("""
            CREATE CLASS cusA OF lib1 AS Custom
            CREATE CLASS cusB OF lib1 AS cusA
            RENAME CLASS cusA OF lib1 TO cusRenamed
            ADD CLASS cusB OF lib1 TO lib2
            """);
        var lib1 = ClassLibrary.Load(Path.Combine(Dir, "lib1.jpclass"));
        Assert.NotNull(lib1.Find("cusRenamed"));
        Assert.Equal("cusRenamed", lib1.Find("cusB")!.ParentClass); // references inside the library follow the rename
        var lib2 = ClassLibrary.Load(Path.Combine(Dir, "lib2.jpclass"));
        Assert.Equal("lib1.jpclass", lib2.Find("cusB")!.ParentLibrary);
        var o = Run("""
            o = NEWOBJECT("cusB", "lib2")
            ? o.ParentClass
            REMOVE CLASS cusB OF lib1
            """);
        Assert.Equal("cusrenamed", o.Trim().ToLowerInvariant());
        Assert.Null(ClassLibrary.Load(Path.Combine(Dir, "lib1.jpclass")).Find("cusB"));
    }

    [Fact]
    public void Creating_a_class_that_exists_is_an_error_and_unknown_parents_are_rejected()
    {
        var o = Run("""
            CREATE CLASS cusA OF lib1 AS Custom
            TRY
              CREATE CLASS cusA OF lib1 AS Custom
            CATCH TO oErr
              ? "dup"
            ENDTRY
            TRY
              CREATE CLASS cusX OF lib1 AS NoSuchClass
            CATCH TO oErr
              ? oErr.ErrorNo
            ENDTRY
            """);
        Assert.Equal("dup\n      1733", o.Trim());
    }

    [Fact]
    public void Avcxclasses_lists_the_classes_with_their_base_class_and_description()
    {
        Run("CREATE CLASS cmdOk OF buttons AS CommandButton");
        var path = Path.Combine(Dir, "buttons.jpclass");
        var lib = ClassLibrary.Load(path);
        lib.Find("cmdOk")!.Description = "An OK button";
        ClassLibrary.NewClass(lib, "cmdDefaultOk", "cmdOk");
        ClassFileWriter.Save(lib, path);
        var o = Run("""
            n = AVCXCLASSES(aInfo, "buttons")
            ? n, aInfo[1,1], aInfo[1,4], aInfo[2,1], aInfo[2,2], aInfo[2,4], aInfo[2,8]
            """);
        Assert.Equal("2 cmddefaultok commandbutton cmdok commandbutton commandbutton An OK button", System.Text.RegularExpressions.Regex.Replace(o.Trim(), @"\s+", " "));
    }

    [Fact]
    public void Legacy_libraries_are_read_only()
    {
        File.Copy(TestPaths.Corpus("foxunit", "cprogres.vcx"), Path.Combine(Dir, "cprogres.vcx"));
        File.Copy(TestPaths.Corpus("foxunit", "cprogres.VCT"), Path.Combine(Dir, "cprogres.VCT"));
        var o = Run("""
            TRY
              CREATE CLASS cusNew OF cprogres.vcx AS Custom
            CATCH TO oErr
              ? oErr.ErrorNo
            ENDTRY
            n = AVCXCLASSES(aInfo, "cprogres.vcx")
            ? n > 0
            """);
        Assert.Equal("1102\n.T.", o.Trim().Replace("      ", ""));
    }
}
