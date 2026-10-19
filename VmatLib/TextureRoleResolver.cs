namespace VmatLib;

/// <summary>
/// 语义槽位 → 具体着色器参数键的解析优先级档位（规格 §5.3）。
/// 数值越小优先级越高，<see cref="None"/> 表示该角色在此模板上无法解析。
/// </summary>
public enum TextureKeyTier
{
    /// <summary>未命中任何档位：不写入任何参数。</summary>
    None = 0,

    /// <summary>P1 直名无缀：键形态 <c>Texture{R}</c>。</summary>
    ExactName = 1,

    /// <summary>P2 直名 + 数字尾缀：键形态 <c>Texture{R}{N}</c>。</summary>
    ExactNameNumbered = 2,

    /// <summary>P3 Layer 前缀：键形态 <c>TextureLayer{N}{R}</c>。</summary>
    LayerPrefixed = 3,

    /// <summary>P4 派生通道名精确相等：<c>Texture</c> 之后的整个通道名等于主 Token 或某别名。</summary>
    DerivedChannelEqual = 4,

    /// <summary>P5 带限定词的复合通道：键形态 <c>Texture{Q}{R}</c>，<c>{Q}</c> 非空。</summary>
    QualifiedComposite = 5,
}

/// <summary>
/// 一个语义槽位未能解析到参数键的原因（供 GUI 区分「没填」与「不敢填」）。
/// </summary>
public enum TextureResolveFailure
{
    /// <summary>已成功解析（<see cref="TextureRoleResolution.IsResolved"/> 为 <c>true</c>）。</summary>
    None = 0,

    /// <summary>模板无对应键：该着色器根本没有承载这个槽位的纹理参数。</summary>
    NoMatchingKey,

    /// <summary>多候选并列：P5 档命中多个带限定词的复合通道且无法在通用规则下确定唯一胜者，
    /// 因此刻意不写入任何参数。</summary>
    AmbiguousQualified,

    /// <summary><see cref="AmbiguousQualified"/> 的兼容别名（v1.1 期间的内部命名），数值相同。</summary>
    AmbiguousCandidates = AmbiguousQualified,
}

/// <summary>
/// 某个角色在某模板上的一个合格参数键候选（规格 §5.3 / §5.4）。不可变值对象。
/// </summary>
/// <remarks>构造候选。</remarks>
/// <param name="parameterKey">该参数在 <c>ShaderTemplate.Parameters</c> 中的键名。</param>
/// <param name="role">本候选服务的角色。</param>
/// <param name="tier">命中档位。</param>
/// <param name="derivedChannel">剥掉 <c>Texture</c> 前缀、Layer 前缀与尾号后的通道名。</param>
/// <param name="qualifier">P5 档的非空限定词；其余档位为 <c>null</c>。</param>
/// <param name="layerNumber">Layer 序号，<c>0</c> 表示无 Layer 前缀。</param>
/// <param name="trailingNumber">末尾数字尾号，<c>0</c> 表示无尾号。</param>
/// <param name="parameterIndex">该参数在模板 <c>Parameters</c> 中的声明下标。</param>
public sealed class TextureKeyCandidate(
    string parameterKey,
    TextureRole role,
    TextureKeyTier tier,
    string derivedChannel,
    string? qualifier,
    int layerNumber,
    int trailingNumber,
    int parameterIndex)
{

    /// <summary>参数键，例如 <c>TextureLayer1Normal</c>。</summary>
    public string ParameterKey { get; } = parameterKey;

    /// <summary>本候选服务的角色。</summary>
    public TextureRole Role { get; } = role;

    /// <summary>命中档位。</summary>
    public TextureKeyTier Tier { get; } = tier;

    /// <summary>派生通道名，例如 <c>Normal</c> / <c>FoamNormal</c> / <c>TintMask</c>。</summary>
    public string DerivedChannel { get; } = derivedChannel;

    /// <summary>P5 档的非空限定词（例如 <c>Foam</c>）；非 P5 档为 <c>null</c>。</summary>
    public string? Qualifier { get; } = qualifier;

    /// <summary>Layer 序号（<c>0</c> = 无 Layer 前缀）。</summary>
    public int LayerNumber { get; } = layerNumber;

    /// <summary>末尾数字尾号（<c>0</c> = 无尾号）。</summary>
    public int TrailingNumber { get; } = trailingNumber;

    /// <summary>在 <c>ShaderTemplate.Parameters</c> 中的声明下标。</summary>
    public int ParameterIndex { get; } = parameterIndex;

    /// <summary>调试用摘要。</summary>
    /// <returns>人可读的一行描述。</returns>
    public override string ToString() => $"{ParameterKey}（{(int)Tier} {Tier}，channel={DerivedChannel}，layer={LayerNumber}，num={TrailingNumber}）";
}

