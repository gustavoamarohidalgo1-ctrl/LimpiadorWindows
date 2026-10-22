using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Limpiador.Windows;

public sealed record NativeFileMetadata(string Path, long LogicalBytes, long AllocatedBytes, ulong VolumeSerial,
    string FileId, uint HardLinkCount, FileAttributes Attributes, DateTime LastWriteUtc, DateTime ChangeTimeUtc,
    uint ReparseTag, bool DeletePending, DateTime CreationTimeUtc)
{
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;
    public bool IsCloudPlaceholder => (ReparseTag & 0xffff0fff) == 0x9000001a;
    public bool SameVersion(NativeFileMetadata other) => VolumeSerial == other.VolumeSerial && FileId == other.FileId
        && LogicalBytes == other.LogicalBytes && LastWriteUtc == other.LastWriteUtc && ChangeTimeUtc == other.ChangeTimeUtc
        && Attributes == other.Attributes && HardLinkCount == other.HardLinkCount && !DeletePending && !other.DeletePending;
}
public sealed record DiskSpace(string VolumePath, string FileSystem, long AvailableBytes, long TotalBytes, long FreeBytes, uint ClusterBytes);

[SupportedOSPlatform("windows10.0.17763")]
public static class WindowsFileSystem
{
    public static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            throw new PlatformNotSupportedException("Limpiador necesita Windows 10 1809 o Windows 11.");
    }
    public static NativeFileMetadata GetMetadata(string path)
    {
        EnsureWindows(); path = NormalizeLocalPath(path);
        using var handle = Open(path, NativeMethods.ReadAttributes, NativeMethods.ShareRead | NativeMethods.ShareWrite | NativeMethods.ShareDelete);
        return ReadMetadata(handle, path);
    }
    internal static NativeFileMetadata ReadMetadata(SafeFileHandle handle, string path)
    {
        var basic = NativeMethods.Query<NativeMethods.FileBasicInfo>(handle, 0);
        var standard = NativeMethods.Query<NativeMethods.FileStandardInfo>(handle, 1);
        var id = NativeMethods.Query<NativeMethods.FileIdInfo>(handle, 18);
        var tag = NativeMethods.Query<NativeMethods.FileAttributeTagInfo>(handle, 9);
        // FILE_STANDARD_INFO reports allocation for this file, not unique ownership of ReFS shared extents.
        var allocation = standard.AllocationSize;
        if ((basic.Attributes & ((uint)FileAttributes.Compressed | (uint)FileAttributes.SparseFile)) != 0 && standard.Directory == 0)
            allocation = NativeMethods.Query<NativeMethods.FileCompressionInfo>(handle, 8).CompressedFileSize;
        return new(path, standard.EndOfFile, allocation, id.VolumeSerialNumber, $"{id.FileIdHigh:X16}{id.FileIdLow:X16}",
            standard.NumberOfLinks, (FileAttributes)basic.Attributes, DateTime.FromFileTimeUtc(basic.LastWriteTime),
            DateTime.FromFileTimeUtc(basic.ChangeTime), tag.ReparseTag, standard.DeletePending != 0, DateTime.FromFileTimeUtc(basic.CreationTime));
    }
    public static DiskSpace GetDiskSpace(string path)
    {
        EnsureWindows(); path = NormalizeLocalPath(path);
        var volume = new StringBuilder(32768);
        if (!NativeMethods.GetVolumePathNameW(path, volume, (uint)volume.Capacity)) throw NativeMethods.Error("No se pudo identificar el volumen.");
        var root = volume.ToString();
        if (NativeMethods.GetDriveTypeW(root) != 3) throw new NotSupportedException("Solo se admiten discos locales fijos.");
        if (!NativeMethods.GetDiskFreeSpaceExW(root, out var available, out var total, out var free)) throw NativeMethods.Error("No se pudo consultar el espacio libre.");
        var fs = new StringBuilder(64);
        if (!NativeMethods.GetVolumeInformationW(root, null, 0, out _, out _, out _, fs, (uint)fs.Capacity)) throw NativeMethods.Error("No se pudo identificar el sistema de archivos.");
        if (!NativeMethods.GetDiskFreeSpaceW(root, out var sectors, out var bytes, out _, out _)) throw NativeMethods.Error("No se pudo leer el tamaño de clúster.");
        return new(root, fs.ToString(), checked((long)available), checked((long)total), checked((long)free), checked(sectors * bytes));
    }
    internal static SafeFileHandle Open(string path, uint access, uint share)
    {
        var handle = NativeMethods.CreateFileW(path, access, share, 0, NativeMethods.OpenExisting, NativeMethods.OpenReparsePoint | NativeMethods.BackupSemantics, 0);
        if (!handle.IsInvalid) return handle;
        var error = NativeMethods.Error($"No se pudo abrir {path} de forma segura."); handle.Dispose(); throw error;
    }
    // File.Exists/Directory.Exists also return false on access errors. Only genuine absence is safe
    // when deciding whether a restore may create a name or SQLite may create a journal sidecar.
    internal static bool EntryExists(string path)
    {
        path = NormalizeLocalPath(path);
        if (NativeMethods.GetFileAttributesW(path) != uint.MaxValue) return true; // Reports the link itself, even if dangling.
        var code = Marshal.GetLastWin32Error();
        if (code is 2 or 3) return false; // ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND
        throw new IOException($"No se pudo comprobar si existe {path}; se cancela por seguridad.", new System.ComponentModel.Win32Exception(code));
    }
    public static string NormalizeLocalPath(string path)
    {
        EnsureWindows(); ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0)
            throw new ArgumentException("Se necesita una ruta absoluta local, sin dispositivos ni flujos alternativos.", nameof(path));
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (normalized.Split('\\', '/').Any(part => part.EndsWith(' ') || part.EndsWith('.')))
            throw new ArgumentException("La ruta contiene nombres ambiguos de Windows.", nameof(path));
        return normalized;
    }
    internal static void RequireMutableFile(NativeFileMetadata state)
    {
        if (state.IsDirectory || state.IsReparsePoint || state.DeletePending || state.HardLinkCount != 1 ||
            (state.Attributes & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Encrypted | FileAttributes.Offline)) != 0)
            throw new IOException("El archivo es especial, está enlazado, protegido o no está completamente disponible; se omite.");
    }
    internal static void RequireExpected(NativeFileMetadata current, NativeFileMetadata? expected)
    {
        if (expected is not null && !current.SameVersion(expected)) throw new IOException("El archivo cambió desde el análisis; vuelve a analizarlo.");
    }
}

