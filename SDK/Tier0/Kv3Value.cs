
// ============================================================================
// SDK.Tier0 — KV3 值模型（t16 自 AssetCompiler\Models.cs 抽出，t18 沉入 SDK）
// ============================================================================
namespace SDK.Tier0;

/// <summary>KV3 值形态（对应 tier0 的 KVValueType 子集）。</summary>
public enum Kv3Kind
{
    Null,
    Table,
    Array,
    String,
    Int,
    Float,
    Boolean
}

/// <summary>
/// KV3 节点。Table 走 <see cref="Table"/>（字典，重复键不保留——与 tier0/ValveKeyValue 一致）；
/// Array 走 <see cref="Items"/>；标量走 <see cref="Text"/>。
/// </summary>
public sealed class Kv3Value(Kv3Kind kind, string text, List<Kv3Value> items)
{
    public Kv3Kind Kind { get; init; } = kind;
    public string Text { get; init; } = text;
    public List<Kv3Value> Items { get; init; } = items;
    public Dictionary<string, Kv3Value>? Table { get; init; }

    /// <summary>KV3 flag 前缀（resource: / panorama: / …），无则为空。</summary>
    public string Flag { get; init; } = "";

    /// <summary>子节点数：表取键数、数组取元素数、标量取 0。</summary>
    public int Count => Kind == Kv3Kind.Table ? (Table?.Count ?? 0) : (Kind == Kv3Kind.Array ? Items.Count : 0);

    public bool IsTable => Kind == Kv3Kind.Table;
    public bool IsArray => Kind == Kv3Kind.Array;

    public override string ToString() => Kind == Kv3Kind.Table ? $"{{{Count}}}" : (Kind == Kv3Kind.Array ? $"[{Count}]" : Text);
}
