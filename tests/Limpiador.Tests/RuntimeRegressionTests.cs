using Limpiador.Core;
using Limpiador.Runtime;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class RuntimeRegressionTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return ("Un historial dañado no impide ver las operaciones recuperables", CorruptHistoryAsync);
        yield return ("El historial rechaza identidades ajenas y campos nulos sin reemplazar datos válidos", InvalidHistoryAsync);
        yield return ("Escrituras concurrentes del historial dejan un JSON completo sin temporales", ConcurrentHistoryAsync);
        yield return ("Un destino de historial no reemplazable informa el error y conserva sus datos", UnwritableHistoryAsync);
        yield return ("La salida de mantenimiento conserva inicio y final con memoria limitada", BoundedOutputAsync);
        yield return ("Una solicitud cancelada no inicia un comando de mantenimiento", CancelledCommandAsync);
    }

    private static Task CorruptHistoryAsync()
    {
        using var fixture = new Fixture();
        var store = new HistoryStore(Path.Combine(fixture.Root, "History"));
        var valid = new HistoryRecord { Title = "Operación recuperable", CompressedFiles = [new("file", "id", 12, DateTime.UtcNow)] };
        store.Save(valid);
        var damagedId = Guid.NewGuid().ToString("N");
        var path = Path.Combine(store.DirectoryPath, damagedId + ".json");
        File.WriteAllText(path, "{truncated");
        var listed = store.List();
        Require(listed.Count == 2 && listed.Single(x => x.Id == valid.Id).CanRestore, "Se ocultó una operación válida.");
        var damaged = listed.Single(x => x.Id == damagedId);
        Require(!damaged.CanRestore && damaged.Details.Count > 0, "Se ofreció restaurar un historial dañado.");
        Require(File.ReadAllText(path) == "{truncated", "La lectura sobrescribió el historial dañado.");
        return Task.CompletedTask;
    }

    private static Task InvalidHistoryAsync()
    {
        using var fixture = new Fixture();
        var store = new HistoryStore(Path.Combine(fixture.Root, "History"));
        var record = new HistoryRecord { Title = "Conservar" };
        store.Save(record);
        var path = Path.Combine(store.DirectoryPath, record.Id + ".json");
        var original = File.ReadAllText(path);
        ExpectInvalid(() => store.Save(record with { Details = null! }));
        Require(File.ReadAllText(path) == original, "Un registro inválido reemplazó el historial original.");
        File.WriteAllText(path, JsonSerializer.Serialize(record with { Id = Guid.NewGuid().ToString("N") }));
        ExpectInvalid(() => store.Read(record.Id));
        var incomplete = JsonNode.Parse(original)!.AsObject();
        incomplete[nameof(HistoryRecord.CompressedFiles)] = null;
        File.WriteAllText(path, incomplete.ToJsonString());
        ExpectInvalid(() => store.Read(record.Id));
        Require(store.List().Single().CanRestore == false, "Campos nulos habilitaron restauración.");
        return Task.CompletedTask;
    }

    private static async Task ConcurrentHistoryAsync()
    {
        using var fixture = new Fixture();
        var store = new HistoryStore(Path.Combine(fixture.Root, "History"));
        var record = new HistoryRecord();
        HistoryRecord Version(int index) => record with
        { Status = "Operación " + index, Details = [new string((char)('a' + index), 4096)] };
        void CheckComplete(HistoryRecord restored)
        {
            var index = int.Parse(restored.Status.Split(' ')[1]);
            Require(index >= 0 && index < 16 && restored.Id == record.Id &&
                restored.Details.Single() == new string((char)('a' + index), 4096), "El JSON mezcla escrituras distintas.");
        }
        store.Save(Version(0));
        using var start = new ManualResetEventSlim();
        var writers = Task.WhenAll(Enumerable.Range(0, 16).Select(index => Task.Run(() =>
        {
            var writer = index % 2 == 0 ? store : new HistoryStore(store.DirectoryPath);
            start.Wait();
            for (var iteration = 0; iteration < 4; iteration++) writer.Save(Version(index));
        })));
        var reader = Task.Run(() =>
        {
            var readingStore = new HistoryStore(store.DirectoryPath);
            start.Wait();
            do { CheckComplete(readingStore.Read(record.Id)); Thread.Yield(); }
            while (!writers.IsCompleted);
        });
        start.Set();
        await Task.WhenAll(writers, reader);
        CheckComplete(store.Read(record.Id));
        Require(Directory.GetFiles(store.DirectoryPath).Length == 1, "Quedaron temporales después de guardar.");
    }

    private static Task UnwritableHistoryAsync()
    {
        using var fixture = new Fixture();
        var store = new HistoryStore(Path.Combine(fixture.Root, "History"));
        var record = new HistoryRecord { Title = "No reemplazar el destino" };
        var destination = Path.Combine(store.DirectoryPath, record.Id + ".json");
        Directory.CreateDirectory(destination);
        var existing = Path.Combine(destination, "conservar.txt");
        File.WriteAllText(existing, "contenido anterior");
        var rejected = false;
        try { store.Save(record); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { rejected = true; }
        Require(rejected, "Un error permanente de sustitución se ocultó como un guardado correcto.");
        Require(Directory.Exists(destination) && File.ReadAllText(existing) == "contenido anterior", "El destino existente fue alterado.");
        Require(Directory.GetFiles(store.DirectoryPath).Length == 0, "Un guardado rechazado dejó temporales.");
        return Task.CompletedTask;
    }

    private static async Task BoundedOutputAsync()
    {
        const int limit = 64;
        var text = "INICIO" + new string('x', 1_000_000) + "FIN";
        using var reader = new StringReader(text);
        var output = await BoundedProcessOutput.ReadAsync(reader, limit);
        Require(output.StartsWith(text[..32], StringComparison.Ordinal) && output.EndsWith(text[^32..], StringComparison.Ordinal),
            "Se perdieron el contexto inicial o los últimos mensajes del proceso.");
        Require(output.Contains("omitidos") && output.Length < 200 && reader.Peek() == -1, "No se drenó toda la salida con límite de memoria.");
        foreach (var length in new[] { 0, 1, 31, 32, 63, 64 })
        {
            var shortText = new string('a', length);
            Require(await BoundedProcessOutput.ReadAsync(new StringReader(shortText), limit) == shortText,
                "Se truncó una salida que cabía en el límite.");
        }
    }

    private static async Task CancelledCommandAsync()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await OfficialCommandRunner.RunAsync(new(OfficialCommandKind.GitGarbageCollect, "git.exe", []), cancellation.Token);
        }
        catch (OperationCanceledException) { return; }
        throw new Exception("Una cancelación previa no fue respetada.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void ExpectInvalid(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("Se aceptó un historial no válido.");
    }
}
