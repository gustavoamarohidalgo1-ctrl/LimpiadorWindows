using System.Text.Json;

namespace Limpiador.Runtime;

/// <summary>Atomic journals survive cancellation and an interrupted process.</summary>
public sealed class HistoryStore
{
    public string DirectoryPath { get; }
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public HistoryStore(string directoryPath) => DirectoryPath = Path.GetFullPath(directoryPath);

    public void Save(HistoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ValidateRecord(record, record.Id);
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, record.Id + ".json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, record, JsonOptions);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public HistoryRecord Read(string id)
    {
        ValidateId(id);
        using var stream = new FileStream(Path.Combine(DirectoryPath, id + ".json"), FileMode.Open,
            FileAccess.Read, FileShare.Read | FileShare.Delete);
        var record = JsonSerializer.Deserialize<HistoryRecord>(stream)
                     ?? throw new InvalidDataException("El historial no es válido.");
        ValidateRecord(record, id);
        return record;
    }

    public IReadOnlyList<HistoryRecord> List()
    {
        if (!Directory.Exists(DirectoryPath)) return [];
        var records = new List<HistoryRecord>();
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            try
            {
                records.Add(Read(id));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException)
            {
                // Keep a visible, non-restorable entry instead of hiding damage or breaking the entire list.
                records.Add(new HistoryRecord
                {
                    Id = id, Date = ReadTimestamp(path), Title = "Historial no disponible",
                    Status = "No se pudo leer",
                    Details = [$"No se pudo abrir {Path.GetFileName(path)}: {ex.Message}",
                        "El archivo original se conserva. No se ha intentado restaurar esta operación."]
                });
            }
        }
        return records.OrderByDescending(r => r.Date).ToArray();
    }

    private static void ValidateId(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Identificador de historial no válido.");
    }

    private static DateTimeOffset ReadTimestamp(string path)
    {
        try { return new DateTimeOffset(File.GetLastWriteTimeUtc(path)); }
        catch (IOException) { return DateTimeOffset.UtcNow; }
        catch (UnauthorizedAccessException) { return DateTimeOffset.UtcNow; }
    }

    private static void ValidateRecord(HistoryRecord record, string expectedId)
    {
        ValidateId(record.Id);
        if (!record.Id.Equals(expectedId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("El identificador del historial no coincide con su archivo.");
        if (record.Title is null || record.Status is null || record.Details is null ||
            record.RecycleJournals is null || record.CompressedFiles is null || record.ApprovedRoots is null ||
            record.Details.Any(x => x is null) || record.RecycleJournals.Any(string.IsNullOrWhiteSpace) ||
            record.ApprovedRoots.Any(string.IsNullOrWhiteSpace) ||
            record.CompressedFiles.Any(x => x is null || string.IsNullOrWhiteSpace(x.Path) ||
                string.IsNullOrWhiteSpace(x.FileId) || x.Length < 0))
            throw new InvalidDataException("El historial contiene campos incompletos o no válidos.");
    }
}