/// <summary>
/// 一个角色在某模板上的解析结果（规格 §5.3–§5.5）。不可变值对象。
/// </summary>
public sealed class TextureRoleResolution
{
    private TextureRoleResolution(
        TextureRole role,
        bool isResolved,
        string? parameterKey,
        TextureKeyTier tier,
        IReadOnlyList<TextureKeyCandidate> candidates,
        TextureResolveFailure unresolvedReason,
        string diagnosticText)
    {
        Role = role;
        IsResolved = isResolved;
        ParameterKey = parameterKey;
        Tier = tier;
        Candidates = candidates;
        UnresolvedReason = unresolvedReason;
        DiagnosticText = diagnosticText;
    }

    /// <summary>被解析的角色。</summary>
    public TextureRole Role { get; }

    /// <summary>是否解析成功；为 <c>false</c> 时 <see cref="ParameterKey"/> 必为 <c>null</c>。</summary>
    public bool IsResolved { get; }

    /// <summary>
    /// 命中的参数键；未解析时为 <c>null</c>。多候选并列时同样为 <c>null</c>——
    /// 刻意「不填」优于「猜错槽位」。
    /// </summary>
    public string? ParameterKey { get; }

    /// <summary>
    /// 命中档位；未解析时为 <see cref="TextureKeyTier.None"/>。
    /// 多候选并列时虽然候选都属于 P5，但对调用方而言「没有命中任何档」，
    /// 故统一报告 <see cref="TextureKeyTier.None"/>，细节由 <see cref="Candidates"/> 与
    /// <see cref="DiagnosticText"/> 承载。
    /// </summary>
    public TextureKeyTier Tier { get; }

    /// <summary>
    /// 最低命中档位内的全部候选，已按 §5.4 排序（首个即胜者）。
    /// 未解析且模板无对应键时为空列表；多候选并列时长度 ≥ 2。
    /// 多候选时<b>复用本属性</b>承载候选清单，不再新增结果类型。
    /// </summary>
    public IReadOnlyList<TextureKeyCandidate> Candidates { get; }

    /// <summary>未解析原因，区分「模板无对应键」与「多候选并列」。</summary>
    public TextureResolveFailure UnresolvedReason { get; }

    /// <summary>可直接展示给用户的一行诊断文本。</summary>
    public string DiagnosticText { get; }

    /// <summary><see cref="DiagnosticText"/> 的兼容别名。</summary>
    public string Diagnostic => DiagnosticText;

    /// <summary>构造解析成功的结果。</summary>
    /// <param name="role">角色。</param>
    /// <param name="winner">胜出候选（<c>Candidates[0]</c>）。</param>
    /// <param name="candidates">已排序的同档候选列表。</param>
    /// <returns>已解析结果。</returns>
    public static TextureRoleResolution Resolved(TextureRole role, TextureKeyCandidate winner, IReadOnlyList<TextureKeyCandidate> candidates)
    {
        var text = $"{TextureRoleTokens.Describe(role)}（{role}）→ {winner.ParameterKey}（{(int)winner.Tier} {winner.Tier}）";
        return new TextureRoleResolution(role, true, winner.ParameterKey, winner.Tier, candidates, TextureResolveFailure.None, text);
    }

