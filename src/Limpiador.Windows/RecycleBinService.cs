using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Limpiador.Windows;

public enum RecycleItemState { Prepared, Recycled, Failed, Restored }
public sealed record RecycleItemResult(string OriginalPath, string? RecyclePath, RecycleItemState State, string? Error);
public sealed record RecycleBatchResult(string JournalPath, IReadOnlyList<RecycleItemResult> Items, bool Aborted);

[SupportedOSPlatform("windows10.0.17763")]
public static class RecycleBinService
{
    // The source flags in PreDeleteItem must also allow recycling; permanent deletion is vetoed.
    // NOERRORUI + EARLYFAILURE turn a full/disabled/unavailable Bin into a failed operation.
    private const uint RecycleFlags = 0x00000004 | 0x00000010 | 0x00000400 | 0x00002000 | 0x00100000 | 0x00080000 | 0x20000000;
    // FOF_RENAMEONCOLLISION prevents replacement even if another process creates the destination
    // after our absence check. PreMoveItem still refuses any destination/name different from the journal.
    private const uint RestoreFlags = 0x00000004 | 0x00000008 | 0x00000010 | 0x00000400 | 0x00002000 | 0x00100000 | 0x20000000;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static RecycleBatchResult Recycle(IEnumerable<string> paths, string historyDirectory, Action<string>? journalPrepared = null, IReadOnlyDictionary<string, NativeFileMetadata>? expected = null)
    {
        WindowsFileSystem.EnsureWindows();
        expected = expected is null ? null : new Dictionary<string, NativeFileMetadata>(expected, StringComparer.OrdinalIgnoreCase);
        var items = paths.Select(WindowsFileSystem.NormalizeLocalPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (items.Length == 0) throw new ArgumentException("No hay archivos seleccionados.");
        if (items.Any(a => items.Any(b => !a.Equals(b, StringComparison.OrdinalIgnoreCase) && a.StartsWith(b.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException("No selecciones una carpeta y un elemento dentro de ella a la vez.");
        historyDirectory = WindowsFileSystem.NormalizeLocalPath(historyDirectory);
        if (items.Any(p => historyDirectory.Equals(p, StringComparison.OrdinalIgnoreCase) || historyDirectory.StartsWith(p.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("El historial no puede estar dentro de los elementos que se van a reciclar.");
        Directory.CreateDirectory(historyDirectory);
        using var historyLease = new NativePathLease(historyDirectory, includeLeaf: true);
        var journal = new RecycleJournal { Id = Guid.NewGuid(), CreatedUtc = DateTime.UtcNow };
        foreach (var path in items)
        {
            using var lease = new NativePathLease(path);
            ValidateTree(path, expected);
            var before = WindowsFileSystem.GetMetadata(path);
            if (expected is not null)
            {
                if (!expected.TryGetValue(path, out var observed)) throw new IOException("Falta el estado analizado de un elemento seleccionado.");
                WindowsFileSystem.RequireExpected(before, observed);
            }
            journal.Entries.Add(new RecycleEntry { OriginalPath = path, Before = before });
        }
        var journalPath = Path.Combine(historyDirectory, $"recycle-{journal.Id:N}.json");
        Save(journalPath, journal); // Durable intent exists before any Shell operation.
        journalPrepared?.Invoke(journalPath);
        return RunSta(() =>
        {
            var aborted = false;
            foreach (var entry in journal.Entries)
            {
                try
                {
                    using var lease = new NativePathLease(entry.OriginalPath);
                    WindowsFileSystem.RequireExpected(WindowsFileSystem.GetMetadata(entry.OriginalPath), entry.Before);
                    ValidateTree(entry.OriginalPath, expected);
                    var sink = new RecycleProgressSink(entry, journal, journalPath, restore: false, expected);
                    Execute(entry.OriginalPath, null, sink);
                    if (sink.Error is not null) throw sink.Error;
                    if (entry.State != RecycleItemState.Recycled) throw new IOException("El Shell no confirmó que el elemento esté en la Papelera.");
                }
                catch (Exception ex)
                {
                    entry.Error = ex.Message;
                    if (entry.State == RecycleItemState.Prepared) entry.State = RecycleItemState.Failed;
                    Save(journalPath, journal); aborted = true;
                    break; // A safety failure cancels subsequent operations.
                }
            }
            return Result(journalPath, journal, aborted);
        });
    }

    public static RecycleBatchResult Restore(string journalPath)
    {
        WindowsFileSystem.EnsureWindows(); journalPath = WindowsFileSystem.NormalizeLocalPath(journalPath);
        using var historyLease = new NativePathLease(journalPath);
        var journal = LoadJournal(journalPath);
        return RunSta(() =>
        {
            var aborted = false;
            foreach (var entry in journal.Entries.Where(e => e.State == RecycleItemState.Recycled))
            {
                try
                {
                    RequireRecycleIdentity(entry);
                    if (WindowsFileSystem.EntryExists(entry.OriginalPath)) throw new IOException("La ruta original ya existe; no se sobrescribirá.");
                    var parent = Path.GetDirectoryName(entry.OriginalPath)!;
                    if (!Directory.Exists(parent)) throw new IOException("La carpeta original no existe; créala antes de restaurar.");
                    using var destinationLease = new NativePathLease(entry.OriginalPath);
                    using var sourceLease = new NativePathLease(entry.RecyclePath!);
                    var sink = new RecycleProgressSink(entry, journal, journalPath, restore: true);
                    Execute(entry.RecycleParsingName!, parent, sink, entry.RecycleItemId);
                    if (sink.Error is not null) throw sink.Error;
                    if (entry.State != RecycleItemState.Restored) throw new IOException("El Shell no confirmó la restauración.");
                }
                catch (Exception ex) { entry.Error = ex.Message; Save(journalPath, journal); aborted = true; }
            }
            return Result(journalPath, journal, aborted);
        });
    }
    public static RecycleBatchResult ReadJournal(string journalPath)
    {
        journalPath = WindowsFileSystem.NormalizeLocalPath(journalPath);
        using var historyLease = new NativePathLease(journalPath);
        var journal = LoadJournal(journalPath);
        return Result(journalPath, journal, journal.Entries.Any(e => e.State is RecycleItemState.Prepared or RecycleItemState.Failed));
    }
    private static RecycleJournal LoadJournal(string path)
    {
        try
        {
            // Read a stable regular file. Do not follow a replaced journal into another path.
            using var handle = WindowsFileSystem.Open(path, NativeMethods.GenericRead, NativeMethods.ShareRead);
            var metadata = WindowsFileSystem.ReadMetadata(handle, path);
            if (metadata.IsDirectory || metadata.IsReparsePoint || metadata.DeletePending || metadata.HardLinkCount != 1)
                throw new IOException("El historial no es un archivo ordinario independiente.");
            using var stream = new FileStream(handle, FileAccess.Read);
            var journal = JsonSerializer.Deserialize<RecycleJournal>(stream, Json) ?? throw new IOException("Historial inválido.");
            if (journal.Version != 1 || journal.Id == Guid.Empty || journal.Entries is null || journal.Entries.Count == 0)
                throw new IOException("Versión o contenido de historial no admitido.");
            var originalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in journal.Entries)
            {
                if (entry is null || entry.Before is null || !Enum.IsDefined(entry.State)) throw new IOException("El historial contiene un estado incompleto o desconocido.");
                var original = WindowsFileSystem.NormalizeLocalPath(entry.OriginalPath);
                var observed = WindowsFileSystem.NormalizeLocalPath(entry.Before.Path);
                if (!original.Equals(entry.OriginalPath, StringComparison.OrdinalIgnoreCase) || !original.Equals(observed, StringComparison.OrdinalIgnoreCase)
                    || original.Equals(Path.GetPathRoot(original), StringComparison.OrdinalIgnoreCase) || !originalPaths.Add(original))
                    throw new IOException("La ruta original no coincide con el archivo analizado o está repetida.");
                if (entry.Before.FileId is null || entry.Before.FileId.Length != 32 || !entry.Before.FileId.All(Uri.IsHexDigit)
                    || entry.Before.LogicalBytes < 0 || entry.Before.AllocatedBytes < 0 || entry.Before.IsReparsePoint || entry.Before.DeletePending)
                    throw new IOException("La identidad guardada del archivo no es válida.");
                if (entry.State is RecycleItemState.Recycled or RecycleItemState.Restored)
                {
                    if (string.IsNullOrWhiteSpace(entry.RecyclePath) || string.IsNullOrWhiteSpace(entry.RecycleParsingName) || entry.RecycleItemId is null)
                        throw new IOException("El historial no contiene un elemento recuperable completo.");
                    _ = NormalizeRecyclePath(entry.RecyclePath);
                    _ = DecodeItemId(entry.RecycleItemId);
                }
            }
            // Validate every entry before restoring any item, including an unexpected nested selection.
            foreach (var original in originalPaths)
                for (var parent = Path.GetDirectoryName(original); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                    if (originalPaths.Contains(parent)) throw new IOException("El historial contiene rutas originales superpuestas.");
            return journal;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or OverflowException)
        { throw new IOException("El historial no tiene un formato de recuperación válido.", ex); }
    }
    private static RecycleBatchResult Result(string path, RecycleJournal journal, bool aborted) =>
        new(path, journal.Entries.Select(e => new RecycleItemResult(e.OriginalPath, e.RecyclePath, e.State, e.Error)).ToArray(), aborted);

    private static void ValidateTree(string root, IReadOnlyDictionary<string, NativeFileMetadata>? expected = null)
    {
        _ = WindowsFileSystem.GetDiskSpace(root);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>(); var pending = new Stack<string>(); pending.Push(root); var count = 0;
        while (pending.TryPop(out var path))
        {
            if (++count > 250000) throw new IOException("Demasiados elementos para comprobar los bloqueos de forma segura en una sola operación.");
            var state = WindowsFileSystem.GetMetadata(path);
            seen.Add(path);
            if (expected is not null)
            {
                if (!expected.TryGetValue(path, out var observed) || !state.SameVersion(observed))
                    throw new IOException("Un elemento del árbol cambió o apareció después del análisis; se cancela el reciclaje.");
            }
            if (state.IsReparsePoint || state.DeletePending || (state.Attributes & (FileAttributes.System | FileAttributes.Offline)) != 0)
                throw new IOException("La selección contiene archivos especiales, enlaces o datos de la nube; se omite.");
            if (state.IsDirectory)
                foreach (var child in Directory.EnumerateFileSystemEntries(path)) pending.Push(child);
            else files.Add(path);
        }
        if (expected is not null && expected.Keys.Any(path =>
            (path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) && !seen.Contains(path)))
            throw new IOException("Un elemento del árbol desapareció después del análisis; se cancela el reciclaje.");
        RestartManager.RequireUnlocked(files);
    }
    private static void RequireRecycleIdentity(RecycleEntry entry)
    {
        if (entry.RecyclePath is null || entry.RecycleParsingName is null || entry.RecycleItemId is null || entry.Before is null) throw new IOException("El historial no contiene una ruta recuperable.");
        var path = NormalizeRecyclePath(entry.RecyclePath);
        var metadata = WindowsFileSystem.GetMetadata(path);
        if (metadata.VolumeSerial != entry.Before.VolumeSerial || metadata.FileId != entry.Before.FileId || metadata.IsReparsePoint || metadata.DeletePending || metadata.IsDirectory != entry.Before.IsDirectory)
            throw new IOException("La identidad del elemento en la Papelera cambió; no se restaurará automáticamente.");
    }
    private static string NormalizeRecyclePath(string path)
    {
        path = WindowsFileSystem.NormalizeLocalPath(path);
        var prefix = Path.Combine(Path.GetPathRoot(path)!, "$Recycle.Bin") + "\\";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("La ruta guardada no está en la Papelera de Windows.");
        return path;
    }
    private static void Execute(string source, string? destination, RecycleProgressSink sink, string? itemId = null)
    {
        var operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), throwOnError: true)!)!;
        IShellItem? item = null, folder = null;
        try
        {
            operation.SetOperationFlags(destination is null ? RecycleFlags : RestoreFlags);
            item = itemId is null ? CreateItem(source) : CreateItemFromId(itemId);
            if (itemId is not null && !ShellPath(item).Equals(sink.Entry.RecyclePath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("El objeto de la Papelera no coincide con la ruta guardada.");
            if (destination is null) operation.DeleteItem(item, sink);
            else
            {
                folder = CreateItem(destination);
                operation.MoveItem(item, folder, Path.GetFileName(sink.Entry.OriginalPath), sink);
            }
            operation.PerformOperations();
            if (sink.Error is not null) throw sink.Error;
            operation.GetAnyOperationsAborted(out var aborted);
            if (aborted) throw new IOException($"Windows canceló la operación. Último callback: {sink.LastCallback}.");
        }
        catch (COMException ex)
        {
            var detail = sink.Error?.Message ?? $"Último callback: {sink.LastCallback}. {ex.Message}";
            throw new IOException($"El Shell rechazó la operación (HRESULT 0x{ex.HResult:X8}). {detail}",
                sink.Error is null ? ex : new AggregateException(sink.Error, ex));
        }
        finally
        {
            if (folder is not null) Marshal.FinalReleaseComObject(folder);
            if (item is not null) Marshal.FinalReleaseComObject(item);
            Marshal.FinalReleaseComObject(operation);
            GC.KeepAlive(sink);
        }
    }
    private static IShellItem CreateItem(string path)
    {
        var id = typeof(IShellItem).GUID; Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, ref id, out var item)); return item;
    }
    private static string CaptureItemId(IShellItem item)
    {
        Marshal.ThrowExceptionForHR(SHGetIDListFromObject(item, out var pointer));
        try
        {
            var size = ILGetSize(pointer);
            if (size < 2 || size > 65536) throw new IOException("Identificador del Shell fuera de límites.");
            var bytes = new byte[size]; Marshal.Copy(pointer, bytes, 0, bytes.Length); return Convert.ToBase64String(bytes);
        }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }
    private static IShellItem CreateItemFromId(string encoded)
    {
        var bytes = DecodeItemId(encoded);
        var pointer = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var iid = typeof(IShellItem).GUID; Marshal.ThrowExceptionForHR(SHCreateItemFromIDList(pointer, ref iid, out var item)); return item;
        }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }
    private static byte[] DecodeItemId(string encoded)
    {
        if (encoded.Length > 87384) throw new IOException("Identificador de Papelera fuera de límites.");
        var bytes = Convert.FromBase64String(encoded);
        if (bytes.Length < 2 || bytes.Length > 65536) throw new IOException("Identificador de Papelera inválido.");
        var offset = 0;
        while (offset + 2 <= bytes.Length)
        {
            var count = BitConverter.ToUInt16(bytes, offset);
            if (count == 0) break;
            if (count < 2 || offset + count > bytes.Length) throw new IOException("Identificador de Papelera truncado.");
            offset += count;
        }
        if (offset != bytes.Length - 2 || BitConverter.ToUInt16(bytes, offset) != 0) throw new IOException("Identificador de Papelera incompleto.");
        return bytes;
    }
    internal static string ShellPath(IShellItem item) => ShellName(item, 0x80058000);
    private static string ShellName(IShellItem item, uint kind)
    {
        item.GetDisplayName(kind, out var pointer);
        try { return Marshal.PtrToStringUni(pointer) ?? throw new IOException("El Shell no devolvió la ruta del elemento."); }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }
    private static T RunSta<T>(Func<T> work)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return work();
        T? result = default; Exception? error = null;
        var thread = new Thread(() => { try { result = work(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
        return result!;
    }
    private static void Save(string path, RecycleJournal journal)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { JsonSerializer.Serialize(stream, journal, Json); stream.Flush(flushToDisk: true); }
        if (!MoveFileExW(temporary, path, 1 | 8)) throw NativeMethods.Error("No se pudo guardar el historial; se cancela la operación.");
    }
    [DllImport("shell32.dll")] private static extern int SHGetIDListFromObject([MarshalAs(UnmanagedType.IUnknown)] object item, out nint id);
    [DllImport("shell32.dll")] private static extern uint ILGetSize(nint id);
    [DllImport("shell32.dll")] private static extern int SHCreateItemFromIDList(nint id, ref Guid iid, out IShellItem item);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(string path, nint context, ref Guid iid, out IShellItem item);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool MoveFileExW(string oldPath, string newPath, uint flags);

    public sealed class RecycleJournal
    {
        public int Version { get; set; } = 1;
        public Guid Id { get; set; }
        public DateTime CreatedUtc { get; set; }
        public List<RecycleEntry> Entries { get; set; } = [];
    }
    public sealed class RecycleEntry
    {
        public string OriginalPath { get; set; } = "";
        public string? RecyclePath { get; set; }
        public string? RecycleParsingName { get; set; }
        public string? RecycleItemId { get; set; }
        public NativeFileMetadata? Before { get; set; }
        public RecycleItemState State { get; set; }
        public string? Error { get; set; }
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class RecycleProgressSink : IFileOperationProgressSink
    {
        private readonly RecycleJournal journal;
        private readonly string journalPath;
        private readonly bool restore;
        private readonly IReadOnlyDictionary<string, NativeFileMetadata>? expected;
        internal RecycleEntry Entry { get; }
        internal Exception? Error { get; private set; }
        internal string LastCallback { get; private set; } = "ninguno";
        internal RecycleProgressSink(RecycleEntry entry, RecycleJournal journal, string journalPath, bool restore, IReadOnlyDictionary<string, NativeFileMetadata>? expected = null)
        { Entry = entry; this.journal = journal; this.journalPath = journalPath; this.restore = restore; this.expected = expected; }
        private int Guard(string context, Action action)
        {
            LastCallback = context;
            try { action(); return 0; }
            catch (Exception ex) { Error ??= new IOException($"{context}: {ex.Message}", ex); return unchecked((int)0x80004004); }
        }
        private int Reject(string context) => Guard(context, () => throw new IOException("El Shell propuso una operación no autorizada; se cancela."));
        public int PreDeleteItem(uint flags, IShellItem item) => Guard($"PreDeleteItem(flags=0x{flags:X8})", () =>
        {
            if (restore || (flags & 0x80) == 0) throw new IOException("Windows no ofrece reciclaje recuperable; se impide borrar permanentemente.");
            var path = WindowsFileSystem.NormalizeLocalPath(ShellPath(item));
            if (!path.Equals(Entry.OriginalPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("El Shell cambió la selección.");
            WindowsFileSystem.RequireExpected(WindowsFileSystem.GetMetadata(path), Entry.Before);
            ValidateTree(path, expected);
        });
        public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created) => Guard($"PostDeleteItem(flags=0x{flags:X8}, resultado=0x{result:X8})", () =>
        {
            Marshal.ThrowExceptionForHR(result);
            if (created is null) throw new IOException("Windows no devolvió un elemento recuperable. Revisa la Papelera; no se ejecutarán más operaciones.");
            Entry.RecyclePath = ShellPath(created);
            Entry.RecycleItemId = CaptureItemId(created);
            Entry.RecycleParsingName = ShellName(created, 0x80028000); // Preserve the Recycle Bin namespace item for Shell restore.
            Entry.State = RecycleItemState.Recycled;
            RequireRecycleIdentity(Entry); Save(journalPath, journal);
        });
        public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, string? name) => Guard($"PreMoveItem(flags=0x{flags:X8}, nombre={name ?? "<null>"})", () =>
        {
            // TSF_OVERWRITE_EXIST is a transfer policy, not evidence that a destination exists;
            // Windows can set it for an ordinary move to a new name. Collision protection belongs
            // to RestoreFlags plus the exact path/absence checks below, not a blanket veto of bit 0x2.
            if (!restore || (flags & 0x400) != 0) throw new IOException("No se permite copiar y borrar durante una restauración.");
            var source = WindowsFileSystem.NormalizeLocalPath(ShellPath(item));
            var parent = WindowsFileSystem.NormalizeLocalPath(ShellPath(destination));
            if (!source.Equals(Entry.RecyclePath, StringComparison.OrdinalIgnoreCase)
                || !parent.Equals(Path.GetDirectoryName(Entry.OriginalPath), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(name, Path.GetFileName(Entry.OriginalPath), StringComparison.OrdinalIgnoreCase))
                throw new IOException($"El Shell cambió el origen o el destino de la restauración: origen={source}; carpeta={parent}; nombre={name ?? "<null>"}.");
            var parentState = WindowsFileSystem.GetMetadata(parent);
            if (!parentState.IsDirectory || parentState.IsReparsePoint || parentState.VolumeSerial != Entry.Before!.VolumeSerial)
                throw new IOException("La carpeta de destino cambió o pertenece a otro volumen.");
            RequireRecycleIdentity(Entry);
            if (WindowsFileSystem.EntryExists(Entry.OriginalPath)) throw new IOException("El destino de restauración ya existe.");
        });
        public int PostMoveItem(uint flags, IShellItem item, IShellItem destination, string? name, int result, IShellItem? created) => Guard($"PostMoveItem(flags=0x{flags:X8}, nombre={name ?? "<null>"}, resultado=0x{result:X8})", () =>
        {
            Marshal.ThrowExceptionForHR(result);
            if (created is not null)
            {
                var actualPath = WindowsFileSystem.NormalizeLocalPath(ShellPath(created));
                if (!actualPath.Equals(Entry.OriginalPath, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"El Shell conservó el elemento en otra ruta para evitar una colisión: {actualPath}. Revisa esa ruta; no se confirma la restauración original.");
            }
            var restored = WindowsFileSystem.GetMetadata(Entry.OriginalPath);
            if (restored.VolumeSerial != Entry.Before!.VolumeSerial || restored.FileId != Entry.Before.FileId) throw new IOException("La identidad restaurada no coincide.");
            Entry.State = RecycleItemState.Restored; Entry.Error = null; Save(journalPath, journal);
        });
        public int StartOperations() => 0;
        public int FinishOperations(int result) => Guard($"FinishOperations(resultado=0x{result:X8})", () => Marshal.ThrowExceptionForHR(result));
        public int PreRenameItem(uint flags, IShellItem item, string? name) => Reject($"PreRenameItem(flags=0x{flags:X8}, nombre={name ?? "<null>"})");
        public int PostRenameItem(uint flags, IShellItem item, string? name, int result, IShellItem? created) => 0;
        public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, string? name) => Reject($"PreCopyItem(flags=0x{flags:X8}, nombre={name ?? "<null>"})");
        public int PostCopyItem(uint flags, IShellItem item, IShellItem destination, string? name, int result, IShellItem? created) => 0;
        public int PreNewItem(uint flags, IShellItem destination, string? name) => Reject($"PreNewItem(flags=0x{flags:X8}, nombre={name ?? "<null>"})");
        public int PostNewItem(uint flags, IShellItem destination, string? name, string? template, uint attributes, int result, IShellItem? created) => 0;
        public int UpdateProgress(uint total, uint current) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }
}
