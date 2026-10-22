using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Limpiador.Core;
using Limpiador.Windows;

namespace Limpiador.Runtime;

[SupportedOSPlatform("windows10.0.17763")]
public static class AdvancedWindowsAnalysis
{
    public static Task<ScanResult> EnrichAsync(ScanResult scan, ScanOptions options, WindowsMetadataProvider metadata,
        CancellationToken cancellationToken = default) => Task.Run(() => Enrich(scan, options, metadata, cancellationToken), cancellationToken);

    private static ScanResult Enrich(ScanResult scan, ScanOptions options, WindowsMetadataProvider metadata, CancellationToken token)
    {
        WindowsFileSystem.EnsureWindows(); var watch = Stopwatch.StartNew();
        var candidates = scan.Candidates.ToList(); var warnings = scan.Warnings.ToList(); var policy = new SafetyPolicy(options);
        var fileSystems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < candidates.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var item = candidates[index];
            if (item.Action != ActionKind.Compress || item.Paths.Count == 0 || !item.CanSelect) continue;
            string? blocked = null;
            try
            {
                var root = Path.GetPathRoot(item.Paths[0])!;
                if (!fileSystems.TryGetValue(root, out var fs)) fileSystems[root] = fs = WindowsFileSystem.GetDiskSpace(item.Paths[0]).FileSystem;
                if (!fs.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) blocked = "Este volumen no admite compresión NTFS.";
                else if (item.Snapshots.Where(x => !x.IsDirectory).All(x => (x.Attributes & FileAttributes.Compressed) != 0))
                    blocked = "Todos los archivos de este elemento ya están comprimidos.";
                else if (item.Snapshots.Any(x => !x.IsDirectory && (x.LinkCount != 1 ||
                    (x.Attributes & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Encrypted | FileAttributes.Offline)) != 0)))
                    blocked = "Contiene archivos protegidos, cifrados o con enlaces físicos que no se comprimen automáticamente.";
            }
            catch (Exception ex) when (ExpectedIo(ex)) { blocked = "No se pudo comprobar el soporte de compresión del volumen."; }
            if (blocked is not null) candidates[index] = item with { CanSelect = false, IsBlocked = true, BlockReason = blocked, Recommended = false, MaximumEligible = false };
        }
        var cloudCandidates = new List<Candidate>(); long cloudEntries = 0, cloudFiles = 0, cloudBytes = 0;
        var roots = KnownFolders.CloudRoots();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cloudAge = CloudAge(options);
        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            if (policy.IsExcludedPath(root)) continue;
            try
            {
                RequireCloudAncestors(root, token);
                var pending = new Stack<string>(); pending.Push(root);
                while (pending.TryPop(out var folder))
                {
                    token.ThrowIfCancellationRequested();
                    if (policy.IsExcludedPath(folder)) continue;
                    if (!visited.Add(folder)) continue;
                    try
                    {
                        foreach (var path in Directory.EnumerateFileSystemEntries(folder))
                        {
                            token.ThrowIfCancellationRequested();
                            if (policy.IsExcludedPath(path)) continue;
                            if (++cloudEntries > options.MaximumEntries) throw new CloudLimitException();
                            NativeFileMetadata state;
                            try { state = WindowsFileSystem.GetMetadata(path); }
                            catch (Exception ex) when (ExpectedIo(ex)) { continue; }
                            if (state.IsReparsePoint && !state.IsCloudPlaceholder) continue;
                            if (policy.IsProtectedPath(path) || SafetyPolicy.IsSensitiveFile(path)) continue;
                            if (state.IsDirectory)
                            {
                                // Enumerating names is metadata-only; file streams are never opened/read here.
                                pending.Push(path); continue;
                            }
                            cloudFiles++; cloudBytes += state.AllocatedBytes;
                            if (!EligibleCloudMetadata(state, options.UtcNow, cloudAge, options.MinimumBytes)) continue;
                            try
                            {
                                var cloud = CloudFiles.GetState(path);
                                if (!cloud.CanDehydrate || cloud.OnDiskDataBytes < options.MinimumBytes) continue;
                                var snapshot = metadata.Capture(path);
                                if (!state.SameVersion(metadata.Expected(path)!)) continue;
                                cloudCandidates.Add(new Candidate
                                {
                                    Id = Id("cloud:" + path), Title = "OneDrive: liberar descarga de " + Path.GetFileName(path),
                                    Category = CleanupCategory.Unused, Action = ActionKind.CloudDehydrate, Risk = Risk.Safe,
                                    Description = path,
                                    Consequence = "Conserva el archivo sincronizado en OneDrive y su marcador local. Necesitará Internet y volver a descargarse para leerlo; no va a la Papelera.",
                                    Paths = [path], Snapshots = [snapshot], Bytes = Math.Min(state.AllocatedBytes, cloud.OnDiskDataBytes),
                                    Recommended = false, MaximumEligible = true, LastActivityUtc = Latest(state),
                                    Reasons = [$"Sin cambios desde hace al menos {cloudAge.TotalDays:0} días.", "Cloud Files confirma sincronización completa, cero cambios pendientes y que no está fijado en el dispositivo.", "Solo incluido automáticamente al revisar el modo máximo."]
                                });
                            }
                            catch (Exception ex) when (ExpectedIo(ex)) { /* Unknown provider state cannot become eligible. */ }
                        }
                    }
                    catch (Exception ex) when (ExpectedIo(ex)) { warnings.Add($"OneDrive: no se pudo revisar por completo {folder}; los elementos inaccesibles se omitieron."); }
                }
            }
            catch (CloudLimitException) { warnings.Add("Se alcanzó el límite de entradas del análisis de OneDrive; las restantes se omitieron."); break; }
            catch (Exception ex) when (ExpectedIo(ex)) { warnings.Add($"OneDrive: se omitió una raíz cuya ruta no pudo verificarse: {root}."); }
        }
        // One Restart Manager session checks all candidate files; any uncertainty excludes the batch.
        if (cloudCandidates.Count > 0)
        {
            try { RestartManager.RequireUnlocked(cloudCandidates.SelectMany(x => x.Paths), token); candidates.AddRange(cloudCandidates); }
            catch (Exception ex) when (ExpectedIo(ex))
            {
                warnings.Add("OneDrive: se omitieron las descargas propuestas porque hay archivos abiertos o no se pudieron verificar sus bloqueos.");
            }
        }
        if (options.IncludeDuplicates)
            foreach (var duplicate in scan.Candidates.Where(x => x.Category == CleanupCategory.Duplicates && x.Action == ActionKind.Delete && x.CanSelect && !x.IsBlocked))
            {
                token.ThrowIfCancellationRequested();
                if (duplicate.PreservedPath is not { } source || duplicate.SHA256 is null || duplicate.Paths.Count != 1) continue;
                var target = duplicate.Paths[0];
                try
                {
                    if (!policy.Validate(source).Allowed || !policy.Validate(target).Allowed) continue;
                    var a = metadata.Expected(source); var b = metadata.Expected(target);
                    if (a is null || b is null || !EligibleClone(a) || !EligibleClone(b)) continue;
                    if (a.VolumeSerial != b.VolumeSerial || a.FileId == b.FileId || a.LogicalBytes != b.LogicalBytes) continue;
                    if (!a.SameVersion(WindowsFileSystem.GetMetadata(source)) || !b.SameVersion(WindowsFileSystem.GetMetadata(target))) continue;
                    var minAge = options.MinimumAge > TimeSpan.FromHours(24) ? options.MinimumAge : TimeSpan.FromHours(24);
                    if (options.UtcNow - Latest(a) < minAge || options.UtcNow - Latest(b) < minAge) continue;
                    var volume = WindowsFileSystem.GetDiskSpace(target);
                    if (!volume.FileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase)) continue;
                    var bytes = Math.Min(b.AllocatedBytes, b.LogicalBytes / volume.ClusterBytes * volume.ClusterBytes);
                    if (bytes < options.MinimumBytes) continue;
                    RestartManager.RequireUnlocked([source, target], token);
                    candidates.Add(duplicate with
                    {
                        Id = Id("refs:" + source + ":" + target), Title = "ReFS: compartir bloques de " + Path.GetFileName(target),
                        Action = ActionKind.CloneDuplicates, Risk = Risk.Safe, Recommended = false, MaximumEligible = true,
                        Description = target, Bytes = bytes,
                        Consequence = "Conserva ambos archivos independientes: escribir en uno no cambia el otro. Comparte bloques de contenido idéntico con ReFS; nunca crea enlaces duros.",
                        Reasons = ["Coinciden en SHA-256 y están en el mismo volumen ReFS.", "Las dos identidades y sus fechas siguen coincidiendo con el análisis.", "El ahorro indicado es un límite estimado: ReFS puede tener bloques compartidos previamente."],
                        Snapshots = [WindowsMetadataProvider.Convert(b)]
                    });
                }
                catch (Exception ex) when (ExpectedIo(ex)) { /* Unsupported volumes, locked files or unknown state are omitted. */ }
            }
        var databaseCount = 0;
        if (options.IncludeCompression)
        {
            var local = policy.LocalAppDataPath; var roaming = policy.RoamingAppDataPath;
            foreach (var state in metadata.Captured)
            {
                token.ThrowIfCancellationRequested();
                if (state.IsDirectory || state.LogicalBytes < 50_000_000 || state.HardLinkCount != 1 ||
                    !new[] { ".db", ".sqlite", ".sqlite3" }.Contains(Path.GetExtension(state.Path), StringComparer.OrdinalIgnoreCase) ||
                    options.UtcNow - Latest(state) < TimeSpan.FromDays(7) || !policy.Validate(state.Path).Allowed) continue;
                var knownData = SafetyPolicy.IsWithin(state.Path, local) || SafetyPolicy.IsWithin(state.Path, roaming) ||
                    SafetyPolicy.IsWithin(state.Path, Path.Combine(policy.ProfilePath, ".codex")) ||
                    SafetyPolicy.IsWithin(state.Path, Path.Combine(policy.ProfilePath, ".claude"));
                if (!knownData || new[] { Path.Combine(local, "Microsoft"), Path.Combine(roaming, "Microsoft"),
                    Path.Combine(local, "Packages"), Path.Combine(local, "Programs") }.Any(root => SafetyPolicy.IsWithin(state.Path, root))) continue;
                try
                {
                    var info = SqliteCompactor.Inspect(state.Path);
                    if (!info.CanCompact || info.ReclaimableBytes < options.MinimumBytes || !state.SameVersion(WindowsFileSystem.GetMetadata(state.Path))) continue;
                    candidates.Add(new()
                    {
                        Id = Id("sqlite:" + state.Path), Title = "Compactar SQLite: " + Path.GetFileName(state.Path),
                        Description = state.Path, Category = CleanupCategory.Compression, Action = ActionKind.CompactDatabase,
                        Risk = Risk.Safe, Recommended = false, MaximumEligible = true, Bytes = info.ReclaimableBytes,
                        Paths = [state.Path], Snapshots = [WindowsMetadataProvider.Convert(state)], LastActivityUtc = Latest(state),
                        Consequence = "Conserva tablas y filas; recupera páginas libres mediante VACUUM. Requiere la aplicación cerrada, tiempo de CPU y espacio temporal.",
                        Reasons = ["SQLite confirma páginas libres y un esquema sin FTS, triggers ni ROWID implícitos que puedan cambiar.", "Se comprobarán de nuevo bloqueo exclusivo, integridad y contenido lógico antes y después de compactar."]
                    });
                    databaseCount++;
                }
                catch (Exception ex) when (ExpectedIo(ex)) { }
            }
        }
        if (databaseCount > 0)
            candidates.RemoveAll(x => x.Title == "Compactación de bases SQLite" && x.Paths.Count == 0);
        return scan with
        {
            Candidates = candidates.OrderBy(x => x.Category).ThenByDescending(x => x.Bytes).ToArray(),
            Warnings = warnings.Distinct().ToArray(),
            Stats = scan.Stats with { Entries = scan.Stats.Entries + cloudEntries, Files = scan.Stats.Files + cloudFiles,
                Bytes = scan.Stats.Bytes + cloudBytes, Duration = scan.Stats.Duration + watch.Elapsed }
        };
    }

    public static DehydrationResult ExecuteCloud(Candidate candidate, ScanOptions options, WindowsMetadataProvider metadata,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WindowsFileSystem.EnsureWindows();
        if (candidate.Action != ActionKind.CloudDehydrate || !candidate.CanSelect || candidate.IsBlocked || candidate.Paths.Count != 1 || candidate.Snapshots.Count != 1)
            throw new IOException("La selección no es una descarga individual de OneDrive verificada.");
        var path = WindowsFileSystem.NormalizeLocalPath(candidate.Paths[0]);
        var roots = KnownFolders.CloudRoots();
        if (!roots.Any(root => path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("La carpeta OneDrive ya no está registrada para este usuario.");
        var policy = new SafetyPolicy(options);
        if (policy.IsProtectedPath(path) || SafetyPolicy.IsSensitiveFile(path)) throw new IOException("El archivo es una ruta protegida.");
        RequireCloudAncestors(path, cancellationToken);
        var expected = metadata.Expected(path) ?? throw new IOException("Falta el estado original del análisis de OneDrive.");
        if (WindowsMetadataProvider.Convert(expected) != candidate.Snapshots[0]) throw new IOException("El estado de la selección no coincide con el análisis.");
        var current = WindowsFileSystem.GetMetadata(path);
        if (!current.SameVersion(expected) || !EligibleCloudMetadata(current, DateTime.UtcNow, CloudAge(options), options.MinimumBytes))
            throw new IOException("El archivo de OneDrive cambió desde el análisis o ya no cumple la antigüedad mínima.");
        var cloud = CloudFiles.GetState(path);
        if (!cloud.CanDehydrate) throw new IOException("La descarga no está sincronizada o se fijó en el dispositivo.");
        return CloudFiles.Dehydrate(path, expected, cancellationToken);
    }
    private static TimeSpan CloudAge(ScanOptions options) => options.UnusedAge > TimeSpan.FromDays(90) ? options.UnusedAge : TimeSpan.FromDays(90);
    private static DateTime Latest(NativeFileMetadata state) => new[] { state.LastWriteUtc, state.CreationTimeUtc, state.ChangeTimeUtc }.Max();
    private static bool EligibleCloudMetadata(NativeFileMetadata state, DateTime now, TimeSpan age, long minimumBytes) =>
        state.IsCloudPlaceholder && !state.IsDirectory && !state.DeletePending && state.HardLinkCount == 1 && state.AllocatedBytes >= minimumBytes &&
        (state.Attributes & (FileAttributes.System | FileAttributes.ReadOnly | FileAttributes.Encrypted | (FileAttributes)0x80000)) == 0 && now - Latest(state) >= age;
    private static bool EligibleClone(NativeFileMetadata state) => !state.IsDirectory && !state.IsReparsePoint && !state.DeletePending && state.HardLinkCount == 1 &&
        (state.Attributes & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Encrypted | FileAttributes.Offline | FileAttributes.SparseFile | FileAttributes.Compressed)) == 0;
    private static void RequireCloudAncestors(string path, CancellationToken cancellationToken = default)
    {
        var parts = new Stack<string>();
        for (string? current = WindowsFileSystem.NormalizeLocalPath(path); current is not null; current = Path.GetDirectoryName(current)) parts.Push(current);
        foreach (var part in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = WindowsFileSystem.GetMetadata(part);
            if (state.DeletePending || (state.IsReparsePoint && !state.IsCloudPlaceholder)) throw new IOException("La ruta OneDrive contiene una redirección ajena a Cloud Files.");
        }
    }
    private static bool ExpectedIo(Exception ex) => ex is IOException or UnauthorizedAccessException or NotSupportedException or COMException or ArgumentException;
    private static string Id(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant())))[..24].ToLowerInvariant();
    private sealed class CloudLimitException : Exception;
}
