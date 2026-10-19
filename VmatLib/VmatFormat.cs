using System.Globalization;

namespace VmatLib;

/// <summary>
/// Source 2 KeyValues（VMAT）文档的解析/序列化入口。
/// t24：底层从三方库 ValveKeyValue 改为 tier0 原生 KV 函数
/// （LoadKV3FromKV3OrKV1 / SaveKV3AsKV1Text，经 <see cref="KvAdapter"/>），
/// 与 SDK 的 KV3 链同源——解析与写出都用引擎自己的实现。
/// </summary>
public static class VmatFormat
{
    /// <summary>解析 VMAT 文本（KV1/KV3 自动判别）。</summary>
    public static VmatNode Parse(string source) => KvAdapter.Parse(source);

    /// <summary>把 VmatNode 树写回 KV1 文本（引擎规范布局，见 <see cref="VmatFormatter"/>）。</summary>
    public static string Serialize(VmatNode root) => KvAdapter.Serialize(root);

    /// <summary>序列化后再解析回来，返回 (文本, 解析树)——供往返校验。</summary>
    public static (string Text, VmatNode Parsed) SerializeAndReParse(VmatNode root)
    {
        var text = KvAdapter.Serialize(root);
        var reparsed = KvAdapter.Parse(text);
        return (text, reparsed);
    }
}

/// <summary>
/// Helper extensions for working with parsed <see cref="VmatNode"/> trees.
/// </summary>
public static class VmatNodeExtensions
{
    /// <inheritdoc/>
    public static IEnumerable<VmatNode> Flatten(this VmatNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var desc in child.Flatten())
                yield return desc;
        }
    }

    /// <inheritdoc/>
    public static string ToKeyValueText(this VmatNode root) => VmatFormat.Serialize(root);

    /// <inheritdoc/>
    public static float GetFloat(this VmatNode node, string key, float fallback = 0f)
    {
        var c = node.FindChild(key);
        return c?.TryParseFloat() ?? fallback;
    }

    /// <inheritdoc/>
    public static int GetInt(this VmatNode node, string key, int fallback = 0)
    {
        var c = node.FindChild(key);
        return c?.TryParseInt() ?? fallback;
    }

    /// <inheritdoc/>
    public static bool GetBool(this VmatNode node, string key, bool fallback = false)
    {
        var c = node.FindChild(key);
        return c?.TryParseBool() ?? fallback;
    }

    /// <inheritdoc/>
    public static string GetString(this VmatNode node, string key, string fallback = "")
    {
        var c = node.FindChild(key);
        return c?.Value ?? fallback;
    }

    /// <inheritdoc/>
    public static Vector4 GetVector(this VmatNode node, string key, Vector4? fallback = null)
    {
        var c = node.FindChild(key);
        return c is null ? fallback ?? default : c.TryParseVector() ?? fallback ?? default;
    }

    /// <inheritdoc/>
    public static void SetFloat(this VmatNode node, string key, float value)
    {
        var c = node.FindChild(key) ?? node.AddChild(key);
        c.Value = value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    /// <inheritdoc/>
    public static void SetInt(this VmatNode node, string key, int value)
    {
        var c = node.FindChild(key) ?? node.AddChild(key);
        c.Value = value.ToString(CultureInfo.InvariantCulture);
    }

    /// <inheritdoc/>
    public static void SetBool(this VmatNode node, string key, bool value)
    {
        var c = node.FindChild(key) ?? node.AddChild(key);
        c.Value = value ? "1" : "0";
    }

    /// <inheritdoc/>
    public static void SetString(this VmatNode node, string key, string value)
    {
        var c = node.FindChild(key) ?? node.AddChild(key);
        c.Value = value;
    }

    /// <inheritdoc/>
    public static void SetVector(this VmatNode node, string key, Vector4 value)
    {
        var c = node.FindChild(key) ?? node.AddChild(key);
        c.Value = value.ToString();
    }

    /// <inheritdoc/>
    public static VmatNode AddChild(this VmatNode node, string key)
    {
        var existing = node.FindChild(key);
        if (existing is not null) return existing;
        var child = new VmatNode(key);
        node.Children.Add(child);
        return child;
    }

    /// <inheritdoc/>
    public static void RemoveChild(this VmatNode node, string key) => node.Children.RemoveAll(c => string.Equals(c.Key, key, StringComparison.Ordinal));

    /// <inheritdoc/>
    public static VmatNode FirstContainer(this VmatNode node) => node.Children.FirstOrDefault(c => c.IsContainer) ?? node;
}