using System.Text.RegularExpressions;

namespace Limpiador.Core;

public sealed partial class Scanner
{
    private static readonly string[] ProjectMarkers = [".git", "package.json", "pyproject.toml", "requirements.txt", "setup.py", "Cargo.toml", "go.mod", "pubspec.yaml", "build.gradle", "build.gradle.kts", "pom.xml", "CMakeLists.txt", "composer.json", "Gemfile", "ProjectSettings", "Package.swift"];

    private static void ScanProjects(ScanContext c)
    {
        foreach (var root in c.Entries().Where(x => x.Snapshot.IsDirectory && !SafetyPolicy.IsWithin(x.Path, c.Local) && !SafetyPolicy.IsWithin(x.Path, c.Roaming)))
        {
            if (IsDependencyPath(root.Path) || c.Policy.IsCloudPath(root.Path)) continue;
            var children = c.Children(root.Path).ToArray();
            bool Has(string name) => name == ".git" && c.Index.RepositoryRoots.Contains(root.Path) ||
                children.Any(x => Path.GetFileName(x.Path).Equals(name, StringComparison.OrdinalIgnoreCase));
            bool Extension(string ext) => children.Any(x => Path.GetExtension(x.Path).Equals(ext, StringComparison.OrdinalIgnoreCase));
            if (!ProjectMarkers.Any(Has) && !Extension(".csproj") && !Extension(".fsproj") && !Extension(".vbproj") && !Extension(".sln") && !Extension(".tf")) continue;
            c.ProjectRoots.Add(root.Path);
            var project = Path.GetFileName(root.Path);
            var node = Has("package.json");
            var python = Has("pyproject.toml") || Has("requirements.txt") || Has("setup.py");
            var gradle = Has("build.gradle") || Has("build.gradle.kts");
            var dotnet = Extension(".csproj") || Extension(".fsproj") || Extension(".vbproj");
            var unity = Has("Assets") && Has("ProjectSettings");
            var flutter = Has("pubspec.yaml");
            var rust = Has("Cargo.toml");
            var maven = Has("pom.xml");
            var buildType = node ? Names("node", "npm", "pnpm", "yarn") : python ? Names("python", "pythonw", "pytest", "uv") :
                gradle || maven ? Names("java", "gradle", "mvn", "studio64") : dotnet ? Names("dotnet", "MSBuild", "devenv") :
                unity ? Names("Unity", "Unity Hub") : flutter ? Names("dart", "flutter") : rust ? Names("cargo", "rustc") : Names("cmake", "ninja", "make");
            void Artifact(string relative, bool safe, bool maximum = false, string? explanation = null)
            {
                c.Add(Path.Combine(root.Path, relative), $"{project}: {relative}", CleanupCategory.Projects, safe ? Risk.Safe : Risk.Review,
                    recommend: safe && !maximum, maximum: maximum, processes: buildType,
                    consequence: maximum ? "Será necesario reinstalar las dependencias o recompilar; puede requerir internet."
                        : safe ? "El proyecto vuelve a generar estos intermedios al compilar o ejecutar las pruebas."
                        : "Puede contener entregas, informes históricos o datos propios. Revisa su contenido antes de retirarlo.", reason: explanation);
            }
            if (node)
            {
                Artifact("node_modules", true, true, "Se encontró package.json junto a las dependencias.");
                foreach (var cache in new[] { ".nuxt", ".parcel-cache", ".turbo", ".eslintcache", ".angular", ".vite", Path.Combine(".next", "cache") }) Artifact(cache, true);
                foreach (var output in new[] { ".next", ".svelte-kit" }) Artifact(output, false, explanation: "Puede contener servidores publicados y entregas; la caché verificable se ofrece aparte.");
                foreach (var output in new[] { "dist", "out", "coverage", "test-results", "playwright-report" }) Artifact(output, false);
                // electron-builder often stores source icons and packaging inputs in build/.
                Artifact("build", false, explanation: "build/ puede guardar recursos de electron-builder, no solo compilaciones.");
            }
            if (python)
            {
                foreach (var cache in new[] { "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".hypothesis" }) Artifact(cache, true);
                foreach (var environment in new[] { ".venv", "venv", ".tox", ".nox" }) Artifact(environment, true, true);
                foreach (var output in new[] { "dist", "build", "htmlcov", "reports" }) Artifact(output, false);
            }
            if (gradle)
            {
                foreach (var cache in new[] { ".gradle", ".kotlin", ".cxx", "build/intermediates", "build/tmp", "build/generated", "build/kotlin", "build/classes" })
                    Artifact(cache.Replace('/', Path.DirectorySeparatorChar), true);
                foreach (var output in new[] { "build/outputs", "build/reports", "build/test-results", "build/libs" })
                    Artifact(output.Replace('/', Path.DirectorySeparatorChar), false);
            }
            if (dotnet) { Artifact("obj", true); Artifact("bin", false, explanation: "Las salidas pueden contener publicaciones, configuraciones o datos únicos."); Artifact("TestResults", false); }
            if (unity) { Artifact("Library", true, true); Artifact("Temp", true); Artifact("obj", true); Artifact("Logs", true); Artifact("Builds", false); }
            if (flutter) { Artifact(".dart_tool", true); Artifact("build", false); }
            if (rust || maven) Artifact("target", false, explanation: "Incluye binarios e informes; no todo lo que contiene está necesariamente regenerado.");
            if (Has("CMakeLists.txt"))
            {
                foreach (var build in children.Where(x => x.Snapshot.IsDirectory &&
                    (Path.GetFileName(x.Path) == "build" || Path.GetFileName(x.Path).StartsWith("cmake-build-", StringComparison.OrdinalIgnoreCase))))
                {
                    c.Add(Path.Combine(build.Path, "CMakeFiles"), $"{project}: intermedios CMake", CleanupCategory.Projects, Risk.Safe, recommend: true,
                        consequence: "CMake vuelve a generar sus intermedios.", processes: buildType);
                    c.Add(build.Path, $"{project}: salidas CMake", CleanupCategory.Projects, Risk.Review, processes: buildType, allowOverlap: true);
                }
            }
            if (Extension(".tf")) { Artifact(".terraform", true, true); /* .tfstate is never considered a cache. */ }
            if (Has("composer.json")) Artifact("vendor", true, true);
            if (Has("Gemfile")) Artifact(Path.Combine("vendor", "bundle"), true, true);
            foreach (var cache in new[] { Path.Combine(".wrangler", "cache"), Path.Combine(".wrangler", "tmp") }) Artifact(cache, true);
            foreach (var output in new[] { "entregas", "entrega", "deliverables", "results", "resultados", "informes" }) Artifact(output, false);
            // Read-only measurement of loose Git objects. Never traverse links or run git during analysis.
            var git = Path.Combine(root.Path, ".git");
            if (!c.Policy.IsExcludedPath(git) && !c.Policy.ContainsExcludedPath(git) &&
                Directory.Exists(git) && (File.GetAttributes(git) & FileAttributes.ReparsePoint) == 0)
            {
                var objects = Path.Combine(git, "objects");
                var loose = ReadLooseGitBytes(objects, c.Token);
                if (loose >= Math.Max(c.Options.MinimumBytes, 10_000_000))
                    c.Command(root.Path, $"Compactar historial de {project}", new(OfficialCommandKind.GitGarbageCollect,
                        "git.exe", ["-C", root.Path, "gc", "--no-prune"], root.Path), Risk.Review, loose,
                        "Git empaqueta objetos sin podar el historial. El ahorro es una estimación; no elimina tus archivos de trabajo.");
            }
        }
    }

    private static bool IsDependencyPath(string path) => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(x => x is "node_modules" or "site-packages" or "vendor" or ".git" or "packages" or "toolchains" or ".venv" or "venv");

    private static long ReadLooseGitBytes(string objects, CancellationToken token)
    {
        try
        {
            if (!Directory.Exists(objects) || (File.GetAttributes(objects) & FileAttributes.ReparsePoint) != 0) return 0;
            long bytes = 0;
            var count = 0;
            foreach (var dir in Directory.EnumerateDirectories(objects))
            {
                token.ThrowIfCancellationRequested();
                if (!Regex.IsMatch(Path.GetFileName(dir), "^[0-9a-f]{2}$") || (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    if (++count > 100_000) return 0;
                    var info = new FileInfo(file);
                    if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0) return 0;
                    bytes += info.Length;
                }
            }
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static void ScanToolData(ScanContext c)
    {
        foreach (var tool in new[] { ".codex", ".claude", ".config", ".local" })
        foreach (var child in c.Children(c.H(tool)))
        {
            var name = Path.GetFileName(child.Path);
            if (c.Covered(child.Path) || name is "auth.json" or "credentials.json" or "settings.json" or "config.toml") continue;
            if (name.Equals("logs", StringComparison.OrdinalIgnoreCase) || name.Equals("log", StringComparison.OrdinalIgnoreCase))
                c.Add(child.Path, $"Registros de {tool}", CleanupCategory.Logs, Risk.Safe, recommend: true,
                    consequence: "Se pierde el historial de diagnóstico, no las conversaciones ni la configuración.", processes: Names(tool.TrimStart('.'), "node"));
            else if (name.Contains("session", StringComparison.OrdinalIgnoreCase) || name.Equals("projects", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var session in c.Children(child.Path).Where(x => c.IsOld(x, TimeSpan.FromDays(30))))
                    c.Add(session.Path, $"Conversaciones antiguas de {tool}", CleanupCategory.ToolData, Risk.Review,
                        consequence: "Se pierde el historial de conversaciones o trabajo guardado en esta ruta. No es una caché regenerable.",
                        minimumAge: TimeSpan.FromDays(30), processes: Names(tool.TrimStart('.'), "node"));
            }
            else if (name is "cache" or "caches" or "tmp")
            {
                var config = string.Join('\n', new[] { "config.toml", "settings.json", "config.json" }.Select(x => c.ReadText(Path.Combine(c.H(tool), x)) ?? ""));
                c.Add(child.Path, $"Datos temporales de {tool}: {name}", CleanupCategory.ToolData, Risk.Review,
                    consequence: "Comprueba que no sea una carpeta de datos configurada por ti.",
                    reason: config.Contains(name, StringComparison.OrdinalIgnoreCase) ? "La configuración menciona esta ruta; se exige revisión." : "Se reconoce el nombre, pero no se presupone que sea regenerable.",
                    processes: Names(tool.TrimStart('.'), "node"));
            }
        }
        foreach (var root in new[] { c.H(".local", "state"), c.H(".config") })
        foreach (var tool in c.Children(root).Where(x => !c.Covered(x.Path) && c.IsOld(x, TimeSpan.FromDays(90))))
            c.Add(tool.Path, "Configuración antigua de herramienta: " + Path.GetFileName(tool.Path), CleanupCategory.ToolData, Risk.Review,
                consequence: "Se pierden ajustes e historial. Que no haya un comando en PATH no demuestra que la herramienta no exista.", minimumAge: TimeSpan.FromDays(90));
    }

    private static void ScanInstallersAndLogs(ScanContext c)
    {
        var installers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".iso", ".img", ".apk", ".aab" };
        foreach (var file in c.Entries().Where(x => !x.Snapshot.IsDirectory))
        {
            c.Token.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(file.Path);
            var name = Path.GetFileName(file.Path);
            if (c.IsPersonal(file.Path) && !IsDependencyPath(file.Path) && !c.InProject(file.Path) &&
                (installers.Contains(ext) || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                    (name.Contains("setup", StringComparison.OrdinalIgnoreCase) || name.Contains("install", StringComparison.OrdinalIgnoreCase))))
                c.Add(file.Path, "Instalador: " + name, CleanupCategory.Installers, Risk.Review,
                    consequence: "Puede ser la única copia de esta versión. No se deduce que esté instalada a partir del nombre del archivo.", minimumAge: TimeSpan.FromDays(7));
            if (ext.Equals(".hprof", StringComparison.OrdinalIgnoreCase) || ext.Equals(".dmp", StringComparison.OrdinalIgnoreCase))
                c.Add(file.Path, "Volcado de fallo: " + name, CleanupCategory.Logs, Risk.Review,
                    consequence: "Se pierde información útil para investigar un fallo. Puede contener datos de la aplicación.", processes: Names("java", "devenv", "dotnet"));
            if (ext.Equals(".log", StringComparison.OrdinalIgnoreCase) && file.Bytes >= Math.Max(c.Options.MinimumBytes, 20_000_000))
            {
                var known = name.Equals("npm-debug.log", StringComparison.OrdinalIgnoreCase) ||
                    (SafetyPolicy.IsWithin(file.Path, c.Local) || SafetyPolicy.IsWithin(file.Path, c.Roaming)) &&
                    new[] { "logs", "log", "Crashpad", "Crash Reports" }.Contains(Path.GetFileName(Path.GetDirectoryName(file.Path)), StringComparer.OrdinalIgnoreCase);
                c.Add(file.Path, "Registro grande: " + name, CleanupCategory.Logs, known ? Risk.Safe : Risk.Review, recommend: known,
                    consequence: "Se pierde ese registro de diagnóstico. Los registros futuros se seguirán creando.");
            }
        }
        foreach (var root in new[] { c.L("CrashDumps"), c.L("Microsoft", "Windows", "WER", "ReportArchive"), c.L("Microsoft", "Windows", "WER", "ReportQueue") })
        foreach (var report in c.Children(root))
            c.Add(report.Path, "Informe de fallos de Windows: " + Path.GetFileName(report.Path), CleanupCategory.Logs, Risk.Review,
                consequence: "Ya no podrás usar este informe para diagnosticar ese fallo.");
        foreach (var root in new[] { c.L("npm-cache", "_logs"), c.H(".npm", "_logs"), c.R("Code", "logs"), c.R("Cursor", "logs"), c.H(".wrangler", "logs") })
            c.Add(root, "Registros de herramienta: " + Path.GetFileName(Path.GetDirectoryName(root)), CleanupCategory.Logs, Risk.Safe, recommend: true,
                consequence: "Se pierde el historial de diagnóstico de la herramienta; no sus proyectos.", processes: Names("node", "Code", "Cursor", "wrangler"));
    }
}