    /// <summary>构造未解析的结果。</summary>
    /// <param name="role">角色。</param>
    /// <param name="reason">未解析原因。</param>
    /// <param name="candidates">已排序的候选列表（多候选并列时长度 ≥ 2）。</param>
    /// <param name="diagnosticText">诊断文本。</param>
    /// <returns>未解析结果。</returns>
    public static TextureRoleResolution Unresolved(
        TextureRole role,
        TextureResolveFailure reason,
        IReadOnlyList<TextureKeyCandidate> candidates,
        string diagnosticText) => new(role, false, null, TextureKeyTier.None, candidates, reason, diagnosticText);

    /// <summary>调试用摘要。</summary>
    /// <returns>诊断文本。</returns>
    public override string ToString() => DiagnosticText;
}

/// <summary>
/// 语义槽位 → 着色器参数键的解析器（规格 §5）。
///
/// <para><b>第一步：通道名派生（§5.1）。</b>只有同时满足下列两条的参数才进入候选池：
/// ① 键以 <c>Texture</c> 开头（<see cref="StringComparison.Ordinal"/>）——因此
/// <c>SkyTexture</c>、<c>g_tNormal1</c> 一律不参与自动分配；②
/// <c>Kind == ShaderParamKind.Texture</c> 或 <c>Shape == ShaderValueShape.TextureOrVector</c>
/// ——后者允许「常量 vec4 ↔ 贴图路径」互换，正是 <c>csgo_environment</c> 的
/// <c>TextureRoughness1</c> 与 <c>csgo_water_fancy</c> 的 <c>TextureFoamNormal</c>
/// 能被正确解析的前提。随后剥掉 <c>Layer{N}</c> 前缀与末尾数字尾号，得到通道名。</para>
///
/// <para><b>第二步：分档（§5.3）。</b>逐角色按 P1 → P5 求值，首个非空档位即命中：
/// P1 <c>Texture{R}</c> → P2 <c>Texture{R}{N}</c> → P3 <c>TextureLayer{N}{R}</c> →
/// P4 通道名整体等于主 Token 或某别名 → P5 通道名以主 Token / 别名结尾且限定词非空。
/// P1–P3 只认<b>主</b> Token（P4/P5 才启用别名），保证「直名」档的稳定；
/// P4 与 P5 互斥（P4 要求整体相等、P5 要求前缀非空），故不会重复计入。</para>
///
/// <para><b>第三步：同档取舍（§5.4）。</b>档内附加序 → <c>Parameters</c> 声明下标 →
/// <see cref="string.CompareOrdinal(String, String)"/> 三键全序，结果唯一可复现。</para>
///
/// <para><b>第四步：P5 的唯一性红线。</b>本工具的产物是直接进游戏的 .vmat，
/// 静默猜错槽位会生成「看起来正常、实则贴错」的文件，比「没填上」危险得多
/// ——后者用户一眼看得到。因此 P5 只有在合格候选<b>恰好一个</b>时才算命中；
/// 多个候选并列时一律判定为 <see cref="TextureResolveFailure.AmbiguousQualified"/>，
/// 不写入任何参数，并把全部候选键名放进 <see cref="TextureRoleResolution.DiagnosticText"/>。
/// 正因为拒绝在代码里猜，§5.4 才删掉了 P5 的 <c>qualifier.Length</c> 排序键：
/// 原先「限定词最短者胜」这条规则本身是偶然的（<c>Foam</c> 4 &lt; <c>Waves</c> 5 &lt;
/// <c>Debris</c> 6 只是碰巧），却会把普通 <c>_normal</c> 静默写进泡沫法线槽位。
/// 规则保持通用，<b>不含任何 per-shader 特判</b>。</para>
///
/// <para>纯函数：不读文件、不改全局状态，相同入参恒等出参。</para>
/// </summary>
public static class TextureRoleResolver
{
    private const string TexturePrefix = "Texture";
    private const string LayerPrefix = "Layer";

    /// <summary>
    /// 取出模板中全部「候选参数」的键名，保持 <c>Parameters</c> 声明顺序。
    /// </summary>
    /// <param name="shader">目标着色器模板；为 <c>null</c> 时返回空列表。</param>
    /// <returns>满足 §5.1 两条候选条件的参数键。</returns>
    public static IReadOnlyList<string> TextureParameterKeys(ShaderTemplate shader)
    {
        if (shader?.Parameters is null) return [];
        var keys = new List<string>();
        for (var i = 0; i < shader.Parameters.Count; i++)
        {
            if (IsCandidateParameter(shader.Parameters[i])) keys.Add(shader.Parameters[i].Key);
        }
        return keys;
    }

