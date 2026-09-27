using System.Text;
using JoePro.Core;
using JoePro.Language;

namespace JoePro.Runtime.Builtins;

public static partial class Library
{
    private static readonly Dictionary<int, FileStream> Handles = new();
    private static int _nextHandle = 10;
    /// <summary>The operating system error of the last low-level file function (FERROR()).</summary>
    private static int _ferror;

    /// <summary>The native base class a library class derives from (following parents in this and other libraries).</summary>
    public static string BaseClassOf(Interpreter rt, JoePro.Documents.ClassFile lib, JoePro.Documents.ClassDocument cls, string libPath, int depth = 0)
    {
        if (depth > 50) return "";
        if (cls.ParentLibrary == null)
        {
            if (lib.Find(cls.ParentClass) is { } parent && !ReferenceEquals(parent, cls)) return BaseClassOf(rt, lib, parent, libPath, depth + 1);
            return BaseClasses.Exists(cls.ParentClass) ? BaseClasses.Canonical(cls.ParentClass) : "";
        }
        var parentPath = rt.ResolveClassFile(cls.ParentLibrary, ".jpclass", ".vcx", libPath);
        if (parentPath == null) return "";
        try
        {
            var parentLib = JoePro.Documents.ClassLibrary.Load(parentPath);
            return parentLib.Find(cls.ParentClass) is { } p ? BaseClassOf(rt, parentLib, p, parentPath, depth + 1) : "";
        }
        catch (Exception ex) when (ex is FormatException or IOException) { return ""; }
    }

    private static char? FieldTypeOf(Interpreter rt, Expr e)
    {
        JoePro.Data.WorkArea? wa = null;
        string? field = null;
        switch (e)
        {
            case NameExpr n when rt.Session.Current.InUse && rt.Session.Current.FieldIndex(n.Name) >= 0:
                wa = rt.Session.Current; field = n.Name; break;
            case MemberExpr { Target: NameExpr a } m when rt.Session.FindAlias(a.Name) is { } w && w.FieldIndex(m.Name) >= 0:
                wa = w; field = m.Name; break;
            case AliasFieldExpr af when rt.Session.FindAlias(af.Alias) is { } w2 && w2.FieldIndex(af.Field) >= 0:
                wa = w2; field = af.Field; break;
        }
        return wa == null ? null : wa.Table.Fields[wa.FieldIndex(field!)].Type;
    }