// Ancestors are opened without FILE_SHARE_DELETE and retained until the mutation finishes.
// A junction/symlink cannot be swapped in after validation while these handles are held.
[SupportedOSPlatform("windows10.0.17763")]
internal sealed class NativePathLease : IDisposable
{
    private readonly List<SafeFileHandle> handles = [];
    internal NativePathLease(string path, bool allowCloud = false, bool includeLeaf = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        path = WindowsFileSystem.NormalizeLocalPath(path);
        if (path.Equals(Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase)) throw new IOException("No se puede modificar la raíz del disco.");
        var parts = new Stack<string>(); var next = includeLeaf ? path : Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(next))
        { cancellationToken.ThrowIfCancellationRequested(); parts.Push(next); next = Path.GetDirectoryName(next); }
        try
        {
            foreach (var part in parts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var handle = WindowsFileSystem.Open(part, NativeMethods.ReadAttributes, NativeMethods.ShareRead | NativeMethods.ShareWrite);
                handles.Add(handle);
                var tag = NativeMethods.Query<NativeMethods.FileAttributeTagInfo>(handle, 9);
                var cloudTag = (tag.ReparseTag & 0xffff0fff) == 0x9000001a;
                if ((tag.Attributes & (uint)FileAttributes.ReparsePoint) != 0 && !(allowCloud && cloudTag))
                    throw new IOException("La ruta contiene un enlace, punto de unión o redirección; se omite.");
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch { Dispose(); throw; }
    }
    public void Dispose() { foreach (var handle in handles) handle.Dispose(); handles.Clear(); }
}
