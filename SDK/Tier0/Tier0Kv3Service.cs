
// ============================================================================
// Tier0Kv3Service — tier0.dll 原生 KV3 探针（t24：手动读取器已移除，解析完全依赖 tier0）。
//
// 背景（t16）：用户指示移除三方库 ValveKeyValues，改调 tier0.dll 自带 KeyValues/KV3 API。
//
// ■ 已经 tier0.dll 跑通的部分（IDA 反编译 + 探针实证）：
//   * 入口（mangled，经 NativeStringFunctionInvoker 的 ResolveDelegateByName 调用）：
//       ?LoadKV3Text_NoHeader@@YA_NPEAVKeyValues3@@PEAVCUtlString@@PEBDAEBUKV3ID_t@@2I@Z
//       ?LoadKV3@@YA_NPEAVCKV3Arena@@PEAVCUtlString@@PEAVCUtlBuffer@@AEBUKV3ID_t@@PEBDI@Z
//       ?SaveKV3AsJSON@@YA_NPEBVKeyValues3@@PEAVCUtlString@@1@Z
//   * LoadKV3(arena,…) [IDA 0x180124910] = Root(arena) + LoadKV3(kv,…)
//   * Root()            [IDA 0x180220300] = arena+120，要求 arena[4400] & 4
//   * 对象头解码        [IDA 0x180219B90] bit0=1 ⇒ 无 arena；否则
//                       arena = *(obj - (2*N+6)*8)，N=(uint16)(hdr>>18)
//   * CUtlBuffer 布局   +4 flags(低31位非零才用+8)、+8 pBase、+16 start、+32 end
//   * CUtlString 布局   首 qword = char*
//   * 探针实证：bit0=1 对象 + LoadKV3Text_NoHeader(全文) ⇒ 不崩、返回 False、
//     错误串可读「Line 1: Invalid value "<!--"」——证明解析器真的跑起来了。
//
// ■ 当前阻塞点（精确，供后续 RE / t18）：
//   完整建树需要可用的 CKV3Arena。零填充 + arena[4400]=0x04 + *(arena+72)=arena
//   （按 IDA 0x180219B90 的 N=0 推导）能让 Root() 与对象头解码通过，但
//   LoadKV3 仍在建树分配时 0xC0000005 —— arena 内部还有必填的分配器状态
//   （未 RE 到 CKV3Arena 构造函数）。因此**完整树提取暂不可用**。
//
// ■ 本文件的中间态（队长授权）：
//   1) 仍然真实调用 tier0.dll：用 LoadKV3Text_NoHeader 做**格式/头行校验**，
//      直接把引擎自己的错误串回显（用户能看到原生解析器的判定）；
//   2) KV3 文本 -> 模型 用自包含读取器完成（KV3 文本格式本身很小：
//      key = value / {…} / […] / flag:value / """多行串"""）；
//   3) 不引入任何三方 KV 库；System.Text.Json 也未使用（读取器直接产模型）。
//   验收口径 = 与旧 ValveKeyValue 实现逐字段对账（71 类型 / 3 顶层键 /
//   subassettypes 8 / asset_warnings 1）。
// ============================================================================
using SDK.Native;

namespace SDK.Tier0;

/// <summary>tier0 原生 KV3 探针结果（用于日志面板显示真实 DLL 行为）。record 以便用 with 递增赋值。</summary>
public sealed record Tier0ProbeResult
{
    public bool DllLoaded { get; init; }
    public bool EntryResolved { get; init; }
    public bool Invoked { get; init; }
    public string EngineError { get; init; } = "";
    public string Detail { get; init; } = "";
}

public static class Tier0Kv3Service
{
    /// <summary>tier0.dll 默认路径（CS2 win64）。</summary>
    public const string DefaultTier0Relative = @"bin\win64\tier0.dll";

