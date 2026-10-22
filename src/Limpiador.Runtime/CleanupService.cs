using System.Runtime.Versioning;
using Limpiador.Core;
using Limpiador.Windows;

namespace Limpiador.Runtime;

[SupportedOSPlatform("windows10.0.17763")]
public sealed class CleanupService
{
    private readonly ScanOptions options;
    private readonly SafetyPolicy policy;
    private readonly WindowsMetadataProvider metadata;
    private readonly HistoryStore history;
    private readonly SemaphoreSlim gate = new(1, 1);
    private Dictionary<string, Candidate> candidates = new(StringComparer.Ordinal);
    public HistoryStore History => history;

    public CleanupService(ScanOptions options, WindowsMetadataProvider metadata, HistoryStore? history = null)
    {
        WindowsFileSystem.EnsureWindows();
        this.options = options; this.metadata = metadata; policy = new(options);
        this.history = history ?? new HistoryStore(Path.Combine(WindowsHost.DataDirectory, "History"));
    }
    public void SetScan(ScanResult result) => candidates = result.Candidates.ToDictionary(x => x.Id, StringComparer.Ordinal);

    public async Task<OperationReport> ExecuteAsync(IReadOnlyList<string> ids, IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { return await Task.Run(() => ExecuteCoreAsync(ids, progress, cancellationToken), CancellationToken.None); }
        finally { gate.Release(); }
    }

