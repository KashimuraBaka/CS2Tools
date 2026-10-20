// ============================================================================
// SDK.Native.Module — CModule 等价物（CS2Fixes utils/module.h 范式）
//
// 职责：
//   * 模块加载（LoadLibraryW）+ Base / Size / 节表
//   * FindInterface(name)  —— 走 CreateInterface，不用裸偏移猜 vtable
//   * FindSignature(pattern) —— SignatureIterator 等价物：逐字节比对 + `?` 通配
//   * FindUniqueSignature  —— 要求唯一命中；多命中/零命中都算失败（CS2Fixes 语义）
//   * 委托绑定：按导出名 / 按 RVA
//
// 与旧 NativeStringFunctionInvoker 的关系：本类是纯基础设施，不含任何业务
// （anchors / 编译注册表 / KV3 协议都不在这里）。旧 helper 仍被 RegistryService、
// InvokerSmokeTest 使用，逐步迁移后退役。
// ============================================================================
namespace SDK.Native;

/// <summary>一个已加载的原生模块（DLL）。</summary>
public sealed partial class Module : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    public string Path { get; }
    public IntPtr Base { get; }
    public uint SizeOfImage { get; }

    /// <summary>节表项（VA 与文件指针均已换算为 RVA/文件偏移）。</summary>
    public sealed record Section(string Name, uint Va, uint VirtualSize, uint RawPointer, uint RawSize);
    public IReadOnlyList<Section> Sections { get; }

    private Module(string path, IntPtr handle, IntPtr baseAddr, uint size, List<Section> secs)
    {
        Path = path; _handle = handle; Base = baseAddr; SizeOfImage = size; Sections = secs;
    }

    public static Module Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("模块不存在: " + path);
        var h = NativeLibrary.Load(path);
        if (!NativeLibrary.TryGetExport(h, "CreateInterface", out var _))
            NativeLibrary.TryGetExport(h, "DllMain", out _);   // 仅用于拿句柄
        // 用 GetModuleHandleEx 拿真正的基址（CreateInterface 的地址反推不可靠）
        IntPtr realBase = GetModuleBase(path);
        var secs = ReadSections(path);
        uint size = secs.Count > 0 ? MaxEnd(secs) : 0;
        return new Module(path, h, realBase, size, secs);
    }

    private static uint MaxEnd(List<Section> secs)
    {
        uint m = 0;
        foreach (var s in secs) { uint e = s.Va + s.VirtualSize; if (e > m) m = e; }
        return m;
    }

    // t24/t28g：LibraryImport（源生成 P/Invoke 封送，编译期生成 marshalling 代码）
    [LibraryImport("kernel32", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandleW(string lpModuleName);

    [LibraryImport("kernel32", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr LoadLibraryW(string lpFileName);

    private static IntPtr GetModuleBase(string path)
    {
        // LoadLibraryW 幂等：已加载则返回同一句柄；句柄即基址
        var h = LoadLibraryW(path);
        if (h == IntPtr.Zero) throw new InvalidOperationException("LoadLibraryW 失败: " + path + " (win32err " + Marshal.GetLastWin32Error() + ")");
        return h;
    }

    private static List<Section> ReadSections(string path)
    {
        var b = File.ReadAllBytes(path);
        int peOff = BitConverter.ToInt32(b, 0x3C);
        int coff = peOff + 4;
        ushort numSec = BitConverter.ToUInt16(b, coff + 2);
        ushort optSize = BitConverter.ToUInt16(b, coff + 16);
        int secTab = coff + 20 + optSize;
        var list = new List<Section>();
        for (int i = 0; i < numSec; i++)
        {
            int s = secTab + i * 40;
            string name = System.Text.Encoding.ASCII.GetString(b, s, 8).TrimEnd('\0');
            uint vs = BitConverter.ToUInt32(b, s + 8);
            uint va = BitConverter.ToUInt32(b, s + 12);
            uint rs = BitConverter.ToUInt32(b, s + 16);
            uint rp = BitConverter.ToUInt32(b, s + 20);
            list.Add(new Section(name, va, vs, rp, rs));
        }
        return list;
    }

    /// <summary>RVA -> 运行时地址。</summary>
    public IntPtr GetRva(int rva) => new(Base.ToInt64() + rva);

    /// <summary>取导出函数地址（GetProcAddress 等价）。</summary>
    public IntPtr GetExport(string name)
    {
        NativeLibrary.TryGetExport(_handle, name, out var p);
        return p == IntPtr.Zero ? IntPtr.Zero : p;
    }

    /// <summary>按导出名绑定强类型委托。</summary>
    public T ResolveDelegateByName<T>(string exportName) where T : Delegate
    {
        var p = GetExport(exportName);
        if (p == IntPtr.Zero) throw new MissingMethodException(Path + " 没有导出 " + exportName);
        return Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    /// <summary>按 RVA 绑定强类型委托（用于未导出的内部函数，由签名定位得到）。</summary>
    public T ResolveDelegateAtRva<T>(int rva) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(GetRva(rva));

    /// <summary>
    /// CreateInterface(name, null) —— 接口一律走这个，不用裸偏移猜 vtable。
    /// </summary>
    public IntPtr FindInterface(string interfaceName)
    {
        var fn = ResolveDelegateByName<CreateInterfaceFn>("CreateInterface");
        return fn(interfaceName, IntPtr.Zero);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr CreateInterfaceFn([MarshalAs(UnmanagedType.LPStr)] string name, IntPtr ret);

    // ---------------- 签名扫描 ----------------

    /// <summary>
    /// 解析 "48 89 5C 24 ? 57" 形式的模式为 (bytes, mask)。`?` = 通配。
    /// </summary>
    public static (byte[] bytes, bool[] mask) ParsePattern(string pattern)
    {
        var toks = pattern.Trim().Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[toks.Length];
        var mask = new bool[toks.Length];
        for (int i = 0; i < toks.Length; i++)
        {
            if (toks[i] == "?") { bytes[i] = 0; mask[i] = false; }
            else { bytes[i] = Convert.ToByte(toks[i], 16); mask[i] = true; }
        }
        return (bytes, mask);
    }

    /// <summary>在 .text 内扫描模式，返回全部命中的 RVA。</summary>
    public List<int> FindSignature(string pattern)
    {
        var (bytes, mask) = ParsePattern(pattern);
        int n = bytes.Length;
        var hits = new List<int>();
        var text = Sections.FirstOrDefault(s => s.Name == ".text") ?? throw new InvalidOperationException(Path + " 没有 .text 节");
        var b = File.ReadAllBytes(Path);
        long start = text.RawPointer;
        long end = text.RawPointer + Math.Min(text.RawSize, text.VirtualSize) - n;
        for (long i = start; i < end; i++)
        {
            bool ok = true;
            for (int k = 0; k < n; k++)
            {
                if (mask[k] && b[i + k] != bytes[k]) { ok = false; break; }
            }
            if (ok) hits.Add((int)(i - text.RawPointer + text.Va));
        }
        return hits;
    }

    /// <summary>
    /// 要求唯一命中。零命中或多命中都抛错（CS2Fixes 的 fail-fast 语义）——
    /// 宁可明确失败，也不静默用错地址。
    /// </summary>
    public int FindUniqueSignature(string name, string pattern)
    {
        var hits = FindSignature(pattern);
        if (hits.Count == 0) throw new SignatureNotFoundException(name, Path, pattern, hits);
        if (hits.Count > 1) throw new SignatureAmbiguousException(name, Path, pattern, hits);
        return hits[0];
    }

    /// <summary>
    /// CS2Fixes 式「定位引用者再提取 RIP 相对位移」：给定某函数内的调用点偏移，
    /// 读取 E8 rel32 并算出被调函数 RVA。用于「目标被多处内联展开、
    /// 但只有一个调用方」的场景（如 arena 内嵌符号表初始化）。
    /// </summary>
    public int ResolveCallTargetRva(int callerRva, int callSiteOffsetInCaller)
    {
        var b = File.ReadAllBytes(Path);
        var text = Sections.First(s => s.Name == ".text");
        long off = (long)callerRva - text.Va + text.RawPointer + callSiteOffsetInCaller;
        if (b[off] != 0xE8) throw new InvalidOperationException(
            $"{Path} RVA 0x{callerRva + callSiteOffsetInCaller:X} 处不是 E8（call rel32），实际 0x{b[off]:X2}");
        int rel = BitConverter.ToInt32(b, (int)off + 1);
        return callerRva + callSiteOffsetInCaller + 5 + rel;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != IntPtr.Zero) { NativeLibrary.Free(_handle); _handle = IntPtr.Zero; }
    }
}

public sealed class SignatureNotFoundException(string name, string mod, string pat, List<int> hits)
    : Exception($"签名 [{name}] 在 {Path.GetFileName(mod)} 中未命中（模式 {hits.Count} 处）。游戏更新后代码已漂移，需按 Signatures.cs 中该条的「如何重新找到它」注释重新定位。")
{
    public string SignatureName { get; } = name; public string ModulePath { get; } = mod; public string Pattern { get; } = pat;
}

public sealed class SignatureAmbiguousException(string name, string mod, string pat, List<int> hits)
    : Exception($"签名 [{name}] 在 {Path.GetFileName(mod)} 中有 {hits.Count} 处命中 ({string.Join(", ", hits.ConvertAll(h => "0x" + h.ToString("X")))}），无法确定用哪一个。需加长模式或改用「定位调用方再提取 rel32」的方式。")
{
    public string SignatureName { get; } = name; public string ModulePath { get; } = mod; public string Pattern { get; } = pat; public List<int> Hits { get; } = hits;
}
