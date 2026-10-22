namespace Limpiador.Core;

public enum Risk { Safe, Review, Caution }
public enum ActionKind { Delete, Compress, Decompress, OfficialCommand, CloudDehydrate, CloneDuplicates, CompactDatabase }
public enum CleanupCategory
{
    Emulators, Development, ApplicationCaches, SystemTemporary, ApplicationLeftovers,
    Projects, ToolData, Installers, Logs, RecycleBin, Duplicates, Unused, Forgotten, LargeFiles, Compression
}
public enum OfficialCommandKind { DockerBuildCachePrune, DockerDanglingImagesPrune, GitGarbageCollect, NpmCacheClean, NuGetHttpCacheClear, PipCachePurge }
public sealed record OfficialCommand(OfficialCommandKind Kind, string Executable, IReadOnlyList<string> Arguments, string? WorkingDirectory = null);

public sealed record FileSnapshot(string Path, bool IsDirectory, long Length, long AllocatedBytes,
    DateTime LastWriteTimeUtc, DateTime CreationTimeUtc, FileAttributes Attributes,
    string? FileId = null, uint LinkCount = 1)
{
    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;
    public bool IsOffline => (Attributes & FileAttributes.Offline) != 0;
}

public interface IFileMetadataProvider { FileSnapshot Capture(string path); }

/// <summary>Supplies fixture metadata on any host. Native Windows builds inject allocation/file-ID metadata.</summary>
public sealed class FileMetadataProvider : IFileMetadataProvider
{
    public FileSnapshot Capture(string path)
    {
        var attributes = File.GetAttributes(path);
        var directory = (attributes & FileAttributes.Directory) != 0;
        FileSystemInfo info = directory ? new DirectoryInfo(path) : new FileInfo(path);
        var length = directory ? 0 : ((FileInfo)info).Length;
        return new(Path.GetFullPath(path), directory, length, length, info.LastWriteTimeUtc,
            info.CreationTimeUtc, attributes);
    }
}

public sealed class ScanOptions
{
    public string ProfilePath { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string? LocalAppDataPath { get; init; }
    public string? RoamingAppDataPath { get; init; }
    public string? TempPath { get; init; }
    public IReadOnlyList<string>? PersonalRoots { get; init; }
    public IReadOnlyList<string> AdditionalCloudRoots { get; init; } = [];
    public IReadOnlyList<string> AllowedRoots { get; init; } = [];
    public IReadOnlyList<string> ExcludedRoots { get; init; } = [];
    public long MinimumBytes { get; init; } = 1_000_000;
    public TimeSpan MinimumAge { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan UnusedAge { get; init; } = TimeSpan.FromDays(90);
    public bool IncludeDuplicates { get; init; } = true;
    public bool IncludeCompression { get; init; } = true;
    public int MaximumEntries { get; init; } = 500_000;
    public long MaximumHashBytes { get; init; } = 64L * 1024 * 1024 * 1024;
    public IFileMetadataProvider? MetadataProvider { get; init; }
    /// <summary>True=in use; false=known idle; null=unknown. Unknown prevents selection.</summary>
    public Func<IReadOnlyList<string>, bool?>? AreProcessesRunning { get; init; }
    public DateTime UtcNow { get; init; } = DateTime.UtcNow;
}

public sealed record Candidate
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Consequence { get; init; } = "";
    public CleanupCategory Category { get; init; }
    public Risk Risk { get; init; } = Risk.Review;
    public ActionKind Action { get; init; } = ActionKind.Delete;
    public IReadOnlyList<string> Paths { get; init; } = [];
    public IReadOnlyList<FileSnapshot> Snapshots { get; init; } = [];
    public long Bytes { get; init; }
    public bool Recommended { get; init; }
    public bool MaximumEligible { get; init; }
    public bool CanSelect { get; init; } = true;
    public bool IsBlocked { get; init; }
    public string? BlockReason { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public IReadOnlyList<string> ProcessNames { get; init; } = [];
    public OfficialCommand? Command { get; init; }
    public string? PreservedPath { get; init; }
    public string? SHA256 { get; init; }
    public DateTime? LastActivityUtc { get; init; }
    public bool IsIrreversible => Action == ActionKind.OfficialCommand && Command?.Kind != OfficialCommandKind.GitGarbageCollect;
}

public sealed record ScanProgress(string Phase, long FilesVisited, long BytesVisited, string? CurrentPath = null, double Fraction = 0);
public sealed record DirectorySummary(string Path, long Bytes, long Files, DateTime LastActivityUtc, bool Complete,
    bool ContainsSensitive, bool ContainsPersonal, bool ContainsRepository);
public sealed record ScanStatistics(long Entries, long Files, long Bytes, int SkippedDirectories, TimeSpan Duration, bool AllocationMeasured);
public sealed record ScanResult
{
    public IReadOnlyList<Candidate> Candidates { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<DirectorySummary> Directories { get; init; } = [];
    public ScanStatistics Stats { get; init; } = new(0, 0, 0, 0, TimeSpan.Zero, false);
}

public sealed record SafetyDecision(bool Allowed, string Reason)
{
    public static SafetyDecision Permit { get; } = new(true, "");
}

public sealed partial record CleanupPlan
{
    public IReadOnlyList<Candidate> Regenerates { get; init; } = [];
    public IReadOnlyList<Candidate> WithoutDeleting { get; init; } = [];
    public IReadOnlyList<Candidate> CostsDownloadOrRebuild { get; init; } = [];
    public IReadOnlyList<Candidate> Manual { get; init; } = [];
    public IReadOnlyList<Candidate> Excluded { get; init; } = [];
    public IReadOnlyDictionary<string, string> ExclusionReasons { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<Candidate> Items => Regenerates.Concat(WithoutDeleting).Concat(CostsDownloadOrRebuild).Concat(Manual).ToArray();
    public long EstimatedBytes => Items.Sum(x => x.Bytes);

}
