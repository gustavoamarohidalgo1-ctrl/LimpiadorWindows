using System.Text;

namespace Limpiador.Runtime;

/// <summary>Drains a child process completely while retaining a bounded beginning and ending.</summary>
internal static class BoundedProcessOutput
{
    internal static async Task<string> ReadAsync(TextReader reader, int maximumCharacters = 65_536)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCharacters, 2);
        var firstLimit = maximumCharacters / 2;
        var lastLimit = maximumCharacters - firstLimit;
        var first = new StringBuilder(firstLimit);
        var last = new StringBuilder(lastLimit);
        var buffer = new char[4096];
        long total = 0;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory())) > 0)
        {
            total += count;
            var keepFirst = Math.Min(firstLimit - first.Length, count);
            first.Append(buffer, 0, keepFirst);
            last.Append(buffer, keepFirst, count - keepFirst);
            if (last.Length > lastLimit) last.Remove(0, last.Length - lastLimit);
        }
        var omitted = total - first.Length - last.Length;
        return omitted == 0 ? first.ToString() + last :
            first + $"\n[… {omitted:N0} caracteres intermedios omitidos …]\n" + last;
    }
}
