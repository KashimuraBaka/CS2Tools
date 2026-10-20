// ============================================================================
// SDK.Native.EngineServiceGuard — 进程内编译路径的**前置守卫**（t28c）。
//
// 背景：resourcecompiler.dll 的编译路径（AssetConfig_Init / GenerateResourceFile）
// 依赖引擎服务图——AssetConfig_Init 内经一批间接槽调用访问全局服务对象：
//   qword_183B60158（+96/+368）、qword_183B60060（+112/+360）、
//   g_pThreadPool（+24）、off_182FEAE88（+104/+504）、qword_183B60140（+88）
// 这些全局量由**引擎主程序**在服务注册阶段写入；我们的进程只加载了
// tier0/resourcecompiler 两个 DLL，没有那张图 ⇒ 槽位为 NULL ⇒ 调用即 0xC0000005。
// 且 .NET 对 AccessViolationException 默认 fail-fast（不可 catch），
// 进程内直调会**整个应用陪葬**（t12 实证，也曾因此用子进程隔离）。
//
// 本守卫的做法：调用前先读这些全局量，**任缺一个就不调用**，把「引擎服务图未注册」
// 作为可预期的业务错误上报——应用不崩、用户看到明确原因。
// （VEH 向量化异常恢复路线已实测不可行：.NET 运行时对原生 AV 走 fail-fast，
//  且上下文重写后重执行仍崩，见 t28c 台账。）
// ============================================================================
using System.Runtime.InteropServices;

namespace SDK.Native;

/// <summary>引擎服务图状态。</summary>
public sealed record EngineServiceStatus(
    bool Complete,
    IReadOnlyList<string> Missing,
    string Summary);

public static class EngineServiceGuard
{
    /// <summary>
    /// AssetConfig_Init 直接经间接槽访问的全局服务对象（RVA，相对 resourcecompiler.dll 基址）。
    /// 名字为反编译推断的用途；判据是 AssetConfig_Init(sub_180187EA0) 函数体的调用序列。
    /// </summary>
    private static readonly (string Name, int Rva)[] ServiceSlots =
    [
        ("FileOrAssetSystem(qword_183B60158)", 0x3B60158),
        ("FbxOrEngineCore(qword_183B60060)",   0x3B60060),
        ("EconTypeRegistry(qword_183B60140)",  0x3B60140),
        ("GlobalObject(off_182FEAE88)",        0x2FEAE88),
    ];

    /// <summary>g_pThreadPool（tier0 全局，AssetConfig_Init 经 *g_pThreadPool+24 创建线程池）。
    /// RVA 0x39A3D0：由 tier0 内 `mov rcx, cs:g_pThreadPool`（0x1800891BF, 48 8B 0D rel32）反算。</summary>
    private const int Tier0ThreadPoolRva = 0x39A3D0;

    /// <summary>
    /// 检查进程内是否具备引擎服务图。任缺一项即返回 <c>Complete=false</c>——
    /// 此时**不要**调用 AssetConfig_Init / GenerateResourceFile。
    /// </summary>
    public static EngineServiceStatus Check(string gameBinDir)
    {
        var rcPath = Path.Combine(gameBinDir, Signatures.ResourceCompilerDll);
        var t0Path = Path.Combine(gameBinDir, Signatures.Tier0Dll);
        if (!File.Exists(rcPath) || !File.Exists(t0Path))
            return new EngineServiceStatus(false, new[] { "dll-missing" }, "模块缺失: " + rcPath);

        using var rc = Module.Load(rcPath);
        using var t0 = Module.Load(t0Path);

        var missing = new List<string>();
        foreach (var (name, rva) in ServiceSlots)
        {
            if (rva >= rc.SizeOfImage) { missing.Add(name + "(unmapped)"); continue; }   // 越界绝不读
            long v = Marshal.ReadInt64(rc.GetRva(rva));
            if (v == 0) missing.Add(name);
        }
        if (Tier0ThreadPoolRva >= t0.SizeOfImage) missing.Add("g_pThreadPool(unmapped)");
        else if (Marshal.ReadInt64(t0.GetRva(Tier0ThreadPoolRva)) == 0) missing.Add("g_pThreadPool");

        return missing.Count == 0
            ? new EngineServiceStatus(true, Array.Empty<string>(), "引擎服务图完整（4+1 槽位非空）")
            : new EngineServiceStatus(false, missing,
                "引擎服务图未注册（缺 " + missing.Count + " 槽位）——resourcecompiler 编译路径由引擎主程序注册服务，" +
                "本进程只加载了 tier0/resourcecompiler，无法进程内编译");
    }
}
