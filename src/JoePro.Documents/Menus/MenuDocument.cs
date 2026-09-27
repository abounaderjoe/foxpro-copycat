using System.Text;
using JoePro.Core;
using JoePro.Documents.Yaml;
using JoePro.Legacy.Formats;

namespace JoePro.Documents.Menus;

public enum MenuKind { Menu, Shortcut }

/// <summary>Where a menu bar goes relative to the system menu (VFP General Options → Location).</summary>
public enum MenuLocation { Replace, Append, Before, After }

/// <summary>
/// A menu (.jpmenu): items with prompts, keys, SKIP FOR, messages, marks, and what they do — a command, a procedure,
/// a submenu, or a system bar. Runs directly (DO main.jpmenu) or generates MPR-compatible code.
/// </summary>
public sealed class MenuDocument
{
    public MenuKind Kind { get; set; }
    public MenuLocation Location { get; set; }
    /// <summary>For Before/After: the system menu pad (for example _MEDIT).</summary>
    public string? LocationPad { get; set; }
    public string? Description { get; set; }
    public string? Setup { get; set; }
    public string? Cleanup { get; set; }
    public List<MenuNode> Items { get; } = new();

    public static MenuDocument Load(string path) => MenuSerializer.Parse(File.ReadAllText(path));
    public void Save(string path) => File.WriteAllText(path, MenuSerializer.Write(this), new UTF8Encoding(false));

    /// <summary>Every item, depth first.</summary>
    public IEnumerable<MenuNode> AllItems()
    {
        IEnumerable<MenuNode> Walk(List<MenuNode> items)
        {
            foreach (var i in items)
            {
                yield return i;
                foreach (var c in Walk(i.Items)) yield return c;
            }
        }
        return Walk(Items);
    }
}

public sealed class MenuNode
{
    public string Prompt { get; set; } = "";
    /// <summary>Pad name (top level) or submenu popup name; generated from the prompt when empty.</summary>
    public string? Name { get; set; }
    public string? KeyName { get; set; }
    public string? KeyText { get; set; }
    public string? SkipFor { get; set; }
    public string? Message { get; set; }
    public string? Picture { get; set; }
    public bool Mark { get; set; }
    public string? Comment { get; set; }
    /// <summary>A single command run when the item is chosen.</summary>
    public string? Command { get; set; }
    /// <summary>Code run when the item is chosen (becomes a procedure).</summary>
    public string? Procedure { get; set; }
    /// <summary>A VFP system bar (_MED_COPY, _MFI_QUIT…).</summary>
    public string? SystemBar { get; set; }
    /// <summary>Submenu items.</summary>
    public List<MenuNode> Items { get; } = new();

    public bool IsSeparator => Prompt.Trim() == "\\-";
    public string Caption => Prompt.Replace("\\<", "");

    public MenuNode Clone()
    {
        var n = new MenuNode
        {
            Prompt = Prompt, Name = Name, KeyName = KeyName, KeyText = KeyText, SkipFor = SkipFor, Message = Message, Picture = Picture, Mark = Mark,
            Comment = Comment, Command = Command, Procedure = Procedure, SystemBar = SystemBar,
        };
        foreach (var c in Items) n.Items.Add(c.Clone());
        return n;
    }
}

public static class MenuSerializer
{
    public const string Header = "Joe Pro menu v1";

    private static string Word<T>(T v) where T : Enum { var s = v.ToString(); return char.ToLowerInvariant(s[0]) + s[1..]; }

    public static string Write(MenuDocument doc)
    {
        var root = new YamlMap();
        root.Set("description", doc.Description);
        root.SetWord("kind", Word(doc.Kind), "menu");
        root.SetWord("location", Word(doc.Location), "replace");
        root.Set("locationPad", doc.LocationPad);
        root.Set("setup", Block(doc.Setup));
        root.Set("cleanup", Block(doc.Cleanup));
        root.SetNode("items", Items(doc.Items));
        return YamlText.Write(root, Header);
    }

    private static string? Block(string? code) => string.IsNullOrWhiteSpace(code) ? null : code.TrimEnd('\n', '\r').Replace("\r\n", "\n") + "\n";

    private static YamlSeq Items(List<MenuNode> items)
    {
        var seq = new YamlSeq();
        foreach (var i in items)
        {
            var m = new YamlMap();
            if (i.SystemBar != null && i.Prompt.Length == 0) m.SetWord("bar", i.SystemBar.ToLowerInvariant());
            else
            {
                m.Add("prompt", new YamlScalar(i.Prompt, quoted: true));
                if (i.SystemBar != null) m.SetWord("bar", i.SystemBar.ToLowerInvariant());
            }
            m.Set("name", i.Name).Set("key", i.KeyName).Set("keyText", i.KeyText).Set("skipFor", i.SkipFor).Set("message", i.Message)
             .Set("picture", i.Picture).Set("mark", i.Mark).Set("comment", i.Comment).Set("command", i.Command).Set("procedure", Block(i.Procedure));
            m.SetNode("items", Items(i.Items));
            seq.Add(m);
        }
        return seq;
    }

