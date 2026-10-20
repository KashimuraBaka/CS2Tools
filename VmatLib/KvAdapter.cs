// ============================================================================
// VmatLib.KvAdapter — VMAT 文档的 KV 读写（t24：改用本仓 SDK 的 tier0 原生链）
//
// 原实现用三方库 ValveKeyValue（KVObject/KVSerializer）。按本仓纪律
// 「KV 一律用引擎自己的函数」，改为完全走 tier0 原生导出：
//   读：LoadKV3FromKV3OrKV1（按内容自动判别 KV3/KV1）
//       → SaveKV3AsJSON → SDK.Kv3JsonModel 桥成 Kv3Value 树 → VmatNode（Kv1ToJson）
//   写：VmatNode → JSON（System.Text.Json，BCL）
//       → LoadKV3FromJSON → SaveKV3AsKV1Text（引擎规范 KV1 输出）
// 语义与原版一致：KV1 文档名即顶层键（"Layer0"）；KV3 匿名顶层提升唯一
// Layer0 子节点为根；数组扁平化为数字键子节点；注释按引擎行为丢弃。
// ============================================================================
using SDK.Tier0;
using System.Text;
using System.Text.Json;

namespace VmatLib;

/// <summary>
/// VMAT 文档（KeyValues1/3）与 <see cref="VmatNode"/> 树之间的双向适配，
/// 底层全部走 tier0 原生 KV 函数（经 SDK.Tier0.Kv3NativeArena）。
/// </summary>
public static class KvAdapter
{
    /// <summary>Source 2 .vmat 的顶层节点名。</summary>
    private const string Layer0Key = "Layer0";

    /// <summary>
    /// 解析 KV1 / KV3 文本为 <see cref="VmatNode"/> 树。
    /// 判定由引擎自己做（LoadKV3FromKV3OrKV1 按内容判别，无需手工判头）。
    /// </summary>
    public static VmatNode Parse(string text)
    {
        var nat = Kv3NativeArena.Kv1ToJson(text);
        if (!nat.Ok) throw new InvalidDataException("KV 解析失败: " + nat.Detail);
        return BuildRoot(Kv3JsonModel.ParseJson(nat.Json));
    }

    /// <summary>
    /// 把 <see cref="VmatNode"/> 树序列化为 KV1 文本（引擎 SaveKV3AsKV1Text 的规范布局）。
    /// </summary>
    public static string Serialize(VmatNode root)
    {
        var nat = Kv3NativeArena.Kv1FromJson(ToJson(root));
        if (!nat.Ok) throw new InvalidOperationException("KV 写出失败: " + nat.Detail);
        return nat.Json;
    }

    /// <summary>完整往返：解析 → 写出 → 再解析，比较两棵树结构是否一致。</summary>
    public static bool RoundTripEquals(string kvText)
    {
        var first = Parse(kvText);
        var re = Serialize(first);
        var second = Parse(re);
        return StructuralEquals(first, second);
    }

    /// <summary>逐字段比较两棵 VmatNode 树（往返测试用）。</summary>
    public static bool StructuralEquals(VmatNode a, VmatNode b)
    {
        if (!string.Equals(a.Key, b.Key, StringComparison.Ordinal)) return false;
        if (!string.Equals(a.Value ?? string.Empty, b.Value ?? string.Empty, StringComparison.Ordinal)) return false;
        if (a.Children.Count != b.Children.Count) return false;
        for (var i = 0; i < a.Children.Count; i++)
            if (!StructuralEquals(a.Children[i], b.Children[i])) return false;
        return true;
    }

    // ─── internal helpers ───────────────────────────────────────────────────

    /// <summary>
    /// 把引擎 JSON 的根表转成本库节点树，必要时把正文里的 <c>Layer0</c> 提升为根节点。
    /// （本库内存模型：根本身就叫 Layer0，参数是它的直接子节点——VmatDocument.ShaderName
    /// 依赖这一点在根的子节点里找 shader。KV1 文档名即顶层键，天然满足；
    /// KV3 顶层匿名、Layer0 是正文子节点，需提升。）
    /// </summary>
    private static VmatNode BuildRoot(Dictionary<string, Kv3Value> table)
    {
        var root = new VmatNode(string.Empty);
        CopyChildren(table, root);

        if (root.Key.Length == 0
            && root.Children.Count == 1
            && string.Equals(root.Children[0].Key, Layer0Key, StringComparison.Ordinal)
            && root.Children[0].IsContainer)
        {
            var lifted = new VmatNode(Layer0Key);
            lifted.Children.AddRange(root.Children[0].Children);
            return lifted;
        }
        return root;
    }

    private static void CopyChildren(Dictionary<string, Kv3Value> table, VmatNode dst)
    {
        foreach (var (name, value) in table)
        {
            var child = new VmatNode(name);
            switch (value.Kind)
            {
                case Kv3Kind.Table:
                    CopyChildren(value.Table ?? new Dictionary<string, Kv3Value>(), child);
                    break;
                case Kv3Kind.Array:
                    {
                        // KV1 没有原生数组；扁平化为数字键子节点，结构可往返。
                        var arr = new VmatNode(string.Empty);
                        var i = 0;
                        foreach (var el in value.Items)
                        {
                            var itemChild = new VmatNode(i.ToString());
                            if (el.Kind == Kv3Kind.Table)
                                CopyChildren(el.Table ?? new Dictionary<string, Kv3Value>(), itemChild);
                            else
                                itemChild.Value = el.Text;
                            arr.Children.Add(itemChild);
                            i++;
                        }
                        child.Children.Add(arr);
                        break;
                    }
                default:
                    child.Value = value.Text;
                    break;
            }
            dst.Children.Add(child);
        }
    }

    /// <summary>VmatNode 树 → JSON 文本（BCL System.Text.Json；叶子一律写字符串，KV1 输出都会加引号）。</summary>
    private static string ToJson(VmatNode root)
    {
        var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            // 引擎 SaveKV3AsKV1Text 用树里的 "_root_keyname" 取文档名（t24 实证）；
            // 不写它则文档名为空、正文多包一层 Layer0。
            w.WriteString("_root_keyname", root.Key.Length == 0 ? Layer0Key : root.Key);
            foreach (var c in root.Children) WriteNode(w, c.Key, c);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteNode(Utf8JsonWriter w, string key, VmatNode node)
    {
        if (node.IsContainer)
        {
            w.WriteStartObject(key);
            foreach (var c in node.Children) WriteNode(w, c.Key, c);
            w.WriteEndObject();
        }
        else
        {
            w.WriteString(key, node.Value ?? string.Empty);
        }
    }
}
