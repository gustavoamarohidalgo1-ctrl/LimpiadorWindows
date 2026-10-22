using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Limpiador.Core;
using Limpiador.Windows;

namespace Limpiador.Runtime;

[SupportedOSPlatform("windows10.0.17763")]
public sealed class WindowsMetadataProvider : IFileMetadataProvider
{
    private readonly ConcurrentDictionary<string, NativeFileMetadata> snapshots = new(StringComparer.OrdinalIgnoreCase);
    public FileSnapshot Capture(string path)
    {
        var state = WindowsFileSystem.GetMetadata(path);
        snapshots[state.Path] = state;
        return Convert(state);
    }
    public NativeFileMetadata? Expected(string path) => snapshots.GetValueOrDefault(Path.GetFullPath(path));
    public IReadOnlyList<NativeFileMetadata> Captured => snapshots.Values.ToArray();
    public static FileSnapshot Convert(NativeFileMetadata state) => new(state.Path, state.IsDirectory,
        state.LogicalBytes, state.AllocatedBytes, state.LastWriteUtc, state.CreationTimeUtc, state.Attributes,
        $"{state.VolumeSerial:X16}:{state.FileId}", state.HardLinkCount);
}
