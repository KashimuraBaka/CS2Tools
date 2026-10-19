namespace VmatLib;

/// <summary>
/// 一条「文件 → 着色器参数」的最终分配记录（规格 §7.7）。不可变值对象。
/// </summary>
/// <remarks>构造分配记录。</remarks>
/// <param name="filePath">源文件路径（原样）。</param>
/// <param name="vmatPath">写入 VMAT 的最终值（贴图根目录内为相对路径 + 正斜杠）。</param>
/// <param name="role">命中的语义槽位。</param>
/// <param name="parameterKey">目标参数键。</param>
/// <param name="matchedSuffix">命中的归一化后缀。</param>
public sealed class TextureAssignment(string filePath, string vmatPath, TextureRole role, string parameterKey, string matchedSuffix)
{

    /// <summary>源文件路径（原样，不做改写）。</summary>
    public string FilePath { get; } = filePath;

    /// <summary>写入 VMAT 的最终值。</summary>
    public string VmatPath { get; } = vmatPath;

    /// <summary>命中的语义槽位。</summary>
    public TextureRole Role { get; } = role;

    /// <summary>目标参数键，例如 <c>TextureLayer1Normal</c>。</summary>
    public string ParameterKey { get; } = parameterKey;

    /// <summary>命中的归一化后缀，例如 <c>normal</c>。</summary>
    public string MatchedSuffix { get; } = matchedSuffix;

    /// <summary>调试用摘要。</summary>
    /// <returns>人可读的一行描述。</returns>
    public override string ToString() => $"{ParameterKey} ← {VmatPath}（{Role}）";
}

/// <summary>
/// 「多个贴图命中同一槽位」的冲突记录（规格 §6.5）。不可变值对象。
/// </summary>
/// <remarks>构造冲突记录。</remarks>
/// <param name="role">发生冲突的槽位。</param>
/// <param name="keptFilePath">胜出并将被写入的文件路径。</param>
/// <param name="droppedFilePaths">被淘汰、未写入的文件路径。</param>
/// <param name="reason">面向用户的中文原因说明。</param>
public sealed class TextureConflict(TextureRole role, string keptFilePath, IReadOnlyList<string> droppedFilePaths, string reason)
{

    /// <summary>发生冲突的槽位。</summary>
    public TextureRole Role { get; } = role;

    /// <summary>胜出的文件路径。</summary>
    public string KeptFilePath { get; } = keptFilePath;

    /// <summary>被淘汰的文件路径（不会写入任何参数）。</summary>
    public IReadOnlyList<string> DroppedFilePaths { get; } = droppedFilePaths;

    /// <summary>
    /// 面向用户的原因说明，形如
    /// <c>同槽位冲突（Normal），已保留 brick_normal.png</c>。
    /// </summary>
    public string Reason { get; } = reason;

    /// <summary>调试用摘要。</summary>
    /// <returns>人可读的一行描述。</returns>
    public override string ToString() => $"{Reason}；淘汰 {DroppedFilePaths.Count} 个";
}