    /// <summary>
    /// 由参数键派生通道名（§5.1）：去扩展名式的 <c>Texture</c> 前缀、剥 <c>Layer{N}</c> 前缀、
    /// 剥末尾数字尾号。
    /// </summary>
    /// <param name="parameterKey">参数键。</param>
    /// <returns>
    /// 通道名，例如 <c>TextureColor1</c> → <c>Color</c>、<c>TextureLayer1Normal</c> → <c>Normal</c>、
    /// <c>TextureFoamNormal</c> → <c>FoamNormal</c>、<c>TextureCubeMap</c> → <c>CubeMap</c>。
    /// 键不以 <c>Texture</c> 开头（如 <c>SkyTexture</c>）时返回空串，表示「不参与自动分配」。
    /// </returns>
    public static string DeriveChannel(string parameterKey) => TryParseKey(parameterKey, out var parsed) ? parsed.Channel : string.Empty;

    /// <summary>
    /// 列出某角色在模板上的<b>最低命中档位内的全部候选</b>，已按 §5.4 排序（首个即胜者）。
    /// 模板无对应键时返回空列表。
    /// </summary>
    /// <param name="shader">目标着色器模板；为 <c>null</c> 时返回空列表。</param>
    /// <param name="role">待解析角色；<see cref="TextureRole.Unknown"/> 永远返回空列表。</param>
    /// <returns>已排序的候选列表。</returns>
    public static IReadOnlyList<TextureKeyCandidate> ResolveCandidates(ShaderTemplate shader, TextureRole role)
    {
        if (shader?.Parameters is null || role == TextureRole.Unknown) return [];
        var tokens = TextureRoleTokens.TokensOf(role);
        if (tokens.Count == 0) return [];

        var bestTier = TextureKeyTier.None;
        var candidates = new List<TextureKeyCandidate>();
        for (var i = 0; i < shader.Parameters.Count; i++)
        {
            var p = shader.Parameters[i];
            if (!IsCandidateParameter(p)) continue;
            if (!TryParseKey(p.Key, out var parsed)) continue;
            var tier = Classify(tokens, parsed);
            if (tier == TextureKeyTier.None) continue;

            var candidate = new TextureKeyCandidate(
                p.Key,
                role,
                tier,
                parsed.Channel,
                tier == TextureKeyTier.QualifiedComposite
                    ? QualifierOf(parsed.Channel, tokens)
                    : null,
                parsed.Layer,
                parsed.Number,
                i);

            if (bestTier == TextureKeyTier.None || tier < bestTier)
            {
                bestTier = tier;
                candidates.Clear();
                candidates.Add(candidate);
            }
            else if (tier == bestTier)
            {
                candidates.Add(candidate);
            }
        }

        return candidates.Count == 0 ? [] : SortByTier(candidates, bestTier);
    }

    /// <summary>
    /// 解析某角色在某模板上应写入的参数键。
    /// </summary>
    /// <param name="shader">目标着色器模板。</param>
    /// <param name="role">待解析角色。</param>
    /// <returns>
    /// 解析结果。命中时 <see cref="TextureRoleResolution.IsResolved"/> 为 <c>true</c>；
    /// 未命中时 <see cref="TextureRoleResolution.ParameterKey"/> 为 <c>null</c>，
    /// 并在 <see cref="TextureRoleResolution.DiagnosticText"/> 中说明原因与可执行的改名建议。
    /// </returns>
    public static TextureRoleResolution Resolve(ShaderTemplate shader, TextureRole role)
    {
        var candidates = ResolveCandidates(shader, role);
        if (candidates.Count == 0)
        {
            return TextureRoleResolution.Unresolved(
                role,
                TextureResolveFailure.NoMatchingKey,
                [],
                BuildDiagnosticText(role, TextureResolveFailure.NoMatchingKey, []));
        }

        var winner = candidates[0];

        // P5 唯一性红线：合格候选多于一个时判定为未解析，宁可留空也不猜错槽位。
        return winner.Tier == TextureKeyTier.QualifiedComposite && candidates.Count > 1
            ? TextureRoleResolution.Unresolved(
                role,
                TextureResolveFailure.AmbiguousQualified,
                candidates,
                BuildDiagnosticText(role, TextureResolveFailure.AmbiguousQualified, candidates))
            : TextureRoleResolution.Resolved(role, winner, candidates);
    }

