using Limpiador.Core;
using Limpiador.Runtime;
using Limpiador.App;
using System.Security.Cryptography;

internal static class PreferencesRegressionTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return ("Preferencias: guarda exclusiones normalizadas sin guardar selecciones", () => Run(PreferenceRoundTrip));
        yield return ("Preferencias: configuración dañada no se sustituye por exclusiones vacías", () => Run(CorruptPreferences));
        yield return ("Preferencias: un guardado inválido conserva el archivo anterior", () => Run(InvalidSave));
        yield return ("Diagnóstico: argumentos conservan exclusiones y rechazan opciones incompletas", () => Run(DiagnosticParsing));
        yield return ("Plan explícito: no añade recomendaciones y rechaza selecciones obsoletas", () => Run(ExplicitSelection));
        yield return ("Vista previa: explica toda la selección sin añadir ni repetir acciones", () => Run(ReviewPlan));
        yield return ("Hash: cancelación entre bloques conserva el flujo y el contenido", () => Run(CancelHash));
    }
    private static Task Run(Action action) { action(); return Task.CompletedTask; }
    private static void PreferenceRoundTrip()
    {
        using var fixture = new Fixture();
        var store = new AppPreferencesStore(Path.Combine(fixture.Root, "preferences.json"));
        Require(store.Load().ExcludedFolders.Count == 0 && !File.Exists(store.FilePath), "La carga por defecto creó archivos.");
        var excluded = Path.Combine(fixture.Root, "protected");
        var paths = new List<string> { excluded, Path.Combine(excluded, ".") };
        store.Save(new() { MinimumAgeDays = 180, AdditionalFolder = fixture.Root, ExcludedFolders = paths });
        paths.Clear();
        var loaded = store.Load();
        Require(loaded.MinimumAgeDays == 180 && loaded.AdditionalFolder == fixture.Root && loaded.ExcludedFolders.SequenceEqual([excluded]), "No se conservaron las preferencias independientes del borrador.");
        Require(!File.ReadAllText(store.FilePath).Contains("Selected", StringComparison.Ordinal) && Directory.GetFiles(fixture.Root).Length == 1, "Persistió selecciones o archivos temporales.");
    }
    private static void CorruptPreferences()
    {
        using var fixture = new Fixture();
        var store = new AppPreferencesStore(fixture.Write("preferences.json", "{truncated"));
        Expect<InvalidDataException>(() => store.Load());
        Require(File.ReadAllText(store.FilePath) == "{truncated", "Se sobrescribió la configuración dañada.");
        foreach (var json in new[]
        {
            "{}", "{\"Version\":1,\"MinimumAgeDays\":90}",
            "{\"Version\":1,\"MinimumAgeDays\":90,\"ExcludedFolders\":null}",
            "{\"Version\":999,\"MinimumAgeDays\":90,\"ExcludedFolders\":[]}",
            "{\"Version\":1,\"MinimumAgeDays\":90,\"ExcludedFolders\":[],\"SelectedIds\":[\"action\"]}"
        })
        {
            File.WriteAllText(store.FilePath, json);
            Expect<InvalidDataException>(() => store.Load());
        }
        var directoryStore = new AppPreferencesStore(fixture.Root);
        Expect<InvalidDataException>(() => directoryStore.Load());
        if (!OperatingSystem.IsWindows())
        {
            File.Delete(store.FilePath);
            File.CreateSymbolicLink(store.FilePath, Path.Combine(fixture.Root, "missing.json"));
            Expect<InvalidDataException>(() => store.Load());
        }
    }
    private static void InvalidSave()
    {
        using var fixture = new Fixture();
        var store = new AppPreferencesStore(Path.Combine(fixture.Root, "preferences.json"));
        store.Save(new() { ExcludedFolders = [fixture.Root] });
        var original = File.ReadAllText(store.FilePath);
        foreach (var bad in new[] { new AppPreferences { MinimumAgeDays = 1 }, new AppPreferences { ExcludedFolders = ["relative"] }, new AppPreferences { ExcludedFolders = null! } })
            Expect<InvalidDataException>(() => store.Save(bad));
        Require(File.ReadAllText(store.FilePath) == original && Directory.GetFiles(fixture.Root).Length == 1, "Un error de validación dañó el archivo previo.");
    }
    private static void DiagnosticParsing()
    {
        using var fixture = new Fixture();
        var existing = Path.Combine(fixture.Root, "existing");
        var added = Path.Combine(fixture.Root, "added");
        var original = new AppPreferences { ExcludedFolders = [existing] };
        var options = DiagnosticArguments.Parse(["--diagnostico", "--excluir", added, "--dias", "180", "--excluir", existing], original);
        Require(options.MinimumAgeDays == 180 && options.ExcludedFolders.SequenceEqual([existing, added]) && original.ExcludedFolders.SequenceEqual([existing]), "Se perdieron o modificaron exclusiones guardadas.");
        foreach (var args in new[] { new[] { "--excluir" }, new[] { "--dias", "15" }, new[] { "--carpeta", "--json" }, new[] { "--desconocido" } })
            Expect<ArgumentException>(() => DiagnosticArguments.Parse(args, original));
    }
    private static void ExplicitSelection()
    {
        using var fixture = new Fixture();
        var first = new Candidate { Id = "first", Paths = [Path.Combine(fixture.Root, "one")], Risk = Risk.Safe, Recommended = true };
        var second = first with { Id = "second", Paths = [Path.Combine(fixture.Root, "two")] };
        var plan = SelectionPlanner.Create([first, second], [second.Id]);
        Require(plan.Items.Select(x => x.Id).SequenceEqual([second.Id]), "La vista previa añadió una recomendación no solicitada.");
        Expect<ArgumentException>(() => SelectionPlanner.Create([first], [second.Id]));
    }
    private static void ReviewPlan()
    {
        var item = new UiItem("one", "Uno", "cache", "Cachés", "", 10, UiRisk.Safe, "Papelera", [], [], []);
        var reviewed = new UiCleanupPlan([item], [new("two", "Dos", "Solapado")]);
        CleanupPlanReview.Validate(reviewed, ["one", "two"]);
        Require(reviewed.AcceptedBytes == 10 && new UiCleanupPlan([], reviewed.Omissions).AcceptedBytes == 0,
            "El resumen no corresponde exclusivamente a las acciones aceptadas.");
        var invalid = new[]
        {
            new UiCleanupPlan([item, item], []), new UiCleanupPlan([item], []),
            new UiCleanupPlan([item with { Id = "foreign" }], [new("two", "Dos", "Solapado")]),
            new UiCleanupPlan([item], [new("two", "Dos", "")]),
            new UiCleanupPlan([item with { CanSelect = false }], [new("two", "Dos", "Solapado")])
        };
        foreach (var plan in invalid) Expect<InvalidOperationException>(() => CleanupPlanReview.Validate(plan, ["one", "two"]));
    }
    private static void CancelHash()
    {
        var content = Enumerable.Range(0, 400_000).Select(x => (byte)x).ToArray();
        using var regular = new MemoryStream(content, writable: false);
        Require(CancellableHash.Compute(regular, CancellationToken.None) == Convert.ToHexString(SHA256.HashData(content)), "El hash difiere de SHA-256.");
        using var cancellation = new CancellationTokenSource();
        using var cancelled = new CancelOnRead(content, cancellation);
        Expect<OperationCanceledException>(() => CancellableHash.Compute(cancelled, cancellation.Token));
        Require(cancelled.Position < cancelled.Length && cancelled.ReadCalls == 1, "Continuó leyendo tras solicitar cancelar.");
        Require(content.SequenceEqual(cancelled.ToArray()), "El hash modificó el contenido.");
    }
    private sealed class CancelOnRead(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes, writable: false)
    {
        public int ReadCalls { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            var result = base.Read(buffer, offset, count);
            cancellation.Cancel(); return result;
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Se esperaba " + typeof(T).Name);
    }
}
