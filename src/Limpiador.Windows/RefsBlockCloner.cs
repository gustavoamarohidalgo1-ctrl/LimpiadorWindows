using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;

namespace Limpiador.Windows;
public sealed record BlockCloneResult(string SourcePath, string TargetPath, long ClonedBytes, string Sha256);

[SupportedOSPlatform("windows10.0.17763")]
public static class RefsBlockCloner
{
    /// <summary>Shares aligned ReFS extents between already identical, independent files. Never makes hard links.</summary>
    public static BlockCloneResult CloneIdentical(string source, string target, NativeFileMetadata? expectedSource = null, NativeFileMetadata? expectedTarget = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        source = WindowsFileSystem.NormalizeLocalPath(source); target = WindowsFileSystem.NormalizeLocalPath(target);
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Las dos rutas deben ser distintas.");
        using var sourceLease = new NativePathLease(source, cancellationToken: cancellationToken);
        using var targetLease = new NativePathLease(target, cancellationToken: cancellationToken);
        var disk = WindowsFileSystem.GetDiskSpace(target);
        cancellationToken.ThrowIfCancellationRequested();
        if (!disk.FileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("Compartir bloques con copias independientes requiere ReFS. NTFS no se reemplaza por enlaces duros.");
        RestartManager.RequireUnlocked([source, target], cancellationToken);
        using var sourceHandle = WindowsFileSystem.Open(source, NativeMethods.GenericRead, 0);
        using var targetHandle = WindowsFileSystem.Open(target, NativeMethods.GenericRead | NativeMethods.GenericWrite, 0);
        var a = WindowsFileSystem.ReadMetadata(sourceHandle, source); var b = WindowsFileSystem.ReadMetadata(targetHandle, target);
        cancellationToken.ThrowIfCancellationRequested();
        WindowsFileSystem.RequireMutableFile(a); WindowsFileSystem.RequireMutableFile(b);
        WindowsFileSystem.RequireExpected(a, expectedSource); WindowsFileSystem.RequireExpected(b, expectedTarget);
        if (a.VolumeSerial != b.VolumeSerial || a.FileId == b.FileId || a.LogicalBytes != b.LogicalBytes)
            throw new IOException("Se necesitan dos archivos independientes del mismo tamaño y volumen.");
        if ((a.Attributes & (FileAttributes.SparseFile | FileAttributes.Compressed)) != 0 || (b.Attributes & (FileAttributes.SparseFile | FileAttributes.Compressed)) != 0)
            throw new NotSupportedException("No se comparten bloques de archivos dispersos o comprimidos.");
        var hash = NtfsCompression.Hash(sourceHandle, cancellationToken);
        if (NtfsCompression.Hash(targetHandle, cancellationToken) != hash) throw new IOException("Los archivos no tienen el mismo contenido SHA-256.");
        var length = a.LogicalBytes / disk.ClusterBytes * disk.ClusterBytes;
        if (length == 0) throw new NotSupportedException("El archivo no contiene un clúster completo para compartir.");
        // Every request is cluster-aligned and smaller than 4 GiB. Unaligned tail stays untouched.
        long done = 0; var max = (4L * 1024 * 1024 * 1024 - disk.ClusterBytes);
        Exception? operationError = null;
        // Cancellation before this boundary leaves both files untouched, including their timestamps.
        // After the first extent operation, complete the clone, verification and timestamp restoration.
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            while (done < length)
            {
                var count = Math.Min(max, length - done);
                NativeMethods.Control(targetHandle, 0x00098344, new DuplicateExtentsData
                { SourceHandle = sourceHandle.DangerousGetHandle(), SourceOffset = done, TargetOffset = done, ByteCount = count });
                done += count;
            }
            if (!NativeMethods.FlushFileBuffers(targetHandle)) throw NativeMethods.Error("No se pudieron confirmar los bloques compartidos.");
            if (NtfsCompression.Hash(targetHandle) != hash || NtfsCompression.Hash(sourceHandle) != hash)
                throw new IOException("Falló la verificación SHA-256 de los archivos tras compartir bloques.");
        }
        catch (Exception ex) { operationError = ex; }
        // Changing physical extents does not represent a user content edit. Retain the original
        // failure too if both the extent operation and timestamp restoration fail.
        var time = b.LastWriteUtc.ToFileTimeUtc();
        if (!SetFileTime(targetHandle, 0, 0, ref time))
        {
            var timestampError = NativeMethods.Error("No se pudo conservar la fecha del archivo.");
            if (operationError is not null)
                throw new IOException($"Falló la clonación ReFS ({operationError.Message}) y no se pudo conservar su fecha ({timestampError.Message}).", new AggregateException(operationError, timestampError));
            throw timestampError;
        }
        if (operationError is not null) ExceptionDispatchInfo.Capture(operationError).Throw();
        return new(source, target, done, hash);
    }
    [StructLayout(LayoutKind.Sequential)] private struct DuplicateExtentsData { internal nint SourceHandle; internal long SourceOffset, TargetOffset, ByteCount; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileTime(Microsoft.Win32.SafeHandles.SafeFileHandle file, nint creation, nint access, ref long write);
}
