using System.Text.Json.Serialization;

namespace VmatLib;

/// <summary>
/// 应用配置（规格 §3.2）。落在 <c>%APPDATA%\VmatGenerator\settings.json</c>，
/// 由 <see cref="VmatGeneratorSettingsStore"/> 负责读写。
///
/// <para>所有属性均为可变的公开属性并带默认值——用户可以直接在
/// <see cref="System.Text.Json"/> 的对象模型上编辑，也便于 GUI 的设置页双向绑定。
/// 写盘时的字段合法性由 <see cref="VmatGeneratorSettingsStore.Sanitize"/>
/// 逐字段校验，非法字段只回退该项而不丢弃整份配置。</para>
/// </summary>
public sealed class VmatGeneratorSettings
{
    /// <summary>当前支持的配置结构版本；其余值一律修复为该值。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>默认着色器名；必须是 <see cref="ShaderCatalog"/> 中存在的键。</summary>
    public const string DefaultShaderNameValue = "csgo_environment.vfx";

    /// <summary>默认构造：全部字段取默认值，<see cref="Rules"/> 为空列表。</summary>
    public VmatGeneratorSettings()
    {
        SchemaVersion = CurrentSchemaVersion;
        DefaultShaderName = DefaultShaderNameValue;
        TextureRoot = string.Empty;
        AutoAssignOnDrop = true;
        RecurseTextureFolders = true;
        AdoptDroppedVmatFolderAsMaterialsRoot = true;
        Rules = [];
    }

    /// <summary>配置目录（<c>%APPDATA%\VmatGenerator</c>）。首次保存时才创建。</summary>
    public static string SettingsDirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VmatGenerator");

    /// <summary>配置文件完整路径（<c>%APPDATA%\VmatGenerator\settings.json</c>）。</summary>
    public static string SettingsFilePath => Path.Combine(SettingsDirectoryPath, "settings.json");

    /// <summary>配置结构版本；仅 <see cref="CurrentSchemaVersion"/> 合法。</summary>
    [JsonPropertyOrder(-2)]
    public int SchemaVersion { get; set; }

    /// <summary>拖入贴图但当前没有材质时，新建材质所用的默认着色器名。</summary>
    [JsonPropertyOrder(-1)]
    public string DefaultShaderName { get; set; }

    /// <summary>
    /// 贴图路径换算基准目录；为空或目录不存在时，全部按原路径写回。
    /// 刻意<b>不</b>用 <c>Directory.Exists</c> 作为合法性判据——网络盘临时掉线不该丢配置。
    /// </summary>
    public string TextureRoot { get; set; }

    /// <summary>
    /// 快速导航向导第一步的「资产文件夹」：扫描贴图的来源目录。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="ProjectRoot"/> 分开，是为了支持「贴图在引擎目录、材质在工程目录」
    /// 这类布局——两者不同目录时，<c>.vmat</c> 里写的贴图路径仍然相对本目录换算。
    /// <b>只校验类型，不校验目录是否存在</b>：外接盘掉线、网络盘未挂载都不该让用户配置被清掉。
    /// </remarks>
    public string AssetsRoot { get; set; } = string.Empty;

    /// <summary>
    /// 快速导航向导第一步的「项目文件夹」：生成的 <c>.vmat</c> 输出目录。
    /// </summary>
    /// <remarks>与 <see cref="AssetsRoot"/> 一样只校验类型，不校验目录存在性。</remarks>
    public string ProjectRoot { get; set; } = string.Empty;

    /// <summary>拖入后是否按后缀自动写入参数。</summary>
    public bool AutoAssignOnDrop { get; set; }

    /// <summary>拖入贴图文件夹时是否递归扫描子目录（不影响 .vmat 探测，后者始终递归）。</summary>
    public bool RecurseTextureFolders { get; set; }

    /// <summary>拖入材质目录 / .vmat 时是否接管材质根目录。</summary>
    public bool AdoptDroppedVmatFolderAsMaterialsRoot { get; set; }

    /// <summary>后缀规则表；<b>数组顺序即优先级下标</b>，是 §6.4 的第三判据。</summary>
    public List<TextureSuffixRule> Rules { get; set; }

    /// <summary>
    /// 构造一份带 §4 全部 35 条种子规则的默认配置（<c>textureRoot</c> 为空、其余开关为 <c>true</c>）。
    /// 种子表的<b>书写顺序不表示优先级</b>——优先级在运行时由「最长后缀优先 + 数组下标」裁定。
    /// </summary>
    /// <returns>全新的默认配置实例（每次调用返回新对象，调用方可安全改写）。</returns>
    public static VmatGeneratorSettings CreateDefault()
    {
        var settings = new VmatGeneratorSettings
        {
            SchemaVersion = CurrentSchemaVersion,
            DefaultShaderName = DefaultShaderNameValue,
            TextureRoot = string.Empty,
            AutoAssignOnDrop = true,
            RecurseTextureFolders = true,
            AdoptDroppedVmatFolderAsMaterialsRoot = true,
            Rules = [.. SeedRules()],
        };
        return settings;
    }