    /// <summary>
    /// 组装面向用户的诊断文本（§5.6）。多候选时额外给出「改名建议」——把候选键的
    /// <b>派生通道名</b>（<see cref="TextureKeyCandidate.DerivedChannel"/>）转小写并前置 <c>_</c>，
    /// 例如 <c>TextureSelfIllumMask</c> → <c>_selfillummask</c>、
    /// <c>TextureTintMask1</c> → <c>_tintmask</c>。
    ///
    /// <para><b>为什么用通道名而不是键的剩余部分。</b>§5.1 的通道名已经剥掉了数字尾号，
    /// 而用户手写的文件名本来就不带尾号——建议 <c>_tintmask1</c> 会让用户多打一个无意义的
    /// 数字。用通道名派生出来的才是用户真正会写出来的名字。</para>
    ///
    /// <para><b>闭环不变量。</b>这些建议后缀必须都能在 §4 的种子表中找到<b>已启用</b>规则，
    /// 且该规则指向的槽位解析出来的参数键必须<b>恰好等于</b>对应的候选键；否则用户照着提示
    /// 改名反而到不了目标槽位（比原症状更糟）。该不变量由
    /// <see cref="TextureAssignmentSelfTest"/> 的 INV-DIAG-CLOSURE 与穷举比对两条用例
    /// 机器化断言：任何新增的 P5 通道若没有配套种子规则，自检会立刻失败。</para>
    ///
    /// <para><b>逐字契约（§5.6）。</b>返回文本会被 GUI 原样显示在状态栏，
    /// 因此两句模板、连接符与句末句号都是<b>逐字锁定</b>的：
    /// <list type="bullet">
    /// <item><c>AmbiguousQualified</c> → <c>该着色器没有通用的 {槽位} 槽位，候选为 {候选}；请改用 {后缀} 后缀，或改选其他着色器。</c></item>
    /// <item><c>NoMatchingKey</c> → <c>该着色器没有 {槽位} 对应的贴图参数（已跳过）。</c></item>
    /// </list>
    /// 单行输出、不得插入 <c>\n</c>；候选与建议后缀固定用 <c>" / "</c> 连接，
    /// <b>不得</b>使用全角顿号「、」。§11 第 9 项用例对本方法做<b>整串相等</b>断言
    /// （而非「包含候选键名」），任何句式漂移都会让自检立刻失败。</para>
    /// </summary>
    /// <param name="role">角色。</param>
    /// <param name="failure">未解析原因。</param>
    /// <param name="candidates">已排序的候选列表。</param>
    /// <returns>一行中文诊断文本。</returns>
    public static string BuildDiagnosticText(
        TextureRole role,
        TextureResolveFailure failure,
        IReadOnlyList<TextureKeyCandidate> candidates)
    {
        var name = TextureRoleTokens.Describe(role);

        // §5.6 表格逐字实现：两句模板都必须单行、以句号收尾，
        // 候选/建议后缀一律用 " / " 连接（全角顿号「、」不得使用，会被误读为顿号列表）。
        if (failure == TextureResolveFailure.NoMatchingKey)
            return $"该着色器没有 {name} 对应的贴图参数（已跳过）。";

        if (failure == TextureResolveFailure.AmbiguousQualified)
        {
            // §5.6 组装细则：候选按 §5.4 第 2 排序键 ParameterIndex 升序（即模板声明序），
            // 建议后缀与之同序；不得改用旧的限定词长度序。
            var ordered = (candidates ?? [])
                .OrderBy(c => c.ParameterIndex)
                .ToList();
            var keys = string.Join(" / ", ordered.Select(c => c.ParameterKey));
            var suffixes = string.Join(" / ", ordered.Select(SuggestedSuffixFor));
            return $"该着色器没有通用的 {name} 槽位，候选为 {keys}；"
                 + $"请改用 {suffixes} 后缀，或改选其他着色器。";
        }

        return name;
    }