    public static MenuDocument Parse(string text)
    {
        var root = YamlText.Parse(text);
        root.CheckKeys("description", "kind", "location", "locationPad", "setup", "cleanup", "items");
        var doc = new MenuDocument
        {
            Description = root.Str("description"),
            Kind = Enum.TryParse<MenuKind>(root.Str("kind", "menu"), true, out var k) ? k : throw new FormatException($"Line {root["kind"]!.Line}: kind should be menu or shortcut."),
            Location = Enum.TryParse<MenuLocation>(root.Str("location", "replace"), true, out var l) ? l : throw new FormatException($"Line {root["location"]!.Line}: location should be replace, append, before or after."),
            LocationPad = root.Str("locationPad"),
            Setup = root.Str("setup"),
            Cleanup = root.Str("cleanup"),
        };
        ReadItems(root.Seq("items"), doc.Items);
        return doc;
    }

    private static void ReadItems(IEnumerable<YamlNode> nodes, List<MenuNode> into)
    {
        foreach (var node in nodes)
        {
            var m = node as YamlMap ?? throw new FormatException($"Line {node.Line}: a menu item should be a mapping.");
            m.CheckKeys("prompt", "bar", "name", "key", "keyText", "skipFor", "message", "picture", "mark", "comment", "command", "procedure", "items");
            if (m["prompt"] == null && m["bar"] == null) throw new FormatException($"Line {m.Line}: a menu item needs a prompt (or a system bar).");
            var item = new MenuNode
            {
                Prompt = m.Str("prompt", ""), SystemBar = m.Str("bar")?.ToUpperInvariant(), Name = m.Str("name"), KeyName = m.Str("key"), KeyText = m.Str("keyText"),
                SkipFor = m.Str("skipFor"), Message = m.Str("message"), Picture = m.Str("picture"), Mark = m.Bool("mark"), Comment = m.Str("comment"),
                Command = m.Str("command"), Procedure = m.Str("procedure"),
            };
            var actions = new[] { item.Command != null, item.Procedure != null, item.SystemBar != null, m["items"] != null }.Count(x => x);
            if (actions > 1) throw new FormatException($"Line {m.Line}: a menu item has one action: command, procedure, bar or items.");
            ReadItems(m.Seq("items"), item.Items);
            into.Add(item);
        }
    }
}

/// <summary>Writes the MPR program for a menu (the code GENMENU would generate), which Joe Pro and VFP both run.</summary>
public static class MenuGenerator
{
    private static string Q(string s) => Literal.Quote(s);

