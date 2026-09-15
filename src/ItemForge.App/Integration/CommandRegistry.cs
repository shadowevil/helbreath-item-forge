using System.Text.Json;
using ItemForge.Core;

namespace ItemForge.App;

// Permission level of the integration. Off = server stopped. Observe = read + navigate + screenshots.
// Full = content mutation / save / delete / disk writes. Ordered so a tool's MinMode gates it.
public enum IntegrationMode
{
    Off = 0,
    Observe = 1,
    Full = 2,
}

// The result of a tool call: an MCP content array (text and/or image blocks) plus an error flag.
public sealed record ToolResult(IReadOnlyList<object> Content, bool IsError = false)
{
    public static ToolResult Text(string text) => new(new object[] { new { type = "text", text } });

    public static ToolResult Json(object data) =>
        new(new object[] { new { type = "text", text = JsonSerializer.Serialize(data, JsonOpts.Pretty) } });

    // Errors are JSON like every other reply, so a caller parsing tool output never special-cases prose.
    public static ToolResult Err(string message) =>
        new(new object[] { new { type = "text", text = JsonSerializer.Serialize(new { ok = false, error = message }, JsonOpts.Pretty) } }, true);
}

// One registered tool: its MCP name / description / JSON-Schema, the minimum mode that unlocks it, and the
// handler (given the raw `arguments` element).
public sealed record ToolDef(
    string Name,
    string Description,
    object InputSchema,
    IntegrationMode MinMode,
    Func<JsonElement, Task<ToolResult>> Handler);

// The single source of truth for the tool surface: it generates tools/list (mode-filtered) and dispatches
// tools/call. Adding a tool is one Register() call.
public sealed class CommandRegistry
{
    private readonly Dictionary<string, ToolDef> _tools = new(StringComparer.Ordinal);

    public void Register(ToolDef def) => _tools[def.Name] = def;

    public IReadOnlyList<ToolDef> ToolsFor(IntegrationMode mode) =>
        _tools.Values.Where(t => mode >= t.MinMode).OrderBy(t => t.Name, StringComparer.Ordinal).ToList();

    public ToolDef? Get(string name) => _tools.TryGetValue(name, out var d) ? d : null;
}
