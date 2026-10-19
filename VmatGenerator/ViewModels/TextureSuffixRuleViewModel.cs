using CommunityToolkit.Mvvm.ComponentModel;
using VmatLib;

namespace VmatGenerator.ViewModels;

/// <summary>
/// 后缀规则表的一行：<c>文件名后缀 → 语义槽位（TextureRole）</c>。
///
/// <see cref="Role"/> 刻意存<b>枚举名字符串</b>而不是枚举本身：
/// <list type="bullet">
/// <item>下拉框可以直接 <c>ItemsSource = KnownRoles</c> 双向绑定，无需转换器；</item>
/// <item>非法值（用户手工编辑 / 旧配置文件）不会让绑定抛异常，
/// 解析时退回 <see cref="TextureRole.Unknown"/></item>
/// </list>
///
/// <see cref="Suffix"/> / <see cref="Role"/> / <see cref="Enabled"/> 任一变化都会
/// 同步刷新两个派生只读属性 <see cref="NormalizedSuffix"/> 与 <see cref="RoleDisplay"/>，
/// 供规则表直接显示。
/// </summary>
public sealed partial class TextureSuffixRuleViewModel : ObservableObject
{
    public TextureSuffixRuleViewModel()
    {
    }

    public TextureSuffixRuleViewModel(string suffix, TextureRole role, bool enabled)
    {
        _suffix = suffix;
        _role = role.ToString();
        _enabled = enabled;
    }

    /// <summary>后缀原文，例如 <c>_normal</c>（大小写、连字符、空格都由 VmatLib 归一化）。</summary>
    [ObservableProperty]
    private string _suffix = string.Empty;

    /// <summary>语义槽位的枚举名字符串，例如 <c>Normal</c>。</summary>
    [ObservableProperty]
    private string _role = nameof(TextureRole.Unknown);

    /// <summary>是否参与匹配（关闭后该规则被 <c>TextureSuffixMatcher</c> 跳过）。</summary>
    [ObservableProperty]
    private bool _enabled = true;

    /// <summary>
    /// 宿主对话框的 ViewModel，供行内绑定回读跨行上下文（当前选中的生成着色器类型）。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么不用 <c>RelativeSource FindAncestor</c>：</b>「当前着色器解析预览」列
    /// 原先写作 <c>Path="DataContext.SelectedShader",
    /// RelativeSource={RelativeSource AncestorType=...}</c>。该绑定位于
    /// <b><c>MultiBinding</c> 的子绑定</b>里，WPF 无法为它解析祖先，于是退回到
    /// <c>DataContext</c>（也就是本行对象）求值，产生
    /// <c>'DataContext' property not found on 'TextureSuffixRuleViewModel'</c> 路径错误，
    /// 该列恒为空。同一文件里语义槽位列的相同写法却能正常工作，差别就在于它<b>不是</b>
    /// MultiBinding 子绑定——所以根因是 MultiBinding 的子绑定，不是祖先回溯本身
    /// （先前改成 <c>AncestorType={x:Type DataGrid}</c> 同样报错，已证伪）。</para>
    /// <para>改为持有宿主、走普通路径绑定，视觉树是否连通便不再影响结果；
    /// <see cref="MainViewModel.SelectedShader"/> 变化时由
    /// <see cref="ObservableObject"/> 发出通知，所有行自动重算，无需手工同步。</para>
    /// <para>允许为 <c>null</c>（单元测试直接构造时）：转换器据此显示「未选着色器」占位。</para>
    /// </remarks>
    public MainViewModel? Owner { get; init; }

    /// <summary>归一化后的后缀（只读）：<c>TextureSuffixMatcher.NormalizeName(Suffix)</c>。</summary>
    public string NormalizedSuffix => Safe(() => TextureSuffixMatcher.NormalizeName(Suffix), Suffix);

    /// <summary>语义槽位的中文名（只读），例如 <c>法线</c>。</summary>
    public string RoleDisplay => Safe(() => TextureRoleTokens.Describe(ParseRole(Role)), Role);

    partial void OnSuffixChanged(string value) => OnPropertyChanged(nameof(NormalizedSuffix));

    partial void OnRoleChanged(string value) => OnPropertyChanged(nameof(RoleDisplay));

    /// <summary>转成可序列化的 <see cref="TextureSuffixRule"/>（保存配置时使用）。</summary>
    public TextureSuffixRule ToRule() => new(Suffix, ParseRole(Role), Enabled);

    /// <summary>从配置里的规则构造界面行（载入配置时使用）。</summary>
    public static TextureSuffixRuleViewModel FromRule(TextureSuffixRule rule, MainViewModel? owner = null) => new(rule.Suffix, rule.Role, rule.Enabled) { Owner = owner };

    /// <summary>枚举名 → 枚举；解析失败一律退回 <see cref="TextureRole.Unknown"/>。</summary>
    public static TextureRole ParseRole(string? text) => TextureRoleTokens.TryParseRole(text, out var role) ? role : TextureRole.Unknown;

    /// <summary>
    /// 派生属性的计算不应让界面崩溃（VmatLib 侧一旦有异常，这里退回可显示的原文）。
    /// </summary>
    private static string Safe(Func<string> calc, string fallback)
    {
        try
        {
            return calc();
        }
        catch
        {
            return fallback;
        }
    }
}