namespace VmatLib;

/// <summary>
/// 一个着色器上被推断出的「语义槽位 → 候选后缀」清单。不可变值对象。
/// </summary>
/// <param name="role">语义槽位。</param>
/// <param name="roleDisplay">槽位中文名。</param>
/// <param name="parameterKey">命中的着色器参数键。</param>
/// <param name="candidates">建议用于匹配该槽位的文件后缀，已按推荐度排序并去重。</param>
public sealed class InferredSuffixSet(
    TextureRole role,
    string roleDisplay,
    string parameterKey,
    IReadOnlyList<string> candidates)
{
    /// <summary>语义槽位。</summary>
    public TextureRole Role { get; } = role;

    /// <summary>槽位中文名，例如「法线」。</summary>
    public string RoleDisplay { get; } = roleDisplay;

    /// <summary>命中的着色器参数键；推断不出时为空串。</summary>
    public string ParameterKey { get; } = parameterKey;

    /// <summary>候选后缀（均以 <c>_</c> 开头、小写）。</summary>
    public IReadOnlyList<string> Candidates { get; } = candidates;

    /// <summary>调试用摘要。</summary>
    public override string ToString() => $"{RoleDisplay}({ParameterKey}) ← {string.Join(" ", Candidates)}";
}

/// <summary>
/// 按<b>着色器模板</b>推断「这个着色器需要哪些贴图」，并据此给出每种贴图的文件后缀。
///
/// <para><b>为什么需要推断。</b>用户手上的贴图命名千奇百怪，但着色器能接收的槽位是
/// <b>有限且已声明</b>的——模板里每个 <see cref="ShaderParamKind.Texture"/> 参数就是一个槽位。
/// 先问「这个着色器要什么」，再据此准备匹配用的后缀，比让用户凭空回忆该起什么名字可靠得多。</para>
///
/// <para><b>推断链路。</b>逐个角色调 <see cref="TextureRoleResolver.Resolve"/>；只有解析成功
/// （即该着色器确实有槽位能接）的角色才进入结果——所以列出来的每一项都是「这个着色器真的要」，
/// 而不是把所有角色一股脑列出来让用户自己挑。</para>
///
/// <para><b>候选后缀从哪来。</b>取 <see cref="TextureRoleTokens"/> 里该角色的全部 Token
/// 转成 <c>_小写</c> 形式，再补上该角色在 Valve 生态里的固定简写
/// （例如颜色槽位的 <c>_null</c>——与 <c>_normal</c>、<c>_tran</c> 配套的那套三件套写法）。
/// 简写排在前面，因为它们在真实资产里出现得更频繁。</para>
///
/// <para>纯函数、无状态、线程安全。</para>
/// </summary>
public static class ShaderSuffixInference
{
    /// <summary>
    /// 推断某个着色器可用的语义槽位及其候选后缀。
    /// </summary>
    /// <param name="shader">目标着色器模板；为 <c>null</c> 时返回空列表。</param>
    /// <returns>
    /// 按 <see cref="TextureRole"/> 声明顺序排列的结果；只含解析成功的槽位，
    /// 且每个槽位至少有一个候选后缀。
    /// </returns>
    public static IReadOnlyList<InferredSuffixSet> Infer(ShaderTemplate? shader)
    {
        if (shader is null) return [];

        var result = new List<InferredSuffixSet>();
        foreach (var role in TextureRoleTokens.AllRoles)
        {
            if (role == TextureRole.Unknown) continue;

            var resolution = TextureRoleResolver.Resolve(shader, role);
            if (!resolution.IsResolved) continue;

            var candidates = CandidatesOf(role);
            if (candidates.Count == 0) continue;

            result.Add(new InferredSuffixSet(
                role,
                TextureRoleTokens.Describe(role),
                resolution.ParameterKey ?? string.Empty,
                candidates));
        }

        return result;
    }

    /// <summary>
    /// 取某角色的候选后缀：Valve 常用简写在前，随后是 Token 派生形式；已去重。
    /// </summary>
    /// <param name="role">语义槽位。</param>
    /// <returns>小写、带前导下划线的后缀列表。</returns>
    public static IReadOnlyList<string> CandidatesOf(TextureRole role)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();

        foreach (var suffix in ConventionalOf(role))
        {
            if (seen.Add(suffix)) ordered.Add(suffix);
        }

        foreach (var token in TextureRoleTokens.TokensOf(role))
        {
            if (string.IsNullOrWhiteSpace(token)) continue;
            var suffix = "_" + token.Trim().TrimStart('_').ToLowerInvariant();
            if (seen.Add(suffix)) ordered.Add(suffix);
        }

        return ordered;
    }

    /// <summary>
    /// 各角色在 Valve 生态里的固定简写。这些写法在真实资产里非常普遍，
    /// 但不在 <see cref="TextureRoleTokens"/> 的通道名表里，因此单独补一份。
    /// </summary>
    private static IReadOnlyList<string> ConventionalOf(TextureRole role) => role switch
    {
        // _null 是 Valve 体系里基础色的固定写法，与 _normal / _tran 配套使用。
        TextureRole.Color => new[] { "_null", "_col" },
        TextureRole.Normal => ["_normal", "_n"],
        TextureRole.Translucency => ["_tran", "_trans"],
        TextureRole.Roughness => ["_rough"],
        TextureRole.Metalness => ["_metal"],
        TextureRole.AmbientOcclusion => ["_ao"],
        TextureRole.Height => ["_height"],
        TextureRole.Detail => ["_detail"],
        TextureRole.DetailMask => ["_detailmask"],
        TextureRole.Emissive => ["_emissive", "_selfillum"],
        TextureRole.Mask => ["_mask"],
        TextureRole.FoamMask => ["_foam"],
        TextureRole.FoamNormal => ["_foamnormal"],
        TextureRole.WavesMask => ["_waves"],
        TextureRole.WavesNormal => ["_wavesnormal"],
        TextureRole.WavesHeight => ["_wavesheight"],
        TextureRole.DebrisColor => ["_debris"],
        TextureRole.DebrisNormal => ["_debrisnormal"],
        TextureRole.DebrisHeight => ["_debrisheight"],
        TextureRole.TintMask => ["_tintmask"],
        TextureRole.SelfIllumMask => ["_selfillummask"],
        TextureRole.RimMask => ["_rimmask"],
        TextureRole.CubeMap => ["_cube"],
        TextureRole.LowEndCubeMap => ["_lowendcubemap"],
        TextureRole.Lightmap => ["_lightmap"],
        _ => [],
    };
}
