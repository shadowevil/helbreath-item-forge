using System.Text.Json;

namespace ItemForge.App;

// Portable settings persisted as settings.json NEXT TO THE EXE (AppContext.BaseDirectory), so the tool
// stays self-contained to its runtime folder - no AppData, no temp. Same design as the HBA Workbench.
public sealed class Settings
{
    public string Theme { get; set; } = "System"; // System | Light | Dark

    // Workspace folder (holds forge-workspace.json). Empty = find it by walking up from the exe folder.
    public string WorkspaceDir { get; set; } = "";

    // Claude Code integration (togglable local MCP control surface). The Workbench owns port 4000, the
    // kanban board 8787 and the Helbreath client bridge 8791, so the forge defaults to 4001.
    public string IntegrationMode { get; set; } = "Off"; // Off | Observe | Full
    public int IntegrationPort { get; set; } = 4001;
    public string IntegrationToken { get; set; } = "";

    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
        }
        catch
        {
            // corrupt / unreadable settings fall back to defaults
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // best-effort; a read-only folder just means settings don't persist
        }
    }
}
