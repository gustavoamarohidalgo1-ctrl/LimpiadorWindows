namespace Limpiador.App;

/// <summary>Rejects inconsistent previews without changing the user's selection.</summary>
internal static class CleanupPlanReview
{
    public static void Validate(UiCleanupPlan plan, IReadOnlyList<string> requestedIds)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(requestedIds);
        var requested = requestedIds.ToHashSet(StringComparer.Ordinal);
        if (requested.Count != requestedIds.Count || requested.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("La selección contiene identificadores inválidos o repetidos. Vuelve a analizar.");
        var represented = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in plan.Items)
        {
            if (!requested.Contains(item.Id) || !represented.Add(item.Id) || !item.CanSelect || item.SizeBytes < 0)
                throw new InvalidOperationException("El plan no coincide con los elementos solicitados. Vuelve a analizar antes de limpiar.");
        }
        foreach (var omission in plan.Omissions)
        {
            if (!requested.Contains(omission.ItemId) || !represented.Add(omission.ItemId) || string.IsNullOrWhiteSpace(omission.Reason))
                throw new InvalidOperationException("El plan contiene omisiones incoherentes o sin motivo. Vuelve a analizar.");
        }
        if (!represented.SetEquals(requested))
            throw new InvalidOperationException("El plan no explica todos los elementos de la selección. Vuelve a analizar.");
    }
}
