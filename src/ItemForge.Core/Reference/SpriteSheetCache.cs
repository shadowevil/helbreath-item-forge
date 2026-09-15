namespace ItemForge.Core.Reference;

// Keeps copied sheets in memory. A backdrop is recomposed on every slider tick, and a character sheet is
// several megabytes, so decoding one per frame is not an option.
public static class SpriteSheetCache
{
    private const int Capacity = 24;

    private static readonly object Gate = new();
    private static readonly LinkedList<Entry> Entries = new();

    private sealed record Entry(string Key, SpriteSheet Sheet);

    public static SpriteSheet Load(string pngPath)
    {
        var info = new FileInfo(pngPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"sprite sheet not found: {pngPath}", pngPath);
        }
        string key = $"{info.FullName.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";

        lock (Gate)
        {
            for (var node = Entries.First; node is not null; node = node.Next)
            {
                if (node.Value.Key == key)
                {
                    Entries.Remove(node);
                    Entries.AddFirst(node);
                    return node.Value.Sheet;
                }
            }
        }

        var sheet = SpriteSheet.Load(pngPath);
        lock (Gate)
        {
            Entries.AddFirst(new Entry(key, sheet));
            while (Entries.Count > Capacity)
            {
                Entries.RemoveLast();
            }
        }
        return sheet;
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Entries.Clear();
        }
    }
}
