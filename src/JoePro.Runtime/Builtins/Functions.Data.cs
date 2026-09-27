using JoePro.Core;
using JoePro.Data;
using JoePro.Language;

namespace JoePro.Runtime.Builtins;

public static partial class Library
{
    private static int FieldArg(CallContext c, int i, WorkArea wa)
    {
        var v = c[i];
        if (v.Kind == ValueKind.Number) return (int)v.AsNumber - 1;
        var idx = wa.FieldIndex(v.AsString.Trim());
        if (idx < 0) throw VfpException.FieldNotFound(v.AsString);
        return idx;
    }

    private static void RegisterData()
    {
        Add("RECNO", c =>
        {
            if (!c.Has(0) && c.Rt.RecordContext is { } rec) return N(rec.RecNo);
            var wa = c.Area(0);
            return N(wa.InUse ? wa.RecNo : 0);
        });
        Add("RECCOUNT", c => { var wa = c.Area(0); return N(wa.InUse ? wa.RecordCount : 0); });
        Add("EOF", c => L(c.Area(0) is var wa && (!wa.InUse || wa.Eof)));
        Add("BOF", c => L(c.Area(0) is var wa && (!wa.InUse || wa.Bof)));
        Add("FOUND", c => L(c.Area(0).Found));
        Add("DELETED", c =>
        {
            if (c.Rt.SqlContext?.IsDeleted(c.Has(0) && c[0].Kind == ValueKind.Character ? c.Str(0).Trim() : null) is { } sqlDeleted) return L(sqlDeleted);
            if (!c.Has(0) && c.Rt.RecordContext is { } rec) return L(rec.Deleted);
            return L(c.Area(0).Deleted);
        });
        Add("ALIAS", c =>
        {
            var wa = c.Has(0) ? c.Rt.ResolveWorkArea(c[0]) : c.Rt.Session.Current;
            return S(wa.InUse ? wa.Alias : "");
        });
        Add("SELECT", c =>
        {
            if (!c.Has(0)) return N(c.Rt.Session.CurrentAreaNumber);
            var v = c[0];
            if (v.Kind == ValueKind.Number)
            {
                if ((int)v.AsNumber == 0) return N(c.Rt.Session.CurrentAreaNumber);
                for (int i = DataSession.MaxWorkAreas; i >= 1; i--)
                    if (!c.Rt.Session.OpenWorkAreas().Any(w => w.Number == i)) return N(i);
                return N(0);
            }
            return N(c.Rt.Session.FindAlias(v.AsString.Trim())?.Number ?? 0);
        });
        Add("USED", c =>
        {
            if (!c.Has(0)) return L(c.Rt.Session.Current.InUse);
            var v = c[0];
            if (v.Kind == ValueKind.Number) return L(c.Rt.Session.Area((int)v.AsNumber).InUse);
            return L(c.Rt.Session.FindAlias(v.AsString.Trim()) != null);
        });
        Add("DBF", c => { var wa = c.Area(0); return S(wa.InUse ? wa.Source.ToUpperInvariant() : ""); });
        Add("FIELD", c =>
        {
            var wa = c.Area(1);
            var n = c.Int(0);
            return S(wa.InUse && n >= 1 && n <= wa.Table.Fields.Count ? wa.Table.Fields[n - 1].Name : "");
        });
        Add("FCOUNT", c => { var wa = c.Area(0); return N(wa.InUse ? wa.Table.Fields.Count : 0); });
        Add("FSIZE", c =>
        {
            var wa = c.Area(1);
            var f = wa.Table.Fields[FieldArg(c, 0, wa)];
            return N(f.Width);
        });
        Add("RECSIZE", c => { var wa = c.Area(0); return N(wa.InUse ? 1 + wa.Table.Fields.Sum(f => f.Width) : 0); });
        Add("AFIELDS", c =>
        {
            var wa = c.Area(1);
            if (!wa.InUse) return N(0);
            var fields = wa.Table.Fields;
            var s = wa.Table.Schema;
            var arr = c.NewArray(0, fields.Count, 18);
            for (int i = 0; i < fields.Count; i++)
            {
                var f = fields[i];
                int r = i + 1;
                arr[r, 1] = S(f.Name);
                arr[r, 2] = S(f.Type.ToString());
                arr[r, 3] = N(f.Width);
                arr[r, 4] = N(f.Decimals);
                arr[r, 5] = L(f.Nullable);
                arr[r, 6] = L(f.Binary);
                arr[r, 7] = S(f.RuleExpr ?? "");
                arr[r, 8] = S(f.RuleText ?? "");
                arr[r, 9] = S(f.DefaultExpr ?? "");
                arr[r, 10] = S(s.RuleExpr ?? "");
                arr[r, 11] = S(s.RuleText ?? "");
                arr[r, 12] = S(wa.Table.Store.Kind == StoreKind.Database ? s.Name : "");
                arr[r, 13] = S(s.InsertTrigger ?? "");
                arr[r, 14] = S(s.UpdateTrigger ?? "");
                arr[r, 15] = S(s.DeleteTrigger ?? "");
                arr[r, 16] = S(s.Comment ?? "");
                arr[r, 17] = N(f.AutoIncNext ?? 0);
                arr[r, 18] = N(f.AutoIncNext.HasValue ? f.AutoIncStep : 0);
            }
            return N(fields.Count);
        });
        Add("ORDER", c => { var wa = c.Area(0); return S(wa.InUse && wa.Order != null ? wa.Order.Name : ""); });
        Add("TAG", c =>
        {
            // TAG([cIndexFile,] nTag [, alias])
            int ni = c.Has(0) && c[0].Kind == ValueKind.Character ? 1 : 0;
            var wa = c.Area(ni + 1);
            var n = c.Int(ni);
            var tags = wa.InUse ? wa.Table.Schema.Tags : [];
            return S(n >= 1 && n <= tags.Count ? tags[n - 1].Name : "");
        });
        Add("TAGCOUNT", c => { var wa = c.Area(1); return N(wa.InUse ? wa.Table.Schema.Tags.Count : 0); });
        Add("TAGNO", c =>
        {
            var wa = c.Area(2);
            var name = c.Has(0) ? c.Str(0) : wa.Order?.Name ?? "";
            return N(wa.Table.Schema.Tags.FindIndex(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) + 1);
        });
        Add("KEY", c =>
        {
            int ni = c.Has(0) && c[0].Kind == ValueKind.Character ? 1 : 0;
            var wa = c.Area(ni + 1);
            if (!c.Has(ni)) return S(wa.Order?.Expression ?? "");
            var n = c.Int(ni);
            var tags = wa.Table.Schema.Tags;
            return S(n >= 1 && n <= tags.Count ? tags[n - 1].Expression : "");
        });
        Add("FOR", c =>
        {
            var wa = c.Area(1);
            if (!c.Has(0)) return S(wa.Order?.ForExpression ?? "");
            var n = c.Int(0);
            var tags = wa.Table.Schema.Tags;
            return S(n >= 1 && n <= tags.Count ? tags[n - 1].ForExpression ?? "" : "");
        });
        Add("DESCENDING", c => { var wa = c.Area(1); return L(wa.Order?.Descending ?? false); });
        Add("CANDIDATE", c =>
        {
            var wa = c.Area(1);
            var tag = c.Has(0) ? wa.Table.Schema.Tags.ElementAtOrDefault(c.Int(0) - 1) : wa.Order;
            return L(tag?.Kind is TagKind.Candidate or TagKind.Primary);
        });
        Add("PRIMARY", c =>
        {
            var wa = c.Area(1);
            var tag = c.Has(0) ? wa.Table.Schema.Tags.ElementAtOrDefault(c.Int(0) - 1) : wa.Order;
            return L(tag?.Kind == TagKind.Primary);
        });
        Add("SEEK", c =>
        {
            var wa = c.Area(1);
            TagDef? tag = null;
            if (c.Has(2))
            {
                var t = c[2];
                tag = t.Kind == ValueKind.Number ? wa.Table.Schema.Tags[(int)t.AsNumber - 1] : wa.Table.Schema.FindTag(t.AsString) ?? throw new VfpException(1683, "Index tag is not found.");
            }
            return L(wa.Seek(c[0], tag));
        });
        Add("INDEXSEEK", c =>
        {
            var move = c.Bool(1, false);
            var wa = c.Area(2);
            TagDef? tag = c.Has(3) ? (c[3].Kind == ValueKind.Number ? wa.Table.Schema.Tags[c.Int(3) - 1] : wa.Table.Schema.FindTag(c.Str(3))) : null;
            if (move) return L(wa.Seek(c[0], tag));
            var saved = wa.Eof ? 0 : wa.RecNo;
            var found = wa.Seek(c[0], tag);
            if (saved > 0) wa.Go(saved); else if (!wa.Eof) { wa.GoBottom(); wa.Skip(); }
            return L(found);
        });
        Add("LOOKUP", c =>
        {
            // LOOKUP(ReturnField, eSearchExpression, SearchedField [, cTagName])
            string alias;
            string retField;
            switch (c.Exprs[0])
            {
                case MemberExpr { Target: NameExpr a } m: alias = a.Name; retField = m.Name; break;
                case AliasFieldExpr af: alias = af.Alias; retField = af.Field; break;
                case NameExpr n: alias = c.Rt.Session.Current.Alias; retField = n.Name; break;
                default: throw VfpException.InvalidArgument();
            }
            var wa = c.Rt.Session.ResolveAlias(alias);
            var key = c[1];
            var searched = c.Exprs[2] switch { MemberExpr m => m.Name, AliasFieldExpr af => af.Field, NameExpr n => n.Name, _ => ExprPrinter.Print(c.Exprs[2]) };
            var tag = c.Has(3) ? wa.Table.Schema.FindTag(c.Str(3)) : wa.Table.Schema.Tags.FirstOrDefault(t => t.Expression.Trim().Equals(searched, StringComparison.OrdinalIgnoreCase));
            bool found;
            if (tag != null) found = wa.Seek(key, tag);
            else
            {
                var fi = wa.FieldIndex(searched);
                wa.GoTop();
                found = false;
                while (!wa.Eof)
                {
                    if (VfpCompare.AreEqual(wa.Get(fi), key, c.Rt.CompareMode())) { found = true; break; }
                    wa.Skip();
                }
            }
            var ri = wa.FieldIndex(retField);
            return found ? wa.Get(ri) : wa.Table.Fields[ri].BlankValue();
        });
        Add(["RLOCK", "LOCK"], c =>
        {
            if (c.Has(0) && c[0].Kind == ValueKind.Character && !c.Has(1))
            {
                var waA = c.Rt.Session.ResolveAlias(c.Str(0));
                return L(waA.RLock());
            }
            var wa = c.Area(1);
            if (!c.Has(0)) return L(wa.RLock());
            var recs = c.Str(0).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return L(recs.All(r => wa.RLock(int.Parse(r))));
        });
        Add("FLOCK", c => L(c.Area(0).FLock()));
        Add("ISRLOCKED", c => { var wa = c.Area(1); return L(c.Rt.Session.Locks.IsLocked(wa, c.Int(0, wa.RecNo))); });
        Add("ISFLOCKED", c => L(false));
        Add("ISEXCLUSIVE", c => L(c.Area(0).Exclusive));
        Add("ISREADONLY", c => L(c.Area(0).ReadOnly));
        Add("TABLEUPDATE", c =>
        {
            var rows = c.Has(0) ? (c[0].Kind == ValueKind.Logical ? (c[0].AsBool ? 1 : 0) : c.Int(0)) : 0;
            var force = c.Bool(1, false);
            var wa = c.Area(2);
            return L(wa.TableUpdate(allRows: rows != 0, force: force));
        });
        Add("TABLEREVERT", c => N(c.Area(1).TableRevert(c.Bool(0, false))));
        Add("CURSORSETPROP", c =>
        {
            var prop = c.Str(0).ToUpperInvariant();
            var wa = c.Area(2);
            if (prop == "BUFFERING") wa.SetBuffering(c.Int(1));
            else Views.SetCursorProp(wa, prop, c.Has(1) ? c[1] : Value.False);
            return Value.True;
        });
        Add("CURSORGETPROP", c =>
        {
            var prop = c.Str(0).ToUpperInvariant();
            var wa = c.Area(1);
            if (Views.GetCursorProp(wa, prop) is { } viewProp) return viewProp;
            return prop switch
            {
                "BUFFERING" => N(wa.BufferMode),
                "SOURCETYPE" => N(wa.IsCursor ? 2 : 3),
                "SOURCENAME" => S(wa.Source),
                "DATABASE" => S(wa.Table.Store.Kind == StoreKind.Database ? wa.Table.Store.Path : ""),
                "READONLY" => L(wa.ReadOnly),
                "KEYFIELDLIST" => S(string.Join(",", wa.Table.Schema.Tags.Where(t => t.Kind is TagKind.Primary or TagKind.Candidate).Select(t => t.Expression))),
                _ => Value.EmptyString,
            };
        });
        Add("GETFLDSTATE", c =>
        {
            var wa = c.Area(1);
            var v = c[0];
            if (v.Kind == ValueKind.Number && v.AsNumber == -1)
                return S(string.Concat(Enumerable.Range(-1, wa.Table.Fields.Count + 1).Select(i => wa.GetFieldState(i).ToString())));
            if (v.Kind == ValueKind.Number && v.AsNumber == 0) return N(wa.GetFieldState(-1));
            return N(wa.GetFieldState(FieldArg(c, 0, wa)));
        });
        Add("OLDVAL", c => { var wa = c.Area(1); return wa.OldVal(OldValField(c, wa)); });
        Add("CURVAL", c => { var wa = c.Area(1); return wa.CurVal(OldValField(c, wa)); });
        Add("GETNEXTMODIFIED", c => N(c.Area(1).GetNextModified(c.Int(0))));
        Add("TXNLEVEL", c => N(c.Rt.Session.TransactionLevel));
        Add("DBC", c => S(c.Rt.Session.CurrentDatabase?.Path.ToUpperInvariant() ?? ""));
        Add("DBUSED", c => L(c.Rt.Session.OpenDatabases.Any(d => d.Name.Equals(Path.GetFileNameWithoutExtension(c.Str(0)), StringComparison.OrdinalIgnoreCase))));
        Add("DBGETPROP", c =>
        {
            var name = c.Str(0);
            var type = c.Str(1).ToUpperInvariant();
            var prop = c.Str(2).ToUpperInvariant();
            var db = c.Rt.Session.CurrentDatabase ?? throw new VfpException(1520, "No database is open or set as the current database.");
            if (type == "DATABASE") return prop == "VERSION" ? N(1) : Value.EmptyString;
            var dot = name.IndexOf('.');
            if (type == "CONNECTION") return ConnectionProp(db, name, prop);
            if (type == "VIEW" || (type == "FIELD" && dot > 0 && db.GetView(name[..dot]) != null))
                return ViewProp(db, name, type, prop);
            var t = db.OpenTable(dot > 0 ? name[..dot] : name, c.Rt);
            if (type == "FIELD" && dot > 0)
            {
                var f = t.Fields[t.Schema.FieldIndex(name[(dot + 1)..])];
                return prop switch
                {
                    "CAPTION" => S(f.Caption ?? ""), "DEFAULTVALUE" => S(f.DefaultExpr ?? ""), "RULEEXPRESSION" => S(f.RuleExpr ?? ""),
                    "RULETEXT" => S(f.RuleText ?? ""), "COMMENT" => S(f.Comment ?? ""), "FORMAT" => S(f.Format ?? ""), "INPUTMASK" => S(f.InputMask ?? ""),
                    "DISPLAYCLASS" => S(f.DisplayClass ?? ""), "DISPLAYCLASSLIBRARY" => S(f.DisplayClassLibrary ?? ""),
                    _ => Value.EmptyString,
                };
            }
            return prop switch
            {
                "PRIMARYKEY" => S(t.Schema.Tags.FirstOrDefault(x => x.Kind == TagKind.Primary)?.Name ?? ""),
                "RULEEXPRESSION" => S(t.Schema.RuleExpr ?? ""), "RULETEXT" => S(t.Schema.RuleText ?? ""),
                "INSERTTRIGGER" => S(t.Schema.InsertTrigger ?? ""), "UPDATETRIGGER" => S(t.Schema.UpdateTrigger ?? ""),
                "DELETETRIGGER" => S(t.Schema.DeleteTrigger ?? ""), "COMMENT" => S(t.Schema.Comment ?? ""),
                _ => Value.EmptyString,
            };
        });
        Add("AUSED", c =>
        {
            var areas = c.Rt.Session.OpenWorkAreas().ToList();
            if (areas.Count == 0) return N(0);
            var arr = c.NewArray(0, areas.Count, 2);
            for (int i = 0; i < areas.Count; i++) { arr[i + 1, 1] = S(areas[i].Alias); arr[i + 1, 2] = N(areas[i].Number); }
            return N(areas.Count);
        });
        Add("CPDBF", _ => N(0));
        Add("CPCURRENT", _ => N(1252));
        Add("REQUERY", c => N(Views.Requery(c.Rt, c.Area(0))));
        Add("DBSETPROP", c =>
        {
            var name = c.Str(0).Trim();
            var type = c.Str(1).Trim().ToUpperInvariant();
            var prop = c.Str(2).Trim();
            var value = c[3];
            var db = c.Rt.Session.CurrentDatabase ?? throw new VfpException(1520, "No database is open or set as the current database.");
            var dot = name.IndexOf('.');
            switch (type)
            {
                case "VIEW":
                {
                    var v = db.GetView(name) ?? throw new VfpException(1562, $"Cannot find object {name.ToUpperInvariant()} in the database.");
                    if (prop.Equals("SQL", StringComparison.OrdinalIgnoreCase) || prop.Equals("SourceType", StringComparison.OrdinalIgnoreCase) || prop.Equals("ConnectName", StringComparison.OrdinalIgnoreCase))
                        throw new VfpException(1559, $"Property {prop.ToUpperInvariant()} is read-only.");
                    if (!ViewDefinition.ViewDefaults.ContainsKey(prop)) throw new VfpException(1559, $"Property {prop.ToUpperInvariant()} is invalid.");
                    v.Set(prop, value);
                    db.SaveView(v);
                    return Value.True;
                }
                case "FIELD" when dot > 0 && db.GetView(name[..dot]) is { } view:
                {
                    var field = name[(dot + 1)..];
                    if (!ViewDefinition.FieldDefaults.ContainsKey(prop)) throw new VfpException(1559, $"Property {prop.ToUpperInvariant()} is invalid.");
                    view.SetField(field, prop, value);
                    db.SaveView(view);
                    return Value.True;
                }
                case "CONNECTION":
                {
                    var conn = db.GetConnection(name) ?? throw new VfpException(1562, $"Cannot find object {name.ToUpperInvariant()} in the database.");
                    switch (prop.ToUpperInvariant())
                    {
                        case "CONNECTSTRING": conn.ConnectString = value.AsString; break;
                        case "DATASOURCE": conn.DataSource = value.AsString; break;
                        case "USERID": conn.UserId = value.AsString; break;
                        case "PASSWORD": conn.Password = value.AsString; break;
                        case "DATABASE": conn.Database = value.AsString; break;
                        default: conn.Properties[prop] = PropValue.Encode(value); break;
                    }
                    db.SaveConnection(conn);
                    return Value.True;
                }
                case "TABLE":
                {
                    if (!prop.Equals("Comment", StringComparison.OrdinalIgnoreCase))
                        throw new VfpException(1559, $"Property {prop.ToUpperInvariant()} is read-only (set rules with ALTER TABLE and triggers with CREATE TRIGGER).");
                    var t = db.OpenTable(name, c.Rt);
                    var schema = new TableSchema(t.Schema.Name, t.Schema.Fields)
                    {
                        Comment = value.AsString, RuleExpr = t.Schema.RuleExpr, RuleText = t.Schema.RuleText,
                        InsertTrigger = t.Schema.InsertTrigger, UpdateTrigger = t.Schema.UpdateTrigger, DeleteTrigger = t.Schema.DeleteTrigger,
                    };
                    db.UpdateTableProperties(schema);
                    return Value.True;
                }
                case "FIELD" when dot > 0:
                {
                    var t = db.OpenTable(name[..dot], c.Rt);
                    var i = t.Schema.FieldIndex(name[(dot + 1)..]);
                    if (i < 0) throw VfpException.FieldNotFound(name[(dot + 1)..]);
                    var text = value.AsString;
                    var f = t.Fields[i];
                    f = prop.ToUpperInvariant() switch
                    {
                        "CAPTION" => f with { Caption = text },
                        "COMMENT" => f with { Comment = text },
                        "FORMAT" => f with { Format = text },
                        "INPUTMASK" => f with { InputMask = text },
                        "DISPLAYCLASS" => f with { DisplayClass = text },
                        "DISPLAYCLASSLIBRARY" => f with { DisplayClassLibrary = text },
                        "DEFAULTVALUE" or "RULEEXPRESSION" or "RULETEXT" =>
                            throw new VfpException(1559, $"Property {prop.ToUpperInvariant()} is read-only (set it with ALTER TABLE … ALTER COLUMN).") ,
                        _ => throw new VfpException(1559, $"Property {prop.ToUpperInvariant()} is invalid."),
                    };
                    var fields = t.Fields.ToList();
                    fields[i] = f;
                    var schema = new TableSchema(t.Schema.Name, fields)
                    {
                        Comment = t.Schema.Comment, RuleExpr = t.Schema.RuleExpr, RuleText = t.Schema.RuleText,
                        InsertTrigger = t.Schema.InsertTrigger, UpdateTrigger = t.Schema.UpdateTrigger, DeleteTrigger = t.Schema.DeleteTrigger,
                    };
                    db.UpdateTableProperties(schema);
                    return Value.True;
                }
                default:
                    throw VfpException.NotSupported($"DBSETPROP() for {type.ToLowerInvariant()} properties");
            }
        });
        Add("ADBOBJECTS", c =>
        {
            var db = c.Rt.Session.CurrentDatabase ?? throw new VfpException(1520, "No database is open or set as the current database.");
            var kind = c.Str(1).Trim().ToUpperInvariant();
            if (kind == "RELATION")
            {
                var rels = db.Relations();
                if (rels.Count == 0) return N(0);
                var ra = c.NewArray(0, rels.Count, 5);
                for (int i = 0; i < rels.Count; i++)
                {
                    ra[i + 1, 1] = S(rels[i].ChildTable.ToUpperInvariant()); ra[i + 1, 2] = S(rels[i].ParentTable.ToUpperInvariant());
                    ra[i + 1, 3] = S(rels[i].ChildTag.ToUpperInvariant()); ra[i + 1, 4] = S(rels[i].ParentTag.ToUpperInvariant());
                    ra[i + 1, 5] = S(rels[i].RiCode);
                }
                return N(rels.Count);
            }
            IReadOnlyList<string> names = kind switch
            {
                "TABLE" => db.TableNames(),
                "VIEW" => db.ObjectNames(DbObjectStore.ViewKind),
                "CONNECTION" => db.ObjectNames(DbObjectStore.ConnectionKind),
                _ => throw VfpException.InvalidArgument(),
            };
            if (names.Count == 0) return N(0);
            var arr = c.NewArray(0, names.Count, 0);
            for (int i = 0; i < names.Count; i++) arr[i + 1] = S(names[i].ToUpperInvariant());
            return N(names.Count);
        });
        Add("INDBC", c =>
        {
            var db = c.Rt.Session.CurrentDatabase ?? throw new VfpException(1520, "No database is open or set as the current database.");
            var name = c.Str(0).Trim();
            return c.Str(1).Trim().ToUpperInvariant() switch
            {
                "TABLE" => L(db.HasTable(name)),
                "VIEW" => L(db.HasObject(DbObjectStore.ViewKind, name)),
                "CONNECTION" => L(db.HasObject(DbObjectStore.ConnectionKind, name)),
                "FIELD" or "INDEX" => L(db.TableNames().Select(t => db.OpenTable(t, c.Rt))
                    .Any(t => c.Str(1).Trim().Equals("FIELD", StringComparison.OrdinalIgnoreCase) ? t.Schema.FieldIndex(name) >= 0 : t.Schema.Tags.Any(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))),
                _ => throw VfpException.InvalidArgument(),
            };
        });
        Add("REFRESH", _ => N(0));
    }

