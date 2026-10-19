using CommunityToolkit.Mvvm.ComponentModel;
using VmatLib;

namespace VmatGenerator.ViewModels;

/// <summary>
/// 「该着色器需要几类贴图、每类认哪些后缀」的一行。显示槽位名与命中的参数键，
/// 后缀本身<b>可以直接改</b>——推断只是给个起点，真实资产的命名习惯千差万别，
/// 能自己敲成 <c>_base | _d | _diffuse</c> 才谈得上可用。
///
/// <para><b>后缀之间用 <c>|</c> 分隔。</b>竖线是这类短词表最顺手的分隔符：
/// 一眼能数出几个、删一个只动一个字符、也不会和后缀里可能出现的
/// <c>_ - . 空格</c> 打架。</para>
///
/// <para><b>显示与匹配用两种形态。</b><see cref="SuffixText"/> 保留用户敲的原样
/// （带前导下划线，和磁盘上的文件名对得上）；真正拿去匹配的是
/// <see cref="ParsedSuffixes"/>，它经 <see cref="TextureSuffixMatcher.NormalizeName"/>
/// 去掉前导下划线、转小写并去重。</para>
/// </summary>
/// <remarks>构造一行推断结果。</remarks>
/// <param name="set">VmatLib 层的推断结果。</param>
/// <param name="existingSuffixes">
/// 规则表里<b>已归一化</b>的后缀集合（小写、<b>无</b>前导下划线）；
/// 候选同样归一化后再比对——候选带 <c>_</c>、规则表不带，直接比对永远不相等。
/// </param>
public sealed partial class InferredSuffixViewModel(InferredSuffixSet set, IReadOnlySet<string> existingSuffixes) : ObservableObject
{
    /// <summary>后缀之间的分隔符。</summary>
    public const string Separator = "|";

    private readonly InferredSuffixSet _set = set;
    private IReadOnlySet<string> _existingSuffixes = existingSuffixes;

    /// <summary>语义槽位。</summary>
    public TextureRole Role => _set.Role;

    /// <summary>槽位中文名，例如「法线」。</summary>
    public string RoleDisplay => _set.RoleDisplay;

    /// <summary>命中的着色器参数键，例如 <c>TextureNormal1</c>。</summary>
    public string ParameterKey => _set.ParameterKey;

    /// <summary>
    /// 可编辑的后缀文本，多个用 <c>|</c> 分隔。回车或失焦即生效。
    /// </summary>
    [ObservableProperty]
    private string _suffixText = Format(set.Candidates);

    /// <summary>是否把本行后缀并入本次扫描。</summary>
    [ObservableProperty]
    private bool _isEnabled = true;

    /// <summary>
    /// 相对规则表新增的后缀。
    /// </summary>
    /// <remarks>
    /// 用户新敲进来的后缀自然落在这一栏，所以「新增 N」是跟着输入实时变的，
    /// 而不是在构造时算死。
    /// </remarks>
    public IReadOnlyList<string> Fresh =>
        [.. ParsedSuffixes.Where(s => !_existingSuffixes.Contains(s))];

    /// <summary>新增后缀的个数，用于列表右侧的计数标记。</summary>
    public int FreshCount => Fresh.Count;

    /// <summary>新增后缀的可读摘要，用于按钮与提示。</summary>
    public string FreshSummary =>
        FreshCount == 0 ? "（均已在规则表）" : $"新增 {FreshCount} 个：{string.Join(Separator, Fresh)}";

    /// <summary>解析出的后缀：已归一化、去重、剔除空项。</summary>
    public IReadOnlyList<string> ParsedSuffixes => Parse(SuffixText);

    /// <summary>参与本次扫描时实际生效的后缀（仅在勾选时非空）。</summary>
    public IReadOnlyList<string> EffectiveSuffixes =>
        IsEnabled ? ParsedSuffixes : [];

    /// <summary>本行是否还有后缀；决定勾选框是否可用。</summary>
    public bool HasSuffixes => ParsedSuffixes.Count > 0;

    /// <summary>本行是否有相对规则表新增的后缀；驱动列表右侧的「新增 N」标记。</summary>
    /// <remarks>
    /// 单独给一个布尔量，是因为 <c>FreshCount</c> 是整数：
    /// <c>DataTrigger Value="1"</c> 的含义是「恰好等于 1」，不是「大于 0」，
    /// 直接拿计数去触发会让 2、3、5 这些行一个都显示不出来。
    /// </remarks>
    public bool HasFreshSuffixes => FreshCount > 0;

    /// <summary>
    /// 规则表变化后刷新「新增」判定，<b>不</b>动用户已经敲好的文本。
    /// </summary>
    /// <remarks>
    /// 「把新增后缀加入规则表」之后必须走这条路：走整份重推断会把刚敲的自定义后缀
    /// 一并冲掉，用户等于白输入。
    /// </remarks>
    public void UpdateExisting(IReadOnlySet<string> existingSuffixes)
    {
        _existingSuffixes = existingSuffixes;
        RaiseDerived();
    }

    /// <summary>把后缀列表拼成可编辑文本。</summary>
    private static string Format(IEnumerable<string> suffixes) => string.Join($" {Separator} ", suffixes);

    /// <summary>把可编辑文本拆回后缀列表。</summary>
    private static IReadOnlyList<string> Parse(string? text) => string.IsNullOrWhiteSpace(text)
            ? Array.Empty<string>()
            : [.. text.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Select(TextureSuffixMatcher.NormalizeName)
                  .Where(s => s.Length > 0)
                  .Distinct(StringComparer.Ordinal)];

    /// <summary>后缀文本或勾选状态变化后，刷新所有派生属性。</summary>
    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(Fresh));
        OnPropertyChanged(nameof(FreshCount));
        OnPropertyChanged(nameof(FreshSummary));
        OnPropertyChanged(nameof(ParsedSuffixes));
        OnPropertyChanged(nameof(EffectiveSuffixes));
        OnPropertyChanged(nameof(HasSuffixes));
        OnPropertyChanged(nameof(HasFreshSuffixes));
    }

    partial void OnSuffixTextChanged(string value) => RaiseDerived();

    partial void OnIsEnabledChanged(bool value) => OnPropertyChanged(nameof(EffectiveSuffixes));
}