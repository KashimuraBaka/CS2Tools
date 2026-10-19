namespace VmatLib;

/// <summary>一次 <see cref="VmatBuildService.Build"/> 的进度快照。</summary>
/// <param name="index">已完成组数。</param>
/// <param name="total">总组数。</param>
/// <param name="message">面向用户的一行说明（可直接显示）。</param>
public sealed class VmatBuildProgress(int index, int total, string message)
{
    /// <summary>已完成组数。</summary>
    public int Index { get; } = index;

    /// <summary>总组数。</summary>
    public int Total { get; } = total;

    /// <summary>面向用户的一行说明。</summary>
    public string Message { get; } = message;

    /// <summary>百分比 0–100；总数为 0 时取 0。</summary>
    public double Percent => Total <= 0 ? 0d : Math.Clamp(Index * 100d / Total, 0d, 100d);
}

/// <summary>
/// 待写出的一个 <c>.vmat</c>：同一目录下、去掉后缀后基名相同的贴图归为一组。
/// </summary>
/// <remarks>
/// 例如 <c>wall_diff.png</c> / <c>wall_n.png</c> / <c>wall_ao.png</c> 去掉命中后缀后
/// 基名都是 <c>wall</c>，同目录下的它们合成一个 <c>wall.vmat</c>；
/// 不同目录的同名基名<b>不会</b>合并——同名覆盖会静默吃掉另一处材质。
/// </remarks>
/// <remarks>构造分组。</remarks>
/// <param name="directory">贴图所在目录（不带尾部分隔符）。</param>
/// <param name="baseName">去掉命中后缀后的基名。</param>
/// <param name="assignments">归入本组的分配记录。</param>
public sealed class VmatBuildGroup(string directory, string baseName, IReadOnlyList<TextureAssignment> assignments)
{
    private readonly List<TextureAssignment> _assignments = [.. assignments];

    /// <summary>贴图所在目录；为空表示与进程工作目录同层。</summary>
    public string Directory { get; } = directory;

    /// <summary>去掉命中后缀后的基名。</summary>
    public string BaseName { get; } = baseName;

    /// <summary>归入本组的分配记录。</summary>
    public IReadOnlyList<TextureAssignment> Assignments => _assignments;

    /// <summary>目标 <c>.vmat</c> 的完整路径：<c>Directory/BaseName.vmat</c>。</summary>
    public string TargetPath => Path.Combine(Directory, BaseName + ".vmat");
}

