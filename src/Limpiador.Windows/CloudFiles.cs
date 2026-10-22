using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Limpiador.Windows;
public sealed record CloudFileState(string Path, bool InSync, bool Pinned, long OnDiskDataBytes, long ValidatedBytes, long ModifiedBytes, int PinState)
{
    public bool CanDehydrate => InSync && !Pinned && PinState is 0 or 2 && ModifiedBytes == 0 && ValidatedBytes >= OnDiskDataBytes;
}
public sealed record DehydrationResult(string Path, long BeforeAllocatedBytes, long AfterAllocatedBytes)
{
    public long SavedBytes => Math.Max(0, BeforeAllocatedBytes - AfterAllocatedBytes);
}

[SupportedOSPlatform("windows10.0.17763")]
public static class CloudFiles
{
    public static CloudFileState GetState(string path)
    {
        path = WindowsFileSystem.NormalizeLocalPath(path);
        using var lease = new NativePathLease(path, allowCloud: true);
        using var handle = WindowsFileSystem.Open(path, NativeMethods.ReadAttributes, NativeMethods.ShareRead | NativeMethods.ShareWrite | NativeMethods.ShareDelete);
        var metadata = WindowsFileSystem.ReadMetadata(handle, path);
        if (!metadata.IsCloudPlaceholder || metadata.IsDirectory) throw new IOException("El archivo no es un marcador de Cloud Files.");
        return ReadState(handle, path);
    }
    public static DehydrationResult Dehydrate(string path, NativeFileMetadata? expected = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        path = WindowsFileSystem.NormalizeLocalPath(path);
        var roots = KnownFolders.CloudRoots();
        cancellationToken.ThrowIfCancellationRequested();
        if (!roots.Any(root => path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("El archivo no pertenece a una raíz OneDrive registrada para este usuario.");
        using var lease = new NativePathLease(path, allowCloud: true, cancellationToken: cancellationToken);
        RestartManager.RequireUnlocked([path], cancellationToken);
        Marshal.ThrowExceptionForHR(CfOpenFileWithOplock(path, 1 | 2, out var protectedHandle));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CfReferenceProtectedHandle(protectedHandle)) throw new IOException("El proveedor perdió el bloqueo del archivo; se omite.");
            try
            {
                using var handle = new SafeFileHandle(CfGetWin32HandleFromProtectedHandle(protectedHandle), ownsHandle: false);
                if (handle.IsInvalid) throw new IOException("Cloud Files no devolvió un identificador válido.");
                var before = WindowsFileSystem.ReadMetadata(handle, path);
                cancellationToken.ThrowIfCancellationRequested();
                WindowsFileSystem.RequireExpected(before, expected);
                if (!before.IsCloudPlaceholder || before.IsDirectory || before.HardLinkCount != 1 || before.DeletePending ||
                    (before.Attributes & (FileAttributes.System | FileAttributes.ReadOnly | FileAttributes.Encrypted | (FileAttributes)0x80000)) != 0)
                    throw new IOException("Solo se pueden liberar marcadores de archivos OneDrive independientes.");
                var state = ReadState(handle, path);
                if (!state.CanDehydrate) throw new IOException("El archivo no está totalmente sincronizado o está marcado para conservarlo en este dispositivo.");
                // Never set InSync, clear pinned state, truncate, delete or recreate a placeholder.
                // The provider/kernel can reject dehydration; there is deliberately no fallback.
                cancellationToken.ThrowIfCancellationRequested();
                Marshal.ThrowExceptionForHR(CfDehydratePlaceholder(protectedHandle, 0, -1, 0, 0));
                // After dehydration starts, finish verifying the synchronized placeholder without interruption.
                var after = WindowsFileSystem.ReadMetadata(handle, path);
                var afterState = ReadState(handle, path);
                if (after.FileId != before.FileId || after.LogicalBytes != before.LogicalBytes || !after.IsCloudPlaceholder || !afterState.InSync)
                    throw new IOException("Cloud Files no confirmó el marcador sincronizado después de liberar la descarga.");
                return new(path, before.AllocatedBytes, after.AllocatedBytes);
            }
            finally { CfReleaseProtectedHandle(protectedHandle); }
        }
        finally { CfCloseHandle(protectedHandle); }
    }
    private static CloudFileState ReadState(SafeFileHandle handle, string path)
    {
        const int size = 65536; var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.ThrowExceptionForHR(CfGetPlaceholderInfo(handle, 1, buffer, size, out var returned));
            if (returned < 60) throw new IOException("Cloud Files devolvió metadatos incompletos.");
            return new(path, Marshal.ReadInt32(buffer, 36) == 1, Marshal.ReadInt32(buffer, 32) == 1,
                Marshal.ReadInt64(buffer, 0), Marshal.ReadInt64(buffer, 8), Marshal.ReadInt64(buffer, 16), Marshal.ReadInt32(buffer, 32));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)] private static extern int CfOpenFileWithOplock(string path, uint flags, out nint handle);
    [DllImport("cldapi.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool CfReferenceProtectedHandle(nint handle);
    [DllImport("cldapi.dll")] private static extern nint CfGetWin32HandleFromProtectedHandle(nint handle);
    [DllImport("cldapi.dll")] private static extern void CfReleaseProtectedHandle(nint handle);
    [DllImport("cldapi.dll")] private static extern void CfCloseHandle(nint handle);
    [DllImport("cldapi.dll")] private static extern int CfGetPlaceholderInfo(SafeFileHandle handle, int infoClass, nint buffer, uint size, out uint returned);
    [DllImport("cldapi.dll")] private static extern int CfDehydratePlaceholder(nint handle, long start, long length, uint flags, nint overlapped);
}
