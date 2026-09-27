using System.Text;
using JoePro.Runtime;

namespace JoePro.Ui.Runtime;

/// <summary>The Screen: output of ?, ??, LIST and friends, shown in the IDE's Screen tab or an application's main window.</summary>
public sealed class ScreenOutput : IConsoleOutput, IClearableOutput
{
    private readonly StringBuilder _text = new();

    public int Column { get; private set; }
    public string Text => _text.ToString();
    public event Action? Changed;

    public void Write(string text)
    {
        _text.Append(text);
        var nl = text.LastIndexOf('\n');
        Column = nl >= 0 ? text.Length - nl - 1 : Column + text.Length;
        Changed?.Invoke();
    }

    public void NewLine()
    {
        _text.Append('\n');
        Column = 0;
        Changed?.Invoke();
    }

    public void Clear()
    {
        _text.Clear();
        Column = 0;
        Changed?.Invoke();
    }
}

