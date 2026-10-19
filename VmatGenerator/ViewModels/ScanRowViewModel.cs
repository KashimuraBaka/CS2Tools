using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using VmatGenerator.Imaging;
using VmatLib;

namespace VmatGenerator.ViewModels;

/// <summary>
/// 第二步扫描列表里的一行：一个被发现的图像文件，以及它被归到了哪个 <c>.vmat</c>。
/// </summary>
/// <remarks>
/// <b>为什么 <see cref="GroupName"/> 可写：</b>自动扫描按「去掉后缀后的基名」分组，
/// 但用户对「哪些贴图属于同一份材质」的判断常常与命名约定不一致
/// （例如同一张 <c>wall_diff.png</c> 被两个材质共用）。
/// 允许逐行改归属，比强迫用户去改后缀规则更直接。
/// </remarks>
public sealed partial class ScanRowViewModel : ObservableObject
{
    /// <summary>构造一行扫描结果。</summary>
    /// <param name="filePath">源文件完整路径。</param>
    /// <param name="matchedSuffix">命中的归一化后缀；未命中时为空串。</param>
    /// <param name="role">命中的语义槽位；未命中时为 <see cref="TextureRole.Unknown"/>。</param>
    /// <param name="groupName">初始归属的 .vmat 名（= 去掉后缀后的基名）。</param>
    public ScanRowViewModel(string filePath, string matchedSuffix, TextureRole role, string groupName)
    {
        FilePath = filePath;
        MatchedSuffix = matchedSuffix;
        _role = role;
        GroupName = groupName;
    }

    /// <summary>源文件完整路径。</summary>
    public string FilePath { get; }

    /// <summary>文件名（含扩展名），列表显示用。</summary>
    public string FileName => Path.GetFileName(FilePath);

    /// <summary>所在子目录相对资产根的路径；直接位于根下时为空串。</summary>
    public string RelativeDirectory { get; init; } = string.Empty;

    /// <summary>命中的归一化后缀，例如 <c>normal</c>；未命中时为空串。</summary>
    public string MatchedSuffix { get; }

    /// <summary>槽位下拉框的候选项；由 <see cref="UpdateRoleOptions"/> 按当前着色器填充。</summary>
    /// <remarks>
    /// <para><b>只有当前着色器真的有的参数键才是候选项。</b>
    /// 给 <c>csgo_environment</c> 列出 FoamMask、给 <c>csgo_water_fancy</c> 列出
    /// AmbientOcclusion 都没有意义：这些键在对应着色器里根本不存在，
    /// 用户挑了只会得到一个写不进去的槽位。</para>
    ///
    /// <para><b>本行已选中的槽位永远在列表里。</b>换着色器会让一批已归类的行失去依据，
    /// 静默清空等于替用户做决定，而留着又会被误读成「这个着色器支持」。
    /// 所以额外补一项 <see cref="RoleOptionViewModel.IsSupported"/> 为假的候选，
    /// 在下拉框与列表里都标出来（见 <see cref="SelectedRoleOption"/>）。</para>
    /// </remarks>
    public IReadOnlyList<RoleOptionViewModel> RoleOptions { get; private set; } = RoleOptionViewModel.None;

    /// <summary>
    /// 下拉框当前选中的候选项；读写都经它，槽位本身仍以 <see cref="Role"/> 为准。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么绑它而不是绑 Role。</b>换着色器会整批换掉候选项列表，
    /// 组合框是拿项本身去新列表里找回选中的；绑裸枚举时它比不中，选中项被清成 -1、
    /// 关闭态随即一片空白，读起来像这一行没加载出来。候选项共用共享实例
    /// （<see cref="RoleOptionViewModel.Get"/>），同一个项在新旧两份列表里是同一个引用，比得中。</para>
    ///
    /// <para><b>setter 刻意忽略 null。</b>换列表的一瞬间组合框会因为「找不到原来的项」
    /// 回写一次 null；照直写下去等于把用户的归类结果清空。真要取消归类应该取消
    /// 「生成」勾选框，而不是靠一次着色器切换顺手做掉。</para>
    /// </remarks>
    public RoleOptionViewModel? SelectedRoleOption
    {
        get => Role == TextureRole.Unknown ? null : RoleOptionViewModel.Get(Role, IsSupportedRole(Role));
        set
        {
            if (value is null || value.Role == Role) return;
            Role = value.Role;
        }
    }

    /// <summary>某个槽位是否在当前着色器的支持范围里。</summary>
    private bool IsSupportedRole(TextureRole role)
    {
        foreach (var supported in _supportedRoles)
        {
            if (supported == role) return true;
        }

        return false;
    }