/// <summary>一次生成的结果汇总。</summary>
/// <remarks>构造结果。</remarks>
/// <param name="writtenFiles">已写出的 <c>.vmat</c> 路径。</param>
/// <param name="groups">全部分组（含未来可能被跳过的）。</param>
/// <param name="unassignedFiles">未命中任何启用规则、未写入的文件。</param>
/// <param name="conflicts">同槽位冲突记录。</param>
/// <param name="unresolvedRoles">命中规则但无法确定参数键的角色。</param>
/// <param name="totalFiles">本次输入的文件总数。</param>
/// <param name="skippedExisting">因目标已存在而<b>未写入</b>的路径。</param>
/// <param name="copiedImageFiles">随材质复制出去的贴图目标路径。</param>
/// <param name="skippedImageCopies">因已存在且未允许覆盖而未复制的贴图路径。</param>
/// <param name="missingBaseColorGroups">因组内没有颜色贴图而整体跳过的材质名。</param>
public sealed class VmatBuildResult(
    IReadOnlyList<string> writtenFiles,
    IReadOnlyList<VmatBuildGroup> groups,
    IReadOnlyList<string> unassignedFiles,
    IReadOnlyList<TextureConflict> conflicts,
    IReadOnlyList<TextureRoleResolution> unresolvedRoles,
    int totalFiles,
    IReadOnlyList<string> skippedExisting,
    IReadOnlyList<string>? copiedImageFiles = null,
    IReadOnlyList<string>? skippedImageCopies = null,
    IReadOnlyList<string>? missingBaseColorGroups = null)
{

    /// <summary>已写出的 <c>.vmat</c> 路径（与 <see cref="VmatBuildGroup.TargetPath"/> 一致）。</summary>
    public IReadOnlyList<string> WrittenFiles { get; } = writtenFiles;

    /// <summary>全部分组。</summary>
    public IReadOnlyList<VmatBuildGroup> Groups { get; } = groups;

    /// <summary>
    /// 未命中任何启用规则的文件——<b>不会被写进任何 .vmat</b>。
    /// 用户自定义后缀若无对应规则就落在这里，必须显式呈现，不能静默丢弃。
    /// </summary>
    public IReadOnlyList<string> UnassignedFiles { get; } = unassignedFiles;

    /// <summary>同槽位冲突记录（同一参数键被多张贴图占用）。</summary>
    public IReadOnlyList<TextureConflict> Conflicts { get; } = conflicts;

    /// <summary>命中规则但无法确定参数键的角色（诊断见 <see cref="TextureRoleResolution.DiagnosticText"/>）。</summary>
    public IReadOnlyList<TextureRoleResolution> UnresolvedRoles { get; } = unresolvedRoles;

    /// <summary>本次输入的文件总数。</summary>
    public int TotalFiles { get; } = totalFiles;

    /// <summary>
    /// 因目标 <c>.vmat</c> 已存在而<b>未写入</b>的路径（<c>overwriteExisting = false</c> 时）。
    /// </summary>
    /// <remarks>
    /// 生成是「拖入即写盘」的场景，默默覆盖用户已有的材质是不可接受的默认行为。
    /// 宁可跳过并在界面上明确告知，也不要替用户做这个决定。
    /// </remarks>
    public IReadOnlyList<string> SkippedExisting { get; } = skippedExisting;

    /// <summary>随材质一并复制到输出目录的<b>贴图文件</b>目标路径（只有 <see cref="VmatBuildService.BuildByGroup"/> 会填）。</summary>
    public IReadOnlyList<string> CopiedImageFiles { get; } = copiedImageFiles ?? [];

    /// <summary>因目的地已存在且未允许覆盖而<b>未复制</b>的贴图路径。</summary>
    public IReadOnlyList<string> SkippedImageCopies { get; } = skippedImageCopies ?? [];

    /// <summary>
    /// 因组内<b>没有 basecolor（颜色）贴图</b>而整体跳过的材质名。
    /// 颜色贴图是材质成立的底线，宁可不写也不产出没有底色的空壳材质。
    /// </summary>
    public IReadOnlyList<string> MissingBaseColorGroups { get; } = missingBaseColorGroups ?? [];
}

