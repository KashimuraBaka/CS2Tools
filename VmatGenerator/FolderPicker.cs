using Microsoft.Win32;
using System.IO;
using VmatGenerator.Diagnostics;

namespace VmatGenerator;

/// <summary>
/// 文件夹选择器，封装 <see cref="OpenFolderDialog"/>。
///
/// <para><b>为什么用 WPF 自带的这个而不是 WinForms 的 <c>FolderBrowserDialog</c>：</b>
/// <c>OpenFolderDialog</c> 属于 <c>PresentationFramework</c>（WPF），纯 WPF 工程直接可用；
/// <c>FolderBrowserDialog</c> 则要引入 <c>UseWindowsForms</c>，随之而来的
/// <c>ImplicitUsings</c> 二义引用与 <c>WFO0003</c> 的高 DPI 整改，为一个选择对话框
/// 去改整个应用的 DPI 行为并不划算。</para>
///
/// <para><b>为什么早期版本要绕开它：</b>当时的 <c>OpenFolderDialog</c> 只有单选的
/// <c>FolderName</c>，拿不到多选结果，因此曾经改用 <c>OpenFileDialog</c> 的
/// 「伪文件夹」模式顶替——那种模式下列表里仍能按文件名过滤、扩展名栏也照常显示，
/// 与「选文件夹」的预期不符。<c>.NET 10</c> 起该对话框已提供
/// <see cref="OpenFolderDialog.Multiselect"/> 与 <see cref="OpenFolderDialog.FolderNames"/>，
/// 这层将就已经没有必要了。</para>
///
/// <para>拖放进快速导航窗口同样可以给出文件夹，因此本对话框只覆盖「按钮」这条路，
/// 不是唯一入口。</para>
/// </summary>
internal static class FolderPicker
{
    /// <summary>弹出单选文件夹对话框。</summary>
    /// <param name="title">对话框标题。</param>
    /// <param name="initialDirectory">初始目录；为空或不存在时由 Shell 自行决定。</param>
    /// <returns>用户选中的目录完整路径；取消或出错返回 <c>null</c>。</returns>
    public static string? Pick(string title, string? initialDirectory)
    {
        var picked = PickCore(title, initialDirectory, multiselect: false);
        return picked.Count > 0 ? picked[0] : null;
    }

    /// <summary>弹出多选文件夹对话框。</summary>
    /// <param name="title">对话框标题。</param>
    /// <param name="initialDirectory">初始目录；为空或不存在时由 Shell 自行决定。</param>
    /// <returns>用户选中的目录（已去重、已归一）；取消或出错返回空列表。</returns>
    public static IReadOnlyList<string> PickMany(string title, string? initialDirectory) => PickCore(title, initialDirectory, multiselect: true);

    /// <summary>两种模式共用的实现：对话框一律只在用户点「选择」后返回结果。</summary>
    private static IReadOnlyList<string> PickCore(string title, string? initialDirectory, bool multiselect)
    {
        var (ok, folders) = ControlErrorRecorder.GuardWithDialog("选择文件夹", null, () =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = title,
                Multiselect = multiselect,
                InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : null,
            };

            if (dlg.ShowDialog() != true) return Array.Empty<string>();

            // 单选读 FolderName、多选读 FolderNames；两者都可能带尾部分隔符或短路径，
            // 统一交给 Normalize 归一。
            var picked = multiselect ? dlg.FolderNames : [dlg.FolderName];
            return [.. picked
                .Select(Normalize)
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => p!)
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        });

        return ok && folders is not null ? folders : [];
    }

    /// <summary>把对话框返回值归一成一个真实存在的目录。</summary>
    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (Directory.Exists(path)) return Path.GetFullPath(path);

        // 选中的项可能刚好在对话框关闭后被删除，或指向一个不存在的路径；
        // 退回其上级目录，好过什么都不给。
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        return !string.IsNullOrEmpty(parent) && Directory.Exists(parent) ? parent : null;
    }
}