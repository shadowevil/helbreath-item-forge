using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ItemForge.Core;

// Shared serializer settings for card files and the MCP wire. camelCase, nulls omitted, and no aggressive
// HTML escaping - these are data files and a loopback JSON API, not HTML output.
public static class JsonOpts
{
    public static readonly JsonSerializerOptions Compact = Build(indented: false);
    public static readonly JsonSerializerOptions Pretty = Build(indented: true);

    private static JsonSerializerOptions Build(bool indented) => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = indented,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };
}
