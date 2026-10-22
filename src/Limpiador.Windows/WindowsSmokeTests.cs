using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Limpiador.Windows;

/// <summary>Integration tests create their own unique directory and never operate on existing user files.</summary>
[SupportedOSPlatform("windows10.0.17763")]
public static class WindowsSmokeTests
{
    public static Task<IReadOnlyList<string>> RunAsync(string fixtureParent, bool includeRecycle = true) => Task.Run(() => Run(fixtureParent, includeRecycle));
    public static IReadOnlyList<string> Run(string fixtureParent, bool includeRecycle = true)
    {
        WindowsFileSystem.EnsureWindows(); fixtureParent = WindowsFileSystem.NormalizeLocalPath(fixtureParent);
        var root = Path.Combine(fixtureParent, "Limpiador-native-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, ".limpiador-fixture");
        using (var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write)) stream.WriteByte(1);
        var results = new List<string>(); var passed = false;
        try
        {
            var file = Path.Combine(root, "compressible.txt");
            File.WriteAllText(file, string.Concat(Enumerable.Repeat("Fixture exclusivo de Limpiador Windows.\r\n", 100000)), Encoding.UTF8);
            var metadata = WindowsFileSystem.GetMetadata(file); var hash = Hash(file);
            Require(metadata.LogicalBytes == new FileInfo(file).Length && metadata.HardLinkCount == 1 && metadata.FileId.Length == 32, "Metadatos del archivo");
            Require(metadata.CreationTimeUtc > DateTime.UnixEpoch && metadata.AllocatedBytes > 0, "Fechas y tamaño asignado");
            results.Add("PASS metadatos, identidad de 128 bits y asignación física");
            Require(WindowsFileSystem.EntryExists(file) && !WindowsFileSystem.EntryExists(Path.Combine(root, "absent.txt")), "Existencia de entradas");
            ExpectIOException(() => WindowsFileSystem.EntryExists(Path.Combine(root, "invalid<name>.txt")), "Un error Win32 de nombre inválido no significa ausencia");
            results.Add("PASS comprobación de existencia distingue ausencia de errores Win32");
            var disk = WindowsFileSystem.GetDiskSpace(file);
            Require(disk.ClusterBytes > 0 && disk.AvailableBytes > 0, "Información del volumen");
            _ = KnownFolders.Get(KnownFolder.LocalAppData); _ = KnownFolders.Get(KnownFolder.Downloads);
            results.Add("PASS volumen y carpetas conocidas");
            ExpectFailure(() => RestartManager.GetLockingProcesses([root]), "Restart Manager debe fallar ante una carpeta");
            results.Add("PASS errores de Restart Manager cierran la operación");
            ExpectFailure(() => CloudFiles.GetState(file), "Un archivo ordinario no se considera sincronizado");
            results.Add("PASS archivo local excluido de Cloud Files");
            // A canceled request must stop before volume checks, locks, hashing or the first FSCTL.
            // Run both APIs on every supported test volume: cancellation must win even without ReFS.
            var canceledCopy = Path.Combine(root, "canceled-clone.txt"); File.Copy(file, canceledCopy);
            var canceledCopyMetadata = WindowsFileSystem.GetMetadata(canceledCopy);
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                ExpectCancellation(() => NtfsCompression.SetCompressed(file, true, metadata, cancellation.Token), cancellation.Token, "Compresión previamente cancelada");
                ExpectCancellation(() => RefsBlockCloner.CloneIdentical(file, canceledCopy, metadata, canceledCopyMetadata, cancellation.Token), cancellation.Token, "Clonación previamente cancelada");
                ExpectCancellation(() => CloudFiles.Dehydrate(file, metadata, cancellation.Token), cancellation.Token, "Deshidratación previamente cancelada antes de consultar OneDrive");
                ExpectCancellation(() => RestartManager.RequireUnlocked([file], cancellation.Token), cancellation.Token, "Consulta de bloqueos previamente cancelada");
                using var readHandle = WindowsFileSystem.Open(file, NativeMethods.GenericRead, NativeMethods.ShareRead);
                ExpectCancellation(() => NtfsCompression.Hash(readHandle, cancellation.Token), cancellation.Token, "Hash previo cancelado");
            }
            var afterCanceledSource = WindowsFileSystem.GetMetadata(file); var afterCanceledCopy = WindowsFileSystem.GetMetadata(canceledCopy);
            Require(metadata.SameVersion(afterCanceledSource) && metadata.AllocatedBytes == afterCanceledSource.AllocatedBytes
                && canceledCopyMetadata.SameVersion(afterCanceledCopy) && canceledCopyMetadata.AllocatedBytes == afterCanceledCopy.AllocatedBytes
                && Hash(file) == hash && Hash(canceledCopy) == hash, "Cancelación conserva identidad, contenido, atributos, fechas y asignación de ambos archivos");
            File.Delete(canceledCopy);
            results.Add("PASS cancelación previa: NTFS/ReFS/Cloud Files/hash/bloqueos se detienen sin cambiar contenido, atributos ni asignación");
            if (disk.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            {
                var compressed = NtfsCompression.SetCompressed(file, true, metadata);
                Require(compressed.Compressed && compressed.SavedBytes > 0 && Hash(file) == hash, "Compresión, ahorro y hash");
                Require(WindowsFileSystem.GetMetadata(file).FileId == metadata.FileId, "Identidad tras compresión");
                var uncompressed = NtfsCompression.SetCompressed(file, false, WindowsFileSystem.GetMetadata(file));
                Require(!uncompressed.Compressed && Hash(file) == hash, "Descompresión y hash");
                results.Add("PASS compresión/descompresión NTFS, ahorro, SHA-256 e identidad");
                using (var open = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    ExpectFailure(() => NtfsCompression.SetCompressed(file, true), "Archivo abierto");
                Require(Hash(file) == hash, "Archivo abierto no cambió");
                results.Add("PASS archivo abierto excluido");
                var hardlink = Path.Combine(root, "hardlink.txt");
                if (!CreateHardLinkW(hardlink, file, 0)) throw NativeMethods.Error("No se pudo crear el enlace duro de prueba.");
                Require(WindowsFileSystem.GetMetadata(file).HardLinkCount == 2, "Número de enlaces duros");
                ExpectFailure(() => NtfsCompression.SetCompressed(file, true), "Archivo con varios enlaces");
                File.Delete(hardlink);
                results.Add("PASS enlaces duros detectados y excluidos");
                var stale = WindowsFileSystem.GetMetadata(file); File.AppendAllText(file, "Cambio posterior al análisis");
                ExpectFailure(() => NtfsCompression.SetCompressed(file, true, stale), "Metadatos desactualizados");
                results.Add("PASS cambio posterior al análisis excluido");
                var alias = Path.Combine(root, "redirect");
                if (CreateSymbolicLinkW(alias, root, 1 | 2))
                {
                    ExpectFailure(() => NtfsCompression.SetCompressed(Path.Combine(alias, "compressible.txt"), true), "Ancestro redirigido");
                    Directory.Delete(alias);
                    results.Add("PASS enlace de directorio excluido");
                }
                else results.Add("SKIP enlace de directorio: Windows no concedió creación de symlink");
            }
            else if (disk.FileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase))
            {
                var duplicate = Path.Combine(root, "duplicate.txt"); File.Copy(file, duplicate);
                var duplicateMetadata = WindowsFileSystem.GetMetadata(duplicate);
                var cloned = RefsBlockCloner.CloneIdentical(file, duplicate, metadata, duplicateMetadata);
                Require(cloned.ClonedBytes > 0 && Hash(duplicate) == hash, "Clonación ReFS");
                Require(WindowsFileSystem.GetMetadata(duplicate).FileId != metadata.FileId, "Copias independientes");
                Require(WindowsFileSystem.GetMetadata(duplicate).LastWriteUtc == duplicateMetadata.LastWriteUtc, "Fecha del contenido conservada en ReFS");
                using (var stream = new FileStream(duplicate, FileMode.Open, FileAccess.Write)) stream.WriteByte(0x58);
                Require(Hash(file) == hash && Hash(duplicate) != hash, "Independencia de escritura de ReFS");
                results.Add("PASS ReFS: bloques compartidos y escrituras independientes");
            }
            else results.Add("SKIP compresión/clonación: volumen distinto de NTFS/ReFS");
            results.AddRange(SqliteCompactor.RunFixtureTests(root));
            if (includeRecycle)
            {
                var guardedFolder = Path.Combine(root, "guarded-tree"); Directory.CreateDirectory(guardedFolder);
                var guardedChild = Path.Combine(guardedFolder, "child.txt"); File.WriteAllText(guardedChild, "Original fixture");
                Dictionary<string, NativeFileMetadata> GuardedSnapshot() => new(StringComparer.OrdinalIgnoreCase)
                { [guardedFolder] = WindowsFileSystem.GetMetadata(guardedFolder), [guardedChild] = WindowsFileSystem.GetMetadata(guardedChild) };
                var changedTree = RecycleBinService.Recycle([guardedFolder], Path.Combine(root, "history"),
                    _ => File.AppendAllText(guardedChild, "changed after validation"), GuardedSnapshot());
                Require(changedTree.Aborted && Directory.Exists(guardedFolder) && File.Exists(guardedChild), "Hijo cambiado después de intención no se recicla");
                var added = Path.Combine(guardedFolder, "new-child.txt");
                var addedTree = RecycleBinService.Recycle([guardedFolder], Path.Combine(root, "history"),
                    _ => File.WriteAllText(added, "New fixture"), GuardedSnapshot());
                Require(addedTree.Aborted && File.Exists(added) && Directory.Exists(guardedFolder), "Hijo agregado después de intención no se recicla");
                File.Delete(added);
                var removedTree = RecycleBinService.Recycle([guardedFolder], Path.Combine(root, "history"),
                    _ => File.Delete(guardedChild), GuardedSnapshot());
                Require(removedTree.Aborted && Directory.Exists(guardedFolder), "Hijo eliminado después de intención impide reciclar el árbol");
                results.Add("PASS Papelera: cambios, altas y bajas en descendientes posteriores al análisis impiden reciclar");
                var recycledFile = Path.Combine(root, "recycle.txt"); File.WriteAllText(recycledFile, "Fixture para verificar la Papelera y deshacer.");
                var recycledHash = Hash(recycledFile); string? preparedPath = null;
                var recycled = RecycleBinService.Recycle([recycledFile], Path.Combine(root, "history"), path =>
                { Require(File.Exists(path) && File.Exists(recycledFile), "Historial guardado antes de reciclar"); preparedPath = path; });
                Require(!recycled.Aborted && recycled.Items.Single().State == RecycleItemState.Recycled && !File.Exists(recycledFile), "Reciclaje confirmado");
                Require(preparedPath == recycled.JournalPath && recycled.Items.Single().RecyclePath is not null, "Ruta de recuperación persistida");
                var pristineJournal = File.ReadAllText(recycled.JournalPath);
                var redirectedPath = Path.Combine(root, "unexpected-restore.txt");
                string ModifiedJournal(Action<RecycleBinService.RecycleJournal> modify)
                {
                    var data = JsonSerializer.Deserialize<RecycleBinService.RecycleJournal>(pristineJournal)!;
                    modify(data); return JsonSerializer.Serialize(data);
                }
                var invalidJournals = new[]
                {
                    "{",
                    ModifiedJournal(j => j.Entries = null!),
                    ModifiedJournal(j => j.Entries[0].OriginalPath = redirectedPath),
                    ModifiedJournal(j => j.Entries[0].RecycleItemId = "invalid base64!"),
                    ModifiedJournal(j => j.Entries.Add(new() { OriginalPath = redirectedPath, Before = metadata, State = (RecycleItemState)999 }))
                };
                try
                {
                    foreach (var invalid in invalidJournals)
                    {
                        File.WriteAllText(recycled.JournalPath, invalid);
                        ExpectIOException(() => RecycleBinService.ReadJournal(recycled.JournalPath), "Historial inválido se informa como IOException");
                        ExpectIOException(() => RecycleBinService.Restore(recycled.JournalPath), "Todo el historial se valida antes de restaurar");
                        Require(!WindowsFileSystem.EntryExists(recycledFile) && !WindowsFileSystem.EntryExists(redirectedPath)
                            && WindowsFileSystem.EntryExists(recycled.Items.Single().RecyclePath!), "Historial inválido no mueve el fixture");
                    }
                }
                finally { File.WriteAllText(recycled.JournalPath, pristineJournal); }
                results.Add("PASS Papelera: JSON, estado, ruta y PIDL inválidos rechazan el lote completo sin mover datos");
                File.WriteAllText(recycledFile, "Nuevo archivo que debe conservarse.");
                var occupiedHash = Hash(recycledFile);
                var occupiedRestore = RecycleBinService.Restore(recycled.JournalPath);
                Require(occupiedRestore.Aborted && occupiedRestore.Items.Single().State == RecycleItemState.Recycled
                    && Hash(recycledFile) == occupiedHash && WindowsFileSystem.EntryExists(recycled.Items.Single().RecyclePath!), "Restauración no reemplaza un destino ocupado");
                File.Delete(recycledFile); // Only the new fixture above, never the recycled original.
                results.Add("PASS Papelera: destino ocupado se conserva y el original sigue recuperable");
                var restored = RecycleBinService.Restore(recycled.JournalPath);
                Require(!restored.Aborted && restored.Items.Single().State == RecycleItemState.Restored && Hash(recycledFile) == recycledHash, "Restauración verificada");
                results.Add("PASS Papelera Shell: intención duradera, ruta recuperable y restauración exacta");
                var folder = Path.Combine(root, "recycle-folder"); Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "nested.txt"), "Fixture anidado.");
                var recycledFolder = RecycleBinService.Recycle([folder], Path.Combine(root, "history"));
                Require(!recycledFolder.Aborted && !Directory.Exists(folder), "Reciclaje de carpeta");
                var restoredFolder = RecycleBinService.Restore(recycledFolder.JournalPath);
                Require(!restoredFolder.Aborted && File.ReadAllText(Path.Combine(folder, "nested.txt")) == "Fixture anidado.", "Restauración de carpeta");
                results.Add("PASS carpeta anidada reciclada y restaurada");
            }
            else results.Add("SKIP Papelera: prueba omitida explícitamente");
            passed = true; return results;
        }
        catch (Exception ex) { throw new IOException($"Falló una prueba nativa. Se conservan los fixtures y sus historiales en {root}.", ex); }
        finally
        {
            // Only this newly created fixture directory is removed, and only after every requested check passed.
            if (passed && File.Exists(marker)) Directory.Delete(root, recursive: true);
        }
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException("Verificación fallida: " + message); }
    private static void ExpectFailure(Action work, string message)
    {
        try { work(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or NotSupportedException) { return; }
        throw new InvalidOperationException("La operación debía rechazarse: " + message);
    }
    private static void ExpectIOException(Action work, string message)
    {
        try { work(); } catch (IOException) { return; }
        throw new InvalidOperationException("La operación debía informar IOException: " + message);
    }
    private static void ExpectCancellation(Action work, CancellationToken token, string message)
    {
        try { work(); }
        catch (OperationCanceledException ex) when (ex.CancellationToken == token && token.IsCancellationRequested) { return; }
        throw new InvalidOperationException("La operación debía cancelarse antes de modificar archivos: " + message);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLinkW(string link, string target, nint security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool CreateSymbolicLinkW(string link, string target, uint flags);
}
