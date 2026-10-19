using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using VmatGenerator.Diagnostics;

namespace VmatGenerator;

/// <summary>
/// Application entry point. The .NET 10 built-in Fluent theme is switched on by
/// the <c>ThemeMode="System"</c> attribute in <c>App.xaml</c>; here we
/// expose the shared picker fallback directory and — critically — install the
/// global exception handlers that stop a failing control from killing the application.
///
/// 防闪退策略（对应 issues「点击控件报错直接闪退」）：
/// <list type="bullet">
/// <item><see cref="System.Windows.Application.DispatcherUnhandledException"/> —— UI 线程未处理异常。
/// 记日志 + <c>e.Handled = true</c> 标记已处理，进程不再退出。</item>
/// <item><see cref="System.AppDomain.UnhandledException"/> —— 非 UI 线程的
/// 致命异常，无法恢复，但至少落盘。</item>
/// <item><see cref="TaskScheduler.UnobservedTaskException"/> —— 未观察的 Task 异常，
/// 标记为已观察，避免进程在 GC 时被终止。</item>
/// </list>
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Fallback starting directory for file/folder pickers when the user has not
    /// chosen a folder yet.
    /// <para>
    /// This is deliberately <b>not</b> a materials directory. The application no
    /// longer auto-opens any folder on startup, and baking a developer's absolute
    /// path into the binary made the app useless on any other machine.
    /// </para>
    /// </summary>
    public static string PickerFallbackDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    // 旧的共享 MaterialScanner / VmatGenerator 字段已随参数编辑器一起删除：
    // 向导走的是 VmatBuildService 这条独立生成路径，不需要这两个共享实例。

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 让错误记录器能在任意线程把结果切回 UI 线程。
        ControlErrorRecorder.Attach(Dispatcher);

        InstallGlobalHandlers();
        LogStartupBanner();
    }

    /// <summary>安装三层全局异常拦截。</summary>
    private void InstallGlobalHandlers()
    {
        // ── 1. UI 线程未处理异常：这是「点控件就闪退」的主因 ──
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // ── 2. 后台线程致命异常：无法恢复，至少落盘 ──
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                ErrorLog.Write(ErrorSeverity.Fatal, "后台线程未处理异常", "(非 UI 线程)", ex);
            }
            ErrorLog.Info("进程即将退出", "(全局)");
        };

        // ── 3. 未观察的 Task 异常：标记已观察，避免 GC 时终止进程 ──
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ErrorLog.Write(ErrorSeverity.Warning, "未观察的 Task 异常", "(后台任务)", args.Exception);
            // 不再触发进程终止。
            args.SetObserved();
        };

        // 注：WPF 的数据绑定错误不会导致进程退出（只会渲染为空），且 .NET 10 已移除
        // PresentationTraceListener / PresentationTraceSources，无法再监听，
        // 因此这里不做绑定错误采集。绑定类问题请由业务代码用 Guard 显式记录。
    }

    /// <summary>
    /// UI 线程未处理异常的统一出口。记录控件信息后把异常标记为已处理，
    /// 应用继续运行 —— 这是「不闪退」的核心。
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            // 派发到 Dispatcher 之后 sender 通常已丢失，
            // 因此用「当前键盘焦点」定位用户刚才点的那个控件。
            var control = ControlErrorRecorder.DescribeFocused();

            ErrorLog.Write(ErrorSeverity.Error, "界面操作失败（已拦截）", control, e.Exception);

            // ★ 关键：不重新抛出，Dispatcher 不再终止进程。
            e.Handled = true;

            // 这里刻意<b>不</b>弹模态对话框：
            //   1) 模态框会阻塞 UI 线程，在无桌面会话（服务 / 自动化）下还会永久挂起；
            //   2) 异常往往连续发生，弹窗会淹没界面。
            // 改为由 MainWindow 订阅 ErrorLog.EntryLogged，在状态栏显示非阻塞的错误角标。
        }
        catch
        {
            // 兜底处理器本身出错时仍然标记已处理，宁可漏报也不要闪退。
            e.Handled = true;
        }
    }

    /// <summary>
    /// 把错误提示到状态栏（不阻塞）。MainWindow 订阅 <see cref="ErrorLog.EntryLogged"/> 实现。
    /// </summary>
    public static string BuildErrorBanner(int errorCount) => errorCount > 0
            ? $"⚠ 已拦截 {errorCount} 个错误，详情见「帮助 → 错误日志」。"
            : string.Empty;

    /// <summary>在启动横幅里记一条，方便确认日志系统本身工作正常。</summary>
    private static void LogStartupBanner()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("VMAT 生成器 启动");
            sb.AppendLine($"  运行时     : {Environment.Version}");
            sb.AppendLine($"  可执行文件 : {Environment.ProcessPath}");
            sb.AppendLine($"  工作目录   : {Directory.GetCurrentDirectory()}");
            sb.AppendLine($"  日志文件   : {ErrorLog.LogFilePath}");
            sb.AppendLine($"  日志目录   : {ErrorLog.LogDirectory}");
            ErrorLog.Info("应用启动", "(全局)");
            File.AppendAllText(ErrorLog.LogFilePath, sb.ToString() + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // 启动横幅失败不影响应用。
        }
    }
}