using JoePro.Core;
using JoePro.Runtime;

namespace JoePro.Migration;

/// <summary>Connects the migration layer to the runtime's IMPORT command.</summary>
public static class MigrationCommands
{
    public static void Register() => Interpreter.ImportHandler = (rt, source, target, database) => ImportFiles(rt, source, target);

    /// <summary>
    /// IMPORT FOXPRO path [TO folder]: imports a .DBF, a .DBC, converts a .SCX form or .VCX class library, or a whole folder, writes the
    /// migration report next to the output and opens the result.
    /// </summary>
    public static MigrationReport ImportFiles(Interpreter rt, string source, string? target)
    {
        var src = Path.GetFullPath(Path.Combine(rt.Options.Default_, source));
        var dst = Path.GetFullPath(Path.Combine(rt.Options.Default_, target ?? "."));
        var report = new MigrationReport();
        var importer = new LegacyImporter(report, rt);
        if (Directory.Exists(src))
        {
            importer.ImportFolder(src, dst);
        }
        else
        {
            src = JoePro.Data.DataSession.FindIgnoringCase(src) ?? throw VfpException.FileNotFound(source);
            report.Source = src;
            report.Target = dst;
            switch (Path.GetExtension(src).ToLowerInvariant())
            {
                case ".dbc":
                {
                    importer.ImportDatabase(src, dst);
                    rt.Session.OpenDatabase(Path.Combine(dst, Path.GetFileNameWithoutExtension(src).ToLowerInvariant() + JoePro.Data.Store.DatabaseExtension));
                    break;
                }
                case ".dbf":
                {
                    var jpt = importer.ImportTable(src, dst);
                    // Replace a read-only snapshot opened earlier with USE legacy.dbf.
                    var snapshot = rt.Session.FindAlias(Path.GetFileNameWithoutExtension(src));
                    int? reuse = snapshot is { IsCursor: true, ReadOnly: true } ? snapshot.Number : null;
                    snapshot?.Close();
                    var area = reuse ?? (rt.Session.Current.InUse ? rt.Session.FreeArea() : rt.Session.CurrentAreaNumber);
                    rt.Session.Use(jpt, area);
                    rt.Session.Select(area);
                    break;
                }
                case ".scx" or ".vcx":
                    report.Source = Path.GetDirectoryName(src)!;
                    importer.ConvertClassFile(src, dst);
                    break;
                case ".pjx":
                    report.Source = Path.GetDirectoryName(src)!;
                    importer.ConvertProjectFile(src, dst);
                    break;
                case ".mnx":
                    report.Source = Path.GetDirectoryName(src)!;
                    importer.ConvertMenuFile(src, dst);
                    break;
                case ".frx" or ".lbx":
                    report.Source = Path.GetDirectoryName(src)!;
                    importer.ConvertReportFile(src, dst);
                    break;
                default:
                    throw new VfpException(ErrorCodes.InvalidArgument, "IMPORT expects a .DBF, .DBC, .SCX, .VCX, .FRX, .LBX, .MNX or .PJX file, or a folder.");
            }
        }
        report.Save(dst);
        var counts = report.Findings.GroupBy(f => f.Status).ToDictionary(g => g.Key, g => g.Count());
        rt.Notify($"Imported into {dst}. Converted: {counts.GetValueOrDefault(FindingStatus.Converted) + counts.GetValueOrDefault(FindingStatus.ConvertedWithChanges)}, " +
                  $"needs review: {counts.GetValueOrDefault(FindingStatus.NeedsReview)}, unsupported: {counts.GetValueOrDefault(FindingStatus.Unsupported)}, " +
                  $"failed: {counts.GetValueOrDefault(FindingStatus.Failed)}. Report: {Path.Combine(dst, "migration-report.html")}");
        return report;
    }
}
