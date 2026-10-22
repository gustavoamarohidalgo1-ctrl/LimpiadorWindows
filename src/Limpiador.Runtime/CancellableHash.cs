using System.Buffers;
using System.Security.Cryptography;

namespace Limpiador.Runtime;

internal static class CancellableHash
{
    internal static string Compute(Stream stream, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = stream.Read(buffer, 0, buffer.Length);
                if (count == 0) break;
                hash.AppendData(buffer, 0, count);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}