    private async Task<OperationReport> ExecuteCoreAsync(IReadOnlyList<string> ids, IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var requested = ids.Distinct().Select(id => candidates.TryGetValue(id, out var value) ? value :
            throw new ArgumentException("La selección no pertenece al último análisis.")).ToArray();
        if (requested.Length == 0) return new("No hay elementos seleccionados.", [], [], 0);
        // Only explicitly requested IDs reach this boundary. The maximum plan is reviewed in the UI first.
        var plan = SelectionPlanner.Create(requested, ids);
        var selected = plan.Items.ToArray();
        var record = new HistoryRecord { ApprovedRoots = policy.ApprovedRoots.ToList() }; history.Save(record);
        var completed = new List<string>();
        foreach (var omitted in requested.ExceptBy(selected.Select(x => x.Id), x => x.Id))
            record.Details.Add($"Omitido: {omitted.Title}. {plan.ExclusionReasons.GetValueOrDefault(omitted.Id) ?? "No está incluido en el plan permitido."}");
        var before = FreeSpace();
        var finishedNormally = false;
        try
        {
            for (var index = 0; index < selected.Length; index++)
            {
                if (cancellationToken.IsCancellationRequested) { record.Details.Add("Detenido entre operaciones por solicitud del usuario."); break; }
                var item = selected[index];
                progress?.Report(new($"Comprobando {item.Title}", index, selected.Length));
                try
                {
                    if (WindowsHost.AreProcessesRunning(item.ProcessNames) != false)
                        throw new IOException("Hay procesos asociados en uso o no se pudo comprobar su estado.");
                    if (item.Action == ActionKind.OfficialCommand)
                    {
                        await RunCommandAsync(item, record, cancellationToken);
                    }
                    else if (item.Action == ActionKind.CloudDehydrate)
                    {
                        var cloudResult = AdvancedWindowsAnalysis.ExecuteCloud(item, options, metadata, cancellationToken);
                        record.Details.Add($"Copia local liberada sin borrar de OneDrive: {cloudResult.Path}; {cloudResult.SavedBytes:N0} bytes. Se descarga de nuevo al abrirla.");
                    }
                    else
                    {
                        // A fresh provider avoids replacing the original native snapshots kept by the scan.
                        var current = SnapshotValidator.Validate(item, policy, new WindowsMetadataProvider(), cancellationToken);
                        foreach (var entry in current)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var original = metadata.Expected(entry.Path);
                            if (original is null || !original.SameVersion(WindowsFileSystem.GetMetadata(entry.Path)))
                                throw new IOException("Cambió la identidad o el estado del archivo después del análisis.");
                        }
                        RestartManager.RequireUnlocked(current.Where(x => !x.IsDirectory).Select(x => x.Path), cancellationToken);
                        // ReFS takes its own exclusive source/target handles and verifies SHA-256 inside that lock.
                        using var preservedCopyLease = item.Action == ActionKind.Delete ? LockPreservedCopy(item, cancellationToken) : null;
                        progress?.Report(new($"Procesando {item.Title}", index, selected.Length));
                        cancellationToken.ThrowIfCancellationRequested();
                        switch (item.Action)
                        {
                            case ActionKind.Delete:
                                var recycle = RecycleBinService.Recycle(item.Paths, Path.Combine(history.DirectoryPath, "Recycle"), journal =>
                                {
                                    record.RecycleJournals.Add(journal); history.Save(record);
                                }, current.ToDictionary(x => x.Path, x => metadata.Expected(x.Path)!, StringComparer.OrdinalIgnoreCase));
                                foreach (var result in recycle.Items)
                                    record.Details.Add($"{result.State}: {result.OriginalPath}{(string.IsNullOrEmpty(result.Error) ? "" : " — " + result.Error)}");
                                if (recycle.Aborted || recycle.Items.Any(x => x.State != RecycleItemState.Recycled))
                                    throw new IOException("Windows no completó todos los movimientos a la Papelera; consulta los detalles.");
                                break;
                            case ActionKind.Compress:
                            case ActionKind.Decompress:
                                foreach (var file in current.Where(x => !x.IsDirectory))
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    // Durable undo intent is recorded before the filesystem call.
                                    if (item.Action == ActionKind.Compress && (file.Attributes & FileAttributes.Compressed) == 0)
                                    {
                                        record.CompressedFiles.Add(new(file.Path, file.FileId!, file.Length, file.LastWriteTimeUtc)); history.Save(record);
                                    }
                                    var result = NtfsCompression.SetCompressed(file.Path, item.Action == ActionKind.Compress, metadata.Expected(file.Path), cancellationToken);
                                    record.Details.Add($"{file.Path}: cambio de espacio asignado {result.SavedBytes:N0} bytes.");
                                }
                                break;
                            case ActionKind.CloneDuplicates:
                                if (item.PreservedPath is null) throw new IOException("Falta el original conservado.");
                                foreach (var file in current.Where(x => !x.IsDirectory))
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    var result = RefsBlockCloner.CloneIdentical(item.PreservedPath, file.Path,
                                        metadata.Expected(item.PreservedPath), metadata.Expected(file.Path), cancellationToken);
                                    record.Details.Add($"Bloques compartidos en ReFS: {result.TargetPath}, {result.ClonedBytes:N0} bytes.");
                                }
                                break;
                            case ActionKind.CompactDatabase:
                                if (current.Count != 1 || current[0].IsDirectory) throw new IOException("Solo se compacta una base SQLite individual por operación.");
                                var compacted = SqliteCompactor.Compact(current[0].Path, metadata.Expected(current[0].Path));
                                record.Details.Add($"SQLite compactada: {compacted.Path}; {compacted.ReclaimedBytes:N0} bytes recuperados, integridad y contenido lógico verificados.");
                                break;
                            default: throw new NotSupportedException("Esta acción requiere su comprobación específica y no se ejecutará como borrado.");
                        }
                    }
                    completed.Add(item.Id);
                }
                catch (OperationCanceledException) { record.Details.Add("Detenido antes de modificar el siguiente elemento."); break; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
                { record.Details.Add($"Omitido {item.Title}: {ex.Message}"); }
                history.Save(record);
            }
            finishedNormally = true;
        }
        catch (Exception ex)
        {
            record.Details.Add(DescribeFailure(ex));
            throw;
        }
        finally
        {
            record.Status = cancellationToken.IsCancellationRequested ? "Detenido" :
                !finishedNormally ? "Interrumpido por un error" :
                completed.Count == requested.Length ? "Finalizado" : "Finalizado con elementos omitidos";
            // Even partial operations change snapshots; a fresh scan is required before another cleanup.
            candidates.Clear();
            history.Save(record);
        }
        var measured = FreeSpace() - before;
        var summary = $"{completed.Count} de {requested.Length} elementos completados. Cambio libre observado: {measured:N0} bytes. Lo enviado a la Papelera ocupa espacio hasta que se vacíe.";
        progress?.Report(new("Operación terminada. Vuelve a analizar para actualizar los resultados.", selected.Length, selected.Length));
        return new(summary, record.Details, completed, measured, record.Id);
    }

    private async Task RunCommandAsync(Candidate item, HistoryRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var command = item.Command ?? throw new IOException("Falta el comando de mantenimiento.");
        if (item.Paths.Any(path => policy.IsExcludedPath(path) || policy.ContainsExcludedPath(path)))
            throw new IOException("El comando afecta a una carpeta excluida de la limpieza.");
        if (command.Kind != OfficialCommandKind.GitGarbageCollect && policy.ExcludedRoots.Count > 0)
            throw new IOException("El comando global no permite garantizar las exclusiones de carpetas. Administra esta limpieza desde la propia herramienta.");
        if (command.Kind == OfficialCommandKind.GitGarbageCollect)
        {
            if (command.WorkingDirectory is null || !policy.Validate(command.WorkingDirectory).Allowed)
                throw new IOException("El repositorio no está dentro de una ruta autorizada.");
            var git = Path.Combine(command.WorkingDirectory, ".git");
            if (!Directory.Exists(git) || (File.GetAttributes(git) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("El repositorio ya no coincide o usa una redirección.");
            var pending = new Stack<string>(); pending.Push(git);
            while (pending.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                        throw new IOException("El almacenamiento de Git contiene enlaces o archivos sin conexión.");
                    if (path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) throw new IOException("Git tiene una operación pendiente.");
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        record.Details.Add($"Comando iniciado: {command.Kind}"); history.Save(record);
        record.Details.Add(await OfficialCommandRunner.RunAsync(command, cancellationToken));
    }

    private FileStream? LockPreservedCopy(Candidate item, CancellationToken cancellationToken)
    {
        if (item.PreservedPath is null) return null;
        if (!policy.Validate(item.PreservedPath).Allowed || item.SHA256 is null)
            throw new IOException("No se pudo comprobar la copia que se conservará.");
        RestartManager.RequireUnlocked([item.PreservedPath], cancellationToken);
        var preserved = new FileStream(item.PreservedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (!CancellableHash.Compute(preserved, cancellationToken).Equals(item.SHA256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Una copia cambió desde la comparación; no se elimina ningún duplicado.");
            foreach (var path in item.Paths)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!CancellableHash.Compute(stream, cancellationToken).Equals(item.SHA256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Una copia cambió desde la comparación; no se elimina ningún duplicado.");
            }
            return preserved; // Deny writes/deletion of the original until the operation on its copies has finished.
        }
        catch { preserved.Dispose(); throw; }
    }

    public async Task<OperationReport> RestoreAsync(string id, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                var record = history.Read(id);
                if (!record.CanRestore) return new OperationReport("No hay operaciones recuperables.", [], [], 0);
                var restorePolicy = new SafetyPolicy(new ScanOptions
                {
                    ProfilePath = options.ProfilePath, LocalAppDataPath = options.LocalAppDataPath,
                    RoamingAppDataPath = options.RoamingAppDataPath, TempPath = options.TempPath,
                    PersonalRoots = options.PersonalRoots, AdditionalCloudRoots = options.AdditionalCloudRoots,
                    AllowedRoots = record.ApprovedRoots, ExcludedRoots = options.ExcludedRoots
                });
                var details = new List<string>(); var success = true;
                try
                {
                    foreach (var journal in record.RecycleJournals)
                    {
                        if (cancellationToken.IsCancellationRequested) { success = false; break; }
                        if (!SafetyPolicy.IsWithin(journal, Path.Combine(history.DirectoryPath, "Recycle"))) throw new IOException("Historial de Papelera fuera de su carpeta.");
                        progress?.Report(new("Restaurando desde la Papelera…", 0, 0));
                        try
                        {
                            var pendingRestore = RecycleBinService.ReadJournal(journal);
                            if (pendingRestore.Items.Any(item => item.State == RecycleItemState.Recycled &&
                                (restorePolicy.IsExcludedPath(item.OriginalPath) || restorePolicy.ContainsExcludedPath(item.OriginalPath))))
                                throw new IOException("Este historial afecta a una carpeta excluida. Revisa tus preferencias antes de restaurar.");
                            cancellationToken.ThrowIfCancellationRequested();
                            var result = RecycleBinService.Restore(journal);
                            foreach (var item in result.Items)
                            {
                                details.Add($"{item.State}: {item.OriginalPath} {item.Error}");
                                if (item.State != RecycleItemState.Restored &&
                                    !(item.State is RecycleItemState.Failed or RecycleItemState.Prepared &&
                                      (File.Exists(item.OriginalPath) || Directory.Exists(item.OriginalPath)))) success = false;
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { success = false; details.Add(ex.Message); }
                    }
                    foreach (var file in record.CompressedFiles)
                    {
                        if (cancellationToken.IsCancellationRequested) { success = false; break; }
                        try
                        {
                            var decision = restorePolicy.Validate(file.Path, allowAppFiles: true);
                            if (!decision.Allowed) throw new IOException(decision.Reason);
                            var state = WindowsFileSystem.GetMetadata(file.Path); var current = WindowsMetadataProvider.Convert(state);
                            if (current.FileId != file.FileId || current.Length != file.Length || current.LastWriteTimeUtc != file.LastWriteUtc)
                                throw new IOException("El archivo cambió después de comprimirse; no se descomprime automáticamente.");
                            NtfsCompression.SetCompressed(file.Path, false, state, cancellationToken);
                            details.Add($"Descomprimido: {file.Path}");
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { success = false; details.Add($"{file.Path}: {ex.Message}"); }
                    }
                    var stopped = cancellationToken.IsCancellationRequested && !success;
                    if (stopped)
                        details.Add("Restauración detenida entre operaciones. Los elementos pendientes siguen disponibles en el historial.");
                    record.Restored = success;
                    record.Status = stopped ? "Restauración detenida" : success ? "Restaurado" : "Restauración parcial";
                    return new OperationReport(stopped ? "Restauración detenida: revisa los detalles y los elementos pendientes."
                        : success ? "Restauración completada." : "Restauración parcial: revisa los detalles.", details, [], 0, record.Id);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    record.Restored = false;
                    record.Status = "Restauración detenida";
                    details.Add("Restauración detenida antes de modificar el siguiente elemento. Los cambios ya completados constan en el historial.");
                    throw;
                }
                catch (Exception ex)
                {
                    record.Status = "Restauración interrumpida";
                    details.Add(DescribeFailure(ex));
                    throw;
                }
                finally
                {
                    candidates.Clear();
                    record.Details.AddRange(details);
                    history.Save(record);
                }
            }, CancellationToken.None);
        }
        finally { gate.Release(); }
    }

    private static long FreeSpace() => WindowsHost.Disks().Sum(x => x.AvailableBytes);

    private static string DescribeFailure(Exception error) => "Operación interrumpida: " +
        (error is AggregateException aggregate
            ? string.Join(" | ", aggregate.Flatten().InnerExceptions.Select(x => x.Message))
            : error.Message);
}
