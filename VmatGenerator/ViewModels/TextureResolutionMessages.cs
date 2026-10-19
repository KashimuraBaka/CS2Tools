using VmatLib;

namespace VmatGenerator.ViewModels;

/// <summary>
/// 把「后缀已命中语义槽位、但该着色器写不进去」的情况翻译成状态栏/试算区的中文提示。
///
/// <para><b>不变量：VmatGenerator 侧不得二次拼装诊断文本。</b>候选键名、建议后缀、槽位显示名
/// 全部由 <see cref="TextureRoleResolution.DiagnosticText"/>（§5.6 逐字锁定）携带，
/// 本类只做一件事——<b>把 VmatLib 的文本按前缀原样透传</b>。VmatGenerator 自行重算候选或重新推导
/// 建议后缀会产生两份可能互相漂移的真相，本文件因此不再持有任何字符串派生逻辑。</para>
///
/// <para><b>两种拒绝写入的原因必须让用户能分辨</b>（§5.4 + §8.9）：
/// <list type="number">
/// <item><c>NoMatchingKey</c> —— 候选列表为空，该着色器压根没有这个语义槽位，
/// 用户<b>无需做任何处理</b>，故用中性前缀 <c>ℹ</c>。例：<c>csgo_water_fancy.vfx</c>
/// 没有通用 <c>Roughness</c>（它是标量 <c>g_flWaterRoughnessMax/Min</c>）。</item>
/// <item><c>AmbiguousQualified</c> —— 模板里存在多个同语义但用途不同的槽位，
/// 程序<b>不猜</b>，一个也不写，用户<b>必须</b>改名或换着色器，故用 <c>⚠</c>。例：
/// <c>csgo_water_fancy.vfx</c> 上普通 <c>_normal</c> 的候选是 <c>TextureFoamNormal</c> /
/// <c>TextureDebrisNormal</c> / <c>TextureWavesNormal</c>。</item>
/// </list></para>
///
/// <para><b>为什么必须区分前缀</b>：把「无需处理」呈现成告警，用户会因此去改动本来没问题的配置。
/// 这是 §5.4 把「猜错」换成「如实不写」的意义所在——如实还必须诚实到「哪一类不用管」。</para>
///
/// <para>本类只读 <see cref="TextureRoleResolution"/> 既有的 <c>IsResolved</c> /
/// <c>UnresolvedReason</c> / <c>Candidates</c> / <c>DiagnosticText</c>，
/// 不新增任何与 VmatLib 契约冲突的返回类型，也<b>不改变候选顺序</b>。</para>
/// </summary>
internal static class TextureResolutionMessages
{
    /// <summary>§8.9：<c>NoMatchingKey</c> 行的中性前缀。</summary>
    private const string InfoPrefix = "ℹ";

    /// <summary>§8.9：<c>AmbiguousQualified</c> 行的告警前缀。</summary>
    private const string WarnPrefix = "⚠";

    /// <summary>把单个未解析角色翻译成一行中文说明（不自带前缀，由 <see cref="Summarize"/> 统一加）。</summary>
    /// <param name="resolution">VmatLib 解析结果；已解析时原样返回其 <c>ParameterKey</c>。</param>
    /// <returns>
    /// 未解析时返回 <see cref="TextureRoleResolution.DiagnosticText"/> 的<b>原样透传</b>；
    /// 若该文本意外为空（理论上不应发生），退化为一句不含候选、不含建议的最小诊断。
    /// </returns>
    public static string Describe(TextureRoleResolution resolution)
    {
        if (resolution is null) return string.Empty;
        if (resolution.IsResolved) return resolution.ParameterKey ?? string.Empty;

        // 原样透传 VmatLib 的 §5.6 文本——候选与建议后缀都在里面，VmatGenerator 不再重算。
        var text = resolution.DiagnosticText;
        if (!string.IsNullOrWhiteSpace(text)) return text;

        // 兜底：只报「没写进去」，绝不凭空造候选或建议后缀。
        return resolution.UnresolvedReason == TextureResolveFailure.AmbiguousQualified
            ? $"该着色器没有通用的 {resolution.Role} 槽位，未写入任何参数。"
            : $"该着色器没有 {resolution.Role} 对应的贴图参数（已跳过）。";
    }

    /// <summary>
    /// 把一批未解析角色汇总为状态栏文案；全部已解析时返回空串。
    ///
    /// <para>按 <see cref="TextureResolveFailure"/> 分流：</para>
    /// <list type="bullet">
    /// <item><b>只有 <c>AmbiguousQualified</c></b> —— 逐个原样输出 <c>DiagnosticText</c>，
    /// 每行前缀 <c>⚠</c>（用户必须处理）。</item>
    /// <item><b>只有 <c>NoMatchingKey</c></b> —— 合并为一句
    /// <c>ℹ 有 {n} 个槽位在该着色器中没有对应贴图参数，已跳过。</c>，
    /// <c>n</c> 为该类角色数（这类无需用户处理，逐条罗列反而是噪音）。</item>
    /// <item><b>两者都有</b> —— 先 <c>ℹ</c> 行、后 <c>⚠</c> 行，用换行分隔（§8.9 表）。</item>
    /// </list>
    /// </summary>
    /// <param name="unresolvedRoles">本次写入后仍未解析的角色列表。</param>
    /// <returns>可直接放进状态栏的多行文本；无未解析角色时返回空串。</returns>
    public static string Summarize(IReadOnlyList<TextureRoleResolution> unresolvedRoles)
    {
        if (unresolvedRoles is null || unresolvedRoles.Count == 0) return string.Empty;

        var unresolved = unresolvedRoles.Where(r => r is { IsResolved: false }).ToList();
        if (unresolved.Count == 0) return string.Empty;

        var ambiguous = unresolved
            .Where(r => r.UnresolvedReason == TextureResolveFailure.AmbiguousQualified)
            .ToList();
        var noMatch = unresolved
            .Where(r => r.UnresolvedReason == TextureResolveFailure.NoMatchingKey)
            .ToList();

        var lines = new List<string>();

        if (noMatch.Count > 0)
            lines.Add($"{InfoPrefix} 有 {noMatch.Count} 个槽位在该着色器中没有对应贴图参数，已跳过。");

        foreach (var r in ambiguous)
        {
            // §5.6：多角色未解析时逐角色一行，不去重、不合并。
            var body = Describe(r);
            if (body.Length > 0) lines.Add($"{WarnPrefix} {body}");
        }

        return lines.Count == 0 ? string.Empty : string.Join(Environment.NewLine, lines);
    }
}