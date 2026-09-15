using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ItemForge.Core.Cards;

// One model card = one 3D model bound to one item. Every adjustment for that item lives in this file and
// nowhere else. The file name (cards/<id>.json) is the id and is authoritative, so a file can never
// disagree with its own content about who it is.
//
// Unknown fields survive a load/save at every level ([JsonExtensionData]), so a card written by a newer
// build round-trips through an older one without losing data. The four presentation blocks are free-form
// JSON in schema 1: later phases define their shape, and this phase only has to carry them intact.
public sealed class Card
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;
    public string Name { get; set; } = "";

    // What kind of item this is (ItemTypes.All). Picks the pose baseline the Worn presentation seeds from.
    public string ItemType { get; set; } = "";

    public string Notes { get; set; } = "";
    public CardModel Model { get; set; } = new();
    public JsonObject Worn { get; set; } = new();
    public JsonObject Equip { get; set; } = new();
    public JsonObject Inventory { get; set; } = new();
    public JsonObject Ground { get; set; } = new();
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Modified { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore]
    public string Id { get; set; } = "";

    [JsonIgnore]
    public string DisplayName =>string.IsNullOrWhiteSpace(Name) ? Id : Name;

    public Card Clone()
    {
        var copy = JsonSerializer.Deserialize<Card>(JsonSerializer.Serialize(this, JsonOpts.Pretty), JsonOpts.Pretty)!;
        copy.Id = Id;
        return copy;
    }
}

public sealed class CardModel
{
    // Workspace-relative (forward slashes) when the file is inside the workspace, else absolute.
    public string Path { get; set; } = "";

    // SHA-256 of the file when it was bound. A mismatch flags a changed model instead of silently baking it.
    public string Sha256 { get; set; } = "";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public static class ItemTypes
{
    public static readonly IReadOnlyList<string> All = new[]
    {
        "one_handed_sword", "two_handed_sword", "axe", "hammer", "staff", "wand", "bow", "shield", "tool", "other",
    };
}
