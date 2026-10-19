// ---------------------------------------------------------------------------
//  NativeStringFunctionInvoker.cs  —  “按字符串调用本机函数”的可复用 helper
//
//  用途：给定 DLL 路径 + 函数名字符串（或 RVA/锚点）+ 参数 → 调用该函数。
//
//  两条路径（务必区分，见 tools\csharp\stream_kv3_zstd_huffman_fix.md）：
//    A) 导出函数：LoadLibraryW + GetProcAddress（或 .NET 5+ NativeLibrary.GetExport）
//       + Marshal.GetDelegateForFunctionPointer<TDelegate>() → 委托调用。
//    B) 未导出内部函数：GetProcAddress 对内部函数**无效**（它只查导出表），
//       必须「模块基址 + RVA」或「特征签名扫描」定位，再用 Marshal 构造委托。
//       RVA 来源：本项目 anchors\*.json（t9/t12/t17 产出的锚点库）
//                 + tools\locate-anchors.ps1（跨构建重新定位）。
//
//  与 t24 的对接（reports\assettypes_common_compileridentifier_report.md）：
//    CResourceCompilerSystem::GetCompilerByIdentifier @ 0x180188300 **未导出**
//    ⇒ 无法由外部按名调用。本 helper 因此提供等价实现 ResolveCompilerFactoryByString()：
//    只读加载模块 → 读真实的 43 条注册表（RVA 来自 anchors 的 key_facts.registry_table_va）
//    → 与引擎相同的**大小写不敏感线性扫描** → 返回 +0x08 的工厂函数指针
//    → 可用 InvokeResolvedFactory() 真正调用它（内建单例 thunk）。
//
//  ABI / 封送要点（x64）：
//    * 唯一调用约定是 Microsoft x64（rcx,rdx,r8,r9 + 影子空间）；C# 委托必须标
//      [UnmanagedFunctionPointer(CallingConvention.Cdecl)]（x64 下与 StdCall 等价，
//      但显式写 Cdecl 更不易误读；绝不要用 Winapi 之外的 32 位假设）。
//    * 字符串：`string` + CharSet.Ansi（LPStr/UTF-8 在 x64 下多为 ANSI 字节）；
//      宽字符 API 用 CharSet.Unicode（LPWStr）。.NET 5+ 也可用
//      [MarshalAs(UnmanagedType.LPUTF8Str)]；返回 `char*` 用 Marshal.PtrToStringAnsi/UTF8。
//    * 结构体：显式 [StructLayout(LayoutKind.Sequential)]，注意 8 字节对齐与填充。
//    * out/int*：用 `out int`/`IntPtr`；bool 一律用 4 字节 BOOL（`[MarshalAs(UnmanagedType.Bool)]`）。
//    * 错误码：LibraryImport 上标 SetLastError=true，调用后立刻 Marshal.GetLastWin32Error()。
//    * 句柄释放：LoadLibraryW 必须配 FreeLibrary（本文件的 ModuleScope 实现了 IDisposable）。
//    * 失败模式：GetProcAddress 返回 IntPtr.Zero ⇒ 名字拼错或**函数未导出**；
//      委托签名不匹配 ⇒ 栈/寄存器错配，通常表现为 AccessViolation（不可恢复）。
// ---------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace SDK.Native;

/// <summary>本机函数调用失败（加载、导出查找、锚点校验等）时抛出。</summary>
public sealed class NativeInvokeException(string message) : Exception(message)
{
}

