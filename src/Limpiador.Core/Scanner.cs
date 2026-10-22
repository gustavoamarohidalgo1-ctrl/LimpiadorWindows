using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Limpiador.Core;

/// <summary>Read-only analysis. Construction, planning and diagnostic export never execute cleanup commands.</summary>
public sealed partial class Scanner
{
    public Task<ScanResult> ScanAsync(ScanOptions options, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MinimumBytes < 0 || options.MaximumEntries < 1 || options.MaximumHashBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        return Task.Run(() => Scan(options, progress, cancellationToken), cancellationToken);
    }

    private static ScanResult Scan(ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        var policy = new SafetyPolicy(options);
        if (!Directory.Exists(policy.ProfilePath)) throw new DirectoryNotFoundException("No existe la carpeta de usuario indicada.");
        progress?.Report(new("Procesos y carpetas protegidas", 0, 0, Fraction: 0.01));
        var index = new FileIndex(options, policy);
        index.Build(progress, token);
        var context = new ScanContext(options, policy, index, token, progress);
        context.Phase("Emuladores y SDK de Android", 0.35); ScanEmulators(context);
        context.Phase("Herramientas de desarrollo", 0.4); ScanDevelopment(context);
        context.Phase("Cachés de aplicaciones y navegadores", 0.45); ScanApplications(context);
        context.Phase("Temporales de Windows", 0.5); ScanTemporary(context);
        context.Phase("Restos de aplicaciones", 0.54); ScanLeftovers(context);
        context.Phase("Proyectos y compilaciones", 0.59); ScanProjects(context);
        context.Phase("Datos de herramientas", 0.64); ScanToolData(context);
        context.Phase("Instaladores y registros", 0.69); ScanInstallersAndLogs(context);
        if (options.IncludeDuplicates) { context.Phase("Archivos duplicados: comprobando contenido", 0.75); ScanDuplicates(context); }
        context.Phase("Archivos sin uso, olvidados y grandes", 0.88); ScanPersonal(context);
        if (options.IncludeCompression) { context.Phase("Compresión sin borrar", 0.95); ScanCompression(context); }
        context.AddInformation(CleanupCategory.RecycleBin, "Papelera de reciclaje", "Windows administra la Papelera por usuario y unidad. Ábrela para revisar y restaurar sus archivos.");
        context.Warnings.Add("El análisis no borra archivos. Las fechas indican cambios; la ausencia de cambios no demuestra que un archivo no se use.");
        if (options.MetadataProvider is null) context.Warnings.Add("Tamaños estimados por longitud de archivo: el proveedor nativo permite medir asignación y enlaces de Windows.");
        progress?.Report(new("Análisis terminado", index.Files, index.Bytes, Fraction: 1));
        return new()
        {
            Candidates = context.Candidates.OrderBy(x => x.Category).ThenByDescending(x => x.Bytes).ToArray(),
            Warnings = index.Warnings.Concat(context.Warnings).Distinct().ToArray(),
            Directories = index.Entries.Values.Where(x => x.Snapshot.IsDirectory).Select(x => new DirectorySummary(x.Path, x.Bytes,
                x.Files, x.Latest, x.Complete, x.Sensitive, x.Personal, x.Repository)).ToArray(),
            Stats = new(index.Entries.Count, index.Files, index.Bytes, index.Skipped, watch.Elapsed, options.MetadataProvider is not null)
        };
    }

