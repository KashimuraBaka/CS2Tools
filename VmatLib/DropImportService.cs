namespace VmatLib;

/// <summary>拖入内容的六类分档（规格 §2.2 的 D1–D6）。</summary>
public enum DropCategory
{
    /// <summary>空拖入：<c>FileDrop</c> 为空或全部是空白项。</summary>
    None = 0,

    /// <summary>D1 材质文件夹：目录且递归枚举到至少一个 <c>.vmat</c>。</summary>
    MaterialFolder,

    /// <summary>D2 <c>.vmat</c> 文件：拖入的全部条目都是 <c>.vmat</c> 文件。</summary>
    VmatFile,

    /// <summary>D3 贴图文件：拖入的全部条目都是贴图文件。</summary>
    TextureFiles,

    /// <summary>D4 只含贴图的文件夹。</summary>
    TextureOnlyFolder,

    /// <summary>D5 混合内容：展开后既有 <c>.vmat</c> 又有贴图。</summary>
    Mixed,

    /// <summary>D6 无关内容：既非 <c>.vmat</c> 也非贴图。</summary>
    Unsupported,
}

/// <summary>一次拖拽的展开结果（规格 §7.8）。不可变值对象。</summary>
/// <remarks>构造分析结果。</remarks>
/// <param name="category">六类分档之一。</param>
/// <param name="vmatFiles">展开后发现的 <c>.vmat</c> 文件。</param>
/// <param name="textureFiles">展开后发现的贴图文件。</param>
/// <param name="otherFiles">既非 <c>.vmat</c> 也非贴图的文件。</param>
/// <param name="materialRootCandidate">建议的材质根目录；不适用时为 <c>null</c>。</param>
/// <param name="textureRootCandidate">建议的贴图根目录；不适用时为 <c>null</c>。</param>
/// <param name="requiresRecursion">本次分析是否用到了递归枚举。</param>
/// <param name="summary">面向用户的一行说明。</param>
public sealed class DropAnalysis(
    DropCategory category,
    IReadOnlyList<string> vmatFiles,
    IReadOnlyList<string> textureFiles,
    IReadOnlyList<string> otherFiles,
    string? materialRootCandidate,
    string? textureRootCandidate,
    bool requiresRecursion,
    string summary)
{

    /// <summary>六类分档之一。</summary>
    public DropCategory Category { get; } = category;

    /// <summary>展开后发现的 <c>.vmat</c> 文件。</summary>
    public IReadOnlyList<string> VmatFiles { get; } = vmatFiles;

    /// <summary>展开后发现的贴图文件。</summary>
    public IReadOnlyList<string> TextureFiles { get; } = textureFiles;

    /// <summary>既非 <c>.vmat</c> 也非贴图的文件。</summary>
    public IReadOnlyList<string> OtherFiles { get; } = otherFiles;

    /// <summary>
    /// 建议的材质根目录；混合内容时取「第一个含 <c>.vmat</c> 的目录」，
    /// 纯文件时取「第一个 <c>.vmat</c> 的父目录」；不适用时为 <c>null</c>。
    /// </summary>
    public string? MaterialRootCandidate { get; } = materialRootCandidate;

    /// <summary>
    /// 建议的贴图根目录；纯贴图文件取公共父目录、只含贴图的文件夹取该目录本身；
    /// 不适用时为 <c>null</c>。
    /// </summary>
    public string? TextureRootCandidate { get; } = textureRootCandidate;

    /// <summary>本次分析是否用到了递归枚举。</summary>
    public bool RequiresRecursion { get; } = requiresRecursion;

    /// <summary>面向用户的一行说明。</summary>
    public string Summary { get; } = summary;

    /// <summary>没有任何可导入内容。</summary>
    public bool IsEmpty =>
        Category is DropCategory.None or DropCategory.Unsupported;

    /// <summary>调试用摘要。</summary>
    /// <returns>人可读的一行描述。</returns>
    public override string ToString() => $"{Category}：.vmat {VmatFiles.Count} 个、贴图 {TextureFiles.Count} 张、无关 {OtherFiles.Count} 个";
}

/// <summary>
/// 拖拽内容分析与分类（规格 §2.2）。把「拖进来一堆路径」翻译成
/// <see cref="DropAnalysis"/>，供 GUI 决定要不要改材质根目录、要不要跑
/// <see cref="TextureAssigner"/>。GUI 只负责取 <c>string[]</c> 与接线，本类不依赖 WPF。
///
/// <para><b>两个独立的递归开关。</b>§2.4 规定 <c>.vmat</c> 探测<b>始终</b>递归
/// （否则拖入 <c>materials/</c> 这类父目录会一个材质都找不到），
/// 而贴图枚举才受 <see cref="recurseTextureFolders"/> 控制——
/// 贴图目录动辄上千张，默认递归代价高，用户可随时关掉。</para>
///
/// <para><b>枚举预算。</b>每个目录最多枚举 5000 个条目，超出即截断，避免用户误拖
/// 整个磁盘把 GUI 卡死。</para>
///
/// <para><b>失败隔离。</b>单个目录 <c>UnauthorizedAccessException</c> 或断链只跳过该目录，
/// 不中断整体处理。</para>
/// </summary>
/// <remarks>
/// 构造拖拽分析器。
/// </remarks>
/// <param name="scanner">
/// 材质扫描器。分析阶段本身不需要真正解析 <c>.vmat</c>，这里保留引用以便 GUI
/// 复用同一实例、避免重复构造；通过 <see cref="Scanner"/> 访问。
/// </param>
/// <param name="recurseTextureFolders">拖入贴图文件夹时是否递归扫描子目录（与 .vmat 探测无关）。</param>
public sealed class DropImportService(MaterialScanner scanner, bool recurseTextureFolders)
{
    /// <summary>单个目录的枚举上限（§2.4）。</summary>
    public const int EnumerationBudgetPerDirectory = 5000;