    private static void RegisterMisc()
    {
        // ---- Menus ----
        Add("BAR", c => { var b = c.Rt.Menus.LastBar; return b != null && int.TryParse(b, out var n) ? N(n) : S(b ?? ""); });
        Add("POPUP", c => S(c.Rt.Menus.LastPopup?.ToUpperInvariant() ?? ""));
        Add("PROMPT", c => S(c.Rt.Menus.LastPrompt));
        Add("PAD", c => S(c.Rt.Menus.LastPad?.ToUpperInvariant() ?? ""));
        Add("MENU", c => S((c.Rt.Menus.LastMenu ?? c.Rt.Menus.ActiveMenu)?.ToUpperInvariant() ?? ""));
        PopupDef P(CallContext c) => c.Rt.Menus.Popup(c.Str(0)) ?? throw new VfpException(1639, $"Popup {c.Str(0).ToUpperInvariant()} is not defined.");
        MenuBarDef M(CallContext c) => c.Rt.Menus.Menus.GetValueOrDefault(c.Str(0)) ?? throw new VfpException(1637, $"Menu {c.Str(0).ToUpperInvariant()} is not defined.");
        BarDef B(CallContext c) => P(c).Bar(c.Arg(1, Value.Zero) is { Kind: ValueKind.Number } n ? ((int)n.AsNumber).ToString() : c.Str(1))
                                   ?? throw new VfpException(1640, "Bar is not defined.");
        PadDef D(CallContext c) => M(c).Pad(c.Str(1)) ?? throw new VfpException(1638, $"Pad {c.Str(1).ToUpperInvariant()} is not defined.");
        Add("CNTBAR", c => N(P(c).Bars.Count));
        Add("CNTPAD", c => N(M(c).Pads.Count));
        Add("GETBAR", c =>
        {
            var bars = P(c).Bars;
            var i = (int)c.Num(1);
            if (i < 1 || i > bars.Count) throw VfpException.InvalidArgument();
            var b = bars[i - 1];
            return b.SystemBar != null ? S(b.SystemBar) : N(b.Number);
        });
        Add("GETPAD", c =>
        {
            var pads = M(c).Pads;
            var i = (int)c.Num(1);
            if (i < 1 || i > pads.Count) throw VfpException.InvalidArgument();
            return S(pads[i - 1].Name.ToUpperInvariant());
        });
        Add("PRMBAR", c => S(B(c).Caption));
        Add("PRMPAD", c => S(D(c).Caption));
        Add("MRKBAR", c => L(B(c).Mark));
        Add("MRKPAD", c => L(D(c).Mark));
        Add("SKPBAR", c => L(c.Rt.IsMenuItemSkipped(B(c))));
        Add("SKPPAD", c => L(c.Rt.IsMenuItemSkipped(D(c))));

        // AVCXCLASSES(aInfo, cLibrary): one row per class — name, parent, parent library, base class, toolbar icon,
        // container icon, scale mode, description, #INCLUDE file, user info, OLE public.
        Add("AVCXCLASSES", c =>
        {
            var path = c.Rt.ResolveClassFile(c.Str(1), ".jpclass", ".vcx") ?? throw VfpException.FileNotFound(c.Str(1));
            var lib = JoePro.Documents.ClassLibrary.Load(path);
            var arr = c.NewArray(0, lib.Classes.Count, 11);
            var classes = lib.Classes.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
            for (int i = 0; i < classes.Count; i++)
            {
                var cls = classes[i];
                int r = i + 1;
                arr[r, 1] = S(cls.Name.ToLowerInvariant());
                arr[r, 2] = S(cls.ParentClass.ToLowerInvariant());
                arr[r, 3] = S(cls.ParentLibrary ?? "");
                arr[r, 4] = S(BaseClassOf(c.Rt, lib, cls, path).ToLowerInvariant());
                arr[r, 5] = S(cls.Icon ?? "");
                arr[r, 6] = S(cls.ContainerIcon ?? "");
                arr[r, 7] = S("Pixels");
                arr[r, 8] = S(cls.Description ?? "");
                arr[r, 9] = S(lib.Includes.FirstOrDefault() ?? "");
                arr[r, 10] = S("");
                arr[r, 11] = L(cls.OlePublic);
            }
            return N(classes.Count);
        });

        // ---- Conditional and null handling ----
        Add("IIF", c =>
        {
            c.Require(3, 3);
            var cond = c[0];
            return cond.Kind == ValueKind.Logical && cond.AsBool ? c[1] : c[2];
        });
        Add("ICASE", c =>
        {
            int i = 0;
            for (; i + 1 < c.Count; i += 2)
                if (c[i].Kind == ValueKind.Logical && c[i].AsBool) return c[i + 1];
            return i < c.Count ? c[i] : Value.Null;
        });
        Add("EVL", c => c[0].IsEmpty || c[0].IsNull ? c[1] : c[0]);
        Add("NVL", c => c[0].IsNull ? c[1] : c[0]);
        Add("INLIST", c =>
        {
            c.Require(2);
            var v = c[0];
            if (v.IsNull) return Value.Null;
            var mode = c.Rt.CompareMode();
            for (int i = 1; i < c.Count; i++)
            {
                var x = c[i];
                if (x.IsNull) continue;
                if (v.Kind == x.Kind || (v.Kind is ValueKind.Number or ValueKind.Currency && x.Kind is ValueKind.Number or ValueKind.Currency) || (v.Kind is ValueKind.Date or ValueKind.DateTime && x.Kind is ValueKind.Date or ValueKind.DateTime))
                    if (VfpCompare.Compare(v, x, mode) == 0) return Value.True;
            }
            return Value.False;
        });
        Add("BETWEEN", c =>
        {
            if (c[0].IsNull || c[1].IsNull || c[2].IsNull) return Value.Null;
            var mode = c.Rt.CompareMode();
            return L(VfpCompare.Compare(c[0], c[1], mode) >= 0 && VfpCompare.Compare(c[0], c[2], mode) <= 0);
        });
        Add("EMPTY", c => L(c[0].IsEmpty));
        Add("ISNULL", c => L(c[0].IsNull));
        Add("ISBLANK", c =>
        {
            var v = c[0];
            return L(v.Kind switch
            {
                ValueKind.Character => v.AsString.Trim().Length == 0,
                ValueKind.Date or ValueKind.DateTime => v.IsEmptyDate,
                ValueKind.Null => false,
                _ => false,
            });
        });
        Add("VARTYPE", c =>
        {
            if (c.Exprs[0] is NameExpr n && c.Rt.FindVariable(n.Name) == null && !(c.Rt.Session.Current.InUse && c.Rt.Session.Current.FieldIndex(n.Name) >= 0))
                return S("U");
            try { return S(c[0].VarType.ToString()); }
            catch (VfpException) { return S("U"); }
        });
        Add("TYPE", c =>
        {
            var text = c.Str(0);
            if (c.Has(1) && c.Int(1) == 1)
            {
                var v = c.Rt.FindVariable(text.Trim());
                return S(v == null ? "U" : v.IsArray ? "A" : v.Value.Kind == ValueKind.Object && ((VfpObject)v.Value.AsObject).Items != null ? "C" : "U");
            }
            try
            {
                var e = Parser.ParseExpression(text);
                // A field reference reports the field's type: memo, general, blob and varbinary fields are M, G, W and Q.
                if (FieldTypeOf(c.Rt, e) is { } ft && ft is 'M' or 'G' or 'W' or 'Q') return S(ft.ToString());
                var v = c.Rt.Eval(e);
                return S(v.Kind == ValueKind.Null ? "X" : v.VarType.ToString());
            }
            catch (VfpException) { return S("U"); }
        });
        Add("EVALUATE", c => c.Rt.Evaluate(c.Str(0)));
        Add("EXECSCRIPT", c =>
        {
            var unit = Parser.ParseProgram(c.Str(0), "EXECSCRIPT");
            var proc = new ProcedureDef("EXECSCRIPT", unit.MainParameters, unit.MainLocalParameters, unit.Main, 1);
            var args = Enumerable.Range(1, c.Count - 1).Select(i => new Interpreter.Arg(c[i], null)).ToList();
            foreach (var p in unit.Procedures) c.Rt.CurrentFrame.Unit?.Procedures.TryAdd(p.Key, p.Value);
            return c.Rt.Invoke(proc, unit, args, bindDeclared: false);
        });
        Add("SET", c => SetFunction(c));
        Add("ON", c => S(c.Rt.OnCommand(c.Str(0), c.Has(1) ? c.Str(1) : null)));

        // ---- Errors and program state ----
        Add("ERROR", c => N(c.Rt.LastErrorNumber));
        Add("MESSAGE", c => S(c.Has(0) ? "" : c.Rt.LastErrorMessage));
        Add("LINENO", c => N(c.Rt.CurrentFrame.Line));
        Add("PROGRAM", c =>
        {
            if (!c.Has(0)) return S(c.Rt.CurrentFrame.Program);
            var n = c.Int(0);
            var frames = new List<Frame>();
            for (var f = c.Rt.CurrentFrame; f != null; f = f.Parent) frames.Add(f);
            frames.Reverse();
            if (n == -1) return N(frames.Count);
            return S(n >= 1 && n <= frames.Count ? frames[n - 1].Program : "");
        });
        Add(["PCOUNT", "PARAMETERS"], c => N(c.Rt.CurrentFrame.ParameterCount));
        Add("AERROR", c =>
        {
            if (c.Rt.LastErrorNumber == 0) return N(0);
            var arr = c.NewArray(0, 1, 7);
            arr[1, 1] = N(c.Rt.LastErrorNumber);
            arr[1, 2] = S(c.Rt.LastErrorMessage);
            for (int i = 0; i < 5; i++) arr[1, i + 3] = c.Rt.LastErrorDetail is { } d ? d[i] : Value.Null;
            return N(1);
        });

        // ---- System ----
        Add("VERSION", c =>
        {
            if (!c.Has(0)) return S(Interpreter.VersionString);
            return c.Int(0) switch
            {
                4 => S("09.00.0000.0000"),
                5 => N(900),
                2 => N(2),
                3 => S("00"),
                _ => S(Interpreter.VersionString),
            };
        });
        Add("OS", c => S(c.Has(0) && c.Int(0) == 1 ? Environment.OSVersion.VersionString : OperatingSystem.IsWindows() ? "Windows " + Environment.OSVersion.Version.Major : Environment.OSVersion.Platform.ToString()));
        Add("GETENV", c => S(Environment.GetEnvironmentVariable(c.Str(0)) ?? ""));
        Add("SYS", c => Sys(c));
        Add("MESSAGEBOX", c =>
        {
            var text = c[0].Kind == ValueKind.Character ? c.Str(0) : TransformDefault(c[0], c.Options);
            var flags = c.Has(1) && c[1].Kind == ValueKind.Number ? c.Int(1) : 0;
            var title = c.Has(2) ? c.Str(2) : "Joe Pro";
            if (c.Rt.MessageBox != null) return N(c.Rt.MessageBox(text, title, flags));
            c.Rt.Notify($"[{title}] {text}");
            return N((flags & 0xF) switch { 1 => 1, 2 => 3, 3 or 4 => 6, 5 => 4, _ => 1 });
        });
        Add("INPUTBOX", c => S(c.Has(2) ? c.Str(2) : ""));
        Add("INKEY", c => N(c.Rt.InKey()));
        Add("LASTKEY", c => N(c.Rt.LastKey));
        Add(["ROW", "COL", "PROW", "PCOL"], _ => N(0));
        Add("CHRSAW", _ => Value.False);
        Add("SROWS", _ => N(25));
        Add("SCOLS", _ => N(80));
        Add(["WONTOP", "WOUTPUT", "WTITLE"], _ => Value.EmptyString);
        Add(["WEXIST", "WVISIBLE", "CAPSLOCK", "NUMLOCK", "INSMODE"], _ => Value.False);
        Add("__DOFORM", c => throw VfpException.NotSupported("DO FORM"));

        // ---- Files ----
        Add("FILE", c => L(File.Exists(c.Rt.Session.ResolvePath(c.Str(0).Trim(), ""))));
        Add("DIRECTORY", c => L(Directory.Exists(Path.Combine(c.Options.Default_, c.Str(0).Trim()))));
        Add("CURDIR", c =>
        {
            var d = c.Options.Default_;
            var root = Path.GetPathRoot(d) ?? "";
            return S(Path.DirectorySeparatorChar + Path.GetRelativePath(root, d).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        });
        Add("FULLPATH", c => S(Path.GetFullPath(Path.Combine(c.Options.Default_, c.Str(0))).ToUpperInvariant()));
        Add("JUSTFNAME", c => S(FileName(c.Str(0))));
        Add("JUSTSTEM", c => { var f = FileName(c.Str(0)); var d = f.LastIndexOf('.'); return S(d < 0 ? f : f[..d]); });
        Add("JUSTEXT", c => { var f = FileName(c.Str(0)); var d = f.LastIndexOf('.'); return S(d < 0 ? "" : f[(d + 1)..]); });
        Add("JUSTPATH", c => { var s = Norm(c.Str(0)); var i = s.LastIndexOfAny(['\\', '/']); return S(i < 0 ? "" : s[..i]); });
        Add("JUSTDRIVE", c => { var s = c.Str(0); return S(s.Length >= 2 && s[1] == ':' ? s[..2] : ""); });
        Add("FORCEEXT", c => { var s = Norm(c.Str(0)); var e = c.Str(1).TrimStart('.'); var dot = s.LastIndexOf('.'); var sl = s.LastIndexOfAny(['\\', '/']); return S((dot > sl ? s[..dot] : s) + (e.Length > 0 ? "." + e : "")); });
        Add("FORCEPATH", c => { var p = c.Str(1).TrimEnd(); return S(p + (p.EndsWith('\\') || p.EndsWith('/') || p.Length == 0 ? "" : "\\") + FileName(c.Str(0))); });
        Add("DEFAULTEXT", c => { var s = c.Str(0); return S(Path.HasExtension(s) ? s : s + "." + c.Str(1).TrimStart('.')); });
        Add("ADDBS", c => { var s = c.Str(0).TrimEnd(); return S(s.Length == 0 || s.EndsWith('\\') || s.EndsWith('/') ? s : s + (s.Contains('/') && !s.Contains('\\') ? "/" : "\\")); });
        Add("FILETOSTR", c =>
        {
            var p = c.Rt.Session.ResolvePath(c.Str(0), "");
            if (!File.Exists(p)) throw VfpException.FileNotFound(c.Str(0));
            return S(Encoding.Latin1.GetString(File.ReadAllBytes(p)) is var s && LooksUtf8(p) ? File.ReadAllText(p) : s);
        });
        Add("STRTOFILE", c =>
        {
            var text = c[0].Kind == ValueKind.Binary ? c[0].AsBinary : Encoding.UTF8.GetBytes(c.Str(0));
            var path = Path.Combine(c.Options.Default_, c.Str(1));
            var additive = c.Has(2) && (c[2].Kind == ValueKind.Logical ? c[2].AsBool : (c.Int(2) & 1) != 0);
            if (additive) { using var fs = new FileStream(path, FileMode.Append); fs.Write(text); }
            else File.WriteAllBytes(path, text);
            return N(text.Length);
        });
        Add("ADIR", c =>
        {
            var skeleton = c.Has(1) ? c.Str(1) : "*.*";
            var full = Path.Combine(c.Options.Default_, skeleton);
            var dir = Path.GetDirectoryName(full) ?? c.Options.Default_;
            var pattern = Path.GetFileName(full);
            if (pattern == "*.*") pattern = "*";
            var files = Directory.Exists(dir) ? Directory.GetFiles(dir, pattern).Select(f => new FileInfo(f)).OrderBy(f => f.Name).ToList() : [];
            var attrs = c.Has(2) ? c.Str(2).ToUpperInvariant() : "";
            var dirs = attrs.Contains('D') && Directory.Exists(dir) ? Directory.GetDirectories(dir, pattern).Select(d => new DirectoryInfo(d)).ToList() : [];
            int n = files.Count + dirs.Count;
            if (n == 0) return N(0);
            var arr = c.NewArray(0, n, 5);
            int r = 1;
            foreach (var f in files)
            {
                arr[r, 1] = S(f.Name.ToUpperInvariant());
                arr[r, 2] = N(f.Length);
                arr[r, 3] = Value.DateOf(DateOnly.FromDateTime(f.LastWriteTime));
                arr[r, 4] = S(f.LastWriteTime.ToString("HH:mm:ss"));
                arr[r, 5] = S((f.IsReadOnly ? "R" : ".") + "...." );
                r++;
            }
            foreach (var d in dirs)
            {
                arr[r, 1] = S(d.Name.ToUpperInvariant());
                arr[r, 2] = N(0);
                arr[r, 3] = Value.DateOf(DateOnly.FromDateTime(d.LastWriteTime));
                arr[r, 4] = S(d.LastWriteTime.ToString("HH:mm:ss"));
                arr[r, 5] = S("....D");
                r++;
            }
            return N(n);
        });
        Add("FCREATE", c => N(OpenHandle(Path.Combine(c.Options.Default_, c.Str(0)), FileMode.Create, FileAccess.ReadWrite)));
        Add("FOPEN", c =>
        {
            var p = c.Rt.Session.ResolvePath(c.Str(0), "");
            if (!File.Exists(p)) { _ferror = 2; return N(-1); }
            var mode = c.Int(1, 0) % 10;
            return N(OpenHandle(p, FileMode.Open, mode switch { 1 => FileAccess.Write, 2 => FileAccess.ReadWrite, _ => FileAccess.Read }));
        });
        Add("FCLOSE", c => { lock (Handles) { if (Handles.Remove(c.Int(0), out var fs)) { fs.Dispose(); return Value.True; } } return Value.False; });
        Add("FREAD", c =>
        {
            var fs = Handle(c.Int(0));
            var buf = new byte[Math.Max(0, c.Int(1))];
            var n = fs.Read(buf, 0, buf.Length);
            return S(Encoding.Latin1.GetString(buf, 0, n));
        });
        Add("FGETS", c =>
        {
            var fs = Handle(c.Int(0));
            var max = c.Int(1, 254);
            var sb = new StringBuilder();
            int b;
            while (sb.Length < max && (b = fs.ReadByte()) >= 0)
            {
                if (b == '\n') break;
                if (b == '\r')
                {
                    var next = fs.ReadByte();
                    if (next != '\n' && next >= 0) fs.Seek(-1, SeekOrigin.Current);
                    break;
                }
                sb.Append((char)b);
            }
            return S(sb.ToString());
        });
        Add(["FWRITE", "FPUTS"], c =>
        {
            var fs = Handle(c.Int(0));
            var s = c.Str(1);
            if (c.Has(2)) s = s[..Math.Min(s.Length, c.Int(2))];
            if (c.Name.Equals("FPUTS", StringComparison.OrdinalIgnoreCase)) s += "\r\n";
            var bytes = Encoding.Latin1.GetBytes(s);
            fs.Write(bytes);
            return N(bytes.Length);
        });
        Add("FEOF", c => { var fs = Handle(c.Int(0)); return L(fs.Position >= fs.Length); });
        Add("FSEEK", c =>
        {
            var fs = Handle(c.Int(0));
            var origin = c.Int(2, 0) switch { 1 => SeekOrigin.End, 2 => SeekOrigin.Current, _ => SeekOrigin.Begin };
            return N(fs.Seek(c.Int(1), origin));
        });
        Add("FFLUSH", c => { Handle(c.Int(0)).Flush(); return Value.True; });
        Add("FCHSIZE", c => { var fs = Handle(c.Int(0)); fs.SetLength(c.Int(1)); return N(fs.Length); });

        // ---- Objects ----
        Add("CREATEOBJECT", c =>
        {
            var cls = c.Str(0);
            if (!c.Rt.ClassExists(cls) && (cls.Contains('.') || cls.StartsWith("net:", StringComparison.OrdinalIgnoreCase)))
                return Value.Object(ClrObjectProxy.Create(cls));
            var args = Enumerable.Range(1, c.Count - 1).Select(i => new Interpreter.Arg(c[i], null)).ToList();
            var o = c.Rt.CreateObject(c.Rt.ResolveClass(cls), args);
            return o == null ? Value.Null : Value.Object(o);
        });
        Add("NEWOBJECT", c =>
        {
            var cls = c.Str(0);
            ProgramUnit? module = c.Has(1) && c.Str(1).Length > 0 ? c.Rt.LoadLibrary(c.Str(1)) : null;
            var args = Enumerable.Range(3, Math.Max(0, c.Count - 3)).Select(i => new Interpreter.Arg(c[i], null)).ToList();
            var o = c.Rt.CreateObject(c.Rt.ResolveClass(cls, module), args);
            return o == null ? Value.Null : Value.Object(o);
        });
        Add("GETOBJECT", c =>
        {
            if (!OperatingSystem.IsWindows()) throw VfpException.NotSupported("GETOBJECT (COM automation is available on Windows only)");
            throw VfpException.NotSupported("GETOBJECT (attaching to running COM servers is not implemented yet)");
        });
        Add("CREATEOBJECTEX", _ => throw VfpException.NotSupported("CREATEOBJECTEX (COM automation arrives in Phase 2)"));
        Add("PEMSTATUS", c =>
        {
            var o = Obj(c, 0);
            var name = c.Str(1);
            var what = c.Int(2);
            var prop = o.FindProperty(name);
            var method = o.Class.FindMethod(name);
            return what switch
            {
                5 => L(prop != null || method != null || name.Equals("Parent", StringComparison.OrdinalIgnoreCase)),
                3 => S(prop != null ? (prop.Value.Kind == ValueKind.Object ? "Object" : "Property") : method != null ? "Method" : ""),
                4 => L(o.Class.Hierarchy().Any(h => h.Definition != null && (h.Definition.Members.Any(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) || h.Definition.Methods.ContainsKey(name)))),
                6 => L(o.Class.Parent?.FindMethod(name) != null),
                2 => L(o.Class.Hierarchy().Any(h => h.Definition?.Protected.Contains(name) == true)),
                _ => Value.False,
            };
        });
        Add("ADDPROPERTY", c =>
        {
            var o = Obj(c, 0);
            o.Set(c.Str(1), c.Has(2) ? c[2] : Value.False);
            return Value.True;
        });
        Add("REMOVEPROPERTY", c => L(Obj(c, 0).Properties.Remove(c.Str(1))));
        Add("AMEMBERS", c =>
        {
            var o = Obj(c, 1);
            var mode = c.Int(2, 0);
            var names = o.Properties.Keys.ToList();
            if (mode == 1)
                names.AddRange(o.Class.Hierarchy().Where(h => h.Definition != null).SelectMany(h => h.Definition!.Methods.Keys));
            names = names.Select(n => n.ToUpperInvariant()).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (names.Count == 0) return N(0);
            var arr = c.NewArray(0, names.Count, mode == 1 ? 2 : 0);
            for (int i = 0; i < names.Count; i++)
            {
                if (mode == 1)
                {
                    arr[i + 1, 1] = S(names[i]);
                    arr[i + 1, 2] = S(o.FindProperty(names[i]) is { } p ? (p.Value.Kind == ValueKind.Object ? "Object" : "Property") : "Method");
                }
                else arr[i + 1] = S(names[i]);
            }
            return N(names.Count);
        });
        Add("COMPOBJ", c =>
        {
            var a = Obj(c, 0);
            var b = Obj(c, 1);
            if (a.Class.Name != b.Class.Name || a.Properties.Count != b.Properties.Count) return Value.False;
            foreach (var (k, v) in a.Properties)
                if (!b.Properties.TryGetValue(k, out var w) || !v.Value.Equals(w.Value)) return Value.False;
            return Value.True;
        });
        Add("GETPEM", c =>
        {
            if (c[0].Kind == ValueKind.Object) return c.Rt.GetProperty(Obj(c, 0), c.Str(1));
            var o = c.Rt.CreateObject(c.Rt.ResolveClass(c.Str(0)), [], noInit: true)!;
            return c.Rt.GetProperty(o, c.Str(1));
        });
        Add("DODEFAULT", c => c.Rt.DoDefault(Enumerable.Range(0, c.Count).Select(i => new Interpreter.Arg(c[i], null)).ToList()));
        Add("NODEFAULT", c => { c.Rt.CurrentFrame.NoDefault = true; return Value.True; });
        Add("RAISEEVENT", c => c.Rt.InvokeMethod(Obj(c, 0), c.Str(1), Enumerable.Range(2, c.Count - 2).Select(i => new Interpreter.Arg(c[i], null)).ToList()));
        Add("BINDEVENT", c =>
        {
            if (c[0].Kind != ValueKind.Object || c[0].AsObject is not VfpObject src) throw VfpException.NotSupported("BINDEVENT to window handles");
            c.Rt.BindEvent(src, c.Str(1), Obj(c, 2), c.Str(3), c.Int(4, 0));
            return N(1);
        });
        Add("AEVENTS", c =>
        {
            if (c[1].Kind != ValueKind.Object)
            {
                // AEVENTS(a, 0): the source of the event that is running the current delegate.
                if (c.Rt.CurrentEventSource is not { } es) return N(0);
                var ea = c.NewArray(0, 3, 0);
                ea[1] = Value.Object(es.Source); ea[2] = S(es.Event); ea[3] = S("Event");
                return N(3);
            }
            var rows = c.Rt.BindingsFor(Obj(c, 1)).ToList();
            if (rows.Count == 0) return N(0);
            var arr = c.NewArray(0, rows.Count, 5);
            for (int i = 0; i < rows.Count; i++)
            {
                arr[i * 5 + 1] = Value.Object(rows[i].Source); arr[i * 5 + 2] = S(rows[i].Event);
                arr[i * 5 + 3] = Value.Object(rows[i].Handler); arr[i * 5 + 4] = S(rows[i].Method); arr[i * 5 + 5] = N(rows[i].Flags);
            }
            return N(rows.Count);
        });
        Add("UNBINDEVENTS", c => N(c.Rt.UnbindEvents(Obj(c, 0), c.Has(1) ? c.Str(1) : null, c.Has(2) ? Obj(c, 2) : null, c.Has(3) ? c.Str(3) : null)));
        Add("ACLASS", c =>
        {
            var o = Obj(c, 1);
            var names = o.Class.Hierarchy().Select(h => h.Name.ToUpperInvariant()).ToList();
            var arr = c.NewArray(0, names.Count, 0);
            for (int i = 0; i < names.Count; i++) arr[i + 1] = S(names[i]);
            return N(names.Count);
        });

        // ---- Arrays ----
        Add("ALEN", c =>
        {
            var a = c.Array(0);
            var mode = c.Int(1, 0);
            return N(mode switch { 1 => a.Rows, 2 => a.Cols, _ => a.Length });
        });
        Add("ASCAN", c =>
        {
            var a = c.Array(0);
            var target = c[1];
            int start = c.Int(2, 1), count = c.Has(3) && c.Int(3) >= 0 ? c.Int(3) : a.Length;
            int col = c.Int(4, -1), flags = c.Int(5, 0);
            bool exact = (flags & 4) != 0 || ((flags & 2) == 0 && c.Rt.Options.Exact);
            bool ci = (flags & 1) != 0;
            bool rowResult = (flags & 8) != 0;
            if (start < 1) start = 1;
            var end = Math.Min(a.Length, start + count - 1);
            for (int i = start; i <= end; i++)
            {
                if (col > 0 && a.TwoDimensional && a.Subscript(i).Col != col) continue;
                var v = a[i];
                if (v.Kind != target.Kind && !(v.Kind is ValueKind.Number or ValueKind.Currency && target.Kind is ValueKind.Number or ValueKind.Currency)) continue;
                bool eq;
                if (v.Kind == ValueKind.Character)
                {
                    var x = ci ? v.AsString.ToUpperInvariant() : v.AsString;
                    var y = ci ? target.AsString.ToUpperInvariant() : target.AsString;
                    eq = VfpCompare.CompareStrings(x, y, exact ? StringCompareMode.Padded : StringCompareMode.RightLength) == 0;
                }
                else eq = VfpCompare.AreEqual(v, target, StringCompareMode.Padded);
                if (eq) return N(rowResult && a.TwoDimensional ? a.Subscript(i).Row : i);
            }
            return N(0);
        });
        Add("ASORT", c =>
        {
            var a = c.Array(0);
            int start = c.Int(1, 1), count = c.Has(2) && c.Int(2) > 0 ? c.Int(2) : -1;
            bool desc = c.Int(3, 0) == 1;
            bool ci = (c.Int(4, 0) & 1) != 0;
            if (!a.TwoDimensional)
            {
                var items = a.Raw.Skip(start - 1).Take(count < 0 ? int.MaxValue : count).ToList();
                items.Sort((x, y) => CompareForSort(x, y, ci));
                if (desc) items.Reverse();
                for (int i = 0; i < items.Count; i++) a[start + i] = items[i];
                return N(1);
            }
            var (startRow, sortCol) = a.Subscript(start);
            var rows = Enumerable.Range(startRow, count < 0 ? a.Rows - startRow + 1 : Math.Min(count, a.Rows - startRow + 1))
                .Select(r => Enumerable.Range(1, a.Cols).Select(col => a[r, col]).ToArray()).ToList();
            rows.Sort((x, y) => CompareForSort(x[sortCol - 1], y[sortCol - 1], ci));
            if (desc) rows.Reverse();
            for (int i = 0; i < rows.Count; i++)
                for (int col = 1; col <= a.Cols; col++) a[startRow + i, col] = rows[i][col - 1];
            return N(1);
        });
        Add("ACOPY", c =>
        {
            var src = c.Array(0);
            var name = c.ArrayName(1);
            var dst = c.Rt.FindVariable(name)?.Array;
            int srcStart = c.Int(2, 1), n = c.Has(3) && c.Int(3) >= 0 ? c.Int(3) : src.Length - srcStart + 1, dstStart = c.Int(4, 1);
            if (dst == null || dst.Length < dstStart + n - 1)
            {
                var created = c.NewArray(1, src.TwoDimensional ? src.Rows : src.Length, src.Cols);
                if (dst != null) for (int i = 1; i <= Math.Min(dst.Length, created.Length); i++) created[i] = dst[i];
                dst = created;
            }
            for (int i = 0; i < n; i++) dst[dstStart + i] = src[srcStart + i];
            return N(n);
        });
        Add("ADEL", c =>
        {
            var a = c.Array(0);
            var n = c.Int(1);
            if (!a.TwoDimensional || c.Int(2, 1) == 1 && !a.TwoDimensional)
            {
                for (int i = n; i < a.Length; i++) a[i] = a[i + 1];
                a[a.Length] = Value.False;
            }
            else if (c.Int(2, 1) == 2)
            {
                for (int r = 1; r <= a.Rows; r++)
                {
                    for (int col = n; col < a.Cols; col++) a[r, col] = a[r, col + 1];
                    a[r, a.Cols] = Value.False;
                }
            }
            else
            {
                for (int r = n; r < a.Rows; r++)
                    for (int col = 1; col <= a.Cols; col++) a[r, col] = a[r + 1, col];
                for (int col = 1; col <= a.Cols; col++) a[a.Rows, col] = Value.False;
            }
            return N(1);
        });
        Add("AINS", c =>
        {
            var a = c.Array(0);
            var n = c.Int(1);
            if (!a.TwoDimensional)
            {
                for (int i = a.Length; i > n; i--) a[i] = a[i - 1];
                a[n] = Value.False;
            }
            else if (c.Int(2, 1) == 2)
            {
                for (int r = 1; r <= a.Rows; r++)
                {
                    for (int col = a.Cols; col > n; col--) a[r, col] = a[r, col - 1];
                    a[r, n] = Value.False;
                }
            }
            else
            {
                for (int r = a.Rows; r > n; r--)
                    for (int col = 1; col <= a.Cols; col++) a[r, col] = a[r - 1, col];
                for (int col = 1; col <= a.Cols; col++) a[n, col] = Value.False;
            }
            return N(1);
        });
        Add("AELEMENT", c => { var a = c.Array(0); return N(c.Has(2) ? a.Index(c.Int(1), c.Int(2)) : c.Int(1)); });
        Add("ASUBSCRIPT", c => { var a = c.Array(0); var (r, col) = a.Subscript(c.Int(1)); return N(c.Int(2) == 1 ? r : col); });
        Add("ALINES", c =>
        {
            var text = c[1].IsNull ? "" : c.Str(1);
            var flags = c.Has(2) && c[2].Kind == ValueKind.Number ? c.Int(2) : (c.Has(2) && c[2].Kind == ValueKind.Logical && c[2].AsBool ? 1 : 0);
            IEnumerable<string> parts;
            if (c.Count > 3)
            {
                var seps = Enumerable.Range(3, c.Count - 3).Select(c.Str).Where(s => s.Length > 0).ToArray();
                parts = text.Split(seps, StringSplitOptions.None);
            }
            else parts = SplitLines(text);
            if ((flags & 1) != 0) parts = parts.Select(p => p.Trim());
            if ((flags & 4) != 0) parts = parts.Where(p => p.Length > 0);
            var list = parts.ToList();
            if (text.Length == 0 && (flags & 4) == 0) list = [""];
            if (list.Count == 0) return N(0);
            var arr = c.NewArray(0, list.Count, 0);
            for (int i = 0; i < list.Count; i++) arr[i + 1] = S(list[i]);
            return N(list.Count);
        });
    }

    private static int CompareForSort(Value x, Value y, bool ci)
    {
        if (x.Kind != y.Kind) return x.Kind.CompareTo(y.Kind);
        if (ci && x.Kind == ValueKind.Character) return string.CompareOrdinal(x.AsString.ToUpperInvariant(), y.AsString.ToUpperInvariant());
        try { return VfpCompare.Compare(x, y, StringCompareMode.Padded); }
        catch (VfpException) { return 0; }
    }

    private static VfpObject Obj(CallContext c, int i) =>
        c[i].Kind == ValueKind.Object && c[i].AsObject is VfpObject o ? o : throw VfpException.InvalidArgument();

    private static string Norm(string path) => path.Trim();

    /// <summary>File name part of a path, treating both '\' and '/' as separators (FoxPro paths are Windows paths).</summary>
    private static string FileName(string path)
    {
        path = path.Trim();
        var i = path.LastIndexOfAny(['\\', '/', ':']);
        return i < 0 ? path : path[(i + 1)..];
    }

    private static bool LooksUtf8(string path)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return true;
        try { _ = new UTF8Encoding(false, true).GetString(b); return b.Any(x => x > 127); }
        catch (DecoderFallbackException) { return false; }
    }