    private sealed class ScanContext
    {
        internal ScanOptions Options { get; }
        internal SafetyPolicy Policy { get; }
        internal FileIndex Index { get; }
        internal CancellationToken Token { get; }
        internal List<Candidate> Candidates { get; } = [];
        internal List<string> Warnings { get; } = [];
        internal HashSet<string> Preserved { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> ProjectRoots { get; } = new(StringComparer.OrdinalIgnoreCase);
        private readonly IProgress<ScanProgress>? progress;
        private readonly Func<IReadOnlyList<string>, bool?> processCheck;
        private readonly HashSet<string> offered = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, GitIndexReader.Inspection> gitIndexes = new(StringComparer.OrdinalIgnoreCase);
        internal string Home => Policy.ProfilePath;
        internal string Local => Policy.LocalAppDataPath;
        internal string Roaming => Policy.RoamingAppDataPath;
        internal DateTime Now => Options.UtcNow;
        internal ScanContext(ScanOptions options, SafetyPolicy policy, FileIndex index, CancellationToken token, IProgress<ScanProgress>? progress)
        {
            Options = options; Policy = policy; Index = index; Token = token; this.progress = progress;
            if (options.AreProcessesRunning is not null) processCheck = options.AreProcessesRunning;
            else
            {
                var processes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var reliable = true;
                try
                {
                    foreach (var process in Process.GetProcesses())
                    {
                        using (process) { try { processes.Add(process.ProcessName); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { reliable = false; } }
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { reliable = false; }
                processCheck = names => !reliable ? null : names.Any(name => processes.Contains(Path.GetFileNameWithoutExtension(name)));
            }
        }
        internal string H(params string[] parts) => Path.Combine(new[] { Home }.Concat(parts).ToArray());
        internal string L(params string[] parts) => Path.Combine(new[] { Local }.Concat(parts).ToArray());
        internal string R(params string[] parts) => Path.Combine(new[] { Roaming }.Concat(parts).ToArray());
        internal void Phase(string title, double fraction) { Token.ThrowIfCancellationRequested(); progress?.Report(new(title, Index.Files, Index.Bytes, Fraction: fraction)); }
        internal bool Covered(string path) => HasAncestor(path, offered);
        internal bool InProject(string path) => HasAncestor(path, ProjectRoots);
        private static bool HasAncestor(string path, HashSet<string> roots)
        {
            for (string? current = SafetyPolicy.Normalize(path); current is not null; current = Path.GetDirectoryName(current))
                if (roots.Contains(current)) return true;
            return false;
        }
        internal bool IsPersonal(string path) => Policy.PersonalRoots.Any(root => SafetyPolicy.IsWithin(path, root));
        internal bool Has(string dir, string child) => Index.Get(Path.Combine(dir, child)) is not null;
        internal string? ReadText(string path) => SmallText(path, Policy);
        internal IEnumerable<IndexedEntry> Children(string path)
        {
            foreach (var entry in Index.Children(path)) { Token.ThrowIfCancellationRequested(); yield return entry; }
        }
        internal IEnumerable<IndexedEntry> Entries()
        {
            foreach (var entry in Index.Entries.Values) { Token.ThrowIfCancellationRequested(); yield return entry; }
        }
        internal bool IsOld(IndexedEntry entry, TimeSpan? age = null) => Now - entry.Latest >= (age ?? Options.MinimumAge);
        internal static string Id(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];
        internal void AddInformation(CleanupCategory category, string title, string description)
        {
            Candidates.Add(new() { Id = Id(category + title), Category = category, Title = title, Description = description,
                Consequence = "Solo información; no hay una operación automática.", CanSelect = false, IsBlocked = true,
                BlockReason = "Se administra con las herramientas de Windows o de la aplicación.", Risk = Risk.Review });
        }
        internal Candidate? Add(string path, string title, CleanupCategory category, Risk risk = Risk.Review,
            bool recommend = false, bool maximum = false, string consequence = "Va a la Papelera de reciclaje. Revisa su contenido antes de decidir.",
            string[]? processes = null, string? reason = null, ActionKind action = ActionKind.Delete, bool allowPersonal = false,
            TimeSpan? minimumAge = null, bool allowOverlap = false, string? preserved = null, string? hash = null)
        {
            Token.ThrowIfCancellationRequested();
            path = SafetyPolicy.Normalize(path);
            if (!allowOverlap && Covered(path)) return null;
            var entry = Index.Get(path);
            if (entry is null || entry.Bytes < Options.MinimumBytes) return null;
            var originalRisk = risk;
            var reasons = new List<string>();
            if (reason is not null) reasons.Add(reason);
            var decision = Policy.Validate(path, allowAppFiles: action is ActionKind.Compress or ActionKind.Decompress);
            var blocked = !decision.Allowed || !entry.Complete;
            string? blockReason = !decision.Allowed ? decision.Reason : !entry.Complete ? "No se pudo revisar todo el contenido, o contiene enlaces o rutas protegidas." : null;
            if (entry.Sensitive && action == ActionKind.Delete) { risk = Risk.Caution; blocked = true; blockReason = "Contiene credenciales o llaves protegidas."; reasons.Add(blockReason); }
            if (entry.Repository && action == ActionKind.Delete) { risk = Risk.Caution; reasons.Add("Contiene código e historial de Git."); }
            if (action == ActionKind.Delete && ContainingRepository(path) is { } repository)
            {
                if (!gitIndexes.TryGetValue(repository, out var git)) gitIndexes[repository] = git = GitIndexReader.Read(repository, Policy);
                var relative = Path.GetRelativePath(repository, path).Replace('\\', '/');
                if (!git.Known)
                {
                    risk = risk < Risk.Review ? Risk.Review : risk;
                    reasons.Add(git.Reason ?? "No se pudo comprobar qué archivos están versionados; se exige revisión.");
                }
                else if (git.Tracked.Any(file => file.Equals(relative, StringComparison.OrdinalIgnoreCase) || file.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    risk = Risk.Caution;
                    reasons.Add("Contiene archivos versionados en Git; no se considera una compilación o caché prescindible.");
                }
            }
            if (entry.Personal && !allowPersonal && action == ActionKind.Delete) { risk = risk < Risk.Review ? Risk.Review : risk; reasons.Add("Contiene documentos, medios, bases de datos, modelos u otros resultados que podrían ser únicos."); }
            if (IsBackupName(Path.GetFileName(path)) && action == ActionKind.Delete) { risk = risk < Risk.Review ? Risk.Review : risk; reasons.Add("El nombre sugiere una copia de seguridad; puede ser la única copia de algo."); }
            var age = minimumAge ?? (Options.MinimumAge > TimeSpan.FromHours(24) ? Options.MinimumAge : TimeSpan.FromHours(24));
            if (!IsOld(entry, age)) { blocked = true; blockReason ??= $"Cambió recientemente; necesita al menos {age.TotalHours:0} horas sin cambios."; }
            var names = processes ?? [];
            bool? inUse;
            try { inUse = processCheck(names); } catch { inUse = null; }
            if (inUse != false) { blocked = true; blockReason = inUse == true ? "Una aplicación relacionada está funcionando." : "No se pudo comprobar qué aplicaciones están funcionando."; }
            if (entry.Snapshot.LinkCount > 1) reasons.Add("Tiene enlaces físicos: borrar un nombre puede no liberar sus datos.");
            if (originalRisk == Risk.Safe && risk != Risk.Safe && action == ActionKind.Delete)
                consequence = "No se ha demostrado que todo este contenido se regenere. Puede contener archivos propios, versionados o únicos. Revisa su contenido; si decides retirarlo, va a la Papelera.";
            var snapshots = Index.Snapshots(path, Token);
            var candidate = new Candidate
            {
                Id = Id(category + ":" + action + ":" + path), Title = title, Category = category, Risk = risk, Action = action,
                Description = path, Consequence = consequence, Paths = [path], Snapshots = snapshots,
                Bytes = snapshots.Any(x => x.LinkCount > 1) ? 0 : entry.Bytes,
                Recommended = recommend && risk == Risk.Safe && !blocked,
                MaximumEligible = (recommend || maximum) && risk == Risk.Safe && !blocked,
                CanSelect = !blocked, IsBlocked = blocked, BlockReason = blockReason, Reasons = reasons,
                ProcessNames = names, PreservedPath = preserved, SHA256 = hash, LastActivityUtc = entry.Latest
            };
            Candidates.Add(candidate);
            if (action == ActionKind.Delete) offered.Add(path);
            if (preserved is not null) Preserved.Add(preserved);
            return candidate;
        }
        private string? ContainingRepository(string path)
        {
            for (var directory = Index.Get(path)?.Snapshot.IsDirectory == true ? path : Path.GetDirectoryName(path);
                directory is not null && Policy.ApprovedRoots.Any(root => SafetyPolicy.IsWithin(directory, root)); directory = Path.GetDirectoryName(directory))
                if (Index.RepositoryRoots.Contains(directory)) return directory;
            return null;
        }
        internal void Command(string path, string title, OfficialCommand command, Risk risk, long bytes = 0, string? consequence = null)
        {
            Token.ThrowIfCancellationRequested();
            var entry = Index.Get(path);
            var names = command.Kind == OfficialCommandKind.GitGarbageCollect ? new[] { "git", "git-remote-https" } : new[] { "docker" };
            var excluded = Policy.IsExcludedPath(path) || Policy.ContainsExcludedPath(path);
            var globalWithExclusions = command.Kind != OfficialCommandKind.GitGarbageCollect && Policy.ExcludedRoots.Count > 0;
            // Official maintenance actions require an explicit review even when their result is reproducible.
            Candidates.Add(new() { Id = Id(command.Kind + path), Title = title, Category = CleanupCategory.Development,
                Action = ActionKind.OfficialCommand, Risk = risk, Paths = [path], Command = command, Bytes = bytes,
                Snapshots = entry is null ? [] : [entry.Snapshot], Description = "Se utiliza el comando oficial de la herramienta.",
                Consequence = consequence ?? "No pasa por la Papelera. Revisa el alcance del comando antes de ejecutarlo.",
                CanSelect = !excluded && !globalWithExclusions, IsBlocked = excluded || globalWithExclusions,
                BlockReason = globalWithExclusions ? "Este comando tiene alcance global y no puede garantizar las exclusiones de carpetas configuradas."
                    : excluded ? "El alcance del comando incluye una carpeta excluida." : null,
                ProcessNames = names, LastActivityUtc = entry?.Latest, Reasons = ["Operación explícita: el análisis no ejecutó este comando."] });
        }
    }

    private static bool IsBackupName(string name) => new[] { "backup", "respaldo", "copia de", "old", "original" }
        .Any(x => name.StartsWith(x, StringComparison.OrdinalIgnoreCase) || name.EndsWith("-" + x, StringComparison.OrdinalIgnoreCase) || name.EndsWith("_" + x, StringComparison.OrdinalIgnoreCase));
    private static string[] Names(params string[] names) => names;
    private static string? SmallText(string path, SafetyPolicy policy, int limit = 262_144)
    {
        try
        {
            if (policy.IsExcludedPath(path)) return null;
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > limit || (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0) return null;
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
