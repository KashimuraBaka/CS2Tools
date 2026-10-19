// ============================================================================
// VmatLib.VmatFormatter — VMAT 文本规范化（t24：改走 tier0 原生 KV 链）
//
// 原实现用三方库 ValveKeyValue 解析 + 序列化。现改为：解析走引擎
// LoadKV3FromKV3OrKV1，写出走引擎 SaveKV3AsKV1Text——输出即引擎自己的
// KV1 规范布局（顶层 "Layer0"，一键一值加引号），与 Valve 出厂材质同形。
// 已知限制（引擎行为）：注释不保留；重复键保留但无法挂回注释。
// ============================================================================
using System.Text;
using SDK.Tier0;

namespace VmatLib;

/// <summary>
/// 把 VMAT（KeyValues1）文本规范化成引擎的规范布局。
/// <code>
/// "Layer0"
/// {
/// 	"shader"  "csgo_vertexlitgeneric.vfx"
/// 	"F_DONT_FLIP_BACKFACE_NORMALS"  "0"
/// }
/// </code>
/// 实现不做任何自有解析：读与写都走 tier0 原生 KV 函数（经
/// <see cref="Kv3NativeArena"/>），因此格式化永远不会改变文档含义。
/// </summary>
public static class VmatFormatter
{
    /// <summary>引擎 KV1 写出的缩进/布局由引擎决定，此处仅作文档说明。</summary>
    public const string Indent = "\t";

    /// <summary>按内容判定格式并解析，再以引擎规范布局重新产出。</summary>
    public static string Format(string source) => KvAdapter.Serialize(KvAdapter.Parse(source));

    /// <summary>就地格式化一个文件（UTF-8 无 BOM、LF 换行）。</summary>
    /// <returns>文件字节确实发生变化时为 <c>true</c>。</returns>
    public static bool FormatFile(string path)
    {
        var original = File.ReadAllText(path);
        var formatted = Format(original);
        if (string.Equals(original, formatted, StringComparison.Ordinal)) return false;
        // 显式 LF + UTF-8 无 BOM：Valve 出厂文件既不用 CRLF 也不用 BOM，
        // 且 BOM 会让某些 Source 2 工具把第一个键读坏。
        File.WriteAllText(path, formatted, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return true;
    }

    /// <summary>源文本是否已符合规范布局（即 <see cref="Format"/> 是否空操作）。</summary>
    public static bool IsCanonical(string source) => string.Equals(Format(source), source, StringComparison.Ordinal);

    /// <summary>
    /// 两份 VMAT 文本的结构相等比较（忽略布局）：两边都走同一条引擎解析路径，
    /// 按文档顺序逐键比较——值变了算不等，制表符变了不算。
    /// </summary>
    public static bool Equivalent(string a, string b) => KvAdapter.StructuralEquals(KvAdapter.Parse(a), KvAdapter.Parse(b));
}
