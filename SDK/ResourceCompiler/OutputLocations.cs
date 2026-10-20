using System;
using System.IO;

namespace SDK.ResourceCompiler;

/// <summary>
/// 产物落地位置解析（t28u）。
/// DSH 沙盒把进程写入关在工作区内（实测：项目目录可写，%TEMP% / C:\Windows\Temp / F: 全部被拒），
/// 所以回退级必须是**项目内**目录，而不是临时目录。
/// </summary>
public static class OutputLocations
{
    /// <summary>项目内产物目录名（在含 CS2-Tools.sln 的目录下）。</summary>
    public const string FolderName = "Compiled";

    private const string SolutionName = "CS2-Tools.sln";

    /// <summary>
    /// 从 AppContext.BaseDirectory 向上寻找含 CS2-Tools.sln 的目录，返回其下 Compiled 子目录；
    /// 找不到（例如独立发布的布局）返回 null，由调用方退化到源目录 / 临时目录。
    /// 标准 bin 布局 BaseDirectory = ...\CS2-Tools\AssetCompiler\bin\x64\Debug\net10.0-windows\，向上 5 级即到。
    /// </summary>
    public static string? ProjectLocalDirectory()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && d is not null; i++, d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, SolutionName)))
                return Path.Combine(d.FullName, FolderName);
        }
        return null;
    }

    /// <summary>最后兜底：临时目录（沙盒内多半不可写，仅对不受限启动有意义）。</summary>
    public static string TempDirectory() => Path.Combine(Path.GetTempPath(), "CS2-Tools-Compiled");
}
