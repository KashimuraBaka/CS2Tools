using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmatGenerator.Diagnostics;
using VmatGenerator.Imaging;
using VmatLib;

namespace VmatGenerator.ViewModels;

/// <summary>
/// 「贴图快速导航」三步向导：
/// <list type="number">
/// <item>选择<b>目标着色器</b>、<b>资产文件夹</b>（扫描贴图的来源）与<b>项目文件夹</b>（.vmat 输出目录）；</item>
/// <item>列出扫描到的全部图像，按「去掉后缀后的基名」自动归类到各个 .vmat，
/// 允许逐行改后缀后重新扫描、也允许手动指定某张贴图属于哪个 .vmat；
/// 右侧实时显示选中那份 .vmat 生成后的 KeyValues1 文本；</item>
/// <item>开始生成，显示进度条与当前正在处理的内容。</item>
/// </list>
///
/// <para>单独成文件，与「规则表编辑」那份主体分开：本文件讲的是一次生成作业的
/// 生命周期（配置 → 分组 → 写盘），主体讲的是后缀规则本身怎么维护。</para>
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>向导步骤数（1/2/3）。</summary>
    public const int StepCount = 3;

    private CancellationTokenSource? _buildCts;

    // ─── 第一步：目标与目录 ───────────────────────────────────────────────

    /// <summary>
    /// 资产文件夹：扫描贴图的来源目录。
    /// </summary>
    /// <remarks>递归与否由已有的 <see cref="RecurseTextureFolders"/> 开关控制。</remarks>
    [ObservableProperty]
    private string _assetsRoot = string.Empty;

    /// <summary>
    /// 项目文件夹：生成的 <c>.vmat</c> 输出目录，与贴图所在位置无关。
    /// </summary>
    /// <remarks>
    /// 与资产文件夹分开，是为了让「贴图在引擎目录、材质在工程目录」这类布局可用；
    /// <c>.vmat</c> 里写的贴图路径仍然相对<b>资产</b>根。
    /// </remarks>
    [ObservableProperty]
    private string _projectRoot = string.Empty;

    // ─── 第二步：扫描与分组 ───────────────────────────────────────────────

    /// <summary>扫描到的全部图像，一行一个。</summary>
    public ObservableCollection<ScanRowViewModel> ScanRows { get; } = [];

    /// <summary>当前选中的行；其所属 .vmat 的生成结果实时显示在右侧预览。</summary>
    [ObservableProperty]
    private ScanRowViewModel? _selectedRow;

    /// <summary>选中行所属 <c>.vmat</c> 生成后的 KeyValues1 文本预览。</summary>
    [ObservableProperty]
    private string _selectedPreview = "（尚未扫描）";

    /// <summary>扫描结果的一句话摘要。</summary>
    [ObservableProperty]
    private string _scanSummary = "尚未扫描。";


    // ─── 第三步：生成进度 ─────────────────────────────────────────────────

    /// <summary>进度百分比 0–100。</summary>
    [ObservableProperty]
    private double _progressPercent;

    /// <summary>当前正在处理的内容（生成时会持续变化）。</summary>
    [ObservableProperty]
    private string _progressMessage = "尚未生成。";

    /// <summary>生成中。为 <c>true</c> 时出现取消按钮。</summary>
    [ObservableProperty]
    private bool _isGenerating;

    /// <summary>上一次生成的完整结果说明。</summary>
    [ObservableProperty]
    private string _buildSummary = string.Empty;

    /// <summary>
    /// 最近一次<b>成功完成</b>的生成所用的输出目录（当时的项目文件夹快照）。
    /// 空串 = 从未成功生成过；「打开输出文件夹」按钮只认这个值。
    /// </summary>
    /// <remarks>存快照而不是实时 <see cref="ProjectRoot"/>：生成完又去改目录时，
    /// 按钮指向的仍是真正写出了文件的那个文件夹。</remarks>
    [ObservableProperty]
    private string _lastOutputFolder = string.Empty;

    /// <summary>「打开输出文件夹」是否可点：有过一次成功生成，且当前不在生成中。</summary>
    public bool CanOpenOutputFolder => !IsGenerating && LastOutputFolder.Length > 0;

    // ─── 步骤导航 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 当前步骤，1–3。步骤条与内容区都由它驱动。
    /// </summary>
    [ObservableProperty]
    private int _stepIndex = 1;

    /// <summary>第一步是否填齐（着色器 + 两个目录都存在）。</summary>
    public bool IsStep1Ready =>
        SelectedShader is not null
        && Directory.Exists(AssetsRoot)
        && Directory.Exists(ProjectRoot);

    /// <summary>第二步是否可进入（至少扫到一张命中的贴图）。</summary>
    public bool IsStep2Ready => ScanRows.Any(r => r.IsMatched);

    /// <summary>回到第一步。</summary>
    [RelayCommand]
    public void GoToStep1() { if (!IsGenerating) StepIndex = 1; }

    /// <summary>进入第二步；第一步未填齐时不放行。</summary>
    [RelayCommand]
    public void GoToStep2()
    {
        if (!IsGenerating && IsStep1Ready)
        {
            StepIndex = 2;
            TryAutoScan();
        }
    }

    /// <summary>进入第三步；没有可生成的内容时不放行。</summary>
    [RelayCommand]
    public void GoToStep3()
    {
        if (!IsGenerating && IsStep1Ready && IsStep2Ready) StepIndex = 3;
    }

    /// <summary>
    /// 底部「下一步」：按当前步骤顺推，到第三步为止。
    /// </summary>
    /// <remarks>
    /// 条件不满足时静默不动，而不是弹提示：向导里「下一步」置灰已足够说明原因，
    /// 弹窗反而打断「填错了→回去改」的连续操作。
    /// </remarks>
    [RelayCommand]
    public void GoToNextStep()
    {
        switch (StepIndex)
        {
            case 1 when IsStep1Ready:
                StepIndex = 2;
                TryAutoScan();
                break;
            case 2 when IsStep2Ready: StepIndex = 3; break;
            default:
                break;
        }
    }

    /// <summary>
    /// 底部「上一步」：回到前一步。
    /// </summary>
    /// <remarks>
    /// 刻意<b>不校验</b>各步的就绪条件——往回走是为了改前面的输入，
    /// 拿「第二步没扫到东西」去拦住「回第一步」只会把人困在原地。
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanGoPreviousStep))]
    public void GoToPreviousStep()
    {
        if (IsGenerating || StepIndex <= 1) return;
        StepIndex--;
    }

    /// <summary>「上一步」是否可点：不在第一步、且没有正在生成。</summary>
    public bool CanGoPreviousStep => StepIndex > 1 && !IsGenerating;

    /// <summary>「开始生成」是否可点：停在第三步、且没有正在生成。</summary>
    public bool CanStartGenerate => StepIndex == 3 && !IsGenerating;

    /// <summary>
    /// 广播受步骤影响的派生状态。
    /// </summary>
    /// <remarks>
    /// 这些值都是几个属性的组合，CommunityToolkit 不会自动为组合值发通知，
    /// 必须在相关属性各自变化时手工补一次，否则按钮会停留在旧状态。
    /// </remarks>
    public void NotifyStepStateChanged()
    {
        OnPropertyChanged(nameof(IsStep1Ready));
        OnPropertyChanged(nameof(IsStep2Ready));
        OnPropertyChanged(nameof(CanGoPreviousStep));
        OnPropertyChanged(nameof(CanStartGenerate));
        OnPropertyChanged(nameof(CanOpenOutputFolder));
        OpenOutputFolderCommand.NotifyCanExecuteChanged();
        GoToStep1Command.NotifyCanExecuteChanged();
        GoToStep2Command.NotifyCanExecuteChanged();
        GoToStep3Command.NotifyCanExecuteChanged();
        GoToNextStepCommand.NotifyCanExecuteChanged();
        GoToPreviousStepCommand.NotifyCanExecuteChanged();
    }

    partial void OnStepIndexChanged(int value) => NotifyStepStateChanged();

    partial void OnIsGeneratingChanged(bool value) => NotifyStepStateChanged();

    partial void OnLastOutputFolderChanged(string value)
    {
        OnPropertyChanged(nameof(CanOpenOutputFolder));
        OpenOutputFolderCommand.NotifyCanExecuteChanged();
    }

    partial void OnAssetsRootChanged(string value)
    {
        NotifyStepStateChanged();
        ScanSummary = string.IsNullOrWhiteSpace(value) ? "尚未扫描。" : ScanSummary;
        TryAutoScan();
    }

    partial void OnProjectRootChanged(string value)
    {
        NotifyStepStateChanged();
        TryAutoScan();
    }

    /// <summary>第一步的目录选择：资产文件夹（扫描来源）。</summary>
    [RelayCommand]
    public void BrowseAssets() => ControlErrorRecorder.Guard("选择资产文件夹", this, () =>
                                       {
                                           var picked = FolderPicker.Pick("选择资产文件夹（贴图来源）", AssetsRoot);
                                           if (picked is not null) AssetsRoot = picked;
                                       });

    /// <summary>第一步的目录选择：项目文件夹（.vmat 输出）。</summary>
    [RelayCommand]
    public void BrowseProject() => ControlErrorRecorder.Guard("选择项目文件夹", this, () =>
                                        {
                                            var picked = FolderPicker.Pick("选择项目文件夹（.vmat 输出位置）", ProjectRoot);
                                            if (picked is not null) ProjectRoot = picked;
                                        });

    /// <summary>扫描资产文件夹，填充第二步列表。</summary>
    [RelayCommand]
    public void Scan() => ControlErrorRecorder.Guard("扫描资产文件夹", this, ScanCore);

    /// <summary>
    /// 按用户补充的后缀重新扫描。
    /// </summary>
    /// <remarks>
    /// 与「扫描」是同一条路径，区别只有规则表：这里把 <see cref="InferredSuffixes"/>
    /// 临时追加到规则末尾，让本次扫描生效且<b>不落盘</b>。
    /// </remarks>
    [RelayCommand]
    public void Rescan() => ControlErrorRecorder.Guard("按后缀重新扫描", this, ScanCore);

    /// <summary>把扫描列表里 <see cref="ScanRowViewModel.GroupNameOverride"/> 的手动指定全部还原。</summary>
    [RelayCommand]
    public void ResetManualGroups()
    {
        if (IsGenerating) return;
        foreach (var row in ScanRows)
            row.GroupNameOverride = string.Empty;
        RefreshMaterialPreview();
        ScanSummary = $"已还原自动分组，共 {ScanRows.Count} 行。";
    }

    // ─── 扫描实现 ─────────────────────────────────────────────────────────

    /// <summary>上一次扫描所用的资产目录；用于判断「目录真的换了」还是只是重复赋值。</summary>
    private string _lastScannedAssetsRoot = string.Empty;

    /// <summary>
    /// 载入已保存配置期间为 true：读回配置不是用户改了路径，不该顺手扫一遍盘。
    /// </summary>
    private bool _suppressAutoScan;

    /// <summary>
    /// 资产目录定下来后自动扫一次，省掉「选完路径还要再点扫描」这一步。
    /// </summary>
    /// <remarks>
    /// <para>只认<b>资产目录变化</b>：项目目录改一改没必要把同一批贴图重扫一遍。</para>
    /// <para>手动「扫描」按钮原样保留——自动扫只覆盖「第一次定目录」这个时机，
    /// 规则改了、文件增删了、想强制重来一遍，都还得靠它。</para>
    /// </remarks>
    private void TryAutoScan()
    {
        if (_suppressAutoScan || !IsStep1Ready) return;
        if (string.Equals(_lastScannedAssetsRoot, AssetsRoot, StringComparison.OrdinalIgnoreCase)) return;

        Scan();
    }
    private void ScanCore()
    {
        if (!Directory.Exists(AssetsRoot))
        {
            ScanSummary = $"资产文件夹不存在：{AssetsRoot}";
            return;
        }
        if (SelectedShader is not { } shader)
        {
            ScanSummary = "请先选择目标着色器。";
            return;
        }

        foreach (var row in ScanRows)
        {
            row.GroupChanged -= OnRowGroupChanged;
            row.RoleChanged -= OnRowGroupChanged;
            row.IncludeChanged -= OnRowGroupChanged;
        }

        var rules = BuildEffectiveRules();
        var matcher = new TextureSuffixMatcher(rules);

        // 重扫意味着文件可能已经增删改，旧缩略图一律作废，否则缓存会一直占着预算。
        ThumbnailCache.Shared.Clear();

        ScanRows.Clear();
        var option = RecurseTextureFolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.EnumerateFiles(AssetsRoot, "*", option)
            .Where(TexturePathRules.IsTextureFile)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 第一遍：只做后缀匹配，不定归属。
        var matched = 0;
        var defaulted = 0;
        var parsed = new List<(string File, string Suffix, TextureRole Role, string BaseName, string RelativeDir, bool Matched)>();
        foreach (var file in files)
        {
            var match = matcher.Match(file);
            var hit = match?.Role ?? TextureRole.Unknown;
            // 未命中后缀规则的贴图默认按 Color 槽位处理：多数散图本身就是颜色贴图，
            // 与其留一行「未命中」等用户逐行手改，不如先给一个大概率正确的默认值。
            // 默认条目不带后缀（后缀长度 0），同材质里真后缀命中的 Color 贴图在
            // §6.5 冲突裁决里必然胜出——默认是「补位」，不是「抢位」。
            var isMatched = hit != TextureRole.Unknown;
            var role = isMatched ? hit : TextureRole.Color;
            if (isMatched) matched++; else defaulted++;

            var relativeDir = Path.GetRelativePath(AssetsRoot, Path.GetDirectoryName(file) ?? AssetsRoot);
            if (relativeDir == ".") relativeDir = string.Empty;

            var suffix = match?.MatchedSuffix ?? string.Empty;
            parsed.Add((file, suffix, role, StripSuffix(Path.GetFileNameWithoutExtension(file), suffix), relativeDir, isMatched));
        }

        // 每个目录里，真命中 Color 的贴图基名才是该目录的材质名；
        // 默认 Color 的散图没有发言权，否则任何散图都能给自己立门户。
        var owners = parsed.Where(p => p.Matched && p.Role == TextureRole.Color)
            .GroupBy(p => p.RelativeDir, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(p => p.BaseName)], StringComparer.OrdinalIgnoreCase);

        // 第二遍：定归属。只有没命中后缀的贴图（含默认 Color 的）才回退到
        // TextureColor 材质名——命中的贴图基名本身就是对的（wall.png 与 wall_normal.png 归到 wall）。
        var supported = GetSupportedRoles();
        foreach (var p in parsed)
        {
            var baseName = !p.Matched && owners.TryGetValue(p.RelativeDir, out var names)
                ? PickOwner(names, p.BaseName) ?? p.BaseName
                : p.BaseName;

            var row = new ScanRowViewModel(p.File, p.Suffix, p.Role, baseName)
            {
                RelativeDirectory = p.RelativeDir,
                MatchedByRule = p.Matched,
                // 不管有无命中，资源默认全选：用户勾的语义是「这个文件我要带走」，
                // 而不是「后缀规则认得它」。没命中的行默认 Color 槽位、带着勾直接参与
                // 材质；想排除谁，取消勾选即可。
                Include = true,
            };
            row.UpdateRoleOptions(supported);
            row.GroupChanged += OnRowGroupChanged;
            row.RoleChanged += OnRowGroupChanged;   // 槽位决定写哪个参数键，预览必须跟着变
            row.IncludeChanged += OnRowGroupChanged; // 取消勾选可能让整个材质退出候选名单
            ScanRows.Add(row);
        }

        SelectedRow = ScanRows.FirstOrDefault(r => r.IsMatched);
        RefreshMaterialNameOptions();
        RefreshMaterialPreview();

        _lastScannedAssetsRoot = AssetsRoot;

        ScanSummary = matched == 0
            ? $"共 {ScanRows.Count} 个图像，没有一个命中后缀规则——已全部默认 Color 槽位，"
              + $"可逐行改槽位或补充后缀后重新扫描。"
            : defaulted == 0
                ? $"共 {ScanRows.Count} 个图像，命中 {matched} 个，归为 {CountGroups()} 个材质。"
                : $"共 {ScanRows.Count} 个图像，命中 {matched} 个；未命中的 {defaulted} 个默认 Color 槽位，"
                  + $"共归为 {CountGroups()} 个材质。";
    }

    /// <summary>
    /// 在同一目录的若干 TextureColor 材质名里，为一张没命中后缀的贴图挑一个归属。
    /// </summary>
    /// <remarks>
    /// <para><b>先找前缀吻合的。</b><c>wall_extra.png</c> 该跟 <c>wall</c>，
    /// 而不是目录里恰好排在最前面的那个材质。多个候选时取最长的那个
    /// （<c>wall</c> 与 <c>wall_dark</c> 同时存在时，<c>wall_dark_extra.png</c> 归后者）。</para>
    ///
    /// <para><b>词边界是必须的。</b><c>wallpaper.png</c> 不该被 <c>wall</c> 领走——
    /// 否则仅凭名字碰巧以 wall 开头，就会被并进不相干的材质。</para>
    ///
    /// <para><b>前缀不吻合就返回 null，不做兜底。</b>曾经退到「目录里第一个材质名」，
    /// 结果 <c>wallpaper.png</c> 被塞进了 <c>crate</c>（按路径序 crate 排在 wall 前面）。
    /// 一张明显不属于任何材质的贴图，硬塞进某个材质比单独立一个更糟：
    /// 它会以错误的身份混进材质预览，用户很难看出是归名规则干的。
    /// 留在自己的名下，用户一眼就知道「这张我还没归类」。</para>
    /// </remarks>
    /// <param name="names">该目录下 TextureColor 贴图的基名列表。</param>
    /// <param name="baseName">待归类贴图去掉后缀后的基名。</param>
    /// <returns>归属的材质名；无吻合候选时返回 <c>null</c>。</returns>
    private static string? PickOwner(IReadOnlyList<string> names, string baseName)
    {
        string? best = null;

        foreach (var name in names)
        {
            if (baseName.Length <= name.Length) continue;
            if (!baseName.StartsWith(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (baseName[name.Length] is not ('_' or '-' or '.')) continue;
            if (best is null || name.Length > best.Length) best = name;
        }

        return best;
    }
    private void OnRowGroupChanged() =>
        // 材质名候选现在只取决于各行所在文件夹里的 .vmat 文件（扫描时算好），
        // 改槽位、改归属、勾选都不会让它变，无需在这里重枚举。
        RefreshMaterialPreview();

    /// <summary>
    /// 重算材质名下拉候选：每一行的候选 = <b>该行贴图所在文件夹下实际存在的 .vmat 文件</b>。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么按文件夹枚举而不是用扫描出的分组名。</b>用户的资产目录里，
    /// 现有材质就是贴图旁边的那些 <c>.vmat</c>；把散图并进同目录的现成材质
    /// 是最常见的操作，下拉直接列这些名字最贴合意图。之前列的是
    /// 「本次扫描里由 basecolor 定义的材质」，同目录几十个真实材质一个都不出现，
    /// 反而全是别处目录的名字。</para>
    ///
    /// <para><b>候选不等于限制。</b>组合框可编辑，输入一个新名字同样有效——
    /// 那仍是给新材质添第一张贴图的路径。没有颜色贴图的材质名照旧会在
    /// 生成端被整体跳过（basecolor 是材质成立的必要条件）。</para>
    ///
    /// <para>同一目录只枚举一次，同目录的行共享同一份列表实例；
    /// 推给每一行而不是放 VM 上：行没有回引用，
    /// 与 <see cref="ScanRowViewModel.RoleOptions"/> 同一套「列表的一方推给行」模式。</para>
    /// </remarks>
    private void RefreshMaterialNameOptions()
    {
        var byDir = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in ScanRows)
        {
            if (!byDir.TryGetValue(row.RelativeDirectory, out var names))
            {
                names = ListVmatFileNamesIn(row.RelativeDirectory);
                byDir[row.RelativeDirectory] = names;
            }

            row.UpdateMaterialNames(names);
        }
    }

    /// <summary>
    /// 枚举某个资产子目录（相对资产根）下的全部 <c>.vmat</c> 文件名，带扩展名、已排序。
    /// </summary>
    /// <remarks>
    /// 只枚举该目录一层（不含子目录）——「当前文件夹」就是字面意思，
    /// 子目录的材质属于子目录自己那些行的候选。目录不存在或读不了时返回空列表：
    /// 下拉空着不算错，输入新名字的路径依然畅通，不值得为此弹错误。
    /// </remarks>
    private IReadOnlyList<string> ListVmatFileNamesIn(string relativeDirectory)
    {
        var dir = string.IsNullOrEmpty(relativeDirectory)
            ? AssetsRoot
            : Path.Combine(AssetsRoot, relativeDirectory);
        try
        {
            return !Directory.Exists(dir)
                ? Array.Empty<string>()
                : Directory.EnumerateFiles(dir, "*.vmat", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// 收集逐文件的<b>手动槽位覆盖</b>：计划里每一行当前的槽位就是权威值。
    /// </summary>
    /// <remarks>
    /// <b>为什么全量给而不是只给「手动改过的」。</b>行上的槽位本来就 = 自动命中值 ∪ 默认 Color ∪ 手动改动，
    /// 全量给让 Assign 无需区分来源，预览 / 生成与界面上看到的一致；未改过的行覆盖值
    /// 与自动判定相同，是恒等操作。未勾选或仍未归类的行根本不在计划里，自然不参与。
    /// 键的比较器与 <see cref="TextureAssigner"/> 的去重语义一致（Windows 大小写不敏感）。
    /// </remarks>
    private Dictionary<string, TextureRole> BuildRoleOverrides()
    {
        var overrides = new Dictionary<string, TextureRole>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var row in ScanRows)
        {
            if (!row.Include || !row.IsMatched) continue;
            overrides[row.FilePath] = row.Role;
        }
        return overrides;
    }

    /// <summary>拼出本次扫描实际使用的规则表 = 规则表 + 推断出且勾选的新增后缀。</summary>
    /// <remarks>
    /// 早先这里是「把用户手填的补充后缀一律按 <see cref="TextureRole.Color"/> 处理」——
    /// 于是 <c>_normal</c>、<c>_tran</c> 全被当成颜色贴图。现在每个后缀都带着
    /// <b>它自己推断出来的槽位</b>进来，猜错的可能只剩「推断本身」，不再是「一律 Color」。
    /// </remarks>
    private List<TextureSuffixRule> BuildEffectiveRules()
    {
        var rules = Rules.Select(r => r.ToRule()).ToList();

        foreach (var row in InferredSuffixes)
        {
            foreach (var suffix in row.EffectiveSuffixes)
            {
                rules.Add(new TextureSuffixRule(suffix, row.Role));
            }
        }

        return rules;
    }

    // ─── 按着色器推断贴图槽位 ───────────────────────────────────────────────

    /// <summary>
    /// 当前着色器能承载的语义槽位，以及每个槽位对应的候选后缀。
    /// </summary>
    /// <remarks>
    /// 着色器一换就整体重算——同一个资产目录换着色器，要的贴图完全不是一回事。
    /// </remarks>
    public ObservableCollection<InferredSuffixViewModel> InferredSuffixes { get; } = [];

    /// <summary>推断结果的摘要文案。</summary>
    public string InferredSummary => InferredSuffixes.Count == 0
        ? SelectedShader is null
            ? "尚未选择着色器。"
            : $"{SelectedShader.DisplayName} 没有可用的贴图槽位。"
        : $"{SelectedShader?.DisplayName} 需要 {InferredSuffixes.Count} 类贴图，"
          + $"其中 {InferredSuffixes.Sum(r => r.FreshCount)} 个后缀规则表里还没有。";

    /// <summary>
    /// 把推断出的、规则表里还没有的后缀并入规则表。
    /// </summary>
    /// <remarks>
    /// 推断默认只作用于<b>本次扫描</b>，不擅自改用户的持久配置；
    /// 想要它长期生效，就显式点这个按钮，随后「保存设置」写进注册表。
    /// </remarks>
    [RelayCommand]
    public void ApplyInferredSuffixes()
    {
        var added = 0;
        foreach (var row in InferredSuffixes.Where(r => r.IsEnabled))
        {
            foreach (var suffix in row.Fresh)
            {
                Rules.Add(new TextureSuffixRuleViewModel(suffix, row.Role, true));
                added++;
            }
        }

        // 只刷新「哪些算新增」的判定，不整份重推断：
        // 重推断会把用户刚敲进去的自定义后缀一并冲掉，等于白输入。
        foreach (var row in InferredSuffixes)
        {
            row.UpdateExisting(CollectCanonicalSuffixes());
        }

        OnPropertyChanged(nameof(InferredSummary));
        ScanSummary = added == 0
            ? "推断出的后缀规则表里已经都有了，无需添加。"
            : $"已把推断出的 {added} 个后缀加入规则表——点「保存设置」后写入注册表。";
    }

    /// <summary>
    /// 规则表里所有后缀的归一化形态（小写、无前导下划线），用于判定哪些是新增。
    /// </summary>
    private IReadOnlySet<string> CollectCanonicalSuffixes()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in Rules)
        {
            var key = CanonicalSuffix(rule.Suffix);
            if (key.Length > 1) existing.Add(key);
        }

        return existing;
    }

    /// <summary>按当前着色器重新推断槽位与候选后缀，并标出哪些是规则表里没有的。</summary>
    public void RefreshInferredSuffixes()
    {
        var existing = CollectCanonicalSuffixes();

        InferredSuffixes.Clear();
        foreach (var set in ShaderSuffixInference.Infer(SelectedShader))
        {
            InferredSuffixes.Add(new InferredSuffixViewModel(set, existing));
        }

        OnPropertyChanged(nameof(InferredSuffixes));
        OnPropertyChanged(nameof(InferredSummary));
    }

    /// <summary>把后缀归一化成 <c>_小写</c> 形式，供「是否已存在」比较。</summary>
    private static string CanonicalSuffix(string? suffix)
    {
        var normalized = TextureSuffixMatcher.NormalizeName(suffix ?? string.Empty);
        return normalized.Length == 0 ? string.Empty : "_" + normalized;
    }
    private int CountGroups() =>
        // 材质由「勾选且已归类」的行构成；现在扫描默认全勾且未命中的行自动默认 Color，
        // IsMatched 基本恒真，仅用户手动取消归类（Unknown）的行被排除——与 BuildPlan 口径一致。
        ScanRows.Where(r => r.Include && r.IsMatched)
            .Select(r => r.RelativeDirectory + "\0" + r.GroupName)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();

    private static string StripSuffix(string fileNameNoExt, string matchedSuffix)
    {
        if (matchedSuffix.Length > 0
            && fileNameNoExt.Length >= matchedSuffix.Length
            && fileNameNoExt.EndsWith(matchedSuffix, StringComparison.OrdinalIgnoreCase))
        {
            fileNameNoExt = fileNameNoExt[..^matchedSuffix.Length];
        }
        var name = fileNameNoExt.Trim('_', '-', ' ');
        return name.Length == 0 ? "material" : name;
    }

    /// <summary>把拖放进来的路径设为资产文件夹；拖入文件则取其所在目录。</summary>
    /// <remarks>
    /// 向导以「扫一个目录」为单位工作，没有「一堆零散文件」的概念，
    /// 因此拖入文件时自动取其父目录；用户仍可在第一步手动改。
    /// </remarks>
    public void AddDroppedPaths(IEnumerable<string> paths) => ControlErrorRecorder.Guard("拖入资产文件夹", this, () =>
                                                                   {
                                                                       foreach (var raw in paths)
                                                                       {
                                                                           if (string.IsNullOrWhiteSpace(raw)) continue;
                                                                           var dir = Directory.Exists(raw) ? raw : Path.GetDirectoryName(Path.GetFullPath(raw));
                                                                           if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) { AssetsRoot = dir; break; }
                                                                       }
                                                                   });

    // ─── 第二步：预览 ─────────────────────────────────────────────────────

    /// <summary>选中行变化时自动刷新右侧预览。</summary>
    partial void OnSelectedRowChanged(ScanRowViewModel? value) => RefreshMaterialPreview();
    /// <summary>重建右侧预览：选中行所属那份 .vmat 的完整生成结果。</summary>
    /// <remarks>
    /// 走的是与写盘<b>完全同一条</b> <see cref="VmatBuildService"/>，因此预览里看到的就是
    /// 第三步会写出的内容，不存在「预览和实际不一致」的问题。
    /// </remarks>
    public void RefreshMaterialPreview()
    {
        if (SelectedShader is not { } shader)
        {
            SelectedPreview = "（尚未选择着色器）";
            return;
        }
        if (SelectedRow is null)
        {
            SelectedPreview = "（尚未扫描）";
            return;
        }

        var group = SelectedRow.GroupName;
        var dir = SelectedRow.RelativeDirectory;
        // 预览的分组口径必须与 BuildPlan 完全一致：同文件夹 + 同名才算同一份材质。
        // 只按名字聚会把 concrete\wall 与 floor\wall 两组的贴图混进同一份预览，
        // 看到的和第三步实际写出的就不是同一回事。
        var files = ScanRows
            .Where(r => r.Include && r.IsMatched
                && string.Equals(r.RelativeDirectory, dir, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.GroupName, group, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.FilePath)
            .ToList();

        if (files.Count == 0)
        {
            SelectedPreview = $"（{group} 下面还没有勾选且已归类的贴图）";
            return;
        }

        SelectedPreview = ControlErrorRecorder.Guard(
            "预览材质文本", this,
            () => VmatBuildService.Preview(shader, files, BuildEffectiveRules(), AssetsRoot,
                BuildRoleOverrides()),
            fallback: "（预览失败，详见错误日志）");
    }

    // ─── 第三步：生成 ─────────────────────────────────────────────────────

    /// <summary>
    /// 执行生成。
    /// </summary>
    /// <remarks>
    /// <b>为什么放后台线程：</b><see cref="Progress{T}"/> 的回调要经由 UI 同步上下文回到
    /// 派发线程。若在 UI 线程同步跑完整个循环，回调只在循环结束后才排队，
    /// 进度条会「一步跳到 100%」而全程不动。
    /// </remarks>
    [RelayCommand]
    public async Task Generate()
    {
        if (IsGenerating) return;
        if (SelectedShader is not { } shader) return;

        var plan = BuildPlan();
        if (plan.Count == 0)
        {
            ProgressMessage = "没有可生成的内容：请先扫描并勾选贴图。";
            return;
        }

        IsGenerating = true;
        ProgressPercent = 0;
        BuildSummary = string.Empty;
        ProgressMessage = $"共 {plan.Count} 个材质待生成…";

        _buildCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<VmatBuildProgress>(p =>
            {
                ProgressPercent = p.Percent;
                ProgressMessage = p.Message;
            });

            var overrides = BuildRoleOverrides();
            var result = await Task.Run(
                () => VmatBuildService.BuildByGroup(shader, plan, BuildEffectiveRules(),
                    AssetsRoot, ProjectRoot, progress, overwriteExisting: true,
                    roleOverrides: overrides, cancellationToken: _buildCts.Token),
                _buildCts.Token).ConfigureAwait(true);

            BuildSummary = Describe(result);
            // 快照输出目录：成功写完才记录，按钮只认「真正产出过文件的位置」，
            // 之后就算改了项目文件夹也不会把按钮指向没写过东西的目录。
            LastOutputFolder = ResolveCommonOutputFolder(result);
            ProgressPercent = 100;
            ProgressMessage = BuildSummary;
        }
        catch (OperationCanceledException)
        {
            BuildSummary = "已取消生成。已写出的 .vmat 不会被回滚。";
            ProgressMessage = BuildSummary;
        }
        catch (Exception ex)
        {
            ErrorLog.Error("生成 .vmat", nameof(Generate), ex);
            BuildSummary = $"生成失败：{ex.Message}";
            ProgressMessage = BuildSummary;
        }
        finally
        {
            _buildCts.Dispose();
            _buildCts = null;
            IsGenerating = false;
        }
    }

    /// <summary>取消正在进行的生成。</summary>
    [RelayCommand]
    public void CancelGeneration()
    {
        if (_buildCts is { IsCancellationRequested: false }) _buildCts.Cancel();
    }

    /// <summary>
    /// 在资源管理器里打开上一次生成的输出文件夹（按钮在生成成功后才可点）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenOutputFolder))]
    public void OpenOutputFolder()
    {
        var folder = LastOutputFolder;
        if (folder.Length == 0) return;

        ControlErrorRecorder.GuardWithDialog("打开输出文件夹", this, () =>
        {
            if (!Directory.Exists(folder))
            {
                throw new DirectoryNotFoundException(
                    $"输出文件夹已不存在，可能在生成之后被移动或删除。\n\n{folder}");
            }

            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        });
    }

    /// <summary>
    /// 从生成结果里取输出目录：所有写出的 .vmat 都在「项目根 + 贴图子目录」下，
    /// 优先取项目根本身（一键直达整个输出树的顶层）；拿不到结果时退回当时的项目文件夹。
    /// </summary>
    private string ResolveCommonOutputFolder(VmatBuildResult result)
    {
        if (!string.IsNullOrWhiteSpace(ProjectRoot) && Directory.Exists(ProjectRoot))
            return ProjectRoot;
        var first = result.WrittenFiles.FirstOrDefault();
        return first is null ? string.Empty : Path.GetDirectoryName(first) ?? string.Empty;
    }

    /// <summary>
    /// 把列表里勾选且命中的行，按「<b>所在文件夹</b> + 最终分组名」聚成生成计划。
    /// </summary>
    /// <remarks>
    /// <para>用户的<b>手动指定优先</b>于自动基名——同一个 <c>wall_diff.png</c> 被分到
    /// <c>wall</c> 还是 <c>wall_b</c>，由列表那一列说了算。</para>
    /// <para><b>计划键必须带上所在文件夹。</b>输出目录取的是组内 basecolor 贴图
    /// 镜像出来的子目录，同名材质（<c>concrete\wall</c> 与 <c>floor\wall</c>）
    /// 若只按名字聚合会被并成一组：所有贴图挤进第一个文件夹，其它文件夹的
    /// <c>.vmat</c> 根本没有被写出来，槽位还会跨文件夹互相抢占。
    /// 键用 相对目录 + <c>\0</c> + 组名 拼成（<c>\0</c> 是文件名里的非法字符，
    /// 任何真实材质名都无法伪造出同样的键），值里仍只放贴图文件，供生成端用。</para>
    /// </remarks>
    private Dictionary<string, List<string>> BuildPlan()
    {
        var plan = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in ScanRows)
        {
            if (!row.Include || !row.IsMatched) continue;
            var key = row.RelativeDirectory + "\0" + row.GroupName;
            if (!plan.TryGetValue(key, out var files))
            {
                files = [];
                plan[key] = files;
            }
            files.Add(row.FilePath);
        }
        return plan;
    }

    /// <summary>把生成结果汇总成一段可直接显示的中文说明。</summary>
    private static string Describe(VmatBuildResult result)
    {
        var sb = new StringBuilder();
        sb.Append($"生成 {result.WrittenFiles.Count} 个 .vmat，共 {result.TotalFiles} 张贴图，"
            + $"复制到输出目录 {result.CopiedImageFiles.Count} 张。");
        if (result.Conflicts.Count > 0) sb.Append($"　同槽位冲突 {result.Conflicts.Count} 处。");
        if (result.SkippedExisting.Count > 0)
            sb.Append($"　同名文件已存在、跳过 {result.SkippedExisting.Count} 个（未覆盖）。");
        if (result.SkippedImageCopies.Count > 0)
            sb.Append($"　贴图已存在、未覆盖 {result.SkippedImageCopies.Count} 张。");
        if (result.MissingBaseColorGroups.Count > 0)
        {
            sb.Append($"　没有 basecolor 而整体跳过 {result.MissingBaseColorGroups.Count} 个材质："
                + $"{string.Join("、", result.MissingBaseColorGroups.Take(5))}");
        }

        if (result.UnassignedFiles.Count > 0)
            sb.Append($"　未写入 {result.UnassignedFiles.Count} 个。");
        return sb.ToString();
    }
}
