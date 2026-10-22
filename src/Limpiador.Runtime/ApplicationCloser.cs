using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace Limpiador.Runtime;

/// <summary>Requests a normal window close. Never terminates processes or performs cleanup.</summary>
[SupportedOSPlatform("windows10.0.17763")]
public static class ApplicationCloser
{
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "system", "winlogon", "shellhost", "applicationframehost", "taskmgr",
        "csrss", "lsass", "services", "smss", "wininit", "dwm", "sihost", "fontdrvhost",
        "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp", "RuntimeBroker"
    };

    public static async Task<OperationReport> CloseAsync(IReadOnlyList<string> processNames,
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("El cierre de aplicaciones solo está disponible en Windows.");
        var details = new List<string>();
        var requested = new List<(Process Process, string Name)>();
        var seenIds = new HashSet<int>();
        using var currentProcess = Process.GetCurrentProcess();
        var sessionId = currentProcess.SessionId;
        try
        {
            // Process discovery and normal window messages must not block the WPF dispatcher.
            await Task.Run(() =>
            {
                foreach (var rawName in processNames.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    var name = rawName.Trim();
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                    if (name.Length == 0 || name.IndexOfAny(['/', '\\', ':', '*', '?']) >= 0 || ProtectedNames.Contains(name))
                    {
                        details.Add($"No se solicita el cierre de un proceso protegido o no válido: {rawName}.");
                        continue;
                    }
                    Process[] processes;
                    try { processes = Process.GetProcessesByName(name); }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                    { details.Add($"No se pudo comprobar {name}: {ex.Message}"); continue; }
                    foreach (var process in processes)
                    {
                        var retained = false;
                        try
                        {
                            if (cancellationToken.IsCancellationRequested) continue;
                            if (process.Id == Environment.ProcessId || process.SessionId != sessionId || !seenIds.Add(process.Id) || ProtectedNames.Contains(process.ProcessName)) continue;
                            if (process.HasExited) continue;
                            if (process.MainWindowHandle == IntPtr.Zero)
                            { details.Add($"{name} (PID {process.Id}) no tiene una ventana principal. No se cierra."); continue; }
                            progress?.Report(new($"Solicitando el cierre de {name}…", 0, 0));
                            if (process.CloseMainWindow())
                            {
                                requested.Add((process, name)); retained = true;
                                details.Add($"Solicitud de cierre enviada a {name} (PID {process.Id}).");
                            }
                            else details.Add($"{name} no aceptó la solicitud de cierre. Cierra sus ventanas manualmente.");
                        }
                        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                        { details.Add($"No se pudo solicitar el cierre de {name}: {ex.Message}"); }
                        finally { if (!retained) process.Dispose(); }
                    }
                }
            }, CancellationToken.None);
            var timer = Stopwatch.StartNew();
            while (!cancellationToken.IsCancellationRequested && timer.Elapsed < TimeSpan.FromSeconds(20) && requested.Any(x => !HasExited(x.Process)))
            {
                var closed = requested.Count(x => HasExited(x.Process));
                progress?.Report(new("Esperando a las aplicaciones. Revisa si alguna pide guardar cambios…", closed, requested.Count));
                try { await Task.Delay(250, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            }
            var finished = 0;
            foreach (var (process, name) in requested)
            {
                if (HasExited(process)) { finished++; details.Add($"{name} se cerró."); }
                else details.Add($"{name} sigue abierto. No se ha forzado su cierre. Revisa sus ventanas antes de volver a analizar.");
            }
            if (cancellationToken.IsCancellationRequested) details.Add("Se detuvo la espera. Las solicitudes de cierre que ya se enviaron pueden seguir pendientes.");
            if (requested.Count == 0) details.Add("No había aplicaciones con una ventana principal a las que solicitar el cierre.");
            return new($"{finished} de {requested.Count} aplicaciones cerradas. Vuelve a analizar antes de limpiar.", details, [], 0);
        }
        finally { foreach (var (process, _) in requested) process.Dispose(); }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
        catch (Win32Exception) { return false; }
    }
}
