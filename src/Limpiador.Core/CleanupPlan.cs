using System.Collections.ObjectModel;

namespace Limpiador.Core;

public sealed partial record CleanupPlan
{
    public static CleanupPlan Create(IEnumerable<Candidate> candidates, bool maximum = false, IEnumerable<string>? manualIds = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var requested = (manualIds ?? []).ToHashSet(StringComparer.Ordinal);
        var all = candidates.ToArray();
        var repeated = all.GroupBy(x => x.Id, StringComparer.Ordinal).Where(x => x.Skip(1).Any())
            .Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
        var eligible = new List<(Candidate Item, string[] Paths)>();
        var preserved = new PathPrefixIndex<Candidate>();
        foreach (var item in all)
        {
            string? reason = null;
            if (repeated.Contains(item.Id)) reason = "Identificador repetido: se excluyen todas sus apariciones. Vuelve a analizar antes de seleccionar.";
            else if (!item.CanSelect || item.IsBlocked) reason = item.BlockReason ?? "Este elemento está bloqueado y no se puede seleccionar.";
            else if (maximum && item.Risk != Risk.Safe && item.IsIrreversible)
                reason = "El modo máximo excluye esta operación irreversible que requiere revisión.";
            else if (!requested.Contains(item.Id) && !(item.Risk == Risk.Safe && (item.Recommended || maximum && item.MaximumEligible)))
                reason = item.Risk != Risk.Safe ? "Requiere selección manual; no se ha solicitado." : "No se ha solicitado ni está incluido en este plan automático.";
            if (reason is not null) { reasons[item.Id] = reason; continue; }
            eligible.Add((item, item.Paths.Select(SafetyPolicy.Normalize).ToArray()));
            if (item.PreservedPath is { } original) preserved.Add(SafetyPolicy.Normalize(original), item);
        }

        // Keep the existing priority and stable input order on ties. Path indexes
        // remove the all-pairs searches without changing which overlap wins.
        var acceptedPaths = new PathPrefixIndex<Candidate>();
        var regenerates = new List<Candidate>();
        var withoutDeleting = new List<Candidate>();
        var costs = new List<Candidate>();
        var manual = new List<Candidate>();
        foreach (var (item, paths) in eligible.OrderBy(x => x.Item.Paths.Sum(p => (long)p.Length)).ThenByDescending(x => x.Item.Bytes))
        {
            var dependency = paths.Select(preserved.Descendant).FirstOrDefault(x => x is not null);
            if (dependency is not null)
            {
                reasons[item.Id] = $"Contiene la original que debe conservarse para «{Label(dependency)}»: {dependency.PreservedPath}";
                continue;
            }
            var overlap = paths.Select(acceptedPaths.Overlap).FirstOrDefault(x => x is not null);
            if (overlap is not null)
            {
                reasons[item.Id] = $"Se solapa con «{Label(overlap)}» ({overlap.Id}), que ya está incluido en el plan.";
                continue;
            }
            foreach (var path in paths) acceptedPaths.Add(path, item);
            if (requested.Contains(item.Id)) manual.Add(item);
            else if (item.Recommended && item.Action == ActionKind.Delete) regenerates.Add(item);
            else if (item.Action is ActionKind.Compress or ActionKind.Decompress or ActionKind.CloneDuplicates or ActionKind.CompactDatabase ||
                item.Command?.Kind == OfficialCommandKind.GitGarbageCollect) withoutDeleting.Add(item);
            else costs.Add(item);
        }
        return new()
        {
            Regenerates = regenerates.ToArray(), WithoutDeleting = withoutDeleting.ToArray(),
            CostsDownloadOrRebuild = costs.ToArray(), Manual = manual.ToArray(),
            Excluded = all.Where(x => reasons.ContainsKey(x.Id)).ToArray(),
            ExclusionReasons = new ReadOnlyDictionary<string, string>(reasons)
        };
    }

    private static string Label(Candidate item) => string.IsNullOrWhiteSpace(item.Title) ? item.Id : item.Title;
}
