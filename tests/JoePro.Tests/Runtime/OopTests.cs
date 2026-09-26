namespace JoePro.Tests.Runtime;

public class OopTests : RuntimeHarness
{
    [Fact]
    public void Bindevent_runs_delegate_before_or_after_the_event()
    {
        var o = Run("""
            PUBLIC gcLog
            gcLog = ""
            oSrc = CREATEOBJECT("Src")
            oH = CREATEOBJECT("Handler")
            BINDEVENT(oSrc, "Click", oH, "Before")
            BINDEVENT(oSrc, "Click", oH, "After", 1)
            oSrc.Click()
            ? gcLog
            ? UNBINDEVENTS(oSrc, "Click", oH, "Before")
            gcLog = ""
            oSrc.Click()
            ? gcLog
            ? UNBINDEVENTS(oSrc)
            gcLog = ""
            oSrc.Click()
            ? gcLog

            DEFINE CLASS Src AS Custom
              PROCEDURE Click
                gcLog = gcLog + "click;"
            ENDDEFINE

            DEFINE CLASS Handler AS Custom
              PROCEDURE Before
                gcLog = gcLog + "before;"
              PROCEDURE After
                gcLog = gcLog + "after;"
            ENDDEFINE
            """);
        Assert.Equal("before;click;after;\n         1\nclick;after;\n         1\nclick;", o);
    }

    [Fact]
    public void Bindevent_flag_2_ignores_plain_method_calls_and_aevents_reports_the_source()
    {
        var o = Run("""
            PUBLIC gcLog
            gcLog = ""
            oSrc = CREATEOBJECT("Custom")
            oSrc.Name = "theSource"
            oH = CREATEOBJECT("Handler")
            BINDEVENT(oSrc, "Refresh", oH, "OnRefresh", 2)
            oSrc.Refresh()
            ? "[" + gcLog + "]"
            BINDEVENT(oSrc, "Refresh", oH, "OnRefresh")
            oSrc.Refresh()
            ? gcLog
            ? AEVENTS(aB, oSrc), aB(1, 2), aB(1, 4)

            DEFINE CLASS Handler AS Custom
              PROCEDURE OnRefresh
                AEVENTS(aSrc, 0)
                gcLog = gcLog + aSrc(1).Name + "." + aSrc(2)
            ENDDEFINE
            """);
        Assert.Equal("[]\ntheSource.Refresh\n         1 REFRESH OnRefresh", o);
    }

    [Fact]
    public void Protected_members_are_visible_only_inside_the_object()
    {
        var o = Run("""
            oAcct = CREATEOBJECT("Account")
            oAcct.Deposit(50)
            ? oAcct.GetBalance()
            TRY
              x = oAcct.nBalance
            CATCH TO oErr
              ? oErr.ErrorNo
            ENDTRY
            TRY
              oAcct.nBalance = 1000
            CATCH TO oErr
              ? oErr.ErrorNo
            ENDTRY
            TRY
              oAcct.Audit()
            CATCH TO oErr
              ? oErr.ErrorNo
            ENDTRY

            DEFINE CLASS Account AS Custom
              PROTECTED nBalance, Audit
              nBalance = 10
              PROCEDURE Deposit(n)
                THIS.nBalance = THIS.nBalance + n
                THIS.Audit()
              PROCEDURE GetBalance
                RETURN THIS.nBalance
              PROCEDURE Audit
            ENDDEFINE
            """);
        Assert.Equal("        60\n      1734\n      1734\n      1734", o);
    }

    [Fact]
    public void Hidden_members_are_invisible_to_subclass_code_but_protected_ones_are_not()
    {
        var o = Run("""
            oChild = CREATEOBJECT("Child")
            ? oChild.ReadProtected()
            TRY
              x = oChild.ReadHidden()
            CATCH TO oErr
              ? oErr.ErrorNo
            ENDTRY
            ? oChild.ParentReadsHidden()

            DEFINE CLASS Base AS Custom
              PROTECTED cProt
              HIDDEN cHid
              cProt = "prot"
              cHid = "hid"
              PROCEDURE ParentReadsHidden
                RETURN THIS.cHid
            ENDDEFINE

            DEFINE CLASS Child AS Base
              PROCEDURE ReadProtected
                RETURN THIS.cProt
              PROCEDURE ReadHidden
                RETURN THIS.cHid
            ENDDEFINE
            """);
        Assert.Equal("prot\n      1734\nhid", o);
    }

    [Fact]
    public void Net_objects_can_be_created_and_used()
    {
        var o = Run("""
            oSb = CREATEOBJECT("net:System.Text.StringBuilder")
            oSb.Append("Joe")
            oSb.Append(" Pro")
            ? oSb.Length, oSb.ToString()
            oList = CREATEOBJECT("net:System.Collections.ArrayList")
            oList.Add(3)
            oList.Add("x")
            ? oList.Count, oList.Item(1)
            """);
        Assert.Equal("         7 Joe Pro\n         2 x", o);
    }
}
