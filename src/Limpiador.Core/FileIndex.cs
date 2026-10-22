namespace Limpiador.Core;

internal sealed class IndexedEntry(FileSnapshot snapshot)
{
    public FileSnapshot Snapshot { get; } = snapshot;
    public string Path => Snapshot.Path;
    public List<string> Children { get; } = [];
    public long Bytes { get; set; } = snapshot.AllocatedBytes;
    public long Files { get; set; } = snapshot.IsDirectory ? 0 : 1;
    public DateTime Latest { get; set; } = snapshot.LastWriteTimeUtc > snapshot.CreationTimeUtc ? snapshot.LastWriteTimeUtc : snapshot.CreationTimeUtc;
    public bool Complete { get; set; } = true;
    public bool Sensitive { get; set; } = SafetyPolicy.IsSensitiveFile(snapshot.Path);
    public bool Personal { get; set; } = !snapshot.IsDirectory && FileIndex.PersonalExtensions.Contains(System.IO.Path.GetExtension(snapshot.Path));
    public bool Repository { get; set; }
}

internal sealed class FileIndex
{
    internal static readonly HashSet<string> PersonalExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pdf", ".odt", ".ods", ".csv", ".tsv", ".ipynb", ".patch", ".diff",
      ".jpg", ".jpeg", ".png", ".heic", ".raw", ".psd", ".ai", ".mp4", ".mov", ".mp3", ".wav", ".flac", ".blend", ".prproj", ".drp",
      ".sqlite", ".sqlite3", ".db", ".mdb", ".accdb", ".bak", ".sql", ".vhd", ".vhdx", ".qcow2", ".gguf", ".ggml", ".safetensors", ".onnx", ".pt", ".ckpt", ".tflite",
      ".parquet", ".arrow", ".feather", ".h5", ".hdf5", ".npz", ".npy", ".rds", ".pkl", ".pickle", ".dmp", ".trace", ".etl", ".perfetto-trace", ".apk", ".aab" };
    public Dictionary<string, IndexedEntry> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal HashSet<string> RepositoryRoots { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Warnings { get; } = [];
    public long Files { get; private set; }
    public long Bytes { get; private set; }
    public int Skipped { get; private set; }
    public bool Limited { get; private set; }
    private readonly ScanOptions options;
    private readonly SafetyPolicy policy;
    private readonly IFileMetadataProvider metadata;
    public FileIndex(ScanOptions options, SafetyPolicy policy) { this.options = options; this.policy = policy; metadata = options.MetadataProvider ?? new FileMetadataProvider(); }

    public void Build(IProgress<ScanProgress>? progress, CancellationToken token)
    {
        var roots = policy.ApprovedRoots.OrderBy(x => x.Length)
            .Aggregate(new List<string>(), (list, root) => { if (!list.Any(parent => SafetyPolicy.IsWithin(root, parent))) list.Add(root); return list; });
        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            if (Limited) break;
            if (policy.IsExcludedPath(root)) { MarkExcluded(root); continue; }
            if (!Directory.Exists(root)) continue;
            if (!CanTraverseRoot(root)) continue;
            Visit(root, progress, token);
        }
        foreach (var entry in Entries.Values.OrderByDescending(x => x.Path.Length))
        {
            token.ThrowIfCancellationRequested();
            var parent = Path.GetDirectoryName(entry.Path);
            if (parent is null || !Entries.TryGetValue(parent, out var above)) continue;
            above.Children.Add(entry.Path);
            above.Bytes += entry.Bytes;
            above.Files += entry.Files;
            above.Latest = above.Latest > entry.Latest ? above.Latest : entry.Latest;
            above.Complete &= entry.Complete;
            above.Personal |= entry.Personal;
            above.Sensitive |= entry.Sensitive;
            above.Repository |= entry.Repository;
        }
        if (Limited) Warnings.Add("Se alcanzó el límite de archivos del análisis. Las carpetas incompletas no se pueden seleccionar.");
    }

    private void Visit(string root, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        // Keep one enumerator per depth, rather than queueing every name in a wide
        // directory before checking the entry budget or cancellation again.
        var stack = new Stack<(string Path, IEnumerator<string> Children)>();
        string? next = root;
        try
        {
            while (next is not null || stack.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                if (next is null)
                {
                    var frame = stack.Peek();
                    try
                    {
                        if (frame.Children.MoveNext()) next = SafetyPolicy.Normalize(frame.Children.Current);
                        else { stack.Pop().Children.Dispose(); continue; }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        MarkUnavailable(frame.Path);
                        stack.Pop().Children.Dispose();
                        continue;
                    }
                }
                var path = next;
                next = null;
                if (Path.GetFileName(path).Equals(".git", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(path) is { } repository)
                    RepositoryRoots.Add(repository);
                if (policy.IsExcludedPath(path)) { MarkExcluded(path); continue; }
                if (Entries.ContainsKey(path)) continue;
                if (Entries.Count >= options.MaximumEntries)
                {
                    Limited = true;
                    foreach (var existing in Entries.Values.Where(x => x.Snapshot.IsDirectory))
                    { token.ThrowIfCancellationRequested(); existing.Complete = false; }
                    break;
                }
                try
                {
                    var snap = metadata.Capture(path);
                    var entry = new IndexedEntry(snap) { Complete = !policy.ContainsExcludedPath(path) };
                    Entries[path] = entry;
                    if (snap.IsReparsePoint || snap.IsOffline || policy.IsCloudPath(path))
                    { entry.Complete = false; Skipped++; continue; }
                    if (!snap.IsDirectory) { Files++; Bytes += snap.AllocatedBytes; continue; }
                    if (Path.GetFileName(path).Equals(".git", StringComparison.OrdinalIgnoreCase))
                    { entry.Repository = true; entry.Complete = false; continue; }
                    if (policy.IsProtectedPath(path) && !policy.ApprovedRoots.Contains(path, StringComparer.OrdinalIgnoreCase) &&
                        !path.Equals(Path.Combine(policy.ProfilePath, "AppData"), StringComparison.OrdinalIgnoreCase) &&
                        !path.Equals(Path.Combine(policy.LocalAppDataPath, "Microsoft"), StringComparison.OrdinalIgnoreCase) &&
                        !path.Equals(Path.Combine(policy.RoamingAppDataPath, "Microsoft"), StringComparison.OrdinalIgnoreCase) &&
                        !path.Equals(Path.Combine(policy.LocalAppDataPath, "Packages"), StringComparison.OrdinalIgnoreCase) &&
                        !path.Equals(Path.Combine(policy.LocalAppDataPath, "Programs"), StringComparison.OrdinalIgnoreCase))
                    { entry.Complete = false; entry.Sensitive = true; Skipped++; continue; }
                    stack.Push((path, Directory.EnumerateFileSystemEntries(path).GetEnumerator()));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                { MarkUnavailable(path); }
                finally
                {
                    if (Entries.Count % 256 == 0) progress?.Report(new("Índice del disco", Files, Bytes, path, 0.15));
                }
            }
        }
        finally { foreach (var frame in stack) frame.Children.Dispose(); }
    }

    private bool CanTraverseRoot(string root)
    {
        try
        {
            // A redirected Known Folder may itself be ordinary while an ancestor
            // is a junction. Checking only the root's metadata would follow it.
            if (policy.IsCloudPath(root)) { MarkUnavailable(root); return false; }
            for (string? path = root; path is not null; path = Path.GetDirectoryName(path))
                if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                { MarkUnavailable(root); return false; }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { MarkUnavailable(root); return false; }
    }

    private void MarkUnavailable(string path)
    {
        Skipped++;
        if (Entries.TryGetValue(path, out var current)) current.Complete = false;
        var parent = Path.GetDirectoryName(path);
        if (parent is not null && Entries.TryGetValue(parent, out var above)) above.Complete = false;
        if (Warnings.Count < 100) Warnings.Add($"No se pudo revisar: {path}");
    }

    private void MarkExcluded(string path)
    {
        Skipped++;
        var parent = Path.GetDirectoryName(path);
        if (parent is not null && Entries.TryGetValue(parent, out var above))
        {
            above.Complete = false;
            if (Path.GetFileName(path).Equals(".git", StringComparison.OrdinalIgnoreCase)) above.Repository = true;
        }
        if (Warnings.Count < 100) Warnings.Add($"Carpeta excluida por configuración: {path}");
    }

    public IndexedEntry? Get(string path) => Entries.GetValueOrDefault(SafetyPolicy.Normalize(path));
    public IReadOnlyList<FileSnapshot> Snapshots(string path, CancellationToken cancellationToken = default)
    {
        var result = new List<FileSnapshot>();
        if (Get(path) is not { } root) return result;
        var pending = new Stack<IndexedEntry>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            result.Add(current.Snapshot);
            foreach (var child in current.Children) pending.Push(Entries[child]);
        }
        return result;
    }
    public IEnumerable<IndexedEntry> Children(string path) => Get(path)?.Children.Select(x => Entries[x]) ?? [];
}
