using VmatLib;

namespace VmatGenerator.ViewModels;

/// <summary>
/// 扫描列表「槽位」下拉框里的一个候选项。
/// </summary>
/// <remarks>
/// <para><b>为什么要包一层，而不是直接用 <see cref="TextureRole"/>。</b>
/// 候选项要区分「当前着色器支持」与「本行已选中、但当前着色器不支持」两种状态，
/// 这个状态只有生成列表的一方知道，枚举值本身带不出来。而这一项<b>不能</b>简单删掉——
/// 换着色器会让一批已归类的行失去依据，静默清空等于替用户做决定；
/// 让它留在列表里并标注出来，用户一眼就知道该行的依据已经失效。</para>
///
/// <para><b>同一状态共用同一实例（享元）。</b>这不是为了省内存，而是为了让组合框
/// 找回选中项：换着色器会整批换掉 <see cref="ScanRowViewModel.RoleOptions"/>，
/// 而选择器是拿<b>项本身</b>去列表里比对的，实例一换就比不中，
/// 选中项被清成 -1、关闭态随即显示空白——读起来像「这一行没加载出来」。
/// 实例复用之后同一个项在新旧两份列表里是同一个引用，比对必然命中。</para>
///
/// <para>不可变，且由 <see cref="Get"/> 统一构造；不要自己 new，否则就失去上面的意义。</para>
/// </remarks>
public sealed class RoleOptionViewModel
{
    /// <summary>
    /// 全部「槽位 × 是否受支持」组合的实例表，一次建好。
    /// 从 <see cref="Enum.GetValues"/> 生成，将来加槽位不用改这里。
    /// </summary>
    private static readonly IReadOnlyDictionary<(TextureRole Role, bool Supported), RoleOptionViewModel> Pool =
        Enum.GetValues<TextureRole>()
            .SelectMany(role => new[] { new RoleOptionViewModel(role, true), new RoleOptionViewModel(role, false) })
            .ToDictionary(option => (option.Role, option.IsSupported));

    /// <summary>供「还没按着色器筛选」时使用的空列表。</summary>
    public static IReadOnlyList<RoleOptionViewModel> None { get; } = [];

    /// <summary>取某个「槽位 × 是否受支持」组合的唯一实例。</summary>
    /// <param name="role">候选项代表的槽位。</param>
    /// <param name="isSupported">当前着色器是否有这个参数键。</param>
    /// <returns>该组合的共享实例。</returns>
    public static RoleOptionViewModel Get(TextureRole role, bool isSupported) => Pool[(role, isSupported)];

    private RoleOptionViewModel(TextureRole role, bool isSupported)
    {
        Role = role;
        IsSupported = isSupported;
    }

    /// <summary>该候选项代表的槽位。</summary>
    public TextureRole Role { get; }

    /// <summary>当前选中的着色器是否有这个参数键。</summary>
    /// <remarks>
    /// 为假时生成不会静默写错地方：解析失败会被记进未解析槽位清单并报给用户。
    /// </remarks>
    public bool IsSupported { get; }

    /// <summary>候选项的中英文本，与扫描列表其它地方取同一份。</summary>
    public string Display => TextureRoleTokens.DescribeBilingual(Role);

    /// <summary>鼠标悬停时的完整说明。</summary>
    /// <remarks>
    /// 「当前着色器没有这个参数键」是硬伤提示，「生成时会被跳过并列入未解析槽位」是后果提示，
    /// 两句都得说：只说前半句，用户不知道该怎么做。
    /// </remarks>
    public string Note => IsSupported
        ? "这一张贴图在 .vmat 里对应哪个参数槽位"
        : $"当前着色器没有 {Role} 的参数键，生成时会跳过并列入「未解析槽位」";

    /// <summary>
    /// 槽位列里直接显示的那一行短提示；受支持时为空串（模板据此折叠掉整行）。
    /// </summary>
    /// <remarks>
    /// <b>为什么不能直接用 <see cref="Note"/>。</b>那一列只有 214px，整句说明放下去
    /// 会在中间被硬裁成「⚠ 当前着色器没有 Translucency 的参数键，生」，比不显示更糟。
    /// 短句进列表，完整解释留给鼠标。
    /// </remarks>
    public string ShortWarning => IsSupported ? string.Empty : "⚠ 当前着色器不支持";

    /// <inheritdoc/>
    public override string ToString() => Display;
}
