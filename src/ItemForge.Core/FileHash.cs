using System.Security.Cryptography;

namespace ItemForge.Core;

// SHA-256 of a file, cached by (full path, length, last write time) so listing many cards does not
// re-hash large models on every refresh.
public static class FileHash
{
    private static readonly Dictionary<string, (long Length, DateTime Written, string Hash)> Cache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public static string Sha256(string fullPath)
    {
        var info = new FileInfo(fullPath);
        lock (Gate)
        {
            if (Cache.TryGetValue(info.FullName, out var hit) &&
                hit.Length == info.Length && hit.Written == info.LastWriteTimeUtc)
            {
                return hit.Hash;
            }
        }

        string hash;
        using (var stream = File.OpenRead(info.FullName))
        {
            hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        lock (Gate)
        {
            Cache[info.FullName] = (info.Length, info.LastWriteTimeUtc, hash);
        }
        return hash;
    }
}