/// <summary>
/// 按贴图生成新的 <c>.vmat</c>：分配槽位 → 按基名分组 → 以着色器出厂基线为底稿写入路径值 → 落盘。
/// </summary>
/// <remarks>
/// <para><b>底稿来源</b>是 <see cref="ShaderTemplate"/> 的默认参数表，因此生成的是一个
/// 参数完整、可直接打开编辑的材质，而不是只含几张贴图的残缺文档。</para>
/// <para><b>为什么复用 <see cref="TextureAssigner.Assign"/></b>：拖放写入已有材质与
/// 从零生成走的是同一套分档与槽位判定（P1–P5）。两处各写一套判定，必然随时间漂移。</para>
/// </remarks>
public static class VmatBuildService
{
    /// <summary>
    /// 执行一次生成。
    /// </summary>
    /// <param name="shader">目标着色器；为 <c>null</c> 时不会有任何分配，所有文件都会
    /// 落进 <see cref="VmatBuildResult.UnassignedFiles"/> 语义的报告里。</param>
    /// <param name="textureFilePaths">用户提供的文件 / 文件夹路径；非贴图条目会被忽略并计入未命中。</param>
    /// <param name="rules">后缀规则表。</param>
    /// <param name="textureRoot">贴图根目录；为空时按 <see cref="TexturePathRules"/> 原样写绝对路径。</param>
    /// <param name="progress">进度回调；可传 <c>null</c>。</param>
    /// <param name="overwriteExisting">
    /// 目标 <c>.vmat</c> 已存在时是否覆盖。<b>默认 <c>false</c></b>——跳过并记入
    /// <see cref="VmatBuildResult.SkippedExisting"/>，绝不默默盖掉用户已有材质。
    /// </param>
    /// <param name="cancellationToken">取消令牌；每组开始前检查。约定摆在参数表最后。</param>
    public static VmatBuildResult Build(
        ShaderTemplate shader,
        IEnumerable<string> textureFilePaths,
        IReadOnlyList<TextureSuffixRule> rules,
        string? textureRoot,
        IProgress<VmatBuildProgress>? progress = null,
        bool overwriteExisting = false,
        CancellationToken cancellationToken = default)
    {
        var ruleList = rules ?? [];
        var matcher = new TextureSuffixMatcher(ruleList);

        // 分组必须在冲突消解「之前」完成。
        // TextureAssigner.Assign 按角色做全局冲突裁定：两个目录下的 wall_diff 都会
        // 解析到 TextureColor，先消解就会互相残杀，其中的一个被丢弃，
        // 而它们本该各自生成一个 wall.vmat。因此这里只借 matcher 求后缀与基名，
        // 真正的槽位分配留到组内单独做。
        var buckets = GroupByBaseName(textureFilePaths, matcher, out var unassigned, out var totalFiles);

        progress?.Report(new VmatBuildProgress(
            0, buckets.Count,
            $"已分配 {totalFiles - unassigned.Count} 张贴图，分为 {buckets.Count} 组，正在生成…"));

        var written = new List<string>(buckets.Count);
        var skipped = new List<string>();
        var conflicts = new List<TextureConflict>();
        var unresolved = new List<TextureRoleResolution>();
        var groups = new List<VmatBuildGroup>(buckets.Count);

        for (var i = 0; i < buckets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bucket = buckets[i];

            // 组内单独分配：冲突裁定因此被限制在「同一目录同一材质」内。
            var assign = TextureAssigner.Assign(shader, bucket.Files, ruleList, textureRoot);
            conflicts.AddRange(assign.Conflicts);
            unresolved.AddRange(assign.UnresolvedRoles);
            unassigned.AddRange(assign.UnassignedFiles);

            var group = new VmatBuildGroup(bucket.Directory, bucket.BaseName, assign.Assignments);
            groups.Add(group);

            if (!overwriteExisting && File.Exists(group.TargetPath))
            {
                skipped.Add(group.TargetPath);
                progress?.Report(new VmatBuildProgress(
                    i + 1, buckets.Count,
                    $"已跳过 {group.BaseName}.vmat（同名文件已存在，未覆盖）"));
                continue;
            }

            var values = new Dictionary<string, string>(shader.BuildDefaultValueMap());
            foreach (var a in group.Assignments)
            {
                // 同键多次出现时后者覆盖前者；冲突已由 TextureAssigner 单列在 Conflicts 里。
                values[a.ParameterKey] = a.VmatPath;
            }

            var text = VmatGenerator.Render(
                shader,
                values,
                systemAttributeOverrides: shader.SystemAttributeDefaults.ToDictionary(kv => kv.Key, kv => kv.Value));

            File.WriteAllText(group.TargetPath, text);
            written.Add(group.TargetPath);

            progress?.Report(new VmatBuildProgress(
                i + 1, buckets.Count,
                $"已生成 {group.BaseName}.vmat（{group.Assignments.Count} 张贴图）"));
        }

        return new VmatBuildResult(
            written, groups, unassigned, conflicts, unresolved, totalFiles, skipped);
    }

