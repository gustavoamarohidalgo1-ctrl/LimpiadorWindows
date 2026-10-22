using Limpiador.Core;

namespace Limpiador.Runtime;

/// <summary>Checks the complete tree, including additions, removals and ancestor reparse points.</summary>
public static class SnapshotValidator
{
    public static IReadOnlyList<FileSnapshot> Validate(Candidate item, SafetyPolicy policy, IFileMetadataProvider metadata,
        CancellationToken cancellationToken = default)
    {
        if (!item.CanSelect || item.IsBlocked) throw new IOException(item.BlockReason ?? "Elemento bloqueado.");
        if (item.Paths.Count == 0 || item.Snapshots.Count == 0) throw new IOException("No hay una captura completa del elemento.");
        var expected = item.Snapshots.ToDictionary(x => Path.GetFullPath(x.Path), StringComparer.OrdinalIgnoreCase);
        var observed = new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in item.Paths)
        {
            RequireSafe(root, policy, item.Action);
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.TryPop(out var path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequireSafe(path, policy, item.Action);
                var current = metadata.Capture(path);
                if (current.IsReparsePoint || current.IsOffline) throw new IOException("La ruta contiene un enlace o un archivo sin descargar.");
                if (!expected.TryGetValue(current.Path, out var prior) || !SameVersion(prior, current))
                    throw new IOException("El contenido cambió desde el análisis. Analiza de nuevo antes de limpiarlo.");
                if (!observed.TryAdd(current.Path, current)) continue;
                if (current.IsDirectory)
                    foreach (var child in Directory.EnumerateFileSystemEntries(path)) pending.Push(child);
            }
        }
        if (observed.Count != expected.Count) throw new IOException("Faltan archivos respecto al análisis; se omite el elemento.");
        return observed.Values.ToArray();
    }

    public static bool SameVersion(FileSnapshot before, FileSnapshot after) =>
        before.IsDirectory == after.IsDirectory && before.Length == after.Length &&
        before.LastWriteTimeUtc == after.LastWriteTimeUtc && before.CreationTimeUtc == after.CreationTimeUtc &&
        before.Attributes == after.Attributes && before.LinkCount == after.LinkCount &&
        (before.FileId is null || StringComparer.Ordinal.Equals(before.FileId, after.FileId));

    private static void RequireSafe(string path, SafetyPolicy policy, ActionKind action)
    {
        var decision = policy.Validate(path, allowAppFiles: action is ActionKind.Compress or ActionKind.Decompress);
        if (!decision.Allowed) throw new IOException(decision.Reason);
    }
}
