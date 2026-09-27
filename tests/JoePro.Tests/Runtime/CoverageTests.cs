using JoePro.Runtime.Builtins;

namespace JoePro.Tests.Runtime;

/// <summary>The VFP 9 function coverage matrix, and the functions added by the coverage sweep.</summary>
public class CoverageTests : RuntimeHarness
{
    [Fact]
    public void At_least_95_percent_of_VFP9_functions_are_supported_or_listed_as_unsupported()
    {
        var implemented = Library.Names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = VfpCatalog.Functions.Where(f => !implemented.Contains(f) && !VfpCatalog.Unsupported.ContainsKey(f)).ToList();
        var covered = VfpCatalog.Functions.Length - missing.Count;
        Assert.True(covered >= VfpCatalog.Functions.Length * 0.95, "Missing: " + string.Join(", ", missing));
        // Nothing is both implemented and listed as unsupported.
        Assert.DoesNotContain(VfpCatalog.Unsupported.Keys, implemented.Contains);
        Assert.All(VfpCatalog.Unsupported.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }

    [Fact]
    public void Line_search_and_character_variants()
    {
        var o = Run("""
            t = "alpha" + CHR(13) + CHR(10) + "Beta gamma" + CHR(13) + "beta"
            ? ATLINE("beta", t), ATCLINE("BETA", t), RATLINE("et", t), ATLINE("zzz", t)
            ? LEFTC("abcdef", 2), RIGHTC("abcdef", 2), STUFFC("abcdef", 2, 3, "X"), CHRTRANC("abc", "b", "B")
            ? NORMALIZE("customer->name + upper('abc')")
            """);
        var lines = o.Trim().Split('\n').Select(l => l.Trim()).ToArray();
        Assert.Equal("3 2 3 0", string.Join(" ", lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        Assert.Equal("ab ef aXef aBc", lines[1]);
        Assert.Equal("CUSTOMER.NAME + UPPER('abc')", lines[2]);
    }

    [Fact]
    public void Index_filter_and_relation_information()
    {
        var o = Run("""
            CREATE CURSOR parent (id I, name C(10))
            INDEX ON id TAG id CANDIDATE
            INDEX ON UPPER(name) TAG name UNIQUE DESCENDING
            INSERT INTO parent VALUES (1, "one")
            INSERT INTO parent VALUES (2, "two")
            CREATE CURSOR child (pid I, x C(5))
            INDEX ON pid TAG pid
            SELECT parent
            SET RELATION TO id INTO child
            SET FILTER TO id > 0
            ? FILTER(), RELATION(1), TARGET(1), RELATION(2) == ""
            n = ATAGINFO(ta)
            ? n, ta[1,1], ta[1,2], ta[2,2], ta[2,3], ta[2,5]
            SET ORDER TO id
            GO 1
            ? KEYMATCH(2), KEYMATCH(9), RECNO(), UNIQUE(2), UNIQUE(1), IDXCOLLATE(1)
            """);
        var lines = o.Trim().Split('\n').Select(l => l.Trim()).ToArray();
        Assert.Equal("id>0 id CHILD .T.", lines[0].Replace(" > ", ">").ToLowerInvariant().Replace("child", "CHILD").Replace(".t.", ".T."));
        Assert.Equal("2 ID CANDIDATE UNIQUE UPPER(name) DESCENDING", string.Join(" ", lines[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        Assert.Equal(".T. .F. 1 .T. .F. MACHINE", string.Join(" ", lines[2].Split(' ', StringSplitOptions.RemoveEmptyEntries)));
    }

    [Fact]
    public void Stack_procedure_and_session_information()
    {
        File.WriteAllText(Path.Combine(Dir, "procs.prg"), """
            #DEFINE LIMIT 10
            PROCEDURE first
            ENDPROC
            FUNCTION second
            ENDFUNC
            DEFINE CLASS foo AS Custom
                PROCEDURE bar
                ENDPROC
            ENDDEFINE
            """);
        File.WriteAllText(Path.Combine(Dir, "stack.prg"), """
            LOCAL n
            n = inner()
            RETURN n
            FUNCTION inner
            RETURN ASTACKINFO(aStk)
            """);
        var o = Run("""
            n = APROCINFO(ap, "procs.prg")
            ? n, ap[1,1], ap[1,3], ap[2,1], ap[2,2], ap[3,3], ap[4,1], ap[5,1], ap[5,3]
            ? APROCINFO(ap2, "procs.prg", 1), ap2[2,1]
            ? ASESSIONS(aSes) >= 1, aSes[1] > 0
            k = stack()
            ? k >= 2
            """);
        var lines = o.Trim().Split('\n').Select(l => string.Join(" ", l.Split(' ', StringSplitOptions.RemoveEmptyEntries))).ToArray();
        Assert.Equal("5 LIMIT Define first 2 Procedure foo foo.bar Method", lines[0]);
        Assert.Equal("2 second", lines[1]);
        Assert.Equal(".T. .T.", lines[2]);
        Assert.Equal(".T.", lines[3]);
    }

    [Fact]
    public void File_dates_locfile_and_dialog_fallbacks()
    {
        Directory.CreateDirectory(Path.Combine(Dir, "sub"));
        File.WriteAllText(Path.Combine(Dir, "sub", "found.txt"), "x");
        var o = Run("""
            ? FDATE("sub/found.txt") = DATE(), LEN(FTIME("sub/found.txt")), VARTYPE(FDATE("sub/found.txt", 1))
            SET PATH TO sub
            ? "FOUND.TXT" $ LOCFILE("found.txt")
            ? GETFILE() == "", PUTFILE() == "", GETDIR() == "", GETCOLOR(), FERROR()
            ? FOPEN("nope.txt"), FERROR()
            ? RIGHT(HOME(), 1) $ "\/", FKMAX(), FKLABEL(2), SYSMETRIC(1) > 0
            """);
        var lines = o.Trim().Split('\n').Select(l => string.Join(" ", l.Split(' ', StringSplitOptions.RemoveEmptyEntries))).ToArray();
        Assert.Equal(".T. 8 T", lines[0]);
        Assert.Equal(".T.", lines[1]);
        Assert.Equal(".T. .T. .T. -1 0", lines[2]);
        Assert.Equal("-1 2", lines[3]);
        Assert.Equal(".T. 12 F2 .T.", lines[4]);
        Assert.Throws<JoePro.Core.VfpException>(() => Run("? LOCFILE('missing.txt')"));
    }

    [Fact]
    public void Dialog_hooks_are_used_when_the_host_supplies_them()
    {
        Rt.FileDialog = (kind, _, _, _) => kind == "folder" ? "/tmp/pick" : "/tmp/" + kind + ".txt";
        Rt.ColorDialog = _ => 255;
        var o = Run("? GETFILE(), PUTFILE(), GETCOLOR()");
        Assert.Equal("/tmp/open.txt /tmp/save.txt 255", string.Join(" ", o.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)));
    }

    [Fact]
    public void Cursor_to_xml_and_back_keeps_types()
    {
        var o = Run("""
            CREATE CURSOR cust (id I, name C(12), bal N(8,2), ok L, since D)
            INSERT INTO cust VALUES (1, "Alfreds", 12.5, .T., {^2024-02-03})
            INSERT INTO cust VALUES (2, "Berglunds", -3, .F., {^2023-12-31})
            ? CURSORTOXML("cust", "lcXml", 1, 0, 0, "1")
            ? "<name>Alfreds</name>" $ lcXml, "xsd:schema" $ lcXml
            USE IN cust
            ? XMLTOCURSOR(lcXml, "back")
            ? ALIAS(), TYPE("back.bal"), TYPE("back.since"), TYPE("back.ok"), FSIZE("name")
            GO 2
            ? ALLTRIM(name), bal, since = {^2023-12-31}, ok
            = CURSORTOXML("back", "plain", 2)
            ? XMLTOCURSOR(plain, "inferred"), TYPE("inferred.bal"), TYPE("inferred.since"), TYPE("inferred.name")
            """);
        var lines = o.Trim().Split('\n').Select(l => string.Join(" ", l.Split(' ', StringSplitOptions.RemoveEmptyEntries))).ToArray();
        Assert.Equal("2", lines[0]);
        Assert.Equal(".T. .T.", lines[1]);
        Assert.Equal("2", lines[2]);
        Assert.Equal("BACK N D L 12", lines[3]);
        Assert.Equal("Berglunds -3.00 .T. .F.", lines[4]);
        Assert.Equal("2 N D C", lines[5]);
    }

    [Fact]
    public void Field_state_can_be_set_under_buffering()
    {
        var o = Run("""
            SET MULTILOCKS ON
            CREATE CURSOR t (a I, b C(3))
            INSERT INTO t VALUES (1, "x")
            = CURSORSETPROP("Buffering", 5)
            GO 1
            REPLACE a WITH 2
            ? GETFLDSTATE("a"), GETFLDSTATE("b")
            = SETFLDSTATE("a", 1)
            = SETFLDSTATE("b", 2)
            ? GETFLDSTATE("a"), GETFLDSTATE("b")
            """);
        var lines = o.Trim().Split('\n').Select(l => string.Join(" ", l.Split(' ', StringSplitOptions.RemoveEmptyEntries))).ToArray();
        Assert.Equal("2 1", lines[0]);
        Assert.Equal("1 2", lines[1]);
    }

    [Fact]
    public void Objtoclient_adds_container_offsets()
    {
        var o = Run("""
            f = CREATEOBJECT("Form")
            f.AddObject("cnt", "Container")
            f.cnt.Top = 10
            f.cnt.Left = 20
            f.cnt.AddObject("txt", "TextBox")
            f.cnt.txt.Top = 5
            f.cnt.txt.Left = 7
            f.cnt.txt.Width = 90
            ? OBJTOCLIENT(f.cnt.txt, 1), OBJTOCLIENT(f.cnt.txt, 2), OBJTOCLIENT(f.cnt.txt, 3)
            """);
        Assert.Equal("15 27 90", string.Join(" ", o.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)));
    }

    [Fact]
    public void Sys_codes_for_indexes_settings_paths_and_checksums()
    {
        File.WriteAllText(Path.Combine(Dir, "a1.txt"), "x");
        File.WriteAllText(Path.Combine(Dir, "a2.txt"), "x");
        var o = Run("""
            CREATE CURSOR t (id I, name C(5))
            INDEX ON id TAG id
            INDEX ON name TAG name FOR id > 0
            SET ORDER TO name
            ? SYS(14, 1), SYS(21), SYS(22), SYS(2021, 2), SYS(2001, "EXACT"), SYS(103)
            ? SYS(2000, "a*.txt"), SYS(2000, "a*.txt", 1), SYS(2000, "a*.txt", 1) == ""
            ? SYS(2007, "123456789", 0, 1), SYS(2014, "sub" + CHR(47) + "f.txt")
            f = CREATEOBJECT("Form")
            f.AddObject("cnt", "Container")
            ? SYS(1272, f.cnt), SYS(2011)
            """);
        var lines = o.Trim().Split('\n').Select(l => string.Join(" ", l.Split(' ', StringSplitOptions.RemoveEmptyEntries))).ToArray();
        Assert.Equal("id 2 NAME id > 0 OFF OFF", lines[0].Replace("ID>0", "id > 0").Replace("id>0", "id > 0"));
        Assert.Equal("A1.TXT A2.TXT .T.", lines[1]);
        Assert.Equal("3421780262 SUB" + Path.DirectorySeparatorChar + "F.TXT", lines[2]);
        Assert.Equal("form.cnt Exclusive", lines[3]);
    }

    [Fact]
    public void Set_commands_are_all_classified_and_values_read_back()
    {
        Assert.All(VfpCatalog.SetCommands, kv => Assert.True(kv.Value.Status is VfpCatalog.SetStatus.Supported or VfpCatalog.SetStatus.Missing || !string.IsNullOrWhiteSpace(kv.Value.Reason), kv.Key));
        var o = Run("""
            ? SET("MULTILOCKS"), SET("REPROCESS"), SET("UDFPARMS")
            SET MULTILOCKS ON
            SET REPROCESS TO 5
            SET CURRENCY TO "EUR"
            ? SET("MULTILOCKS"), SET("REPROCESS"), SET("CURRENCY")
            SET NULLDISPLAY TO "(none)"
            ? .NULL.
            SET NULLDISPLAY TO
            SET MEMOWIDTH TO 10
            t = "one two three four five"
            ? MEMLINES(t), MLINE(t, 2), SET("MEMOWIDTH")
            SET FDOW TO 2
            ? DOW({^2024-01-01}, 0), DOW({^2024-01-01})
            SET SPACE OFF
            ? "a", "b"
            SET SPACE ON
            SET FIXED ON
            SET DECIMALS TO 3
            ? 1.5
            SET FIXED OFF
            """);
        var lines = o.Trim().Split('\n').Select(l => string.Join(" ", l.Split(' ', StringSplitOptions.RemoveEmptyEntries))).ToArray();
        Assert.Equal("OFF 0 VALUE", lines[0]);
        Assert.Equal("ON 5 LEFT", lines[1]);
        Assert.Equal("(none)", lines[2]);
        Assert.Equal("3 three four 10", lines[3]);
        Assert.Equal("1 2", lines[4]);
        Assert.Equal("ab", lines[5]);
        Assert.Equal("1.500", lines[6]);
    }

    [Fact]
    public void Set_alternate_console_datasession_and_assert()
    {
        var o = Run("""
            SET ALTERNATE TO alt.txt
            SET ALTERNATE ON
            ? "to both"
            SET CONSOLE OFF
            ? "file only"
            SET CONSOLE ON
            SET ALTERNATE OFF
            ? "screen only"
            SET ALTERNATE TO
            ? SET("ALTERNATE")
            SET ASSERTS ON
            ASSERT 1 = 2 MESSAGE "custom failure"
            SET ASSERTS OFF
            ASSERT .F.
            """);
        Assert.Contains("to both", o);
        Assert.DoesNotContain("file only", o);
        Assert.Contains("screen only", o);
        var alt = File.ReadAllText(Path.Combine(Dir, "alt.txt"));
        Assert.Contains("to both", alt);
        Assert.Contains("file only", alt);
        Assert.DoesNotContain("screen only", alt);
        Assert.Single(StatusMessages, m => m.Contains("custom failure"));
        Assert.Throws<JoePro.Core.VfpException>(() => Run("SET DATASESSION TO 999"));
    }

    [Fact]
    public void Set_udfparms_currency_and_reprocess()
    {
        File.WriteAllText(Path.Combine(Dir, "bump.prg"), "LPARAMETERS n\nn = n + 1\nRETURN n");
        var o = Run("""
            x = 1
            = bump(x)
            ? x
            SET UDFPARMS TO REFERENCE
            = bump(x)
            ? x
            SET UDFPARMS TO VALUE
            ? TRANSFORM(12.5, "@$ 99.99")
            SET CURRENCY TO "EUR"
            SET CURRENCY RIGHT
            ? TRANSFORM(12.5, "@$ 99.99"), SET("CURRENCY"), SET("CURRENCY", 1)
            SET REPROCESS TO 3
            CREATE CURSOR lk (a I)
            APPEND BLANK
            ? RLOCK(), SET("REPROCESS")
            """);
        var lines = o.Trim().Split('\n').Select(l => string.Join(" ", l.Split(' ', StringSplitOptions.RemoveEmptyEntries))).ToArray();
        Assert.Equal("1", lines[0]);
        Assert.Equal("2", lines[1]);
        Assert.Equal("$12.50", lines[2]);
        Assert.Equal("12.50EUR RIGHT EUR", lines[3]);
        Assert.Equal(".T. 3", lines[4]);
    }

    [Fact]
    public void Set_key_limits_records_to_a_key_range_of_the_controlling_index()
    {
        var o = Run("""
            CREATE CURSOR k (id I, name C(10))
            FOR i = 1 TO 9
                INSERT INTO k VALUES (i, IIF(i < 4, "Smith", IIF(i < 7, "Jones", "Adams")))
            ENDFOR
            INDEX ON name TAG name
            INDEX ON id TAG id
            SET ORDER TO name
            SET KEY TO "Jones"
            COUNT TO a
            SET KEY TO RANGE "J", "Sm"
            COUNT TO b
            SET ORDER TO id
            SET KEY TO RANGE 3, 5
            COUNT TO c
            SET KEY TO RANGE 8,
            COUNT TO d
            SET ORDER TO name
            COUNT TO e
            SET KEY TO
            SET ORDER TO id
            COUNT TO f
            ? a, b, c, d, e, f
            """);
        Assert.Equal("3 6 3 2 9 9", string.Join(" ", o.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)));
    }

    [Fact]
    public void Set_strictdate_rejects_ambiguous_date_constants()
    {
        Assert.Equal("0 .T.", string.Join(" ", Run("? SET(\"STRICTDATE\"), {01/02/2024} = {^2024-01-02}").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        Run("SET STRICTDATE TO 1");
        Assert.Equal(".T. .T.", string.Join(" ", Run("? {^2024-01-02} > {}, {} = {}").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        var ex = Assert.Throws<JoePro.Core.VfpException>(() => Run("? {01/02/2024}"));
        Assert.Equal(2032, ex.Number);
    }

    [Fact]
    public void Set_procedure_and_classlib_restore_from_a_saved_list()
    {
        File.WriteAllText(Path.Combine(Dir, "p1.prg"), "FUNCTION f1\nRETURN 1");
        File.WriteAllText(Path.Combine(Dir, "p2.prg"), "FUNCTION f2\nRETURN 2");
        var o = Run("""
            SET PROCEDURE TO p1, p2
            lcSaved = SET("PROCEDURE")
            SET PROCEDURE TO
            SET PROCEDURE TO (lcSaved)
            ? f1() + f2()
            """);
        Assert.Equal("3", o.Trim());
    }

    [Fact]
    public void Table_information_functions()
    {
        var o = Run("""
            CREATE TABLE info FREE (id I AUTOINC, name C(10))
            INSERT INTO info (name) VALUES ("a")
            INSERT INTO info (name) VALUES ("b")
            ? GETAUTOINCVALUE(), HEADER(), LUPDATE() = DATE(), ISTRANSACTABLE(), ISMEMOFETCHED("name")
            ? ALANGUAGE(af, 2) > 300, ASCAN(af, "ATAGINFO") > 0, ALANGUAGE(ac, 1) > 200, ALANGUAGE(ab, 3) > 20
            """);
        var lines = o.Trim().Split('\n').Select(l => string.Join(" ", l.Split(' ', StringSplitOptions.RemoveEmptyEntries))).ToArray();
        Assert.Equal("2 360 .T. .T. .T.", lines[0]);
        Assert.Equal(".T. .T. .T. .T.", lines[1]);
    }
}