    /// <summary>一个待处理的分组：同一目录 + 同一基名。</summary>
    private sealed class Bucket(string directory, string baseName, List<string> files)
    {
        public string Directory { get; } = directory;
        public string BaseName { get; } = baseName;
        public List<string> Files { get; } = files;
    }

    /// <summary>
    /// 渲染单个分组的生成结果但<b>不写盘</b>，用于第二步的实时预览。
    /// </summary>
    /// <remarks>
    /// 走的是与 <see cref="BuildByGroup"/> 完全相同的渲染代码路径，因此预览里看到的
    /// 就是第三步会写出的内容——不存在「预览与实际不一致」这种最伤信任的偏差。
    /// 预览不写盘，故无需 <c>projectRoot</c>：输出位置不影响文本内容。
    /// </remarks>
    /// <param name="shader"></param>
    /// <param name="textureFilePaths"></param>
    /// <param name="rules"></param>
    /// <param name="assetsRoot"></param>
    /// <param name="roleOverrides">逐文件手动槽位覆盖，见 <see cref="TextureAssigner.Assign"/>。</param>
    public static string Preview(
        ShaderTemplate shader,
        IEnumerable<string> textureFilePaths,
        IReadOnlyList<TextureSuffixRule> rules,
        string? assetsRoot,
        IReadOnlyDictionary<string, TextureRole>? roleOverrides = null)
    {
        var assign = TextureAssigner.Assign(
            shader, textureFilePaths, rules ?? [], assetsRoot,
            roleOverrides);
        return RenderGroup(shader, assign.Assignments);
    }

