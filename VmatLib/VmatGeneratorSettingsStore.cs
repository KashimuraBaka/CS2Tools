using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace VmatLib;

/// <summary>配置加载结果（规格 §3.4）。</summary>
public enum SettingsLoadOutcome
{
    /// <summary>配置文件不存在，返回默认配置；<b>不</b>写盘，等用户首次保存时再落盘。</summary>
    Defaulted,

    /// <summary>加载成功且全部字段合法，原样返回。</summary>
    Loaded,

    /// <summary>部分字段非法；只回退非法字段，合法字段全部保留。</summary>
    Repaired,

    /// <summary>文件损坏（空文件 / 非法 JSON / 读失败）；已备份后返回默认配置。</summary>
    RecoveredFromCorruption,
}

/// <summary>一次配置加载的诊断信息（规格 §3.4）。</summary>
/// <remarks>构造加载报告。</remarks>
/// <param name="outcome">加载结果分类。</param>
/// <param name="loadedFromDisk">是否真的读到了磁盘内容。</param>
/// <param name="errorMessage">错误摘要；成功时为 <c>null</c>。</param>
/// <param name="backupFilePath">损坏文件的备份路径；未备份时为 <c>null</c>。</param>
/// <param name="repairedFields">被修复的字段名列表。</param>
public sealed class SettingsLoadReport(
    SettingsLoadOutcome outcome,
    bool loadedFromDisk,
    string? errorMessage,
    string? backupFilePath,
    IReadOnlyList<string> repairedFields)
{

    /// <summary>加载结果分类。</summary>
    public SettingsLoadOutcome Outcome { get; } = outcome;

    /// <summary>是否真的读到了磁盘内容。</summary>
    public bool LoadedFromDisk { get; } = loadedFromDisk;

    /// <summary>错误摘要；成功时为 <c>null</c>。</summary>
    public string? ErrorMessage { get; } = errorMessage;

    /// <summary>损坏文件的备份路径；未备份时为 <c>null</c>。</summary>
    public string? BackupFilePath { get; } = backupFilePath;

    /// <summary>被修复的字段名（形如 <c>schemaVersion</c>、<c>rules[3].suffix(重复)</c>）。</summary>
    public IReadOnlyList<string> RepairedFields { get; } = repairedFields;

    /// <summary>可直接展示给用户的状态栏文案。</summary>
    /// <returns>中文说明；无异常时返回描述结果的短语。</returns>
    public string ToStatusMessage() => Outcome switch
    {
        SettingsLoadOutcome.Repaired
            => $"已修复 {RepairedFields.Count} 个非法设置字段（{string.Join("、", RepairedFields)}）。",
        SettingsLoadOutcome.RecoveredFromCorruption
            => $"设置文件损坏，已备份为 {BackupFilePath} 并恢复默认设置。",
        SettingsLoadOutcome.Defaulted => "未找到设置文件，已使用默认设置。",
        _ => "设置已加载。",
    };

    /// <summary>调试用摘要。</summary>
    /// <returns>人可读的一行描述。</returns>
    public override string ToString() => $"{Outcome}（修复 {RepairedFields.Count} 项{(ErrorMessage is null ? "" : "：" + ErrorMessage)}）";
}

/// <summary>一次配置保存的诊断信息。</summary>
/// <remarks>构造保存报告。</remarks>
/// <param name="success">是否写入成功。</param>
/// <param name="filePath">目标文件路径。</param>
/// <param name="errorMessage">错误摘要；成功时为 <c>null</c>。</param>
public sealed class SettingsSaveReport(bool success, string filePath, string? errorMessage)
{

    /// <summary>是否写入成功。</summary>
    public bool Success { get; } = success;

    /// <summary>目标文件路径。</summary>
    public string FilePath { get; } = filePath;

    /// <summary>错误摘要；成功时为 <c>null</c>。</summary>
    public string? ErrorMessage { get; } = errorMessage;

    /// <summary>调试用摘要。</summary>
    /// <returns>人可读的一行描述。</returns>
    public override string ToString() => Success ? $"已保存到 {FilePath}" : $"保存失败：{ErrorMessage}";
}

