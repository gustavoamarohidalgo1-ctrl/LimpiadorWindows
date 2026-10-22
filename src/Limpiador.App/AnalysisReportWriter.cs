using System.IO;
using System.Text.Json;

namespace Limpiador.App;

internal static class AnalysisReportWriter
{
    public static async Task WriteAsync(string path, UiScanResult result, DateTimeOffset analyzedAt,
        UiScanOptions? scanOptions, bool analysisCurrent, CancellationToken cancellationToken)
    {
        var report = new
        {
            SchemaVersion = 1,
            ExportedAtUtc = DateTimeOffset.UtcNow,
            AnalyzedAtUtc = analyzedAt,
            AnalysisCurrent = analysisCurrent,
            ScanOptions = scanOptions,
            result.Summary,
            result.Warnings,
            // This is a report, never a saved selection or an executable cleanup plan.
            Items = result.Items.Select(item => new
            {
                item.Id, item.Name, item.CategoryId, item.CategoryName, item.Description,
                item.SizeBytes, Risk = item.Risk.ToString(), item.ActionLabel,
                item.Paths, item.Reasons, item.Costs, item.CanSelect, item.BlockedReason,
                item.Recommended, item.MaximumGroup, item.IsIrreversible, item.UsesRecycleBin,
                item.LastUsed, item.CanCloseApplications, item.ApplicationsToClose
            }),
            Directories = result.Directories?.Select(directory => new
            {
                directory.Path, directory.Bytes, directory.Files, directory.Complete,
                directory.Sensitive, directory.Personal, directory.Repository
            })
        };
        var destination = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, report, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
