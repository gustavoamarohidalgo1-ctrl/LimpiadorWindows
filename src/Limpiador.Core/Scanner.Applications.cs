using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Limpiador.Core;

public sealed partial class Scanner
{
    private static readonly HashSet<string> BrowserCaches = new(StringComparer.OrdinalIgnoreCase)
    { "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "ShaderCache", "GrShaderCache", "GraphiteDawnCache", "GPUPersistentCache", "CacheStorage", "ScriptCache" };

    private static void ScanApplications(ScanContext c)
    {
        var browserRoots = new (string Path, string Name, string Process)[]
        {
            (c.L("Google", "Chrome", "User Data"), "Google Chrome", "chrome"),
            (c.L("Google", "Chrome Beta", "User Data"), "Chrome Beta", "chrome"),
            (c.L("Google", "Chrome SxS", "User Data"), "Chrome Canary", "chrome"),
            (c.L("Microsoft", "Edge", "User Data"), "Microsoft Edge", "msedge"),
            (c.L("Microsoft", "Edge Beta", "User Data"), "Edge Beta", "msedge"),
            (c.L("Microsoft", "Edge Dev", "User Data"), "Edge Dev", "msedge"),
            (c.L("Microsoft", "Edge SxS", "User Data"), "Edge Canary", "msedge"),
            (c.L("BraveSoftware", "Brave-Browser", "User Data"), "Brave", "brave"),
            (c.L("BraveSoftware", "Brave-Browser-Beta", "User Data"), "Brave Beta", "brave"),
            (c.L("Chromium", "User Data"), "Chromium", "chrome"),
            (c.L("Vivaldi", "User Data"), "Vivaldi", "vivaldi"),
            (c.R("Opera Software", "Opera Stable"), "Opera", "opera"),
            (c.R("Opera Software", "Opera GX Stable"), "Opera GX", "opera"),
            (c.L("Packages", "TheBrowserCompany.Arc_ttt1ap7aakyb4", "LocalCache", "Local", "Arc", "User Data"), "Arc", "Arc")
        };
        foreach (var browser in browserRoots)
        {
            if (c.Index.Get(browser.Path) is null) continue;
            var profiles = c.Children(browser.Path).Where(x => x.Snapshot.IsDirectory &&
                (Path.GetFileName(x.Path) is "Default" or "Guest Profile" or "System Profile" || Regex.IsMatch(Path.GetFileName(x.Path), @"^Profile \d+$"))).Select(x => x.Path).ToList();
            if (c.Has(browser.Path, "Preferences")) profiles.Add(browser.Path);
            foreach (var profile in profiles) ScanChromiumProfile(c, profile, browser.Name, browser.Process);
            foreach (var component in new[] { "WidevineCdm", "OptimizationHints", "hyphen-data", "ZxcvbnData", "screen_ai", "WasmTtsEngine", "OnDeviceHeadSuggestModel", "CertificateRevocation", "PKIMetadata" })
            {
                var versions = c.Children(Path.Combine(browser.Path, component)).Where(x => x.Snapshot.IsDirectory && Regex.IsMatch(Path.GetFileName(x.Path), @"^\d+(\.\d+)+$"))
                    .OrderByDescending(x => NumericVersion(Path.GetFileName(x.Path))).ToArray();
                foreach (var old in versions.Skip(1)) c.Add(old.Path, $"{browser.Name}: versión anterior de {component}", CleanupCategory.ApplicationCaches,
                    Risk.Safe, recommend: true, consequence: "Se conserva la versión más reciente del componente.", processes: Names(browser.Process));
            }
            var headless = browser.Path + "-headless";
            c.Add(headless, $"Perfil automatizado sobrante de {browser.Name}", CleanupCategory.ApplicationCaches, Risk.Review,
                consequence: "Se pierde el estado guardado de ese perfil de automatización.", processes: Names(browser.Process, "node", "python"));
        }
        foreach (var app in c.Children(c.Roaming).Where(x => x.Snapshot.IsDirectory && c.Has(x.Path, "Local State")))
        {
            if (browserRoots.Any(x => SafetyPolicy.IsWithin(app.Path, x.Path) || SafetyPolicy.IsWithin(x.Path, app.Path))) continue;
            var appName = Path.GetFileName(app.Path);
            ScanChromiumProfile(c, app.Path, appName, appName);
            foreach (var partition in c.Children(Path.Combine(app.Path, "Partitions"))) ScanChromiumProfile(c, partition.Path, appName, appName);
        }
        foreach (var product in new[] { "Firefox", "Thunderbird" })
        foreach (var profile in c.Children(c.L("Mozilla", product, "Profiles")).Concat(c.Children(c.L(product, "Profiles"))))
            foreach (var cache in new[] { "cache2", "startupCache", "shader-cache" })
                c.Add(Path.Combine(profile.Path, cache), $"{product}: {cache}", CleanupCategory.ApplicationCaches, Risk.Safe, recommend: true,
                    consequence: "El navegador o cliente de correo reconstruye esta caché. No se borra su correo, historial ni contraseñas.", processes: Names(product));
        foreach (var app in new[] { "Code", "Cursor", "Discord", "Slack", "Teams", "Zoom", "ChatGPT" })
        foreach (var root in new[] { c.R(app), c.L(app) })
        {
            foreach (var cache in new[] { "Cache", "Code Cache", "GPUCache", "CachedData", "CachedExtensionVSIXs", "CachedProfilesData" })
                c.Add(Path.Combine(root, cache), $"Caché de {app}: {cache}", CleanupCategory.ApplicationCaches, Risk.Safe, recommend: true,
                    consequence: "La aplicación vuelve a generar o descargar su caché; sus conversaciones y documentos se conservan.", processes: Names(app, app == "Teams" ? "ms-teams" : app));
            var versions = c.Children(root).Where(x => x.Snapshot.IsDirectory && Path.GetFileName(x.Path).StartsWith("app-", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => NumericVersion(Path.GetFileName(x.Path))).ToArray();
            foreach (var old in versions.Skip(1)) c.Add(old.Path, $"Versión anterior de {app}", CleanupCategory.ApplicationCaches, Risk.Review,
                consequence: "Comprueba que el acceso directo y el actualizador usan otra versión.", processes: Names(app, "Update"));
        }
        foreach (var cache in new[]
        {
            (c.L("Microsoft", "Office", "16.0", "Wef"), "complementos de Office", Names("WINWORD", "EXCEL", "POWERPNT", "OUTLOOK")),
            (c.L("Adobe", "Common", "Media Cache"), "medios de Adobe", Names("Adobe Premiere Pro", "AfterFX")),
            (c.L("Adobe", "Common", "Media Cache Files"), "medios derivados de Adobe", Names("Adobe Premiere Pro", "AfterFX")),
            (c.R("Adobe", "Common", "Media Cache"), "medios de Adobe", Names("Adobe Premiere Pro", "AfterFX")),
            (c.L("NVIDIA", "DXCache"), "sombreadores NVIDIA DirectX", Array.Empty<string>()),
            (c.L("NVIDIA", "GLCache"), "sombreadores NVIDIA OpenGL", Array.Empty<string>()),
            (c.L("AMD", "DxCache"), "sombreadores AMD", Array.Empty<string>())
        }) c.Add(cache.Item1, "Caché de " + cache.Item2, CleanupCategory.ApplicationCaches, Risk.Safe, recommend: true,
            consequence: "Se vuelve a generar cuando la aplicación la necesita; el primer inicio puede tardar más.", processes: cache.Item3);
        // Packaged applications expose dedicated TempState; LocalState can hold the only copy of user data.
        foreach (var package in c.Children(c.L("Packages")))
        {
            c.Add(Path.Combine(package.Path, "TempState"), "Temporales de aplicación: " + Path.GetFileName(package.Path), CleanupCategory.ApplicationCaches,
                Risk.Review, consequence: "Revisa que la aplicación esté cerrada. Nunca se retira LocalState ni la carpeta completa del paquete.");
            foreach (var cache in c.Children(Path.Combine(package.Path, "LocalCache")).Where(x => BrowserCaches.Contains(Path.GetFileName(x.Path))))
                c.Add(cache.Path, "Caché de aplicación empaquetada", CleanupCategory.ApplicationCaches, Risk.Review,
                    consequence: "Se detectó una carpeta de caché; verifica la aplicación que la administra.");
        }
        foreach (var entry in c.Entries().Where(x => x.Snapshot.IsDirectory))
        {
            var name = Path.GetFileName(entry.Path);
            if (name.EndsWith(" Previews.lrdata", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(" Smart Previews.lrdata", StringComparison.OrdinalIgnoreCase))
                c.Add(entry.Path, "Vistas previas de Lightroom", CleanupCategory.ApplicationCaches, Risk.Safe, recommend: true,
                    consequence: "Lightroom vuelve a generar las vistas previas desde las fotos originales.", processes: Names("Lightroom"), allowPersonal: true);
            else if (name.EndsWith(" Smart Previews.lrdata", StringComparison.OrdinalIgnoreCase))
                c.Add(entry.Path, "Vistas previas inteligentes de Lightroom", CleanupCategory.ApplicationCaches, Risk.Review,
                    consequence: "Puedes perder la posibilidad de trabajar sin las fotos originales conectadas.", processes: Names("Lightroom"), allowPersonal: true);
            else if (name.Equals("CacheClip", StringComparison.OrdinalIgnoreCase) && c.IsPersonal(entry.Path))
                c.Add(entry.Path, "Caché de reproducción de DaVinci Resolve", CleanupCategory.ApplicationCaches, Risk.Review,
                    consequence: "Verifica en Resolve que esta sea su caché; no se deben retirar los clips originales.", processes: Names("Resolve"), allowPersonal: true);
            else if (name.Equals("shadercache", StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetFileName(Path.GetDirectoryName(entry.Path)), "steamapps", StringComparison.OrdinalIgnoreCase))
                c.Add(entry.Path, "Sombreadores descargados de Steam", CleanupCategory.ApplicationCaches, Risk.Safe, maximum: true,
                    consequence: "Steam vuelve a descargar o compilar los sombreadores al abrir los juegos.", processes: Names("steam", "steamwebhelper"), allowPersonal: true);
        }
    }

    private static void ScanChromiumProfile(ScanContext c, string profile, string app, string process)
    {
        foreach (var cache in BrowserCaches)
        {
            var path = cache is "CacheStorage" or "ScriptCache" ? Path.Combine(profile, "Service Worker", cache) : Path.Combine(profile, cache);
            c.Add(path, $"{app}: {cache} ({Path.GetFileName(profile)})", CleanupCategory.ApplicationCaches, Risk.Safe, recommend: true,
                consequence: "Se vuelve a descargar el contenido y a compilar el código de los sitios; algunas páginas tardarán más en abrir.", processes: Names(process));
        }
        foreach (var database in c.Children(Path.Combine(profile, "IndexedDB")))
            if (c.IsOld(database, TimeSpan.FromDays(60))) c.Add(database.Path, $"{app}: datos antiguos de un sitio", CleanupCategory.ApplicationCaches, Risk.Review,
                consequence: "Puede contener trabajo sin sincronizar y datos sin conexión de una web. Revisa el sitio antes de retirarlo.",
                processes: Names(process), allowPersonal: true, minimumAge: TimeSpan.FromDays(60));
    }

    private static void ScanTemporary(ScanContext c)
    {
        foreach (var entry in c.Children(c.Policy.TempPath))
        {
            var name = Path.GetFileName(entry.Path);
            var known = Regex.IsMatch(name, @"^(npm-|pip-|uv-|gradle-|kotlin-|node-compile-cache|scoped_dir|chrome_BITS_|rustc|dotnet-|MSBuildTemp|NuGetScratch)", RegexOptions.IgnoreCase) ||
                (!entry.Snapshot.IsDirectory && new[] { ".tmp", ".temp" }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase));
            c.Add(entry.Path, "Temporal de usuario: " + name, CleanupCategory.SystemTemporary, known ? Risk.Safe : Risk.Review,
                recommend: known, consequence: known ? "La herramienta vuelve a generar sus temporales. Si contiene resultados propios, se exige revisión."
                    : "Una carpeta temporal puede guardar resultados únicos. Revisa sus archivos antes de retirarla.",
                processes: KnownProcesses(name), reason: "Solo se analiza la carpeta temporal autorizada del usuario.");
        }
    }

    private static string[] KnownProcesses(string name)
    {
        if (name.StartsWith("npm", StringComparison.OrdinalIgnoreCase) || name.StartsWith("node", StringComparison.OrdinalIgnoreCase)) return Names("node", "npm");
        if (name.StartsWith("pip", StringComparison.OrdinalIgnoreCase) || name.StartsWith("uv", StringComparison.OrdinalIgnoreCase)) return Names("python", "pythonw", "pip", "uv");
        if (name.Contains("gradle", StringComparison.OrdinalIgnoreCase) || name.Contains("kotlin", StringComparison.OrdinalIgnoreCase)) return Names("java", "gradle");
        if (name.Contains("chrome", StringComparison.OrdinalIgnoreCase) || name.StartsWith("scoped_dir", StringComparison.OrdinalIgnoreCase)) return Names("chrome", "msedge", "brave");
        if (name.Contains("MSBuild", StringComparison.OrdinalIgnoreCase) || name.Contains("NuGet", StringComparison.OrdinalIgnoreCase) || name.StartsWith("dotnet", StringComparison.OrdinalIgnoreCase)) return Names("dotnet", "MSBuild", "devenv");
        return [];
    }

    private static void ScanLeftovers(ScanContext c)
    {
        var installed = OperatingSystem.IsWindows() ? InstalledNames() : null;
        if (installed is null) { c.Warnings.Add("No se pudo obtener el inventario de aplicaciones instaladas: los posibles restos no se presuponen huérfanos."); return; }
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Microsoft", "Packages", "Programs", "Temp", "Application Data", "ConnectedDevicesPlatform", "Publishers", "Comms", "CrashDumps", "D3DSCache", "Google", "Mozilla", "NVIDIA", "AMD", "Adobe", "JetBrains" };
        foreach (var root in new[] { c.Local, c.Roaming })
        foreach (var entry in c.Children(root).Where(x => x.Snapshot.IsDirectory && !c.Covered(x.Path)))
        {
            var name = Path.GetFileName(entry.Path);
            if (excluded.Contains(name) || name.StartsWith('.') || !c.IsOld(entry, TimeSpan.FromDays(90))) continue;
            var simple = NormalizeAppName(name);
            if (simple.Length < 4 || installed.Any(x => x.Contains(simple, StringComparison.OrdinalIgnoreCase) || simple.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;
            if (c.Index.Snapshots(entry.Path, c.Token).Any(x => !x.IsDirectory && Path.GetExtension(x.Path).Equals(".exe", StringComparison.OrdinalIgnoreCase))) continue;
            c.Add(entry.Path, "Datos sin aplicación identificada: " + name, CleanupCategory.ApplicationLeftovers, Risk.Review,
                consequence: "Puede ser una app portátil o datos que quieres conservar para reinstalarla. Se pierde su historial y configuración si decides retirarlos.",
                reason: "No se encontró una coincidencia clara en el inventario de desinstalación; esto no demuestra que sea basura.", minimumAge: TimeSpan.FromDays(90));
        }
    }
    private static string NormalizeAppName(string text) => Regex.Replace(text, "[^a-z0-9]", "", RegexOptions.IgnoreCase).ToLowerInvariant();

    [SupportedOSPlatform("windows")]
    private static HashSet<string>? InstalledNames()
    {
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", false);
                if (uninstall is null) continue;
                foreach (var keyName in uninstall.GetSubKeyNames())
                {
                    using var app = uninstall.OpenSubKey(keyName, false);
                    if (app?.GetValue("DisplayName") is string name && NormalizeAppName(name).Length >= 4) names.Add(NormalizeAppName(name));
                }
            }
            return names;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return null; }
    }
}
