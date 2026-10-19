namespace VmatLib;

/// <summary>
/// 单个文件的后缀匹配结果（规格 §6.3 / §6.4）。不可变值对象，由
/// <see cref="TextureSuffixMatcher"/> 产出。
/// </summary>
/// <remarks>构造匹配结果。</remarks>
/// <param name="filePath">原始传入的文件路径（不做任何改写）。</param>
/// <param name="fileNameWithoutExtension">不带扩展名的文件名，用于冲突裁决的第三判据。</param>
/// <param name="normalizedName">归一化后的词干，例如 <c>concrete_wall_n</c>。</param>
/// <param name="role">命中的语义槽位。</param>
/// <param name="matchedSuffix">命中的规则后缀（<b>归一化后</b>的形式，例如 <c>normal</c>）。</param>
/// <param name="matchedSuffixLength">归一化后缀长度，§6.4 / §6.5 的首要裁定键。</param>
/// <param name="isWholeNameMatch">是否为「整名相等」命中（优先于尾部边界命中）。</param>
public sealed class TextureSuffixMatch(
    string filePath,
    string fileNameWithoutExtension,
    string normalizedName,
    TextureRole role,
    string matchedSuffix,
    int matchedSuffixLength,
    bool isWholeNameMatch)
{

    /// <summary>原始传入的文件路径。</summary>
    public string FilePath { get; } = filePath;

    /// <summary>不带扩展名的文件名，例如 <c>brick_normal.png</c> → <c>brick_normal</c>。</summary>
    public string FileNameWithoutExtension { get; } = fileNameWithoutExtension;

    /// <summary>归一化词干，例如 <c>Concrete-Wall _N.png</c> → <c>concrete_wall_n</c>。</summary>
    public string NormalizedName { get; } = normalizedName;

    /// <summary>命中的语义槽位。</summary>
    public TextureRole Role { get; } = role;

    /// <summary>命中的规则后缀（归一化形式，不含前导下划线）。</summary>
    public string MatchedSuffix { get; } = matchedSuffix;

    /// <summary>归一化后缀长度；最长者胜（§6.4 第 1 判据）。</summary>
    public int MatchedSuffixLength { get; } = matchedSuffixLength;

    /// <summary>
    /// <c>true</c> 表示整个词干等于规则后缀（如 <c>normal.png</c> 命中规则 <c>normal</c>）；
    /// <c>false</c> 表示「下划线边界处的后缀匹配」（如 <c>wall_normal.png</c>）。
    /// </summary>
    public bool IsWholeNameMatch { get; } = isWholeNameMatch;

    /// <summary>调试用摘要。</summary>
    /// <returns>人可读的一行描述。</returns>
    public override string ToString() => $"{FileNameWithoutExtension} → {Role}（{(IsWholeNameMatch ? "整名" : "边界")}命中「{MatchedSuffix}」）";
}

/// <summary>
/// 后缀 → 语义槽位匹配器（规格 §6）。
///
/// <para><b>归一化（§6.2）。</b>文件名与规则后缀都经过同一条 <see cref="NormalizeName"/>
/// 流水线：去扩展名 → 小写 → 把 <c>_ - 空格 .</c> 折叠为单个 <c>_</c> → 去首尾下划线。
/// 这让 <c>Concrete-Wall _N.png</c>、<c>concrete_wall_n.png</c>、
/// <c>CONCRETE-WALL_N.PNG</c> 三种写法等价。</para>
///
/// <para><b>边界必须显式存在（§6.3）。</b>只有 <c>n == s</c>（整名）或
/// <c>n.EndsWith("_" + s)</c> 才算命中。因此 <c>concrete_normal.png</c> 不会命中规则
/// <c>n</c>（末尾是 <c>_normal</c> 而非 <c>_n</c>），短后缀不会误吞长单词——这是种子表里
/// 同时存在 <c>_normal</c> 与 <c>_n</c> 却不会互相干扰的根本原因。</para>
///
/// <para><b>多规则裁定（§6.4）。</b>全部四档判据构成全序：归一化后缀长度降序 →
/// 整名命中优先 → 规则数组原始下标升序 → <see cref="string.CompareOrdinal(String, String)"/>
/// 升序。因此任意输入的结果唯一、可复现、可单测。</para>
///
/// <para><b>无副作用。</b>构造时对传入规则做一次快照（归一化后缀 + 原始下标），
/// 之后所有匹配只读内存，既不碰文件系统也不改传入集合，可安全并发调用。</para>
/// </summary>
public sealed class TextureSuffixMatcher
{
    private readonly RuleEntry[] _entries;

    /// <summary>
    /// 用一组规则构造匹配器。规则会被立即快照成不可变表，因此之后修改原集合不影响本实例。
    /// </summary>
    /// <param name="rules">规则集合；为 <c>null</c> 时按空规则表处理（任何文件都不会命中）。</param>
    public TextureSuffixMatcher(IEnumerable<TextureSuffixRule> rules)
    {
        var source = rules?.Where(r => r is not null).ToList() ?? [];
        Rules = source.AsReadOnly();
        _entries = new RuleEntry[source.Count];
        for (var i = 0; i < source.Count; i++)
        {
            _entries[i] = new RuleEntry(
                NormalizeName(source[i].Suffix ?? string.Empty),
                source[i].Role,
                source[i].Enabled,
                i);
        }
    }

    /// <summary>本匹配器生效的规则快照（保持传入顺序，即优先级下标）。</summary>
    public IReadOnlyList<TextureSuffixRule> Rules { get; }

