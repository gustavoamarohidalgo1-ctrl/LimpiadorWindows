using System.Diagnostics;
using Limpiador.Core;

namespace Limpiador.Runtime;

public static class OfficialCommandRunner
{
    /// <summary>The command line is rebuilt from a closed allowlist; UI/scanner text is never shell code.</summary>
    public static async Task<string> RunAsync(OfficialCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Solo Windows.");
        var (fileName, arguments) = command.Kind switch
        {
            OfficialCommandKind.DockerBuildCachePrune => ("docker.exe", new[] { "builder", "prune", "--force", "--filter", "until=168h" }),
            OfficialCommandKind.DockerDanglingImagesPrune => ("docker.exe", new[] { "image", "prune", "--force" }),
            OfficialCommandKind.GitGarbageCollect => ("git.exe", new[] { "-c", "core.hooksPath=NUL", "-c", "core.fsmonitor=false", "-c", "gc.reflogExpire=never", "-c", "gc.reflogExpireUnreachable=never", "gc", "--no-prune" }),
            OfficialCommandKind.NuGetHttpCacheClear => ("dotnet.exe", new[] { "nuget", "locals", "http-cache", "--clear" }),
            OfficialCommandKind.PipCachePurge => ("pip.exe", new[] { "cache", "purge" }),
            _ => throw new NotSupportedException("Este comando no tiene un ejecutable nativo autorizado; utiliza su candidato de archivos.")
        };
        var executable = ResolveExecutable(fileName) ?? throw new FileNotFoundException($"No se encontró {fileName} en el PATH.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = command.WorkingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException("No se pudo iniciar el comando.");
        var outputTask = BoundedProcessOutput.ReadAsync(process.StandardOutput);
        var errorTask = BoundedProcessOutput.ReadAsync(process.StandardError);
        // A stop request takes effect between operations. Do not kill a running maintenance transaction.
        await process.WaitForExitAsync(CancellationToken.None);
        var output = await outputTask; var error = await errorTask;
        if (process.ExitCode != 0) throw new IOException($"{fileName} terminó con código {process.ExitCode}: {error.Trim()}");
        return (output + Environment.NewLine + error).Trim();
    }

    public static string? ResolveExecutable(string name)
    {
        if (Path.GetFileName(name) != name || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var clean = directory.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(clean) || !Path.IsPathFullyQualified(clean) || clean.StartsWith(@"\\")) continue;
            try
            {
                var candidate = Path.Combine(clean, name);
                if (File.Exists(candidate) && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) == 0) return candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { /* An inaccessible or stale PATH entry must not hide subsequent installations. */ }
        }
        return null;
    }
}
