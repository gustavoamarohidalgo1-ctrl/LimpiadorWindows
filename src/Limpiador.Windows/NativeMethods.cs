using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Limpiador.Windows;

internal static class NativeMethods
{
    internal const uint ReadAttributes = 0x80, GenericRead = 0x80000000, GenericWrite = 0x40000000;
    internal const uint OpenExisting = 3, OpenReparsePoint = 0x00200000, BackupSemantics = 0x02000000;
    internal const uint ShareRead = 1, ShareWrite = 2, ShareDelete = 4;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, nint buffer, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(SafeFileHandle handle, uint code, nint input, uint inputSize, nint output, uint outputSize, out uint returned, nint overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FlushFileBuffers(SafeFileHandle handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetDiskFreeSpaceExW(string directory, out ulong available, out ulong total, out ulong free);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetVolumePathNameW(string path, StringBuilder volumePath, uint capacity);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetVolumeInformationW(string root, StringBuilder? name, uint nameSize, out uint serial, out uint componentLength, out uint flags, StringBuilder fileSystem, uint fileSystemSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetDiskFreeSpaceW(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint GetDriveTypeW(string root);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetFileAttributesW(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);

    internal static IOException Error(string operation) => new(operation, new Win32Exception(Marshal.GetLastWin32Error()));
    internal static T Query<T>(SafeFileHandle handle, int informationClass) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetFileInformationByHandleEx(handle, informationClass, buffer, (uint)size)) throw Error("No se pudieron leer los metadatos del archivo.");
            return Marshal.PtrToStructure<T>(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    internal static void Control<T>(SafeFileHandle handle, uint code, T value) where T : struct
    {
        var size = Marshal.SizeOf<T>(); var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, buffer, false);
            if (!DeviceIoControl(handle, code, buffer, (uint)size, 0, 0, out _, 0)) throw Error("Windows rechazó la operación del sistema de archivos.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [StructLayout(LayoutKind.Sequential)] internal struct FileBasicInfo { internal long CreationTime, LastAccessTime, LastWriteTime, ChangeTime; internal uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] internal struct FileStandardInfo { internal long AllocationSize, EndOfFile; internal uint NumberOfLinks; internal byte DeletePending, Directory; }
    [StructLayout(LayoutKind.Sequential)] internal struct FileIdInfo { internal ulong VolumeSerialNumber, FileIdLow, FileIdHigh; }
    [StructLayout(LayoutKind.Sequential)] internal struct FileAttributeTagInfo { internal uint Attributes, ReparseTag; }
    [StructLayout(LayoutKind.Sequential)] internal struct FileCompressionInfo { internal long CompressedFileSize; internal ushort CompressionFormat; internal byte CompressionUnitShift, ChunkShift, ClusterShift; internal byte Reserved1, Reserved2, Reserved3; }
}
