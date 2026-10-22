namespace Limpiador.Core;

/// <summary>Shared fail-closed boundary. Final mutation must call ValidateTree again, after checking snapshots.</summary>
public sealed class SafetyPolicy
{
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
    private static readonly HashSet<string> ProtectedSegments = new(Comparer)
    {
        ".ssh", ".gnupg", ".git", ".aws", ".azure", ".kube", "Credentials", "Vault", "Protect",
        "WindowsApps", "System Volume Information", "$Recycle.Bin", "Recovery", "WinSxS", "System32", "SysWOW64",
        "LimpiadorUltraWindows", "LimpiadorWindows", "Wallet", "Keychains"
    };
    private static readonly HashSet<string> SecretExtensions = new(Comparer)
        { ".pem", ".pfx", ".p12", ".key", ".keystore", ".jks", ".kdbx", ".tfstate" };
    private static readonly string[] CloudNames = ["OneDrive", "Dropbox", "Google Drive", "GoogleDrive", "iCloudDrive", "iCloud Drive", "Nextcloud", "ownCloud", "Box", "pCloud", "MEGA", "SynologyDrive", "Proton Drive", "Tresorit"];
    private readonly string[] roots;
    private readonly string[] structural;
    private readonly string[] clouds;
    private readonly PathPrefixIndex<string> exclusions = new();
    public string ProfilePath { get; }
    public string LocalAppDataPath { get; }
    public string RoamingAppDataPath { get; }
    public string TempPath { get; }
    public IReadOnlyList<string> PersonalRoots { get; }
    public IReadOnlyList<string> ApprovedRoots => roots;
    public IReadOnlyList<string> ExcludedRoots { get; }

