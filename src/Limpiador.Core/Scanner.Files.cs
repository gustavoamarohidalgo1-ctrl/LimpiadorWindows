using System.Security.Cryptography;

namespace Limpiador.Core;

public sealed partial class Scanner
{
    private static void ScanDuplicates(ScanContext c)
    {
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".exe", ".dll", ".sys", ".pdb", ".db", ".sqlite", ".sqlite3", ".vhd", ".vhdx", ".qcow2", ".pst", ".ost" };
        var files = c.Entries().Where(x => !x.Snapshot.IsDirectory && x.Complete && !x.Sensitive &&
            x.Snapshot.Length >= Math.Max(1, c.Options.MinimumBytes) && x.Snapshot.LinkCount == 1 &&
            c.IsPersonal(x.Path) && !c.Covered(x.Path) && !forbidden.Contains(Path.GetExtension(x.Path)) &&
            !c.InProject(x.Path) && c.IsOld(x) && c.Policy.Validate(x.Path).Allowed).ToArray();
        long budgetUsed = 0;
        // Reuse one buffer for the scan instead of allocating a large object per file.
        var buffer = new byte[128 * 1024];
        foreach (var sameSize in files.GroupBy(x => x.Snapshot.Length).Where(x => x.Count() > 1))
        {
            c.Token.ThrowIfCancellationRequested();
            var hashed = new List<(IndexedEntry Entry, string Hash)>();
            var budgetReached = false;
            foreach (var file in sameSize)
            {
                c.Token.ThrowIfCancellationRequested();
                if (budgetUsed > c.Options.MaximumHashBytes - file.Snapshot.Length)
                {
                    c.Warnings.Add("Se alcanzó el límite de lectura de duplicados. Los archivos no comprobados no se proponen como copias.");
                    budgetReached = true;
                    break;
                }
                budgetUsed += file.Snapshot.Length;
                var hash = StableHash(file.Snapshot, c.Options.MetadataProvider ?? new FileMetadataProvider(), c.Policy, buffer, c.Token);
                if (hash is not null) hashed.Add((file, hash));
            }
            foreach (var group in hashed.GroupBy(x => x.Hash).Where(x => x.Count() > 1))
            {
                // Prefer a non-download original, then the oldest arrival. Never propose every member of a group.
                var ordered = group.OrderBy(x => x.Entry.Path.Split(Path.DirectorySeparatorChar).Any(p => p.Equals("Downloads", StringComparison.OrdinalIgnoreCase)))
                    .ThenBy(x => x.Entry.Snapshot.CreationTimeUtc).ThenBy(x => x.Entry.Path.Length).ThenBy(x => x.Entry.Path, StringComparer.OrdinalIgnoreCase).ToArray();
                var original = ordered[0].Entry;
                c.Preserved.Add(original.Path);
                var seenIds = new HashSet<string>(StringComparer.Ordinal);
                if (original.Snapshot.FileId is not null) seenIds.Add(original.Snapshot.FileId);
                foreach (var copy in ordered.Skip(1))
                {
                    if (copy.Entry.Snapshot.FileId is not null && !seenIds.Add(copy.Entry.Snapshot.FileId)) continue;
                    c.Add(copy.Entry.Path, "Copia idéntica: " + Path.GetFileName(copy.Entry.Path), CleanupCategory.Duplicates, Risk.Review,
                        consequence: "Solo se retira esta copia. Antes de actuar se debe volver a comprobar que la original existe y conserva el mismo contenido.",
                        reason: "Coincidencia SHA-256; se conserva: " + original.Path, preserved: original.Path, hash: group.Key, allowPersonal: true);
                }
            }
            if (budgetReached) break;
        }
    }

    private static string? StableHash(FileSnapshot expected, IFileMetadataProvider metadata, SafetyPolicy policy, byte[] buffer, CancellationToken token)
    {
        try
        {
            if (!policy.Validate(expected.Path).Allowed) return null;
            var before = metadata.Capture(expected.Path);
            if (!SameFile(expected, before)) return null;
            // A Windows reader denies concurrent writes and deletion while hashing.
            using var stream = new FileStream(expected.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); sha.AppendData(buffer, 0, count); }
            var hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            return SameFile(before, metadata.Capture(expected.Path)) ? hash : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static bool SameFile(FileSnapshot expected, FileSnapshot actual) =>
        expected.IsDirectory == actual.IsDirectory && expected.Length == actual.Length &&
        expected.LastWriteTimeUtc == actual.LastWriteTimeUtc && expected.CreationTimeUtc == actual.CreationTimeUtc &&
        expected.Attributes == actual.Attributes && expected.FileId == actual.FileId && expected.LinkCount == actual.LinkCount;

    private static void ScanPersonal(ScanContext c)
    {
        bool Needed(string path) => c.Preserved.Any(original => SafetyPolicy.IsWithin(original, path) || SafetyPolicy.IsWithin(path, original));
        foreach (var personal in c.Policy.PersonalRoots)
        foreach (var entry in c.Children(personal))
        {
            if (c.Covered(entry.Path) || Needed(entry.Path)) continue;
            if (c.IsOld(entry, c.Options.UnusedAge))
                c.Add(entry.Path, "Sin cambios recientes: " + Path.GetFileName(entry.Path), CleanupCategory.Unused,
                    entry.Repository || c.ProjectRoots.Contains(entry.Path) ? Risk.Caution : Risk.Review,
                    consequence: "Es material propio. Windows no registra de forma fiable todas las lecturas; revisa si todavía lo necesitas.",
                    reason: $"Sin cambios observados durante al menos {c.Options.UnusedAge.TotalDays:0} días; no demuestra que no se utilice.",
                    minimumAge: c.Options.UnusedAge, allowPersonal: true);
        }
        foreach (var entry in c.Entries())
        {
            c.Token.ThrowIfCancellationRequested();
            if (!c.IsPersonal(entry.Path) || c.Covered(entry.Path) || Needed(entry.Path) || IsDependencyPath(entry.Path)) continue;
            var ext = Path.GetExtension(entry.Path);
            var name = Path.GetFileName(entry.Path);
            if (!entry.Snapshot.IsDirectory && new[] { ".crdownload", ".part", ".partial", ".download" }.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                c.Add(entry.Path, "Descarga incompleta: " + name, CleanupCategory.Forgotten, Risk.Review,
                    consequence: "Pierdes lo descargado hasta ahora; puede que tengas que iniciar la descarga otra vez.", processes: Names("chrome", "msedge", "firefox", "brave", "opera"));
                continue;
            }
            if (entry.Snapshot.IsDirectory && IsBackupName(name))
            {
                c.Add(entry.Path, "Copia de respaldo para revisar: " + name, CleanupCategory.Forgotten, Risk.Review,
                    consequence: "Puede contener una versión que ya no existe en el proyecto original. Revisa y compara antes de retirarla.", minimumAge: TimeSpan.FromDays(14));
                continue;
            }
            if (!entry.Snapshot.IsDirectory && new[] { ".aux", ".toc", ".lof", ".lot", ".fls", ".fdb_latexmk", ".synctex" }.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                var tex = Path.ChangeExtension(entry.Path, ".tex");
                if (c.Index.Get(tex) is not null && !c.InProject(entry.Path))
                    c.Add(entry.Path, "Intermedio de LaTeX: " + name, CleanupCategory.Forgotten, Risk.Safe, recommend: true,
                        consequence: "El archivo .tex está al lado y LaTeX vuelve a generar este intermedio.", processes: Names("latexmk", "pdflatex", "xelatex", "lualatex", "tectonic"));
            }
            if (!entry.Snapshot.IsDirectory && entry.Bytes >= Math.Max(c.Options.MinimumBytes, 50_000_000))
                c.Add(entry.Path, "Archivo grande: " + name, CleanupCategory.LargeFiles, Risk.Review,
                    consequence: "El tamaño no significa que sobre. Revisa su contenido y conserva un respaldo si hace falta.", allowPersonal: true);
        }
        foreach (var file in c.Entries().Where(x => !x.Snapshot.IsDirectory && x.Bytes >= 200_000_000 &&
            (SafetyPolicy.IsWithin(x.Path, c.Local) || SafetyPolicy.IsWithin(x.Path, c.Roaming)) && !c.Covered(x.Path)))
            c.Add(file.Path, "Archivo grande de aplicación: " + Path.GetFileName(file.Path), CleanupCategory.LargeFiles, Risk.Review,
                consequence: "Puede contener datos de la aplicación, correo o discos virtuales. Adminístralo preferentemente desde su programa.", allowPersonal: true);
    }

    private static void ScanCompression(ScanContext c)
    {
        var textExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".log", ".jsonl", ".ndjson", ".txt", ".json", ".csv", ".tsv", ".xml" };
        var grouped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { c.H(".codex", "sessions"), c.H(".claude", "projects"), c.H(".codex", "runtimes") })
        {
            foreach (var file in c.Index.Snapshots(root, c.Token).Where(x => !x.IsDirectory && textExtensions.Contains(Path.GetExtension(x.Path))))
            {
                var candidate = c.Add(file.Path, "Comprimir sin borrar: " + Path.GetFileName(file.Path), CleanupCategory.Compression,
                    Risk.Safe, recommend: true, action: ActionKind.Compress, minimumAge: TimeSpan.FromDays(7), allowOverlap: true,
                    consequence: "Compresión transparente solo en NTFS compatible. Conserva el contenido y se puede descomprimir; el ahorro se mide al terminar.",
                    processes: Names("codex", "claude", "node"), reason: "El tamaño mostrado es el contenido revisado, no un ahorro garantizado.");
                if (candidate is not null) grouped.Add(file.Path);
            }
        }
        foreach (var file in c.Entries().Where(x => !x.Snapshot.IsDirectory && x.Bytes >= 10_000_000 && textExtensions.Contains(Path.GetExtension(x.Path)) &&
            !grouped.Contains(x.Path) && c.IsPersonal(x.Path) && !IsDependencyPath(x.Path)))
            c.Add(file.Path, "Comprimir archivo antiguo: " + Path.GetFileName(file.Path), CleanupCategory.Compression, Risk.Safe,
                maximum: true, action: ActionKind.Compress, minimumAge: TimeSpan.FromDays(7), allowOverlap: true,
                consequence: "No se borra el contenido. La compresión requiere NTFS y el archivo cerrado; se puede deshacer con Descomprimir.",
                reason: "Texto antiguo potencialmente compresible; el tamaño revisado no es una promesa de ahorro.");
        foreach (var program in c.Children(c.L("Programs")))
            c.Add(program.Path, "Comprimir programa de usuario: " + Path.GetFileName(program.Path), CleanupCategory.Compression, Risk.Review,
                action: ActionKind.Compress, allowOverlap: true, minimumAge: TimeSpan.FromDays(1),
                consequence: "Solo es posible con el programa y su actualizador cerrados, en NTFS, preservando contenido y firma. Se puede descomprimir.",
                processes: Names(Path.GetFileName(program.Path), "Update", "msiexec"), reason: "Los programas de Windows y Microsoft Store se excluyen.");
        foreach (var candidate in c.Candidates.Where(x => x.Action == ActionKind.Compress).ToArray())
        {
            // Do not add the whole file size to the plan's promised savings; native execution measures the difference.
            var index = c.Candidates.IndexOf(candidate);
            c.Candidates[index] = candidate with { Bytes = 0, Description = candidate.Description + $" · {candidate.Bytes:N0} bytes revisados; ahorro pendiente de medir" };
        }
        var databases = c.Entries().Where(x => !x.Snapshot.IsDirectory &&
            new[] { ".db", ".sqlite", ".sqlite3" }.Contains(Path.GetExtension(x.Path), StringComparer.OrdinalIgnoreCase) &&
            (SafetyPolicy.IsWithin(x.Path, c.Local) || SafetyPolicy.IsWithin(x.Path, c.Roaming)) && x.Bytes >= 50_000_000).Count();
        if (databases > 0) c.AddInformation(CleanupCategory.Compression, "Compactación de bases SQLite",
            $"Se detectaron {databases} bases grandes. No se ejecuta VACUUM genérico: requiere comprobar tablas virtuales, ROWID, bloqueos y una copia recuperable con SQLite.");
    }
}