    private const string VmatExtension = ".vmat";
    private readonly bool recurseTextureFolders = recurseTextureFolders;

    /// <summary>构造时传入的材质扫描器，供 GUI 复用（不参与本类的分析逻辑）。</summary>
    internal MaterialScanner Scanner { get; } = scanner;

    /// <summary>
    /// 判断一组拖拽路径是否值得接收（§2.3 的等价判定）。
    ///
    /// <para><b>v1.3 已裁决本签名为正确形式。</b>理由是 <c>VmatLib</c> 为纯 <c>net10.0</c> 类库，
    /// 不得引入 <c>System.Windows.IDataObject</c>；<c>IDataObject → string[]</c> 的转换
    /// 只发生在 GUI 层的 <c>MainWindow.FileDropOf</c>。其余 8 个 §7 锁定签名逐字实现。</para>
    /// </summary>
    /// <param name="fileDropPaths">
    /// 已从 <c>DataFormats.FileDrop</c> 取出的路径数组；允许为 <c>null</c>。
    /// </param>
    /// <returns>至少有一条非空白路径时返回 <c>true</c>。</returns>
    public static bool CanAccept(string[]? fileDropPaths)
    {
        if (fileDropPaths is null || fileDropPaths.Length == 0) return false;
        foreach (var path in fileDropPaths)
        {
            if (!string.IsNullOrWhiteSpace(path)) return true;
        }
        return false;
    }

    /// <summary>
    /// 展开并分类一组拖入路径（§2.2 的 D1–D6）。
    /// </summary>
    /// <param name="droppedPaths">从 <c>FileDrop</c> 取得的路径数组。</param>
    /// <returns>分类结果；输入为空时返回 <see cref="DropCategory.None"/>。</returns>
    public DropAnalysis Analyze(IReadOnlyList<string> droppedPaths)
    {
        var vmatFiles = new List<string>();
        var textureFiles = new List<string>();
        var otherFiles = new List<string>();
        var droppedDirectories = new List<string>();
        string? firstVmatDirectory = null;
        var sawAnyEntry = false;
        var truncated = false;
        var skippedSubfolderTextures = 0;

        foreach (var raw in droppedPaths ?? [])
        {
            var path = raw?.Trim();
            if (string.IsNullOrEmpty(path)) continue;
            sawAnyEntry = true;

            if (Directory.Exists(path))
            {
                droppedDirectories.Add(path);
                CollectDirectory(path, vmatFiles, textureFiles, otherFiles, ref truncated, ref skippedSubfolderTextures);
                if (firstVmatDirectory is null && vmatFiles.Count > 0) firstVmatDirectory = path;
                continue;
            }

            if (IsVmatFile(path))
            {
                vmatFiles.Add(path);
                continue;
            }

            if (TexturePathRules.IsTextureFile(path))
            {
                textureFiles.Add(path);
                continue;
            }

            otherFiles.Add(path);
        }

        if (!sawAnyEntry)
        {
            return new DropAnalysis(DropCategory.None, vmatFiles, textureFiles, otherFiles, null, null, false, "未识别可导入的内容，已忽略。");
        }

        var category = Classify(vmatFiles.Count, textureFiles.Count, droppedDirectories.Count);
        var materialRoot = category switch
        {
            DropCategory.MaterialFolder => firstVmatDirectory,
            DropCategory.VmatFile => ParentOf(vmatFiles),
            DropCategory.Mixed => firstVmatDirectory ?? ParentOf(vmatFiles),
            _ => null,
        };
        var textureRoot = category switch
        {
            DropCategory.TextureFiles => NullIfEmpty(TexturePathRules.CommonParentDirectory(textureFiles)),
            DropCategory.TextureOnlyFolder => droppedDirectories.Count > 0 ? droppedDirectories[0] : null,
            DropCategory.Mixed => NullIfEmpty(TexturePathRules.CommonParentDirectory(textureFiles)),
            _ => null,
        };

        var summary = BuildSummary(category, vmatFiles, textureFiles, otherFiles, truncated, skippedSubfolderTextures);

        return new DropAnalysis(
            category,
            vmatFiles,
            textureFiles,
            otherFiles,
            materialRoot,
            textureRoot,
            requiresRecursion: droppedDirectories.Count > 0 && recurseTextureFolders,
            summary);
    }

