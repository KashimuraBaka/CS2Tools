// ============================================================================
// 数据模型 —— AssetCompiler（t18：KV3 值模型已沉入 SDK.Tier0，此处只留 UI 行模型）
// ============================================================================
namespace AssetCompiler.Models;

/// <summary>assettypes 的一个主类型条目（对应 SDK.Data.AssetTypesIndex 的一行）。</summary>
/// <remarks>行是不可变的（init-only）；筛选时重建 ItemsSource，故不需要 INotifyPropertyChanged。</remarks>
public sealed class AssetTypeRow
{
    public string Name { get; init; } = "";
    public string Class { get; init; } = "";
    public string Ext { get; init; } = "";
    public string FriendlyName { get; init; } = "";
    public string CompilerIdentifier { get; init; } = "";
    public string BlockIds { get; init; } = "";
    public string Encodings { get; init; } = "";
    public bool HasCompiler { get; init; }

    /// <summary>m_HideForRetailMods 元素个数（0 = 字段不存在或空数组）。</summary>
    public int HideForRetailMods { get; init; }

    /// <summary>m_Blocks 元素个数。</summary>
    public int BlockCount { get; init; }

    /// <summary>该类型在 KV3 里的字段总数（用于详情展示）。</summary>
    public int FieldCount { get; init; }

    public string CompilerDisplay => HasCompiler ? CompilerIdentifier : "(none)";
}

/// <summary>subassettypes 的一个条目（独立分区，无 _class / 编译器）。</summary>
public sealed class SubAssetTypeRow
{
    public string Name { get; init; } = "";
    public string Flags { get; init; } = "";
}

/// <summary>编译注册表的一条（从 resourcecompiler.dll 内存实时读出）。</summary>
public sealed class RegistryEntryRow
{
    public int Index { get; init; }
    public string Identifier { get; init; } = "";
    public IntPtr Factory { get; init; }
    public IntPtr CachedInstance { get; init; }
    public IntPtr EntryAddress { get; init; }
    public long FactoryRva { get; init; }
    public long SingletonRva { get; init; }

    public string FactoryRvaText => "0x" + FactoryRva.ToString("X");
    public string SingletonRvaText => SingletonRva > 0 ? "0x" + SingletonRva.ToString("X") : "(not instantiated)";
    public string FactoryVaText => "0x" + Factory.ToInt64().ToString("X");
}

/// <summary>编译器解析结果（UI 口径：条目级 28/29）。</summary>
public sealed class ResolveResult
{
    public bool Found { get; set; }
    public string Identifier { get; set; } = "";
    public string Message { get; set; } = "";
    public RegistryEntryRow? Entry { get; set; }
}

/// <summary>单个源文件的编译结果。</summary>
public sealed class CompileItemResult
{
    public string Source { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string Tier { get; set; } = "-";
    public string Detail { get; set; } = "";
    public string OutputPath { get; set; } = "-";
    public string OutputSize { get; set; } = "-";
    public string OutputMtime { get; set; } = "-";
    public long ElapsedMs { get; set; }
}
