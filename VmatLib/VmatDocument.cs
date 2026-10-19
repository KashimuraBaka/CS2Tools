namespace VmatLib;

/// <summary>
/// A strongly-typed summary of a parsed VMAT file, surfaced to the UI layer.
///
/// Tree shape (as produced by <see cref="KvAdapter.Parse"/> and used by
/// <see cref="VmatGenerator.BuildDocument"/>):
///   root (Key = "Layer0")
///   ├── "shader"          -> "csgo_environment.vfx"
///   ├── "F_TRANSLUCENT"   -> "0"
///   ├── ...
///   ├── "Compiled Textures" { "g_tColor" -> "...vtex", ... }
///   └── "SystemAttributes" { "PhysicsSurfaceProperties" -> "plaster" }
/// </summary>
/// <inheritdoc/>
public sealed class VmatDocument(string filePath, VmatNode root)
{

    /// <inheritdoc/>
    public string FilePath { get; } = filePath;
    /// <inheritdoc/>
    public VmatNode Root { get; } = root;
    /// <inheritdoc/>
    public string ShaderName { get; } = root.Children
            .FirstOrDefault(c => string.Equals(c.Key, "shader", StringComparison.Ordinal))?.Value
            ?? string.Empty;

    /// <inheritdoc/>
    public string DisplayName => string.IsNullOrEmpty(FilePath) ? "(new)" : Path.GetFileName(FilePath);

    /// <summary>Top-level keys of <c>Layer0</c>: feature flags, parameters, sub-blocks.</summary>
    public IEnumerable<VmatNode> Layers => Root.Children;

    /// <summary>The <c>Layer0</c> node itself (the root of the VMAT tree).</summary>
    public VmatNode Layer0 => Root;
}