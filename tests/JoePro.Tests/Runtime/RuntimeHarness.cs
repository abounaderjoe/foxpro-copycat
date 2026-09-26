using JoePro.Runtime;

namespace JoePro.Tests.Runtime;

public abstract class RuntimeHarness : IDisposable
{
    protected readonly string Dir = TestPaths.TempDir();
    protected readonly StringWriter Out = new();
    protected readonly List<string> StatusMessages = new();
    protected readonly Interpreter Rt;

    protected RuntimeHarness()
    {
        Rt = new Interpreter(new TextWriterOutput(Out), Dir);
        Rt.Status += StatusMessages.Add;
        Rt.ExecuteCommand("SET TALK OFF");
    }

    /// <summary>Runs code and returns the printed output with line endings normalized.</summary>
    protected string Run(string code)
    {
        Out.GetStringBuilder().Clear();
        Rt.ExecuteCommand(code);
        return Out.ToString().Replace("\r\n", "\n");
    }

    protected string Eval(string expr) => JoePro.Core.Formatter.ToDisplay(Rt.Evaluate(expr), Rt.Options);

    public void Dispose() => Rt.Session.Dispose();
}
