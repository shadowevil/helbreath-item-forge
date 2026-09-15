using System.Reflection;
using System.Text.Json;

namespace ItemForge.App;

// Owns the lifecycle of the Claude Code integration: the MCP server, the permission mode, the port and the
// token. Tools are registered once; tools/list and tools/call are filtered by the live mode. Every feature
// of the forge is drivable here (item-forge-mcp.md is the agent guide).
public sealed class IntegrationController : IDisposable
{
    private const string ServerName = "helbreath-item-forge";

    private static readonly object EmptySchema = new { type = "object", properties = new { } };

    private readonly IAutomationHost _host;
    private readonly CommandRegistry _registry = new();
    private readonly string _version;
    private McpServer? _server;

    public IntegrationMode Mode { get; private set; } = IntegrationMode.Off;
    public int Port { get; }
    public string Token { get; }
    public bool IsRunning => _server is not null;

    public event Action? Changed;
    public event Action<string>? Log;

    public IntegrationController(IAutomationHost host, int port, string token)
    {
        _host = host;
        Port = port;
        Token = token;
        _version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
        RegisterTools();
    }

    public string ConnectCommand =>
        $"claude mcp add --transport http {ServerName} http://127.0.0.1:{Port}/mcp " +
        $"--header \"Authorization: Bearer {Token}\"";

    public string McpJsonConfig => $$"""
        {
          "mcpServers": {
            "{{ServerName}}": {
              "type": "http",
              "url": "http://127.0.0.1:{{Port}}/mcp",
              "headers": { "Authorization": "Bearer {{Token}}" }
            }
          }
        }
        """;

    // Off stops the server; any other mode ensures it is running. Returns an error if it could not start.
    public string? SetMode(IntegrationMode mode)
    {
        if (mode == IntegrationMode.Off)
        {
            Stop();
            return null;
        }

        if (_server is null)
        {
            try
            {
                var server = new McpServer(Port, Token, _registry, () => Mode, _version, s => Log?.Invoke(s));
                server.Start();
                _server = server;
            }
            catch (Exception ex)
            {
                Mode = IntegrationMode.Off;
                Changed?.Invoke();
                return ex.Message;
            }
        }

        Mode = mode;
        Log?.Invoke($"{mode} mode - http://127.0.0.1:{Port}/mcp");
        Changed?.Invoke();
        return null;
    }

    public void Stop()
    {
        if (_server is not null)
        {
            _server.Dispose();
            _server = null;
            Log?.Invoke("stopped");
        }
        Mode = IntegrationMode.Off;
        Changed?.Invoke();
    }

    public void Dispose() => Stop();

