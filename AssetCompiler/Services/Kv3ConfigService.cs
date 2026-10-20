
// ============================================================================
// Kv3ConfigService —— 加载 assettypes_common.txt
//
// 演进：
//   t11  ValveKeyValue（NuGet 单数包 0.72.0.534）——已移除；
//   t16  tier0.dll 原生 KV3 路径（探针 + 自包含读取器，两者均已于 t24/t28e 移除）；
//   t18  **DLL 调用层已抽到独立 SDK 工程 projects\SDK\**：
//         本类现在只是 UI 与 SDK 之间的**薄封装**——真正的 P/Invoke、arena/Root+120
//         布局、CUtlString/CUtlBuffer 结构、错误串回显都在
//         SDK.Tier0.Kv3NativeArena / SDK.Tier0.Kv3Value 里。
//         UI 层不直接出现 P/Invoke。
//
// 对外接口：Load（游戏目录 → assettypes_common.txt → tier0 原生链解析）。
// ============================================================================
using AssetCompiler.Models;
using SDK.Tier0;
using System.Text.RegularExpressions;

namespace AssetCompiler.Services;

public sealed class ConfigLoadResult
{
    public List<AssetTypeRow> Types { get; init; } = [];
    public List<SubAssetTypeRow> SubTypes { get; init; } = [];
    public string EncodingName { get; init; } = "";
    public string EncodingId { get; init; } = "";
    public string FormatName { get; init; } = "";
    public string FormatId { get; init; } = "";
    public long FileBytes { get; init; }
    public int FileLines { get; init; }
    public int WarningsCount { get; init; }
    public string WarningTitle { get; init; } = "";

    /// <summary>tier0 原生探针结果（t16）：真实 DLL 调用的判定/错误串。</summary>
}

public static partial class Kv3ConfigService
{
    public const string DefaultConfigRelative = @"bin\assettypes_common.txt";

    /// <summary>tier0 原生链的运行说明（供日志面板显示用了哪条路径）。</summary>
    public static string NativeNote { get; set; } = "";