    private static Value ViewProp(Store db, string name, string type, string prop)
    {
        var dot = name.IndexOf('.');
        var view = db.GetView(type == "FIELD" ? name[..dot] : name) ?? throw new VfpException(1562, $"Cannot find object {name.ToUpperInvariant()} in the database.");
        if (type == "FIELD") return view.GetField(name[(dot + 1)..], prop);
        return prop switch
        {
            "SQL" => S(view.Sql),
            "SOURCETYPE" => N(view.Remote ? 2 : 1),
            "CONNECTNAME" => S(view.Connection ?? ""),
            _ => view.Get(prop),
        };
    }

    private static Value ConnectionProp(Store db, string name, string prop)
    {
        var conn = db.GetConnection(name) ?? throw new VfpException(1562, $"Cannot find object {name.ToUpperInvariant()} in the database.");
        return prop switch
        {
            "CONNECTSTRING" => S(conn.ConnectString ?? ""),
            "DATASOURCE" => S(conn.DataSource ?? ""),
            "USERID" => S(conn.UserId ?? ""),
            "PASSWORD" => S(conn.Password ?? ""),
            "DATABASE" => S(conn.Database ?? ""),
            _ => conn.Properties.TryGetValue(prop, out var v) ? PropValue.Decode(v)
                : prop switch
                {
                    "ASYNCHRONOUS" or "DISPWARNINGS" => Value.False,
                    "BATCHMODE" => Value.True,
                    "CONNECTTIMEOUT" => N(15),
                    "DISPLOGIN" or "TRANSACTIONS" => N(1),
                    "IDLETIMEOUT" or "QUERYTIMEOUT" => N(0),
                    "PACKETSIZE" => N(4096),
                    "WAITTIME" => N(100),
                    _ => Value.EmptyString,
                },
        };
    }

    private static int OldValField(CallContext c, WorkArea wa)
    {
        var name = c[0].Kind == ValueKind.Character ? c.Str(0).Trim() : throw VfpException.InvalidArgument();
        var dot = name.IndexOf('.');
        if (dot > 0) name = name[(dot + 1)..];
        var i = wa.FieldIndex(name);
        if (i < 0) throw VfpException.FieldNotFound(name);
        return i;
    }
}
