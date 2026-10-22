namespace Limpiador.Core;

/// <summary>Case-insensitive Windows path components, with O(depth) ancestor/descendant queries.</summary>
internal sealed class PathPrefixIndex<T> where T : class
{
    private sealed class Node
    {
        internal Dictionary<string, Node>? Children;
        internal T? Value;
        internal T? FirstDescendant;
    }

    private readonly Node root = new();

    internal void Add(string normalizedPath, T value)
    {
        var node = root;
        node.FirstDescendant ??= value;
        foreach (var component in Components(normalizedPath))
        {
            node.Children ??= new(StringComparer.OrdinalIgnoreCase);
            if (!node.Children.TryGetValue(component, out var next)) node.Children[component] = next = new();
            node = next;
            node.FirstDescendant ??= value;
        }
        node.Value ??= value;
    }

    internal T? Ancestor(string normalizedPath)
    {
        var node = root;
        if (node.Value is not null) return node.Value;
        foreach (var component in Components(normalizedPath))
        {
            if (node.Children is null || !node.Children.TryGetValue(component, out var next)) return null;
            node = next;
            if (node.Value is not null) return node.Value;
        }
        return null;
    }

    internal T? Descendant(string normalizedPath)
    {
        var node = root;
        foreach (var component in Components(normalizedPath))
        {
            if (node.Children is null || !node.Children.TryGetValue(component, out var next)) return null;
            node = next;
        }
        return node.FirstDescendant;
    }

    internal T? Overlap(string normalizedPath) => Ancestor(normalizedPath) ?? Descendant(normalizedPath);

    private static IEnumerable<string> Components(string normalizedPath)
    {
        // Include the volume as a component: C:\, D:\ and UNC shares must never overlap.
        var volume = Path.GetPathRoot(normalizedPath) ?? "";
        yield return volume;
        foreach (var part in normalizedPath[volume.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            yield return part;
    }
}