    private void RegisterTools()
    {
        // --- observe ---

        Register("ping",
            "Health check: confirms the Item Forge integration is reachable and reports its version and current permission mode.",
            EmptySchema, IntegrationMode.Observe,
            _ => Task.FromResult(ToolResult.Json(new { ok = true, app = ServerName, version = _version, mode = Mode.ToString() })));

        Register("get_state",
            "Structured snapshot of the forge: workspace folder, card count, the current view (gallery / card / none), the open card (id, name, dirty flag), integration mode + port, open windows and settings. This is the primary way to 'see' the app - call it before and after acting instead of relying on screenshots.",
            EmptySchema, IntegrationMode.Observe,
            async _ => ToolResult.Json(await _host.GetStateAsync()));

        Register("list_cards",
            "Re-read cards/ from disk and list every card: id, name, itemType, modelPath, modelStatus (None / Ok / Missing / Changed), modified, file path, and an error when the file is unreadable.",
            EmptySchema, IntegrationMode.Observe,
            async _ => ToolResult.Json(await _host.ListCardsAsync()));

        Register("get_card",
            "One card in full: its summary plus the complete card JSON. When the card is open, the JSON is the UNSAVED working copy and 'dirty' says whether it differs from disk.",
            Schema(("id", "string", "Card id (the file name in cards/ without .json).", true)),
            IntegrationMode.Observe,
            async a =>
            {
                string? id = Str(a, "id");
                if (id is null) return ToolResult.Err("'id' is required.");
                var detail = await _host.GetCardAsync(id);
                return detail is null ? ToolResult.Err($"no card '{id}'") : ToolResult.Json(detail);
            });

        Register("screenshot",
            "Capture a PNG of app windows. 'target': 'main' (default), 'all', or a window-title substring. By default the PNGs come back as inline image blocks; pass 'destFile' (an absolute .png path, Full mode only) to write them to disk and get {ok, files:[{path,width,height}]} instead. With several windows and a destFile, later files get _2, _3 ... suffixes.",
            Schema(("target", "string", "main | all | window-title substring.", false), ("destFile", "string", "Absolute .png path to write instead of returning inline images (Full mode).", false)),
            IntegrationMode.Observe,
            ScreenshotAsync);

        Register("show_gallery",
            "Switch the editor area to the card gallery (the open card, if any, stays open in its tab).",
            EmptySchema, IntegrationMode.Observe,
            async _ => AsResult(await _host.ShowGalleryAsync()));

        Register("open_card",
            "Open a card in the editor (its Details tab). Refused while another open card has unsaved changes.",
            Schema(("id", "string", "Card id.", true)),
            IntegrationMode.Observe,
            async a => Str(a, "id") is { } id ? AsResult(await _host.OpenCardAsync(id)) : ToolResult.Err("'id' is required."));

        Register("get_model_info",
            "Everything the Model tab knows about the open card: the loaded geometry (triangles, vertices, bounds, texture size and load time), the base setup (scale, rotation, light, ambient), the live view (tab, direction, zoom) and the last render's stats. Pass 'id' to open that card first.",
            Schema(("id", "string", "Card to inspect; defaults to the open card.", false)),
            IntegrationMode.Observe,
            async a => AsResult(await _host.GetModelInfoAsync(Str(a, "id"))));

        Register("set_card_tab",
            "Switch the open card to a tab: 'Details' or 'Model'. The presentation tabs are locked until their phase is built.",
            Schema(("tab", "string", "Details | Model", true)),
            IntegrationMode.Observe,
            async a => Str(a, "tab") is { } tab ? AsResult(await _host.SetCardTabAsync(tab)) : ToolResult.Err("'tab' is required."));

        Register("set_model_view",
            "Point the live 3D view: 'direction' 0..7 (0 = north, clockwise, 45 degrees a step, the client's order) and 'zoom' 1..8 (whole-pixel magnification; the pixels are the bake's own, never smoothed).",
            Schema(("direction", "integer", "Facing 0..7.", false), ("zoom", "integer", "1..8.", false)),
            IntegrationMode.Observe,
            async a => AsResult(await _host.SetModelViewAsync(Int(a, "direction"), Int(a, "zoom"))));

        Register("render_model",
            "Render the card's model through the game camera, off screen, with the card's own setup - the model ALONE on transparency, never the character sprite. Returns the cost (triangles, ms, supersample), the tight opaque box and the pivot derived from it, plus the PNG inline; pass 'destFile' (absolute .png, Full mode) to write it to disk instead.",
            Schema(
                ("id", "string", "Card to render; defaults to the open card.", false),
                ("direction", "integer", "Facing 0..7; defaults to the live view's.", false),
                ("size", "integer", "Frame size in pixels (16..1024, default 160).", false),
                ("supersample", "integer", "1..8, default 4. 1 is what the live view shows.", false),
                ("destFile", "string", "Absolute .png path to write instead of returning an inline image (Full mode).", false)),
            IntegrationMode.Observe,
            RenderModelAsync);

        Register("close_card",
            "Close the open card and return to the gallery. Refused when it has unsaved changes unless 'discard' is true (discarding needs Full mode).",
            Schema(("discard", "boolean", "Throw away unsaved changes (Full mode only).", false)),
            IntegrationMode.Observe,
            async a =>
            {
                bool discard = Bool(a, "discard");
                if (discard && Mode < IntegrationMode.Full) return ToolResult.Err("discard:true needs Full mode.");
                return AsResult(await _host.CloseCardAsync(discard));
            });

        // --- full ---

        Register("create_card",
            "Create a new card file cards/<id>.json from a display name (the id is derived: lowercase, _ separated, made unique). Opens it unless 'open' is false or another card has unsaved changes.",
            Schema(("name", "string", "Display name, e.g. 'Dark Angel Sword'.", true), ("open", "boolean", "Open it in the editor (default true).", false)),
            IntegrationMode.Full,
            async a =>
            {
                string? name = Str(a, "name");
                if (string.IsNullOrWhiteSpace(name)) return ToolResult.Err("'name' is required.");
                bool open = !Has(a, "open") || Bool(a, "open");
                return AsResult(await _host.CreateCardAsync(name, open));
            });

        Register("update_card",
            "Edit the open card's working copy exactly as the Details tab does (the card becomes dirty; save_card writes it). Pass 'id' to open that card first. Only the fields you pass change. 'modelPath' binds a .glb/.gltf (absolute, or relative to the workspace) and records its SHA-256; an empty string clears the binding. All fields are validated before any is applied.",
            new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "string", description = "Card to edit; defaults to the open card." },
                    name = new { type = "string", description = "Display name." },
                    notes = new { type = "string", description = "Free-form notes." },
                    itemType = new { type = "string", description = "one_handed_sword, two_handed_sword, axe, hammer, staff, wand, bow, shield, tool, other - or '' to clear." },
                    modelPath = new { type = "string", description = "Model file to bind; '' clears it." },
                },
            },
            IntegrationMode.Full,
            async a => AsResult(await _host.UpdateCardAsync(new CardUpdate(
                Str(a, "id"), Str(a, "name"), Str(a, "notes"), Str(a, "itemType"), Str(a, "modelPath")))));

        Register("update_model_setup",
            "Edit the card's base model fix-up - the scale, orientation and light every presentation inherits - exactly as the Model tab does (the card becomes dirty; save_card writes it). 'fit' sizes the model to the frame first. Only the fields you pass change, and all are validated before any is applied.",
            new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "string", description = "Card to edit; defaults to the open card." },
                    fit = new { type = "boolean", description = "Set the scale so the model fills about 80% of the frame." },
                    scale = new { type = "number", description = "World units per model unit; greater than 0." },
                    rotationX = new { type = "number", description = "Base fix-up, degrees (applied X then Y then Z)." },
                    rotationY = new { type = "number", description = "Base fix-up, degrees." },
                    rotationZ = new { type = "number", description = "Base fix-up, degrees." },
                    lightYaw = new { type = "number", description = "Light yaw in degrees, fixed to the screen." },
                    lightPitch = new { type = "number", description = "Light pitch, -90..90." },
                    ambient = new { type = "number", description = "Ambient light, 0..1." },
                },
            },
            IntegrationMode.Full,
            async a => AsResult(await _host.UpdateModelSetupAsync(new ModelSetupUpdate(
                Str(a, "id"), Bool(a, "fit"), Num(a, "scale"),
                Num(a, "rotationX"), Num(a, "rotationY"), Num(a, "rotationZ"),
                Num(a, "lightYaw"), Num(a, "lightPitch"), Num(a, "ambient")))));

        Register("save_card",
            "Write the open card to cards/<id>.json (atomic replace). Returns the file path and the new modified time.",
            EmptySchema, IntegrationMode.Full,
            async _ => AsResult(await _host.SaveCardAsync()));

        Register("duplicate_card",
            "Copy a card (every adjustment included) under a new name; the copy gets its own id and file.",
            Schema(("id", "string", "Card to copy.", true), ("name", "string", "Display name for the copy.", true)),
            IntegrationMode.Full,
            async a => Str(a, "id") is { } id && Str(a, "name") is { } name
                ? AsResult(await _host.DuplicateCardAsync(id, name))
                : ToolResult.Err("'id' and 'name' are required."));

        Register("delete_card",
            "Move a card file to cards/.trash/ (not destroyed; the model file is never touched). If it is the open card it is closed first and unsaved edits are lost.",
            Schema(("id", "string", "Card to delete.", true)),
            IntegrationMode.Full,
            async a => Str(a, "id") is { } id ? AsResult(await _host.DeleteCardAsync(id)) : ToolResult.Err("'id' is required."));

        Register("set_setting",
            "Change a persisted setting: 'theme' (System | Light | Dark) or 'workspaceDir' (a folder holding forge-workspace.json; reloads the workspace).",
            Schema(("key", "string", "theme | workspaceDir", true), ("value", "string", "New value.", true)),
            IntegrationMode.Full,
            async a => Str(a, "key") is { } key && Str(a, "value") is { } value
                ? AsResult(await _host.SetSettingAsync(key, value))
                : ToolResult.Err("'key' and 'value' are required."));

        Register("quit",
            "Close the app cleanly (use this, never taskkill). Refused when the open card has unsaved changes unless 'discard' is true.",
            Schema(("discard", "boolean", "Throw away unsaved changes.", false)),
            IntegrationMode.Full,
            async a => AsResult(await _host.QuitAsync(Bool(a, "discard"))));
    }

    private async Task<ToolResult> ScreenshotAsync(JsonElement a)
    {
        string? destFile = Str(a, "destFile");
        if (destFile is not null && Mode < IntegrationMode.Full)
        {
            return ToolResult.Err("'destFile' writes to disk and needs Full mode.");
        }
        if (destFile is not null && (!Path.IsPathRooted(destFile) || !destFile.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
        {
            return ToolResult.Err("'destFile' must be an absolute path ending in .png.");
        }

        var shot = await _host.ScreenshotAsync(Str(a, "target"));
        if (shot.Images.Count == 0)
        {
            return ToolResult.Err($"no window matched '{shot.Target}'");
        }

        if (destFile is null)
        {
            var content = new List<object>();
            foreach (var img in shot.Images)
            {
                content.Add(new { type = "text", text = $"{img.Title} ({img.Width}x{img.Height})" });
                content.Add(new { type = "image", data = img.Base64Png, mimeType = "image/png" });
            }
            return new ToolResult(content);
        }

        var files = new List<object>();
        string dir = Path.GetDirectoryName(destFile)!;
        Directory.CreateDirectory(dir);
        for (int i = 0; i < shot.Images.Count; i++)
        {
            var img = shot.Images[i];
            string path = i == 0 ? destFile : Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(destFile)}_{i + 1}.png");
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(img.Base64Png));
            files.Add(new { path, width = img.Width, height = img.Height });
        }
        return ToolResult.Json(new { ok = true, files });
    }

    private async Task<ToolResult> RenderModelAsync(JsonElement a)
    {
        string? destFile = Str(a, "destFile");
        if (destFile is not null && Mode < IntegrationMode.Full)
        {
            return ToolResult.Err("'destFile' writes to disk and needs Full mode.");
        }
        if (destFile is not null && (!Path.IsPathRooted(destFile) || !destFile.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
        {
            return ToolResult.Err("'destFile' must be an absolute path ending in .png.");
        }

        var result = await _host.RenderModelAsync(new RenderModelArgs(
            Str(a, "id"), Int(a, "direction"), Int(a, "size"), Int(a, "supersample")));
        if (!result.Ok || result.Data is not RenderPayload payload)
        {
            return ToolResult.Err(result.Error ?? "render failed");
        }

        if (destFile is null)
        {
            return new ToolResult(new List<object>
            {
                new { type = "text", text = JsonSerializer.Serialize(new { ok = true, stats = payload.Stats, width = payload.Width, height = payload.Height }) },
                new { type = "image", data = payload.Base64Png, mimeType = "image/png" },
            });
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
        await File.WriteAllBytesAsync(destFile, Convert.FromBase64String(payload.Base64Png));
        return ToolResult.Json(new { ok = true, file = destFile, width = payload.Width, height = payload.Height, stats = payload.Stats });
    }

    // --- helpers ---

    private void Register(string name, string description, object schema, IntegrationMode minMode, Func<JsonElement, Task<ToolResult>> handler) =>
        _registry.Register(new ToolDef(name, description, schema, minMode, handler));

    private static object Schema(params (string Name, string Type, string Description, bool Required)[] props)
    {
        var properties = new Dictionary<string, object>();
        foreach (var p in props)
        {
            properties[p.Name] = new { type = p.Type, description = p.Description };
        }
        var required = props.Where(p => p.Required).Select(p => p.Name).ToArray();
        return required.Length == 0
            ? new { type = "object", properties }
            : new { type = "object", properties, required };
    }

    private static ToolResult AsResult(OpResult r) =>
        r.Ok ? ToolResult.Json(new { ok = true, data = r.Data }) : ToolResult.Err(r.Error ?? "failed");

    private static bool Has(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out _);

    private static string? Str(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int? Int(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)
            ? i
            : null;

    private static float? Num(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d)
            ? (float)d
            : null;
}
