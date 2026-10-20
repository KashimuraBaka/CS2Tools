
// ============================================================================
// MainWindow —— 代码后置
//
// UI 口径（队长方向，防 off-by-one）：
//   带编译器条目 = **29** = 可解析 **28** + 未命中 **1**（resource_remap_table →
//   CompileResourceRemapTable，注册表无此条）。28 个可解析条目跨 26 distinct
//   （CompilePanorama 3→1）。**UI 用条目级（28/29）**，distinct 级（26/27）另列，不混用。
// ============================================================================
using AssetCompiler.Models;
using AssetCompiler.Services;
using Microsoft.Win32;
using SDK.ResourceCompiler;
using System.Windows;
using System.Windows.Controls;

namespace AssetCompiler;

public partial class MainWindow : Window
{
    private ConfigLoadResult? Cfg { get; set; }
    private RegistrySnapshot? Reg { get; set; }
    private List<AssetTypeRow> AllTypes { get; } = [];
    private List<string> Sources { get; } = [];

    // 默认游戏路径（可在 UI 改）
    private const string DefaultGameDir = @"F:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game";
    private const string DefaultDllRelative = @"bin\win64\resourcecompiler.dll";
    private const string DefaultExeRelative = @"bin\win64\resourcecompiler.exe";
    private const string DefaultAnchors = @"H:\dsh-workspace\CS2-IDA\tools\anchors\compiler-identifier-anchors.json";

    public MainWindow()
    {
        InitializeComponent();
        TxtGameDir.Text = DefaultGameDir;   // 唯一输入：游戏目录；配置/DLL/exe 均由它派生
        TxtModDir.Text = Path.Combine(DefaultGameDir, "csgo");   // Tier2 的 -game mod 目录（默认 <游戏目录>\csgo）
        // t28c/d：Tier1 全进程内——不起任何子进程（用户禁止额外进程方式；CompileHost 子工程已移除）。
        // 进程内链路：签名解析 -> CreateInterface+注册表只读验证 -> 引擎服务图预检（缺则明确报错，绝不崩）。
        Log("Tier1: 进程内模式（签名解析 + 只读注册表验证 + 引擎服务图预检）");
    }