/// <summary>
/// 一次贴图自动分配的结果（规格 §7.7）。不可变值对象。
/// </summary>
/// <remarks>构造分配结果。</remarks>
/// <param name="assignments">成功分配记录。</param>
/// <param name="conflicts">同槽位冲突记录。</param>
/// <param name="unassignedFiles">非贴图文件与被显式排除的文件路径。</param>
/// <param name="unresolvedRoles">命中了规则但无法确定参数键的角色解析结果。</param>
/// <param name="totalFiles">本次输入的文件总数。</param>
/// <param name="suggestedTextureRoot">当传入的贴图根目录为空时建议的贴图根目录。</param>
public sealed class TextureAssignResult(
    IReadOnlyList<TextureAssignment> assignments,
    IReadOnlyList<TextureConflict> conflicts,
    IReadOnlyList<string> unassignedFiles,
    IReadOnlyList<TextureRoleResolution> unresolvedRoles,
    int totalFiles,
    string? suggestedTextureRoot)
{

    /// <summary>成功分配记录，按槽位首次出现顺序排列。</summary>
    public IReadOnlyList<TextureAssignment> Assignments { get; } = assignments;

    /// <summary>同槽位冲突记录。</summary>
    public IReadOnlyList<TextureConflict> Conflicts { get; } = conflicts;

    /// <summary>
    /// 非贴图文件或被显式排除的文件路径。
    /// 未命中后缀规则的<b>贴图</b>不再落在这里——它们默认按 Color 槽位参与分配。
    /// </summary>
    public IReadOnlyList<string> UnassignedFiles { get; } = unassignedFiles;

    /// <summary>
    /// 命中了规则、但无法确定参数键的角色解析结果。
    /// 典型来源：P5 多候选并列（水面着色器的 <c>Normal</c>），或模板确实没有对应键。
    /// </summary>
    public IReadOnlyList<TextureRoleResolution> UnresolvedRoles { get; } = unresolvedRoles;

    /// <summary>本次输入的文件总数（去重后）。</summary>
    public int TotalFiles { get; } = totalFiles;

    /// <summary>
    /// 当传入的 <c>textureRoot</c> 为空时，按所有输入文件给出的建议贴图根目录；
    /// 传入非空时为 <c>null</c>，表示「已有贴图根目录，不必改动」。
    /// </summary>
    public string? SuggestedTextureRoot { get; } = suggestedTextureRoot;

    /// <summary>没有任何文件被写入时返回 <c>true</c>，GUI 据此断言「未置脏」。</summary>
    public bool HasNoWrites => Assignments.Count == 0;

    /// <summary>调试用摘要。</summary>
    /// <returns>人可读的一行描述。</returns>
    public override string ToString() => $"共 {TotalFiles} 个文件 → 写入 {Assignments.Count} 项、冲突 {Conflicts.Count} 组、未命中 {UnassignedFiles.Count} 个、未解析 {UnresolvedRoles.Count} 个槽位";
}

