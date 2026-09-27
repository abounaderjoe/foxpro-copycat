using System.Globalization;
using System.Text;
using JoePro.Core;
using JoePro.Data;
using JoePro.Legacy.Formats;

namespace JoePro.Runtime.Sync;

/// <summary>
/// The legacy VFP side. Changes are read from the .DBF files (by snapshot comparison, or from the _joesync_log table
/// the installed DBC triggers write). Changes are written by VFP itself: sync drops batch files into the legacy folder
/// (_joesync_inbox_n.dbf + .ready) and the generated agent program (joesync_agent.prg, run in VFP 9) applies them with
/// VFP's own locking, index maintenance, rules and triggers, and reports each row in _joesync_done_n.txt. Joe Pro never
/// writes live DBF or CDX bytes.
/// </summary>
public sealed class LegacySide(SyncConfig config)
{
    public const string LogTable = "_joesync_log";
    public const string InboxPrefix = "_joesync_inbox_";
    public const string DonePrefix = "_joesync_done_";
    public const string Origin = "JOE";

    public string Folder => config.Legacy;

    public string TablePath(SyncTable t) =>
        DataSession.FindIgnoringCase(Path.Combine(config.Legacy, t.LegacyFile ?? t.Name + ".dbf"))
        ?? throw new VfpException(ErrorCodes.FileDoesNotExist, $"The legacy table {t.LegacyFile ?? t.Name + ".dbf"} is not in {config.Legacy}.");

    /// <summary>Every live (not deleted) row of a legacy table, by key.</summary>
    public Dictionary<string, SyncRow> ReadTable(SyncTable t)
    {
        using var dbf = DbfTable.Open(TablePath(t));
        var names = dbf.Fields.Select(f => f.Name.ToUpperInvariant()).ToList();
        var key = names.IndexOf(t.Key.ToUpperInvariant());
        if (key < 0) throw new VfpException(ErrorCodes.InvalidArgument, $"{t.Name} has no key field {t.Key} on the legacy side.");
        var rows = new Dictionary<string, SyncRow>();
        foreach (var r in dbf.Records())
        {
            if (r.Deleted) continue;
            var row = new SyncRow();
            for (int i = 0; i < names.Count; i++) row[names[i]] = r.Values[i];
            rows[SyncRow.KeyText(r.Values[key])] = row;
        }
        return rows;
    }

    public IReadOnlyList<string> FieldNames(SyncTable t)
    {
        using var dbf = DbfTable.Open(TablePath(t));
        return dbf.Fields.Select(f => f.Name.ToUpperInvariant()).ToList();
    }

    /// <summary>Trigger capture: the log entries after <paramref name="afterId"/> not written by the agent (table, key, id).</summary>
    public List<(long Id, string Table, string Key)> ReadLog(long afterId)
    {
        var path = DataSession.FindIgnoringCase(Path.Combine(config.Legacy, LogTable + ".dbf"));
        if (path == null) throw new VfpException(ErrorCodes.FileDoesNotExist, $"{LogTable}.dbf is not in {config.Legacy}; run joesync_install.prg in VFP first.");
        using var dbf = DbfTable.Open(path);
        var names = dbf.Fields.Select(f => f.Name.ToUpperInvariant()).ToList();
        int iId = names.IndexOf("ID"), iTbl = names.IndexOf("TBL"), iKey = names.IndexOf("KEYVAL"), iOrigin = names.IndexOf("ORIGIN");
        var list = new List<(long, string, string)>();
        foreach (var r in dbf.Records())
        {
            if (r.Deleted) continue;
            var id = (long)r.Values[iId].AsNumber;
            if (id <= afterId) continue;
            if (iOrigin >= 0 && r.Values[iOrigin].AsString.Trim().Equals(Origin, StringComparison.OrdinalIgnoreCase)) { list.Add((id, "", "")); continue; }
            list.Add((id, r.Values[iTbl].AsString.Trim(), r.Values[iKey].AsString.Trim()));
        }
        return list;
    }

