using JoePro.Core;
using JoePro.Data;

namespace JoePro.Runtime;

/// <summary>
/// Implemented by a user-interface host (the Avalonia UI runtime) so FoxPro code can show forms,
/// run the event loop and open tool windows. The console host leaves it unset.
/// </summary>
public interface IUiHost
{
    /// <summary>A property of <paramref name="o"/> changed from code; update the control that renders it.</summary>
    void PropertyChanged(VfpObject o, string property);

    /// <summary>Form.Show([nStyle]): nStyle 1 = modal (returns when the form is hidden or released).</summary>
    void Show(VfpObject form, bool modal);

    void Hide(VfpObject form);

    /// <summary>The form was released (Destroy/Unload already ran); close its window.</summary>
    void Release(VfpObject form);

    /// <summary>Re-reads ControlSource values into the controls of <paramref name="o"/> and its members.</summary>
    void Refresh(VfpObject o);

    void SetFocus(VfpObject control);

    /// <summary>READ EVENTS: runs the event loop until CLEAR EVENTS.</summary>
    void ReadEvents();

    /// <summary>CLEAR EVENTS.</summary>
    void ClearEvents();

    /// <summary>BROWSE: opens an editable grid over a work area. Returns false to fall back to text output.</summary>
    bool Browse(WorkArea area, IReadOnlyList<string>? fields);

    /// <summary>MODIFY COMMAND / MODIFY FILE: opens a file in the code editor. Returns false if unavailable.</summary>
    bool ModifyFile(string path);

    /// <summary>CREATE/MODIFY FORM, CLASS, REPORT, LABEL, MENU, QUERY, PROJECT…: opens a designer. Returns false if unavailable.</summary>
    bool OpenDesigner(DesignerRequest request);
}

/// <summary>
/// A request to open a designer. <see cref="Path"/> is resolved against SET DEFAULT (empty for an untitled document);
/// for CLASS it is the class library and <see cref="ClassName"/> names the class.
/// </summary>
public sealed record DesignerRequest(string Kind, string Path, bool Create, string? ClassName = null, string? BaseClass = null, string? BaseLibrary = null);