    public SafetyPolicy(ScanOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ProfilePath)) throw new ArgumentException("Falta la carpeta de usuario.");
        ProfilePath = Normalize(options.ProfilePath);
        if (Comparer.Equals(ProfilePath, Path.GetPathRoot(ProfilePath)))
            throw new ArgumentException("No se puede usar la raíz de una unidad como perfil.");
        var excluded = options.ExcludedRoots.Select(path => string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("Una carpeta excluida no puede tener una ruta vacía.", nameof(options)) : Normalize(path))
            .Distinct(Comparer).ToArray();
        ExcludedRoots = Array.AsReadOnly(excluded);
        foreach (var path in excluded) exclusions.Add(path, path);
        LocalAppDataPath = Normalize(options.LocalAppDataPath ?? Path.Combine(ProfilePath, "AppData", "Local"));
        RoamingAppDataPath = Normalize(options.RoamingAppDataPath ?? Path.Combine(ProfilePath, "AppData", "Roaming"));
        TempPath = Normalize(options.TempPath ?? Path.Combine(LocalAppDataPath, "Temp"));
        PersonalRoots = (options.PersonalRoots ?? new[] { "Downloads", "Desktop", "Documents", "Pictures", "Videos", "Music" }
            .Select(x => Path.Combine(ProfilePath, x)).ToArray()).Select(Normalize).Distinct(Comparer).ToArray();
        roots = new[] { ProfilePath, LocalAppDataPath, RoamingAppDataPath, TempPath }.Concat(PersonalRoots)
            .Concat(options.AllowedRoots).Select(Normalize).Distinct(Comparer).ToArray();
        structural = roots.Concat(new[] { Path.Combine(ProfilePath, "AppData"), Path.Combine(ProfilePath, "AppData", "LocalLow"),
            Path.Combine(LocalAppDataPath, "Microsoft"), Path.Combine(RoamingAppDataPath, "Microsoft"),
            Path.Combine(LocalAppDataPath, "Packages"), Path.Combine(LocalAppDataPath, "Programs") }).Select(Normalize).ToArray();
        clouds = options.AdditionalCloudRoots.Concat(new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" }
            .Select(Environment.GetEnvironmentVariable).OfType<string>().Where(x => x.Length > 0))
            .Select(Normalize).Distinct(Comparer).ToArray();
    }

    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static bool IsWithin(string path, string parent)
    {
        var p = Normalize(path);
        var root = Normalize(parent);
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return Comparer.Equals(p, root) || p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for an explicitly excluded path and its descendants. Does not exclude its siblings or parents from analysis.</summary>
    public bool IsExcludedPath(string path) => exclusions.Ancestor(Normalize(path)) is not null;

    /// <summary>True when this path contains an excluded path, including equality, even if it does not exist yet.</summary>
    public bool ContainsExcludedPath(string path) => exclusions.Descendant(Normalize(path)) is not null;

    public bool IsCloudPath(string path)
    {
        var full = Normalize(path);
        if (clouds.Any(root => IsWithin(full, root))) return true;
        // Names are deliberately conservative: never hydrate or delete a provider's data while inspecting.
        return full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(segment =>
            CloudNames.Any(name => segment.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                segment.StartsWith(name + " - ", StringComparison.OrdinalIgnoreCase)));
    }

    public static bool IsSensitiveFile(string path)
    {
        var name = Path.GetFileName(path);
        return SecretExtensions.Contains(Path.GetExtension(name)) || name.Equals(".env", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) || name.Equals("id_rsa", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("id_ed25519", StringComparison.OrdinalIgnoreCase) || name.Equals("credentials", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("secrets.json", StringComparison.OrdinalIgnoreCase) || name.Equals("Login Data", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Cookies", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsProtectedPath(string path)
    {
        var full = Normalize(path);
        if (exclusions.Ancestor(full) is not null) return true;
        if (structural.Contains(full, Comparer)) return true;
        if (full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(ProtectedSegments.Contains)) return true;
        if (IsSensitiveFile(full)) return true;
        var system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return system.Length > 0 && IsWithin(full, system);
    }

    public SafetyDecision Validate(string path, bool allowAppFiles = false)
    {
        if (string.IsNullOrWhiteSpace(path)) return new(false, "Ruta vacía.");
        string full;
        try { full = Normalize(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return new(false, "Ruta inválida."); }
        if (exclusions.Ancestor(full) is not null) return new(false, "Carpeta excluida explícitamente del análisis y de las operaciones.");
        if (exclusions.Descendant(full) is not null) return new(false, "Esta carpeta contiene una ruta excluida; no se puede operar sobre el conjunto.");
        if (!roots.Any(root => IsWithin(full, root))) return new(false, "Fuera de las carpetas autorizadas del usuario.");
        if (IsCloudPath(full)) return new(false, "Carpeta sincronizada: se administra desde su proveedor de nube.");
        if (IsProtectedPath(full)) return new(false, "Ruta estructural, del sistema, código o credenciales protegidas.");
        if (!allowAppFiles && IsWithin(full, Path.Combine(LocalAppDataPath, "Programs")))
            return new(false, "Los programas instalados se administran desde Configuración de Windows.");
        // Inspect EVERY existing ancestor, including an approved root that itself became a junction.
        try
        {
            for (string? current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                    return new(false, "Enlace, junction, archivo sin conexión o punto de nube.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(false, "No se pudo comprobar la ruta y sus carpetas superiores."); }
        return SafetyDecision.Permit;
    }

    public SafetyDecision ValidateTree(string path, bool allowAppFiles = false, CancellationToken cancellationToken = default)
    {
        var first = Validate(path, allowAppFiles);
        if (!first.Allowed || !Directory.Exists(path)) return first;
        try
        {
            var pending = new Stack<string>();
            pending.Push(path);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    var decision = Validate(entry, allowAppFiles);
                    if (!decision.Allowed) return decision;
                    if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0) pending.Push(entry);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(false, "No se pudo comprobar todo el contenido; no se tocará."); }
        return SafetyDecision.Permit;
    }
}
