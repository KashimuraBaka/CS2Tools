namespace VmatLib;

/// <summary>
/// 角色（<see cref="TextureRole"/>）↔ 着色器通道 Token 的<b>闭合</b>映射表
/// （<c>docs/feature-dragdrop-suffix-spec.md</c> §5.2）。
///
/// <para><b>分档策略。</b>本表提供两个层次的 Token：</para>
/// <list type="bullet">
///   <item><description><b>主 Token</b>（索引 0）——对应着色器里「直白」的那个通道名，
///   例如 <c>Normal</c>、<c>Color</c>。<b>P1–P3 档只认主 Token</b>，以保证「直名」这一档
///   的行为稳定，不受别名表扩张的影响。</description></item>
///   <item><description><b>别名 Token</b>（索引 ≥ 1）——生态里常见的同义写法，例如
///   <c>Albedo</c> / <c>Diffuse</c> 都算 <c>Color</c>。<b>仅在 P4（整名相等）与
///   P5（带限定词的复合通道）判定中生效</b>。</description></item>
/// </list>
///
/// <para><b>取舍理由。</b>为什么不让别名参与 P1–P3：若别名参与，则一个仅仅因为
/// 「恰好叫 <c>TextureAlbedo</c>」而存在的参数会与 <c>TextureColor</c> 在 P1 档同权重竞争，
/// 结果将取决于别名表的书写顺序而非着色器自身的命名习惯。限定在 P4/P5 使用别名，
/// 既覆盖了别名生态，又不污染最高优先级的三档。</para>
///
/// <para><b>Token 比较一律 <see cref="StringComparison.OrdinalIgnoreCase"/></b>：
/// Source 2 生态里 <c>Lightmap</c> / <c>LightMap</c>、<c>Normal</c> / <c>NORMAL</c> 并存，
/// 大小写敏感会让规则形同虚设。</para>
///
/// <para>纯函数、无状态、线程安全；<see cref="AllRoles"/> 的顺序即 <see cref="TextureRole"/>
/// 的声明顺序，供 GUI 生成下拉框时保持与文档一致。</para>
/// </summary>
public static class TextureRoleTokens
{
    /// <summary>
    /// 全部已知角色，按 <see cref="TextureRole"/> 声明顺序排列；不含枚举中不存在的值。
    /// GUI 的角色下拉框直接绑定本列表的枚举名。
    /// </summary>
    public static IReadOnlyList<TextureRole> AllRoles { get; } = BuildAllRoles();

    private static readonly IReadOnlyDictionary<TextureRole, RoleInfo> Map = BuildMap();

    /// <summary>
    /// 取某个角色的全部 Token：索引 0 恒为主 Token，其后依次为别名 Token。
    /// <see cref="TextureRole.Unknown"/> 没有 Token，返回空列表。
    /// </summary>
    /// <param name="role">待查询的角色。</param>
    /// <returns>只读 Token 列表（长度 ≥ 1 者首项必为主 Token）。</returns>
    public static IReadOnlyList<string> TokensOf(TextureRole role) => Map.TryGetValue(role, out var info) ? info.Tokens : [];

    /// <summary>
    /// 取某个角色的主 Token（P1–P3 档唯一认可的 Token）。
    /// <see cref="TextureRole.Unknown"/> 返回空字符串。
    /// </summary>
    /// <param name="role">待查询的角色。</param>
    /// <returns>主 Token，或 <c>""</c>。</returns>
    public static string PrimaryTokenOf(TextureRole role) => Map.TryGetValue(role, out var info) ? info.Tokens[0] : string.Empty;

    /// <summary>
    /// 取某个角色的中文显示名，用于 GUI 下拉框与诊断文本。
    /// </summary>
    /// <param name="role">待查询的角色。</param>
    /// <returns>中文名；未知值返回枚举名本身。</returns>
    public static string Describe(TextureRole role) => Map.TryGetValue(role, out var info) ? info.Display : role.ToString();

    /// <summary>
    /// 取某个角色的「中文名（枚举名）」双语文本，供需要两者并存的界面使用。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么两个都要。</b>中文名是给人读的，枚举名是与配置文件、规则表
    /// 对得上的唯一标识。翻译本身是有歧义的——<c>Metalness</c> 译成「金属度」还是
    /// 「金属性」、<c>Mask</c> 译成「遮罩」还是「掩模」，不同文档并不一致。
    /// 只给中文，用户按文档核对时会以为程序认错了角色；只给英文，中文用户又要逐个猜。
    /// 两个都摆出来，对错当场可见。</para>
    ///
    /// <para>格式固定为 <c>中文名 (枚举名)</c>：括号里放的是<b>枚举名本身</b>，
    /// 不是参数键。参数键带 Texture 前缀且按着色器分档，与角色并非一一对应，
    /// 混排反而更容易看错。</para>
    /// </remarks>
    /// <param name="role">待查询的角色。</param>
    /// <returns>形如 <c>环境光遮蔽 (AmbientOcclusion)</c> 的双语文本。</returns>
    public static string DescribeBilingual(TextureRole role) => $"{Describe(role)} ({role})";