    /// <summary>判定六类分档（§2.2 的判定条件列）。</summary>
    private static DropCategory Classify(int vmatCount, int textureCount, int directoryCount) => vmatCount > 0 && textureCount > 0
            ? DropCategory.Mixed
            : vmatCount > 0
                ? directoryCount > 0 ? DropCategory.MaterialFolder : DropCategory.VmatFile
                : textureCount > 0
                    ? directoryCount > 0 ? DropCategory.TextureOnlyFolder : DropCategory.TextureFiles
                    : DropCategory.Unsupported;

    /// <summary>
    /// 递归展开一个目录。<c>.vmat</c> 与无关文件始终递归；贴图按
    /// <c>recurseTextureFolders</c> 决定深度——关闭时子目录里的贴图既不算贴图也不算无关文件，
    /// 只在摘要里说明，避免污染 D6 的扩展名清单。
    /// </summary>
    private void CollectDirectory(
        string directory,
        List<string> vmatFiles,
        List<string> textureFiles,
        List<string> otherFiles,
        ref bool truncated,
        ref int skippedSubfolderTextures)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            return; // 权限拒绝 / 断链 → 跳过该目录，不中断整体处理。
        }

        var count = 0;
        var root = SafeFullPath(directory);
        foreach (var file in files)
        {
            if (++count > EnumerationBudgetPerDirectory)
            {
                truncated = true;
                break;
            }

            try
            {
                if (IsVmatFile(file))
                {
                    vmatFiles.Add(file);
                    continue;
                }

                if (TexturePathRules.IsTextureFile(file))
                {
                    // §2.2 D4 把「贴图是否递归」锁为：true → AllDirectories，false → TopDirectoryOnly。
                    // 也就是关闭递归时<b>顶层贴图仍然要收</b>，只有子目录里的才跳过。
                    // （早先的写法在 recurseTextureFolders == false 时直接 continue，
                    //  把顶层贴图也一并丢掉，导致拖入贴图文件夹得到 0 张贴图、
                    //  分档退化成 Unsupported——配置里的这个开关等于把功能整个关死。）
                    if (recurseTextureFolders || IsDirectChild(root, file))
                    {
                        textureFiles.Add(file);
                    }
                    else
                    {
                        skippedSubfolderTextures++;
                    }
                    continue;
                }

                otherFiles.Add(file);
            }
            catch (Exception)
            {
                // 单个文件异常（路径过长、断链）跳过即可。
            }
        }
    }

    private static string BuildSummary(
        DropCategory category,
        List<string> vmatFiles,
        List<string> textureFiles,
        List<string> otherFiles,
        bool truncated,
        int skippedSubfolderTextures)
    {
        var vmatCount = vmatFiles.Count;
        var textureCount = textureFiles.Count;
        var otherCount = otherFiles.Count;
        var core = category switch
        {
            DropCategory.MaterialFolder => $"识别为材质文件夹：{vmatCount} 个 .vmat。",
            DropCategory.VmatFile => $"识别为 .vmat 文件：{vmatCount} 个。",
            DropCategory.TextureFiles => $"识别为贴图文件：{textureCount} 张。",
            DropCategory.TextureOnlyFolder => $"识别为贴图文件夹：{textureCount} 张贴图。",
            DropCategory.Mixed => $"识别为混合内容：{vmatCount} 个 .vmat、{textureCount} 张贴图。",
            DropCategory.Unsupported => $"未识别可导入的内容（{ExtensionList(otherFiles)}），已忽略。",
            _ => "未识别可导入的内容，已忽略。",
        };
        var tail = new List<string>();
        if (otherCount > 0) tail.Add($"忽略 {otherCount} 个无关文件");
        if (skippedSubfolderTextures > 0) tail.Add($"贴图递归已关闭，{skippedSubfolderTextures} 张子目录贴图未计入");
        if (truncated) tail.Add($"枚举已达 {EnumerationBudgetPerDirectory} 项上限，结果可能不完整");
        return tail.Count == 0 ? core : $"{core}（{string.Join("；", tail)}）";
    }

    /// <summary>无关文件的扩展名清单：去点、去重、升序，用「、」连接（§2.2 注）。</summary>
    private static string ExtensionList(IReadOnlyList<string> files)
    {
        var extensions = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            try
            {
                var ext = Path.GetExtension(file).TrimStart('.');
                if (ext.Length > 0) extensions.Add(ext);
            }
            catch (ArgumentException)
            {
                // 忽略无法取扩展名的路径。
            }
        }
        return string.Join("、", extensions);
    }

    private static bool IsVmatFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            return string.Equals(Path.GetExtension(path), VmatExtension, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string? ParentOf(List<string> vmatFiles)
    {
        if (vmatFiles.Count == 0) return null;
        try
        {
            return Path.GetDirectoryName(vmatFiles[0]);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    private static bool IsDirectChild(string root, string file)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(file));
            if (directory is null) return false;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(SafeFullPath(directory), root, comparison);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
