using System.Text;

namespace VmatLib;

/// <summary>
/// Declarative description of a shader parameter: key, display label, type, and
/// (for vectors/scalars) default value. Used to drive the editor UI.
/// </summary>
public enum ShaderParamKind
{
    /// <inheritdoc/>
    Float,
    /// <inheritdoc/>
    Int,
    /// <inheritdoc/>
    Bool,
    /// <inheritdoc/>
    String,
    /// <inheritdoc/>
    Vector,
    /// <inheritdoc/>
    Texture,
}

/// <summary>
/// Marker used by the editor to decide whether the key is a "texture or vec4 constant"
/// (e.g. TextureColor / TextureRoughness). The library still stores a single default,
/// but the UI may offer a toggle.
/// </summary>
public enum ShaderValueShape
{
    /// <inheritdoc/>
    Scalar,
    /// <inheritdoc/>
    Vector,
    /// <inheritdoc/>
    Texture,
    /// <inheritdoc/>
    TextureOrVector,
}

/// <summary>
/// 模板 <c>Attributes</c> 块里的一条键值（逐字保留，写出时原样回放）。
/// </summary>
/// <param name="Key">属性键，如 <c>mapbuilder.nodraw</c>。</param>
/// <param name="Value">属性值，通常是 <c>"1"</c>。</param>
public sealed record ShaderAttribute(string Key, string Value);

/// <summary>
/// Declarative description of a single shader parameter.
/// </summary>
/// <inheritdoc/>
public sealed class ShaderParamTemplate(string key, string display, ShaderParamKind kind, string defaultValue = "")
{

    /// <inheritdoc/>
    public string Key { get; } = key;
    /// <inheritdoc/>
    public string Display { get; } = display;
    /// <inheritdoc/>
    public ShaderParamKind Kind { get; } = kind;
    /// <inheritdoc/>
    public string DefaultValue { get; } = defaultValue;

    /// <summary>
    /// Optional feature flag that gates this parameter in the editor. When <c>null</c>
    /// the parameter is always shown; otherwise it is only displayed when that flag is
    /// enabled. Used for "subgroup" keys like the detail-blend or phong-spec groups.
    /// </summary>
    public string? RequiredFeatureFlag { get; init; }

    /// <summary>
    /// UI hint for how the value should be rendered (scalar/texture/vector hybrid).
    /// </summary>
    public ShaderValueShape Shape { get; init; } = ShaderValueShape.Scalar;
}

/// <summary>
/// Declarative description of a single shader. The <see cref="ShaderName"/> is the
/// value that ends up in the <c>"shader"</c> property of the VMAT <c>Layer0</c>.
/// </summary>
/// <inheritdoc/>
public sealed class ShaderTemplate(
    string shaderName,
    string displayName,
    string description,
    IReadOnlyList<ShaderParamTemplate> parameters,
    IReadOnlyList<string> featureFlags,
    IReadOnlyList<string>? attributeFlags = null,
    IReadOnlyDictionary<string, string>? systemAttributeDefaults = null,
    IReadOnlyList<string>? compiledTextureKeys = null)
{

    /// <inheritdoc/>
    public string ShaderName { get; } = shaderName;
    /// <inheritdoc/>
    public string DisplayName { get; } = displayName;
    /// <inheritdoc/>
    public string Description { get; } = description;
    /// <inheritdoc/>
    public IReadOnlyList<ShaderParamTemplate> Parameters { get; } = parameters;
    /// <inheritdoc/>
    public IReadOnlyList<string> FeatureFlags { get; } = featureFlags;

    /// <summary>
    /// 描述性分类标签（历史遗留的展示元数据）。<b>不再参与写出</b>：
    /// <c>Attributes</c> 块现在逐字取自模板解析结果
    /// <see cref="TemplateAttributes"/>，避免目录与模板各说各话。
    /// </summary>
    public IReadOnlyList<string> AttributeFlags { get; init; } = attributeFlags ?? [];

    /// <summary>内嵌模板资源基名（不带 <c>.vmat</c> / <c>.vfx</c>），默认取 shader 值去后缀。</summary>
    public string TemplateResourceName { get; init; } = string.Empty;

    /// <summary>
    /// 每个 feature flag 在模板里的出厂值（<c>"0"</c> 或 <c>"1"</c>）。
    /// 生成时未显式启用的 flag 按该值写出——「合并后的设置保持默认值」这条
    /// 合并规则的落点；模板没给的 flag 出厂视为 <c>"0"</c>。
    /// </summary>
    public IReadOnlyDictionary<string, string> FeatureFlagDefaults { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// 模板 <c>Attributes</c> 块的原样内容（键与值都逐字保留）。
    /// 生成时按序原样写出；为空时不写该块。
    /// </summary>
    public IReadOnlyList<ShaderAttribute> TemplateAttributes { get; init; } = [];

    /// <summary>
    /// Default values for the <c>SystemAttributes</c> sub-block emitted alongside the
    /// shader parameters. Common keys: <c>PhysicsSurfaceProperties</c>,
    /// <c>LightMapTextureName</c>, <c>DetailTexture</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> SystemAttributeDefaults { get; init; } = systemAttributeDefaults ?? new Dictionary<string, string>();

    /// <summary>
    /// Names of the compiled-texture placeholders that should be emitted even when the
    /// user has not supplied a value. Prevents the editor from accidentally stripping
    /// the Source 2 compiler output keys.
    /// </summary>
    public IReadOnlyList<string> CompiledTextureKeys { get; init; } = compiledTextureKeys ?? [];

    /// <inheritdoc/>
    public override string ToString() => $"{DisplayName} ({ShaderName})";

    /// <summary>
    /// Helper for emitting templates: flatten every parameter (including subgroups) into
    /// a dictionary keyed by Key with the default value. Used by the emitter to render
    /// a baseline VMAT.
    /// </summary>
    public IDictionary<string, string> BuildDefaultValueMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in Parameters)
        {
            if (string.IsNullOrEmpty(p.DefaultValue)) continue;
            // If a subgroup key is gated on a flag that is off, still emit it so the
            // default template shows the contract, but the UI can hide it.
            map[p.Key] = p.DefaultValue;
        }
        return map;
    }

    /// <summary>
    /// Render a single-line summary used in the editor nav pane. (Pure helper.)
    /// </summary>
    public string RenderSummary()
    {
        var sb = new StringBuilder();
        sb.Append(DisplayName).Append(" (").Append(ShaderName).Append(") — ");
        sb.Append(Parameters.Count).Append(" params, ");
        sb.Append(FeatureFlags.Count).Append(" features");
        return sb.ToString();
    }
}
