namespace ItemForge.Core.Model;

// Keeps the last few loaded models in memory. A card's GLB can be tens of megabytes, and the live view
// re-renders on every slider tick, so reloading per frame is not an option.
public static class ModelCache
{
    private const int Capacity = 3;

    private static readonly object Gate = new();
    private static readonly LinkedList<Entry> Entries = new();

    private sealed record Entry(string Key, ModelGeometry Geometry);

    // Loads and caches, keyed by path + size + write time so an edited model is reloaded, not served stale.
    public static ModelGeometry Load(string fullPath)
    {
        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"model not found: {fullPath}", fullPath);
        }
        string key = $"{info.FullName.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";

        lock (Gate)
        {
            var node = Entries.First;
            while (node is not null)
            {
                if (node.Value.Key == key)
                {
                    Entries.Remove(node);
                    Entries.AddFirst(node);
                    return node.Value.Geometry;
                }
                node = node.Next;
            }
        }

        var geometry = GlbLoader.Load(fullPath);
        lock (Gate)
        {
            Entries.AddFirst(new Entry(key, geometry));
            while (Entries.Count > Capacity)
            {
                Entries.RemoveLast();
            }
        }
        return geometry;
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Entries.Clear();
        }
    }
}
