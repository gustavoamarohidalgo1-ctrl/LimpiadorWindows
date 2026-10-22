using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Limpiador.Core;

/// <summary>Read-only Git index inspection. Unknown formats never imply that files are untracked.</summary>
internal static class GitIndexReader
{
    internal sealed record Inspection(bool Known, IReadOnlyList<string> Tracked, string? Reason = null);
    internal static Inspection Read(string repository, SafetyPolicy policy)
    {
        try
        {
            var git = Path.Combine(repository, ".git");
            if (!SafeMetadataPath(git, policy)) return new(false, [], "El directorio Git no se pudo comprobar.");
            if (!Directory.Exists(git))
            {
                var info = new FileInfo(git);
                if (info.Length > 4096) return new(false, [], "Referencia Git no reconocida.");
                var text = File.ReadAllText(git).Trim();
                if (!text.StartsWith("gitdir: ", StringComparison.Ordinal)) return new(false, [], "Referencia Git no reconocida.");
                git = Path.GetFullPath(text[8..], repository);
                if (!SafeMetadataPath(git, policy)) return new(false, [], "El worktree apunta fuera de las carpetas autorizadas o mediante un enlace.");
            }
            var index = Path.Combine(git, "index");
            if (!SafeMetadataPath(index, policy)) return new(false, [], "No se pudo leer el índice de Git.");
            using var file = new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is < 32 or > 128 * 1024 * 1024) return new(false, [], "Tamaño de índice Git no admitido.");
            var data = new byte[checked((int)file.Length)];
            file.ReadExactly(data);
            return Parse(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException or DecoderFallbackException)
        { return new(false, [], "No se pudo interpretar de forma fiable el índice de Git."); }
    }

    private static bool SafeMetadataPath(string path, SafetyPolicy policy)
    {
        if (!policy.ApprovedRoots.Any(root => SafetyPolicy.IsWithin(path, root)) || policy.IsCloudPath(path) ||
            policy.IsExcludedPath(path) || policy.ContainsExcludedPath(path)) return false;
        for (string? p = path; p is not null; p = Path.GetDirectoryName(p))
            if ((File.GetAttributes(p) & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0) return false;
        return true;
    }

    internal static Inspection Parse(byte[] data)
    {
        Inspection Unknown(string reason) => new(false, [], reason);
        if (data.Length < 32 || !data.AsSpan(0, 4).SequenceEqual("DIRC"u8)) return Unknown("Cabecera Git desconocida.");
        var version = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4));
        if (version is not (2 or 3)) return Unknown("Git utiliza un formato de índice no interpretado; se exige revisión.");
        var count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4));
        if (count > 1_000_000) return Unknown("El índice Git es demasiado grande para verificarlo completo.");
        var end = data.Length - 20;
        if (!CryptographicOperations.FixedTimeEquals(SHA1.HashData(data.AsSpan(0, end)), data.AsSpan(end, 20)))
            return Unknown("El índice Git cambió, está incompleto o utiliza otro formato de hash.");
        var tracked = new List<string>((int)count);
        var utf8 = new UTF8Encoding(false, true);
        var offset = 12;
        for (var i = 0u; i < count; i++)
        {
            var start = offset;
            if (offset > end - 62) return Unknown("Entrada Git truncada.");
            var flags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 60, 2));
            offset += 62;
            if ((flags & 0x4000) != 0)
            {
                if (version != 3 || offset > end - 2) return Unknown("Banderas extendidas de Git no reconocidas.");
                offset += 2;
            }
            var zero = Array.IndexOf(data, (byte)0, offset, end - offset);
            if (zero < 0) return Unknown("Nombre Git sin terminador.");
            var byteLength = zero - offset;
            if ((flags & 0x0fff) != 0x0fff && byteLength != (flags & 0x0fff)) return Unknown("Longitud Git incoherente.");
            string name;
            try { name = utf8.GetString(data, offset, byteLength); }
            catch (DecoderFallbackException) { return Unknown("Nombre Git no decodificable."); }
            if (name.Length == 0 || name.StartsWith('/') || name.Split('/').Any(x => x is ".." or ".")) return Unknown("Ruta Git no válida.");
            tracked.Add(name);
            var entryBytes = zero + 1 - start;
            offset = checked(start + ((entryBytes + 7) & ~7));
            if (offset > end) return Unknown("Relleno Git truncado.");
        }
        while (offset < end)
        {
            if (offset > end - 8) return Unknown("Extensión Git truncada.");
            // Lowercase signatures are required extensions (link=split index, sdir=sparse index).
            if (data[offset] is < (byte)'A' or > (byte)'Z') return Unknown("Índice Git dividido o extensión obligatoria no interpretada.");
            var size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 4, 4));
            if (size > end - offset - 8) return Unknown("Extensión Git fuera de límites.");
            offset += checked(8 + (int)size);
        }
        return new(true, tracked);
    }
}