    /// <summary>
    /// 按<b>调用方给定的分组计划</b>写出 .vmat。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Build"/> 的区别：后者自己按基名推断分组，适合拖一批文件直接生成；
    /// 本方法尊重 <paramref name="plan"/> 里已经确定的「哪个文件名归哪份材质」，
    /// 供向导第二步的手动指定生效。
    /// </remarks>
    /// <param name="shader"></param>
    /// <param name="plan">材质名 → 该材质包含的贴图文件路径。键可用
    /// 「相对目录 + <c>'\0'</c> + 材质名」区分<b>不同文件夹里的同名材质</b>：
    /// 它们会各自写进自己镜像出来的输出目录，而不是被并成一个材质；
    /// 写出的文件名只取分隔符之后的部分。</param>
    /// <param name="rules"></param>
    /// <param name="assetsRoot">贴图根目录；<c>.vmat</c> 内写相对它的路径。</param>
    /// <param name="projectRoot">.vmat 输出根目录；输出会镜像贴图相对资产根的子目录。</param>
    /// <param name="progress"></param>
    /// <param name="overwriteExisting">同名 .vmat 已存在时是否覆盖；默认 <c>false</c>，
    /// 向导第三步生成时传 <c>true</c>：直接覆盖，不再跳过。</param>
    /// <param name="roleOverrides">逐文件手动槽位覆盖，见 <see cref="TextureAssigner.Assign"/>。</param>
    /// <param name="cancellationToken">取消令牌；每组开始前检查。约定摆在参数表最后。</param>
    public static VmatBuildResult BuildByGroup(
        ShaderTemplate shader,
        IReadOnlyDictionary<string, List<string>> plan,
        IReadOnlyList<TextureSuffixRule> rules,
        string? assetsRoot,
        string? projectRoot,
        IProgress<VmatBuildProgress>? progress = null,
        bool overwriteExisting = false,
        IReadOnlyDictionary<string, TextureRole>? roleOverrides = null,
        CancellationToken cancellationToken = default)
    {
        var ruleList = rules ?? [];
        var written = new List<string>();
        var skipped = new List<string>();
        var unassigned = new List<string>();
        var conflicts = new List<TextureConflict>();
        var unresolved = new List<TextureRoleResolution>();
        var groups = new List<VmatBuildGroup>(plan.Count);
        var copiedImages = new List<string>();
        var skippedImages = new List<string>();
        var missingBaseColor = new List<string>();
        var totalFiles = 0;
        var index = 0;

        foreach (var (planKey, files) in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            totalFiles += files.Count;

            // 计划键可能带「相对目录 + \0」前缀：不同文件夹里的同名材质（concrete\wall
            // 与 floor\wall）是两个独立材质，调用方用它来阻止跨文件夹合并。
            // 真正写出的文件名只取分隔符之后的材质名；\0 是文件名非法字符，真实名字里不会出现。
            var sep = planKey.IndexOf('\0');
            var name = sep >= 0 ? planKey[(sep + 1)..] : planKey;

            // 冲突在「单个材质内部」裁定，分组之间互不影响。
            var assign = TextureAssigner.Assign(shader, files, ruleList, assetsRoot, roleOverrides);
            conflicts.AddRange(assign.Conflicts);
            unresolved.AddRange(assign.UnresolvedRoles);
            unassigned.AddRange(assign.UnassignedFiles);

            // basecolor 是材质成立的底线：没有颜色贴图的组整体跳过，
            // 绝不写出一份打开只剩默认白底、引擎里直接报错的空壳材质。
            var baseColor = assign.Assignments.FirstOrDefault(a => a.Role == TextureRole.Color);
            if (baseColor is null)
            {
                missingBaseColor.Add(name);
                progress?.Report(new VmatBuildProgress(index, plan.Count,
                    $"已跳过 {name}.vmat（组内没有 basecolor 颜色贴图，材质不成立）"));
                continue;
            }

            // 输出目录 = 项目根 + basecolor 贴图相对资产根的子目录：
            // 输出布局与资产布局一一对应，贴图也一并复制进同一文件夹。
            var targetDir = ResolveOutputDirectory(projectRoot, assetsRoot, baseColor.FilePath);
            var group = new VmatBuildGroup(targetDir, name, assign.Assignments);
            groups.Add(group);

            if (!overwriteExisting && File.Exists(group.TargetPath))
            {
                skipped.Add(group.TargetPath);
                progress?.Report(new VmatBuildProgress(index, plan.Count,
                    $"已跳过 {name}.vmat（同名文件已存在，未覆盖）"));
                continue;
            }

            Directory.CreateDirectory(targetDir);
            File.WriteAllText(group.TargetPath, RenderGroup(shader, assign.Assignments));
            written.Add(group.TargetPath);

            foreach (var a in assign.Assignments)
            {
                var dest = Path.Combine(targetDir, Path.GetFileName(a.FilePath));
                if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(a.FilePath), StringComparison.OrdinalIgnoreCase))
                    continue; // 项目根就设在贴图目录本身：已在原位，无需自我复制。
                if (!overwriteExisting && File.Exists(dest))
                {
                    skippedImages.Add(dest);
                    continue;
                }
                File.Copy(a.FilePath, dest, overwrite: true);
                copiedImages.Add(dest);
            }

