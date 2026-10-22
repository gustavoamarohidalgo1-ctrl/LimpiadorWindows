namespace Limpiador.Runtime;

public static class DiagnosticArguments
{
    public static AppPreferences Parse(IReadOnlyList<string> arguments, AppPreferences defaults)
    {
        var options = AppPreferencesStore.Normalize(defaults);
        var excluded = options.ExcludedFolders.ToList();
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument is "--diagnostico" or "--json" or "--motivos" or "--rutas" or "--maximo") continue;
            if (argument is not ("--dias" or "--carpeta" or "--excluir"))
                throw new ArgumentException($"Opción desconocida: {argument}");
            if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]) || arguments[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Falta el valor de {argument}.");
            var value = arguments[index];
            if (argument == "--dias")
            {
                if (!int.TryParse(value, out var days) || days is not (7 or 30 or 90 or 180 or 365))
                    throw new ArgumentException("--dias admite 7, 30, 90, 180 o 365.");
                options = options with { MinimumAgeDays = days };
            }
            else if (argument == "--carpeta") options = options with { AdditionalFolder = value };
            else excluded.Add(value);
        }
        return AppPreferencesStore.Normalize(options with { ExcludedFolders = excluded });
    }
}
