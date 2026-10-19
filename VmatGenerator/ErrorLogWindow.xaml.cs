using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using VmatGenerator.Diagnostics;

namespace VmatGenerator;

/// <summary>
/// 错误日志窗口：展示 <see cref="ErrorLog"/> 收集到的控件错误记录，
/// 并提供打开日志目录 / 复制 / 清空等操作。
///
/// 说明：本窗口刻意不参与 MVVM —— 它是一个纯诊断视图，直接读静态的
/// <see cref="ErrorLog"/> 即可，避免给主 ViewModel 增加与业务无关的状态。
/// </summary>
public partial class ErrorLogWindow : Window
{
    public ErrorLogWindow()
    {
        InitializeComponent();
        LoadEntries();
        WarnIfDiskWriteBroken();

        // 新记录实时追加（应用运行期间又出错了也能看到）。
        ErrorLog.EntryLogged += OnEntryLogged;
        Closed += (_, _) => ErrorLog.EntryLogged -= OnEntryLogged;
    }

    /// <summary>绑定给说明条显示日志路径。</summary>
    public static string LogPathText => $"日志文件：{ErrorLog.LogFilePath}";

    private void LoadEntries()
    {
        // 最新的排在最前面，方便查看刚发生的错误。
        var items = ErrorLog.Recent.Reverse().ToList();
        EntriesGrid.ItemsSource = items;
        UpdateSummary(items.Count);
        if (EntriesGrid.Items.Count > 0) EntriesGrid.SelectedIndex = 0;
    }

    /// <summary>
    /// 若日志根本写不进磁盘，必须明确告诉用户 —— 否则会出现
    /// 「界面提示已记录，但文件里什么都没有」的情况。
    /// </summary>
    private static void WarnIfDiskWriteBroken()
    {
        if (ErrorLog.LastWriteError is not { Length: > 0 } problem) return;
        MessageBox.Show(
            "错误日志无法写入磁盘，进程退出后记录将丢失。\n\n" +
            "内存中的记录仍然可以查看和复制。\n\n" +
            problem,
            "日志写入失败", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnEntryLogged(ErrorEntry entry) => Dispatcher.Invoke(() =>
                                                         {
                                                             if (EntriesGrid.ItemsSource is System.Collections.IList list)
                                                             {
                                                                 list.Insert(0, entry);
                                                                 UpdateSummary(list.Count);
                                                                 if (EntriesGrid.Items.Count > 0) EntriesGrid.SelectedIndex = 0;
                                                             }
                                                             else
                                                             {
                                                                 // 类型不匹配时退回整体刷新。
                                                                 LoadEntries();
                                                             }
                                                         });

    private void UpdateSummary(int count) => SummaryText.Text = ErrorLog.ErrorCount > 0
            ? $"共 {count} 条记录（本次运行 {ErrorLog.ErrorCount} 个错误）"
            : $"共 {count} 条记录";

    private void OnCopyClicked(object sender, RoutedEventArgs e) => ControlErrorRecorder.GuardWithDialog("复制日志记录", sender, () =>
                                                                         {
                                                                             if (EntriesGrid.SelectedItem is not ErrorEntry entry)
                                                                                 throw new InvalidOperationException("请先选中一条记录。");
                                                                             var text =
                                                                                 $"时间：{entry.TimeText}\n级别：{entry.SeverityText}\n操作：{entry.Operation}\n" +
                                                                                 $"控件：{entry.Control}\n异常：{entry.ExceptionType}\n消息：{entry.Message}\n\n{entry.Detail}";
                                                                             Clipboard.SetText(text);
                                                                             MessageBox.Show("已复制该记录的完整内容到剪贴板。", "错误日志",
                                                                                 MessageBoxButton.OK, MessageBoxImage.Information);
                                                                         });

    private void OnClearClicked(object sender, RoutedEventArgs e) => ControlErrorRecorder.GuardWithDialog("清空错误日志", sender, () =>
                                                                          {
                                                                              var answer = MessageBox.Show(
                                                                                  "确定要清空全部错误记录与磁盘日志文件吗？\n此操作不可撤销。",
                                                                                  "清空错误日志", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                                                                              if (answer != MessageBoxResult.Yes) return;
                                                                              ErrorLog.Clear();
                                                                              LoadEntries();
                                                                          });

    private void OnOpenFolderClicked(object sender, RoutedEventArgs e) => ControlErrorRecorder.GuardWithDialog("打开日志目录", sender, () =>
                                                                                   Process.Start(new ProcessStartInfo { FileName = ErrorLog.LogDirectory, UseShellExecute = true }));

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    /// <summary>切换选中行时，把完整堆栈显示到详情框。</summary>
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => DetailBox.Text = EntriesGrid.SelectedItem is ErrorEntry entry
            ? $"{entry.Operation}\n控件：{entry.Control}\n异常：{entry.ExceptionType}\n\n{entry.Detail}"
            : string.Empty;
}