using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ItemForge.Core;

namespace ItemForge.App;

// What a secondary launch forwards to the running primary: card files to open and whether to ensure the MCP
// server is on. The primary replies with its live MCP connection details.
public sealed record ForwardRequest(string[] Files, bool EnableMcp);

public sealed record ForwardResponse(
    bool Ok, string[] Opened, bool McpEnabled,
    string? Url, string? Token, string? ConnectCommand, string? McpJson, string Message);

// Makes the forge single-instance (the HBA Workbench design): the first launch owns a per-install lock and
// hosts a named pipe; later launches forward their args to it instead of spawning a duplicate that would
// collide on the MCP port. Keyed by the exe directory so separate copies don't interfere.
public static class SingleInstance
{
    private const string AppName = "Helbreath Item Forge";
    private static Mutex? _mutex;
    private static readonly string Id = HashId(AppContext.BaseDirectory);
    private static string MutexName => $"Local\\helbreath_item_forge_{Id}";
    private static string PipeName => $"helbreath_item_forge_{Id}";

    private static string HashId(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.ToLowerInvariant())), 0, 8);

    // True if this process is the primary instance (and now holds the lock for its lifetime).
    public static bool ClaimPrimary()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
        }
        return createdNew;
    }

    // Secondary path: forward args to the primary and return an agent-readable report. Retries briefly so a
    // just-launched primary has time to bring its pipe server up.
    public static string ForwardToPrimary(string[] args)
    {
        var files = new List<string>();
        bool enableMcp = false;
        foreach (var a in args)
        {
            if (string.Equals(a, "--mcp", StringComparison.OrdinalIgnoreCase)) { enableMcp = true; continue; }
            if (!a.StartsWith("--", StringComparison.Ordinal) && File.Exists(a)) files.Add(Path.GetFullPath(a));
        }
        var request = new ForwardRequest(files.ToArray(), enableMcp);

        Exception? last = null;
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                client.Connect(1000);
                WriteFrame(client, JsonSerializer.SerializeToUtf8Bytes(request, JsonOpts.Compact));
                client.Flush();
                var resp = JsonSerializer.Deserialize<ForwardResponse>(ReadFrame(client), JsonOpts.Compact);
                return Report(resp);
            }
            catch (Exception ex)
            {
                last = ex;
                Thread.Sleep(250);
            }
        }
        return $"An instance of {AppName} appears to be running but could not be reached ({last?.Message}). Not launching a second window.";
    }

    // Primary: host the pipe server on a background thread, handing each request to the app.
    public static void StartServer(Func<ForwardRequest, ForwardResponse> handler)
    {
        var thread = new Thread(() => ServerLoop(handler)) { IsBackground = true, Name = "single-instance" };
        thread.Start();
    }

    private static void ServerLoop(Func<ForwardRequest, ForwardResponse> handler)
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                server.WaitForConnection();
                var req = JsonSerializer.Deserialize<ForwardRequest>(ReadFrame(server), JsonOpts.Compact)
                          ?? new ForwardRequest(Array.Empty<string>(), false);
                ForwardResponse resp;
                try { resp = handler(req); }
                catch (Exception ex)
                {
                    resp = new ForwardResponse(false, Array.Empty<string>(), false, null, null, null, null, "handler error: " + ex.Message);
                }
                WriteFrame(server, JsonSerializer.SerializeToUtf8Bytes(resp, JsonOpts.Compact));
                server.Flush();
                try { if (OperatingSystem.IsWindows()) server.WaitForPipeDrain(); } catch { }
            }
            catch
            {
                // Connection dropped or malformed; wait for the next launch.
            }
        }
    }

    // ASCII only: this goes to a console that may not be UTF-8.
    private static string Report(ForwardResponse? r)
    {
        if (r is null)
        {
            return $"Forwarded to the running {AppName}, but got no reply.";
        }
        var sb = new StringBuilder();
        sb.AppendLine($"An instance of {AppName} is already running; forwarded to it.");
        sb.AppendLine(r.Opened.Length > 0 ? $"Opened: {string.Join(", ", r.Opened)}" : "No cards opened.");
        sb.AppendLine();
        if (r.McpEnabled)
        {
            sb.AppendLine("The MCP server is ENABLED on that instance - connect to it:");
            sb.AppendLine($"  URL:   {r.Url}");
            sb.AppendLine($"  Token: {r.Token}");
            sb.AppendLine();
            sb.AppendLine("  Register with Claude Code:");
            sb.AppendLine($"    {r.ConnectCommand}");
            sb.AppendLine();
            sb.AppendLine("  Or add to your project's .mcp.json:");
            sb.AppendLine(r.McpJson);
        }
        else
        {
            sb.AppendLine(r.Message);
        }
        return sb.ToString().TrimEnd();
    }

    private static void WriteFrame(Stream s, byte[] payload)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, payload.Length);
        s.Write(len);
        s.Write(payload, 0, payload.Length);
    }

    private static byte[] ReadFrame(Stream s)
    {
        Span<byte> len = stackalloc byte[4];
        ReadExact(s, len);
        int n = BinaryPrimitives.ReadInt32LittleEndian(len);
        if (n < 0 || n > 8 * 1024 * 1024)
        {
            throw new InvalidDataException("frame too large");
        }
        var buf = new byte[n];
        ReadExact(s, buf);
        return buf;
    }

    private static void ReadExact(Stream s, Span<byte> buf)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int r = s.Read(buf.Slice(off));
            if (r <= 0)
            {
                throw new EndOfStreamException();
            }
            off += r;
        }
    }
}
