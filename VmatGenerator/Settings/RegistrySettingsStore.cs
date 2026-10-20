using Microsoft.Win32;
using System.IO;
using System.Security;
using System.Security.Principal;
using VmatGenerator.Diagnostics;
using VmatLib;

namespace VmatGenerator.Settings;

/// <summary>
/// 配置的注册表存储：<c>HKEY_CURRENT_USER\SOFTWARE\Kashimura\VmatGenerator</c>。
///
/// <para><b>为什么放在 VmatGenerator 而不放在 <c>VmatLib</c>：</b><c>Microsoft.Win32.Registry</c> 是
/// Windows 专有 API，而 <c>VmatLib</c> 被刻意保持为纯净的 <c>net10.0</c> 类库（不含任何
/// WPF / Windows 依赖）。</para>
///
/// <para><b>存储结构（全部使用注册表原生类型，没有序列化层）：</b></para>
/// <code>
/// HKEY_CURRENT_USER\SOFTWARE\Kashimura\VmatGenerator
///     SchemaVersion                                REG_DWORD
///     DefaultShaderName                            REG_SZ
///     TextureRoot                                  REG_SZ
///     AssetsRoot                                   REG_SZ
///     ProjectRoot                                  REG_SZ
///     AutoAssignOnDrop                             REG_DWORD
///     RecurseTextureFolders                        REG_DWORD
///     AdoptDroppedVmatFolderAsMaterialsRoot        REG_DWORD
///     Rules\0000\Suffix                            REG_SZ
///     Rules\0000\Role                              REG_DWORD
///     Rules\0000\Enabled                           REG_DWORD
///     Rules\0001\…
/// </code>
/// <para>注册表本来就是键值表，配置也是键值——直接把字段映射成原生值即可，
/// 没有必要先拼成 JSON 再塞进一个 REG_SZ。早期版本采用「整份 JSON 单值」是多余的：
/// 它把类型信息（布尔、整数）抹平成文本，regedit 里看到的是一长行转义字符串，
/// 还得为此维护一整套编解码与「坏字段回退」逻辑。现在改成原生值后，
/// regedit 里逐项可读可改，字段类型也由注册表自身保证。</para>
///
/// <para><b>规则表为什么用子键：</b>规则是「有序的记录列表」，注册表没有数组类型，
/// 子键是它表达有序集合的原生手段（<see cref="RegistryKey.GetSubKeyNames"/> 的返回
/// 顺序即写入顺序）。子键名用四位零填充的十进制下标，保证字典序与数组下标一致——
/// 这一点是硬要求，因为 §6.4 用数组下标作为多规则命中时的第三判据。</para>
///
/// <para><b>类型不匹配怎么办：</b>读到与预期不符的值（多为手工编辑造成）时，
/// 只记录该字段名并回退该项默认值，其余配置照常读取——与旧版 JSON 路径的
/// 「坏字段回退」语义保持一致。</para>
///
/// <para><b>迁移：</b>旧版把整份配置存在 <c>SettingsJson</c>（REG_SZ）里。读取时若发现
/// 该值仍存在，会按旧格式解析并在下次保存时改写成原生结构，随后删除该值。
/// 更早的 <c>%AppData%\VmatGenerator\settings.json</c> 文件也在注册表为空时迁移，
/// 并改名留档使迁移幂等。</para>
/// </summary>
public static class RegistrySettingsStore
{
    /// <summary>配置所在的注册表路径（相对 <c>HKEY_CURRENT_USER</c>）。</summary>
    public const string KeyPath = @"SOFTWARE\Kashimura\VmatGenerator";

    /// <summary>配置结构版本（<c>REG_DWORD</c>）。</summary>
    public const string SchemaVersionValueName = "SchemaVersion";

    /// <summary>规则表子键名；其下每个子键是一条规则。</summary>
    public const string RulesKeyName = "Rules";