    /// <summary>
    /// 由候选的<b>派生通道名</b>机械派生建议后缀：转 <see cref="string.ToLowerInvariant"/> 后前置 <c>_</c>。
    /// </summary>
    /// <param name="candidate">P5 候选。</param>
    /// <returns>形如 <c>_foamnormal</c> 的建议后缀。</returns>
    public static string SuggestedSuffixFor(TextureKeyCandidate candidate) => candidate is null ? string.Empty : SuggestedSuffixForCore(candidate.DerivedChannel);

    /// <summary>
    /// 由参数键机械派生建议后缀：去 <c>Texture</c> 前缀 → <see cref="string.ToLowerInvariant"/> → 前置 <c>_</c>。
    /// 注意该形式<b>不</b>剥数字尾号，诊断文本请优先使用
    /// <see cref="SuggestedSuffixFor(TextureKeyCandidate)"/>。
    /// </summary>
    /// <param name="parameterKey">候选参数键。</param>
    /// <returns>形如 <c>_foamnormal</c> 的建议后缀。</returns>
    public static string SuggestedSuffixFor(string parameterKey)
    {
        if (string.IsNullOrEmpty(parameterKey)) return string.Empty;
        var stem = parameterKey.StartsWith(TexturePrefix, StringComparison.Ordinal)
            ? parameterKey[TexturePrefix.Length..]
            : parameterKey;
        return SuggestedSuffixForCore(stem);
    }

    private static string SuggestedSuffixForCore(string stem) => string.IsNullOrEmpty(stem) ? string.Empty : "_" + stem.ToLowerInvariant();

    /// <summary>
    /// 批量解析若干角色，返回 <c>角色 → 解析结果</c> 的映射（保持入参顺序）。
    /// </summary>
    /// <param name="shader">目标着色器模板。</param>
    /// <param name="roles">待解析角色集合；为 <c>null</c> 时返回空字典。</param>
    /// <returns>角色到解析结果的字典（键为值类型，不重复）。</returns>
    public static IReadOnlyDictionary<TextureRole, TextureRoleResolution> ResolveAll(ShaderTemplate shader, IEnumerable<TextureRole> roles)
    {
        var map = new Dictionary<TextureRole, TextureRoleResolution>();
        if (roles is null) return map;
        foreach (var role in roles)
        {
            if (map.ContainsKey(role)) continue;
            map[role] = Resolve(shader, role);
        }
        return map;
    }

    // ── 内部实现 ────────────────────────────────────────────────────────────

    /// <summary>§5.1 条件 ① + ②。</summary>
    private static bool IsCandidateParameter(ShaderParamTemplate p) => p is not null
        && p.Key is not null
        && p.Key.StartsWith(TexturePrefix, StringComparison.Ordinal)
        && (p.Kind == ShaderParamKind.Texture || p.Shape == ShaderValueShape.TextureOrVector);

    /// <summary>§5.1 的三步派生。</summary>
    private static bool TryParseKey(string key, out ParsedKey parsed)
    {
        parsed = default;
        if (string.IsNullOrEmpty(key)) return false;
        if (!key.StartsWith(TexturePrefix, StringComparison.Ordinal)) return false;

        var rest = key[TexturePrefix.Length..];
        if (rest.Length == 0) return false;

        var layer = 0;
        if (rest.StartsWith(LayerPrefix, StringComparison.Ordinal))
        {
            var digitsStart = LayerPrefix.Length;
            var i = digitsStart;
            while (i < rest.Length && char.IsAsciiDigit(rest[i])) i++;
            // ^Layer(\d+)(.+)$ ：必须既有数字又有非空后缀
            if (i > digitsStart && i < rest.Length)
            {
                var digits = rest.AsSpan(digitsStart, i - digitsStart);
                if (int.TryParse(digits, out var n) && n >= 1)
                {
                    layer = n;
                    rest = rest[i..];
                }
            }
        }

        // ^(.*?)(\d+)$ ，且 g1 非空 —— 数字只在末尾才算尾号（CubeMap 不受影响）
        var number = 0;
        string baseName;
        var end = rest.Length;
        while (end > 0 && char.IsAsciiDigit(rest[end - 1])) end--;
        if (end > 0 && end < rest.Length)
        {
            baseName = rest[..end];
            number = long.TryParse(rest.AsSpan(end), out var parsedLong)
                ? (int)Math.Min(parsedLong, int.MaxValue)
                : int.MaxValue;
        }
        else
        {
            baseName = rest;
        }

        parsed = new ParsedKey(layer, number, baseName);
        return baseName.Length > 0;
    }

