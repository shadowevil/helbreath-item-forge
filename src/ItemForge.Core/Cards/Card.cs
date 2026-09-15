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

    // How this model is posed and lit in the game camera. Shared by every presentation: the per-presentation
    // placement sits on top of it, so a model fixed up once is fixed up everywhere.
    public ModelSetup Setup { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ModelSetup
{
    // World units per model unit. "Fit" in the Model tab sets it from the model's own size.
    public float Scale { get; set; } = 1f;

    // Degrees, applied X then Y then Z: the base orientation fix-up that puts an arbitrarily authored model
    // upright in the game camera.
    public Vec3 Rotation { get; set; } = new();

    // Sprite pixels from the anchor, before any per-presentation placement. Moving the model here moves its
    // derived pivot by the same amount, which is exactly what it means.
    public Vec2 Offset { get; set; } = new();

    // The light is fixed to the screen, not the world (see ModelRenderer), so these are viewer-relative.
    public float LightYaw { get; set; } = -35f;
    public float LightPitch { get; set; } = 45f;
    public float Ambient { get; set; } = 0.35f;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class Vec2
{
    public float X { get; set; }
    public float Y { get; set; }
}

public sealed class Vec3
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}

public static class ItemTypes
{
    public static readonly IReadOnlyList<string> All = new[]
    {
        "one_handed_sword", "two_handed_sword", "axe", "hammer", "staff", "wand", "bow", "shield", "tool", "other",
    };
}
