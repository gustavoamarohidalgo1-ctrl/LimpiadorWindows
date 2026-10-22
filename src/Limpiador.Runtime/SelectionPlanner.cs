using Limpiador.Core;

namespace Limpiador.Runtime;

/// <summary>The same explicit selection rule is used for preview and execution.</summary>
public static class SelectionPlanner
{
    public static CleanupPlan Create(IEnumerable<Candidate> candidates, IReadOnlyList<string> requestedIds)
    {
        var ids = requestedIds.ToHashSet(StringComparer.Ordinal);
        var requested = candidates.Where(x => ids.Contains(x.Id)).ToArray();
        if (ids.Except(requested.Select(x => x.Id), StringComparer.Ordinal).Any())
            throw new ArgumentException("La selección no pertenece al último análisis. Vuelve a analizar.");
        return CleanupPlan.Create(requested.Select(x => x with { Recommended = false, MaximumEligible = false }), manualIds: ids);
    }
}
