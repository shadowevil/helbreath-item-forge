namespace ItemForge.Core.Cards;

public enum ModelStatus
{
    None,    // no model bound yet
    Ok,      // file present and matches the bound hash
    Missing, // bound path does not exist on this machine
    Changed, // file present but its hash differs from the one bound
}

// Binding a model file to a card, and checking that binding later.
public static class ModelBinding
{
    public static readonly IReadOnlyList<string> Extensions = new[] { ".glb", ".gltf" };

    // Points the card at a model file and records its hash. Returns an error message, or null on success.
    public static string? Bind(Card card, Workspace workspace, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "model path is empty";
        }
        string full = workspace.ToFullPath(path.Trim());
        if (!File.Exists(full))
        {
            return $"model file not found: {full}";
        }
        string ext = System.IO.Path.GetExtension(full).ToLowerInvariant();
        if (!Extensions.Contains(ext))
        {
            return $"unsupported model type '{ext}' (expected {string.Join(", ", Extensions)})";
        }
        card.Model.Path = workspace.ToStoredPath(full);
        card.Model.Sha256 = FileHash.Sha256(full);
        return null;
    }

    public static void Clear(Card card)
    {
        card.Model.Path = "";
        card.Model.Sha256 = "";
    }

    public static ModelStatus Status(CardModel model, Workspace workspace)
    {
        if (string.IsNullOrEmpty(model.Path))
        {
            return ModelStatus.None;
        }
        string full = workspace.ToFullPath(model.Path);
        if (!File.Exists(full))
        {
            return ModelStatus.Missing;
        }
        if (!string.IsNullOrEmpty(model.Sha256) &&
            !string.Equals(FileHash.Sha256(full), model.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return ModelStatus.Changed;
        }
        return ModelStatus.Ok;
    }
}