    /// <summary>
    /// 对单个文件路径做后缀匹配。
    /// </summary>
    /// <param name="filePath">文件路径；只取文件名部分参与匹配。</param>
    /// <returns>命中最具体规则的结果；没有任何启用规则命中时返回 <c>null</c>。</returns>
    public TextureSuffixMatch? Match(string filePath)
    {
        var path = filePath ?? string.Empty;
        var fileNameWithoutExtension = SafeFileNameWithoutExtension(path);
        var normalized = NormalizeName(path);
        if (normalized.Length == 0) return null;

        RuleEntry? best = null;
        var bestWhole = false;
        foreach (var entry in _entries)
        {
            if (!entry.Enabled || entry.NormalizedSuffix.Length == 0) continue;   // ① 空后缀 → 跳过非法规则

            bool whole;
            if (string.Equals(normalized, entry.NormalizedSuffix, StringComparison.Ordinal))
            {
                whole = true;                                                    // ② 整名相等
            }
            else if (normalized.Length > entry.NormalizedSuffix.Length &&
                     normalized.EndsWith("_" + entry.NormalizedSuffix, StringComparison.Ordinal))
            {
                whole = false;                                                   // ③ 下划线边界匹配
            }
            else
            {
                continue;
            }

            if (best is null || IsBetter(entry, whole, best.Value, bestWhole))
            {
                best = entry;
                bestWhole = whole;
            }
        }

        if (best is null) return null;
        var winner = best.Value;
        return new TextureSuffixMatch(
            path,
            fileNameWithoutExtension,
            normalized,
            winner.Role,
            winner.NormalizedSuffix,
            winner.NormalizedSuffix.Length,
            bestWhole);
    }

    /// <summary>
    /// 批量匹配。保持输入顺序；未命中的文件不会出现在结果里（调用方需要自行计入
    /// 「未匹配」列表）。
    /// </summary>
    /// <param name="filePaths">文件路径集合；为 <c>null</c> 时返回空列表。</param>
    /// <returns>全部命中项，顺序与输入一致。</returns>
    public IReadOnlyList<TextureSuffixMatch> MatchAll(IEnumerable<string> filePaths)
    {
        if (filePaths is null) return [];
        var results = new List<TextureSuffixMatch>();
        foreach (var path in filePaths)
        {
            var m = Match(path);
            if (m is not null) results.Add(m);
        }
        return results;
    }

    /// <summary>
    /// 归一化一个文件名或规则后缀（§6.2）：
    /// <list type="number">
    ///   <item><description>去文件类型扩展名（<c>Path.GetFileNameWithoutExtension</c>）；</description></item>
    ///   <item><description>转小写（<c>ToLowerInvariant</c>）；</description></item>
    ///   <item><description><c>_ - 空格 .</c> 四种分隔符统一替换为 <c>_</c>；</description></item>
    ///   <item><description>连续多个 <c>_</c> 折叠为一个；</description></item>
    ///   <item><description>去掉首尾 <c>_</c>。</description></item>
    /// </list>
    /// 规则后缀同样走这条流水线，因此用户把 <c>_Normal.png</c> 写进配置也不会漏匹配。
    /// </summary>
    /// <param name="name">待归一化的名称；<c>null</c> / 空串返回 <c>""</c>。</param>
    /// <returns>归一化结果，永不为 <c>null</c>。</returns>
    public static string NormalizeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;

        var stem = SafeFileNameWithoutExtension(name);
        if (stem.Length == 0) return string.Empty;

        var sb = new System.Text.StringBuilder(stem.Length);
        foreach (var ch in stem.ToLowerInvariant())
        {
            sb.Append(ch is '_' or '-' or ' ' or '.' ? '_' : ch);
        }

        var collapsed = new System.Text.StringBuilder(sb.Length);
        var prevUnderscore = false;
        foreach (var ch in sb.ToString())
        {
            if (ch == '_')
            {
                if (prevUnderscore) continue;
                prevUnderscore = true;
            }
            else
            {
                prevUnderscore = false;
            }
            collapsed.Append(ch);
        }

        return collapsed.ToString().Trim('_');
    }

    /// <summary>§6.4 裁定：candidate 是否严格优于 current。</summary>
    private static bool IsBetter(RuleEntry candidate, bool candidateWhole, RuleEntry current, bool currentWhole)
    {
        // 1. 命中后缀归一化长度降序（normal > n；ambient_occlusion > ao）
        if (candidate.NormalizedSuffix.Length != current.NormalizedSuffix.Length)
            return candidate.NormalizedSuffix.Length > current.NormalizedSuffix.Length;
        // 2. 整名命中优先
        if (candidateWhole != currentWhole) return candidateWhole;
        // 3. 规则数组原始下标升序
        if (candidate.OriginalIndex != current.OriginalIndex)
            return candidate.OriginalIndex < current.OriginalIndex;
        // 4. 后缀序数升序
        return string.CompareOrdinal(candidate.NormalizedSuffix, current.NormalizedSuffix) < 0;
    }

    private static string SafeFileNameWithoutExtension(string value)
    {
        try
        {
            return Path.GetFileNameWithoutExtension(value) ?? string.Empty;
        }
        catch (ArgumentException)
        {
            // 极端路径字符不会在 .NET 上抛，但手工 catch 保证匹配器永远不抛。
            return value;
        }
    }

    private readonly struct RuleEntry(string normalizedSuffix, TextureRole role, bool enabled, int originalIndex)
    {
        public string NormalizedSuffix { get; } = normalizedSuffix;
        public TextureRole Role { get; } = role;
        public bool Enabled { get; } = enabled;
        public int OriginalIndex { get; } = originalIndex;
    }
}