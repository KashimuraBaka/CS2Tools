// ============================================================================
// SDK.Tier0.Kv3JsonModel — 把原生 SaveKV3AsJSON 产出的 JSON 桥接成 Kv3Value 树。
//
// 存在理由：t21 打通了原生链，产出的是 **JSON 文本**（引擎自带的序列化格式）；
// 而 UI 逻辑消费的是 Kv3Value 树（t16 起的既有契约）。
// 用 System.Text.Json（BCL，不引三方库）做 JSON -> Kv3Value 的适配，
// 这样「解析由 tier0 原生完成、模型桥接由 BCL 完成」，既满足用户「用 tier0 读取」
// 的诉求，又不必改 UI/Models 逻辑。
// ============================================================================
using System.Text.Json;
using System.IO;

namespace SDK.Tier0;

public static class Kv3JsonModel
{
    /// <summary>把 SaveKV3AsJSON 的 JSON 文本解析成 Kv3Value 树（根为 Table）。</summary>
    public static Dictionary<string, Kv3Value> ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("KV3 JSON root is not an object (ValueKind=" + root.ValueKind + ")");
        return ToTable(root);
    }

    private static Dictionary<string, Kv3Value> ToTable(JsonElement obj)
    {
        var map = new Dictionary<string, Kv3Value>(StringComparer.Ordinal);
        foreach (var p in obj.EnumerateObject())
            map[p.Name] = FromElement(p.Value);
        return map;
    }

    private static Kv3Value FromElement(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                return new Kv3Value(Kv3Kind.Table, "", []) { Table = ToTable(e) };
            case JsonValueKind.Array:
                var items = new List<Kv3Value>();
                foreach (var it in e.EnumerateArray()) items.Add(FromElement(it));
                return new Kv3Value(Kv3Kind.Array, "", items);
            case JsonValueKind.String:
                return new Kv3Value(Kv3Kind.String, e.GetString() ?? "", []);
            case JsonValueKind.Number:
                return new Kv3Value(Kv3Kind.Float, e.GetRawText(), []);
            case JsonValueKind.True:
                return new Kv3Value(Kv3Kind.Boolean, "true", []);
            case JsonValueKind.False:
                return new Kv3Value(Kv3Kind.Boolean, "false", []);
            default:
                return new Kv3Value(Kv3Kind.Null, "null", []);
        }
    }
}