    /// <summary>
    /// 按当前着色器支持的槽位重算下拉框候选项。换着色器时由列表的一方对每一行调用。
    /// </summary>
    /// <param name="supported">当前着色器支持的槽位（<see cref="TextureRole.Unknown"/> 会被跳过）。</param>
    public void UpdateRoleOptions(IReadOnlyList<TextureRole> supported)
    {
        _supportedRoles = supported ?? [];
        ReapplyRoleOptions();
    }

    /// <summary>最近一次算出的受支持槽位；改槽位时要据此重算候选项。</summary>
    private IReadOnlyList<TextureRole> _supportedRoles = [];

    /// <summary>
    /// 按已记住的支持范围重算候选项；组合框展开时由界面调用。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么不在槽位变更时就重算。</b>那等于在组合框处理选中回写的回调里
    /// 换掉它的 ItemsSource，选中状态正处在中间态，用户点一下反而可能把槽位清空。</para>
    /// <para><b>推迟到展开时也不会露出破绽。</b>关闭态显示的是当前选中项，它上一轮就被要保证
    /// 存在于列表里。唯一可能过期的，是上一轮为「保住旧归类」补的那项不受支持候选——
    /// 而它只在下拉面板展开时才看得见，在它被看见之前重算一次就够。</para>
    /// <para>内容没变时不换列表：换一次面板里的容器会全部重建，光标与滚动位置都会丢。
    /// 绝大多数展开并不需要重建。</para>
    /// </remarks>
    public void ReapplyRoleOptions()
    {
        var expected = BuildRoleOptions();
        if (IsSameRoleOptions(RoleOptions, expected)) return;

        RoleOptions = expected;
        OnPropertyChanged(nameof(RoleOptions));
        OnPropertyChanged(nameof(SelectedRoleOption));
    }

    /// <summary>
    /// 算出应有的候选项：支持范围里的全部，加上当前槽位（若它不受支持）。
    /// </summary>
    /// <remarks>
    /// 结果里最多只有一项不受支持，且恰好就是当前选中项。上一轮补的那项在用户改选别的槽位
    /// 之后已经没有来由，留着会被读成「这个着色器支持它，但它有问题」。
    /// </remarks>
    private IReadOnlyList<RoleOptionViewModel> BuildRoleOptions()
    {
        var list = new List<RoleOptionViewModel>(_supportedRoles.Count + 1);
        var contains = false;

        foreach (var role in _supportedRoles)
        {
            if (role == TextureRole.Unknown) continue;
            list.Add(RoleOptionViewModel.Get(role, true));
            if (role == Role) contains = true;
        }

        // 着色器换掉之后，本行原来靠后缀猜出来的槽位可能已经不存在了。不能直接丢掉：
        // 那等于替用户把归类结果清空，而且组合框找不到选中项会显示成空白，
        // 读起来像「没加载出来」。补一项标注为「不支持」，让它继续可见并说明依据已失效。
        if (!contains && Role != TextureRole.Unknown) list.Add(RoleOptionViewModel.Get(Role, false));

        return list;
    }

    /// <summary>两份候选项是否等价：同序、同槽位、同支持标志。</summary>
    private static bool IsSameRoleOptions(
        IReadOnlyList<RoleOptionViewModel> left, IReadOnlyList<RoleOptionViewModel> right)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
        {
            if (left[i].Role != right[i].Role) return false;
            if (left[i].IsSupported != right[i].IsSupported) return false;
        }