    /// <summary>规则子键里的三个值名。</summary>
    private const string RuleSuffixValue = "Suffix";

    private const string RuleRoleValue = "Role";

    private const string RuleEnabledValue = "Enabled";

    /// <summary>旧版「整份 JSON 单值」的值名，仅用于迁移读取。</summary>
    private const string LegacyBlobValueName = "SettingsJson";

    /// <summary>标量字段名；顺序与 <see cref="VmatGeneratorSettings"/> 的属性一致。</summary>
    private static readonly string[] ScalarValueNames =
    [
        SchemaVersionValueName,
        nameof(VmatGeneratorSettings.DefaultShaderName),
        nameof(VmatGeneratorSettings.TextureRoot),
        nameof(VmatGeneratorSettings.AssetsRoot),
        nameof(VmatGeneratorSettings.ProjectRoot),
        nameof(VmatGeneratorSettings.AutoAssignOnDrop),
        nameof(VmatGeneratorSettings.RecurseTextureFolders),
        nameof(VmatGeneratorSettings.AdoptDroppedVmatFolderAsMaterialsRoot),
    ];

    /// <summary>完整路径的显示形式，仅用于日志与诊断报告。</summary>
    public const string DisplayPath = @"HKEY_CURRENT_USER\" + KeyPath;

    /// <summary>读取配置，附带诊断报告。<b>不会抛异常。</b></summary>
    public static VmatGeneratorSettings? Load(out SettingsLoadReport report)
    {
        ErrorLog.Info("读取配置时的运行环境", KeyPath + Environment.NewLine + DescribeEnvironment());

        try
        {
            // 读取一律用只读键：注册表读权限本来就宽，把读路径开成可写没有好处，
            // 反而会让「误改用户配置」变得可能。
            using var readKey = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            if (readKey is null)
                return LoadLegacyOrDefault(out report);

            // 旧版单值还在、且没有任何原生字段 —— 这是从「整份 JSON」迁移过来的中间态。
            if (HasLegacyBlob(readKey) && !HasNativeValues(readKey))
                return MigrateLegacyBlob(out report);

            if (!HasNativeValues(readKey))
                return LoadLegacyOrDefault(out report);

            var settings = ReadNative(readKey, out var repaired);
            report = new SettingsLoadReport(
                repaired.Count == 0 ? SettingsLoadOutcome.Loaded : SettingsLoadOutcome.Repaired,
                loadedFromDisk: true,
                errorMessage: null,
                backupFilePath: null,
                repairedFields: repaired);
            return settings;
        }
        catch (Exception ex)
        {
            // 刻意<b>不按异常类型筛选</b>：设置存储是「读不出来就退回默认」的语义，
            // 任何异常都不该冒泡打断启动。
            ErrorLog.Error("读取配置", KeyPath, ex);
            report = new SettingsLoadReport(
                SettingsLoadOutcome.Defaulted, loadedFromDisk: false, errorMessage: ex.Message,
                backupFilePath: null, repairedFields: []);
            return VmatGeneratorSettings.CreateDefault();
        }
    }

    /// <summary>读取配置，失败时静默回退默认配置，绝不返回 <c>null</c>。</summary>
    public static VmatGeneratorSettings LoadOrDefault() => Load(out _) ?? VmatGeneratorSettings.CreateDefault();