    /// <summary>
    /// 按归一化形式查找一条规则（大小写、分隔符无关）。
    /// </summary>
    /// <param name="suffix">待查找的后缀，例如 <c>_Normal.png</c>。</param>
    /// <returns>数组中第一条归一化结果相同的规则；没有则返回 <c>null</c>。</returns>
    public TextureSuffixRule? FindRule(string suffix)
    {
        var normalized = TextureSuffixMatcher.NormalizeName(suffix ?? string.Empty);
        if (normalized.Length == 0) return null;
        foreach (var rule in Rules)
        {
            if (rule is null) continue;
            if (string.Equals(TextureSuffixMatcher.NormalizeName(rule.Suffix ?? string.Empty), normalized, StringComparison.Ordinal))
                return rule;
        }
        return null;
    }

    /// <summary>
    /// §4 的默认种子规则。返回全新列表，调用方不得依赖其内部实例。
    ///
    /// <para><b>书写顺序不代表优先级</b>——优先级在运行时由「最长后缀优先 + 数组下标」裁定
    /// （§6.4）。这里按语义分组排列，只是为了让人读起来顺。</para>
    ///
    /// <para><b>闭环不变量。</b>凡是可能成为 P5 候选的通道，其「去 <c>Texture</c> 前缀 +
    /// 小写 + 前置 <c>_</c>」派生后缀都必须在表内出现，否则诊断里给出的改名建议会
    /// 把用户带到一个匹配不上任何规则的死路上。该不变量由
    /// <c>TextureAssignmentSelfTest</c> 机器化断言。</para>
    /// </summary>
    /// <returns>默认种子规则列表，全部 <c>enabled = true</c>。</returns>
    public static IReadOnlyList<TextureSuffixRule> SeedRules() => [
        // 法线
        new("_normal", TextureRole.Normal),
        new("_n", TextureRole.Normal),
        new("_normals", TextureRole.Normal),
        new("_foamnormal", TextureRole.FoamNormal),
        new("_wavesnormal", TextureRole.WavesNormal),
        new("_debrisnormal", TextureRole.DebrisNormal),
        // 颜色 / 反照率
        // _diff 是 Valve 自家 .vmat 生态最常见的写法，与 _diffuse 等价。
        new("_diff", TextureRole.Color),
        new("_diffuse", TextureRole.Color),
        new("_albedo", TextureRole.Color),
        new("_color", TextureRole.Color),
        new("_col", TextureRole.Color),
        // 粗糙度
        new("_rough", TextureRole.Roughness),
        new("_roughness", TextureRole.Roughness),
        // 金属度
        new("_metal", TextureRole.Metalness),
        new("_metalness", TextureRole.Metalness),
        // 环境光遮蔽
        new("_ao", TextureRole.AmbientOcclusion),
        new("_ambientocclusion", TextureRole.AmbientOcclusion),
        // 高度
        new("_height", TextureRole.Height),
        new("_debrisheight", TextureRole.DebrisHeight),
        new("_wavesheight", TextureRole.WavesHeight),
        // 半透明
        // _tran 与 _trans 同样是常用缩写，两者等价。
        new("_tran", TextureRole.Translucency),
        new("_trans", TextureRole.Translucency),
        new("_translucency", TextureRole.Translucency),
        // 细节 / 遮罩
        new("_detail", TextureRole.Detail),
        new("_detailmask", TextureRole.DetailMask),
        new("_normaldetail", TextureRole.NormalDetail),
        new("_mask", TextureRole.Mask),
        new("_tintmask", TextureRole.TintMask),
        new("_selfillummask", TextureRole.SelfIllumMask),
        new("_rimmask", TextureRole.RimMask),
        new("_hairmask", TextureRole.HairMask),
        new("_sssmask", TextureRole.SssMask),
        new("_retroreflectivemask", TextureRole.RetroReflectiveMask),
        new("_decaltranslucency", TextureRole.DecalTranslucency),
        // 自发光
        new("_emissive", TextureRole.Emissive),
        new("_selfillum", TextureRole.Emissive),
        // 水面（csgo_water_fancy）
        new("_foam", TextureRole.FoamMask),
        new("_waves", TextureRole.WavesMask),
        new("_debris", TextureRole.DebrisColor),
        // 光照 / 天空
        new("_lightmap", TextureRole.Lightmap),
        new("_cube", TextureRole.CubeMap),
        new("_lowendcubemap", TextureRole.LowEndCubeMap),
    ];

    /// <summary>调试用摘要。</summary>
    /// <returns>人可读的一行描述。</returns>
    public override string ToString() => $"v{SchemaVersion} · 默认着色器 {DefaultShaderName} · 贴图根 {TextureRoot} · 规则 {Rules.Count} 条";
}