    /// <summary>
    /// 大小写不敏感地把用户输入 / JSON 文本解析为 <see cref="TextureRole"/>。
    /// 刻意<b>不</b>接受纯数字串（<c>Enum.TryParse</c> 默认接受 <c>"5"</c>），
    /// 否则一份被手工改坏的配置会把 <c>"role": "7"</c> 静默当成 FoamMask。
    /// </summary>
    /// <param name="text">待解析文本，允许为 <c>null</c>。</param>
    /// <param name="role">解析成功时输出角色，失败时输出 <see cref="TextureRole.Unknown"/>。</param>
    /// <returns>命中一个已定义的枚举名（含 <c>Unknown</c>）时返回 <c>true</c>。</returns>
    public static bool TryParseRole(string? text, out TextureRole role)
    {
        role = TextureRole.Unknown;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        if (!Enum.TryParse<TextureRole>(trimmed, ignoreCase: true, out var parsed)) return false;
        if (!Enum.IsDefined(parsed)) return false;
        role = parsed;
        return true;
    }

    private static IReadOnlyList<TextureRole> BuildAllRoles() => Enum.GetValues<TextureRole>();

    private static IReadOnlyDictionary<TextureRole, RoleInfo> BuildMap() => new Dictionary<TextureRole, RoleInfo>
    {
        [TextureRole.Unknown] = new([], "未识别"),
        [TextureRole.Color] = new(["Color", "Albedo", "Diffuse", "Base"], "颜色 / 反照率"),
        [TextureRole.Normal] = new(["Normal", "Normals", "Norm"], "法线"),
        [TextureRole.Roughness] = new(["Roughness", "Rough"], "粗糙度"),
        [TextureRole.Metalness] = new(["Metalness", "Metal", "Metallic"], "金属度"),
        [TextureRole.AmbientOcclusion] = new(["AmbientOcclusion", "AO", "Occlusion"], "环境光遮蔽"),
        [TextureRole.Height] = new(["Height", "Displacement"], "高度"),
        [TextureRole.Translucency] = new(["Translucency", "Trans", "Transmission"], "半透明"),
        [TextureRole.Detail] = new(["Detail", "DetailAlbedo"], "细节"),
        [TextureRole.Mask] = new(["Mask"], "遮罩"),
        [TextureRole.Emissive] = new(["Emissive", "SelfIllum", "SelfIllumination", "Emission"], "自发光"),
        [TextureRole.CubeMap] = new(["CubeMap", "Cube", "Sky", "Env"], "Cube 贴图"),
        [TextureRole.Lightmap] = new(["Lightmap", "LightMap", "LightMapTexture"], "光照贴图"),
        [TextureRole.FoamMask] = new(["Foam", "FoamMask"], "泡沫遮罩"),
        [TextureRole.FoamNormal] = new(["FoamNormal"], "泡沫法线"),
        [TextureRole.WavesMask] = new(["Waves", "WavesMask"], "波浪遮罩"),
        [TextureRole.WavesNormal] = new(["WavesNormal"], "波浪法线"),
        [TextureRole.WavesHeight] = new(["WavesHeight"], "波浪高度"),
        [TextureRole.DebrisColor] = new(["Debris", "DebrisColor"], "漂浮物颜色"),
        [TextureRole.DebrisNormal] = new(["DebrisNormal"], "漂浮物法线"),
        [TextureRole.DebrisHeight] = new(["DebrisHeight"], "漂浮物高度"),
        [TextureRole.TintMask] = new(["TintMask"], "染色遮罩"),
        [TextureRole.DetailMask] = new(["DetailMask"], "细节遮罩"),
        [TextureRole.SelfIllumMask] = new(["SelfIllumMask"], "自发光遮罩"),
        [TextureRole.RimMask] = new(["RimMask"], "边缘光遮罩"),
        [TextureRole.LowEndCubeMap] = new(["LowEndCubeMap"], "低端 Cube Map"),
        [TextureRole.HairMask] = new(["HairMask"], "毛发遮罩"),
        [TextureRole.SssMask] = new(["SssMask"], "次表面散射遮罩"),
        [TextureRole.RetroReflectiveMask] = new(["RetroReflectiveMask"], "逆反射遮罩"),
        [TextureRole.NormalDetail] = new(["NormalDetail"], "细节法线"),
        [TextureRole.DecalTranslucency] = new(["DecalTranslucency"], "贴花半透明"),
    };

    private sealed class RoleInfo(string[] tokens, string display)
    {

        /// <summary>索引 0 为主 Token，其后为别名 Token。</summary>
        public IReadOnlyList<string> Tokens { get; } = tokens;

        /// <summary>中文显示名。</summary>
        public string Display { get; } = display;
    }
}