    private static int OpenHandle(string path, FileMode mode, FileAccess access)
    {
        try
        {
            var fs = new FileStream(path, mode, access, FileShare.ReadWrite);
            _ferror = 0;
            lock (Handles)
            {
                var h = _nextHandle++;
                Handles[h] = fs;
                return h;
            }
        }
        catch (FileNotFoundException) { _ferror = 2; return -1; }
        catch (DirectoryNotFoundException) { _ferror = 2; return -1; }
        catch (IOException) { _ferror = 29; return -1; }
        catch (UnauthorizedAccessException) { _ferror = 5; return -1; }
    }

    private static FileStream Handle(int h)
    {
        lock (Handles) return Handles.TryGetValue(h, out var fs) ? fs : throw VfpException.InvalidArgument();
    }

    private static Value SetFunction(CallContext c)
    {
        var o = c.Options;
        var name = c.Str(0).ToUpperInvariant();
        string OnOff(bool b) => b ? "ON" : "OFF";
        return name switch
        {
            "EXACT" => S(OnOff(o.Exact)),
            "ANSI" => S(OnOff(o.Ansi)),
            "DELETED" => S(OnOff(o.Deleted)),
            "NEAR" => S(OnOff(o.Near)),
            "TALK" => S(OnOff(o.Talk)),
            "CENTURY" => S(OnOff(o.Century)),
            "NULL" => S(OnOff(o.Null)),
            "SECONDS" => S(OnOff(o.Seconds)),
            "EXCLUSIVE" => S(OnOff(o.Exclusive)),
            "SAFETY" => S(OnOff(o.Safety)),
            "OPTIMIZE" => S(OnOff(o.Optimize)),
            "HOURS" => N(o.Hours),
            "DECIMALS" => N(o.Decimals),
            "DATE" => S(o.Date.ToString().ToUpperInvariant()),
            "MARK" => S(o.Mark?.ToString() ?? ""),
            "POINT" => S(o.Point.ToString()),
            "SEPARATOR" => S(o.Separator.ToString()),
            "ENGINEBEHAVIOR" => N(o.EngineBehavior),
            "COLLATE" => S(o.Collate),
            "DEFAULT" => S(o.Default_.ToUpperInvariant()),
            "PATH" => S(string.Join(";", o.Path)),
            "DATABASE" => S(c.Rt.Session.CurrentDatabase?.Name ?? ""),
            "ORDER" => S(c.Rt.Session.Current.Order?.Name ?? ""),
            "FILTER" => S(c.Rt.Session.Current.Filter?.Source ?? ""),
            "DATASESSION" => N(c.Rt.Session.Id),
            "CONSOLE" => S(OnOff(o.Console)),
            "ASSERTS" => S(OnOff(o.Asserts)),
            "NULLDISPLAY" => S(o.NullDisplay ?? ".NULL."),
            "MEMOWIDTH" => N(o.MemoWidth),
            "FDOW" => N(o.Fdow),
            "FWEEK" => N(o.Fweek),
            "CURRENCY" when c.Has(1) => S(o.Values.GetValueOrDefault("CURRENCY_SYMBOL") ?? "$"),
            "ALTERNATE" => S(c.Has(1) ? c.Rt.AlternateFile : OnOff(c.Rt.AlternateOn)),
            "PROCEDURE" => S(string.Join(",", c.Rt.ProcedureFileNames)),
            "CLASSLIB" => S(string.Join(",", c.Rt.ClassLibraries.Select(u => u.File ?? u.Name).Select(f => f.ToUpperInvariant()))),
            _ when o.Values.TryGetValue(name, out var stored) => SetValue(name, stored),
            _ when SetDefaults.TryGetValue(name, out var d) => SetValue(name, d),
            _ => Value.EmptyString,
        };
    }

