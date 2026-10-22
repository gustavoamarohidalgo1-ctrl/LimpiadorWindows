using System.Text.Json;
using System.Text.Json.Serialization;

namespace Limpiador.Runtime;

public sealed record AppPreferences
{
    [JsonRequired] public int Version { get; init; } = 1;
    [JsonRequired] public int MinimumAgeDays { get; init; } = 90;
    public string? AdditionalFolder { get; init; }
    [JsonRequired] public IReadOnlyList<string> ExcludedFolders { get; init; } = [];
}

/// <summary>Only scan preferences are persisted. No candidates, selections or executable plans.</summary>
public sealed class AppPreferencesStore(string path)
{
    public string FilePath { get; } = Path.GetFullPath(path);
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public AppPreferences Load()
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(FilePath); }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new InvalidDataException("Las preferencias deben ser un archivo local sin redirecciones.");
        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return Normalize(JsonSerializer.Deserialize<AppPreferences>(stream, Json)
                ?? throw new InvalidDataException("Las preferencias están vacías."));
        }
        catch (JsonException ex) { throw new InvalidDataException("No se pudieron leer las preferencias. Revisa la configuración antes de analizar.", ex); }
    }

    public void Save(AppPreferences preferences)
    {
        var normalized = Normalize(preferences);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, normalized, Json);
                stream.Flush(true);
            }
            File.Move(temporary, FilePath, true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static AppPreferences Normalize(AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (preferences.Version != 1 || preferences.MinimumAgeDays is not (7 or 30 or 90 or 180 or 365) || preferences.ExcludedFolders is null)
            throw new InvalidDataException("La versión o los parámetros de las preferencias no son válidos.");
        var exclusions = preferences.ExcludedFolders.Select(NormalizeFolder).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var additional = string.IsNullOrWhiteSpace(preferences.AdditionalFolder) ? null : NormalizeFolder(preferences.AdditionalFolder);
        return preferences with { AdditionalFolder = additional, ExcludedFolders = exclusions };
    }

    private static string NormalizeFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Contains('\0') || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("Las carpetas deben tener rutas absolutas locales.");
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new InvalidDataException("La ruta de una carpeta no es válida.", ex); }
    }
}
