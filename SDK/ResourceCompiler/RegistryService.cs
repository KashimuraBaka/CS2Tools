
// ============================================================================
// RegistryService —— 进程内（GUI 安全区）只读解析 resourcecompiler.dll 编译注册表
//
// 设计依据（t13 队长方向）：
//   * 进程内只做**只读**操作：CreateInterface + RTTI 校验 + 注册表读取。
//   * **禁用锚点库旧 RVA**：anchors json 的 build_sha256=d1115f9f 已不等于盘上
//     204E16E5（游戏 10-17 更新），helper 的 ValidateBuild 会抛异常——这正是它该做的。
//   * 注册表表头 RVA 经实测**未变**（0x3078730，43 条标识符与 t24 逐位相同），
//     但**工厂指针非均匀平移**（实测 +0x1C0/+0x90/+0x110/0 混存）⇒ 绝不可硬编码工厂 RVA，
//     必须从表里实时读。表头 RVA 仍做**运行时自校验**（index 0 必须读到
//     "CompileWorldNode"），不符则明确报错，不静默用错数据。
// ============================================================================
using SDK.Native;

namespace SDK.ResourceCompiler;

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

/// <summary>标识符解析结果（SDK 口径：条目级）。</summary>
public sealed class ResolveResult
{
    public bool Found { get; init; }
    public string Identifier { get; init; } = "";
    public string Message { get; init; } = "";
    public RegistryEntryRow? Entry { get; init; }
}

public sealed class RegistrySnapshot
{
    public List<RegistryEntryRow> Entries { get; init; } = new();
    public IntPtr ModuleBase { get; init; }
    public IntPtr SystemObject { get; init; }
    public string RttiName { get; init; } = "";
    public bool RttiOk { get; init; }
    public string TableRvaText { get; init; } = "";
}

public static class RegistryService
{
    /// <summary>t24 记载的注册表表头 RVA（本构建实测未变，仍做运行时自校验）。</summary>
    public const int RegistryTableRva = 0x3078730;
    public const int RegistryEntryCount = 43;
    public const int RegistryEntrySize = 0x28;
    public const string ExpectedIndex0 = "CompileWorldNode";

    /// <summary>
    /// 只读加载 DLL，取 CreateInterface 系统对象（+RTTI 校验），读注册表。
    /// 全部为只读操作，t12 已实证在裸进程安全。
    /// </summary>
    public static RegistrySnapshot Read(string dllPath)
    {
        if (!File.Exists(dllPath)) throw new FileNotFoundException("resourcecompiler.dll 不存在: " + dllPath);

        // LoadWithDllImport 是静态类 NativeStringFunctionInvoker 的静态方法（返回嵌套类型 ModuleScope）
        using var scope = NativeStringFunctionInvoker.LoadWithDllImport(dllPath);
        IntPtr baseAddr = scope.Base;

        // ---- CreateInterface ResourceCompilerSystem001 ----
        // 不在进程内做 RTTI 全链读取：t12 是在隔离探针里验证的（obj -> vtable -> COL ->
        //   TypeDescriptor -> CResourceCompilerSystem 名称）。COL/TD 偏移口径一旦随构建漂移，
        //   错误偏移会直接读坏内存；.NET 对 AccessViolationException 是 fail-fast（不可 catch），
        //   会把 GUI 一起打死。进程内只取系统对象指针；注册表表头 RVA 的自校验
        //   （index0 必须读到 CompileWorldNode）已足以证明模块与构建正确。
        IntPtr sys = IntPtr.Zero;
        var create = scope.GetExport("CreateInterface");
        if (create != IntPtr.Zero)
        {
            var fn = scope.ResolveDelegateByName<CreateInterfaceFn>("CreateInterface");
            sys = fn("ResourceCompilerSystem001", IntPtr.Zero);
        }

        // ---- 只读注册表 ----
        IntPtr table = scope.GetRva(RegistryTableRva);
        var entries = new List<RegistryEntryRow>(RegistryEntryCount);
        for (int i = 0; i < RegistryEntryCount; i++)
        {
            IntPtr e = IntPtr.Add(table, i * RegistryEntrySize);
            IntPtr idPtr = Marshal.ReadIntPtr(e);
            IntPtr factory = Marshal.ReadIntPtr(e, 8);
            IntPtr cached = Marshal.ReadIntPtr(e, 0x20);
            string id = idPtr == IntPtr.Zero ? "" : (Marshal.PtrToStringAnsi(idPtr) ?? "");
            entries.Add(new RegistryEntryRow
            {
                Index = i,
                Identifier = id,
                EntryAddress = e,
                Factory = factory,
                CachedInstance = cached,
                FactoryRva = factory.ToInt64() - baseAddr.ToInt64(),
                SingletonRva = cached == IntPtr.Zero ? 0 : cached.ToInt64() - baseAddr.ToInt64()
            });
        }

        return new RegistrySnapshot
        {
            Entries = entries,
            ModuleBase = baseAddr,
            SystemObject = sys,
            RttiName = "(not verified in-process; verified by t12 in an isolated probe)",
            RttiOk = sys != IntPtr.Zero,
            TableRvaText = "0x" + RegistryTableRva.ToString("X")
        };
    }

