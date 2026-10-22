using System.Text.Json;
using System.Text.Json.Serialization;
using Limpiador.Core;
using Limpiador.Runtime;

if (args.Contains("--help") || args.Contains("--ayuda") || args.Contains("-h"))
{
    Console.WriteLine("""
        Limpiador Ultra Windows — diagnóstico de solo lectura
        Uso: LimpiadorUltraWindows.Diagnostico.exe --diagnostico [opciones]
          --json          Resultado JSON en stdout (progreso en stderr)
          --motivos       Mostrar las razones y consecuencias
          --rutas         Mostrar todas las rutas
          --maximo        Mostrar el plan máximo, sin ejecutarlo
          --dias N        Antigüedad: 7, 30, 90, 180 o 365 (sin preferencias: 90)
          --carpeta RUTA   Añadir una carpeta al análisis del usuario
          --excluir RUTA   Excluir una carpeta (se puede repetir)
        Se respetan las preferencias guardadas de la aplicación, incluidas sus exclusiones.
        Este ejecutable nunca limpia, cierra aplicaciones ni cambia archivos.
        """);
    return 0;
}
if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
{
    Console.Error.WriteLine("Limpiador Ultra Windows solo se ejecuta en Windows. Consulta --ayuda.");
    return 2;
}
try
{
    var saved = new AppPreferencesStore(Path.Combine(WindowsHost.DataDirectory, "preferences.json")).Load();
    var settings = DiagnosticArguments.Parse(args, saved);
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    var metadata = new WindowsMetadataProvider();
    var options = WindowsHost.CreateOptions(settings.MinimumAgeDays, settings.AdditionalFolder, metadata, settings.ExcludedFolders);
    string? phase = null;
    var progress = new Progress<ScanProgress>(p =>
    {
        if (p.Phase == phase) return;
        phase = p.Phase; Console.Error.WriteLine($"{p.Phase} · {p.FilesVisited:N0} archivos");
    });
    var result = await new Scanner().ScanAsync(options, progress, cancellation.Token);
    result = await AdvancedWindowsAnalysis.EnrichAsync(result, options, metadata, cancellation.Token);
    var plan = args.Contains("--maximo") ? CleanupPlan.Create(result.Candidates, true) : null;
    if (args.Contains("--json"))
    {
        var json = new JsonSerializerOptions { WriteIndented = true };
        json.Converters.Add(new JsonStringEnumConverter());
        Console.WriteLine(JsonSerializer.Serialize(new { ScanOptions = settings, Scan = result, MaximumPlan = plan }, json));
    }
    else
    {
        Console.WriteLine($"{result.Stats.Files:N0} archivos; {result.Stats.SkippedDirectories:N0} carpetas omitidas; {result.Stats.Duration.TotalSeconds:N1} segundos.");
        foreach (var item in result.Candidates)
        {
            Console.WriteLine($"[{(item.Recommended ? "x" : " ")}] {item.Risk,-8} {item.Bytes,15:N0} B | {item.Category} | {item.Title}{(item.IsBlocked ? " [BLOQUEADO]" : "")}");
            if (args.Contains("--rutas")) foreach (var path in item.Paths) Console.WriteLine("    " + path);
            if (args.Contains("--motivos"))
            {
                foreach (var reason in item.Reasons) Console.WriteLine("    " + reason);
                Console.WriteLine("    " + item.Consequence);
            }
        }
        if (plan is not null)
        {
            Console.WriteLine($"Plan máximo (simulación): {plan.Items.Count} elementos, {plan.EstimatedBytes:N0} bytes estimados; {plan.Excluded.Count} excluidos.");
            foreach (var item in plan.Items) Console.WriteLine($"    {item.Title}: {item.Consequence}");
        }
        foreach (var warning in result.Warnings) Console.WriteLine("Aviso: " + warning);
    }
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Análisis cancelado. No se modificó ningún archivo."); return 130; }
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
