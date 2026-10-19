using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace VmatGenerator.Diagnostics;

/// <summary>
/// 专门记录「出错控件」的辅助组件。
///
/// WPF 里抛异常时通常拿不到 <c>sender</c>（异常经过 Dispatcher 派发时已经丢失），
/// 因此这里提供两条互补的识别路径：
/// <list type="number">
/// <item><see cref="Describe(object?)"/> —— 事件处理器里直接把 <c>sender</c> 传进来，
/// 能拿到控件类型、Name、显示文本、绑定路径等完整信息。</item>
/// <item><see cref="DescribeFocused()"/> —— 全局兜底兜住异常时，取当前键盘焦点元素，
/// 也就是用户「刚才点的那个控件」。</item>
/// </list>
/// </summary>
public static class ControlErrorRecorder
{
    /// <summary>
    /// 描述一个控件，用于写进错误日志。用 <c>·</c> 连接各项，异常安全。
    /// </summary>
    /// <param name="sender">事件里的 sender（可能是控件、DataTemplate 内的元素等）。</param>
    public static string Describe(object? sender)
    {
        var sb = new StringBuilder();
        try
        {
            switch (sender)
            {
                case null:
                    sb.Append("(无 sender)");
                    break;

                case FrameworkElement fe:
                    sb.Append(fe.GetType().Name);
                    if (!string.IsNullOrEmpty(fe.Name)) sb.Append($" Name='{fe.Name}'");
                    var automationId = AutomationProperties.GetName(fe);
                    if (!string.IsNullOrEmpty(automationId)) sb.Append($" AutomationId='{automationId}'");
                    AppendContent(sb, fe);
                    AppendBindingPath(sb, fe);
                    AppendDataContext(sb, fe);
                    AppendPosition(sb, fe);
                    break;

                case ContentElement ce:
                    sb.Append(ce.GetType().Name);
                    break;

                default:
                    sb.Append(sender.GetType().Name);
                    break;
            }
        }
        catch (Exception ex)
        {
            try { sb.Append("(描述控件时出错：").Append(ex.Message).Append(')'); } catch { /* 放弃 */ }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 描述当前键盘焦点元素（用户最后交互的控件）。没有焦点元素时返回占位文本。
    /// </summary>
    public static string DescribeFocused()
    {
        try
        {
            var focused = Keyboard.FocusedElement;
            return focused is null ? "(无焦点控件)" : "焦点 " + Describe(focused);
        }
        catch
        {
            return "(无法读取焦点控件)";
        }
    }

    /// <summary>
    /// 执行一段可能失败的操作；抛异常时记录（含控件描述）并返回 <c>false</c>，
    /// <b>不会</b>向上传播，因此调用方不会闪退。
    /// </summary>
    /// <param name="operation">操作名，例如「打开文件夹」「刷新材质列表」。</param>
    /// <param name="sender">触发操作的控件（可为 <c>null</c>）。</param>
    /// <param name="action">要执行的逻辑。</param>
    /// <returns>未抛异常返回 <c>true</c>；被拦截返回 <c>false</c>。</returns>
    public static bool Guard(string operation, object? sender, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Error(operation, Describe(sender), ex);
            return false;
        }
    }

    /// <summary>
    /// <see cref="Guard"/> 的返回值版本：成功返回 <paramref name="fallback"/>，
    /// 失败则记录并返回 <paramref name="fallback"/>。
    /// </summary>
    public static T Guard<T>(string operation, object? sender, Func<T> func, T fallback)
    {
        try
        {
            return func();
        }
        catch (Exception ex)
        {
            ErrorLog.Error(operation, Describe(sender), ex);
            return fallback;
        }
    }

    /// <summary>
    /// 执行可能失败的操作，失败时弹一个「不中断应用」的错误对话框，
    /// 并把日志路径告诉用户。
    /// </summary>
    public static bool GuardWithDialog(string operation, object? sender, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            var control = Describe(sender);
            ErrorLog.Error(operation, control, ex);
            ReportToUser(operation, control, ex);
            return false;
        }
    }