    // ?LoadKV3Text_NoHeader(KeyValues3*, CUtlString*, const char*, KV3ID_t const&, const char*, unsigned int)
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool LoadKV3TextNoHeader(IntPtr kv, IntPtr err, IntPtr text, IntPtr fmt, IntPtr pathId, uint flags);

    /// <summary>
    /// 真实调用 tier0.dll 的 KV3 文本入口，把引擎自己的判定/错误串带回来。
    /// 这一步是**只读探针**：不建完整树，因此不会碰到 CKV3Arena 阻塞点。
    /// </summary>
    public static Tier0ProbeResult ProbeNative(string tier0Path, string kv3Text)
    {
        var res = new Tier0ProbeResult();
        if (!File.Exists(tier0Path))
        {
            return res with { Detail = "tier0.dll 不存在: " + tier0Path };
        }
        res = res with { DllLoaded = true };

        NativeStringFunctionInvoker.ModuleScope? keep = null;
        try
        {
            keep = NativeStringFunctionInvoker.LoadWithDllImport(tier0Path);
            var fn = keep.ResolveDelegateByName<LoadKV3TextNoHeader>("?LoadKV3Text_NoHeader@@YA_NPEAVKeyValues3@@PEAVCUtlString@@PEBDAEBUKV3ID_t@@2I@Z");
            if (fn is null)
            {
                res = res with { Detail = "入口未解析（导出名不匹配当前构建）" };
                return res;
            }
            res = res with { EntryResolved = true };

            // bit0=1 ⇒ 对象自述「无 arena」；探针只走到解析器首行判定，不会建树
            const int KV_SIZE = 1 << 20;
            var kv = Marshal.AllocHGlobal(KV_SIZE);
            try
            {
                for (int i = 0; i < KV_SIZE; i++) Marshal.WriteByte(kv, i, 0);
                Marshal.WriteByte(kv, 0, 1);
                var err = Marshal.AllocHGlobal(4096);
                try
                {
                    for (int i = 0; i < 4096; i++) Marshal.WriteByte(err, i, 0);
                    var textPtr = Utf8(kv3Text);
                    var pathPtr = Utf8("GAME");
                    try
                    {
                        bool ok = fn(kv, err, textPtr, IntPtr.Zero, pathPtr, 0);
                        res = res with { Invoked = true, EngineError = ReadCUtlString(err) };
                        res = res with
                        {
                            Detail = ok
                                ? "tier0 原生解析器接受该文本"
                                : "tier0 原生解析器拒绝（错误串见 EngineError）——完整建树需 CKV3Arena，见 Tier0Kv3Service 头注"
                        };
                    }
                    finally { Marshal.FreeHGlobal(textPtr); Marshal.FreeHGlobal(pathPtr); }
                }
                finally { Marshal.FreeHGlobal(err); }
            }
            finally { Marshal.FreeHGlobal(kv); }
            return res;
        }
        catch (Exception ex)
        {
            return res with { Detail = ex.GetType().Name + ": " + ex.Message };
        }
        finally { keep?.Dispose(); }
    }


    private static IntPtr Utf8(string s)
    {
        var b = Encoding.UTF8.GetBytes(s + "\0");
        var p = Marshal.AllocHGlobal(b.Length);
        for (int i = 0; i < b.Length; i++) Marshal.WriteByte(p, i, b[i]);
        return p;
    }

    private static string PtrToUtf8(IntPtr p)
    {
        if (p == IntPtr.Zero) return "";
        int i = 0; while (Marshal.ReadByte(p, i) != 0) i++;
        var buf = new byte[i];
        for (int j = 0; j < i; j++) buf[j] = Marshal.ReadByte(p, j);
        return Encoding.UTF8.GetString(buf);
    }

    /// <summary>CUtlString 布局：首 qword = char*。</summary>
    private static string ReadCUtlString(IntPtr strBuf)
    {
        var p = Marshal.ReadInt64(strBuf, 0);
        return p == 0 ? "" : PtrToUtf8(new IntPtr(p));
    }
}
