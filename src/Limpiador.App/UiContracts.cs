namespace Limpiador.App;

/// <summary>Presentation boundary: adapters must revalidate every requested operation.</summary>
public interface IAppController
{
    Task<UiOverview> GetOverviewAsync(CancellationToken cancellationToken);
    Task<UiPreferences> GetPreferencesAsync(CancellationToken cancellationToken);
    Task SavePreferencesAsync(UiPreferences preferences, CancellationToken cancellationToken);
    Task<UiScanResult> ScanAsync(UiScanOptions options, IProgress<UiProgress> progress, CancellationToken cancellationToken);
    Task<UiCleanupPlan> PreparePlanAsync(IReadOnlyList<string> itemIds, CancellationToken cancellationToken);
    Task<UiOperationResult> ExecuteAsync(IReadOnlyList<string> itemIds, IProgress<UiProgress> progress, CancellationToken cancellationToken);
    Task<UiOperationResult> CloseApplicationsAsync(IReadOnlyList<string> itemIds, IProgress<UiProgress> progress, CancellationToken cancellationToken);
    Task<IReadOnlyList<UiHistoryEntry>> GetHistoryAsync(CancellationToken cancellationToken);
    Task<UiOperationResult> RestoreAsync(string historyId, IProgress<UiProgress> progress, CancellationToken cancellationToken);
    void OpenInExplorer(string path);
    void OpenStorageSettings();
}

public enum UiRisk { Safe, Review, Caution }
public sealed record UiDisk(string Name, string RootPath, long TotalBytes, long AvailableBytes)
{
    public double UsedPercent => TotalBytes <= 0 ? 0 : Math.Clamp(100.0 * (TotalBytes - AvailableBytes) / TotalBytes, 0, 100);
    public string FreeText => $"{DisplaySize.Bytes(AvailableBytes)} libres";
    public string CapacityText => $"de {DisplaySize.Bytes(TotalBytes)} · {UsedPercent:F0} % usado";
}
public sealed record UiOverview(string ComputerDescription, IReadOnlyList<UiDisk> Disks);
public sealed record UiPreferences(int MinimumAgeDays = 90, string? AdditionalFolder = null,
    IReadOnlyList<string>? ExcludedFolders = null);
public sealed record UiScanOptions(int MinimumAgeDays = 90, string? AdditionalFolder = null,
    IReadOnlyList<string>? ExcludedFolders = null);
public sealed record UiPlanOmission(string ItemId, string Name, string Reason);
public sealed record UiCleanupPlan(IReadOnlyList<UiItem> Items, IReadOnlyList<UiPlanOmission> Omissions)
{
    public long AcceptedBytes => Items.Sum(item => item.SizeBytes);
}
public sealed record UiProgress(string Message, int Completed = 0, int Total = 0)
{
    public double Percent => Total <= 0 ? 0 : Math.Clamp(100.0 * Completed / Total, 0, 100);
}
public sealed record UiReason(string Title, string Description);
public sealed record UiItem(
    string Id, string Name, string CategoryId, string CategoryName, string Description,
    long SizeBytes, UiRisk Risk, string ActionLabel,
    IReadOnlyList<string> Paths, IReadOnlyList<UiReason> Reasons, IReadOnlyList<string> Costs,
    bool CanSelect = true, string? BlockedReason = null,
    bool Recommended = false, string? MaximumGroup = null,
    bool IsIrreversible = false, bool UsesRecycleBin = true, DateTimeOffset? LastUsed = null,
    bool CanCloseApplications = false, IReadOnlyList<string>? ApplicationsToClose = null)
{
    public string RiskLabel => Risk switch { UiRisk.Safe => "Seguro", UiRisk.Review => "Revisar", _ => "Cuidado" };
    public string SizeText => DisplaySize.Bytes(SizeBytes);
    public string CostText => Costs.Count == 0 ? "Sin costes adicionales indicados." : string.Join("\n", Costs.Select(x => "• " + x));
    public string PathsText => string.Join(Environment.NewLine, Paths);
    public string LastUsedText => LastUsed is null ? "Último uso no disponible" : $"Último cambio o uso: {LastUsed.Value.LocalDateTime:g}";
    public string RecoveryText => IsIrreversible ? "Acción irreversible. No se podrá deshacer desde el historial." : UsesRecycleBin ? "Los archivos se trasladan a la Papelera. El espacio se libera al vaciarla." : "Consulta el resultado y las opciones de recuperación en el historial.";
}
public sealed record UiDirectory(string Path, long Bytes, long Files, bool Complete, bool Sensitive, bool Personal, bool Repository)
{
    public string Name => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path)) is { Length: > 0 } name ? name : Path;
    public string SizeText => DisplaySize.Bytes(Bytes);
    public string FilesText => $"{Files:N0}";
    public string StatusText => string.Join(" · ", new[]
    {
        Complete ? "Lectura completa" : "Lectura parcial", Sensitive ? "Datos sensibles" : null,
        Personal ? "Material personal" : null, Repository ? "Contiene Git" : null
    }.OfType<string>());
}
public sealed record UiScanResult(IReadOnlyList<UiItem> Items, IReadOnlyList<string> Warnings, string Summary,
    IReadOnlyList<UiDirectory>? Directories = null);
public sealed record UiOperationResult(string Summary, IReadOnlyList<string> Details, IReadOnlyList<string>? CompletedItemIds = null);
public sealed record UiHistoryEntry(string Id, DateTimeOffset Date, string Title, string Summary, IReadOnlyList<string> Details, bool CanRestore)
{
    public string DateText => Date.LocalDateTime.ToString("g");
    public string DetailsText => string.Join(Environment.NewLine, Details);
}
public static class DisplaySize
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0.0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value.ToString(unit == 0 ? "N0" : "N1", System.Globalization.CultureInfo.CurrentCulture)} {units[unit]}";
    }
}
