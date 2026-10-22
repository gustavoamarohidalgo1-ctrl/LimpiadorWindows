namespace Limpiador.Runtime;

public sealed record OperationProgress(string Message, int Completed, int Total);
public sealed record OperationReport(string Summary, IReadOnlyList<string> Details,
    IReadOnlyList<string> CompletedIds, long MeasuredFreeBytes, string? HistoryId = null);

public sealed record HistoryRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Date { get; init; } = DateTimeOffset.UtcNow;
    public string Title { get; init; } = "Limpieza";
    public string Status { get; set; } = "En curso";
    public List<string> Details { get; init; } = [];
    public List<string> RecycleJournals { get; init; } = [];
    public List<CompressedFile> CompressedFiles { get; init; } = [];
    public List<string> ApprovedRoots { get; init; } = [];
    public bool Restored { get; set; }
    public bool CanRestore => !Restored && (RecycleJournals.Count > 0 || CompressedFiles.Count > 0);
}

public sealed record CompressedFile(string Path, string FileId, long Length, DateTime LastWriteUtc);
