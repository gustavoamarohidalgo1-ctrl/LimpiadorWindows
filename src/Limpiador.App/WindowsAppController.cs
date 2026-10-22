using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using Limpiador.Core;
using Limpiador.Runtime;

namespace Limpiador.App;

[SupportedOSPlatform("windows10.0.17763")]
public sealed class WindowsAppController : IAppController
{
    private CleanupService? cleanup;
    private Dictionary<string, Candidate> scanned = new(StringComparer.Ordinal);
    private readonly HistoryStore history = new(Path.Combine(WindowsHost.DataDirectory, "History"));
    private readonly AppPreferencesStore preferences = new(Path.Combine(WindowsHost.DataDirectory, "preferences.json"));
    private readonly SemaphoreSlim preferencesGate = new(1, 1);

    public Task<UiPreferences> GetPreferencesAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var saved = preferences.Load();
        return new UiPreferences(saved.MinimumAgeDays, saved.AdditionalFolder, saved.ExcludedFolders);
    }, cancellationToken);

    public async Task SavePreferencesAsync(UiPreferences value, CancellationToken cancellationToken)
    {
        await preferencesGate.WaitAsync(cancellationToken);
        try
        {
            await Task.Run(() => preferences.Save(new AppPreferences
            {
                MinimumAgeDays = value.MinimumAgeDays, AdditionalFolder = value.AdditionalFolder,
                ExcludedFolders = value.ExcludedFolders ?? []
            }), cancellationToken);
            scanned.Clear(); cleanup = null;
        }
        finally { preferencesGate.Release(); }
    }

    public Task<UiOverview> GetOverviewAsync(CancellationToken cancellationToken) => Task.Run(() =>
        new UiOverview($"{Environment.MachineName} · Windows · {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}",
            WindowsHost.Disks().Select(d => new UiDisk($"{d.VolumePath} · {d.FileSystem}", d.VolumePath, d.TotalBytes, d.AvailableBytes)).ToArray()), cancellationToken);

    public async Task<UiScanResult> ScanAsync(UiScanOptions options, IProgress<UiProgress> progress, CancellationToken cancellationToken)
    {
        scanned.Clear(); cleanup = null;
        var metadata = new WindowsMetadataProvider();
        var scanOptions = WindowsHost.CreateOptions(options.MinimumAgeDays, options.AdditionalFolder, metadata, options.ExcludedFolders);
        var scanner = new Scanner();
        var result = await scanner.ScanAsync(scanOptions, new Progress<ScanProgress>(p =>
            progress.Report(new UiProgress($"{p.Phase} · {p.FilesVisited:N0} archivos", (int)(p.Fraction * 100), 100))), cancellationToken);
        result = await AdvancedWindowsAnalysis.EnrichAsync(result, scanOptions, metadata, cancellationToken);
        scanned = result.Candidates.ToDictionary(x => x.Id, StringComparer.Ordinal);
        cleanup = new(scanOptions, metadata, history); cleanup.SetScan(result);
        return new(result.Candidates.Select(ToUi).ToArray(), result.Warnings,
            $"{result.Stats.Files:N0} archivos analizados en {result.Stats.Duration.TotalSeconds:N0} s · {result.Candidates.Count:N0} elementos · {result.Stats.SkippedDirectories:N0} carpetas no revisadas",
            result.Directories.Select(x => new UiDirectory(x.Path, x.Bytes, x.Files, x.Complete, x.ContainsSensitive, x.ContainsPersonal, x.ContainsRepository)).ToArray());
    }

    public Task<UiCleanupPlan> PreparePlanAsync(IReadOnlyList<string> itemIds, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (cleanup is null) throw new InvalidOperationException("Analiza el equipo antes de preparar un plan.");
        var plan = SelectionPlanner.Create(scanned.Values, itemIds);
        return new UiCleanupPlan(plan.Items.Select(ToUi).ToArray(), plan.Excluded.Select(item =>
            new UiPlanOmission(item.Id, item.Title, plan.ExclusionReasons.GetValueOrDefault(item.Id) ?? "Este elemento no forma parte del plan permitido.")).ToArray());
    }, cancellationToken);

    public async Task<UiOperationResult> ExecuteAsync(IReadOnlyList<string> itemIds, IProgress<UiProgress> progress, CancellationToken cancellationToken)
    {
        var service = cleanup ?? throw new InvalidOperationException("Analiza el disco antes de limpiar.");
        try
        {
            var result = await service.ExecuteAsync(itemIds, Bridge(progress), cancellationToken);
            return new(result.Summary, result.Details, null);
        }
        finally { scanned.Clear(); cleanup = null; }
    }

    public async Task<UiOperationResult> CloseApplicationsAsync(IReadOnlyList<string> itemIds, IProgress<UiProgress> progress, CancellationToken cancellationToken)
    {
        var items = itemIds.Select(id => scanned.TryGetValue(id, out var item) ? item : throw new ArgumentException("Vuelve a analizar antes de cerrar aplicaciones.")).ToArray();
        var names = items.Where(x => x.IsBlocked).SelectMany(x => x.ProcessNames).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        try
        {
            var result = await ApplicationCloser.CloseAsync(names, Bridge(progress), cancellationToken);
            return new(result.Summary, result.Details, null);
        }
        finally { scanned.Clear(); cleanup = null; }
    }

    public Task<IReadOnlyList<UiHistoryEntry>> GetHistoryAsync(CancellationToken cancellationToken) => Task.Run<IReadOnlyList<UiHistoryEntry>>(() =>
        history.List().Select(r => new UiHistoryEntry(r.Id, r.Date, r.Title, r.Status, r.Details, r.CanRestore)).ToArray(), cancellationToken);

    public async Task<UiOperationResult> RestoreAsync(string historyId, IProgress<UiProgress> progress, CancellationToken cancellationToken)
    {
        var saved = preferences.Load();
        var service = new CleanupService(WindowsHost.CreateOptions(saved.MinimumAgeDays,
            excludedFolders: saved.ExcludedFolders), new WindowsMetadataProvider(), history);
        try
        {
            var result = await service.RestoreAsync(historyId, Bridge(progress), cancellationToken);
            return new(result.Summary, result.Details, null);
        }
        finally { scanned.Clear(); cleanup = null; }
    }

    public void OpenInExplorer(string path) => WindowsHost.Reveal(path);
    public void OpenStorageSettings() => Process.Start(new ProcessStartInfo("ms-settings:storagesense") { UseShellExecute = true })?.Dispose();

    private static IProgress<OperationProgress> Bridge(IProgress<UiProgress> progress) =>
        new Progress<OperationProgress>(p => progress.Report(new(p.Message, p.Completed, p.Total)));

    private static UiItem ToUi(Candidate item) => new(item.Id, item.Title, item.Category.ToString(), CategoryName(item.Category),
        item.Description, item.Bytes, (UiRisk)item.Risk, item.Action switch
        {
            ActionKind.Delete => "Mover a la Papelera", ActionKind.Compress => "Comprimir en NTFS",
            ActionKind.Decompress => "Descomprimir", ActionKind.CloneDuplicates => "Compartir bloques en ReFS",
            ActionKind.CompactDatabase => "Compactar SQLite",
            ActionKind.CloudDehydrate => "Liberar copia local", ActionKind.OfficialCommand => "Mantenimiento oficial", _ => "Revisar"
        }, item.Paths, item.Reasons.Select((r, i) => new UiReason($"Comprobación {i + 1}", r)).ToArray(),
        string.IsNullOrWhiteSpace(item.Consequence) ? [] : [item.Consequence],
        item.CanSelect && !item.IsBlocked, item.BlockReason, item.Recommended,
        item.Risk == Risk.Safe && item.MaximumEligible ? item.Action is ActionKind.Compress or ActionKind.CompactDatabase or ActionKind.CloneDuplicates ? "Sin borrar archivos" : "Descarga o recompilación" : null,
        item.IsIrreversible, item.Action == ActionKind.Delete,
        item.LastActivityUtc is DateTime date ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)) : null,
        item.IsBlocked && item.ProcessNames.Count > 0, item.ProcessNames);

    public static string CategoryName(CleanupCategory category) => category switch
    {
        CleanupCategory.Emulators => "Emuladores", CleanupCategory.Development => "Cachés de desarrollo",
        CleanupCategory.ApplicationCaches => "Cachés de aplicaciones", CleanupCategory.SystemTemporary => "Temporales",
        CleanupCategory.ApplicationLeftovers => "Restos de aplicaciones", CleanupCategory.Projects => "Compilaciones",
        CleanupCategory.ToolData => "Datos de herramientas", CleanupCategory.Installers => "Instaladores",
        CleanupCategory.Logs => "Registros y fallos", CleanupCategory.RecycleBin => "Papelera",
        CleanupCategory.Duplicates => "Archivos duplicados", CleanupCategory.Unused => "Sin uso reciente",
        CleanupCategory.Forgotten => "Archivos olvidados", CleanupCategory.LargeFiles => "Archivos grandes",
        CleanupCategory.Compression => "Comprimir sin borrar", _ => category.ToString()
    };
}
