using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using VmatGenerator.Diagnostics;
using VmatGenerator.ViewModels;

namespace VmatGenerator;

/// <summary>
/// 主窗口 = <b>贴图快速导航</b>三步向导本体。
///
/// <para>旧主窗口的材质树 / 参数编辑器 / KV 预览 / 打开保存等全部删除后，
/// 快速导航从模态对话框升格为唯一窗口；DataContext 由 XAML 直接建立
/// （<c>Window.DataContext</c>），构造函数因此不再接受 ViewModel 参数。</para>
///
/// <para>每个事件处理器都用 <see cref="ControlErrorRecorder"/> 包住：
/// 即使某个控件的逻辑抛异常，也只是记录 + 提示，不会让整个应用退出。</para>
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // 非阻塞的错误角标：任何控件出错都在底栏提示，不弹模态框。
        ErrorLog.EntryLogged += OnErrorLogged;
        Closed += (_, _) =>
        {
            ErrorLog.EntryLogged -= OnErrorLogged;
            // 唯一窗口关闭即进程退出；顺手取消并释放构建取消令牌源。
            try { ViewModel.Dispose(); } catch { /* 退出路径上的失败不值得再记一笔 */ }
        };

        // 启动白闪修复：窗口句柄一建好就请求 DWM 把本窗口「隐身」（cloak），
        // 直到 WPF 完成首帧渲染（ContentRendered）才解除。
        // 白闪的成因是窗口已经出现在屏幕上、但 WPF 的第一帧（以及 Fluent 的
        // Mica 背景）还没合成出来，这期间 DWM 显示的是白色初始表面。
        // cloak 让这段时间整个窗口对合成器不可见，窗口遂「直接以最终形态出现」。
        // 方案出自 dotnet/wpf#10513 / #5853 维护者给出的官方规避手法；
        // cloak 失败不致命（最坏退回原有白闪），解除失败才是要防的事故，
        // 因此只有 cloak 成功后才挂解除逻辑，且解除在 ContentRendered 必经路径上。
        SourceInitialized += OnSourceInitializedCloakStart;
        ContentRendered += OnContentRenderedUncloak;
    }

    // ─── 启动白闪修复（DWM cloak） ────────────────────────────────────────

    /// <summary>本次启动是否成功 cloak 过；只有 cloak 成功才需要（也会执行）解除。</summary>
    private bool _cloakedForStartup;

    private void OnSourceInitializedCloakStart(object? sender, EventArgs e)
    {
        SourceInitialized -= OnSourceInitializedCloakStart;
        var hwnd = new WindowInteropHelper(this).Handle;
        try
        {
            var cloak = 1; // 1 = 隐身，0 = 现身
            if (DwmSetWindowAttribute(hwnd, DwmwaCloak, ref cloak, sizeof(int)) == 0)
            {
                _cloakedForStartup = true;
            }
        }
        catch (Exception ex)
        {
            // 装饰性问题：记录即可，绝不影响启动。
            ErrorLog.Write(ErrorSeverity.Warning, "启动白闪规避（cloak）", nameof(MainWindow), ex);
        }

        try
        {
            // 去掉系统标题栏后，DWM 不再自动给窗口画圆角；显式请求「圆角」，
            // 让无边框窗口与 Win11 普通窗口观感一致（Win10 不认此属性，静默失败即可）。
            var round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ErrorSeverity.Warning, "窗口圆角", nameof(MainWindow), ex);
        }
    }

    private void OnContentRenderedUncloak(object? sender, EventArgs e)
    {
        if (!_cloakedForStartup) return;
        _cloakedForStartup = false;
        ContentRendered -= OnContentRenderedUncloak;
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var cloak = 0;
            DwmSetWindowAttribute(hwnd, DwmwaCloak, ref cloak, sizeof(int));
        }
        catch (Exception ex)
        {
            // 理论上到这里 hwnd 必然有效（cloak 刚成功过）；万一失败窗口会保持隐身，
            // 记一条错误方便排查——正常路径不会走到这里。
            ErrorLog.Write(ErrorSeverity.Error, "启动白闪规避（解除 cloak）", nameof(MainWindow), ex);
        }
    }

    /// <summary>DWMWA_CLOAK（dwmapi.h）：13。</summary>
    private const int DwmwaCloak = 13;

    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE（dwmapi.h，Win11 起）：33。</summary>
    private const int DwmwaWindowCornerPreference = 33;

    // ─── 最大化边界修正 ────────────────────────────────────────────────────
    //
    // WindowStyle=None + WindowChrome 的经典坑：系统默认按「整块显示器」最大化
    // 无边框窗口，会把任务栏整个盖住。标准做法是自己应答 WM_GETMINMAXINFO，
    // 把最大化矩形改写为所在显示器的工作区（MahApps.Metro、WPF UI 等自绘标题栏
    // 组件都是这么处理的）。坐标全部取当前显示器的物理像素增量，天然适配 DPI 与
    // 多显示器；Win+↑ / 贴边分屏 / 拖动最大化同样走这条消息，一并被修正。

    /// <summary>WM_GETMINMAXINFO。</summary>
    private const int WmGetMinMaxInfo = 0x0024;

    /// <summary>MONITOR_DEFAULTTONEAREST。</summary>
    private const int MonitorDefaultToNearest = 0x00000002;

    /// <inheritdoc/>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (HwndSource.FromHwnd(new WindowInteropHelper(this).Handle) is { } source)
        {
            source.AddHook(MaximizeBoundsHook);
        }
    }

    /// <summary>把最大化矩形限制到显示器工作区（详见 <see cref="WmGetMinMaxInfo"/> 注释）。</summary>
    private static IntPtr MaximizeBoundsHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmGetMinMaxInfo || lParam == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        try
        {
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var info = new MONITORINFOEX();
            info.CbSize = Marshal.SizeOf<MONITORINFOEX>();
            if (!GetMonitorInfo(monitor, ref info))
            {
                return IntPtr.Zero;
            }

            // 直接改写系统即将采用的结构体；不拦截消息，后续默认处理照旧。
            var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            mmi.MaxPositionX = info.WorkLeft - info.MonLeft;
            mmi.MaxPositionY = info.WorkTop - info.MonTop;
            mmi.MaxSizeX = info.WorkRight - info.WorkLeft;
            mmi.MaxSizeY = info.WorkBottom - info.WorkTop;
            Marshal.StructureToPtr(mmi, lParam, true);
        }
        catch (Exception ex)
        {
            // 失败的最坏结果是最大化盖住任务栏（原生缺陷回退），不影响其余窗口行为。
            ErrorLog.Write(ErrorSeverity.Warning, "最大化边界修正", nameof(MainWindow), ex);
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    /// <summary>WINRECT x 2 + POINT x 4（MINMAXINFO，Winuser.h）；仅取用到的前六个字段，其余原样保留。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public int ReservedX, ReservedY;
        public int MaxSizeX, MaxSizeY;
        public int MaxPositionX, MaxPositionY;
        public int MinTrackSizeX, MinTrackSizeY;
        public int MaxTrackSizeX, MaxTrackSizeY;
    }

    /// <summary>MONITORINFOEXW（Winuser.h）；szDevice 保留为定长 32 字符占位。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int CbSize;
        public int MonLeft, MonTop, MonRight, MonBottom;
        public int WorkLeft, WorkTop, WorkRight, WorkBottom;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

    /// <summary>主 ViewModel（由 XAML 建立，这里只作类型化入口）。</summary>
    public MainViewModel ViewModel => (MainViewModel)DataContext;

    // ─── 自绘标题栏按钮 ────────────────────────────────────────────────────
    // WindowStyle=None 后系统按钮消失，三个动作改由标题栏右上角的自绘按钮触发。
    // WindowChrome 仍负责边缘缩放、贴边分屏与 Win+方向键，那些系统路径直接改
    // WindowState，不经过这里，因此最大化按钮的图标/提示用 XAML 绑定 WindowState，
    // 这里只做最简单的状态切换，不需要任何消息钩子。

    private void OnMinimizeClicked(object sender, RoutedEventArgs e) =>
        ControlErrorRecorder.Guard("最小化窗口", sender, () => WindowState = WindowState.Minimized);

    private void OnMaximizeRestoreClicked(object sender, RoutedEventArgs e) =>
        ControlErrorRecorder.Guard("最大化/还原窗口", sender, () =>
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized);

    private void OnCloseClicked(object sender, RoutedEventArgs e) =>
        ControlErrorRecorder.Guard("关闭窗口", sender, () => Close());

    /// <summary>错误发生时更新底栏角标（非阻塞，避免连续弹窗淹没界面）。</summary>
    private void OnErrorLogged(ErrorEntry entry)
    {
        try
        {
            if (entry.Severity is not (ErrorSeverity.Error or ErrorSeverity.Fatal)) return;

            ErrorBanner.Content = $"⚠ {ErrorLog.ErrorCount} 个错误";
            ErrorBanner.ToolTip = App.BuildErrorBanner(ErrorLog.ErrorCount);
            ErrorBanner.Visibility = Visibility.Visible;
        }
        catch
        {
            // 角标更新失败不影响错误记录本身。
        }
    }
    /// <summary>缩略图双击：用系统默认程序打开原图。</summary>
    /// <remarks>
    /// <para><b>为什么不用 <c>MouseDoubleClick</c>。</b>那是 <see cref="System.Windows.Controls.Control"/>
    /// 的事件，而 <see cref="System.Windows.Controls.Image"/> 直接继承
    /// <see cref="FrameworkElement"/>，拿不到它。只能收
    /// <see cref="MouseButtonEventArgs.ClickCount"/> 自己判定。</para>
    ///
    /// <para>文件可能在扫描之后被删掉或移走，因此先确认存在再交给外壳，
    /// 否则 <see cref="Process.Start(ProcessStartInfo)"/> 抛出的异常只会在日志里留一行，用户什么都看不到。</para>
    /// </remarks>
    private void OnThumbnailMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is not FrameworkElement { DataContext: ScanRowViewModel row }) return;

        ControlErrorRecorder.GuardWithDialog("打开贴图", sender, () =>
        {
            if (!File.Exists(row.FilePath))
            {
                throw new FileNotFoundException(
                    $"贴图已不存在，可能在扫描之后被移动或删除。\n\n{row.FilePath}", row.FilePath);
            }

            Process.Start(new ProcessStartInfo(row.FilePath) { UseShellExecute = true });
        });

        // 已消费：避免 DataGrid 再把这次双击当成「打开编辑」之类的默认行为。
        e.Handled = true;
    }

    /// <summary>
    /// 槽位组合框展开：先按当前槽位把候选项对齐一次，再让用户看见面板。
    /// </summary>
    /// <remarks>
    /// <para>换过一次着色器之后，某行可能带着一项「为保住旧归类而补的、不受支持」的候选。
    /// 用户后来把这行改选成别的槽位，那一项就失去来由了，但它还留在列表里，
    /// 展开时会读成「这个着色器支持它，只是它有问题」。</para>
    ///
    /// <para>之所以等到展开才对齐，而不是在槽位变更的回调里做：那时组合框正处在
    /// 选中回写的中间态，此时换 ItemsSource 可能把槽位直接清空。
    /// 详见 <see cref="ScanRowViewModel.ReapplyRoleOptions"/>。</para>
    /// </remarks>
    private void OnRoleDropDownOpened(object sender, EventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ScanRowViewModel row }) return;
        row.ReapplyRoleOptions();
    }

    /// <summary>拖拽<b>进入</b>与<b>悬停</b>共用：判定能否接收，并在合法时给出 Copy 光标。</summary>
    /// <remarks>
    /// 一次拖拽中 <c>DragOver</c> 会连续触发几十次，因此这里只读取
    /// <see cref="DataFormats.FileDrop"/> 的存在性，不枚举内容、不调
    /// <see cref="VmatLib.DropImportService.CanAccept"/>——后者要碰文件系统。
    /// </remarks>
    private void OnWindowDragOver(object sender, DragEventArgs e) => ControlErrorRecorder.Guard("拖入生成来源", this, () =>
                                                                          {
                                                                              e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                                                                                  ? DragDropEffects.Copy
                                                                                  : DragDropEffects.None;
                                                                              e.Handled = true;
                                                                          });

    /// <summary>拖拽离开窗口：清除悬停态。</summary>
    private void OnWindowDragLeave(object sender, DragEventArgs e) => ControlErrorRecorder.Guard("拖入生成来源", this, () =>
                                                                           {
                                                                               e.Effects = DragDropEffects.None;
                                                                               e.Handled = true;
                                                                           });

    /// <summary>
    /// 放下：把文件 / 文件夹路径并入 <see cref="MainViewModel.AssetsRoot"/>。
    /// </summary>
    /// <remarks>
    /// 这里<b>只收集来源，不触发生成</b>。拖入即开始写盘属于破坏性动作：
    /// 用户可能只是想换一个扫描目录，或者还没挑好着色器。
    /// 走到第三步点「开始生成」才是提交。
    /// </remarks>
    private void OnWindowDrop(object sender, DragEventArgs e) => ControlErrorRecorder.Guard("拖入资产文件夹", this, () =>
                                                                      {
                                                                          if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
                                                                          {
                                                                              ViewModel?.AddDroppedPaths(paths);
                                                                              e.Effects = DragDropEffects.Copy;
                                                                          }
                                                                          else
                                                                          {
                                                                              e.Effects = DragDropEffects.None;
                                                                          }
                                                                          e.Handled = true;
                                                                      });

    /// <summary>点击步骤条回到第一步。生成中不允许跳走。</summary>
    private void OnStep1Clicked(object sender, MouseButtonEventArgs e) => ViewModel?.GoToStep1Command.Execute(null);

    /// <summary>点击步骤条跳到第二步；第一步没填齐时不放行。</summary>
    private void OnStep2Clicked(object sender, MouseButtonEventArgs e) => ViewModel?.GoToStep2Command.Execute(null);

    /// <summary>点击步骤条跳到第三步；前两步没满足时不放行。</summary>
    private void OnStep3Clicked(object sender, MouseButtonEventArgs e) => ViewModel?.GoToStep3Command.Execute(null);

    /// <summary>底栏错误角标 → 打开错误日志窗口（帮助菜单已移除，这是它唯一的入口）。</summary>
    private void OnShowErrorLogClicked(object sender, RoutedEventArgs e) => ControlErrorRecorder.GuardWithDialog("打开错误日志窗口", sender,
            () => new ErrorLogWindow { Owner = this }.ShowDialog());
}
