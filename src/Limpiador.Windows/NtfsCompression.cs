using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Limpiador.Windows;

public sealed record CompressionResult(string Path, bool Compressed, long BeforeAllocatedBytes, long AfterAllocatedBytes, string Sha256)
{
    public long SavedBytes => BeforeAllocatedBytes - AfterAllocatedBytes;
}

[SupportedOSPlatform("windows10.0.17763")]
public static class NtfsCompression
{
    public static CompressionResult SetCompressed(string path, bool compressed, NativeFileMetadata? expected = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        path = WindowsFileSystem.NormalizeLocalPath(path);
        using var lease = new NativePathLease(path, cancellationToken: cancellationToken);
        var disk = WindowsFileSystem.GetDiskSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        if (!disk.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("La compresión transparente requiere NTFS.");
        RestartManager.RequireUnlocked([path], cancellationToken);
        using var handle = WindowsFileSystem.Open(path, NativeMethods.GenericRead | NativeMethods.GenericWrite, 0);
        var before = WindowsFileSystem.ReadMetadata(handle, path);
        cancellationToken.ThrowIfCancellationRequested();
        WindowsFileSystem.RequireMutableFile(before); WindowsFileSystem.RequireExpected(before, expected);
        if (before.LogicalBytes > 30L * 1024 * 1024 * 1024) throw new NotSupportedException("NTFS no admite esta operación para archivos de más de 30 GiB.");
        // Decompression and a low-gain compression can allocate additional clusters.
        var required = Math.Max(0, before.LogicalBytes - before.AllocatedBytes) + 64L * 1024 * 1024;
        if (disk.AvailableBytes < required) throw new IOException("No hay suficiente espacio para transformar y, si es necesario, revertir la compresión.");
        var original = GetFormat(handle);
        var hash = Hash(handle, cancellationToken);
        var requested = (ushort)(compressed ? 2 : 0);
        // The cancellation boundary is outside the mutation/rollback region: canceling an analysis
        // must never start a rollback that would itself change the untouched file.
        cancellationToken.ThrowIfCancellationRequested();
        if (original == requested) return new(path, compressed, before.AllocatedBytes, before.AllocatedBytes, hash);
        try
        {
            // Once the first mutation starts, finish verification or rollback without interruption.
            NativeMethods.Control(handle, 0x0009c040, requested); // FSCTL_SET_COMPRESSION
            if (!NativeMethods.FlushFileBuffers(handle)) throw NativeMethods.Error("No se pudo confirmar la compresión en disco.");
            if (!StringComparer.Ordinal.Equals(hash, Hash(handle))) throw new IOException("Falló la verificación SHA-256 tras cambiar la compresión.");
            if (GetFormat(handle) != requested) throw new IOException("Windows no confirmó el formato de compresión solicitado.");
            var after = WindowsFileSystem.ReadMetadata(handle, path);
            if (after.FileId != before.FileId || after.LogicalBytes != before.LogicalBytes) throw new IOException("Cambió la identidad o tamaño del archivo.");
            return new(path, compressed, before.AllocatedBytes, after.AllocatedBytes, hash);
        }
        catch (Exception operationError)
        {
            try
            {
                NativeMethods.Control(handle, 0x0009c040, original);
                if (!NativeMethods.FlushFileBuffers(handle)) throw NativeMethods.Error("No se pudo confirmar la reversión.");
                if (GetFormat(handle) != original || Hash(handle) != hash) throw new IOException("No se pudo verificar la reversión.");
            }
            catch (Exception rollbackError) { throw new AggregateException("La operación y su reversión fallaron. El archivo se conserva; revisa el disco antes de continuar.", operationError, rollbackError); }
            throw;
        }
    }
    internal static string Hash(SafeFileHandle handle, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024]; long offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = RandomAccess.Read(handle, buffer, offset);
            cancellationToken.ThrowIfCancellationRequested();
            if (read == 0) break;
            hash.AppendData(buffer, 0, read); offset += read;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static ushort GetFormat(SafeFileHandle handle)
    {
        var buffer = Marshal.AllocHGlobal(2);
        try
        {
            if (!NativeMethods.DeviceIoControl(handle, 0x0009003c, 0, 0, buffer, 2, out var returned, 0) || returned != 2)
                throw NativeMethods.Error("No se pudo consultar el formato de compresión NTFS.");
            return unchecked((ushort)Marshal.ReadInt16(buffer));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