    /// <summary>
    /// 执行可能返回结果、且可能失败的操作。成功返回 <c>(true, 结果)</c>；
    /// 失败时记录并返回 <c>(false, default)</c>，不会向上传播。
    /// </summary>
    public static (bool Ok, T? Value) GuardWithDialog<T>(string operation, object? sender, Func<T> func)
    {
        try
        {
            return (true, func());
        }
        catch (Exception ex)
        {
            var control = Describe(sender);
            ErrorLog.Error(operation, control, ex);
            ReportToUser(operation, control, ex);
            return (false, default);
        }
    }

    /// <summary>向用户展示错误详情（不阻断应用运行）。</summary>
    public static void ReportToUser(string operation, string control, Exception ex)
    {
        try
        {
            var msg =
                $"操作「{operation}」发生错误，应用已跳过该操作。\n\n" +
                $"控件：{control}\n" +
                $"异常：{ex.GetType().Name}\n" +
                $"{ex.Message}\n\n" +
                $"详细信息已写入：\n{ErrorLog.LogFilePath}";
            MessageBox.Show(msg, "操作失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch
        {
            // 连对话框都弹不出来时静默忽略 —— 记录已经写过了。
        }
    }

    /// <summary>把按钮 / 菜单项 / 列表项的可见文本追加进描述。</summary>
    private static void AppendContent(StringBuilder sb, FrameworkElement fe)
    {
        try
        {
            // 注意 MenuItem 继承自 HeaderedContentControl，必须放在更具体的
            // ContentControl 分支之前或用独立分支，否则模式会被前一个 arm 吞掉。
            var text = fe switch
            {
                ContentControl cc when cc.Content is not null => cc.Content.ToString(),
                HeaderedContentControl hcc => hcc.Header?.ToString(),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(text))
            {
                var flat = text.Replace('\n', ' ').Replace('\r', ' ');
                if (flat.Length > 40) flat = flat[..40] + "…";
                sb.Append($" 文本='{flat}'");
            }
        }
        catch
        {
            // 忽略。
        }
    }

    /// <summary>如果元素上挂了数据绑定，追加绑定路径（最有助于定位问题）。</summary>
    private static void AppendBindingPath(StringBuilder sb, FrameworkElement fe)
    {
        try
        {
            var expr = BindingOperations.GetBindingExpression(fe, FrameworkElement.DataContextProperty);
            if (expr?.ParentBinding?.Path?.Path is { Length: > 0 } path)
                sb.Append($" 绑定='{path}'");
        }
        catch
        {
            // 非 DependencyObject 或没有绑定时忽略。
        }
    }

    /// <summary>追加 DataContext 类型，帮助判断是哪个 ViewModel 出问题。</summary>
    private static void AppendDataContext(StringBuilder sb, FrameworkElement fe)
    {
        try
        {
            if (fe.DataContext is not null)
                sb.Append($" DataContext={fe.DataContext.GetType().Name}");
        }
        catch
        {
            // 忽略。
        }
    }

    /// <summary>追加控件在窗口中的位置与尺寸，便于在截图里定位。</summary>
    private static void AppendPosition(StringBuilder sb, FrameworkElement fe)
    {
        try
        {
            if (!fe.IsLoaded || fe.ActualWidth <= 0) return;

            // 相对所在 Window 的坐标；不在任何 Window 内（例如 Popup）时只记录尺寸。
            if (Window.GetWindow(fe) is { } owner)
            {
                var p = fe.TransformToAncestor(owner).Transform(new Point(0, 0));
                sb.Append($" 位置=({p.X:0},{p.Y:0})");
            }
            sb.Append($" 尺寸={fe.ActualWidth:0}x{fe.ActualHeight:0}");
        }
        catch
        {
            // 控件不在可视树中时忽略。
        }
    }

    /// <summary>取当前 UI 线程的调度器，用于把后台异常切回 UI 线程处理。</summary>
    public static Dispatcher? UiDispatcher { get; private set; }

    /// <summary>记录调度器，供 <see cref="InvokeOnUi"/> 使用。</summary>
    public static void Attach(Dispatcher dispatcher) => UiDispatcher = dispatcher;

    /// <summary>
    /// 在 UI 线程上执行一段逻辑；异常被记录而不上抛。
    /// 若当前已在 UI 线程则直接执行，否则排队等待。
    /// </summary>
    public static bool InvokeOnUi(string operation, Action action)
    {
        try
        {
            var dispatcher = UiDispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                dispatcher.Invoke(action, DispatcherPriority.Normal);
            }
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Error(operation, DescribeFocused(), ex);
            return false;
        }
    }
}