    /// <summary>A name usable as a pad or popup name, from a prompt.</summary>
    public static string NameFrom(string prompt, string prefix, HashSet<string> used)
    {
        var letters = new string(prompt.Replace("\\<", "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (letters.Length == 0) letters = "item";
        if (letters.Length > 8) letters = letters[..8];
        var name = prefix + letters;
        var candidate = name;
        for (int i = 2; !used.Add(candidate); i++) candidate = name + i;
        return candidate;
    }

    public static string Generate(MenuDocument doc, string menuFile = "menu")
    {
        var sb = new StringBuilder();
        var procs = new List<(string Name, string Code)>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var procPrefix = "_" + new string(Path.GetFileNameWithoutExtension(menuFile).Where(char.IsLetterOrDigit).Take(4).ToArray()).ToLowerInvariant();
        sb.Append("* Generated by Joe Pro from ").Append(Path.GetFileName(menuFile)).Append(" - edit the .jpmenu file, not this code.\n");
        if (!string.IsNullOrWhiteSpace(doc.Setup)) sb.Append(doc.Setup.TrimEnd()).Append('\n');

        string Action(MenuNode item)
        {
            if (item.Command is { Length: > 0 } cmd) return cmd;
            if (item.Procedure is { } code && code.Trim().Length > 0)
            {
                var name = procPrefix + (procs.Count + 1).ToString("D4");
                procs.Add((name, code));
                return "DO " + name;
            }
            return "";
        }

        string Options(MenuNode item)
        {
            var o = new StringBuilder();
            if (item.KeyName is { Length: > 0 } k) o.Append(" KEY ").Append(k).Append(", ").Append(Q(item.KeyText ?? ""));
            if (item.SkipFor is { Length: > 0 } skip) o.Append(" SKIP FOR ").Append(skip);
            if (item.Message is { Length: > 0 } msg) o.Append(" MESSAGE ").Append(Q(msg));
            if (item.Picture is { Length: > 0 } pic) o.Append(" PICTURE ").Append(Q(pic));
            return o.ToString();
        }

        void Popup(string popupName, List<MenuNode> items, bool shortcut)
        {
            sb.Append("DEFINE POPUP ").Append(popupName).Append(shortcut ? " SHORTCUT RELATIVE FROM MROW(),MCOL()" : " MARGIN RELATIVE SHADOW").Append('\n');
            var subs = new List<(MenuNode Item, string Popup)>();
            int n = 0;
            foreach (var item in items)
            {
                n++;
                if (item.SystemBar != null)
                {
                    sb.Append("DEFINE BAR ").Append(item.SystemBar).Append(" OF ").Append(popupName);
                    if (item.Prompt.Length > 0) sb.Append(" PROMPT ").Append(Q(item.Prompt));
                    sb.Append(Options(item)).Append('\n');
                    continue;
                }
                sb.Append("DEFINE BAR ").Append(n).Append(" OF ").Append(popupName).Append(" PROMPT ").Append(Q(item.Prompt)).Append(Options(item)).Append('\n');
                if (item.Mark) sb.Append("SET MARK OF BAR ").Append(n).Append(" OF ").Append(popupName).Append(" TO .T.\n");
                if (item.Items.Count > 0)
                {
                    var sub = item.Name is { Length: > 0 } nm ? nm : NameFrom(item.Prompt, "", used);
                    used.Add(sub);
                    sb.Append("ON BAR ").Append(n).Append(" OF ").Append(popupName).Append(" ACTIVATE POPUP ").Append(sub).Append('\n');
                    subs.Add((item, sub));
                }
                else if (!item.IsSeparator && Action(item) is { Length: > 0 } action)
                    sb.Append("ON SELECTION BAR ").Append(n).Append(" OF ").Append(popupName).Append(' ').Append(action).Append('\n');
            }
            foreach (var (item, sub) in subs) Popup(sub, item.Items, false);
        }

        if (doc.Kind == MenuKind.Shortcut)
        {
            var name = NameFrom(Path.GetFileNameWithoutExtension(menuFile), "", used);
            Popup(name, doc.Items, shortcut: true);
            sb.Append("ACTIVATE POPUP ").Append(name).Append('\n');
        }
        else
        {
            if (doc.Location == MenuLocation.Replace) sb.Append("SET SYSMENU TO\n");
            sb.Append("SET SYSMENU AUTOMATIC\n");
            var pads = new List<(MenuNode Item, string Pad, string Popup)>();
            foreach (var item in doc.Items)
            {
                var pad = item.Name is { Length: > 0 } nm ? nm : NameFrom(item.Prompt, "_", used);
                used.Add(pad);
                sb.Append("DEFINE PAD ").Append(pad).Append(" OF _MSYSMENU PROMPT ").Append(Q(item.Prompt));
                if (doc.Location is MenuLocation.Before or MenuLocation.After && doc.LocationPad is { Length: > 0 } where)
                    sb.Append(doc.Location == MenuLocation.Before ? " BEFORE " : " AFTER ").Append(where);
                sb.Append(Options(item)).Append('\n');
                if (item.Items.Count > 0)
                {
                    var popup = NameFrom(item.Prompt, "", used);
                    sb.Append("ON PAD ").Append(pad).Append(" OF _MSYSMENU ACTIVATE POPUP ").Append(popup).Append('\n');
                    pads.Add((item, pad, popup));
                }
                else if (Action(item) is { Length: > 0 } action)
                    sb.Append("ON SELECTION PAD ").Append(pad).Append(" OF _MSYSMENU ").Append(action).Append('\n');
            }
            foreach (var (item, _, popup) in pads) Popup(popup, item.Items, false);
        }
        if (!string.IsNullOrWhiteSpace(doc.Cleanup)) sb.Append(doc.Cleanup.TrimEnd()).Append('\n');
        foreach (var (name, code) in procs)
        {
            sb.Append("\nPROCEDURE ").Append(name).Append('\n');
            foreach (var line in code.TrimEnd().Split('\n')) sb.Append("    ").Append(line.TrimEnd('\r')).Append('\n');
            sb.Append("ENDPROC\n");
        }
        return sb.ToString();
    }
}

public sealed class MenuConversion
{
    public required MenuDocument Document { get; init; }
    public List<ConversionFinding> Findings { get; } = new();
}

/// <summary>
/// Converts a VFP menu definition (.MNX/.MNT) to a .jpmenu document. MNX lists a level header (OBJTYPE 2) followed by
/// its items (OBJTYPE 3); each submenu's level follows depth first. Item OBJCODE gives the action: C(67) command,
/// P(80) procedure, M(77) submenu, N(78) bar number.
/// </summary>
public static class LegacyMenuConverter
{
    private sealed class Rec
    {
        public Dictionary<string, Value> Cols { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string S(string n) => Cols.TryGetValue(n, out var v) && v.Kind == ValueKind.Character ? v.AsString.Replace("\r\n", "\n").TrimEnd() : "";
        public int N(string n) => Cols.TryGetValue(n, out var v) && v.Kind is ValueKind.Number or ValueKind.Currency ? (int)v.AsNumber : 0;
        public bool L(string n) => Cols.TryGetValue(n, out var v) && v.Kind == ValueKind.Logical && v.AsBool;
    }

    public static MenuConversion Convert(string path)
    {
        var recs = new List<Rec>();
        using (var t = DbfTable.Open(path))
        {
            var names = t.Fields.Select(f => f.Name).ToList();
            foreach (var r in t.Records())
            {
                if (r.Deleted) continue;
                var rec = new Rec();
                for (int i = 0; i < names.Count; i++) rec.Cols[names[i]] = r.Values[i];
                recs.Add(rec);
            }
        }
        var file = Path.GetFileName(path);
        var header = recs.FirstOrDefault(r => r.N("OBJTYPE") == 1) ?? throw new FormatException($"{file} has no menu header record; is it a menu file?");
        var doc = new MenuDocument
        {
            Kind = header.N("OBJCODE") == 23 ? MenuKind.Shortcut : MenuKind.Menu,
            Location = header.N("LOCATION") switch { 1 => MenuLocation.Append, 2 => MenuLocation.Before, 3 => MenuLocation.After, _ => MenuLocation.Replace },
            Setup = NullIfEmpty(header.S("SETUP")),
            Cleanup = NullIfEmpty(header.S("CLEANUP")),
        };
        if (doc.Location is MenuLocation.Before or MenuLocation.After) doc.LocationPad = NullIfEmpty(header.S("NAME"));
        var result = new MenuConversion { Document = doc };
        int pos = recs.IndexOf(header) + 1;
        if (pos < recs.Count && recs[pos].N("OBJTYPE") == 2) doc.Items.AddRange(ReadLevel(recs, ref pos, result, file, top: true));
        if (pos < recs.Count)
            result.Findings.Add(new(FindingStatus.NeedsReview, file, $"{recs.Count - pos} menu record(s) after the last level were not converted."));
        if (header.S("PROCEDURE") is { Length: > 0 })
            result.Findings.Add(new(FindingStatus.NeedsReview, file, "The menu's default procedure was not converted; give items without an action their own command."));
        return result;
    }

    private static string? NullIfEmpty(string s) => s.Trim().Length == 0 ? null : s;

    private static List<MenuNode> ReadLevel(List<Rec> recs, ref int pos, MenuConversion result, string file, bool top)
    {
        var level = recs[pos++];
        var count = level.N("NUMITEMS");
        var items = new List<(MenuNode Node, Rec Rec)>();
        while (pos < recs.Count && recs[pos].N("OBJTYPE") == 3 && (count == 0 || items.Count < count))
        {
            var r = recs[pos++];
            var node = new MenuNode
            {
                Prompt = r.S("PROMPT"),
                Name = top ? NullIfEmpty(r.S("NAME")) : null,
                KeyName = NullIfEmpty(r.S("KEYNAME")),
                KeyText = NullIfEmpty(r.S("KEYLABEL")),
                SkipFor = NullIfEmpty(r.S("SKIPFOR")),
                Message = NullIfEmpty(Unquote(r.S("MESSAGE"))),
                Mark = r.S("MARK").Trim().Length > 0,
                Comment = NullIfEmpty(r.S("COMMENT")),
            };
            switch (r.N("OBJCODE"))
            {
                case 67: node.Command = NullIfEmpty(r.S("COMMAND")); break;
                case 80: node.Procedure = NullIfEmpty(r.S("PROCEDURE")); break;
                case 78: node.SystemBar = NullIfEmpty(r.S("NAME"))?.ToUpperInvariant(); break;
            }
            items.Add((node, r));
        }
        foreach (var (node, r) in items)
        {
            if (r.N("OBJCODE") != 77) continue;
            if (pos < recs.Count && recs[pos].N("OBJTYPE") == 2)
            {
                var popupName = recs[pos].S("NAME").Trim();
                if (!top && popupName.Length > 0) node.Name = popupName;
                node.Items.AddRange(ReadLevel(recs, ref pos, result, file, top: false));
            }
            else result.Findings.Add(new(FindingStatus.NeedsReview, file, $"Submenu of \"{node.Caption}\" was not found."));
        }
        return items.Select(i => i.Node).ToList();
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && (s[0] == '"' && s[^1] == '"' || s[0] == '\'' && s[^1] == '\'') ? s[1..^1] : s;
    }
}
