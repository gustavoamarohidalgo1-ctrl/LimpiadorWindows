using Limpiador.Core;
using System.Security.Cryptography;

internal static class CoreRegressionTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return ("Plan: candidatos multi-ruta no solapan otras operaciones", () => { MultiPathPlan(); return Task.CompletedTask; });
        yield return ("Duplicados: conserva coincidencias verificadas al agotar presupuesto", DuplicateBudgetAsync);
        yield return ("Índice: una raíz redirigida no atraviesa enlaces en sus ancestros", RedirectedRootAsync);
        yield return ("Índice: informa y cancela durante una carpeta ancha", WideDirectoryCancellationAsync);
        yield return ("Índice: el límite exacto conserva la integridad del árbol", ExactEntryLimitAsync);
        yield return ("Escáner: la cobertura respeta nombres vecinos de carpeta", NeighbouringCandidatesAsync);
        yield return ("Seguridad: una raíz de volumen nunca es un perfil", () => { VolumeRootIsNotAProfile(); return Task.CompletedTask; });
        yield return ("Plan: explica cada exclusión y rechaza IDs ambiguos", () => { PlannerExclusionReasons(); return Task.CompletedTask; });
        yield return ("Plan: mantiene prioridades y dependencias entre originales", () => { PlannerPriorityAndDependencies(); return Task.CompletedTask; });
        yield return ("Plan: procesa doce mil candidatos sin duplicar operaciones", () => { PlannerScale(); return Task.CompletedTask; });
        yield return ("Exclusiones: no capturan metadatos y protegen padres sin ocultar hermanos", ExcludedSubtreeAsync);
        yield return ("Exclusiones: una raíz completa o redirigida no se recorre", ExcludedRootsAsync);
        yield return ("Exclusiones: Git omitido nunca convierte artefactos en seguros", ExcludedGitAsync);
        yield return ("Exclusiones: comandos globales muestran su incompatibilidad", ExcludedGlobalCommandAsync);
    }

    private static void MultiPathPlan()
    {
        using var fixture = new RegressionFixture();
        var parent = Path.Combine(fixture.Root, "cache");
        var child = new Candidate { Id = "child", Paths = [Path.Combine(parent, "file.txt")], Bytes = 12,
            Risk = Risk.Safe, Recommended = true, Action = ActionKind.Compress };
        var multi = new Candidate { Id = "multi", Paths = [parent, Path.Combine(fixture.Root, "another-independent-cache-with-a-long-name")],
            Bytes = 100, Risk = Risk.Safe, Recommended = true, Action = ActionKind.Delete };
        foreach (var input in new[] { new[] { child, multi }, new[] { multi, child } })
        {
            var plan = CleanupPlan.Create(input);
            Require(plan.Items.Count == 1, "Aceptó compresión de un hijo y eliminación de su carpeta en el mismo plan.");
            Require(plan.Excluded.Count == 1 && plan.EstimatedBytes == plan.Items[0].Bytes,
                "Las operaciones excluidas se contabilizaron como ahorro.");
        }
        var separate = child with { Id = "neighbour", Paths = [parent + "-neighbour"] };
        Require(CleanupPlan.Create([multi, separate]).Items.Count == 2, "Confundió nombres vecinos con rutas solapadas.");
    }

    private static async Task DuplicateBudgetAsync()
    {
        using var fixture = new RegressionFixture();
        const int bytes = 4096;
        foreach (var name in new[] { "a", "b", "c" }) fixture.Write($"Documents/{name}.txt", new string('a', bytes));
        var scan = await new Scanner().ScanAsync(fixture.Options(maximumHashBytes: 2 * bytes));
        var copies = scan.Candidates.Where(x => x.Category == CleanupCategory.Duplicates).ToArray();
        Require(copies.Length == 1, "Descartó una coincidencia comprobada al llegar a un tercer archivo sin presupuesto.");
        var copy = copies[0];
        Require(copy.PreservedPath is not null && !copy.Paths.Contains(copy.PreservedPath), "No conservó un original independiente.");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(copy.PreservedPath!))).ToLowerInvariant();
        Require(copy.SHA256 == hash && copy.Paths.All(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() == hash),
            "Ofreció una copia sin contenido idéntico comprobado.");
        Require(scan.Warnings.Count(x => x.Contains("límite de lectura de duplicados")) == 1, "Falta una advertencia única del límite.");
        var insufficient = await new Scanner().ScanAsync(fixture.Options(maximumHashBytes: bytes));
        Require(!insufficient.Candidates.Any(x => x.Category == CleanupCategory.Duplicates), "Una sola lectura no demuestra una pareja idéntica.");
    }

    private static async Task RedirectedRootAsync()
    {
        using var fixture = new RegressionFixture();
        var profile = Directory.CreateDirectory(Path.Combine(fixture.Root, "profile")).FullName;
        fixture.Write("external/Local/Temp/private.tmp", "Must never be indexed through an alias");
        var link = Path.Combine(fixture.Root, "redirect");
        try { Directory.CreateSymbolicLink(link, Path.Combine(fixture.Root, "external")); }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        { Console.WriteLine("  SKIP raíz redirigida: Windows no permite crear enlaces con este usuario."); return; }
        catch (IOException ex) when (OperatingSystem.IsWindows() && (ex.HResult & 0xffff) == 1314)
        { Console.WriteLine("  SKIP raíz redirigida: falta privilegio de enlace o modo desarrollador."); return; }
        var metadata = new RecordingMetadata();
        var redirected = Path.Combine(link, "Local");
        var result = await new Scanner().ScanAsync(new ScanOptions
        {
            ProfilePath = profile, LocalAppDataPath = redirected, MetadataProvider = metadata,
            MinimumBytes = 1, IncludeCompression = false, IncludeDuplicates = false, AreProcessesRunning = _ => false
        });
        Require(!metadata.Paths.Any(path => SafetyPolicy.IsWithin(path, redirected)), "El índice atravesó un ancestro enlazado de la raíz autorizada.");
        Require(result.Stats.SkippedDirectories > 0 && result.Warnings.Any(x => x.Contains(redirected)), "Omitió la raíz sin comunicarlo.");
    }

    private static async Task WideDirectoryCancellationAsync()
    {
        using var fixture = new RegressionFixture();
        for (var i = 0; i < 800; i++) fixture.Write($"wide/{i:D4}.tmp", "x");
        using var cancellation = new CancellationTokenSource();
        var metadata = new RecordingMetadata();
        var progress = new CallbackProgress(value =>
        {
            if (value.Phase == "Índice del disco" && value.FilesVisited > 0) cancellation.Cancel();
        });
        try { await new Scanner().ScanAsync(fixture.Options(metadata: metadata), progress, cancellation.Token); }
        catch (OperationCanceledException)
        {
            Require(metadata.Paths.Count <= 256, "Continuó leyendo la carpeta tras recibir cancelación.");
            Require(Directory.EnumerateFiles(Path.Combine(fixture.Root, "wide")).Count() == 800, "La cancelación alteró los archivos.");
            return;
        }
        throw new Exception("No entregó progreso cancelable durante la enumeración de una carpeta ancha.");
    }

    private static async Task ExactEntryLimitAsync()
    {
        using var fixture = new RegressionFixture();
        const int files = 20;
        for (var i = 0; i < files; i++) fixture.Write($"cache/{i}.tmp", "x");
        var metadata = new RecordingMetadata();
        // Profile + cache directory + files. A limit equal to the exact tree size
        // must not report truncation just because the last file filled the budget.
        var exact = await new Scanner().ScanAsync(fixture.Options(maximumEntries: files + 2, metadata: metadata));
        Require(exact.Stats.Entries == files + 2 && metadata.Paths.Count == files + 2, "El índice no respetó el recuento exacto.");
        Require(exact.Directories.All(x => x.Complete) && !exact.Warnings.Any(x => x.Contains("límite de archivos")), "Marcó incompleto un árbol completamente leído.");
        var limited = await new Scanner().ScanAsync(fixture.Options(maximumEntries: files));
        Require(limited.Stats.Entries == files && limited.Directories.All(x => !x.Complete), "El límite permitió una carpeta incompleta seleccionable.");
    }

    private static async Task NeighbouringCandidatesAsync()
    {
        using var fixture = new RegressionFixture();
        fixture.Write("AppData/Local/Temp/npm-cache/item.tmp", "a");
        fixture.Write("AppData/Local/Temp/npm-cache-neighbour/item.tmp", "b");
        var scan = await new Scanner().ScanAsync(fixture.Options());
        var candidates = scan.Candidates.Where(x => x.Category == CleanupCategory.SystemTemporary).ToArray();
        Require(candidates.Length == 2 && candidates.All(x => x.CanSelect && x.Recommended), "La búsqueda por ancestros ocultó una carpeta vecina independiente.");
    }

    private static void VolumeRootIsNotAProfile()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))!;
        Require(SafetyPolicy.IsWithin(Path.Combine(root, "example"), root), "La comparación de rutas agregó dos separadores a la raíz.");
        try { _ = new SafetyPolicy(new ScanOptions { ProfilePath = root }); }
        catch (ArgumentException) { return; }
        throw new Exception("Se aceptó una unidad completa como perfil de usuario.");
    }

    private static void PlannerExclusionReasons()
    {
        using var fixture = new RegressionFixture();
        Candidate Item(string id, string path) => new() { Id = id, Title = id, Paths = [Path.Combine(fixture.Root, path)], Risk = Risk.Safe, Recommended = true, Bytes = 10 };
        var parent = Item("parent", "cache");
        var child = Item("child", "cache/file");
        var blocked = Item("blocked", "blocked") with { IsBlocked = true, BlockReason = "Archivo abierto por otra aplicación." };
        var review = Item("review", "review") with { Risk = Risk.Review };
        var irreversible = Item("command", "docker") with { Risk = Risk.Review, Action = ActionKind.OfficialCommand,
            Command = new(OfficialCommandKind.DockerBuildCachePrune, "docker.exe", []) };
        var repeatedA = Item("duplicate-id", "first");
        var repeatedB = Item("duplicate-id", "second");
        Candidate[] all = [parent, child, blocked, review, irreversible, repeatedA, repeatedB];
        var plan = CleanupPlan.Create(all, maximum: true, manualIds: [irreversible.Id, repeatedA.Id]);
        Require(plan.Items.Select(x => x.Id).SequenceEqual([parent.Id]), "Autorizó un ID ambiguo, un solape o una operación bloqueada.");
        Require(plan.Excluded.Count == 6 && plan.ExclusionReasons.Count == 5, "No hay exactamente una razón por ID excluido.");
        Require(plan.Excluded.All(x => !string.IsNullOrWhiteSpace(plan.ExclusionReasons.GetValueOrDefault(x.Id))), "Hay un elemento excluido sin explicación.");
        Require(plan.ExclusionReasons[blocked.Id] == blocked.BlockReason, "Perdió el motivo concreto del bloqueo.");
        Require(plan.ExclusionReasons[child.Id].Contains(parent.Id), "La explicación no identifica el solape que prevalece.");
        Require(!plan.ExclusionReasons.ContainsKey(parent.Id), "El elemento ejecutable aparece como excluido.");
        var reversed = CleanupPlan.Create(all.Reverse(), maximum: true, manualIds: [irreversible.Id, repeatedA.Id]);
        Require(reversed.Excluded.Count(x => x.Id == repeatedA.Id) == 2 && !reversed.Items.Any(x => x.Id == repeatedA.Id), "La ambigüedad de ID depende del orden.");
    }

    private static void PlannerPriorityAndDependencies()
    {
        using var fixture = new RegressionFixture();
        var parent = Path.Combine(fixture.Root, "parent");
        var original = Path.Combine(parent, "original");
        var copy = new Candidate { Id = "copy", Title = "copia", Paths = [Path.Combine(fixture.Root, "copy")], PreservedPath = original };
        var broad = new Candidate { Id = "broad", Paths = [parent] };
        var plan = CleanupPlan.Create([broad, copy], manualIds: [broad.Id, copy.Id]);
        Require(plan.Items.Select(x => x.Id).SequenceEqual([copy.Id]) && plan.ExclusionReasons[broad.Id].Contains(original), "Permitió retirar el contenedor de la original preservada.");
        var a = new Candidate { Id = "a", Paths = [Path.Combine(fixture.Root, "a")], PreservedPath = Path.Combine(fixture.Root, "b") };
        var b = new Candidate { Id = "b", Paths = [Path.Combine(fixture.Root, "b")], PreservedPath = Path.Combine(fixture.Root, "a") };
        Require(CleanupPlan.Create([a, b], manualIds: [a.Id, b.Id]).Items.Count == 0, "Autorizó un ciclo que eliminaría ambas originales.");

        // A small legacy oracle verifies that the indexed algorithm preserves the
        // existing stable priorities, including multiple paths and different actions.
        var candidates = Enumerable.Range(0, 160).Select(i => new Candidate
        {
            Id = "priority-" + i, Risk = Risk.Safe, Recommended = true, Bytes = i % 7,
            Action = i % 3 == 0 ? ActionKind.Compress : ActionKind.Delete,
            Paths = i % 4 == 0 ? [Path.Combine(fixture.Root, "group-" + i % 13), Path.Combine(fixture.Root, "extra-" + i)]
                : [Path.Combine(fixture.Root, "group-" + i % 13, "child-" + i % 5)]
        }).ToArray();
        var expected = new List<Candidate>();
        foreach (var candidate in candidates.OrderBy(x => x.Paths.Sum(p => p.Length)).ThenByDescending(x => x.Bytes))
            if (!expected.Any(prior => prior.Paths.Any(p => candidate.Paths.Any(q => SafetyPolicy.IsWithin(p, q) || SafetyPolicy.IsWithin(q, p)))))
                expected.Add(candidate);
        var actual = CleanupPlan.Create(candidates);
        Require(actual.Items.Select(x => x.Id).Order().SequenceEqual(expected.Select(x => x.Id).Order()), "Cambió las prioridades al optimizar los solapes.");
        Require(actual.Excluded.Count + actual.Items.Count == candidates.Length, "Perdió candidatos al particionar el plan.");
    }

    private static void PlannerScale()
    {
        using var fixture = new RegressionFixture();
        const int count = 6000;
        var candidates = new List<Candidate>(count * 2);
        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(fixture.Root, "cache-" + i.ToString("D5"));
            candidates.Add(new() { Id = "parent-" + i, Paths = [path], Risk = Risk.Safe, Recommended = true, Bytes = 20 });
            candidates.Add(new() { Id = "child-" + i, Paths = [Path.Combine(path, "data")], Risk = Risk.Safe, Recommended = true, Bytes = 10 });
        }
        var plan = CleanupPlan.Create(candidates);
        Require(plan.Items.Count == count && plan.Excluded.Count == count && plan.ExclusionReasons.Count == count, "No resolvió todos los pares de solapes de la selección grande.");
        Require(plan.EstimatedBytes == count * 20L && plan.Items.Select(x => x.Id).Distinct().Count() == count, "Duplicó operaciones o ahorro.");
        Require(plan.Excluded.All(x => plan.ExclusionReasons[x.Id].Contains("parent-")), "Falta el candidato que prevalece en la explicación.");
    }

    private static async Task ExcludedSubtreeAsync()
    {
        using var fixture = new RegressionFixture();
        var secret = fixture.Write("AppData/Local/npm-cache/_cacache/private/secret.tmp", "keep");
        var sibling = fixture.Write("AppData/Local/Temp/npm-allowed/cache.tmp", "allowed");
        fixture.Write("AppData/Local/npm-cache/_cacache/ordinary.tmp", "normal");
        var excluded = Path.GetDirectoryName(secret)!;
        var parent = Path.GetDirectoryName(excluded)!;
        var configured = new List<string> { excluded, Path.Combine(excluded, ".") };
        var metadata = new RecordingMetadata();
        var options = fixture.Options(metadata: metadata, excludedRoots: configured);
        var policy = new SafetyPolicy(options);
        Require(policy.ExcludedRoots.Count == 1 && configured.Count == 2, "No normalizó y deduplicó sin mutar las opciones originales.");
        var result = await new Scanner().ScanAsync(options);
        Require(!metadata.Paths.Any(path => SafetyPolicy.IsWithin(path, excluded)), "Capturó metadatos de una exclusión antes de omitirla.");
        Require(!result.Directories.Any(x => SafetyPolicy.IsWithin(x.Path, excluded)), "Indexó un directorio excluido.");
        Require(result.Directories.Single(x => x.Path == parent).Complete == false, "El padre aparenta ser un árbol completo.");
        var parentCandidate = result.Candidates.Single(x => x.Paths.Contains(parent));
        Require(!parentCandidate.CanSelect && parentCandidate.IsBlocked && parentCandidate.BlockReason!.Contains("excluida"), "Ofreció una operación sobre el padre de una exclusión.");
        Require(result.Candidates.Any(x => x.Paths.Contains(Path.GetDirectoryName(sibling)!) && x.CanSelect && x.Recommended), "Ocultó un hermano permitido de la exclusión.");
        Require(!policy.Validate(secret).Allowed && !policy.Validate(parent).Allowed && !policy.ValidateTree(parent).Allowed,
            "La política de mutación permite la exclusión o un contenedor suyo.");
        Require(policy.Validate(sibling).Allowed && !policy.IsExcludedPath(excluded + "-other"), "Confundió un prefijo vecino con la exclusión.");
        configured.Clear();
        Require(policy.IsExcludedPath(secret), "La política cambió cuando se modificó la colección original.");
        var futurePolicy = new SafetyPolicy(fixture.Options(excludedRoots: [Path.Combine(parent, "future")]));
        Require(!futurePolicy.Validate(parent).Allowed, "Una exclusión ausente dejó de proteger su carpeta padre.");
    }

    private static async Task ExcludedRootsAsync()
    {
        using var fixture = new RegressionFixture();
        fixture.Write("Documents/private.txt", "private");
        var metadata = new RecordingMetadata();
        var result = await new Scanner().ScanAsync(fixture.Options(metadata: metadata, excludedRoots: [fixture.Root]));
        Require(metadata.Paths.Count == 0 && result.Stats.Entries == 0, "Recorrió el perfil excluido completo.");
        var profile = Directory.CreateDirectory(Path.Combine(fixture.Root, "profile")).FullName;
        var local = Path.Combine(fixture.Root, "external", "Local");
        fixture.Write("external/Local/Temp/cache.tmp", "private");
        metadata = new();
        result = await new Scanner().ScanAsync(new ScanOptions
        {
            ProfilePath = profile, LocalAppDataPath = local, ExcludedRoots = [Path.GetDirectoryName(local)!],
            MetadataProvider = metadata, MinimumBytes = 1, IncludeCompression = false, AreProcessesRunning = _ => false
        });
        Require(!metadata.Paths.Any(path => SafetyPolicy.IsWithin(path, local)), "Una Known Folder redirigida eludió la exclusión de su ancestro.");
        Require(metadata.Paths.Contains(profile), "La exclusión redirigida impidió analizar el perfil permitido.");
    }

    private static async Task ExcludedGitAsync()
    {
        using var fixture = new RegressionFixture();
        fixture.Write("Documents/project/sample.csproj", "<Project/>");
        fixture.Write("Documents/project/obj/output.bin", "could be tracked");
        var gitFile = fixture.Write("Documents/project/.git/index", "excluded metadata must not be read");
        var git = Path.GetDirectoryName(gitFile)!;
        var metadata = new RecordingMetadata();
        var scan = await new Scanner().ScanAsync(fixture.Options(metadata: metadata, excludedRoots: [git]));
        var artifact = scan.Candidates.Single(x => x.Paths.Contains(Path.Combine(fixture.Root, "Documents", "project", "obj")));
        Require(artifact.Risk != Risk.Safe && !artifact.Recommended && !artifact.MaximumEligible, "Interpretó Git excluido como ausencia de archivos versionados.");
        Require(!metadata.Paths.Any(path => SafetyPolicy.IsWithin(path, git)), "Capturó metadatos de Git excluido.");
    }

    private static async Task ExcludedGlobalCommandAsync()
    {
        using var fixture = new RegressionFixture();
        fixture.Write(".docker/config.json", "{}");
        var excluded = Path.Combine(fixture.Root, "Documents", "private");
        var scan = await new Scanner().ScanAsync(fixture.Options(excludedRoots: [excluded]));
        var command = scan.Candidates.Single(x => x.Command?.Kind == OfficialCommandKind.DockerBuildCachePrune);
        Require(!command.CanSelect && command.IsBlocked && command.BlockReason!.Contains("global"), "El plan ofrece un comando global incapaz de respetar exclusiones.");
        var unrestricted = await new Scanner().ScanAsync(fixture.Options());
        Require(unrestricted.Candidates.Single(x => x.Command?.Kind == OfficialCommandKind.DockerBuildCachePrune).CanSelect,
            "Cambió el comportamiento del comando cuando no hay exclusiones.");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class CallbackProgress(Action<ScanProgress> callback) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => callback(value);
    }

    private sealed class RecordingMetadata : IFileMetadataProvider
    {
        private readonly FileMetadataProvider actual = new();
        private readonly DateTime old = DateTime.UtcNow.AddDays(-180);
        public List<string> Paths { get; } = [];
        public FileSnapshot Capture(string path)
        {
            Paths.Add(path);
            return actual.Capture(path) with { LastWriteTimeUtc = old, CreationTimeUtc = old };
        }
    }

    private sealed class RegressionFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CleanerCoreRegression-" + Guid.NewGuid().ToString("N"));
        public RegressionFixture() => Directory.CreateDirectory(Root);
        public ScanOptions Options(long maximumHashBytes = 1_000_000, int maximumEntries = 500_000, IFileMetadataProvider? metadata = null,
            IReadOnlyList<string>? excludedRoots = null) => new()
        {
            ProfilePath = Root, MinimumBytes = 1, IncludeCompression = false,
            MaximumHashBytes = maximumHashBytes, MaximumEntries = maximumEntries,
            MetadataProvider = metadata ?? new RecordingMetadata(), AreProcessesRunning = _ => false, ExcludedRoots = excludedRoots ?? []
        };
        public string Write(string relative, string contents)
        {
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
            return path;
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
