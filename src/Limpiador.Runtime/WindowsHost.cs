using System.Diagnostics;
using System.Runtime.Versioning;
using Limpiador.Core;
using Limpiador.Windows;

namespace Limpiador.Runtime;

[SupportedOSPlatform("windows10.0.17763")]
public static class WindowsHost
{
    public static ScanOptions CreateOptions(int unusedDays = 90, string? additionalFolder = null,
        WindowsMetadataProvider? metadata = null, IReadOnlyList<string>? excludedFolders = null)
    {
        WindowsFileSystem.EnsureWindows();
        if (unusedDays is not (7 or 30 or 90 or 180 or 365)) throw new ArgumentOutOfRangeException(nameof(unusedDays));
        var personal = new[] { KnownFolder.Downloads, KnownFolder.Desktop, KnownFolder.Documents,
            KnownFolder.Pictures, KnownFolder.Videos, KnownFolder.Music, KnownFolder.SavedGames }
            .Select(KnownFolders.Get).Where(Directory.Exists).ToList();
        if (!string.IsNullOrWhiteSpace(additionalFolder))
        {
            var path = WindowsFileSystem.NormalizeLocalPath(additionalFolder);
            if (!Directory.Exists(path) || Path.GetPathRoot(path) == path) throw new ArgumentException("Elige una carpeta existente, no una unidad completa.");
            var metadataRoot = WindowsFileSystem.GetMetadata(path);
            if (metadataRoot.IsReparsePoint) throw new ArgumentException("La carpeta adicional no puede ser un enlace ni una junction.");
            personal.Add(path);
        }
        return new()
        {
            ProfilePath = KnownFolders.Get(KnownFolder.Profile),
            LocalAppDataPath = KnownFolders.Get(KnownFolder.LocalAppData),
            RoamingAppDataPath = KnownFolders.Get(KnownFolder.RoamingAppData),
            TempPath = Path.GetTempPath(), PersonalRoots = personal,
            AdditionalCloudRoots = KnownFolders.CloudRoots(), MetadataProvider = metadata ?? new WindowsMetadataProvider(),
            ExcludedRoots = (excludedFolders ?? []).Select(WindowsFileSystem.NormalizeLocalPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            UnusedAge = TimeSpan.FromDays(unusedDays), AreProcessesRunning = AreProcessesRunning
        };
    }

    public static bool? AreProcessesRunning(IReadOnlyList<string> names)
    {
        if (names.Count == 0) return false;
        Process[] processes = [];
        try
        {
            var wanted = names.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
            processes = Process.GetProcesses();
            foreach (var process in processes)
            {
                try { if (wanted.Contains(process.ProcessName)) return true; }
                catch (InvalidOperationException) { /* Process already exited. */ }
                catch (System.ComponentModel.Win32Exception) { return null; }
            }
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public static IReadOnlyList<DiskSpace> Disks()
    {
        WindowsFileSystem.EnsureWindows(); var result = new List<DiskSpace>();
        foreach (var drive in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed && x.IsReady))
            try { result.Add(WindowsFileSystem.GetDiskSpace(drive.RootDirectory.FullName)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result;
    }

    public static string DataDirectory => Path.Combine(KnownFolders.Get(KnownFolder.LocalAppData), "LimpiadorUltraWindows");

    public static void Reveal(string path)
    {
        path = WindowsFileSystem.NormalizeLocalPath(path);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
            { UseShellExecute = false };
        if (File.Exists(path)) { start.ArgumentList.Add("/select,"); start.ArgumentList.Add(path); }
        else if (Directory.Exists(path)) start.ArgumentList.Add(path);
        else throw new FileNotFoundException("La ruta ya no existe.", path);
        Process.Start(start)?.Dispose();
    }
}
