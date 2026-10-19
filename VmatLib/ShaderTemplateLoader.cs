namespace VmatLib;

/// <summary>
/// 内嵌模板文本 → <see cref="ShaderTemplate"/> 零件的解析器。
///
/// <para><b>唯一真值来源。</b>参数键 / 默认值 / feature flag 及出厂值 /
/// SystemAttributes / Attributes / Compiled Textures 全部从模板 KV 文本逐字解析；
/// <see cref="ShaderCatalog"/> 只在其上叠加模板表达不了的展示元数据（中文名、标签、门控）。</para>
///
/// <para><b>键的分类规则</b>（与 Source 2 材质编辑器的命名约定一致）：</para>
/// <list type="bullet">
///   <item><description><c>shader</c> —— 着色器指令值，逐字保留。</description></item>
///   <item><description><c>F_*</c> —— feature flag；裸无引号值（<c>0</c> / <c>1</c>）即出厂值。</description></item>
///   <item><description><c>g_b*</c> → Bool，<c>g_n*</c> → Int，<c>g_fl*</c> / <c>g_f*</c> → Float，
///   <c>g_v*</c> → Vector，其余 <c>g_*</c> → String。</description></item>
///   <item><description><c>Texture*</c>（含 <c>SkyTexture</c> 之类的后缀变体）→ Kind=Texture；
///   默认值以 <c>[</c> 开头的按「贴图或常量向量」对待（Shape=TextureOrVector），
///   否则 Shape=Texture。<c>SkyTexture</c> 不以 <c>Texture</c> 开头，因此天然
///   不是贴图槽位候选（§5.1 条件①）。</description></item>
///   <item><description>嵌套块：<c>SystemAttributes</c> / <c>Attributes</c> /
///   <c>Compiled Textures</c> 分别收集，其余块（<c>VariableState</c>、
///   <c>UnusedVariables</c> 等编辑器状态）忽略。</description></item>
/// </list>
/// </summary>
internal static class TemplateParser
{
    /// <summary>解析结果的零件集合；字段语义见 <see cref="ShaderTemplate"/> 对应成员。</summary>
    internal sealed record Result(
        string ShaderValue,
        IReadOnlyList<ShaderParamTemplate> Parameters,
        IReadOnlyList<string> FeatureFlags,
        IReadOnlyDictionary<string, string> FeatureFlagDefaults,
        IReadOnlyDictionary<string, string> SystemAttributeDefaults,
        IReadOnlyList<ShaderAttribute> TemplateAttributes,
        IReadOnlyList<string> CompiledTextureKeys);

    /// <summary>
    /// 解析一份模板文本。文本必须含 <c>Layer0</c> 容器与 <c>shader</c> 指令，
    /// 否则抛 <see cref="InvalidOperationException"/>（由 <see cref="ShaderCatalog.Build"/> 吞掉并跳过该条目）。
    /// </summary>
    /// <param name="resourceName">模板资源基名（用于日志）。</param>
    /// <param name="text">模板 KV 原文。</param>
    internal static Result Parse(string resourceName, string text)
    {
        var parsed = VmatFormat.Parse(text);
        var layer = FindLayer(parsed)
                    ?? throw new InvalidOperationException($"{resourceName}: 缺少 Layer0 容器。");

        var shaderValue = layer.FindChild("shader")?.Value;
        if (string.IsNullOrWhiteSpace(shaderValue))
            throw new InvalidOperationException($"{resourceName}: 缺少 shader 指令。");

        var parameters = new List<ShaderParamTemplate>();
        var featureFlags = new List<string>();
        var flagDefaults = new Dictionary<string, string>(StringComparer.Ordinal);
        var systemAttributes = new Dictionary<string, string>(StringComparer.Ordinal);
        var attributes = new List<ShaderAttribute>();
        var compiledKeys = new List<string>();

        foreach (var child in layer.Children)
        {
            if (child.IsContainer)
            {
                CollectBlock(child, systemAttributes, attributes, compiledKeys);
                continue;
            }

            var key = child.Key;
            if (key.Length == 0 || key == "shader") continue;

            if (key.StartsWith("F_", StringComparison.Ordinal))
            {
                featureFlags.Add(key);
                flagDefaults[key] = child.Value ?? "0";
                continue;
            }

            parameters.Add(ToParameter(child));
        }

        return new Result(shaderValue, parameters, featureFlags, flagDefaults, systemAttributes, attributes, compiledKeys);
    }

    /// <summary>收集 Layer0 下的已知嵌套块；未知块（编辑器状态）忽略。</summary>
    private static void CollectBlock(
        VmatNode block,
        Dictionary<string, string> systemAttributes,
        List<ShaderAttribute> attributes,
        List<string> compiledKeys)
    {
        switch (block.Key)
        {
            case "SystemAttributes":
                foreach (var child in block.Children)
                    systemAttributes[child.Key] = child.Value ?? string.Empty;
                break;
            case "Attributes":
                // 逐字保留：键与值都不加工，写出时原样回放到 Attributes 块。
                foreach (var child in block.Children)
                    attributes.Add(new ShaderAttribute(child.Key, child.Value ?? string.Empty));
                break;
            case "Compiled Textures":
                foreach (var child in block.Children)
                    compiledKeys.Add(child.Key);
                break;
            default:
                // VariableState / UnusedVariables / Material1 之类：编辑器或工具的附属状态，忽略。
                break;
        }
    }

    /// <summary>按前缀约定把一个标量子节点转换成参数描述。</summary>
    private static ShaderParamTemplate ToParameter(VmatNode child)
    {
        var key = child.Key;
        var value = child.Value ?? string.Empty;

        if (key.StartsWith("Texture", StringComparison.Ordinal))
        {
            // 默认值是 [..] 常量向量的按「贴图或向量」双形态处理（GUI 提供切换），
            // 其余贴图键（含空默认值）默认按纯贴图路径编辑。
            var shape = value.TrimStart().StartsWith('[')
                ? ShaderValueShape.TextureOrVector
                : ShaderValueShape.Texture;
            return new ShaderParamTemplate(key, key, ShaderParamKind.Texture, value) { Shape = shape };
        }

        var (kind, scalarShape) = ClassifyScalar(key);
        return new ShaderParamTemplate(key, key, kind, value) { Shape = scalarShape };
    }

    private static (ShaderParamKind Kind, ShaderValueShape Shape) ClassifyScalar(string key) => key switch
    {
        _ when key.StartsWith("g_b", StringComparison.Ordinal) => (ShaderParamKind.Bool, ShaderValueShape.Scalar),
        _ when key.StartsWith("g_n", StringComparison.Ordinal) => (ShaderParamKind.Int, ShaderValueShape.Scalar),
        _ when key.StartsWith("g_v", StringComparison.Ordinal) => (ShaderParamKind.Vector, ShaderValueShape.Vector),
        // g_fl* 与 g_f* 都是浮点（CS:GO 模板里 g_f* 是简写前缀）。
        _ when key.StartsWith("g_fl", StringComparison.Ordinal) || key.StartsWith("g_f", StringComparison.Ordinal)
            => (ShaderParamKind.Float, ShaderValueShape.Scalar),
        _ => (ShaderParamKind.String, ShaderValueShape.Scalar),
    };

    /// <summary>兼容「根即 Layer0」与「根包一层」两种解析形态；目录校验也复用此入口。</summary>
    internal static VmatNode? FindLayer(VmatNode root) => root is { IsContainer: true, Key: "Layer0" }
            ? root
            : root.Children.FirstOrDefault(c => c.IsContainer && c.Key == "Layer0");
}
