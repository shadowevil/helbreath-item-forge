using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ItemForge.Core.Cards;

public sealed record CardSummary(
    string Id,
    string Name,
    string ItemType,
    string ModelPath,
    ModelStatus ModelStatus,
    DateTimeOffset Modified,
    string File,
    string? Error);

// Reads and writes cards/<id>.json. Writes are atomic (temp file + replace), and delete moves the file into
// cards/.trash/ rather than destroying it.
public sealed class CardStore
{
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9_]*$", RegexOptions.Compiled);

    public Workspace Workspace { get; }

    public string CardsDir => Path.Combine(Workspace.Root, "cards");

    public string TrashDir => Path.Combine(CardsDir, ".trash");

    public CardStore(Workspace workspace)
    {
        Workspace = workspace;
        Directory.CreateDirectory(CardsDir);
    }

    public static bool IsValidId(string id) => IdPattern.IsMatch(id);

    public string PathFor(string id)
    {
        if (!IsValidId(id))
        {
            throw new ArgumentException($"invalid card id '{id}' (lowercase letters, digits and _ only)");
        }
        return Path.Combine(CardsDir, id + ".json");
    }

    public bool Exists(string id) => IsValidId(id) && File.Exists(PathFor(id));

    // The id a file in the cards folder would have, or null when the file is not a card file.
    public string? IdForFile(string file)
    {
        string full = Path.GetFullPath(file);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(CardsDir), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(full), ".json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        string id = Path.GetFileNameWithoutExtension(full);
        return IsValidId(id) ? id : null;
    }

    public IReadOnlyList<CardSummary> List()
    {
        var list = new List<CardSummary>();
        foreach (string file in Directory.EnumerateFiles(CardsDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            string id = Path.GetFileNameWithoutExtension(file);
            if (!IsValidId(id))
            {
                continue;
            }
            try
            {
                list.Add(Summarize(Load(id)));
            }
            catch (Exception ex)
            {
                list.Add(new CardSummary(id, id, "", "", ModelStatus.None, File.GetLastWriteTimeUtc(file), file, ex.Message));
            }
        }
        return list
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .ToList();
    }

    public CardSummary Summarize(Card card) => new(
        card.Id,
        card.DisplayName,
        card.ItemType,
        card.Model.Path,
        ModelBinding.Status(card.Model, Workspace),
        card.Modified,
        PathFor(card.Id),
        null);

    public Card Load(string id)
    {
        string json = File.ReadAllText(PathFor(id), Encoding.UTF8);
        var card = JsonSerializer.Deserialize<Card>(json, JsonOpts.Pretty)
                   ?? throw new InvalidDataException("the card file is empty");
        card.Id = id;
        return card;
    }

    public void Save(Card card)
    {
        var now = Now();
        if (card.Created == default)
        {
            card.Created = now;
        }
        card.Modified = now;
        card.Schema = Card.CurrentSchema;
        WriteAtomic(PathFor(card.Id), JsonSerializer.Serialize(card, JsonOpts.Pretty) + "\n");
    }

    public Card Create(string name)
    {
        var card = new Card { Id = UniqueId(Slugify(name)), Name = name.Trim() };
        Save(card);
        return card;
    }

    public Card Duplicate(string id, string name)
    {
        var copy = Load(id).Clone();
        copy.Id = UniqueId(Slugify(name));
        copy.Name = name.Trim();
        copy.Created = default;
        Save(copy);
        return copy;
    }

    // Moves the card file into cards/.trash/ and returns where it went. The model file is never touched.
    public string Delete(string id)
    {
        string source = PathFor(id);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"no card '{id}'");
        }
        Directory.CreateDirectory(TrashDir);
        string target = Path.Combine(TrashDir, $"{id}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        File.Move(source, target, overwrite: true);
        return target;
    }

    public static string Slugify(string name)
    {
        var sb = new StringBuilder();
        foreach (char ch in name.Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(ch);
            }
            else if (sb.Length > 0 && sb[^1] != '_')
            {
                sb.Append('_');
            }
        }
        string slug = sb.ToString().Trim('_');
        return slug.Length == 0 ? "card" : slug;
    }

    private string UniqueId(string baseId)
    {
        string id = baseId;
        for (int n = 2; File.Exists(Path.Combine(CardsDir, id + ".json")); n++)
        {
            id = $"{baseId}_{n}";
        }
        return id;
    }

    // Whole seconds keep the timestamps in the file readable.
    private static DateTimeOffset Now()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
    }

    private static void WriteAtomic(string path, string text)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }
}
