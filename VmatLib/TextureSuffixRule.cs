namespace VmatLib;

/// <summary>
/// 一条「文件名词干后缀 → 语义槽位」的用户可配置映射（规格 §3.2 / §4）。
///
/// <para>刻意做成可变的 POCO 并保留无参构造函数：<see cref="VmatGeneratorSettings"/> 通过
/// <c>System.Text.Json</c> 直接反序列化本类型（<see cref="TextureRole"/> 由
/// <c>JsonStringEnumConverter</c> 按名称写入 / 读取），因此不需要任何自定义转换器，
/// 用户也能手工编辑 <c>settings.json</c>。匹配用的归一化副本由
/// <see cref="TextureSuffixMatcher"/> 在构造时快照，不在本类型上缓存，避免规则被
/// 改后匹配结果与显示值不一致。</para>
///
/// <para><b>顺序即优先级的一部分。</b>同一 <c>settings.rules</c> 数组内的下标是
/// §6.4 裁定多规则命中时的第三判据——它只在「命中后缀长度相同」且「同为整名 / 边界匹配」
/// 时生效，因此用户通过 GUI 上下移动规则就能确定性地打破平局。</para>
/// </summary>
public sealed class TextureSuffixRule
{
    /// <summary>
    /// 无参构造函数，供 <c>System.Text.Json</c> 反序列化与手工构造使用；
    /// <see cref="Suffix"/> 初始化为空串（而非 <c>null</c>），保证匹配器不会遇到空引用。
    /// </summary>
    public TextureSuffixRule() => Suffix = string.Empty;

    /// <summary>
    /// 用显式三元组构造一条启用状态的规则。
    /// </summary>
    /// <param name="suffix">后缀原文，例如 <c>_normal</c>；允许写成 <c>_Normal.png</c>，
    /// 匹配时会统一走 <see cref="TextureSuffixMatcher.NormalizeName"/> 归一化。</param>
    /// <param name="role">该后缀对应的语义槽位。</param>
    /// <param name="enabled">是否参与匹配；禁用项保留在数组中但被匹配器跳过。</param>
    public TextureSuffixRule(string suffix, TextureRole role, bool enabled = true)
    {
        Suffix = suffix ?? string.Empty;
        Role = role;
        Enabled = enabled;
    }

    /// <summary>后缀原文（未归一化）。匹配时以归一化结果为准。</summary>
    public string Suffix { get; set; }

    /// <summary>该后缀对应的语义槽位。</summary>
    public TextureRole Role { get; set; }

    /// <summary>是否参与匹配。</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 内部标记：JSON 里的 <c>role</c> 无法解析为枚举名。
    /// <c>TextureRole.Unknown</c> 本身是合法枚举值（用户可以显式把某条规则指向「未识别」），
    /// 因此无法靠值本身区分「用户主动选了 Unknown」与「这里本该是个合法角色名却写坏了」，
    /// 只能由 JSON 读取端记录。此标记为 <c>false</c> 时，本类型对外行为与既有定义完全一致。
    /// </summary>
    internal bool RoleWasUnparsable { get; set; }

    /// <summary>调试用摘要，形如 <c>_normal → Normal(启用)</c>。</summary>
    /// <returns>人可读的一行描述。</returns>
    public override string ToString() => $"{Suffix} → {Role}({(Enabled ? "启用" : "停用")})";
}