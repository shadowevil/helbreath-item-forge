using System.Net;
using System.Text;
using System.Text.Json;
using ItemForge.Core;

namespace ItemForge.App;

// A minimal Model Context Protocol server over streamable HTTP, hand-rolled on HttpListener - no ASP.NET,
// no MCP SDK - the same design as the HBA Workbench's. Loopback-only, bearer token on every request. The
// tool list is static, so there is no server->client push and no SSE channel: every POST gets one
// application/json JSON-RPC reply. Implements initialize, notifications, ping, tools/list, tools/call.
public sealed class McpServer : IDisposable
{
    private readonly string _token;
    private readonly CommandRegistry _registry;
    private readonly Func<IntegrationMode> _mode;
    private readonly string _serverVersion;
    private readonly Action<string>? _log;
    private readonly HttpListener _listener = new();
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private CancellationTokenSource? _cts;

    public McpServer(int port, string token, CommandRegistry registry, Func<IntegrationMode> mode,
        string serverVersion, Action<string>? log)
    {
        _token = token;
        _registry = registry;
        _mode = mode;
        _serverVersion = serverVersion;
        _log = log;
        // The 127.0.0.1 literal (not '+' or a hostname) needs no HTTP.sys URL-ACL reservation and no elevation.
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener.Start();
        _ = AcceptLoop(_cts.Token);
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { if (_listener.IsListening) _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { break; } // listener stopped / disposed
            _ = Task.Run(() => HandleSafe(ctx));
        }
    }

    private async Task HandleSafe(HttpListenerContext ctx)
    {
        try { await Handle(ctx); }
        catch (Exception ex)
        {
            _log?.Invoke($"http error: {ex.Message}");
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;

        if (!string.Equals(req.Url?.AbsolutePath, "/mcp", StringComparison.Ordinal))
        {
            res.StatusCode = 404;
            res.Close();
            return;
        }

        if (!string.Equals(req.Headers["Authorization"], "Bearer " + _token, StringComparison.Ordinal))
        {
            res.StatusCode = 401;
            res.AddHeader("WWW-Authenticate", "Bearer");
            res.Close();
            return;
        }

        // No server-initiated stream: a GET (SSE open) is unsupported; a DELETE (session end) just 200s.
        if (req.HttpMethod == "DELETE") { res.StatusCode = 200; res.Close(); return; }
        if (req.HttpMethod != "POST") { res.StatusCode = 405; res.Close(); return; }

        string body;
        using (var sr = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
        {
            body = await sr.ReadToEndAsync();
        }

        object? responseObj;
        try
        {
            using var doc = JsonDocument.Parse(body);
            responseObj = await Dispatch(doc.RootElement);
        }
        catch (JsonException)
        {
            responseObj = Err(null, -32700, "parse error");
        }

        res.AddHeader("Mcp-Session-Id", _sessionId);
        if (responseObj is null)
        {
            res.StatusCode = 202; // notification: accepted, no body
            res.Close();
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(responseObj, JsonOpts.Compact));
        res.StatusCode = 200;
        res.ContentType = "application/json";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    private async Task<object?> Dispatch(JsonElement msg)
    {
        string? method = msg.TryGetProperty("method", out var m) ? m.GetString() : null;
        bool hasId = msg.TryGetProperty("id", out var idEl);
        object? id = hasId ? IdToObject(idEl) : null;
        JsonElement prms = msg.TryGetProperty("params", out var p) ? p : default;

        switch (method)
        {
            case "initialize":
            {
                string ver = prms.ValueKind == JsonValueKind.Object &&
                             prms.TryGetProperty("protocolVersion", out var pv) && pv.ValueKind == JsonValueKind.String
                    ? pv.GetString()!
                    : "2024-11-05";
                _log?.Invoke("client connected");
                return Ok(id, new
                {
                    protocolVersion = ver,
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = "helbreath-item-forge", version = _serverVersion },
                });
            }

            case "notifications/initialized":
            case "notifications/cancelled":
            case "notifications/roots/list_changed":
                return null;

            case "ping":
                return Ok(id, new { });

            case "tools/list":
            {
                var tools = _registry.ToolsFor(_mode())
                    .Select(t => new { name = t.Name, description = t.Description, inputSchema = t.InputSchema })
                    .ToArray();
                return Ok(id, new { tools });
            }

            case "tools/call":
                return await CallTool(id, prms);

            default:
                if (!hasId) return null;
                return Err(id, -32601, $"method not found: {method}");
        }
    }

    private async Task<object?> CallTool(object? id, JsonElement prms)
    {
        string name = prms.ValueKind == JsonValueKind.Object && prms.TryGetProperty("name", out var n)
            ? n.GetString() ?? "" : "";
        JsonElement args = prms.ValueKind == JsonValueKind.Object && prms.TryGetProperty("arguments", out var a)
            ? a : default;

        var tool = _registry.Get(name);
        if (tool is null || _mode() < tool.MinMode)
        {
            return Err(id, -32602, $"unknown or unavailable tool: {name}");
        }

        _log?.Invoke($"call {name}");
        try
        {
            var result = await tool.Handler(args);
            return Ok(id, new { content = result.Content, isError = result.IsError });
        }
        catch (Exception ex)
        {
            // Tool failures are reported IN the result (isError) so the agent sees the message and can adjust.
            return Ok(id, new
            {
                content = new object[] { new { type = "text", text = JsonSerializer.Serialize(new { ok = false, error = ex.Message }, JsonOpts.Pretty) } },
                isError = true,
            });
        }
    }

    private static object Ok(object? id, object result) =>
        new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static object Err(object? id, int code, string message) =>
        new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new Dictionary<string, object?> { ["code"] = code, ["message"] = message },
        };

    // JSON-RPC ids may be a number or a string; preserve the type so the echoed id matches.
    private static object? IdToObject(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.String => e.GetString(),
        _ => null,
    };
}
