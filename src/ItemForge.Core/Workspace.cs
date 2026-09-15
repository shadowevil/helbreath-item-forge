namespace ItemForge.Core;

// The workspace is the folder holding cards/, models/, reference/ and output/. It is found by its marker
// file, forge-workspace.json, which will also carry the settings every card shares (the game camera
// arrives with the renderer).
public sealed class Workspace
{
    public const string MarkerFile = "forge-workspace.json";

    public string Root { get; }

    private Workspace(string root) => Root = root;

    public string MarkerPath => Path.Combine(Root, MarkerFile);

    public static bool IsWorkspace(string dir) => File.Exists(Path.Combine(dir, MarkerFile));

    // A configured directory wins when it is a workspace; otherwise walk up from the start directory (the
    // exe folder, so a build or publish inside the repo finds the repo). Null when neither finds one.
    public static Workspace? Locate(string? configuredDir, string startDir)
    {
        if (!string.IsNullOrWhiteSpace(configuredDir))
        {
            string full = Path.GetFullPath(configuredDir);
            if (IsWorkspace(full))
            {
                return new Workspace(full);
            }
        }

        for (var dir = new DirectoryInfo(Path.GetFullPath(startDir)); dir is not null; dir = dir.Parent)
        {
            if (IsWorkspace(dir.FullName))
            {
                return new Workspace(dir.FullName);
            }
        }
        return null;
    }

    public static Workspace Open(string dir)
    {
        string full = Path.GetFullPath(dir);
        if (!IsWorkspace(full))
        {
            throw new DirectoryNotFoundException($"'{full}' has no {MarkerFile}");
        }
        return new Workspace(full);
    }

    // Makes a folder a workspace by writing the marker. Existing markers are left untouched.
    public static Workspace Create(string dir)
    {
        string full = Path.GetFullPath(dir);
        Directory.CreateDirectory(full);
        string marker = Path.Combine(full, MarkerFile);
        if (!File.Exists(marker))
        {
            File.WriteAllText(marker, "{\n  \"schema\": 1\n}\n");
        }
        return new Workspace(full);
    }

    // Workspace-relative with forward slashes when the file is inside the workspace (so a card is portable
    // between machines), otherwise the absolute path.
    public string ToStoredPath(string path)
    {
        string full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Root, path));
        string rel = Path.GetRelativePath(Root, full);
        bool inside = !Path.IsPathRooted(rel) && rel != ".." && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        return inside ? rel.Replace(Path.DirectorySeparatorChar, '/') : full;
    }

    public string ToFullPath(string stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return "";
        }
        return Path.IsPathRooted(stored)
            ? Path.GetFullPath(stored)
            : Path.GetFullPath(Path.Combine(Root, stored.Replace('/', Path.DirectorySeparatorChar)));
    }

    public bool IsInside(string stored) => !string.IsNullOrEmpty(stored) && !Path.IsPathRooted(stored);
}