/// <summary>“给定 DLL 路径 + 函数名字符串 + 参数 → 调用该函数”的 helper。</summary>
public static partial class NativeStringFunctionInvoker
{
    // ---------------- 源生成 P/Invoke 路径（LibraryImport，kernel32） ----------------
    // t24：由 [DllImport]（运行时 IL stub）改为 [LibraryImport]（编译期生成封送代码）。
    [LibraryImport("kernel32", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr LoadLibraryW(string lpFileName);

    [LibraryImport("kernel32", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeLibrary(IntPtr hModule);

    [LibraryImport("kernel32", SetLastError = true)]
    private static partial IntPtr GetModuleHandleW(IntPtr lpModuleName);

    // ---------------- 模块生命周期 ----------------

    /// <summary>已加载模块的作用域：<see cref="Base"/> 即模块基址（Windows 上 HMODULE == 基址）。</summary>
    public sealed class ModuleScope : IDisposable
    {
        private IntPtr _handle;
        private readonly bool _viaNativeLibrary;

        internal ModuleScope(IntPtr handle, string path, bool viaNativeLibrary)
        {
            _handle = handle; Path = path; _viaNativeLibrary = viaNativeLibrary;
        }

        /// <summary>磁盘上的模块路径（只读加载，不会修改文件）。</summary>
        public string Path { get; }

        /// <summary>模块基址（x64 进程下即 HMODULE 值）。</summary>
        public IntPtr Base => _handle;

        /// <summary>GetProcAddress 语义：仅能命中**导出**函数；内部函数返回 IntPtr.Zero。</summary>
        public IntPtr GetExport(string exportName)
            => _viaNativeLibrary ? NativeLibrary.GetExport(_handle, exportName) : GetProcAddress(_handle, exportName);

        /// <summary>模块基址 + RVA（内部函数定位方式；RVA 来自锚点库）。</summary>
        public IntPtr GetRva(int rva) => IntPtr.Add(_handle, rva);

        /// <summary>把 RVA 处的委托指针包成强类型委托。</summary>
        public TDelegate ResolveDelegateAtRva<TDelegate>(int rva) where TDelegate : Delegate
            => Marshal.GetDelegateForFunctionPointer<TDelegate>(GetRva(rva));

        /// <summary>把导出名解析成强类型委托（按名调用）。</summary>
        public TDelegate ResolveDelegateByName<TDelegate>(string exportName) where TDelegate : Delegate
        {
            var p = GetExport(exportName);
            if (p == IntPtr.Zero)
                throw new NativeInvokeException($"导出 '{exportName}' 未在 {System.IO.Path.GetFileName(Path)} 中找到（GetProcAddress=0，Win32Error={Marshal.GetLastWin32Error()}）。" +
                                                "若它是内部函数，请改用 ResolveRva/ResolveFromAnchors。");
            return Marshal.GetDelegateForFunctionPointer<TDelegate>(p);
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;
            if (_viaNativeLibrary) NativeLibrary.Free(_handle); else FreeLibrary(_handle);
            _handle = IntPtr.Zero;
        }
    }

    /// <summary>用 LoadLibraryW 只读加载模块（DllImport 路径）。</summary>
    public static ModuleScope LoadWithDllImport(string dllPath) => Open(dllPath, viaNativeLibrary: false);

    /// <summary>用 .NET 5+ NativeLibrary 只读加载模块（对照路径，不依赖 DllImport）。</summary>
    public static ModuleScope LoadWithNativeLibrary(string dllPath) => Open(dllPath, viaNativeLibrary: true);

    private static ModuleScope Open(string dllPath, bool viaNativeLibrary)
    {
        if (!System.IO.File.Exists(dllPath)) throw new NativeInvokeException($"模块不存在：{dllPath}");
        if (viaNativeLibrary)
        {
            IntPtr h = NativeLibrary.Load(dllPath);
            if (h == IntPtr.Zero) throw new NativeInvokeException($"NativeLibrary.Load 失败：{dllPath}");
            return new ModuleScope(h, dllPath, true);
        }
        IntPtr h2 = LoadLibraryW(dllPath);
        if (h2 == IntPtr.Zero)
            throw new NativeInvokeException($"LoadLibraryW 失败：{dllPath}，Win32Error={Marshal.GetLastWin32Error()}");
        return new ModuleScope(h2, dllPath, false);
    }

    // ---------------- 路径 A：按名字符串调用**导出**函数 ----------------

    /// <summary>把导出函数名字符串解析成委托（路径 A）。</summary>
    public static TDelegate ResolveExportByName<TDelegate>(string dllPath, string exportName) where TDelegate : Delegate
    {
        using var m = LoadWithDllImport(dllPath);
        return m.ResolveDelegateByName<TDelegate>(exportName);
    }

    /// <summary>按名调用导出函数（自动加载/释放模块），返回装箱结果。</summary>
    /// <param name="dllPath">DLL 全路径（或已在系统搜索路径中的名字）。</param>
    /// <param name="exportName">导出名字符串。</param>
    /// <param name="delegateType">与该导出 ABI 匹配的委托类型。</param>
    /// <param name="args">参数（按 ABI 顺序）。</param>
    public static object? InvokeExportByName(string dllPath, string exportName, Type delegateType, params object?[] args)
    {
        using var m = LoadWithDllImport(dllPath);
        var d = Marshal.GetDelegateForFunctionPointer(m.GetExportOrThrow(exportName), delegateType);
        return d.DynamicInvoke(args);
    }

    private static IntPtr GetExportOrThrow(this ModuleScope m, string exportName)
    {
        var p = m.GetExport(exportName);
        if (p == IntPtr.Zero)
            throw new NativeInvokeException($"导出 '{exportName}' 未找到（Win32Error={Marshal.GetLastWin32Error()}）；内部函数请走 RVA 路径。");
        return p;
    }

    // ---------------- 路径 B：按 RVA / 锚点调用**未导出**函数 ----------------

    /// <summary>枚举 PE 节表（只读读文件，用于 RVA↔文件偏移双向往返校验）。</summary>
    public static IReadOnlyList<PeSection> ReadSections(string modulePath)
    {
        byte[] b = File.ReadAllBytes(modulePath);
        int e = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(0x3C));
        int coff = e + 4;
        int nsec = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(coff + 2));
        int optSize = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(coff + 16));
        int opt = coff + 20;
        var list = new List<PeSection>(nsec);
        for (int i = 0; i < nsec; i++)
        {
            int s = opt + optSize + i * 40;
            string name = Encoding.ASCII.GetString(b, s, 8).TrimEnd('\0');
            list.Add(new PeSection(name,
                BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(s + 8)),
                BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(s + 12)),
                BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(s + 16)),
                BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(s + 20))));
        }
        return list;
    }

    /// <summary>PE 节（RA = RawAddress=文件偏移，VA = 节虚拟地址，RawSize/VirtualSize）。</summary>
    public readonly record struct PeSection(string Name, uint VirtualSize, uint VirtualAddress, uint RawSize, uint RawPointer)
    {
        /// <summary>VA(相对镜像) ↔ 文件偏移：off = VA - VirtualAddress + RawPointer。</summary>
        public long RvaToFileOffset(uint rva) => rva >= VirtualAddress && rva < VirtualAddress + Math.Max(VirtualSize, RawSize)
            ? rva - VirtualAddress + RawPointer : -1;
    }

    /// <summary>RVA→文件偏移（逐节映射；**不要**用 RVA 直接当文件偏移）。</summary>
    public static long RvaToFileOffset(string modulePath, uint rva)
    {
        foreach (var s in ReadSections(modulePath))
        {
            long o = s.RvaToFileOffset(rva);
            if (o >= 0) return o;
        }
        return -1;
    }

    /// <summary>校验模块 sha256 与锚点库记录一致（防止“引擎更新后按旧 RVA 调用”）。</summary>
    public static void ValidateBuild(string modulePath, string expectedSha256)
    {
        using var fs = File.OpenRead(modulePath);
        string actual = Convert.ToHexString(SHA256.HashData(fs));
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new NativeInvokeException($"构建哈希不匹配：{System.IO.Path.GetFileName(modulePath)} 实测 {actual[..16]}… ≠ 锚点记录 {expectedSha256[..16]}…；" +
                                            "请先用 tools\\locate-anchors.ps1 重新定位锚点。");
    }

    /// <summary>按 RVA 解析未导出函数（可选校验构建哈希）。</summary>
    public static TDelegate ResolveRva<TDelegate>(string dllPath, int rva, string? expectedSha256 = null) where TDelegate : Delegate
    {
        if (expectedSha256 is { Length: > 0 }) ValidateBuild(dllPath, expectedSha256);
        using var m = LoadWithDllImport(dllPath);
        return m.ResolveDelegateAtRva<TDelegate>(rva);
    }

    // ---------------- 锚点库（anchors\*.json）集成 ----------------

    /// <summary>锚点库中的单条锚点。</summary>
    public sealed class Anchor
    {
        public string Id { get; init; } = "";
        public string Symbol { get; init; } = "";
        public string Va { get; init; } = "";
        public string Rva { get; init; } = "";
        public string BuildSha256 { get; init; } = "";
        public string SignatureIda { get; init; } = "";
        public int Length { get; init; }
        public string Kind { get; init; } = "";
        public ulong VaValue => Convert.ToUInt64(Va.Replace("0x", ""), 16);
    }

    /// <summary>读取 anchors\*.json（A/B 线产出的锚点库，格式见 anchors\stream_kv3_zstd_huffman_fix.md）。</summary>
    public static (string Module, string BuildSha256, string ImageBase, List<Anchor> Anchors, JsonElement Root)
        ReadAnchors(string anchorsJsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(anchorsJsonPath));
        var root = doc.RootElement;
        string module = root.TryGetProperty("module", out var mo) ? mo.GetString() ?? "" : "";
        string sha = root.TryGetProperty("build_sha256", out var sh) ? sh.GetString() ?? "" : "";
        string ib = root.TryGetProperty("imagebase", out var ibv) ? ibv.GetString() ?? "0x180000000" : "0x180000000";
        var list = new List<Anchor>();
        if (root.TryGetProperty("anchors", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                list.Add(new Anchor
                {
                    Id = a.TryGetProperty("id", out var v1) ? v1.GetString() ?? "" : "",
                    Symbol = a.TryGetProperty("symbol", out var v2) ? v2.GetString() ?? "" : "",
                    Va = a.TryGetProperty("va", out var v3) ? v3.GetString() ?? "0x0" : "0x0",
                    Rva = a.TryGetProperty("rva", out var v4) ? v4.GetString() ?? "" : "",
                    BuildSha256 = a.TryGetProperty("build_sha256", out var v5) ? v5.GetString() ?? "" : "",
                    SignatureIda = a.TryGetProperty("signature_ida", out var v6) ? v6.GetString() ?? "" : "",
                    Length = a.TryGetProperty("length", out var v7) && v7.TryGetInt32(out var l) ? l : 0,
                    Kind = a.TryGetProperty("kind", out var v8) ? v8.GetString() ?? "" : "",
                });
            }
        }
        return (module, sha, ib, list, root.Clone());
    }

    /// <summary>按锚点 id/symbol（前缀匹配）在锚点库中定位 RVA，并按 RVA 构造委托（路径 B 的标准用法）。</summary>
    public static (TDelegate Delegate, Anchor Anchor) ResolveFromAnchors<TDelegate>(
        string anchorsJsonPath, string dllPath, string idOrSymbolPrefix, string imageBaseOverride = "0x180000000")
        where TDelegate : Delegate
    {
        var (module, sha, imageBase, anchors, _) = ReadAnchors(anchorsJsonPath);
        string ib = imageBaseOverride != "0x180000000" ? imageBaseOverride : imageBase;
        ulong baseVal = Convert.ToUInt64(ib.Replace("0x", ""), 16);
        var a = anchors.FirstOrDefault(x => x.Id.StartsWith(idOrSymbolPrefix, StringComparison.OrdinalIgnoreCase)
                                         || x.Symbol.StartsWith(idOrSymbolPrefix, StringComparison.OrdinalIgnoreCase))
                ?? throw new NativeInvokeException($"锚点库中找不到 '{idOrSymbolPrefix}'（库内 {anchors.Count} 条锚点，module={module}）");
        if (!string.IsNullOrEmpty(sha)) ValidateBuild(dllPath, sha);
        ulong va = a.VaValue;
        int rva = checked((int)(va - baseVal));
        using var m = LoadWithDllImport(dllPath);
        return (m.ResolveDelegateAtRva<TDelegate>(rva), a);
    }

    /// <summary>
    /// 路径 B 的“两级调用”形态：拿到内部函数返回的指针后，再用委托调用它。
    /// （对应 t24 的 GetCompilerByIdentifier：解析函数未导出 ⇒ 用 RVA 定位，返回值再转委托。）
    /// </summary>
    public static TSecond InvokeRvaAndReinterpret<TFirst, TSecond>(
        string dllPath, int firstRva, TFirst first, Func<TFirst, IntPtr> invoke, Type secondDelegateType, params object?[] secondArgs)
        where TFirst : Delegate
    {
        var d1 = ResolveRva<TFirst>(dllPath, firstRva);
        IntPtr got = invoke(d1);
        if (got == IntPtr.Zero) throw new NativeInvokeException("第一阶段返回 NULL（解析失败或未命中）。");
        var d2 = Marshal.GetDelegateForFunctionPointer(got, secondDelegateType);
        return (TSecond)d2.DynamicInvoke(secondArgs)!;
    }

    // ---------------- t24 链路：registry 表 + 大小写不敏感扫描 ----------------

    /// <summary>注册表条目（t24 实测结构：{char* id; void* pfnFactory; char* module; char* iface; void* cached}，步长 0x28）。</summary>
    public readonly record struct CompilerRegistryEntry(
        int Index, string Identifier, IntPtr EntryAddress, IntPtr Factory, IntPtr CachedInstance,
        string? ModuleName, string? InterfaceName);

    /// <summary>t24 的解析函数 CResourceCompilerSystem::GetCompilerByIdentifier **未导出**（仅 6 个导出，无此项）。</summary>
    public const string T24ResolverSymbol = "CResourceCompilerSystem::GetCompilerByIdentifier";
    /// <summary>t24 实测：解析函数 VA=0x180188300（RVA 0x188300），未导出。</summary>
    public const int T24ResolverRva = 0x188300;

    /// <summary>
    /// 等价的“字符串 → 编译函数指针”解析（不调用引擎内部函数，而是只读读它的注册表）。
    /// <para>⚠ 生命周期：本方法内部 using 自行加载/释放模块；**返回的指针只有在模块仍加载时才有意义**。
    /// 若调用方还要解引用它（读字节或当函数调用），必须在外层自己持有一次 LoadWithDllImport（LoadLibrary 引用计数），
    /// 否则会实测到 AccessViolationException（见 reports\dllimport_vdata_resourcecompiler_schema_report.md 的失败模式一节）。</para>
    /// 与引擎一致：**大小写不敏感**逐条扫描（复刻 V_stricmp_fast 的比较语义）。
    /// </summary>
    public static CompilerRegistryEntry? ResolveCompilerFactoryByString(
        string dllPath, string identifier, string anchorsJsonPath, int entryCount = 43)
    {
        var (_, sha, imageBase, _, root) = ReadAnchors(anchorsJsonPath);
        if (!string.IsNullOrEmpty(sha)) ValidateBuild(dllPath, sha);
        ulong baseVal = Convert.ToUInt64(imageBase.Replace("0x", ""), 16);

        // registry 表 RVA 来自锚点库 key_facts.registry_table_va（VA - imagebase）
        string tableVa = "0x183078730";
        if (root.TryGetProperty("key_facts", out var kf) && kf.TryGetProperty("registry_table_va", out var tv))
            tableVa = tv.GetString() ?? tableVa;
        int tableRva = checked((int)(Convert.ToUInt64(tableVa.Replace("0x", ""), 16) - baseVal));

        using var m = LoadWithDllImport(dllPath);
        IntPtr p = m.GetRva(tableRva);
        for (int i = 0; i < entryCount; i++)
        {
            IntPtr e = IntPtr.Add(p, i * 0x28);
            IntPtr idPtr = Marshal.ReadIntPtr(e);
            string id = idPtr == IntPtr.Zero ? "" : (Marshal.PtrToStringAnsi(idPtr) ?? "");
            if (string.Equals(id, identifier, StringComparison.OrdinalIgnoreCase))   // V_stricmp_fast 语义
            {
                IntPtr fn = Marshal.ReadIntPtr(e, 8);
                IntPtr cached = Marshal.ReadIntPtr(e, 0x20);
                IntPtr modPtr = Marshal.ReadIntPtr(e, 0x10);
                IntPtr ifPtr = Marshal.ReadIntPtr(e, 0x18);
                return new CompilerRegistryEntry(i, id, e, fn, cached,
                    modPtr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(modPtr),
                    ifPtr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ifPtr));
            }
        }
        return null;  // 引擎此时会记 'Resource type compiler identifier not found: "%s"'
    }

    /// <summary>枚举整张注册表（只读；用于核对 anchors 的 registry_map）。</summary>
    public static List<CompilerRegistryEntry> ReadCompilerRegistry(string dllPath, string anchorsJsonPath, int entryCount = 43)
    {
        var (_, sha, imageBase, _, root) = ReadAnchors(anchorsJsonPath);
        if (!string.IsNullOrEmpty(sha)) ValidateBuild(dllPath, sha);
        ulong baseVal = Convert.ToUInt64(imageBase.Replace("0x", ""), 16);
        string tableVa = "0x183078730";
        if (root.TryGetProperty("key_facts", out var kf) && kf.TryGetProperty("registry_table_va", out var tv))
            tableVa = tv.GetString() ?? tableVa;
        int tableRva = checked((int)(Convert.ToUInt64(tableVa.Replace("0x", ""), 16) - baseVal));
        var list = new List<CompilerRegistryEntry>(entryCount);
        using var m = LoadWithDllImport(dllPath);
        IntPtr p = m.GetRva(tableRva);
        for (int i = 0; i < entryCount; i++)
        {
            IntPtr e = IntPtr.Add(p, i * 0x28);
            IntPtr idPtr = Marshal.ReadIntPtr(e);
            IntPtr fn = Marshal.ReadIntPtr(e, 8);
            IntPtr cached = Marshal.ReadIntPtr(e, 0x20);
            IntPtr modPtr = Marshal.ReadIntPtr(e, 0x10);
            IntPtr ifPtr = Marshal.ReadIntPtr(e, 0x18);
            list.Add(new CompilerRegistryEntry(i, idPtr == IntPtr.Zero ? "" : (Marshal.PtrToStringAnsi(idPtr) ?? ""), e, fn, cached,
                modPtr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(modPtr),
                ifPtr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ifPtr)));
        }
        return list;
    }

    /// <summary>
    /// 调用已解析的工厂指针（t24：内建单例为 2 条指令 `lea rax,&lt;singleton&gt; retn`）。
    /// <para>⚠ 同 ResolveCompilerFactoryByString：entry.Factory 必须指向**仍加载**的模块，调用方需自持作用域。</para>
    /// 先做字节自检，只有形态匹配才真正调用；否则只返回指针并说明原因（避免把构造器当 thunk 盲调）。
    /// </summary>
    public static (IntPtr Result, bool Called, string Note) InvokeResolvedFactory(CompilerRegistryEntry entry)
    {
        if (entry.Factory == IntPtr.Zero) return (IntPtr.Zero, false, "工厂指针为空（该条目走 module/interface 路径）");
        if (!TryDescribeSingletonThunk(entry.Factory, out string shape))
            return (IntPtr.Zero, false, $"工厂字节不像单例 thunk（{shape}）⇒ 未调用，仅返回指针");
        var fn = Marshal.GetDelegateForFunctionPointer<FnNoArgs>(entry.Factory);
        IntPtr r = fn();
        return (r, true, $"已调用 thunk（{shape}）");
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr FnNoArgs();

    /// <summary>识别 `lea rax,&lt;rip-rel&gt;; ret`（含 `mov rax,...`）形态，返回描述文本。</summary>
    public static bool TryDescribeSingletonThunk(IntPtr address, out string shape)
    {
        shape = "";
        try
        {
            var code = new byte[8];
            Marshal.Copy(address, code, 0, 8);
            var hex = Convert.ToHexString(code);

            if (code[0] == 0x48 && code[1] == 0x8D && code[2] == 0x05 && code[7] == 0xC3)
            {
                shape = $"48 8D 05 {hex.Substring(6, 8)} C3 (lea rax,[rip+rel]; ret)";
                return true;
            }

            if (code[0] == 0x48 && code[1] == 0x8B && code[2] == 0x05 && code[7] == 0xC3)
            {
                shape = $"48 8B 05 {hex.Substring(6, 8)} C3 (mov rax,[rip+rel]; ret)";
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>解析 `lea rax,[rip+rel]`/`mov rax,[rip+rel]` 的静态单例目标地址（与 TryDescribeSingletonThunk 配套的独立验证）。</summary>
    public static bool TryComputeSingletonTarget(IntPtr factory, out IntPtr target)
    {
        target = IntPtr.Zero;
        try
        {
            byte[] code = new byte[8];
            Marshal.Copy(factory, code, 0, 8);
            if (code[0] == 0x48 && (code[1] == 0x8D || code[1] == 0x8B) && code[2] == 0x05 && code[7] == 0xC3)
            {
                target = IntPtr.Add(factory, 7 + BitConverter.ToInt32(code, 3));
                return true;
            }
        }
        catch { }
        return false;
    }
    /// <summary>读取 RVA 处的前 N 字节（十六进制），用于锚点自检。</summary>
    public static string HexAtRva(string dllPath, int rva, int count)
    {
        long off = RvaToFileOffset(dllPath, (uint)rva);
        if (off < 0) return "<RVA 不在任何节内>";
        using var fs = File.OpenRead(dllPath);
        fs.Seek(off, SeekOrigin.Begin);
        byte[] buf = new byte[count];
        int got = fs.Read(buf, 0, count);
        return Convert.ToHexString(buf, 0, got);
    }
}
