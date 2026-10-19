namespace VmatLib;

/// <summary>
/// 贴图路径规则（规格 §6.1 / §6.6）：扩展名判定、相对路径换算、公共父目录。
/// 纯静态、无副作用（只读文件系统元信息，不做任何写入）。
/// </summary>
public static class TexturePathRules
{
    /// <summary>
    /// 贴图扩展名集合（§6.1，锁定）。比较一律 <see cref="StringComparison.OrdinalIgnoreCase"/>。
    /// 刻意<b>不</b>包含 <c>.vmat</c>——否则拖入材质时会被误判为贴图。
    /// </summary>
    public static IReadOnlyList<string> TextureExtensions { get; } =
    [
        ".png", ".tga", ".jpg", ".jpeg", ".bmp", ".exr",
        ".hdr", ".pfm", ".dds", ".vtex", ".vtf", ".tif", ".tiff",
    ];

    /// <summary>Windows 上路径比较不区分大小写；其余平台按序数比较。</summary>
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// 判断路径是否为受支持的贴图文件。
    /// </summary>
    /// <param name="path">待判断路径；<c>null</c> / 空串返回 <c>false</c>。</param>
    /// <returns>扩展名命中 <see cref="TextureExtensions"/> 时返回 <c>true</c>。</returns>
    public static bool IsTextureFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string extension;
        try
        {
            extension = Path.GetExtension(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
        if (string.IsNullOrEmpty(extension)) return false;
        foreach (var known in TextureExtensions)
        {
            if (string.Equals(extension, known, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// 尝试把文件路径换算成相对贴图根目录的、正斜杠形式的 VMAT 值（§6.6）。
    /// </summary>
    /// <param name="filePath">文件路径。</param>
    /// <param name="textureRoot">贴图根目录；为空或目录不存在时一律失败。</param>
    /// <returns>
    /// 位于根目录内时返回如 <c>wall/concrete_normal.png</c> 的相对路径（<c>\</c> 已改写为 <c>/</c>）；
    /// 位于根目录外、<paramref name="textureRoot"/> 为空 / 不存在、或无法换算时返回 <c>null</c>。
    /// </returns>
    public static string? TryGetRelativeVmatPath(string filePath, string? textureRoot)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        if (string.IsNullOrWhiteSpace(textureRoot)) return null;
        if (!Directory.Exists(textureRoot)) return null;

        try
        {
            var rootFull = Path.GetFullPath(textureRoot);
            var fileFull = Path.GetFullPath(filePath);
            var relative = Path.GetRelativePath(rootFull, fileFull);

            // 根路径本身（GetRelativePath 对同路径返回 "."）与 ".." 开头的越界路径都判为失败。
            return relative.Length == 0 || relative == "."
                ? null
                : IsOutside(relative) ? null : Path.IsPathRooted(relative) ? null : relative.Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// 最终写入 VMAT 的贴图路径（§6.6）：
    /// 位于贴图根目录内的写相对路径并统一为正斜杠；其余情况<b>原样写回</b>原路径
    /// （刻意不改动斜杠风格，让用户一眼看出路径不在贴图根下）。
    /// </summary>
    /// <param name="filePath">文件路径。</param>
    /// <param name="textureRoot">贴图根目录，可为 <c>null</c> / 空。</param>
    /// <returns>写入 .vmat 的最终字符串。</returns>
    public static string ToVmatPath(string filePath, string? textureRoot) => string.IsNullOrEmpty(filePath) ? string.Empty : TryGetRelativeVmatPath(filePath, textureRoot) ?? filePath;

    /// <summary>
    /// 求一组文件的公共父目录（拖入贴图文件夹、但配置里还没有贴图根目录时用作建议值）。
    /// </summary>
    /// <param name="filePaths">文件路径集合；为 <c>null</c> 或空时返回 <c>""</c>。</param>
    /// <returns>最深的公共祖先目录（不含尾部分隔符），无解时返回 <c>""</c>。</returns>
    public static string CommonParentDirectory(IEnumerable<string> filePaths)
    {
        if (filePaths is null) return string.Empty;

        string? common = null;
        foreach (var raw in filePaths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string directory;
            try
            {
                directory = Path.GetDirectoryName(Path.GetFullPath(raw)) ?? string.Empty;
            }
            catch (ArgumentException)
            {
                continue;
            }
            catch (NotSupportedException)
            {
                continue;
            }
            catch (PathTooLongException)
            {
                continue;
            }
            if (directory.Length == 0) continue;

            common = common is null
                ? directory
                : CommonPrefix(common, directory);
            if (common.Length == 0) return string.Empty;
        }

        return common ?? string.Empty;
    }

    /// <summary>逐段求两个目录的最长公共前缀目录。</summary>
    private static string CommonPrefix(string a, string b)
    {
        var sa = TrimTrailingSeparators(a);
        var sb = TrimTrailingSeparators(b);
        if (sa.Length == 0 || sb.Length == 0) return string.Empty;
        if (sa.Equals(sb, PathComparison)) return sa;

        var shorter = sa.Length <= sb.Length ? sa : sb;
        var longer = sa.Length <= sb.Length ? sb : sa;
        if (!longer.StartsWith(shorter, PathComparison)) return string.Empty;
        if (longer.Length == shorter.Length) return shorter;

        // shorter 必须恰好停在目录边界上，否则 D:\a\ab 与 D:\a\abc 会被误判为同一目录。
        var boundary = shorter.Length;
        while (boundary > 0 && longer[boundary] != Path.DirectorySeparatorChar && longer[boundary] != Path.AltDirectorySeparatorChar)
            boundary--;
        if (boundary == 0) return string.Empty;
        var result = longer[..boundary];
        return result.Length == 2 && result[1] == ':' ? result + Path.DirectorySeparatorChar : result;
    }

    /// <summary>去掉目录尾部分隔符，但保留盘符根（<c>C:\</c> 不裁成 <c>C:</c>）。</summary>
    private static string TrimTrailingSeparators(string directory)
    {
        var trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0
            ? string.Empty
            : trimmed.Length == 2 && trimmed[1] == ':'
            ? trimmed + Path.DirectorySeparatorChar
            : trimmed;
    }

    /// <summary><c>..\</c> / <c>../</c> 开头即表示路径越出根目录。</summary>
    private static bool IsOutside(string relative) => relative == ".."
        || relative.StartsWith("../", StringComparison.Ordinal)
        || relative.StartsWith("..\\", StringComparison.Ordinal);
}