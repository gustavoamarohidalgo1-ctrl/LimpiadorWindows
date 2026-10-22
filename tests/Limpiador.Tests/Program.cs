using Limpiador.Core;
using Limpiador.Runtime;
using System.Security.Cryptography;
using System.Runtime.InteropServices;

Console.WriteLine($"Limpiador Ultra Windows {typeof(HistoryStore).Assembly.GetName().Version} — verificación");
Console.WriteLine($"Sistema: {RuntimeInformation.OSDescription}");
Console.WriteLine($"Arquitectura del sistema: {RuntimeInformation.OSArchitecture}; proceso: {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine($"Entorno: {RuntimeInformation.FrameworkDescription}; fecha UTC: {DateTimeOffset.UtcNow:O}");
Console.WriteLine();

var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Action action) => tests.Add((name, () => { action(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> action) => tests.Add((name, action));
void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"Se esperaba {typeof(T).Name}.");
}

Test("No confundir prefijos de carpetas", () =>
{
    var parent = Path.Combine(Path.GetTempPath(), "perfil");
    Require(SafetyPolicy.IsWithin(Path.Combine(parent, "archivo"), parent), "Descendiente rechazado.");
    Require(!SafetyPolicy.IsWithin(parent + "-otro", parent), "Aceptó un prefijo vecino.");
});
Test("Credenciales, raíces y nubes protegidas", () =>
{
    using var fixture = new Fixture();
    var secret = fixture.Write(".ssh/id_rsa", "PRIVATE");
    var cloud = fixture.Write("OneDrive/documento.txt", "personal");
    var ordinary = fixture.Write("Downloads/data.tmp", "sample");
    var policy = new SafetyPolicy(fixture.Options);
    Require(!policy.Validate(secret).Allowed, "Permitió clave SSH.");
    Require(!policy.Validate(cloud).Allowed, "Permitió ruta sincronizada.");
    Require(!policy.Validate(fixture.Root).Allowed, "Permitió borrar perfil.");
    Require(policy.Validate(ordinary).Allowed, "Rechazó fixture ordinario.");
    Require(!policy.Validate(Path.Combine(fixture.Root, "..", "exterior")).Allowed, "Escape de perfil.");
});
Test("Los enlaces no se siguen", () =>
{
    using var fixture = new Fixture();
    var original = fixture.Write("Documents/guardado.txt", "keep");
    var link = Path.Combine(fixture.Root, "alias");
    try { Directory.CreateSymbolicLink(link, Path.GetDirectoryName(original)!); }
    catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { Console.WriteLine("  SKIP symlink: el usuario Windows no tiene privilegio de enlace."); return; }
    catch (IOException ex) when (OperatingSystem.IsWindows() && (ex.HResult & 0xffff) == 1314) { Console.WriteLine("  SKIP symlink: Windows requiere modo desarrollador o privilegio de enlace."); return; }
    Require(!new SafetyPolicy(fixture.Options).Validate(Path.Combine(link, "guardado.txt")).Allowed, "Permitió enlace de ancestro.");
});
Test("Revalidación detecta modificaciones y archivos añadidos", () =>
{
    using var fixture = new Fixture();
    var path = fixture.Write("cache/old.tmp", "original");
    var root = Path.GetDirectoryName(path)!;
    var provider = new FileMetadataProvider();
    var item = new Candidate { Paths = [root], Snapshots = [provider.Capture(root), provider.Capture(path)] };
    _ = SnapshotValidator.Validate(item, new SafetyPolicy(fixture.Options), provider);
    fixture.Write("cache/new.tmp", "added");
    Throws<IOException>(() => SnapshotValidator.Validate(item, new SafetyPolicy(fixture.Options), provider));
    var fileItem = new Candidate { Paths = [path], Snapshots = [provider.Capture(path)] };
    File.AppendAllText(path, "modified");
    Throws<IOException>(() => SnapshotValidator.Validate(fileItem, new SafetyPolicy(fixture.Options), provider));
});
Test("Modo máximo conserva material para revisar y originales de duplicados", () =>
{
    using var fixture = new Fixture();
    var original = fixture.Write("Documents/original.txt", "same");
    var duplicate = fixture.Write("Downloads/copy.txt", "same");
    var safe = new Candidate { Id = "safe", Risk = Risk.Safe, Recommended = true, Paths = [Path.Combine(fixture.Root, "cache")] };
    var review = new Candidate { Id = "review", Risk = Risk.Review, Recommended = true, MaximumEligible = true, Paths = [original] };
    var copy = new Candidate { Id = "copy", Risk = Risk.Review, Paths = [duplicate], PreservedPath = original };
    Require(CleanupPlan.Create([safe, review], true).Items.Select(x => x.Id).SequenceEqual(["safe"]), "Autoseleccionó Revisar.");
    var manual = CleanupPlan.Create([review, copy], true, ["review", "copy"]);
    Require(manual.Items.Count == 1 && manual.Items[0].Id == "copy", "Permitió borrar el original conservado.");
    Require(CleanupPlan.Create([safe with { IsBlocked = true }], true, ["safe"]).Items.Count == 0, "Permitió elemento bloqueado.");
});
Test("Historial se guarda de forma duradera y rechaza IDs con rutas", () =>
{
    using var fixture = new Fixture();
    var store = new HistoryStore(Path.Combine(fixture.Root, "history"));
    var record = new HistoryRecord { Title = "Prueba" };
    record.Details.Add("intención"); store.Save(record);
    record.Status = "Finalizado"; store.Save(record);
    Require(store.Read(record.Id).Status == "Finalizado" && store.List().Count == 1, "Historial inconsistente.");
    Throws<ArgumentException>(() => store.Read("../../outside"));
});
AsyncTest("Escáner conserva archivos y evita credenciales, nubes y temporales personales", async () =>
{
    using var fixture = new Fixture();
    fixture.Write("AppData/Local/Temp/report.csv", "name,value\none,12");
    fixture.Write("AppData/Local/Temp/key.pem", "PRIVATE");
    fixture.Write("AppData/Local/npm-cache/_cacache/content-v2/pkg.bin", new string('a', 4096));
    fixture.Write("OneDrive/important.txt", new string('x', 4096));
    fixture.Write("Downloads/setup.msi", new string('m', 4096));
    fixture.AgeAll();
    var before = fixture.Hashes();
    var result = await new Scanner().ScanAsync(fixture.Options);
    Require(result.Candidates.Any(x => x.Category == CleanupCategory.Development && x.Paths.Any(p => p.Contains("npm-cache"))), "No detectó npm.");
    Require(result.Candidates.All(x => !(x.Recommended || x.MaximumEligible) || x.Risk == Risk.Safe), "Autoseleccionó material no seguro.");
    Require(result.Candidates.Where(x => x.Paths.Any(p => p.EndsWith("setup.msi"))).All(x => !x.Recommended && x.Risk != Risk.Safe), "MSI nativo se consideró basura automáticamente.");
    Require(result.Candidates.All(x => !x.Paths.Any(p => p.Contains("OneDrive") || p.EndsWith("key.pem")) || !x.CanSelect), "Ofreció datos protegidos.");
    Require(result.Candidates.Where(x => x.Paths.Any(p => p.EndsWith("report.csv"))).All(x => !x.Recommended), "Autoseleccionó informe temporal.");
    Require(before.OrderBy(x => x.Key).SequenceEqual(fixture.Hashes().OrderBy(x => x.Key)), "El análisis modificó archivos.");
});
AsyncTest("Duplicados se comparan por contenido y conservan un original", async () =>
{
    using var fixture = new Fixture();
    fixture.Write("Downloads/a.txt", new string('a', 8192));
    fixture.Write("Documents/b.txt", new string('a', 8192));
    fixture.Write("Documents/c.txt", new string('c', 8192));
    fixture.AgeAll();
    var scan = await new Scanner().ScanAsync(fixture.Options);
    var copies = scan.Candidates.Where(x => x.Category == CleanupCategory.Duplicates && x.Paths.Count > 0).ToArray();
    Require(copies.Length > 0, "No se detectó duplicado idéntico.");
    Require(copies.All(x => x.PreservedPath is not null && x.SHA256 is not null), "Falta original/hash.");
    Require(copies.All(x => !x.Paths.Contains(x.PreservedPath!)), "Ofreció su propio original.");
    Require(copies.All(x => !x.Paths.Any(p => p.EndsWith("c.txt"))), "Agrupó contenido distinto del mismo tamaño.");
});
AsyncTest("La cancelación interrumpe el análisis sin cambios", async () =>
{
    using var fixture = new Fixture(); fixture.Write("Downloads/file.txt", "preserve");
    using var token = new CancellationTokenSource(); token.Cancel();
    try { await new Scanner().ScanAsync(fixture.Options, cancellationToken: token.Token); }
    catch (OperationCanceledException) { Require(File.Exists(Path.Combine(fixture.Root, "Downloads/file.txt")), "Perdió datos."); return; }
    throw new Exception("No respetó cancelación.");
});
AsyncTest("Los intermedios versionados en Git no se seleccionan", async () =>
{
    using var fixture = new Fixture();
    fixture.Write("Documents/project/sample.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
    fixture.Write("Documents/project/obj/tracked.bin", new string('t', 8192));
    fixture.GitIndex("Documents/project", ["obj/tracked.bin"]);
    fixture.AgeAll();
    var result = await new Scanner().ScanAsync(fixture.Options);
    var obj = result.Candidates.FirstOrDefault(x => x.Paths.Any(p => p.EndsWith(Path.Combine("project", "obj"))));
    Require(obj is not null && obj.Risk == Risk.Caution && !obj.Recommended && !obj.MaximumEligible, "Ofreció borrar automáticamente un intermedio versionado.");
});
AsyncTest("Un índice Git desconocido bloquea recomendaciones automáticas", async () =>
{
    using var fixture = new Fixture();
    fixture.Write("Documents/project/package.json", "{\"dependencies\":{}}");
    fixture.Write("Documents/project/node_modules/pkg/index.js", new string('n', 8192));
    fixture.GitIndex("Documents/project", [], version: 4);
    fixture.AgeAll();
    var result = await new Scanner().ScanAsync(fixture.Options);
    var modules = result.Candidates.FirstOrDefault(x => x.Paths.Any(p => p.EndsWith("node_modules")));
    Require(modules is not null && modules.Risk != Risk.Safe && !modules.MaximumEligible, "Confundió índice no comprobado con ausencia de datos versionados.");
});
AsyncTest("Un índice incompleto no permite seleccionar una carpeta", async () =>
{
    using var fixture = new Fixture();
    for (var i = 0; i < 20; i++) fixture.Write($"AppData/Local/npm-cache/_cacache/{i}.tmp", new string('p', 64));
    fixture.AgeAll();
    var result = await new Scanner().ScanAsync(new ScanOptions { ProfilePath = fixture.Root, MinimumBytes = 1, MaximumEntries = 10, AreProcessesRunning = _ => false });
    Require(result.Warnings.Any(x => x.Contains("límite")), "No comunicó análisis incompleto.");
    Require(result.Candidates.Where(x => x.Paths.Any(Directory.Exists)).All(x => !x.CanSelect), "Permitió carpeta parcialmente indexada.");
});

tests.AddRange(RuntimeRegressionTests.All());
tests.AddRange(CoreRegressionTests.All());
tests.AddRange(ReportRegressionTests.All());
tests.AddRange(PreferencesRegressionTests.All());

var failures = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + "\n" + ex); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} pruebas portátiles aprobadas (motor, historial e informes).");
if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
{
    try
    {
        foreach (var message in await Limpiador.Windows.WindowsSmokeTests.RunAsync(Path.GetTempPath())) Console.WriteLine(message);
    }
    catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL integración nativa Windows\n" + ex); }
}
else Console.WriteLine("Las pruebas nativas de Windows requieren Windows; no se simulan como aprobadas.");
return failures == 0 ? 0 : 1;

sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CleanerFixture-" + Guid.NewGuid().ToString("N"));
    public ScanOptions Options => new()
    {
        ProfilePath = Root, MinimumBytes = 1, AreProcessesRunning = _ => false,
        MinimumAge = TimeSpan.FromHours(24), IncludeCompression = false
    };
    public Fixture() => Directory.CreateDirectory(Root);
    public string Write(string relative, string value)
    {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value); return path;
    }
    public void AgeAll()
    {
        var old = DateTime.UtcNow.AddDays(-180);
        foreach (var path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        { File.SetLastWriteTimeUtc(path, old); File.SetCreationTimeUtc(path, old); }
        foreach (var path in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length))
        { Directory.SetLastWriteTimeUtc(path, old); Directory.SetCreationTimeUtc(path, old); }
    }
    public Dictionary<string, string> Hashes() => Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
        .ToDictionary(x => x, x => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x))));
    public void GitIndex(string project, string[] paths, uint version = 2)
    {
        using var stream = new MemoryStream();
        void UInt32(uint value) { var bytes = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, value); stream.Write(bytes); }
        stream.Write("DIRC"u8); UInt32(version); UInt32((uint)paths.Length);
        foreach (var path in paths.Order(StringComparer.Ordinal))
        {
            var start = stream.Position;
            for (var i = 0; i < 10; i++) UInt32(i == 6 ? 0x81A4u : 0);
            stream.Write(new byte[20]);
            var name = System.Text.Encoding.UTF8.GetBytes(path);
            var flags = new byte[2]; System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(flags, (ushort)name.Length);
            stream.Write(flags); stream.Write(name); stream.WriteByte(0);
            while ((stream.Position - start) % 8 != 0) stream.WriteByte(0);
        }
        var bytes = stream.ToArray(); stream.Write(SHA1.HashData(bytes));
        var git = Path.Combine(Root, project.Replace('/', Path.DirectorySeparatorChar), ".git");
        Directory.CreateDirectory(git); File.WriteAllBytes(Path.Combine(git, "index"), stream.ToArray());
    }
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}