/// <summary>
/// 配置持久化（规格 §3.1 / §3.4 / §3.5）。
///
/// <para><b>三条硬约束。</b></para>
/// <list type="number">
///   <item><description><b>永不抛异常。</b>任何读、写、解析、备份过程中的异常都被就地吞掉并
///   转成 <see cref="SettingsLoadReport.ErrorMessage"/> / <see cref="SettingsSaveReport"/>，
///   绝不冒泡到 UI 线程。</description></item>
///   <item><description><b>逐字段修复。</b>非法字段只回退该项，其余合法字段全部保留，
///   修复项名记入 <see cref="SettingsLoadReport.RepairedFields"/>。</description></item>
///   <item><description><b>损坏即备份。</b>读到无法解析的内容时，先把原始字节另存为
///   <c>settings.corrupt-&lt;时间戳&gt;.json</c>，再返回默认配置；备份失败也不阻断。</description></item>
/// </list>
///
/// <para><b>为什么不用强类型反序列化。</b><c>System.Text.Json</c> 遇到
/// <c>"autoAssignOnDrop": "yes"</c> 这类类型错误会抛 <see cref="JsonException"/>
/// 并毁掉整份配置，只能落到「损坏」分支；而 §3.5 要求的是「只回退非法字段」。
/// 因此这里用 <see cref="JsonNode"/> 做宽松读取 + 逐字段校验，让类型错误降级为字段级修复。</para>
///
/// <para>写盘为原子替换：先写 <c>settings.json.tmp</c>（UTF-8 无 BOM、缩进、行尾 <c>\n</c>），
/// 再 <c>File.Move(tmp, path, overwrite: true)</c>，避免写到一半掉电留下半截 JSON。</para>
/// </summary>
public static class VmatGeneratorSettingsStore
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
        NewLine = "\n",
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonDocumentOptions ReadDocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// 从 <see cref="VmatGeneratorSettings.SettingsFilePath"/> 读取配置。
    /// </summary>
    /// <param name="report">加载诊断信息，任何情况下都被赋值。</param>
    /// <returns>
    /// 正常路径下返回配置实例（文件不存在 → 默认配置）。仅当发生无法归类的极端异常时返回
    /// <c>null</c>，此时 <see cref="SettingsLoadReport.ErrorMessage"/> 非空，调用方应改用
    /// <see cref="LoadOrDefault"/>。<b>本方法不会抛异常。</b>
    /// </returns>
    public static VmatGeneratorSettings? Load(out SettingsLoadReport report) => LoadFromFile(VmatGeneratorSettings.SettingsFilePath, out report);

    /// <summary>读取配置，失败时静默回退默认配置，绝不返回 <c>null</c>，绝不抛异常。</summary>
    /// <returns>一份可用的配置实例。</returns>
    public static VmatGeneratorSettings LoadOrDefault() => Load(out _) ?? VmatGeneratorSettings.CreateDefault();

    /// <summary>把配置序列化为 JSON 文本。<b>不碰任何存储介质。</b></summary>
    /// <remarks>
    /// 抽出这一步，是为了让「存到哪里」和「怎么编码」解耦：JSON 文件存储与注册表存储
    /// 共用同一套编码，因此换存储位置不会让配置内容悄悄变样。
    /// </remarks>
    public static string ToJson(VmatGeneratorSettings settings) => JsonSerializer.Serialize(settings, WriteOptions);

    /// <summary>宽松解析 JSON 文本为配置。<b>不碰任何存储介质。</b></summary>
    /// <param name="text">JSON 文本。</param>
    /// <param name="repaired">被字段级修复的字段名；解析失败时为空。</param>
    /// <param name="error">解析失败的原因；成功时为 <c>null</c>。</param>
    /// <returns>
    /// 解析并逐字段校验后的配置；文本为空 / 不是合法 JSON / 根节点不是对象时返回 <c>null</c>。
    /// 单个字段非法不算失败——该字段回退默认值，其余字段照常保留，名字记入
    /// <paramref name="repaired"/>。<b>本方法不会抛异常。</b>
    /// </returns>
    public static VmatGeneratorSettings? ParseJson(
        string text,
        out IReadOnlyList<string> repaired,
        out string? error)
    {
        repaired = [];
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "设置内容为空。";
            return null;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text, nodeOptions: null, documentOptions: ReadDocumentOptions);
        }
        catch (JsonException ex)
        {
            error = $"设置不是合法 JSON：{ex.Message}";
            return null;
        }

        if (root is not JsonObject obj)
        {
            error = "设置的根节点不是 JSON 对象。";
            return null;
        }

        var settings = FromJson(obj, out var repairedList);
        repaired = repairedList;
        return settings;
    }

    /// <summary>
    /// 逐字段校验并就地修复一份配置（§3.5）。
    /// </summary>
    /// <param name="raw">待校验配置；为 <c>null</c> 时按默认配置处理。</param>
    /// <param name="repairedFields">被修复的字段名列表。</param>
    /// <returns>修复后的配置（与 <paramref name="raw"/> 为同一实例，便于 GUI 直接沿用）。</returns>
    public static VmatGeneratorSettings Sanitize(VmatGeneratorSettings raw, out IReadOnlyList<string> repairedFields)
    {
        var repaired = new List<string>();
        var settings = raw ?? new VmatGeneratorSettings();

        if (settings.SchemaVersion != VmatGeneratorSettings.CurrentSchemaVersion)
        {
            settings.SchemaVersion = VmatGeneratorSettings.CurrentSchemaVersion;
            repaired.Add("schemaVersion");
        }

        if (string.IsNullOrWhiteSpace(settings.DefaultShaderName) ||
            ShaderCatalog.Find(settings.DefaultShaderName.Trim()) is null)
        {
            settings.DefaultShaderName = VmatGeneratorSettings.DefaultShaderNameValue;
            repaired.Add("defaultShaderName");
        }
        else
        {
            settings.DefaultShaderName = settings.DefaultShaderName.Trim();
        }

        settings.TextureRoot ??= string.Empty;
        settings.AssetsRoot ??= string.Empty;
        settings.ProjectRoot ??= string.Empty;

        settings.Rules = RepairRules(settings.Rules, repaired);

        repairedFields = repaired;
        return settings;
    }

    /// <summary>
    /// 把配置中的规则表整体重置为 §4 的 35 条种子规则（GUI「恢复默认规则」按钮）。
    /// 其余字段原样保留。
    /// </summary>
    /// <param name="settings">待重置的配置；为 <c>null</c> 时忽略。</param>
    public static void RestoreDefaultRules(VmatGeneratorSettings settings)
    {
        if (settings is null) return;
        settings.Rules = [.. VmatGeneratorSettings.SeedRules()];
    }

    /// <summary>
    /// 把配置原子写入 <see cref="VmatGeneratorSettings.SettingsFilePath"/>。
    /// </summary>
    /// <param name="settings">待保存的配置；为 <c>null</c> 返回失败报告。</param>
    /// <returns>保存结果；<b>本方法不会抛异常</b>。</returns>
    public static SettingsSaveReport Save(VmatGeneratorSettings settings) => SaveToFile(settings, VmatGeneratorSettings.SettingsFilePath);

    // ── 内部实现（供自检与未来的测试入口复用，避免触碰用户真实配置）────────────

    internal static VmatGeneratorSettings? LoadFromFile(string path, out SettingsLoadReport report)
    {
        try
        {
            // ① 文件不存在 —— 返回默认配置，不写盘。
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                report = new SettingsLoadReport(SettingsLoadOutcome.Defaulted, false, null, null, []);
                return VmatGeneratorSettings.CreateDefault();
            }

            byte[] raw = [];
            string text;
            try
            {
                raw = File.ReadAllBytes(path);
                text = DecodeUtf8(raw);
            }
            catch (Exception ex) when (IsIoFailure(ex))
            {
                report = Corrupted(path, raw, $"读取设置文件失败：{ex.Message}");
                return VmatGeneratorSettings.CreateDefault();
            }

            // ② 零字节 / 空白字符
            if (string.IsNullOrWhiteSpace(text))
            {
                report = Corrupted(path, raw, "设置文件为空。");
                return VmatGeneratorSettings.CreateDefault();
            }

            JsonNode? root;
            try
            {
                root = JsonNode.Parse(text, nodeOptions: null, documentOptions: ReadDocumentOptions);
            }
            catch (JsonException ex)
            {
                report = Corrupted(path, raw, $"设置文件不是合法 JSON：{ex.Message}");
                return VmatGeneratorSettings.CreateDefault();
            }

            if (root is not JsonObject obj)
            {
                report = Corrupted(path, raw, "设置文件的根节点不是 JSON 对象。");
                return VmatGeneratorSettings.CreateDefault();
            }

            // ③ 字段非法 —— 只回退非法字段，保留其余合法字段。
            var settings = FromJson(obj, out var repaired);
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
            // 极端兜底：绝不冒泡。返回 null，调用方按契约改用 LoadOrDefault()。
            report = new SettingsLoadReport(SettingsLoadOutcome.Defaulted, false, $"加载设置时发生未预期错误：{ex.Message}", null, []);
            return null;
        }
    }

    internal static SettingsSaveReport SaveToFile(VmatGeneratorSettings settings, string path)
    {
        var target = path ?? string.Empty;
        if (settings is null)
        {
            return new SettingsSaveReport(false, target, "配置对象为 null。");
        }

        try
        {
            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(settings, WriteOptions);
            var temp = target + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, target, overwrite: true);
            return new SettingsSaveReport(true, target, null);
        }
        catch (Exception ex)
        {
            return new SettingsSaveReport(false, target, $"保存设置失败：{ex.Message}");
        }
    }

    /// <summary>把宽松读取的 JSON 对象转成配置，同时完成 §3.5 的字段级校验。</summary>
    private static VmatGeneratorSettings FromJson(JsonObject obj, out List<string> repaired)
    {
        var repairedList = new List<string>();
        var settings = new VmatGeneratorSettings();

        // schemaVersion —— 缺键视为首次运行（不记录修复），键在但值非法才记录。
        if (TryReadInt(obj["schemaVersion"], out var schema))
        {
            settings.SchemaVersion = schema;
        }
        if (settings.SchemaVersion != VmatGeneratorSettings.CurrentSchemaVersion)
        {
            settings.SchemaVersion = VmatGeneratorSettings.CurrentSchemaVersion;
            if (obj.ContainsKey("schemaVersion")) repairedList.Add("schemaVersion");
        }

        // defaultShaderName
        if (TryReadString(obj["defaultShaderName"], out var shaderName) && shaderName.Length > 0)
        {
            settings.DefaultShaderName = shaderName;
        }
        if (ShaderCatalog.Find(settings.DefaultShaderName) is null)
        {
            settings.DefaultShaderName = VmatGeneratorSettings.DefaultShaderNameValue;
            if (obj.ContainsKey("defaultShaderName")) repairedList.Add("defaultShaderName");
        }

        // textureRoot —— 只校验类型，不校验目录是否存在（网络盘掉线不得丢配置）。
        if (TryReadString(obj["textureRoot"], out var textureRoot))
        {
            settings.TextureRoot = textureRoot;
        }
        else if (obj.ContainsKey("textureRoot"))
        {
            settings.TextureRoot = string.Empty;
            repairedList.Add("textureRoot");
        }

        // assetsRoot / projectRoot —— 向导第一步的两个目录，同样只校验类型。
        if (TryReadString(obj["assetsRoot"], out var assetsRoot))
        {
            settings.AssetsRoot = assetsRoot;
        }
        else if (obj.ContainsKey("assetsRoot"))
        {
            settings.AssetsRoot = string.Empty;
            repairedList.Add("assetsRoot");
        }

        if (TryReadString(obj["projectRoot"], out var projectRoot))
        {
            settings.ProjectRoot = projectRoot;
        }
        else if (obj.ContainsKey("projectRoot"))
        {
            settings.ProjectRoot = string.Empty;
            repairedList.Add("projectRoot");
        }

        settings.AutoAssignOnDrop = ReadBool(obj, "autoAssignOnDrop", settings.AutoAssignOnDrop, repairedList);
        settings.RecurseTextureFolders = ReadBool(obj, "recurseTextureFolders", settings.RecurseTextureFolders, repairedList);
        settings.AdoptDroppedVmatFolderAsMaterialsRoot = ReadBool(obj, "adoptDroppedVmatFolderAsMaterialsRoot", settings.AdoptDroppedVmatFolderAsMaterialsRoot, repairedList);

        // rules —— 缺键 / null 视为首次运行（不记录修复）；显式 [] 保留为空，绝不回填。
        settings.Rules = obj["rules"] switch
        {
            null => [.. VmatGeneratorSettings.SeedRules()],
            JsonArray array => RepairRules(ReadRules(array, repairedList), repairedList),
            _ => RepairDefaultRules(repairedList),
        };

        repaired = repairedList;
        return settings;
    }

    private static List<TextureSuffixRule> RepairDefaultRules(List<string> repaired)
    {
        repaired.Add("rules");
        return [.. VmatGeneratorSettings.SeedRules()];
    }

    private static bool ReadBool(JsonObject obj, string name, bool fallback, List<string> repaired)
    {
        if (!obj.ContainsKey(name)) return fallback;
        if (obj[name] is JsonValue value && value.TryGetValue<bool>(out var parsed)) return parsed;
        repaired.Add(name);
        return fallback;
    }

    /// <summary>宽松读取 JSON 布尔，容忍 <c>"1"</c> / <c>"true"</c> 等手写写法。</summary>
    private static bool TryReadBool(JsonNode? node, out bool value)
    {
        value = false;
        if (node is not JsonValue jv) return false;
        if (jv.TryGetValue<bool>(out var b)) { value = b; return true; }
        if (jv.TryGetValue<string>(out var s))
        {
            if (bool.TryParse(s, out var parsedBool)) { value = parsedBool; return true; }
            if (s == "1") { value = true; return true; }
            if (s == "0") { value = false; return true; }
        }
        if (jv.TryGetValue<int>(out var i) && (i == 0 || i == 1)) { value = i == 1; return true; }
        return false;
    }

    private static bool TryReadInt(JsonNode? node, out int value)
    {
        value = 0;
        return node is JsonValue jv && jv.TryGetValue(out value);
    }

    private static bool TryReadString(JsonNode? node, out string value)
    {
        value = string.Empty;
        if (node is not JsonValue jv) return false;
        if (jv.TryGetValue<string>(out var s)) { value = s ?? string.Empty; return true; }
        return false;
    }

    private static List<TextureSuffixRule> ReadRules(JsonArray array, List<string> repaired)
    {
        var rules = new List<TextureSuffixRule>(array.Count);
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject obj)
            {
                repaired.Add($"rules[{i}]");
                continue;
            }

            TryReadString(obj["suffix"], out var suffix);

            // role 必须是枚举名（§3.5）。别名（AO / Rough 等）不是枚举名，会被判为非法。
            var roleParsable = TextureRoleTokens.TryParseRole(
                TryReadString(obj["role"], out var roleText) ? roleText : null,
                out var role);

            var enabled = true;
            if (obj.ContainsKey("enabled") && !TryReadBool(obj["enabled"], out enabled))
            {
                repaired.Add($"rules[{i}].enabled");
                enabled = true;
            }

            var rule = new TextureSuffixRule(suffix, role, enabled)
            {
                RoleWasUnparsable = !roleParsable,
            };
            rules.Add(rule);
        }
        return rules;
    }

    /// <summary>
    /// §3.5 的规则表校验：空后缀丢弃、角色非法置 <see cref="TextureRole.Unknown"/> 并停用、
    /// 归一化后缀重复只保留第一条。
    /// </summary>
    private static List<TextureSuffixRule> RepairRules(List<TextureSuffixRule>? raw, List<string> repaired)
    {
        var result = new List<TextureSuffixRule>();
        if (raw is null) return [.. VmatGeneratorSettings.SeedRules()];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < raw.Count; i++)
        {
            var rule = raw[i];
            if (rule is null)
            {
                repaired.Add($"rules[{i}]");
                continue;
            }

            var normalized = TextureSuffixMatcher.NormalizeName(rule.Suffix ?? string.Empty);
            if (normalized.Length == 0)
            {
                repaired.Add($"rules[{i}].suffix");
                continue;
            }
            if (!seen.Add(normalized))
            {
                repaired.Add($"rules[{i}].suffix(重复)");
                continue;
            }

            if (rule.RoleWasUnparsable)
            {
                rule.Role = TextureRole.Unknown;
                rule.RoleWasUnparsable = false;
                rule.Enabled = false;
                repaired.Add($"rules[{i}].role");
            }
            result.Add(rule);
        }
        return result;
    }

    private static SettingsLoadReport Corrupted(string path, byte[] raw, string message)
    {
        var backup = TryBackup(path, raw);
        return new SettingsLoadReport(SettingsLoadOutcome.RecoveredFromCorruption, false, message, backup, []);
    }

    /// <summary>把损坏文件另存为 <c>settings.corrupt-&lt;时间戳&gt;.json</c>；失败不阻断。</summary>
    private static string? TryBackup(string path, byte[] raw)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory)) return null;
            Directory.CreateDirectory(directory);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var backup = Path.Combine(directory, $"settings.corrupt-{stamp}.json");
            if (raw is { Length: > 0 }) File.WriteAllBytes(backup, raw);
            else File.Copy(path, backup, overwrite: true);
            return backup;
        }
        catch (Exception)
        {
            // 备份失败也不阻断加载——用户还要能继续用软件。
            return null;
        }
    }

    private static string DecodeUtf8(byte[] raw)
    {
        if (raw.Length == 0) return string.Empty;
        // 去掉可能存在的 UTF-8 BOM，否则首字段名会带上不可见字符而被当成未知字段。
        var offset = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF ? 3 : 0;
        return new UTF8Encoding(false, false).GetString(raw, offset, raw.Length - offset);
    }

    private static bool IsIoFailure(Exception ex) => ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException;
}