    /// <summary>加载并完整遍历 KV3 配置。</summary>
    public static ConfigLoadResult Load(string configPath, string? tier0Path = null)
    {
        if (!File.Exists(configPath))
            throw new FileNotFoundException("配置文件不存在: " + configPath);

        var text = File.ReadAllText(configPath);

        // ---- 头行（encoding / format GUID）—— 全部用命名组，避免编号错位 ----
        string encName = "", encId = "", fmtName = "", fmtId = "";
        var firstLine = text.Split('\n')[0];
        var hdr = HeaderRegex().Match(firstLine);
        if (hdr.Success)
        {
            encName = hdr.Groups["enc"].Value;
            encId = hdr.Groups["encId"].Value;
            fmtName = hdr.Groups["fmt"].Value;
            fmtId = hdr.Groups["fmtId"].Value;
        }

        // ---- t21/t24：KV3 解析**完全由 tier0 原生完成**（LoadKV3(arena) -> SaveKV3AsJSON
        //     -> SDK 的 BCL JSON 桥接）；t24 起手动读取器已移除，本方法是唯一路径——
        //     原生链任何一步失败都直接抛错（fail-fast），不再有静默回退。
        // 配置文件在 <game>\bin\assettypes_common.txt，DLL 在 <game>\bin\win64\
        // Signatures.ResourceCompilerDll 已含 "bin\win64\..."，故基准目录就是 <game>
        var gameBinDir = Path.GetDirectoryName(Path.GetDirectoryName(configPath)) ?? ".";
        // t22：地址解析集中到 SDK.Native.Functions.Initialize（签名 + 导出，fail-fast）
        if (!SDK.Native.Functions.IsInitialized)
            SDK.Native.Functions.Initialize(gameBinDir);

        var nat = Kv3NativeArena.Load(gameBinDir, text);
        if (!nat.Ok || string.IsNullOrEmpty(nat.Json))
            throw new InvalidDataException($"tier0 原生 KV3 解析失败（游戏目录: {gameBinDir}）: {nat.Detail}");

        var root = Kv3JsonModel.ParseJson(nat.Json);
        NativeNote = $"tier0 原生链 ({nat.Detail})";
        if (root.Count == 0)
            throw new InvalidDataException($"KV3 解析结果为空（文件: {configPath}）");

        // ---- asset_warnings（计数 + 标题）----
        int warnCount = 0; string warnTitle = "";
        if (root.TryGetValue("asset_warnings", out var warns) && warns.IsTable)
        {
            warnCount = warns.Count;
            foreach (var (_, w) in warns.Table!)
            {
                if (w.Table is not null && w.Table.TryGetValue("m_Title", out var t)) warnTitle = t.Text;
            }
        }

        // ---- assettypes（主类型表）----
        if (!root.TryGetValue("assettypes", out var at) || !at.IsTable)
            throw new InvalidDataException("缺少 assettypes 集合");

        var types = new List<AssetTypeRow>();
        foreach (var (key, entry) in at.Table!)
        {
            string cls = "", ext = "", friendly = "", comp = "", blockIds = "", encs = "";
            int hide = 0, blocks = 0, fields = 0;
            bool hasComp = false;

            if (entry.Table is not null)
            {
                foreach (var (fk, fv) in entry.Table)
                {
                    fields++;
                    switch (fk)
                    {
                        case "_class": cls = fv.Text; break;
                        case "m_Ext": ext = fv.Text; break;
                        case "m_FriendlyName": friendly = fv.Text; break;
                        case "m_CompilerIdentifier":
                            comp = fv.Text;
                            hasComp = !string.IsNullOrEmpty(comp);
                            break;
                        case "m_Blocks":
                            blocks = fv.Count;
                            var ids = new List<string>(); var es = new List<string>();
                            foreach (var blk in fv.Items)
                            {
                                if (blk.Table is not null && blk.Table.TryGetValue("m_BlockID", out var bid)) ids.Add(bid.Text);
                                if (blk.Table is not null && blk.Table.TryGetValue("m_Encoding", out var ben)) es.Add(ben.Text);
                            }
                            blockIds = string.Join("|", ids);
                            encs = string.Join("|", es);
                            break;
                        case "m_HideForRetailMods":
                            hide = fv.Count;
                            break;
                    }
                }
            }

            types.Add(new AssetTypeRow
            {
                Name = key,
                Class = cls,
                Ext = ext,
                FriendlyName = friendly,
                CompilerIdentifier = comp,
                HasCompiler = hasComp,
                BlockIds = blockIds,
                Encodings = encs,
                BlockCount = blocks,
                HideForRetailMods = hide,
                FieldCount = fields
            });
        }

        // ---- subassettypes（独立分区）----
        var subs = new List<SubAssetTypeRow>();
        if (root.TryGetValue("subassettypes", out var sat) && sat.IsTable)
        {
            foreach (var (sk, sv) in sat.Table!)
            {
                var flags = new List<string>();
                if (sv.Table is not null)
                    foreach (var (ik, iv) in sv.Table) flags.Add(ik + "=" + iv.Text);
                subs.Add(new SubAssetTypeRow { Name = sk, Flags = string.Join(", ", flags) });
            }
        }

        return new ConfigLoadResult
        {
            Types = types,
            SubTypes = subs,
            EncodingName = encName,
            EncodingId = encId,
            FormatName = fmtName,
            FormatId = fmtId,
            FileBytes = new FileInfo(configPath).Length,
            FileLines = File.ReadAllLines(configPath).Length,
            WarningsCount = warnCount,
            WarningTitle = warnTitle,
        };
    }

    [GeneratedRegex(@"encoding:(?<enc>[a-z0-9]+):version\{(?<encId>[0-9a-fA-F\-]+)\}\s*format:(?<fmt>[a-z0-9]+):version\{(?<fmtId>[0-9a-fA-F\-]+)\}")]
    private static partial Regex HeaderRegex();
}