/// <summary>
/// 贴图自动分配规划器（规格 §6.4 – §6.6）。
///
/// <para><b>纯函数、无副作用。</b>只读取入参与内存中的规则表，不写任何文件、不改全局状态、
/// 不触碰当前编辑器。相同入参恒等出参，因此可以直接单测与推理；GUI 拿到结果后自行决定
/// 如何把胜者贴图填进对应槽位（现为快速导航的扫描表格）。</para>
///
/// <para><b>处理流水线。</b></para>
/// <list type="number">
///   <item><description>逐文件过滤出贴图，并做后缀匹配（§6.3 / §6.4）；
///   <b>未命中规则的贴图默认按 <see cref="TextureRole.Color"/> 槽位参与分配</b>
///   （后缀长度记 0，冲突时让位给真后缀命中者）；非贴图文件进入
///   <see cref="TextureAssignResult.UnassignedFiles"/>。</description></item>
///   <item><description>同一槽位多个文件时按 §6.5 的四键全序选唯一胜者，其余记入
///   <see cref="TextureConflict"/>；<b>冲突绝不覆盖已写入的值</b>。</description></item>
///   <item><description>胜者按 <see cref="TextureRoleResolver.Resolve"/> 解析出参数键；
///   解析不出（含 P5 多候选并列）时该文件<b>不写入</b>，解析结果记入
///   <see cref="TextureAssignResult.UnresolvedRoles"/>，GUI 可直接展示
///   <see cref="TextureRoleResolution.DiagnosticText"/>。</description></item>
///   <item><description>用 <see cref="TexturePathRules.ToVmatPath"/> 换算最终写入值（§6.6）。</description></item>
/// </list>
///
/// <para><b>不变量。</b>同一个参数键最多出现于 <see cref="TextureAssignResult.Assignments"/>
/// 一次；同一个角色最多产生一条分配与一条冲突记录。</para>
/// </summary>
public static class TextureAssigner
{
    /// <summary>
    /// 规划一批贴图文件到目标着色器参数键的分配方案。
    /// </summary>
    /// <param name="shader">目标着色器模板；为 <c>null</c> 时全部命中规则的文件都会落入
    /// <see cref="TextureAssignResult.UnresolvedRoles"/>，不会产生任何分配。</param>
    /// <param name="textureFilePaths">待分配的贴图文件路径集合；允许含非贴图条目（会被忽略并计入未命中）。</param>
    /// <param name="rules">后缀规则表；为 <c>null</c> 或为空时所有贴图都按默认 Color 槽位处理。</param>
    /// <param name="textureRoot">贴图根目录；为空 / 不存在时按 §6.6 原样写回绝对路径。</param>
    /// <param name="roleOverrides">
    /// 可选的<b>手动槽位覆盖</b>（完整路径 → 槽位；比较器由调用方负责，Windows 上
    /// 用大小写不敏感）。界面上逐行手改的槽位靠它进入分配结果：命中过规则的按覆盖值
    /// 改派，从未命中的按覆盖值<b>加入</b>（用户点名一个槽位，胜过「后缀不认就丢弃」）。
    /// 覆盖值是 <see cref="TextureRole.Unknown"/> 的文件视为<b>显式排除</b>，落进未命中列表。
    /// 同槽位竞争时覆盖条目的后缀长度记 0，真后缀命中者胜出——
    /// 手动指定是「补位」，不是「抢位」。
    /// </param>
    /// <returns>分配方案、冲突说明与未命中文件列表。</returns>
    public static TextureAssignResult Assign(
        ShaderTemplate shader,
        IEnumerable<string> textureFilePaths,
        IReadOnlyList<TextureSuffixRule> rules,
        string? textureRoot,
        IReadOnlyDictionary<string, TextureRole>? roleOverrides = null)
    {
        var input = NormalizeInput(textureFilePaths);
        var matcher = new TextureSuffixMatcher(rules ?? []);

        var unassigned = new List<string>();
        var byRole = new Dictionary<TextureRole, List<GroupBucket>>();
        var roleOrder = new List<TextureRole>();

        foreach (var path in input)
        {
            var forced = LookupOverride(roleOverrides, path);
            if (forced is TextureRole.Unknown)
            {
                // 调用方明确说这张不参与（行被取消勾选 / 槽位未知）：后缀再像也不写。
                unassigned.Add(path);
                continue;
            }

            if (!TexturePathRules.IsTextureFile(path))
            {
                unassigned.Add(path);
                continue;
            }

            var match = matcher.Match(path);
            if (forced is TextureRole forcedRole)
            {
                match = match switch
                {
                    null => new TextureSuffixMatch(path, Path.GetFileNameWithoutExtension(path),
                        string.Empty, forcedRole, string.Empty, 0, false),
                    _ when match.Role == forcedRole => match,
                    _ => new TextureSuffixMatch(match.FilePath, match.FileNameWithoutExtension,
                        match.NormalizedName, forcedRole,
                        match.MatchedSuffix, match.MatchedSuffixLength, match.IsWholeNameMatch),
                };
            }
            else if (match is null || match.Role == TextureRole.Unknown)
            {
                // 未命中任何后缀规则的贴图默认按 Color 槽位参与分配：多数散图本身就是
                // 颜色贴图，「不认识就丢弃」让用户每张都要手动点名槽位。默认条目
                // 后缀长度记 0，与手动覆盖同一套「补位不抢位」语义：同槽位竞争时
                // 真后缀命中者必然胜出。非贴图文件与显式排除仍进未命中列表。
                match = new TextureSuffixMatch(path, Path.GetFileNameWithoutExtension(path),
                    string.Empty, TextureRole.Color, string.Empty, 0, false);
            }

            if (!byRole.TryGetValue(match.Role, out var bucket))
            {
                bucket = [];
                byRole[match.Role] = bucket;
                roleOrder.Add(match.Role);
            }
            bucket.Add(new GroupBucket(match));
        }

        var assignments = new List<TextureAssignment>();
        var conflicts = new List<TextureConflict>();
        var unresolved = new List<TextureRoleResolution>();

        foreach (var role in roleOrder)
        {
            var bucket = byRole[role];
            var winner = PickWinner(bucket);

            if (bucket.Count > 1)
            {
                var dropped = bucket
                    .Where(b => !string.Equals(b.Match.FilePath, winner.Match.FilePath, StringComparison.Ordinal))
                    .Select(b => b.Match.FilePath)
                    .ToList();
                if (dropped.Count > 0)
                {
                    conflicts.Add(new TextureConflict(
                        role,
                        winner.Match.FilePath,
                        dropped,
                        $"同槽位冲突（{role}），已保留 {Path.GetFileName(winner.Match.FilePath)}"));
                }
            }

            var resolution = TextureRoleResolver.Resolve(shader, role);
            if (!resolution.IsResolved || string.IsNullOrEmpty(resolution.ParameterKey))
            {
                unresolved.Add(resolution);
                continue;
            }

            assignments.Add(new TextureAssignment(
                winner.Match.FilePath,
                TexturePathRules.ToVmatPath(winner.Match.FilePath, textureRoot),
                role,
                resolution.ParameterKey,
                winner.Match.MatchedSuffix));
        }

        var suggested = string.IsNullOrWhiteSpace(textureRoot)
            ? NullIfEmpty(TexturePathRules.CommonParentDirectory(input))
            : null;

        return new TextureAssignResult(
            assignments,
            conflicts,
            unassigned,
            unresolved,
            input.Count,
            suggested);
    }