            progress?.Report(new VmatBuildProgress(index, plan.Count,
                $"正在处理 {index}/{plan.Count}：已生成 {name}.vmat（{assign.Assignments.Count} 张贴图）"));
        }

        return new VmatBuildResult(
            written, groups, unassigned, conflicts, unresolved, totalFiles, skipped,
            copiedImages, skippedImages, missingBaseColor);
    }

    /// <summary>
    /// 输出目录 = 项目根拼上 basecolor 贴图相对资产根的子目录；
    /// 贴图不在资产根内（或没设资产根）时退到项目根本身。
    /// </summary>
    /// <remarks>
    /// 以 <b>basecolor</b> 那张贴图的位置为准：一个材质的其它贴图被手动从别处并进来了，
    /// 输出布局仍跟着材质本体走，而不是跟着某张贴图随机漂移。
    /// </remarks>
    private static string ResolveOutputDirectory(string? projectRoot, string? assetsRoot, string baseColorFilePath)
    {
        var root = string.IsNullOrWhiteSpace(projectRoot) ? Directory.GetCurrentDirectory() : projectRoot;
        if (string.IsNullOrWhiteSpace(assetsRoot)) return root;

        try
        {
            var textureDir = Path.GetDirectoryName(baseColorFilePath) ?? string.Empty;
            var relative = Path.GetRelativePath(assetsRoot, textureDir);
            return relative is "." or ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
                || Path.IsPathRooted(relative)
                ? root
                : Path.Combine(root, relative);
        }
        catch (ArgumentException)
        {
            // 非法路径字符等极端输入：退到项目根，让后面的写入自己抛出正常异常。
            return root;
        }
    }

    /// <summary>把分配结果渲染成最终文本。预览与写盘共用，保证两者一致。</summary>
    private static string RenderGroup(ShaderTemplate shader, IReadOnlyList<TextureAssignment> assignments)
    {
        var values = new Dictionary<string, string>(shader.BuildDefaultValueMap());
        foreach (var a in assignments)
            values[a.ParameterKey] = a.VmatPath;

        return VmatGenerator.Render(
            shader,
            values,
            systemAttributeOverrides: shader.SystemAttributeDefaults.ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    /// <summary>
    /// 按「所在目录 + 去掉命中后缀后的基名」把贴图分桶。
    /// </summary>
    /// <remarks>
    /// 保持首次出现顺序，生成顺序与用户看到的文件顺序一致，便于对照进度条。
    /// 未命中后缀或非贴图的文件进入 <paramref name="unassigned"/>。
    /// </remarks>
    private static List<Bucket> GroupByBaseName(
        IEnumerable<string> textureFilePaths,
        TextureSuffixMatcher matcher,
        out List<string> unassigned,
        out int totalFiles)
    {
        unassigned = [];
        var order = new List<string>();
        var map = new Dictionary<string, Bucket>(StringComparer.OrdinalIgnoreCase);
        totalFiles = 0;

        foreach (var path in textureFilePaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            totalFiles++;

            if (!TexturePathRules.IsTextureFile(path))
            {
                unassigned.Add(path);
                continue;
            }

            var match = matcher.Match(path);
            if (match is null || match.Role == TextureRole.Unknown)
            {
                unassigned.Add(path);
                continue;
            }

            var directory = Path.GetDirectoryName(path) ?? string.Empty;
            var baseName = StripSuffix(Path.GetFileNameWithoutExtension(path), match.MatchedSuffix);
            if (baseName.Length == 0) baseName = "material";

            var key = directory + "|" + baseName;
            if (!map.TryGetValue(key, out var bucket))
            {
                bucket = new Bucket(directory, baseName, []);
                map[key] = bucket;
                order.Add(key);
            }
            bucket.Files.Add(path);
        }

        return [.. order.Select(k => map[k])];
    }

    /// <summary>
    /// 去掉文件名末尾命中的归一化后缀，并清理残留的分隔符。
    /// </summary>
    /// <remarks>
    /// 匹配到的是<b>归一化后</b>的后缀（不含分隔符、已转小写），因此
    /// <c>wall_normal</c> 去掉 <c>normal</c> 后剩 <c>wall_</c>，还需再裁一次尾部下划线。
    /// </remarks>
    private static string StripSuffix(string fileNameNoExt, string matchedSuffix)
    {
        if (matchedSuffix.Length > 0
            && fileNameNoExt.Length >= matchedSuffix.Length
            && fileNameNoExt.EndsWith(matchedSuffix, StringComparison.OrdinalIgnoreCase))
        {
            fileNameNoExt = fileNameNoExt[..^matchedSuffix.Length];
        }
        return fileNameNoExt.Trim('_', '-', ' ');
    }
}