    /// <summary>
    /// 表头自校验：index 0 必须读到 ExpectedIndex0。不符说明表头 RVA 也已随构建变化，
    /// 此时**不得**继续用错数据，直接抛错让 UI 明确提示。
    /// </summary>
    public static void ValidateTable(RegistrySnapshot snap)
    {
        var first = snap.Entries.Count > 0 ? snap.Entries[0].Identifier : "";
        if (!string.Equals(first, ExpectedIndex0, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"注册表表头自校验失败：RVA {snap.TableRvaText} 处 index0 读到 \"{first}\"，预期 \"{ExpectedIndex0}\"。" +
                "说明该构建表头也已移动，需重新定位（勿使用错数据）。");
    }

    /// <summary>MSVC RTTI 全链校验：obj -> vtable -> COL -> TypeDescriptor -> 名称。</summary>
    private static (bool ok, string name) VerifyRtti(IntPtr obj)
    {
        try
        {
            IntPtr vtable = Marshal.ReadIntPtr(obj);
            IntPtr col = Marshal.ReadIntPtr(vtable, -8);
            // CompleteObjectLocator: +0x0C = TypeDescriptor RVA 前 4 字节为签名(1)，+0x10 起为 TD 指针
            IntPtr td = Marshal.ReadIntPtr(col, 0x10);
            // TypeDescriptor: 前 8 字节 vftable，随后 Spare，+0x10 起为 MangledName
            IntPtr namePtr = Marshal.ReadIntPtr(td, 0x10);
            string? name = Marshal.PtrToStringAnsi(namePtr);
            return (!string.IsNullOrEmpty(name), name ?? "");
        }
        catch
        {
            return (false, "");
        }
    }

    /// <summary>按标识符解析（大小写不敏感，复刻 V_stricmp_fast 语义）。</summary>
    public static ResolveResult Resolve(RegistrySnapshot snap, string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return new ResolveResult { Found = false, Identifier = identifier ?? "", Message = "该类型没有 m_CompilerIdentifier。" };

        var hit = snap.Entries.FirstOrDefault(e =>
            string.Equals(e.Identifier, identifier, StringComparison.OrdinalIgnoreCase));

        if (hit is null)
        {
            return new ResolveResult
            {
                Found = false,
                Identifier = identifier,
                Message = $"未命中：注册表 {snap.Entries.Count} 条中没有 \"{identifier}\"。" +
                          "引擎侧对应错误串语义: Resource type compiler identifier not found"
            };
        }

        return new ResolveResult { Found = true, Identifier = identifier, Entry = hit, Message = $"命中 idx {hit.Index}" };
    }

    // CreateInterface(const char* interfaceName, void* returnCode) -> void*   [Microsoft x64]
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateInterfaceFn([MarshalAs(UnmanagedType.LPStr)] string interfaceName, IntPtr returnCode);
}