    /// <summary>写入配置。<b>不会抛异常。</b></summary>
    public static SettingsSaveReport Save(VmatGeneratorSettings settings)
    {
        if (settings is null)
            return new SettingsSaveReport(false, DisplayPath, "配置对象为 null。");

        try
        {
            settings = VmatGeneratorSettingsStore.Sanitize(settings, out _);

            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
                ?? throw new IOException($"无法创建配置项 {KeyPath}。");

            key.SetValue(SchemaVersionValueName, settings.SchemaVersion, RegistryValueKind.DWord);
            key.SetValue(nameof(VmatGeneratorSettings.DefaultShaderName), settings.DefaultShaderName, RegistryValueKind.String);
            key.SetValue(nameof(VmatGeneratorSettings.TextureRoot), settings.TextureRoot, RegistryValueKind.String);
            key.SetValue(nameof(VmatGeneratorSettings.AssetsRoot), settings.AssetsRoot, RegistryValueKind.String);
            key.SetValue(nameof(VmatGeneratorSettings.ProjectRoot), settings.ProjectRoot, RegistryValueKind.String);
            key.SetValue(nameof(VmatGeneratorSettings.AutoAssignOnDrop), settings.AutoAssignOnDrop ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue(nameof(VmatGeneratorSettings.RecurseTextureFolders), settings.RecurseTextureFolders ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue(nameof(VmatGeneratorSettings.AdoptDroppedVmatFolderAsMaterialsRoot),
                settings.AdoptDroppedVmatFolderAsMaterialsRoot ? 1 : 0, RegistryValueKind.DWord);

            WriteRules(key, settings.Rules);

            // 旧版单值已由上面的原生结构取代，留着只会让下一次读取又走回 JSON 分支。
            if (key.GetValue(LegacyBlobValueName) is not null)
                key.DeleteValue(LegacyBlobValueName, throwOnMissingValue: false);

            ErrorLog.Info($"保存配置成功（{settings.Rules.Count} 条规则）", KeyPath);
            return new SettingsSaveReport(true, DisplayPath, null);
        }
        catch (Exception ex)
        {
            ErrorLog.Error("保存配置", KeyPath, ex);
            ErrorLog.Error("保存配置失败时的运行环境", nameof(RegistrySettingsStore) + Environment.NewLine
                + DescribeEnvironment(), ex);
            return new SettingsSaveReport(false, DisplayPath, DescribeSaveFailure(ex));
        }
    }

    // ── 原生值的读写 ───────────────────────────────────────────────────────

    /// <summary>旧版「整份 JSON 单值」是否还在。</summary>
    private static bool HasLegacyBlob(RegistryKey key) => key.GetValue(LegacyBlobValueName) is string legacy && !string.IsNullOrWhiteSpace(legacy);

    /// <summary>配置项里是否存在任一原生字段；全都不存在才当作「还没保存过」。</summary>
    private static bool HasNativeValues(RegistryKey key)
    {
        foreach (var name in ScalarValueNames)
        {
            if (key.GetValue(name) is not null) return true;
        }

        return key.GetSubKeyNames().Any(n => string.Equals(n, RulesKeyName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>从注册表原生值还原配置对象。</summary>
    private static VmatGeneratorSettings ReadNative(RegistryKey key, out IReadOnlyList<string> repaired)
    {
        var bad = new List<string>();
        var defaults = new VmatGeneratorSettings();

        var settings = new VmatGeneratorSettings
        {
            SchemaVersion = VmatGeneratorSettings.CurrentSchemaVersion,
            DefaultShaderName = ReadString(key, nameof(VmatGeneratorSettings.DefaultShaderName), bad)
                                ?? defaults.DefaultShaderName,
            TextureRoot = ReadString(key, nameof(VmatGeneratorSettings.TextureRoot), bad) ?? defaults.TextureRoot,
            AssetsRoot = ReadString(key, nameof(VmatGeneratorSettings.AssetsRoot), bad) ?? defaults.AssetsRoot,
            ProjectRoot = ReadString(key, nameof(VmatGeneratorSettings.ProjectRoot), bad) ?? defaults.ProjectRoot,
            AutoAssignOnDrop = ReadFlag(key, nameof(VmatGeneratorSettings.AutoAssignOnDrop), bad) ?? defaults.AutoAssignOnDrop,
            RecurseTextureFolders = ReadFlag(key, nameof(VmatGeneratorSettings.RecurseTextureFolders), bad) ?? defaults.RecurseTextureFolders,
            AdoptDroppedVmatFolderAsMaterialsRoot =
                ReadFlag(key, nameof(VmatGeneratorSettings.AdoptDroppedVmatFolderAsMaterialsRoot), bad)
                ?? defaults.AdoptDroppedVmatFolderAsMaterialsRoot,
            Rules = ReadRules(key, bad),
        };

        settings = VmatGeneratorSettingsStore.Sanitize(settings, out var fixedBySanitize);
        bad.AddRange(fixedBySanitize);

        repaired = [.. bad.Distinct(StringComparer.Ordinal)];
        return settings;
    }

    /// <summary>读 REG_SZ；类型不符只记录字段名并回退。</summary>
    private static string? ReadString(RegistryKey key, string name, List<string> bad)
    {
        var raw = key.GetValue(name);
        if (raw is null) return null;
        if (raw is string s) return s;

        bad.Add(name);
        return null;
    }

    /// <summary>读 REG_DWORD 表示的布尔；类型不符只记录字段名并回退。</summary>
    private static bool? ReadFlag(RegistryKey key, string name, List<string> bad)
    {
        var raw = key.GetValue(name);
        if (raw is null) return null;
        if (raw is int i && i is 0 or 1) return i == 1;

        bad.Add(name);
        return null;
    }

    /// <summary>
    /// 读规则表：按子键名字典序还原成有序列表。
    /// </summary>
    /// <remarks>
    /// 字典序即数组顺序——写入时用四位零填充保证二者一致。手工加入的越界或重名子键
    /// 在这里被跳过，而不是让整份配置读不出来。
    /// </remarks>
    private static List<TextureSuffixRule> ReadRules(RegistryKey key, List<string> bad)
    {
        var rules = new List<TextureSuffixRule>();

        string[] names;
        try
        {
            using var rulesKey = key.OpenSubKey(RulesKeyName, writable: false);
            if (rulesKey is null) return rules;
            names = rulesKey.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            bad.Add(RulesKeyName);
            return rules;
        }

        Array.Sort(names, StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            try
            {
                using var ruleKey = key.OpenSubKey($@"{RulesKeyName}\{name}", writable: false);
                if (ruleKey is null) continue;

                var suffix = ruleKey.GetValue(RuleSuffixValue) as string;
                if (string.IsNullOrWhiteSpace(suffix))
                {
                    // 没有后缀的子键不是规则（多半是手工误建的），跳过而不是丢弃整表。
                    bad.Add($"{RulesKeyName}\\{name}");
                    continue;
                }

                var role = TextureRole.Unknown;
                if (ruleKey.GetValue(RuleRoleValue) is int roleInt && Enum.IsDefined(typeof(TextureRole), roleInt))
                {
                    role = (TextureRole)roleInt;
                }
                else
                {
                    // 角色值越界或类型不对：该条规则保留下来但标记为待修复，
                    // 不让一条坏规则带走整张表。
                    bad.Add($"{RulesKeyName}\\{name}\\{RuleRoleValue}");
                }

                var enabled = ruleKey.GetValue(RuleEnabledValue) is not int flag || flag is not 0 and not 1 || flag == 1;
                rules.Add(new TextureSuffixRule(suffix, role, enabled));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                bad.Add($"{RulesKeyName}\\{name}");
            }
        }

        return rules;
    }

    /// <summary>
    /// 写入规则表：整棵 <c>Rules</c> 子键先删后建。
    /// </summary>
    /// <remarks>
    /// 逐条增量更新留下的旧子键正是「用户删了规则却还生效」这类幽灵配置的来源；
    /// 整棵重建让「保存什么就是什么」，代价只是几十个子键的重建，可以忽略。
    /// </remarks>
    private static void WriteRules(RegistryKey key, List<TextureSuffixRule> rules)
    {
        if (key.GetSubKeyNames().Any(n => string.Equals(n, RulesKeyName, StringComparison.OrdinalIgnoreCase)))
            key.DeleteSubKeyTree(RulesKeyName, throwOnMissingSubKey: false);

        if (rules.Count == 0) return;

        using var rulesKey = key.CreateSubKey(RulesKeyName)
            ?? throw new IOException($"无法创建规则子键 {RulesKeyName}。");

        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (rule is null) continue;

            using var ruleKey = rulesKey.CreateSubKey($"{i:D4}")
                ?? throw new IOException($"无法创建规则子键 {i:D4}。");
            ruleKey.SetValue(RuleSuffixValue, rule.Suffix ?? string.Empty, RegistryValueKind.String);
            ruleKey.SetValue(RuleRoleValue, (int)rule.Role, RegistryValueKind.DWord);
            ruleKey.SetValue(RuleEnabledValue, rule.Enabled ? 1 : 0, RegistryValueKind.DWord);
        }
    }

    /// <summary>
    /// 采集写入现场：身份、完整性级别、配置根的读写可达性。
    /// </summary>
    /// <remarks>只做只读探测，不修改任何注册表或文件权限。</remarks>
    private static string DescribeEnvironment()
    {
        var lines = new List<string>();

        // 用户 SID 与配置根路径是最关键的两行：同一个用户名可以对应不同的登录会话，
        // 而 HKCU / %LOCALAPPDATA% 都是按会话而非按名字解析的。
        try
        {
            var identity = WindowsIdentity.GetCurrent();
            lines.Add($"账户名          : {identity.Name}");
            lines.Add($"用户 SID        : {identity.User?.Value}");

            var principal = new WindowsPrincipal(identity);
            lines.Add($"管理员令牌      : {principal.IsInRole(WindowsBuiltInRole.Administrator)}");
        }
        catch (Exception ex)
        {
            lines.Add($"账户名          : 探测失败（{ex.Message}）");
        }

        lines.Add($"本地应用数据目录: {Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}");
        lines.Add($"进程           : {Environment.ProcessId} {Environment.ProcessPath}");
        lines.Add($"配置根         : {Registry.CurrentUser.Name}");

        try
        {
            // 只打开、不写入：能以可写方式打开即说明 ACL 层面没问题。
            using var probe = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            lines.Add(probe is null
                ? $"配置项探测     : {KeyPath} 尚未存在（首次保存时属正常）"
                : $"配置项探测     : {KeyPath} 可写打开成功，值 {probe.GetValueNames().Length} 个");
        }
        catch (Exception ex)
        {
            lines.Add($"配置项探测     : 打开失败（{ex.GetType().Name}: {ex.Message}）");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>把底层异常翻译成用户看得懂、又不泄露存储位置的提示。</summary>
    private static string DescribeSaveFailure(Exception ex) => ex switch
    {
        UnauthorizedAccessException or SecurityException
            => "没有写入配置的权限，本次设置未保存。"
               + "如果程序曾以管理员身份运行过，请改用普通身份重新启动后再试。",
        _ => $"保存配置失败：{ex.Message}",
    };

    /// <summary>
    /// 把加载结果翻译成面向用户的中文说明。
    /// </summary>
    /// <remarks>
    /// <para>刻意<b>不出现存储位置</b>：配置存在哪里是实现细节，用户点「保存设置」只关心
    /// 存没存上。真要排查，日志里有完整的键路径。</para>
    /// </remarks>
    public static string DescribeLoad(SettingsLoadReport report) => report.Outcome switch
    {
        SettingsLoadOutcome.Repaired
            => $"已修复 {report.RepairedFields.Count} 个非法设置字段（{string.Join("、", report.RepairedFields)}）。",
        SettingsLoadOutcome.RecoveredFromCorruption
            => "配置已损坏，已备份原内容并恢复默认设置。",
        SettingsLoadOutcome.Defaulted
            => "未找到已保存的配置，已使用默认设置。",
        _ => report.LoadedFromDisk
                        ? "设置已加载。"
                        : "已从旧版配置文件迁移设置。",
    };

    // ── 旧版迁移 ───────────────────────────────────────────────────────────

    /// <summary>读取旧版「整份 JSON 单值」，写回原生结构后删除该值。</summary>
    private static VmatGeneratorSettings? MigrateLegacyBlob(out SettingsLoadReport report)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (key?.GetValue(LegacyBlobValueName) is not string json || string.IsNullOrWhiteSpace(json))
        {
            report = new SettingsLoadReport(
                SettingsLoadOutcome.Defaulted, loadedFromDisk: false, errorMessage: null,
                backupFilePath: null, repairedFields: []);
            return VmatGeneratorSettings.CreateDefault();
        }

        var parsed = VmatGeneratorSettingsStore.ParseJson(json, out var repaired, out var error);
        if (parsed is null)
        {
            report = new SettingsLoadReport(
                SettingsLoadOutcome.RecoveredFromCorruption,
                loadedFromDisk: true,
                errorMessage: error,
                backupFilePath: BackUpValue(json),
                repairedFields: []);
            return VmatGeneratorSettings.CreateDefault();
        }

        // 立刻按原生结构落盘并删掉旧值，让迁移只发生一次。
        Save(parsed);

        report = new SettingsLoadReport(
            SettingsLoadOutcome.Loaded, loadedFromDisk: true, errorMessage: null,
            backupFilePath: null, repairedFields: repaired);
        return parsed;
    }

    /// <summary>注册表为空时，尝试从更早的 JSON 文件迁移；没有旧文件就用默认配置。</summary>
    private static VmatGeneratorSettings? LoadLegacyOrDefault(out SettingsLoadReport report)
    {
        if (!File.Exists(VmatGeneratorSettings.SettingsFilePath))
        {
            report = new SettingsLoadReport(
                SettingsLoadOutcome.Defaulted, loadedFromDisk: false, errorMessage: null,
                backupFilePath: null, repairedFields: []);
            return VmatGeneratorSettings.CreateDefault();
        }

        var migrated = VmatGeneratorSettingsStore.Load(out var legacyReport)
            ?? VmatGeneratorSettings.CreateDefault();

        var saved = Save(migrated);
        if (!saved.Success)
        {
            // 迁移没写进去就别改名，否则旧配置会被留在原地却不再被使用。
            ErrorLog.Warn("迁移旧配置到注册表", VmatGeneratorSettings.SettingsFilePath,
                new IOException(saved.ErrorMessage));
            report = legacyReport;
            return migrated;
        }

        report = new SettingsLoadReport(
            SettingsLoadOutcome.Loaded, loadedFromDisk: true, errorMessage: null,
            backupFilePath: ArchiveLegacyFile(), repairedFields: legacyReport.RepairedFields);
        return migrated;
    }

    /// <summary>把旧 JSON 改名留档，让「已迁移」这件事幂等。</summary>
    private static string? ArchiveLegacyFile()
    {
        var source = VmatGeneratorSettings.SettingsFilePath;
        var archive = Path.Combine(
            Path.GetDirectoryName(source) ?? string.Empty,
            "settings.migrated.json");

        try
        {
            File.Move(source, archive, overwrite: true);
            return archive;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 留档失败不影响迁移结果——配置已经安全进了注册表。
            ErrorLog.Warn("留档旧配置文件", source, ex);
            return null;
        }
    }

    /// <summary>把损坏的旧版单值改名留档，返回新值名；失败时返回 <c>null</c>。</summary>
    private static string? BackUpValue(string broken)
    {
        var name = $"{LegacyBlobValueName}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            if (key is null) return null;
            key.SetValue(name, broken, RegistryValueKind.String);
            return name;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            ErrorLog.Warn("留档损坏的注册表配置", KeyPath, ex);
            return null;
        }
    }
}
