using System.Text.Json;
using System.Text.RegularExpressions;

namespace Limpiador.Core;

public sealed partial class Scanner
{
    private static void ScanEmulators(ScanContext c)
    {
        var avds = c.Entries().Where(x => x.Snapshot.IsDirectory && x.Path.EndsWith(".avd", StringComparison.OrdinalIgnoreCase)).ToArray();
        var referencedImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allConfigurationsRead = true;
        foreach (var configured in new[] { Environment.GetEnvironmentVariable("ANDROID_AVD_HOME"),
            Environment.GetEnvironmentVariable("ANDROID_USER_HOME") is { Length: > 0 } androidUser ? Path.Combine(androidUser, "avd") : null }.OfType<string>())
            if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured) && c.Index.Get(configured)?.Complete != true)
                allConfigurationsRead = false;
        foreach (var avd in avds)
        {
            var name = Path.GetFileNameWithoutExtension(avd.Path);
            var config = c.ReadText(Path.Combine(avd.Path, "config.ini"));
            if (config is null) allConfigurationsRead = false;
            else foreach (var line in config.Split('\n').Where(x => x.StartsWith("image.sysdir", StringComparison.Ordinal)))
                referencedImages.Add(line[(line.IndexOf('=') + 1)..].Trim().Replace('\\', '/').Trim('/'));
            c.Add(Path.Combine(avd.Path, "snapshots", "default_boot"), $"Arranque rápido de {name}", CleanupCategory.Emulators,
                Risk.Safe, recommend: true, consequence: "El emulador arrancará en frío y regenerará Quick Boot al cerrarlo.",
                processes: Names("emulator", "qemu-system-x86_64", "qemu-system-aarch64", "adb"), allowPersonal: true);
            foreach (var snapshot in c.Children(Path.Combine(avd.Path, "snapshots")).Where(x => Path.GetFileName(x.Path) != "default_boot"))
                c.Add(snapshot.Path, $"Snapshot guardado de {name}: {Path.GetFileName(snapshot.Path)}", CleanupCategory.Emulators,
                    Risk.Review, consequence: "Se pierde este estado guardado del emulador; no se vuelve a crear automáticamente.", processes: Names("emulator", "qemu-system-x86_64"), allowPersonal: true);
            foreach (var file in c.Children(avd.Path).Where(x => Path.GetFileName(x.Path).StartsWith("userdata-qemu", StringComparison.OrdinalIgnoreCase)))
                c.Add(file.Path, $"Datos de {name} (restablecer)", CleanupCategory.Emulators, Risk.Caution,
                    consequence: "El emulador pierde sus aplicaciones, cuentas y datos. Es una operación de restablecimiento.", processes: Names("emulator", "qemu-system-x86_64", "adb"), allowPersonal: true);
            c.Add(avd.Path, $"Emulador Android completo: {name}", CleanupCategory.Emulators, Risk.Review,
                consequence: "Se retira este dispositivo virtual con todos sus datos. Puedes crear otro, pero empezará vacío.",
                processes: Names("emulator", "qemu-system-x86_64", "qemu-system-aarch64", "adb"), allowPersonal: true, allowOverlap: true);
        }
        var sdkRoots = new[] { c.L("Android", "Sdk"), c.H("Android", "Sdk") }
            .Concat(new[] { Environment.GetEnvironmentVariable("ANDROID_HOME"), Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") }.OfType<string>())
            .Where(x => c.Policy.ApprovedRoots.Any(root => SafetyPolicy.IsWithin(x, root))).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var sdk in sdkRoots)
        {
            foreach (var api in c.Children(Path.Combine(sdk, "system-images")))
            foreach (var variant in c.Children(api.Path))
            foreach (var image in c.Children(variant.Path))
            {
                var relative = Path.GetRelativePath(sdk, image.Path).Replace('\\', '/');
                var used = referencedImages.Any(x => x.EndsWith(relative, StringComparison.OrdinalIgnoreCase));
                if (!used) c.Add(image.Path, $"Imagen Android sin emulador conocido: {Path.GetFileName(api.Path)}", CleanupCategory.Emulators,
                    allConfigurationsRead ? Risk.Safe : Risk.Review, maximum: allConfigurationsRead,
                    consequence: "Android Studio tendrá que descargar esta imagen si vuelves a crear un emulador que la necesite.",
                    processes: Names("emulator", "sdkmanager", "studio64", "java"), allowPersonal: true,
                    reason: allConfigurationsRead ? "Ningún AVD revisado hace referencia a esta imagen." : "No se pudieron interpretar todas las configuraciones AVD.");
            }
            foreach (var family in new[] { "build-tools", "ndk", "cmake", "platforms" })
            {
                var versions = c.Children(Path.Combine(sdk, family)).Where(x => x.Snapshot.IsDirectory).OrderByDescending(x => NumericVersion(Path.GetFileName(x.Path))).ToArray();
                foreach (var old in versions.Skip(1)) c.Add(old.Path, $"Android SDK {family}: {Path.GetFileName(old.Path)}", CleanupCategory.Emulators,
                    Risk.Review, consequence: "Un proyecto puede requerir esta versión exacta. Comprueba su configuración antes de retirarla.",
                    processes: Names("studio64", "java", "sdkmanager", "emulator"), reason: "Versión anterior; no se presupone que tus proyectos hayan dejado de utilizarla.");
            }
            foreach (var cache in new[] { ".temp", ".downloadIntermediates" })
                c.Add(Path.Combine(sdk, cache), "Descargas incompletas del SDK de Android", CleanupCategory.Emulators, Risk.Safe,
                    recommend: true, consequence: "El SDK volverá a descargar lo que falte.", processes: Names("sdkmanager", "studio64", "java"));
            if (avds.Length == 0 && !c.Children(Path.Combine(sdk, "system-images")).Any())
                c.Add(Path.Combine(sdk, "emulator"), "Programa del emulador sin dispositivos ni imágenes", CleanupCategory.Emulators,
                    Risk.Review, consequence: "Android Studio tendrá que instalar de nuevo el emulador si lo necesitas.", processes: Names("emulator", "studio64"));
        }
    }

    private static Version NumericVersion(string text)
    {
        var cleaned = Regex.Replace(text, @"^[^0-9]*", "").Split('-', '+')[0];
        return Version.TryParse(cleaned.Contains('.') ? cleaned : cleaned + ".0", out var version) ? version : new Version(0, 0);
    }

    private static void ScanDevelopment(ScanContext c)
    {
        var caches = new (string Path, string Name, string[] Processes, bool Maximum)[]
        {
            (c.L("npm-cache", "_cacache"), "npm", Names("node", "npm"), true),
            (c.R("npm-cache", "_cacache"), "npm", Names("node", "npm"), true),
            (c.H(".npm", "_cacache"), "npm", Names("node", "npm"), true),
            (c.L("pip", "Cache"), "pip", Names("python", "pythonw", "pip"), true),
            (c.L("uv", "cache"), "uv", Names("uv", "python"), true),
            (c.L("pypoetry", "Cache"), "Poetry", Names("poetry", "python"), true),
            (c.L("NuGet", "v3-cache"), "NuGet HTTP", Names("dotnet", "MSBuild", "devenv"), true),
            (c.L("NuGet", "plugins-cache"), "complementos NuGet", Names("dotnet", "MSBuild"), true),
            (c.H(".nuget", "packages"), "paquetes NuGet", Names("dotnet", "MSBuild", "devenv"), true),
            (c.L("Yarn", "Cache"), "Yarn", Names("node", "yarn"), true),
            (c.L("pnpm-cache"), "pnpm", Names("node", "pnpm"), true),
            (c.L("pnpm", "store"), "almacén pnpm", Names("node", "pnpm"), true),
            (c.H(".bun", "install", "cache"), "Bun", Names("bun"), true),
            (c.H(".cache", "pip"), "pip", Names("python", "pip"), true),
            (c.H(".cache", "uv"), "uv", Names("uv", "python"), true),
            (c.H(".cache", "go-build"), "compilación Go", Names("go"), false),
            (c.L("go-build"), "compilación Go", Names("go"), false),
            (c.H(".cargo", "registry", "cache"), "Rust Cargo", Names("cargo", "rustc"), true),
            (c.H(".gradle", "wrapper", "dists"), "distribuciones Gradle Wrapper", Names("java", "gradle", "studio64"), true),
            (c.H(".gradle", "caches", "modules-2"), "dependencias Gradle", Names("java", "gradle", "studio64"), true),
            (c.H(".gradle", "caches", "jars-9"), "JAR derivados de Gradle", Names("java", "gradle"), false),
            (c.H(".cache", "ms-playwright"), "navegadores Playwright", Names("node", "chrome", "firefox", "msedge"), true),
            (c.L("ms-playwright"), "navegadores Playwright", Names("node", "chrome", "firefox", "msedge"), true),
            (c.L("Cypress", "Cache"), "Cypress", Names("node", "Cypress"), true),
            (c.L("UnrealEngine", "Common", "DerivedDataCache"), "datos derivados de Unreal Engine", Names("UnrealEditor", "UE4Editor"), false),
            (c.L("D3DSCache"), "sombreadores DirectX", Array.Empty<string>(), false)
        };
        foreach (var cache in caches)
            c.Add(cache.Path, $"Caché de {cache.Name}", CleanupCategory.Development, Risk.Safe, recommend: !cache.Maximum, maximum: cache.Maximum,
                consequence: cache.Maximum ? "Se vuelve a descargar cuando la herramienta lo necesite; puede requerir internet y credenciales del repositorio."
                    : "La herramienta vuelve a generar estos datos; su próximo inicio o compilación puede tardar más.", processes: cache.Processes);
        foreach (var version in c.Children(c.H(".gradle", "caches")).Where(x => char.IsDigit(Path.GetFileName(x.Path).FirstOrDefault())))
        foreach (var child in c.Children(version.Path))
        {
            var name = Path.GetFileName(child.Path);
            var derived = name.StartsWith("transforms", StringComparison.OrdinalIgnoreCase) || name is "kotlin-dsl" or "groovy-dsl" or "generated-gradle-jars" or "scripts";
            c.Add(child.Path, $"Gradle {Path.GetFileName(version.Path)}: {name}", CleanupCategory.Development,
                derived ? Risk.Safe : Risk.Review, recommend: derived, consequence: "Gradle volverá a generar o descargar estos datos.", processes: Names("java", "gradle", "studio64"));
        }
        var maven = c.H(".m2", "repository");
        if (c.Index.Get(maven) is { } mavenEntry)
        {
            var jars = c.Index.Snapshots(maven, c.Token).Where(x => !x.IsDirectory && Path.GetExtension(x.Path).Equals(".jar", StringComparison.OrdinalIgnoreCase));
            var localArtifacts = jars.Any(x => c.ReadText(Path.Combine(Path.GetDirectoryName(x.Path)!, "_remote.repositories")) is not { } repositories ||
                repositories.Contains(Path.GetFileName(x.Path) + ">=", StringComparison.Ordinal));
            c.Add(maven, "Repositorio local de Maven", CleanupCategory.Development, localArtifacts || !mavenEntry.Complete ? Risk.Caution : Risk.Review,
                consequence: "Los artefactos instalados con mvn install pueden ser únicos; los remotos se vuelven a descargar.", processes: Names("java", "mvn"),
                reason: localArtifacts ? "Hay artefactos sin una procedencia remota comprobada." : "Revisa si necesitas compilar sin conexión.");
        }
        foreach (var root in new[] { c.H("miniconda3", "pkgs"), c.H("anaconda3", "pkgs"), c.H(".conda", "pkgs") })
            c.Add(root, "Paquetes descargados de Conda", CleanupCategory.Development, Risk.Review,
                consequence: "Usa preferentemente conda clean para respetar los entornos y enlaces existentes.", processes: Names("conda", "python"));
        foreach (var root in new[] { c.H(".rustup", "toolchains"), c.H(".pyenv", "pyenv-win", "versions"), c.R("nvm"), c.H(".fvm", "versions"), c.H("fvm", "versions"), c.H(".jdks"), c.R("uv", "python") })
        {
            var versions = c.Children(root).Where(x => x.Snapshot.IsDirectory).OrderByDescending(x => NumericVersion(Path.GetFileName(x.Path))).ToArray();
            foreach (var old in versions.Skip(1))
                c.Add(old.Path, $"Versión de herramienta para revisar: {Path.GetFileName(old.Path)}", CleanupCategory.Development, Risk.Review,
                    consequence: "Un proyecto, entorno virtual o configuración global puede depender de esta versión exacta.",
                    processes: Names("node", "python", "java", "rustc", "cargo", "dart", "flutter"),
                    reason: "Se conserva la versión más reciente detectada; la anterior no se considera prescindible automáticamente.");
        }
        foreach (var root in new[] { c.L("JetBrains"), c.R("JetBrains") })
        foreach (var ide in c.Children(root))
        foreach (var cache in new[] { "caches", "index", "tmp" })
            c.Add(Path.Combine(ide.Path, cache), $"Caché de {Path.GetFileName(ide.Path)}", CleanupCategory.Development, Risk.Safe, recommend: true,
                consequence: "El IDE reconstruirá índices y cachés. Su historial local y configuraciones se conservan.", processes: Names("idea64", "pycharm64", "webstorm64", "rider64", "studio64", "java"));
        ScanModels(c);
        ScanDeveloperVersionsAndData(c);
        var docker = c.L("Docker");
        if (c.Index.Get(docker) is not null || c.Index.Get(c.H(".docker")) is not null)
        {
            c.Command(docker, "Docker: limpiar caché de compilación con su comando oficial",
                new(OfficialCommandKind.DockerBuildCachePrune, "docker.exe", ["builder", "prune", "--force", "--filter", "until=168h"]), Risk.Review,
                consequence: "Docker elimina su caché de compilación no utilizada de más de siete días. Las próximas compilaciones serán más lentas. No se borran volúmenes.");
            c.AddInformation(CleanupCategory.Development, "Discos virtuales de Docker y WSL",
                "Nunca se borran ni compactan directamente los VHDX del motor: requieren apagado y procedimientos propios de Docker/WSL.");
        }
    }

    private static void ScanModels(ScanContext c)
    {
        var root = c.H(".ollama", "models");
        var manifests = c.Entries().Where(x => !x.Snapshot.IsDirectory && SafetyPolicy.IsWithin(x.Path, Path.Combine(root, "manifests"))).ToArray();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var complete = c.Index.Get(Path.Combine(root, "manifests"))?.Complete == true;
        foreach (var manifest in manifests)
        {
            try
            {
                var text = c.ReadText(manifest.Path);
                if (text is null) { complete = false; continue; }
                using var json = JsonDocument.Parse(text);
                foreach (var digest in Regex.Matches(text, @"sha256:[a-fA-F0-9]{64}").Select(x => x.Value)) used.Add(digest.Replace(':', '-'));
            }
            catch (JsonException) { complete = false; }
        }
        foreach (var blob in c.Children(Path.Combine(root, "blobs")))
        {
            var name = Path.GetFileName(blob.Path);
            if (name.Contains("-partial", StringComparison.OrdinalIgnoreCase))
                c.Add(blob.Path, "Descarga parcial de Ollama", CleanupCategory.Development, Risk.Safe, recommend: true,
                    consequence: "La descarga se reiniciará si vuelves a solicitar el modelo.", processes: Names("ollama"), allowPersonal: true);
            else if (complete && !used.Contains(name))
                c.Add(blob.Path, "Pieza de Ollama no usada por modelos instalados", CleanupCategory.Development, Risk.Safe, recommend: true,
                    consequence: "Ningún manifiesto revisado hace referencia a esta pieza.", processes: Names("ollama"), allowPersonal: true);
        }
        if (manifests.Length > 0) c.AddInformation(CleanupCategory.Development, "Modelos instalados en Ollama",
            $"Se reconocieron {manifests.Length} manifiestos. Para retirar un modelo completo usa ollama rm; sus piezas pueden estar compartidas.");
        foreach (var model in c.Children(c.H(".cache", "huggingface", "hub")).Where(x => Path.GetFileName(x.Path).StartsWith("models--", StringComparison.Ordinal)))
        {
            var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var read = c.Index.Get(Path.Combine(model.Path, "refs"))?.Complete == true;
            foreach (var reference in c.Index.Snapshots(Path.Combine(model.Path, "refs"), c.Token).Where(x => !x.IsDirectory))
            { var value = c.ReadText(reference.Path); if (value is null) read = false; else references.Add(value.Trim()); }
            foreach (var snapshot in c.Children(Path.Combine(model.Path, "snapshots")))
                if (!references.Contains(Path.GetFileName(snapshot.Path))) c.Add(snapshot.Path, "Revisión anterior de Hugging Face", CleanupCategory.Development,
                    Risk.Review, consequence: "Un proyecto puede fijar este hash aunque no lo use ninguna rama. Se volverá a descargar si todavía está disponible.",
                    processes: Names("python", "pythonw"), reason: read ? "No la referencia ninguna rama local." : "Las referencias locales no se pudieron revisar por completo.", allowPersonal: true);
        }
    }

    private static void ScanDeveloperVersionsAndData(ScanContext c)
    {
        foreach (var prefix in new[] { c.R("npm", "node_modules"), c.H(".local", "share", "pnpm", "global") })
        foreach (var package in c.Children(prefix).Where(x => x.Snapshot.IsDirectory)
            .SelectMany(x => Path.GetFileName(x.Path).StartsWith('@') ? c.Children(x.Path) : [x]))
            c.Add(package.Path, $"Paquete global: {Path.GetFileName(package.Path)}", CleanupCategory.Development, Risk.Review,
                consequence: "Sus comandos dejarán de estar disponibles. Reinstálalo con su gestor si lo necesitas.", processes: Names("node", "npm", "pnpm"));
        foreach (var python in c.Children(c.R("Python")))
            c.Add(Path.Combine(python.Path, "site-packages"), "Paquetes de usuario de " + Path.GetFileName(python.Path), CleanupCategory.Development,
                Risk.Review, consequence: "Comprueba si esa versión de Python y sus programas todavía necesitan los paquetes. Pueden incluir instalaciones locales.",
                processes: Names("python", "pythonw", "pip"), minimumAge: TimeSpan.FromDays(90));
        foreach (var root in new[] { c.H(".cache"), c.L("sccache"), c.L("ccache"), c.H(".cache", "tectonic") })
        foreach (var child in c.Children(root).Where(x => !c.Covered(x.Path)))
            c.Add(child.Path, $"Caché de herramienta: {Path.GetFileName(child.Path)}", CleanupCategory.Development, Risk.Review,
                consequence: "El nombre sugiere una caché, pero no demuestra que todo su contenido pueda regenerarse.");
        foreach (var root in new[] { c.H(".vscode", "extensions"), c.H(".cursor", "extensions") })
        {
            var groups = c.Children(root).Where(x => x.Snapshot.IsDirectory).Select(x => (Entry: x, Match: Regex.Match(Path.GetFileName(x.Path), @"^(.+)-(\d+\.\d+\.\d+)(?:-.+)?$")))
                .Where(x => x.Match.Success).GroupBy(x => x.Match.Groups[1].Value, StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
            foreach (var old in group.OrderByDescending(x => NumericVersion(x.Match.Groups[2].Value)).Skip(1))
                c.Add(old.Entry.Path, "Extensión anterior: " + Path.GetFileName(old.Entry.Path), CleanupCategory.Development, Risk.Review,
                    consequence: "Puede ser una versión fijada o reservada para volver atrás. Revisa las extensiones del editor.", processes: Names("Code", "Cursor"));
        }
        foreach (var entry in c.Entries().Where(x => x.Snapshot.IsDirectory &&
            (Path.GetFileName(x.Path).Equals("postgres", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(x.Path).Equals("postgresql", StringComparison.OrdinalIgnoreCase) ||
             Path.GetFileName(x.Path).Equals("mongodb", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(x.Path).Equals("mysql", StringComparison.OrdinalIgnoreCase))))
            if (c.IsOld(entry, TimeSpan.FromDays(30))) c.Add(entry.Path, "Datos antiguos de base de datos: " + Path.GetFileName(entry.Path), CleanupCategory.Development,
                Risk.Caution, consequence: "Puede contener la única copia de tus bases de datos. Exporta y verifica un respaldo antes de retirarlo.",
                processes: Names("postgres", "mysqld", "mongod"), allowPersonal: true);
    }
}
