using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Limpiador.Windows;
public enum KnownFolder { Profile, LocalAppData, RoamingAppData, Downloads, Documents, Desktop, Pictures, Music, Videos, SavedGames }

[SupportedOSPlatform("windows10.0.17763")]
public static class KnownFolders
{
    public static string Get(KnownFolder folder)
    {
        WindowsFileSystem.EnsureWindows();
        var id = new Guid(folder switch
        {
            KnownFolder.Profile => "5E6C858F-0E22-4760-9AFE-EA3317B67173",
            KnownFolder.LocalAppData => "F1B32785-6FBA-4FCF-9D55-7B8E7F157091",
            KnownFolder.RoamingAppData => "3EB685DB-65F9-4CF6-A03A-E3EF65729F3D",
            KnownFolder.Downloads => "374DE290-123F-4565-9164-39C4925E467B",
            KnownFolder.Documents => "FDD39AD0-238F-46AF-ADB4-6C85480369C7",
            KnownFolder.Desktop => "B4BFCC3A-DB2C-424C-B029-7FE99A87C641",
            KnownFolder.Pictures => "33E28130-4E1E-4676-835A-98395C3BC3BB",
            KnownFolder.Music => "4BD8D571-6D19-48D3-BE97-422220080E43",
            KnownFolder.Videos => "18989B1D-99B5-455B-841C-AB7C74E4DDFC",
            KnownFolder.SavedGames => "4C5C32FF-BB9D-43B0-BF9F-7DBCC7DD21AC",
            _ => throw new ArgumentOutOfRangeException(nameof(folder))
        });
        var hr = SHGetKnownFolderPath(ref id, 0x4000, 0, out var pointer); // KF_FLAG_DONT_VERIFY; don't create directories.
        Marshal.ThrowExceptionForHR(hr);
        try { return Marshal.PtrToStringUni(pointer) ?? throw new IOException("Windows no devolvió la carpeta conocida."); }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }
    public static IReadOnlyList<string> CloudRoots()
    {
        WindowsFileSystem.EnsureWindows(); var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }) Add(Environment.GetEnvironmentVariable(key));
        using var accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
        if (accounts is not null)
            foreach (var account in accounts.GetSubKeyNames())
            {
                using var entry = accounts.OpenSubKey(account); Add(entry?.GetValue("UserFolder") as string);
            }
        return paths.ToArray();
        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value) && Directory.Exists(value))
                paths.Add(WindowsFileSystem.NormalizeLocalPath(value));
        }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, nint token, out nint path);
}