        return true;
    }

    /// <summary>
    /// 该贴图命中的语义槽位，可在「槽位」列里手动改。
    /// </summary>
    /// <remarks>
    /// 自动归类按后缀猜，猜错是常态：<c>_spec</c> 可能是粗糙度也可能是自发光，
    /// 取决于材质本身。提供一个组合框把纠正成本从「改规则表 + 全量重扫」
    /// 降到「这一行点两下」。
    /// </remarks>
    [ObservableProperty]
    private TextureRole _role;

    /// <summary>槽位的中文名 + 枚举名，便于在列表里一眼看懂且能与配置文件对上。</summary>
    /// <remarks>中英并排的原因见 <see cref="TextureRoleTokens.DescribeBilingual"/>。
    /// 槽位为 <see cref="TextureRole.Unknown"/> 只剩一种来源：用户手动把行取消归类；
    /// 未命中后缀规则的行由扫描侧直接默认 Color 槽位。</remarks>
    public string RoleDisplay => Role == TextureRole.Unknown ? "（未命中）" : TextureRoleTokens.DescribeBilingual(Role);

    /// <summary>该行是否由后缀规则真命中。未命中的行默认 Color 槽位（<see cref="Role"/>
    /// 仍非 Unknown），所以这里不能用槽位是否 Unknown 来判定。
    /// 「写入目标」列只画一个方向朝右的箭头（目标就是右侧的『输出的 .vmat 文件名』列，
    /// 不在箭头旁重复写材质名）；未命中默认 Color 的行由该列的下划线触发器据此区分。</summary>
    public bool MatchedByRule { get; init; } = true;

    /// <summary>该贴图是否已归类到可用槽位（真命中或默认 Color 都为真）。</summary>
    public bool IsMatched => Role != TextureRole.Unknown;

    /// <summary>该贴图是否参与生成。扫描后<b>不管有无命中一律默认勾选</b>；
    /// 取消勾选才让该贴图退出材质。</summary>
    [ObservableProperty]
    private bool _include;

    /// <summary>勾选状态变化时触发：取消勾选会让该贴图退出材质、也可能让材质整个消失。</summary>
    public event Action? IncludeChanged;

    /// <summary>由生成器挂接 <see cref="Include"/> 的属性变更通知。</summary>
    partial void OnIncludeChanged(bool value) => IncludeChanged?.Invoke();

    /// <summary>槽位被手动改过时触发，由 <see cref="MainViewModel"/> 刷新材质预览。</summary>
    /// <remarks>
    /// 槽位决定 .vmat 里写哪个参数键，改动会直接反映到生成的材质上，
    /// 预览不跟着刷新的话，用户看到的就是一份与实际写入不一致的内容。
    /// </remarks>
    public event Action? RoleChanged;

    /// <summary>由生成器挂接 <see cref="Role"/> 的属性变更通知。</summary>
    partial void OnRoleChanged(TextureRole value)
    {
        OnPropertyChanged(nameof(RoleDisplay));
        OnPropertyChanged(nameof(IsMatched));
        OnPropertyChanged(nameof(SelectedRoleOption));

        // 归好槽位的行必须在勾选状态。「默认全勾」之下这句通常是恒等操作，
        // 但用户先取消勾选、再改槽位时，它把取消撤销回来：用户明说了这张属于
        // 哪个槽位，留着不勾等于让这次修改悄无声息地失效。
        if (value != TextureRole.Unknown) Include = true;

        RoleChanged?.Invoke();
    }

    /// <summary>
    /// 归属的 <c>.vmat</c> 名（不含扩展名）。用户可手动改。
    /// </summary>
    /// <remarks>
    /// 改动会触发 <see cref="GroupChanged"/>，由 <see cref="MainViewModel"/> 重新聚合分组预览。
    /// </remarks>
    [ObservableProperty]
    private string? _groupNameOverride;

    /// <summary>实际生效的分组名：用户指定优先，否则用扫描给出的基名。</summary>
    public string GroupName { get => string.IsNullOrWhiteSpace(GroupNameOverride) ? field : GroupNameOverride.Trim(); private set; }

    /// <summary>
    /// 表格里显示的<b>输出 .vmat 文件名</b>：生效分组名加 <c>.vmat</c> 扩展名。
    /// </summary>
    /// <remarks>
    /// <para>这一列直接写用户真正会得到的文件名（<c>wall.vmat</c> 而不是 <c>wall</c>），
    /// 免得自己在脑子里补扩展名。写回时把扩展名剥掉再存进
    /// <see cref="GroupNameOverride"/>，所以分组键永远不含扩展名，
    /// 与生成器、预览聚合用的 <see cref="GroupName"/> 保持同一口径。</para>
    ///
    /// <para>清空文本 = 清掉手动指定，回到按基名的自动归类。</para>
    /// </remarks>
    public string VmatFileName
    {
        get => GroupName + ".vmat";
        set
        {
            var name = (value ?? string.Empty).Trim();
            if (name.EndsWith(".vmat", StringComparison.OrdinalIgnoreCase))
                name = name[..^".vmat".Length].Trim();
            GroupNameOverride = name;
        }
    }

    /// <summary>分组归属发生变化时触发。</summary>
    public event Action? GroupChanged;

    /// <summary>重设扫描给出的基名（重新扫描时用，会清掉用户的手动指定）。</summary>
    public void ResetGroup(string baseName)
    {
        GroupName = baseName;
        GroupNameOverride = string.Empty;
        OnPropertyChanged(nameof(GroupName));
        OnPropertyChanged(nameof(VmatFileName));
    }

    /// <summary>由生成器挂接 <see cref="GroupNameOverride"/> 的属性变更通知。</summary>
    partial void OnGroupNameOverrideChanged(string? value)
    {
        OnPropertyChanged(nameof(GroupName));
        OnPropertyChanged(nameof(VmatFileName));
        GroupChanged?.Invoke();
    }

    // ─── 「输出的 .vmat 文件名」列的下拉候选 ─────────────────────────────

    /// <summary>
    /// 材质名下拉的候选项：<b>本行贴图所在文件夹下实际存在</b>的全部 <c>.vmat</c> 文件名。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么按文件夹列现有文件。</b>现有材质就是贴图旁边的那些 <c>.vmat</c>，
    /// 把散图并进同目录的现成材质是最常见的操作；下拉列当前文件夹的名字最贴合意图。
    /// 组合框可编辑，输入一个新名字同样有效——那是给新材质添第一张贴图的路径。
    /// 注意 basecolor 仍是材质成立的必要条件：选了/写了没有颜色贴图的材质名，
    /// 生成端会整体跳过。</para>
    ///
    /// <para>由 <see cref="UpdateMaterialNames"/> 整体替换；行本身不知道全局列表，
    /// 与 <see cref="RoleOptions"/> 同一套「列表的一方推给行」的模式。</para>
    /// </remarks>
    public IReadOnlyList<string> MaterialNameOptions { get; private set; } = [];

    /// <summary>替换材质名候选项。扫描后由 <see cref="MainViewModel"/> 按行所在文件夹调用。</summary>
    /// <param name="names">候选材质文件名（带 <c>.vmat</c> 扩展名，与列内文本同形态）。</param>
    public void UpdateMaterialNames(IReadOnlyList<string> names)
    {
        names ??= [];
        // 同一目录的行共享同一份列表实例；没换实例就不抛通知，避免白白重建下拉容器。
        if (ReferenceEquals(MaterialNameOptions, names)) return;

        MaterialNameOptions = names;
        OnPropertyChanged(nameof(MaterialNameOptions));
    }
    // ─── 缩略图 ────────────────────────────────────────────────────────────

    private BitmapSource? _thumbnail;
    private int _thumbnailRequested;

    /// <summary>
    /// 该行的缩略图；还没解出来、或这个文件根本解不出来时为 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <para><b>取值即触发。</b>getter 里调 <see cref="EnsureThumbnail"/>，于是
    /// <c>Image.Source="{Binding Thumbnail}"</c> 一条绑定就够：解码完成后
    /// <see cref="LoadThumbnailAsync"/> 会抛 <c>PropertyChanged(Thumbnail)</c>，
    /// 绑定重新求值把图填上。</para>
    ///
    /// <para><b>为什么不用转换器。</b>早先写成
    /// <c>Source="{Binding FilePath, Converter=…}"</c>，有两个致命问题：
    /// 转换器拿到的是 <see cref="FilePath"/> 这个 <b>string</b>，不是本行，
    /// 类型判断直接落空、永远返回 <c>null</c>；就算改成绑本行，绑定源
    /// （文件路径）此后不会变化，异步解码完成也不会重新求值。
    /// 触发和取值必须是<b>同一个</b>属性，否则异步结果送不回去。</para>
    ///
    /// <para><b>内存不会随列表长度线性增长。</b>缩略图按 64px 解码，
    /// 再由 <see cref="ThumbnailCache"/> 按字节预算做 LRU。</para>
    ///
    /// <para>重复触发由 <see cref="EnsureThumbnail"/> 的一次性闸门兜住，
    /// 绑定被反复求值也只解一次。</para>
    /// </remarks>
    public BitmapSource? Thumbnail
    {
        get
        {
            EnsureThumbnail();
            return _thumbnail;
        }
    }

    /// <summary>是否已拿到缩略图；<c>false</c> 时界面显示占位符。</summary>
    public bool HasThumbnail => _thumbnail is not null;

    /// <summary>
    /// 请求解码本行缩略图。重复调用只生效一次。
    /// </summary>
    /// <remarks>
    /// 由 <see cref="Thumbnail"/> 的 getter 在绑定求值时调用，因此只在行被实现
    /// （滚进可视区）时才发生——DataGrid 用 <c>Recycling</c> 回收模式，
    /// 容器复用给新行时绑定一定重新求值，这比 <c>LoadingRow</c> 事件可靠得多。
    /// </remarks>
    public void EnsureThumbnail()
    {
        if (Interlocked.Exchange(ref _thumbnailRequested, 1) == 1) return;

        // 本方法必然在 UI 线程被调用，先抓住 Dispatcher；
        // 解码跑在后台，回来时把通知切回 UI 线程——WPF 绑定要求 PropertyChanged 在 UI 线程。
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = LoadThumbnailAsync(dispatcher);
    }

    private async Task LoadThumbnailAsync(Dispatcher dispatcher)
    {
        var source = await ThumbnailCache.Shared.GetAsync(FilePath);

        await dispatcher.InvokeAsync(() =>
        {
            _thumbnail = source;
            OnPropertyChanged(nameof(Thumbnail));
            OnPropertyChanged(nameof(HasThumbnail));
        });
    }
}