    /// <summary>
    /// §6.5 同槽位多贴图的确定性取舍：归一化后缀长度降序 → 整名命中优先 →
    /// 不带扩展名的文件名 <see cref="string.CompareOrdinal(String, String)"/> 升序 → 完整路径序数升序。
    /// </summary>
    private static GroupBucket PickWinner(List<GroupBucket> bucket)
    {
        var best = bucket[0];
        for (var i = 1; i < bucket.Count; i++)
        {
            if (IsBetter(bucket[i], best)) best = bucket[i];
        }
        return best;
    }

    private static bool IsBetter(GroupBucket candidate, GroupBucket current)
    {
        var a = candidate.Match;
        var b = current.Match;
        if (a.MatchedSuffixLength != b.MatchedSuffixLength) return a.MatchedSuffixLength > b.MatchedSuffixLength;
        if (a.IsWholeNameMatch != b.IsWholeNameMatch) return a.IsWholeNameMatch;
        var byName = string.CompareOrdinal(a.FileNameWithoutExtension, b.FileNameWithoutExtension);
        return byName != 0 ? byName < 0 : string.CompareOrdinal(a.FilePath, b.FilePath) < 0;
    }

    /// <summary>
    /// 查手动槽位覆盖：键为原始文件路径，比较器由调用方负责（Windows 上用大小写
    /// 不敏感，与 <see cref="NormalizeInput"/> 的去重语义一致）。原样返回覆盖值，
    /// 包括 <see cref="TextureRole.Unknown"/>——它表示调用方的<b>显式排除</b>。
    /// </summary>
    private static TextureRole? LookupOverride(
        IReadOnlyDictionary<string, TextureRole>? overrides, string path) => overrides is null || overrides.Count == 0 ? null : !overrides.TryGetValue(path, out var role) ? null : role;

    /// <summary>去掉空白项并按完整路径去重（大小写按平台语义），保持输入顺序。</summary>
    private static List<string> NormalizeInput(IEnumerable<string> paths)
    {
        var result = new List<string>();
        if (paths is null) return result;
        var seen = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var trimmed = raw.Trim();
            if (seen.Add(trimmed)) result.Add(trimmed);
        }
        return result;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    private readonly struct GroupBucket(TextureSuffixMatch match)
    {
        public TextureSuffixMatch Match { get; } = match;
        public TextureRole Role { get; } = match.Role;
    }
}