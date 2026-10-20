// ============================================================================
// SDK.Native.Signatures —— 签名表加载器（数据正本：SDK\signatures.json）
//
// CS2Fixes gamedata/cs2fixes.jsonc 范式：签名是**数据**不是代码。t24 按用户指示从
// C# 记录改为 JSON 维护——改签名只编辑 signatures.json，不必动代码。
//
// 加载顺序（优先 loose，免重编译维护）：
//   ① 程序输出目录的 signatures.json（由 SDK.csproj 的 Content 复制过来，可直接编辑后重启生效）；
//   ② SDK.dll 内嵌资源里的同一份（编译期嵌入，保底——输出目录文件缺失时仍可用）。
//
// JSON 结构：{ _doc, modules: {tier0|resourcecompiler: 相对路径},
//             signatures: [{name, module, windows(字节模式, ? 通配),
//                           poolInitCallOffset?, howToRefind}] }
// 每条 howToRefind 写明了「失效时如何重新找到它」——游戏更新导致签名漂移时按它重定位。
// ============================================================================
using System.Text.Json;
using System.IO;

namespace SDK.Native;

/// <summary>签名所属模块。</summary>
public enum SigModule { Tier0, ResourceCompiler }

/// <summary>一条命名签名。</summary>
public sealed record Signature(
    string Name,
    SigModule Module,
    string WindowsPattern,
    string HowToRefind,
    int PoolInitCallOffset = 0);

public static class Signatures
{
    private const string JsonFileName = "signatures.json";

    private static Dictionary<SigModule, string> ModulePaths { get; } = [];

    /// <summary>全部签名（JSON 解析结果）。</summary>
    public static IReadOnlyList<Signature> All { get; } = Load();

    /// <summary>tier0.dll 的相对路径（相对 &lt;游戏目录&gt;，即 bin 的上一级）。</summary>
    public static string Tier0Dll => ModulePath(SigModule.Tier0);

    /// <summary>resourcecompiler.dll 的相对路径。</summary>
    public static string ResourceCompilerDll => ModulePath(SigModule.ResourceCompiler);

    /// <summary>ArenaCtor 内调用 SymTabPoolInit 的调用点偏移（该函数被内联 10 份，纯模式不可区分）。</summary>
    public static int ArenaCtor_PoolInitCallOffset => Get("ArenaCtor").PoolInitCallOffset;

    /// <summary>按名称取签名（不存在即抛）。</summary>
    public static Signature Get(string name)
        => All.First(s => s.Name == name);

    /// <summary>按模块枚举签名。</summary>
    public static IEnumerable<Signature> For(SigModule m) => All.Where(s => s.Module == m);

    /// <summary>取模块的相对 DLL 路径。</summary>
    public static string ModulePath(SigModule m)
        => ModulePaths.TryGetValue(m, out var p) ? p : throw new KeyNotFoundException("signatures.json 的 modules 里没有 " + m);

    // ---------------- 加载 ----------------

    private static List<Signature> Load()
    {
        using var doc = JsonDocument.Parse(ReadJson());
        var root = doc.RootElement;

        ModulePaths.Clear();
        foreach (var kv in root.GetProperty("modules").EnumerateObject())
        {
            if (Enum.TryParse<SigModule>(kv.Name, ignoreCase: true, out var mod))
                ModulePaths[mod] = kv.Value.GetString() ?? throw new InvalidOperationException("signatures.json modules." + kv.Name + " 为空");
        }

        var list = new List<Signature>();
        foreach (var e in root.GetProperty("signatures").EnumerateArray())
        {
            list.Add(new Signature(
                e.GetProperty("name").GetString() ?? throw new InvalidOperationException("签名缺少 name"),
                Enum.Parse<SigModule>(e.GetProperty("module").GetString() ?? "", ignoreCase: true),
                e.GetProperty("windows").GetString() ?? throw new InvalidOperationException("签名缺少 windows"),
                e.TryGetProperty("howToRefind", out var h) ? h.GetString() ?? "" : "",
                e.TryGetProperty("poolInitCallOffset", out var off) ? off.GetInt32() : 0));
        }
        if (list.Count == 0) throw new InvalidOperationException("signatures.json 解析出 0 条签名");
        return list;
    }

    private static string ReadJson()
    {
        // ① 输出目录的 loose 文件（免重编译维护）
        var loose = Path.Combine(AppContext.BaseDirectory, JsonFileName);
        if (File.Exists(loose)) return File.ReadAllText(loose);

        // ② 内嵌资源保底（编译进 SDK.dll 的同一份）
        var asm = typeof(Signatures).Assembly;
        var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(JsonFileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("signatures.json 既不在输出目录（" + loose + "）也不在内嵌资源中");
        using var s = asm.GetManifestResourceStream(resName)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