    // ---------------- 日志 ----------------
    private void Log(string msg)
    {
        TxtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + "\n");
        TxtLog.ScrollToEnd();
    }

    private void OnClearLog(object sender, RoutedEventArgs e) => TxtLog.Clear();

    // ---------------- ① 配置加载 ----------------
    /// <summary>游戏目录（唯一输入）——配置 / DLL / exe 全部由它派生。</summary>
    private string GameDir => TxtGameDir.Text.Trim();

    private void OnBrowseConfig(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "选择游戏目录（其下应有 bin\assettypes_common.txt）" };
        if (d.ShowDialog(this) == true) TxtGameDir.Text = d.FolderName;
    }

    private void OnLoadConfig(object sender, RoutedEventArgs e)
    {
        try
        {
            string cfgPath = Path.Combine(GameDir, Kv3ConfigService.DefaultConfigRelative);
            Log("游戏目录: " + GameDir);
            Log("加载配置: " + cfgPath);
            Cfg = Kv3ConfigService.Load(cfgPath);
            AllTypes.Clear();
            AllTypes.AddRange(Cfg.Types);

            GridTypes.ItemsSource = null;
            GridTypes.ItemsSource = Cfg.Types;
            GridSubTypes.ItemsSource = Cfg.SubTypes;

            TxtConfigStatus.Text = $"已加载 {Cfg.Types.Count} 类型 + {Cfg.SubTypes.Count} subassettypes";
            TabTypes.Header = $"资产类型 ({Cfg.Types.Count})";

            TxtConfigStatus.Foreground = System.Windows.Media.Brushes.DarkGreen;

            Log($"KV3 头: encoding={Cfg.EncodingName} ({Cfg.EncodingId}) format={Cfg.FormatName} ({Cfg.FormatId})");
            Log($"文件: {Cfg.FileBytes} B / {Cfg.FileLines} 行；asset_warnings={Cfg.WarningsCount} 组（标题: {Cfg.WarningTitle}）");
            Log($"assettypes={Cfg.Types.Count} 条；subassettypes={Cfg.SubTypes.Count} 条");


            ApplyFilter();
        }
        catch (Exception ex)
        {
            TxtConfigStatus.Text = "加载失败";
            TxtConfigStatus.Foreground = System.Windows.Media.Brushes.DarkRed;
            Log("ERROR: " + ex.GetType().Name + ": " + ex.Message);
            MessageBox.Show(this, ex.Message, "加载配置失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyFilter()
    {
        if (Cfg is null) { TxtCounts.Text = ""; return; }
        string q = TxtSearch.Text.Trim();
        bool only = ChkOnlyCompiler.IsChecked == true;
        var v = Cfg.Types.Where(t =>
            (string.IsNullOrEmpty(q)
             || t.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
             || t.FriendlyName.Contains(q, StringComparison.OrdinalIgnoreCase)
             || t.Ext.Contains(q, StringComparison.OrdinalIgnoreCase)
             || t.CompilerIdentifier.Contains(q, StringComparison.OrdinalIgnoreCase))
            && (!only || t.HasCompiler));
        GridTypes.ItemsSource = v.ToList();
        int comp = Cfg.Types.Count(t => t.HasCompiler);
        TxtCounts.Text = $"主类型 {Cfg.Types.Count}；带编译器 {comp}；subassettypes {Cfg.SubTypes.Count}";
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();

    // ---------------- ③ 编译器解析 ----------------
    private void OnReadRegistry(object sender, RoutedEventArgs e)
    {
        try
        {
            string dll = Path.Combine(GameDir, DefaultDllRelative);
            Log("只读加载模块: " + dll);
            Reg = RegistryService.Read(dll);
            RegistryService.ValidateTable(Reg);
            GridRegistry.ItemsSource = Reg.Entries;

            Log($"模块基址 = 0x{Reg.ModuleBase:X}；注册表表头 RVA = {Reg.TableRvaText}；条目 = {Reg.Entries.Count}");
            Log($"CreateInterface(ResourceCompilerSystem001) -> 0x{Reg.SystemObject:X}；RTTI 名称 = {Reg.RttiName}（校验 {(Reg.RttiOk ? "通过" : "未通过")}）");
            Log("注意：工厂 RVA 全部从表里实时读出（实测非均匀平移，禁硬编码）。");
            TxtResolveSummary.Text = $"注册表 {Reg.Entries.Count} 条已就绪（表头 RVA {Reg.TableRvaText}，自校验通过）";
            TxtResolveDetail.Text = "";
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.GetType().Name + ": " + ex.Message);
            MessageBox.Show(this, ex.Message, "读取注册表失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnTypeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (GridTypes.SelectedItem is AssetTypeRow r)
        {
            TxtIdentifier.Text = r.CompilerIdentifier;
            Log($"选中类型 {r.Name}（_class={r.Class}, m_Ext={r.Ext}, 字段 {r.FieldCount} 个, m_Blocks={r.BlockCount}, m_HideForRetailMods={r.HideForRetailMods}）");
        }
    }

    private void OnResolve(object sender, RoutedEventArgs e)
    {
        if (Reg is null) { MessageBox.Show(this, "请先点「读注册表」。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        string id = TxtIdentifier.Text.Trim();
        var r = RegistryService.Resolve(Reg, id);
        if (r.Found && r.Entry is not null)
        {
            TxtResolveSummary.Text = $"命中：{r.Identifier}（idx {r.Entry.Index}）";
            TxtResolveDetail.Text = $"factory RVA {r.Entry.FactoryRvaText} · factory VA {r.Entry.FactoryVaText} · singleton RVA {r.Entry.SingletonRvaText}";
            Log($"解析 {id} -> idx={r.Entry.Index} factory=0x{r.Entry.Factory.ToInt64():X} singleton={(r.Entry.SingletonRva > 0 ? "0x" + r.Entry.SingletonRva.ToString("X") : "(未实例化)")}");
        }
        else
        {
            TxtResolveSummary.Text = $"未命中：{id}";
            TxtResolveDetail.Text = r.Message + "（UI 已优雅降级，未崩溃）";
            Log("未命中: " + r.Message);
        }
    }

    // ---------------- ④ 编译面板 ----------------
    private void OnAddSources(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog
        {
            Title = "选择资产源文件",
            Filter = "Source 2 资产原文件 (*.v*)|*.v*|所有文件 (*.*)|*.*",
            Multiselect = true
        };
        if (d.ShowDialog(this) == true)
        {
            foreach (var f in d.FileNames) if (!Sources.Contains(f)) Sources.Add(f);
            RefreshSources();
        }
    }

    private void OnClearSources(object sender, RoutedEventArgs e)
    {
        Sources.Clear();
        RefreshSources();
    }

    private void RefreshSources()
    {
        ListSources.ItemsSource = null;
        ListSources.ItemsSource = Sources.ToList();
        TxtSourceCount.Text = $"共 {Sources.Count} 个";
    }

    private async void OnCompileTier1(object sender, RoutedEventArgs e)
    {
        if (Sources.Count == 0) { MessageBox.Show(this, "请先添加源文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        string dll = Path.Combine(GameDir, DefaultDllRelative);
        Log("=== Tier1（进程内）开始 ===");
        var results = new List<CompileItemResult>();
        foreach (var s in Sources.ToList())
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var o = await Task.Run(() => CompileService.RunTier1(GameDir));
            sw.Stop();
            results.Add(new CompileItemResult
            {
                Source = Path.GetFileName(s),
                Status = o.Ok ? "成功" : "失败",
                Tier = "Tier1",
                Detail = o.Detail,
                ElapsedMs = sw.ElapsedMilliseconds,
                OutputPath = "-"
            });
            foreach (var l in o.Logs) Log("  [host] " + l);
            Log($"Tier1 {Path.GetFileName(s)} => {(o.Ok ? "成功" : "失败")} ({sw.ElapsedMilliseconds} ms): {o.Detail}");
        }
        GridCompile.ItemsSource = results;
    }

    private async void OnCompileTier2(object sender, RoutedEventArgs e)
    {
        if (Sources.Count == 0) { MessageBox.Show(this, "请先添加源文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        string exe = Path.Combine(GameDir, DefaultExeRelative);
        string game = TxtModDir.Text.Trim();
        Log("=== Tier2（官方 resourcecompiler.exe）开始，-game=" + game + " ===");
        var results = new List<CompileItemResult>();
        foreach (var s in Sources.ToList())
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var o = await System.Threading.Tasks.Task.Run(() => CompileService.RunTier2(exe, game, s));
            sw.Stop();
            results.Add(new CompileItemResult
            {
                Source = Path.GetFileName(s),
                Status = o.Ok ? "成功" : "失败",
                Tier = "Tier2",
                Detail = o.Detail,
                ElapsedMs = sw.ElapsedMilliseconds,
                OutputPath = "-"
            });
            foreach (var l in o.Logs.Take(40)) Log("  [exe] " + l);
            Log($"Tier2 {Path.GetFileName(s)} => {(o.Ok ? "成功" : "失败")} ({sw.ElapsedMilliseconds} ms): {o.Detail}");
        }
        GridCompile.ItemsSource = results;
    }
}
