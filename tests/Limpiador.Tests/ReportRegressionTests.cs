using Limpiador.App;
using System.Text.Json;

internal static class ReportRegressionTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return ("Informe: conserva riesgos, avisos y vigencia sin guardar una selección ejecutable", ExportAsync);
        yield return ("Informe: cancelar conserva el destino anterior y no deja temporales", CancelAsync);
    }

    private static UiScanResult Result() => new(
        [new("sample", "Elemento de prueba", "Development", "Desarrollo", "Descripción", 4096,
            UiRisk.Review, "Mover a la Papelera", ["fixture-path"], [new("Motivo", "Requiere revisar")], ["Se regenera"],
            Recommended: false)],
        ["Carpeta inaccesible"], "1 elemento",
        [new("fixture-path", 4096, 1, false, false, false, false)]);

    private static async Task ExportAsync()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "informe.json");
        var analyzedAt = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        var excluded = Path.Combine(fixture.Root, "carpeta excluida");
        await AnalysisReportWriter.WriteAsync(path, Result(), analyzedAt, new(180, fixture.Root, [excluded]), false, CancellationToken.None);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var report = document.RootElement;
        Require(report.GetProperty("AnalyzedAtUtc").GetDateTimeOffset() == analyzedAt && !report.GetProperty("AnalysisCurrent").GetBoolean(),
            "Un informe obsoleto se presentó como vigente.");
        Require(report.GetProperty("ScanOptions").GetProperty("MinimumAgeDays").GetInt32() == 180 && report.GetProperty("Warnings").GetArrayLength() == 1,
            "Se perdieron los parámetros o avisos del análisis.");
        Require(report.GetProperty("ScanOptions").GetProperty("ExcludedFolders")[0].GetString() == excluded &&
            report.GetProperty("ScanOptions").GetProperty("AdditionalFolder").GetString() == fixture.Root,
            "El informe no explica las carpetas incluidas y excluidas del análisis.");
        var item = report.GetProperty("Items")[0];
        Require(item.GetProperty("Risk").GetString() == "Review" && item.GetProperty("Paths")[0].GetString() == "fixture-path",
            "El informe no conserva el riesgo o la ubicación.");
        Require(!item.TryGetProperty("Selected", out _) && !report.TryGetProperty("SelectedIds", out _),
            "Se exportó una selección de limpieza.");
        Require(Directory.GetFiles(fixture.Root).Length == 1, "Quedó un archivo temporal de exportación.");
    }

    private static async Task CancelAsync()
    {
        using var fixture = new Fixture();
        var path = fixture.Write("informe.json", "informe anterior que debe conservarse");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await AnalysisReportWriter.WriteAsync(path, Result(), DateTimeOffset.UtcNow, null, true, cancellation.Token);
            throw new Exception("La exportación ignoró una cancelación previa.");
        }
        catch (OperationCanceledException) { }
        Require(await File.ReadAllTextAsync(path) == "informe anterior que debe conservarse", "La cancelación dañó el informe previo.");
        Require(Directory.GetFiles(fixture.Root).Length == 1, "La cancelación dejó archivos temporales.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