    // ---- Batches for the agent ---------------------------------------------------------------------

    /// <summary>
    /// A change for the agent. <see cref="Expected"/> is the legacy row as sync last saw it (null: no row); the agent
    /// applies the change only if the row still has those values, and answers STALE otherwise (a user changed it).
    /// </summary>
    public sealed record Change(int Id, string Table, string KeyField, string Key, char Op, SyncRow? Values, SyncRow? Expected);

    /// <summary>What the agent reported for a row: applied, stale (changed underneath), or failed with a message.</summary>
    public sealed record Result(bool Applied, bool Stale, string? Error);

    /// <summary>Writes a batch for the agent: the .dbf (and .fpt), then the .ready marker it waits for.</summary>
    public void WriteBatch(long batch, IReadOnlyList<Change> changes)
    {
        var name = Path.Combine(config.Legacy, $"{InboxPrefix}{batch:D8}");
        FieldDef[] fields = [new("ID", 'N', 10), new("TBL", 'C', 60), new("KEYFLD", 'C', 60), new("KEYVAL", 'M'), new("OP", 'C', 1), new("DATA", 'M'), new("EXPECT", 'M'), new("EXISTS", 'L')];
        DbfWriter.Write(name + ".dbf", fields, changes.Select(c => (false, new Value[]
        {
            Value.Number(c.Id, 0), Value.String(c.Table), Value.String(c.KeyField), Value.String(c.Key), Value.String(c.Op.ToString()), Value.String(Encode(c.Values)),
            Value.String(Encode(c.Expected)), Value.Logical(c.Expected != null),
        })));
        File.WriteAllText(name + ".ready", batch.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The agent's result for each row of a batch, or null while it has not processed the batch.</summary>
    public Dictionary<int, Result>? ReadDone(long batch)
    {
        var path = Path.Combine(config.Legacy, $"{DonePrefix}{batch:D8}.txt");
        if (!File.Exists(path)) return null;
        var result = new Dictionary<int, Result>();
        foreach (var line in File.ReadAllLines(path))
        {
            var parts = line.Split('|', 3);
            if (parts.Length < 2 || !int.TryParse(parts[0], out var id)) continue;
            var status = parts[1].Trim().ToUpperInvariant();
            result[id] = status switch
            {
                "OK" => new Result(true, false, null),
                "STALE" => new Result(false, true, null),
                _ => new Result(false, false, parts.Length > 2 ? parts[2].Trim() : "failed"),
            };
        }
        return result;
    }

    public void RemoveDone(long batch) => File.Delete(Path.Combine(config.Legacy, $"{DonePrefix}{batch:D8}.txt"));

    /// <summary>field|type|value lines: C text (escaped), N number, D yyyymmdd, T yyyymmddhhmmss, L T/F, B base64, 0 null.</summary>
    public static string Encode(SyncRow? row)
    {
        if (row == null) return "";
        var sb = new StringBuilder();
        foreach (var (field, v) in row.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var (type, text) = v.Kind switch
            {
                ValueKind.Null => ("0", ""),
                ValueKind.Character => ("C", v.AsString.TrimEnd(' ').Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n")),
                ValueKind.Number => ("N", v.AsNumber.ToString("R", CultureInfo.InvariantCulture)),
                ValueKind.Currency => ("N", v.AsCurrency.ToString(CultureInfo.InvariantCulture)),
                ValueKind.Logical => ("L", v.AsBool ? "T" : "F"),
                ValueKind.Date => ("D", v.JulianDay == 0 ? "" : Julian.ToDate(v.JulianDay).ToString("yyyyMMdd", CultureInfo.InvariantCulture)),
                ValueKind.DateTime => ("T", v.JulianMs == 0 ? "" : Julian.ToDateTime(v.JulianMs).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)),
                ValueKind.Binary => ("B", Convert.ToBase64String(v.AsBinary)),
                _ => ("0", ""),
            };
            sb.Append(field.ToUpperInvariant()).Append('|').Append(type).Append('|').Append(text).Append('\n');
        }
        return sb.ToString();
    }

    public static SyncRow Decode(string data, IReadOnlyDictionary<string, FieldDef>? fields = null)
    {
        var row = new SyncRow();
        foreach (var line in data.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.Split('|', 3);
            if (p.Length < 3) continue;
            var text = p[2];
            row[p[0]] = p[1] switch
            {
                "C" => Value.String(text.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\\\", "\\")),
                "N" => Value.Number(double.Parse(text, CultureInfo.InvariantCulture)),
                "L" => Value.Logical(text == "T"),
                "D" => text.Length == 0 ? Value.EmptyDate : Value.DateOf(DateOnly.ParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture)),
                "T" => text.Length == 0 ? Value.EmptyDateTime : Value.DateTimeOf(DateTime.ParseExact(text, "yyyyMMddHHmmss", CultureInfo.InvariantCulture)),
                "B" => Value.Binary(Convert.FromBase64String(text)),
                _ => Value.Null,
            };
        }
        return row;
    }

    // ---- Programs run in VFP -----------------------------------------------------------------------

    private const string KeyFunction = """
        FUNCTION JoeSyncKey(tuValue)
           * The key as text, the same way Joe Pro writes it.
           DO CASE
           CASE VARTYPE(tuValue) $ "CM"
              RETURN ALLTRIM(tuValue)
           CASE VARTYPE(tuValue) $ "NIYF"
              IF tuValue = INT(tuValue)
                 RETURN LTRIM(STR(tuValue, 20, 0))
              ENDIF
              RETURN RTRIM(RTRIM(LTRIM(STR(tuValue, 30, 10)), "0"), ".")
           OTHERWISE
              RETURN TRANSFORM(tuValue)
           ENDCASE
        ENDFUNC
        """;

    /// <summary>joesync_agent.prg: applies Joe Pro's changes in VFP 9 (DO joesync_agent WITH "folder" [, .T. for one pass]).</summary>
    public static string AgentProgram() => """
        * joesync_agent.prg - generated by Joe Pro sync. Run it in Visual FoxPro 9 on a machine that sees the data folder:
        *    DO joesync_agent WITH "\\server\app\data"         && keeps running; press Esc to stop
        *    DO joesync_agent WITH "\\server\app\data", .T.    && one pass
        * It applies the change batches Joe Pro writes (_joesync_inbox_n.dbf) through VFP, so VFP's own locking, indexes,
        * rules and triggers are used, and writes _joesync_done_n.txt with the result of each row.
        LPARAMETERS tcFolder, tlOnce
        LOCAL lnFiles, lnI, laReady[1]
        PUBLIC gcJoeSyncOrigin
        SET DELETED ON
        SET EXCLUSIVE OFF
        SET REPROCESS TO 5 SECONDS
        tcFolder = ADDBS(tcFolder)
        DO WHILE .T.
           gcJoeSyncOrigin = "JOE"
           lnFiles = ADIR(laReady, tcFolder + "_joesync_inbox_*.ready")
           IF lnFiles > 0
              = ASORT(laReady)
              FOR lnI = 1 TO lnFiles
                 DO JoeSyncBatch WITH tcFolder + JUSTSTEM(laReady[lnI, 1]), tcFolder
              ENDFOR
           ENDIF
           gcJoeSyncOrigin = ""
           IF tlOnce
              EXIT
           ENDIF
           = INKEY(2)
           IF LASTKEY() = 27
              EXIT
           ENDIF
        ENDDO
        RELEASE gcJoeSyncOrigin

        PROCEDURE JoeSyncBatch(tcBase, tcFolder)
           LOCAL lcDone
           lcDone = ""
           SELECT 0
           USE (tcBase + ".dbf") ALIAS joesyncin SHARED NOUPDATE
           SCAN
              lcDone = lcDone + TRANSFORM(joesyncin.id) + "|" + JoeSyncRow(tcFolder) + CHR(13) + CHR(10)
           ENDSCAN
           USE IN joesyncin
           = STRTOFILE(lcDone, STRTRAN(tcBase, "_joesync_inbox_", "_joesync_done_") + ".txt")
           ERASE (tcBase + ".dbf")
           ERASE (tcBase + ".fpt")
           ERASE (tcBase + ".ready")
        ENDPROC

        FUNCTION JoeSyncRow(tcFolder)
           LOCAL lcTable, lcKeyField, lcKey, lcOp, lnLines, lnI, lcLine, lnBar, lcField, lcType, lcValue, lcError, loErr, laLines[1]
           lcTable = ALLTRIM(joesyncin.tbl)
           lcKeyField = ALLTRIM(joesyncin.keyfld)
           lcKey = joesyncin.keyval
           lcOp = joesyncin.op
           lcError = ""
           TRY
              IF !USED(lcTable)
                 USE (tcFolder + lcTable) IN 0 SHARED AGAIN ALIAS (lcTable)
              ENDIF
              SELECT (lcTable)
              LOCATE FOR JoeSyncKey(EVALUATE(lcKeyField)) == lcKey
              * Apply only to the row sync saw: if a user changed it meanwhile, say so and let sync merge again.
              IF FOUND() <> joesyncin.exists OR (FOUND() AND !JoeSyncSame(lcTable, joesyncin.expect))
                 RETURN "STALE"
              ENDIF
              IF lcOp = "D"
                 IF FOUND()
                    IF RLOCK()
                       DELETE
                       UNLOCK
                    ELSE
                       lcError = "The record is locked."
                    ENDIF
                 ENDIF
              ELSE
                 IF !FOUND()
                    APPEND BLANK
                 ENDIF
                 IF RLOCK()
                    lnLines = ALINES(laLines, joesyncin.data, 1, CHR(10))
                    FOR lnI = 1 TO lnLines
                       lcLine = laLines[lnI]
                       lnBar = AT("|", lcLine)
                       lcField = LEFT(lcLine, lnBar - 1)
                       lcType = SUBSTR(lcLine, lnBar + 1, 1)
                       lcValue = SUBSTR(lcLine, lnBar + 3)
                       IF TYPE(lcTable + "." + lcField) <> "U"
                          REPLACE (lcField) WITH JoeSyncValue(lcType, lcValue)
                       ENDIF
                    ENDFOR
                    UNLOCK
                 ELSE
                    lcError = "The record is locked."
                 ENDIF
              ENDIF
           CATCH TO loErr
              lcError = loErr.Message
              UNLOCK
           ENDTRY
           IF EMPTY(lcError)
              RETURN "OK"
           ENDIF
           RETURN "ERR|" + CHRTRAN(lcError, CHR(13) + CHR(10) + "|", "   ")
        ENDFUNC

        FUNCTION JoeSyncSame(tcTable, tcExpect)
           * Whether the current record still has the values in tcExpect (field|type|value lines).
           LOCAL lnLines, lnI, lcLine, lnBar, lcField, lcType, luNow, luThen, laLines[1]
           lnLines = ALINES(laLines, tcExpect, 1, CHR(10))
           FOR lnI = 1 TO lnLines
              lcLine = laLines[lnI]
              lnBar = AT("|", lcLine)
              lcField = LEFT(lcLine, lnBar - 1)
              lcType = SUBSTR(lcLine, lnBar + 1, 1)
              IF TYPE(tcTable + "." + lcField) = "U"
                 LOOP
              ENDIF
              luNow = EVALUATE(tcTable + "." + lcField)
              luThen = JoeSyncValue(lcType, SUBSTR(lcLine, lnBar + 3))
              DO CASE
              CASE ISNULL(luNow) OR ISNULL(luThen)
                 IF ISNULL(luNow) <> ISNULL(luThen)
                    RETURN .F.
                 ENDIF
              CASE VARTYPE(luNow) $ "CM"
                 IF !(RTRIM(luNow) == RTRIM(luThen))
                    RETURN .F.
                 ENDIF
              OTHERWISE
                 IF luNow <> luThen
                    RETURN .F.
                 ENDIF
              ENDCASE
           ENDFOR
           RETURN .T.
        ENDFUNC

        FUNCTION JoeSyncValue(tcType, tcValue)
           DO CASE
           CASE tcType = "C"
              RETURN STRTRAN(STRTRAN(STRTRAN(tcValue, "\n", CHR(10)), "\r", CHR(13)), "\\", "\")
           CASE tcType = "N"
              RETURN VAL(tcValue)
           CASE tcType = "L"
              RETURN tcValue = "T"
           CASE tcType = "D"
              IF EMPTY(tcValue)
                 RETURN {}
              ENDIF
              RETURN DATE(VAL(LEFT(tcValue, 4)), VAL(SUBSTR(tcValue, 5, 2)), VAL(SUBSTR(tcValue, 7, 2)))
           CASE tcType = "T"
              IF EMPTY(tcValue)
                 RETURN {/:}
              ENDIF
              RETURN DATETIME(VAL(LEFT(tcValue, 4)), VAL(SUBSTR(tcValue, 5, 2)), VAL(SUBSTR(tcValue, 7, 2)), VAL(SUBSTR(tcValue, 9, 2)), VAL(SUBSTR(tcValue, 11, 2)), VAL(SUBSTR(tcValue, 13, 2)))
           CASE tcType = "B"
              RETURN STRCONV(tcValue, 14)
           OTHERWISE
              RETURN .NULL.
           ENDCASE
        ENDFUNC

        """ + KeyFunction + "\n";

    /// <summary>joesync_install.prg: adds the logging triggers to the legacy DBC, chained to the triggers it already has.</summary>
    public static string InstallProgram(SyncConfig config)
    {
        var dbc = config.LegacyDatabase ?? throw new VfpException(ErrorCodes.InvalidArgument, "Trigger capture needs the legacy database (legacyDatabase in joesync.json).");
        var sb = new StringBuilder();
        sb.Append($$"""
            * joesync_install.prg - generated by Joe Pro sync. Run it once in Visual FoxPro 9 while no one uses the database:
            *    DO joesync_install
            * It creates _joesync_log.dbf next to the database, adds the __joesync_log stored procedure, and chains a logging
            * call in front of each synced table's insert, update and delete triggers. joesync_uninstall.prg undoes it.
            LOCAL lcFolder, lcOld, lcExpr, lcSaved
            OPEN DATABASE "{{dbc}}" EXCLUSIVE
            lcFolder = ADDBS(JUSTPATH(DBC()))
            IF !FILE(lcFolder + "_joesync_log.dbf")
               CREATE TABLE (lcFolder + "_joesync_log") FREE (id I AUTOINC, tbl C(60), keyval C(100), op C(1), ts T, origin C(10))
               USE
            ENDIF
            TEXT TO lcProc NOSHOW
            PROCEDURE __joesync_log(tcTable, tcOp, tcKeyField)
               LOCAL lnSelect, lcOrigin, lcKey
               lnSelect = SELECT()
               lcKey = JoeSyncKey(EVALUATE(tcKeyField))
               lcOrigin = IIF(TYPE("gcJoeSyncOrigin") = "C" AND !EMPTY(gcJoeSyncOrigin), gcJoeSyncOrigin, "LEGACY")
               INSERT INTO (ADDBS(JUSTPATH(DBC())) + "_joesync_log") (tbl, keyval, op, ts, origin) VALUES (tcTable, lcKey, tcOp, DATETIME(), lcOrigin)
               SELECT (lnSelect)
               RETURN .T.
            ENDPROC

            """);
        sb.Append(KeyFunction).Append('\n');
        sb.Append("""
            ENDTEXT
            = STRTOFILE(lcProc, lcFolder + "joesync_procs.prg")
            APPEND PROCEDURES FROM (lcFolder + "joesync_procs.prg")
            ERASE (lcFolder + "joesync_procs.prg")
            lcSaved = ""

            """);
        foreach (var t in config.Tables)
            foreach (var (kind, op) in new[] { ("Insert", "I"), ("Update", "U"), ("Delete", "D") })
            {
                var call = $"__joesync_log(\"{t.Name.ToLowerInvariant()}\", \"{op}\", \"{t.Key.ToLowerInvariant()}\")";
                sb.Append($$"""
                    lcOld = DBGETPROP("{{t.Name.ToLowerInvariant()}}", "TABLE", "{{kind}}Trigger")
                    lcSaved = lcSaved + "{{t.Name.ToLowerInvariant()}}|{{kind.ToUpperInvariant()}}|" + lcOld + CHR(13) + CHR(10)
                    IF !("__joesync_log" $ lcOld)
                       lcExpr = '{{call}}' + IIF(EMPTY(lcOld), "", " AND (" + lcOld + ")")
                       CREATE TRIGGER ON {{t.Name.ToLowerInvariant()}} FOR {{kind.ToUpperInvariant()}} AS &lcExpr
                    ENDIF

                    """);
            }
        sb.Append("""
            IF !FILE(lcFolder + "_joesync_triggers.txt")
               = STRTOFILE(lcSaved, lcFolder + "_joesync_triggers.txt")
            ENDIF
            CLOSE DATABASES
            ? "Joe Pro sync triggers installed."

            """);
        return sb.ToString();
    }

    /// <summary>joesync_uninstall.prg (the cutover tool's last step): puts the original triggers back.</summary>
    public static string UninstallProgram(SyncConfig config)
    {
        var dbc = config.LegacyDatabase ?? "";
        return $$"""
            * joesync_uninstall.prg - generated by Joe Pro sync. Run it in Visual FoxPro 9 after cutover, while no one uses
            * the database: it restores the triggers the tables had before joesync_install.prg.
            LOCAL lcFolder, lnLines, lnI, lcLine, lcTable, lcKind, lcOld, laLines[1]
            OPEN DATABASE "{{dbc}}" EXCLUSIVE
            lcFolder = ADDBS(JUSTPATH(DBC()))
            IF FILE(lcFolder + "_joesync_triggers.txt")
               lnLines = ALINES(laLines, FILETOSTR(lcFolder + "_joesync_triggers.txt"))
               FOR lnI = 1 TO lnLines
                  lcLine = laLines[lnI]
                  lcTable = GETWORDNUM(lcLine, 1, "|")
                  lcKind = GETWORDNUM(lcLine, 2, "|")
                  lcOld = SUBSTR(lcLine, LEN(lcTable) + LEN(lcKind) + 3)
                  DO CASE
                  CASE lcKind = "INSERT" AND EMPTY(lcOld)
                     DELETE TRIGGER ON (lcTable) FOR INSERT
                  CASE lcKind = "INSERT"
                     CREATE TRIGGER ON (lcTable) FOR INSERT AS &lcOld
                  CASE lcKind = "UPDATE" AND EMPTY(lcOld)
                     DELETE TRIGGER ON (lcTable) FOR UPDATE
                  CASE lcKind = "UPDATE"
                     CREATE TRIGGER ON (lcTable) FOR UPDATE AS &lcOld
                  CASE EMPTY(lcOld)
                     DELETE TRIGGER ON (lcTable) FOR DELETE
                  OTHERWISE
                     CREATE TRIGGER ON (lcTable) FOR DELETE AS &lcOld
                  ENDCASE
               ENDFOR
               ERASE (lcFolder + "_joesync_triggers.txt")
            ENDIF
            CLOSE DATABASES
            ? "Joe Pro sync triggers removed. The legacy database is back to its own triggers."

            """;
    }
}
