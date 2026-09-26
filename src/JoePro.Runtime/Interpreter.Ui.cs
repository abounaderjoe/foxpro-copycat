using JoePro.Core;
using JoePro.Data;
using JoePro.Language;

namespace JoePro.Runtime;

/// <summary>Entry points used by UI hosts (form runtime, IDE tool windows).</summary>
public sealed partial class Interpreter
{
    /// <summary>Raises an event on an object in its form's data session. Returns the method's result (.T. if not defined).</summary>
    public Value Raise(VfpObject o, string eventName, params Value[] args)
    {
        var saved = Session;
        Session = SessionFor(o);
        try { return RaiseEvent(o, eventName, args.Select(a => new Arg(a, null)).ToList()); }
        finally { Session = saved; }
    }

    /// <summary>Releases an object (for a form: Destroy, Unload, then the UI host closes its window).</summary>
    public void Release(VfpObject o) => ReleaseObject(o);

    /// <summary>True if the object's class defines the event or method.</summary>
    public bool Handles(VfpObject o, string eventName) => FindHandler(o, eventName) != null;

    /// <summary>Reads a ControlSource expression in the object's context.</summary>
    public Value ReadControlSource(VfpObject o, string controlSource) =>
        InObjectContext(o, () => Evaluate(controlSource));

    /// <summary>
    /// Writes a value to a ControlSource: a field (REPLACE, with rules and triggers), a memory variable,
    /// or an object property.
    /// </summary>
    public void WriteControlSource(VfpObject o, string controlSource, Value value)
    {
        InObjectContext(o, () =>
        {
            var e = Parser.ParseExpression(controlSource);
            switch (e)
            {
                case NameExpr n when Session.Current.InUse && Session.Current.FieldIndex(n.Name) >= 0:
                    WriteField(Session.Current, Session.Current.FieldIndex(n.Name), value);
                    break;
                case MemberExpr { Target: NameExpr a } m when FindVariable(a.Name) is not { Value.Kind: ValueKind.Object } && Session.FindAlias(a.Name) is { } wa:
                    WriteField(wa, wa.FieldIndex(m.Name) is var i and >= 0 ? i : throw VfpException.FieldNotFound(m.Name), value);
                    break;
                case AliasFieldExpr af:
                {
                    var wa = Session.ResolveAlias(af.Alias);
                    WriteField(wa, wa.FieldIndex(af.Field), value);
                    break;
                }
                default:
                    Assign(e, value);
                    break;
            }
            return true;
        });
    }

    private void WriteField(WorkArea wa, int field, Value value)
    {
        if (wa.Eof) return;
        var f = wa.Table.Fields[field];
        if (!value.IsNull) value = f.Coerce(value);
        if (wa.Get(field).Equals(value)) return;
        ReplaceWithRules(wa, [(field, value)]);
    }

    /// <summary>REPLACE of one field on the current record, with rules and triggers (Browse window edits).</summary>
    public void ReplaceField(WorkArea wa, int field, Value value)
    {
        var saved = Session.CurrentAreaNumber;
        try { WriteField(wa, field, value); }
        finally { if (Session.OpenWorkAreas().Contains(wa)) Session.Select(saved); }
    }

    /// <summary>APPEND BLANK with default values (Browse window).</summary>
    public void AppendBlank(WorkArea wa) => AppendWithDefaults(wa, null);

    /// <summary>Runs an action inside a given data session (for tool windows bound to a form's session).</summary>
    public T InSession<T>(DataSession session, Func<T> action)
    {
        var saved = Session;
        Session = session;
        try { return action(); }
        finally { Session = saved; }
    }
}