    /// <summary>VFP's defaults for the SET options that are stored rather than acted on.</summary>
    private static readonly Dictionary<string, string> SetDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MULTILOCKS"] = "OFF", ["REPROCESS"] = "0", ["STRICTDATE"] = "1", ["ESCAPE"] = "ON", ["BELL"] = "ON", ["NOTIFY"] = "ON",
        ["CONFIRM"] = "OFF", ["CARRY"] = "OFF", ["STATUS"] = "OFF", ["ECHO"] = "OFF", ["FIXED"] = "OFF", ["UDFPARMS"] = "VALUE",
        ["COMPATIBLE"] = "OFF", ["CURRENCY"] = "LEFT", ["LOCK"] = "OFF", ["REFRESH"] = "0", ["UNIQUE"] = "OFF", ["AUTOINCERROR"] = "ON",
        ["TABLEVALIDATE"] = "3", ["VARCHARMAPPING"] = "OFF", ["SYSFORMATS"] = "OFF", ["LOGERRORS"] = "ON", ["PRINTER"] = "OFF",
        ["DEVICE"] = "SCREEN", ["CLOCK"] = "OFF", ["CURSOR"] = "ON", ["TYPEAHEAD"] = "20", ["HELP"] = "ON", ["RESOURCE"] = "ON",
        ["BLOCKSIZE"] = "64", ["FULLPATH"] = "ON", ["CPDIALOG"] = "ON", ["SQLBUFFERING"] = "OFF", ["OLEOBJECT"] = "ON",
        ["TEXTMERGE"] = "OFF", ["DEBUG"] = "ON", ["ENGINEBEHAVIOR"] = "90", ["COLLATE"] = "MACHINE", ["DOHISTORY"] = "OFF",
        ["KEYCOMP"] = "WINDOWS", ["BROWSEIME"] = "ON", ["CPCOMPILE"] = "1252", ["TALK"] = "ON", ["SAFETY"] = "ON",
    };

    private static Value SetValue(string name, string text) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)
        && name is "REPROCESS" or "STRICTDATE" or "REFRESH" or "TABLEVALIDATE" or "TYPEAHEAD" or "BLOCKSIZE" or "CPCOMPILE"
            ? N(n) : S(text);


    private static Value Sys(CallContext c)
    {
        var n = c.Int(0);
        switch (n)
        {
            case 0: return S(Environment.MachineName + " # " + Environment.UserName);
            case 1: return S(Julian.FromDate(DateOnly.FromDateTime(DateTime.Today)).ToString());
            case 2: return S(((int)DateTime.Now.TimeOfDay.TotalSeconds).ToString());
            case 3:
            case 2015:
            {
                var ms = (long)(DateTime.UtcNow - new DateTime(2000, 1, 1)).TotalMilliseconds + Interlocked.Increment(ref _sysCounter);
                var s = ToBase36(ms);
                return S(n == 2015 ? "_" + s.PadLeft(9, '0')[^9..] : s.PadLeft(8, '0')[^8..]);
            }
            case 5: return S(Path.GetPathRoot(c.Options.Default_)?.TrimEnd('\\', '/') ?? "");
            case 10: return S(Formatter.DateToString(long.Parse(c.Str(1)), c.Options));
            case 11:
            {
                var v = c[1];
                var jd = v.Kind == ValueKind.Character ? Formatter.ParseDate(v.AsString, c.Options) : v.JulianDay;
                return S(jd.ToString());
            }
            case 16: return S(c.Rt.CurrentFrame.Unit?.File?.ToUpperInvariant() ?? c.Rt.CurrentFrame.Program);
            case 2003: return S(c.Options.Default_.ToUpperInvariant());
            case 2004: return S(AppContext.BaseDirectory.ToUpperInvariant());
            case 2018: return S(c.Rt.LastErrorMessage);
            case 2023: return S(Path.GetTempPath().TrimEnd('\\', '/'));
            case 3054: return S("0");
            case 3050: return S("0");
            case 1037: return Value.EmptyString;
            case 987: return Value.False;
            case 2019: return Value.EmptyString;
            case 6: return Value.EmptyString;
            case 12: return S("655360");
            case 7 or 2002 or 2005 or 3056: return Value.EmptyString;
            case 9: return S("Joe Pro " + Interpreter.VersionString);
            case 13: return S("READY");
            case 14: return CallByName(c.Rt, "KEY", c.Arg(1, N(1)), c.Has(2) ? c[2] : S(c.Rt.Session.Current.Alias));
            case 17: return S(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());
            case 21: return S(((int)CallByName(c.Rt, "TAGNO").AsNumber).ToString());
            case 22: return c.Has(1) ? CallByName(c.Rt, "ORDER", c[1]) : CallByName(c.Rt, "ORDER");
            case 100: return CallByName(c.Rt, "SET", S("CONSOLE"));
            case 101: return CallByName(c.Rt, "SET", S("DEVICE"));
            case 102: return CallByName(c.Rt, "SET", S("PRINTER"));
            case 103: return CallByName(c.Rt, "SET", S("TALK"));
            case 1016: return S((GC.GetTotalMemory(false) / 1024).ToString());
            case 1272:
            {
                var parts = new List<string>();
                for (var o = Obj(c, 1); o != null; o = o.Parent) parts.Insert(0, c.Rt.GetProperty(o, "Name").AsString.ToLowerInvariant());
                return S(string.Join(".", parts));
            }
            case 2000:
            {
                // SYS(2000, cSkeleton [, 1]): the first (or next) file name matching a skeleton.
                if (!(c.Has(2) && c.Int(2) == 1))
                {
                    var full = Path.Combine(c.Options.Default_, c.Str(1).Trim());
                    var dir = Path.GetDirectoryName(full) ?? c.Options.Default_;
                    var pattern = Path.GetFileName(full);
                    _sys2000 = new Queue<string>(Directory.Exists(dir) ? Directory.GetFiles(dir, pattern == "*.*" ? "*" : pattern).Select(f => Path.GetFileName(f).ToUpperInvariant()).Order() : []);
                }
                return S(_sys2000.Count > 0 ? _sys2000.Dequeue() : "");
            }
            case 2001: return CallByName(c.Rt, "SET", c[1], c.Arg(2, N(1)));
            case 2006: return S("Color/VGA");
            case 2007:
            {
                var bytes = Encoding.UTF8.GetBytes(c.Str(1));
                return S(c.Has(3) && c.Int(3) == 1 ? Crc32(bytes).ToString() : Crc16(bytes, c.Has(2) ? c.Int(2) : 0).ToString());
            }
            case 2011:
            {
                var wa = c.Rt.Session.Current;
                if (!wa.InUse) return S("");
                return S(wa.Exclusive ? "Exclusive" : wa.Session.Locks.IsLocked(wa, wa.RecNo) ? "Record Locked" : "Record Unlocked");
            }
            case 2012: return N(64);
            case 2014:
            {
                var from = c.Has(2) ? Path.GetFullPath(Path.Combine(c.Options.Default_, c.Str(2))) : c.Options.Default_;
                if (File.Exists(from)) from = Path.GetDirectoryName(from)!;
                return S(Path.GetRelativePath(from, Path.GetFullPath(Path.Combine(c.Options.Default_, c.Str(1)))).ToUpperInvariant());
            }
            case 2020:
            {
                try { return N(new DriveInfo(Path.GetPathRoot(c.Options.Default_) ?? "/").TotalSize); }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return N(0); }
            }
            case 2021: return CallByName(c.Rt, "FOR", c.Arg(1, N(1)));
            case 2029: return S(c.Rt.Session.Current.InUse ? "48" : "0");
            case 2335: return S("0");
            case 3051: return S("333");
            case 3052: return S("0");
            case 3055: return S("320");
            case 3099: return S("90");
            case 3101: return S("0");
            default:
                c.Rt.Notify($"SYS({n}) is not supported yet.");
                return Value.EmptyString;
        }
    }

    private static long _sysCounter;
    private static Queue<string> _sys2000 = new();

    /// <summary>The SYS() codes Sys() answers (the migration analyzer flags the others).</summary>
    public static readonly IReadOnlySet<int> SysCodes = new HashSet<int>
    {
        0, 1, 2, 3, 5, 6, 7, 9, 10, 11, 12, 13, 14, 16, 17, 21, 22, 100, 101, 102, 103, 987, 1016, 1037, 1272, 2000, 2001, 2002, 2003, 2004, 2005,
        2006, 2007, 2011, 2012, 2014, 2015, 2018, 2019, 2020, 2021, 2023, 2029, 2335, 3050, 3051, 3052, 3054, 3055, 3056, 3099, 3101,
    };

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }

    private static ushort Crc16(byte[] data, int seed)
    {
        ushort crc = (ushort)seed;
        foreach (var b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }

    private static string ToBase36(long v)
    {
        const string digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var sb = new StringBuilder();
        do { sb.Insert(0, digits[(int)(v % 36)]); v /= 36; } while (v > 0);
        return sb.ToString();
    }
}