    /// <summary>§5.3 分档判定。返回 <see cref="TextureKeyTier.None"/> 表示不合格。</summary>
    private static TextureKeyTier Classify(IReadOnlyList<string> tokens, ParsedKey parsed)
    {
        var primary = tokens[0];
        var channel = parsed.Channel;

        // P1 直名无缀：Texture{R}
        if (parsed.Layer == 0 && parsed.Number == 0 && Matches(channel, primary))
            return TextureKeyTier.ExactName;
        // P2 直名 + 数字尾缀：Texture{R}{N}
        if (parsed.Layer == 0 && parsed.Number >= 1 && Matches(channel, primary))
            return TextureKeyTier.ExactNameNumbered;
        // P3 Layer 前缀：TextureLayer{N}{R}
        if (parsed.Layer >= 1 && Matches(channel, primary))
            return TextureKeyTier.LayerPrefixed;
        // P4 派生通道名整体相等（此时启用别名）
        for (var i = 0; i < tokens.Count; i++)
        {
            if (Matches(channel, tokens[i]))
                return TextureKeyTier.DerivedChannelEqual;
        }
        // P5 带限定词的复合通道：前缀非空
        return QualifierOf(channel, tokens) is not null ? TextureKeyTier.QualifiedComposite : TextureKeyTier.None;
    }

    /// <summary>
    /// 取「通道名 = 某 Token + 非空前缀」中的前缀。同一角色的 Token 互不为后缀，
    /// 仍按最长 Token 优先挑选以保证确定性。
    /// </summary>
    private static string? QualifierOf(string channel, IReadOnlyList<string> tokens)
    {
        string? qualifier = null;
        var bestTokenLen = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Length == 0 || channel.Length <= token.Length) continue;
            if (!channel.EndsWith(token, StringComparison.OrdinalIgnoreCase)) continue;
            if (token.Length > bestTokenLen)
            {
                bestTokenLen = token.Length;
                qualifier = channel[..^token.Length];
            }
        }
        return string.IsNullOrEmpty(qualifier) ? null : qualifier;
    }

    private static bool Matches(string channel, string token) => token.Length > 0 && channel.Equals(token, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// §5.4 同档确定性取舍。P5 已不再需要 <c>qualifier.Length</c> 排序键——
    /// 多候选在 <c>Resolve</c> 阶段即判为未解析，唯一候选无需排序。
    /// </summary>
    private static IReadOnlyList<TextureKeyCandidate> SortByTier(List<TextureKeyCandidate> candidates, TextureKeyTier tier)
    {
        var ordered = tier switch
        {
            TextureKeyTier.ExactNameNumbered => candidates.OrderBy(c => c.TrailingNumber),
            TextureKeyTier.LayerPrefixed => candidates.OrderBy(c => c.LayerNumber),
            TextureKeyTier.DerivedChannelEqual => candidates.OrderBy(c => c.TrailingNumber).ThenBy(c => c.LayerNumber),
            _ => candidates.OrderBy(_ => 0),
        };
        return [.. ordered
            .ThenBy(c => c.ParameterIndex)
            .ThenBy(c => c.ParameterKey, StringComparer.Ordinal)];
    }

    private readonly struct ParsedKey(int layer, int number, string channel)
    {
        public int Layer { get; } = layer;
        public int Number { get; } = number;
        public string Channel { get; } = channel;
    }
}
