// ============================================================================
// SDK.Tier0.Kv3NativeArena — tier0 原生 KV3 建链（t21 打通；t22 按 CS2Fixes 范式重构）
//
// 完整链路：
//   1) Functions.Initialize(gameBinDir)  —— 集中解析全部函数（签名 + 导出）
//   2) 构造 CKV3Arena（零填充 + 字段写入）
//   3) Functions.SymTabPoolInit(arena+4352, 2048, 0x200000, 0)  —— 内嵌符号表 bucket 初始化
//   4) 建 253 节点空闲链 + 取 node0 + 写头
//   5) Functions.LoadKv3Arena(arena, &err, &buf, &fmt, pathId, 0) -> true
//   6) root = arena+120 -> Functions.SaveKv3AsJson(root, &out, &err) -> JSON
//
// t22 重构要点（CS2Fixes 范式）：
//   * 消灭裸偏移读写：arena / CUtlBuffer / CUtlString / KV3ID_t 全部改为
//     [StructLayout(LayoutKind.Explicit)] 结构体（显式 FieldOffset），
//     用 Marshal.PtrToStructure / StructureToPtr 访问，可读可校验。
//   * 函数调用全部走 Functions.cs 的强类型委托，不再 ResolveDelegateAtRva 散落各处。
//   * 地址解析集中到 Functions.Initialize，未命中即 fail-fast。
// ============================================================================
using SDK.Native;

namespace SDK.Tier0;

/// <summary>tier0 原生 KV3 加载结果。</summary>
public sealed class NativeKv3Result
{
    public bool Ok { get; init; }
    public string Json { get; init; } = "";
    public string EngineError { get; init; } = "";
    public string Detail { get; init; } = "";
}

// ---------------------------------------------------------------------------
// ABI 结构体（t22：以显式 FieldOffset 取代裸偏移）
// ---------------------------------------------------------------------------

/// <summary>
/// CUtlBuffer（LoadKV3 只读前 40 字节内的 4 个字段）。
/// 布局（t21 实证）：+4 flags、+8 pBase、+16 起始偏移、+32 结束偏移。
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 40)]
public unsafe struct CUtlBuffer
{
    [FieldOffset(0)] public int _pad0;
    [FieldOffset(4)] public uint Flags;        // 低 31 位非零才用 pBase；引擎自建 (len+1)|0x80000000
    [FieldOffset(8)] public byte* pBase;       // 文本指针
    [FieldOffset(16)] public int nStart;       // 起始偏移
    [FieldOffset(24)] public int _pad24;
    [FieldOffset(32)] public int nEnd;         // 结束偏移
}

/// <summary>
/// CUtlString（Source 2 布局：首 qword = char*，SetDirect 直接指向字面量/缓冲）。
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
public unsafe struct CUtlString
{
    [FieldOffset(0)] public byte* pString;
    [FieldOffset(8)] public long _pad8;
}

/// <summary>
/// KV3ID_t（24 B，t21 从 resourcecompiler 的 off_182742108 提取）：
///   +0x00 const char* pName（"generic"）
///   +0x08 GUID format（7412167c-06e9-4698-aff2-e63eb59037e7）
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
public unsafe struct KV3ID_t
{
    [FieldOffset(0)] public byte* pName;
    [FieldOffset(8)] public uint Data1;
    [FieldOffset(12)] public ushort Data2;
    [FieldOffset(14)] public ushort Data3;
    [FieldOffset(16)] public fixed byte Data4[8];
}

public static class Kv3NativeArena
{
    /// <summary>节点容量（参考构造 sub_181EAA400 写死 253）。</summary>
    public const int NodeCapacity = 253;

    /// <summary>CKV3Arena 需要的缓冲大小（参考构造用 4608 B 栈缓冲；我们用堆缓冲给足余量）。</summary>
    public const int ArenaSize = 64 << 20;

    // CKV3Arena 关键字段偏移（t20/t21 反编译 + 探针实证；集中在此便于核对）
    private const int OFF_SELF = 0;          // 自指针
    private const int OFF_UTLBUFFER = 8;     // CUtlBuffer
    private const int OFF_SELF2 = 72;        // 自指针（node0 的 arena 槽）
    private const int OFF_FREELIST = 80;     // 空闲链头
    private const int OFF_NODECAP = 88;      // DWORD 节点容量
    private const int OFF_NODEUSED = 92;     // DWORD 已用节点数
    private const int OFF_F96 = 96;
    private const int OFF_F104 = 104;
    private const int OFF_F112 = 112;
    private const int OFF_FREEBASE = 4168;  // 空闲链数组起点（16 B stride）
    private const int OFF_F4312 = 4312;
    private const int OFF_F4320 = 4320;      // DWORD 32
    private const int OFF_F4324 = 4324;
    private const int OFF_F4328 = 4328;
    private const int OFF_F4336 = 4336;
    private const int OFF_F4352 = 4352;      // 内嵌符号表 bucket 数组
    private const int OFF_F4360 = 4360;
    private const int OFF_F4368 = 4368;
    private const int OFF_F4381 = 4381;
    private const int OFF_F4384 = 4384;      // DWORD 0x7FFFFFFE
    private const int OFF_F4388 = 4388;      // BYTE 1
    private const int OFF_F4392 = 4392;
    private const int OFF_FLAGS = 4400;      // bit2 = has root
    private const int OFF_TAIL0 = 4408;      // ..4488 全零
    private const int OFF_TAIL_END = 4488;
    private const int NODE_STRIDE = 16;

    /// <summary>
    /// 用 tier0 原生 API 解析 KV3 文本，返回 JSON。
    /// </summary>
    public static unsafe NativeKv3Result Load(string gameBinDir, string kv3Text)
    {
        Functions.EnsureInitialized();

        nint arena = 0, err = 0, outStr = 0, err2 = 0, textPtr = 0, fmtName = 0, pathId = 0, bufMem = 0, fmtMem = 0;
        try
        {
            arena = Marshal.AllocHGlobal(ArenaSize);
            for (int i = 0; i < ArenaSize; i++) Marshal.WriteByte(arena, i, 0);
            long A = arena;

            // ---- 内层字段（resourcecompiler sub_181EAA400 逐条移植）----
            Marshal.WriteInt64(arena, OFF_SELF, A);
            Marshal.WriteInt64(arena, OFF_SELF2, A);

            // CUtlBuffer(0,0,0) —— 走强类型委托，结果用结构体落回内存
            // ★ t24 修复：分配 256 B 而非 40 B——原生 CUtlBuffer 大于 40 B，
            //    其构造函数要写 +0x28/+0x30 处的函数指针（CCallStackStatsGatherer/PutOverflow），
            //    40 B 分配会越界写穿 CRT 堆块头 ⇒ 间歇性堆损坏（0xC0000374/0xC0000005，崩率 ~35%）。
            bufMem = Marshal.AllocHGlobal(256);
            for (int i = 0; i < 256; i++) Marshal.WriteByte(bufMem, i, 0);
            try { Functions.CUtlBufferCtor(bufMem, 0, 0, 0); }
            catch { /* ctor 失败则保持零初始化，LoadKV3 只会读到空缓冲 */ }
            var bufStruct = Marshal.PtrToStructure<CUtlBuffer>(bufMem);

            for (int k = OFF_FREEBASE; k <= 4304; k += 8) Marshal.WriteInt64(arena, k, 0);
            Marshal.WriteInt32(arena, OFF_F4312, 0);
            Marshal.WriteInt32(arena, OFF_F4320, 32);
            Marshal.WriteByte(arena, OFF_F4324, 0);
            Marshal.WriteInt64(arena, OFF_F4328, 0);
            Marshal.WriteInt64(arena, OFF_F4336, 0);
            Marshal.WriteInt64(arena, OFF_F4360, 0);
            Marshal.WriteInt64(arena, OFF_F4368, 0);
            Marshal.WriteByte(arena, OFF_F4381, 0);
            Marshal.WriteInt32(arena, OFF_F4384, unchecked((int)0x7FFFFFFE));
            Marshal.WriteByte(arena, OFF_F4388, 1);
            Marshal.WriteInt32(arena, OFF_F4392, 0);
            for (int k = OFF_TAIL0; k <= OFF_TAIL_END; k += 8) Marshal.WriteInt64(arena, k, 0);

            // ---- ★关键★ arena 内嵌符号表 bucket 数组初始化 ----
            try { Functions.SymTabPoolInit((nint)(A + OFF_F4352), 2048, 0x200000, 0); }
            catch (Exception ex) { return new NativeKv3Result { Detail = "SymTabPoolInit 抛出: " + ex.Message }; }

            Marshal.WriteInt32(arena, OFF_NODECAP, NodeCapacity);
            Marshal.WriteInt64(arena, OFF_F96, 0);
            Marshal.WriteInt64(arena, OFF_F104, 0);
            Marshal.WriteInt64(arena, OFF_F112, 0);

            // 空闲链：从 arena+4168 起向下 -16，共 NodeCapacity 次
            long v5 = 0; int v6 = 0; long v7 = A + OFF_FREEBASE;
            do
            {
                v7 -= NODE_STRIDE; v6++;
                Marshal.WriteInt64((nint)v7, v5);
                v5 = v7;
            }
            while (v6 < NodeCapacity);
            Marshal.WriteInt64(arena, OFF_FREELIST, v7);
            Marshal.WriteInt32(arena, OFF_NODEUSED, 0);

            long v8 = Marshal.ReadInt64(arena, OFF_FREEBASE);
            if (v8 != 0) Marshal.WriteInt64((nint)(v8 + 32), A + OFF_SELF2);
            else Marshal.WriteInt64(arena, OFF_FREEBASE + 8, A + OFF_SELF2);
            Marshal.WriteInt64(arena, OFF_F104, 0);
            Marshal.WriteInt64(arena, OFF_F96, Marshal.ReadInt64(arena, OFF_FREEBASE));
            Marshal.WriteInt64(arena, OFF_FREEBASE, A + OFF_SELF2);

            // ---- 外层收尾：取 node0 + 写头 ----
            Marshal.WriteByte(arena, OFF_FLAGS, (byte)(Marshal.ReadByte(arena, OFF_FLAGS) | 4));
            long node = Marshal.ReadInt64(arena, OFF_FREELIST);
            Marshal.WriteInt32(arena, OFF_NODEUSED, Marshal.ReadInt32(arena, OFF_NODEUSED) + 1);
            Marshal.WriteInt64(arena, OFF_FREELIST, Marshal.ReadInt64((nint)node));
            Marshal.WriteInt64((nint)(node + 8), 0);
            int N = (int)((node - A - 120) >> 4);
            Marshal.WriteInt64((nint)node, ((long)(uint)N << 18) | 0x2404L);

            nint root = (nint)(A + 120);

            // ---- CUtlBuffer 指向文本 ----
            err = Marshal.AllocHGlobal(4096);
            for (int i = 0; i < 4096; i++) Marshal.WriteByte(err, i, 0);
            textPtr = Utf8(kv3Text);
            bufStruct.Flags = (uint)(Encoding.UTF8.GetByteCount(kv3Text) + 1) | 0x80000000u;
            bufStruct.pBase = (byte*)textPtr;
            bufStruct.nStart = 0;
            bufStruct.nEnd = Encoding.UTF8.GetByteCount(kv3Text);
            nint buf = Marshal.AllocHGlobal(256);
            bufMem = buf;
            Marshal.StructureToPtr(bufStruct, buf, false);

            // ---- KV3ID_t ----
            fmtMem = Marshal.AllocHGlobal(24);
            for (int i = 0; i < 24; i++) Marshal.WriteByte(fmtMem, i, 0);
            fmtName = Utf8("generic");
            var fmt = new KV3ID_t
            {
                pName = (byte*)fmtName,
                Data1 = 0x7412167Cu,
                Data2 = 0x06E9,
                Data3 = 0x4698,
            };
            fmt.Data4[0] = 0xAF; fmt.Data4[1] = 0xF2; fmt.Data4[2] = 0xE6; fmt.Data4[3] = 0x3E;
            fmt.Data4[4] = 0xB5; fmt.Data4[5] = 0x90; fmt.Data4[6] = 0x37; fmt.Data4[7] = 0xE7;
            Marshal.StructureToPtr(fmt, fmtMem, false);

            pathId = Utf8("EXECUTABLE_PATH");

            bool ok;
            try { ok = Functions.LoadKv3Arena(arena, err, buf, fmtMem, pathId, 0); }
            catch (Exception ex) { return new NativeKv3Result { Detail = "LoadKV3 抛出: " + ex.GetType().Name + ": " + ex.Message }; }

            string engineErr = ReadCUtlString(err);
            if (!ok) return new NativeKv3Result { Ok = false, EngineError = engineErr, Detail = "LoadKV3 返回 false" };

            outStr = Marshal.AllocHGlobal(1 << 20);
            err2 = Marshal.AllocHGlobal(1 << 20);
            for (int i = 0; i < (1 << 20); i++) { Marshal.WriteByte(outStr, i, 0); Marshal.WriteByte(err2, i, 0); }

            bool sok;
            try { sok = Functions.SaveKv3AsJson(root, outStr, err2); }
            catch (Exception ex) { return new NativeKv3Result { Detail = "SaveKV3AsJSON 抛出: " + ex.GetType().Name + ": " + ex.Message }; }
            if (!sok) return new NativeKv3Result { Ok = false, EngineError = ReadCUtlString(err2), Detail = "SaveKV3AsJSON 返回 false" };

            string json = ReadCUtlString(outStr);
            if (string.IsNullOrEmpty(json) || json == "(null-ptr)")
            {
                string alt = ReadCUtlString(err2);
                if (!string.IsNullOrEmpty(alt) && alt != "(null-ptr)" && alt.TrimStart().StartsWith('{')) json = alt;
            }
            if (string.IsNullOrEmpty(json) || json == "(null-ptr)")
                return new NativeKv3Result { Ok = false, Detail = "SaveKV3AsJSON 未产出 JSON" };

            return new NativeKv3Result { Ok = true, Json = json, EngineError = engineErr, Detail = "原生链 OK, JSON " + json.Length + " B" };
        }
        finally
        {
            if (arena != 0) Marshal.FreeHGlobal(arena);
            if (err != 0) Marshal.FreeHGlobal(err);
            if (bufMem != 0) Marshal.FreeHGlobal(bufMem);
            if (outStr != 0) Marshal.FreeHGlobal(outStr);
            if (err2 != 0) Marshal.FreeHGlobal(err2);
            if (textPtr != 0) Marshal.FreeHGlobal(textPtr);
            if (fmtMem != 0) Marshal.FreeHGlobal(fmtMem);
            if (fmtName != 0) Marshal.FreeHGlobal(fmtName);
            if (pathId != 0) Marshal.FreeHGlobal(pathId);
        }
    }

    /// <summary>
    /// KV1 / KV3 文本 → JSON（t24：VmatGenerator 读 .vmat 用）。
    /// LoadKV3FromKV3OrKV1（按内容自动判别 KV3/KV1）→ SaveKV3AsJSON。
    /// ★ 实测（t24）：KeyValues3 对象路径下 JSON 落在 SaveKV3AsJSON 的**第 3 个**
    ///   参数（arena 路径在第 2 个）——故两个参数都读，取有内容者。
    /// </summary>
    public static unsafe NativeKv3Result Kv1ToJson(string text)
    {
        Functions.EnsureInitialized();
        nint kv = 0, err = 0, outA = 0, outB = 0, textPtr = 0, fmtName = 0, pathId = 0, bufMem = 0, fmtMem = 0, buf = 0;
        try
        {
            kv = Marshal.AllocHGlobal(512);
            for (int i = 0; i < 512; i++) Marshal.WriteByte(kv, i, 0);
            Marshal.WriteByte(kv, 0, 1);
            err = Marshal.AllocHGlobal(256);
            for (int i = 0; i < 256; i++) Marshal.WriteByte(err, i, 0);
            bufMem = Marshal.AllocHGlobal(256);
            for (int i = 0; i < 256; i++) Marshal.WriteByte(bufMem, i, 0);
            try { Functions.CUtlBufferCtor(bufMem, 0, 0, 0); } catch { }
            var bufStruct = Marshal.PtrToStructure<CUtlBuffer>(bufMem);
            textPtr = Utf8(text);
            bufStruct.Flags = (uint)(Encoding.UTF8.GetByteCount(text) + 1) | 0x80000000u;
            bufStruct.pBase = (byte*)textPtr;
            bufStruct.nStart = 0;
            bufStruct.nEnd = Encoding.UTF8.GetByteCount(text);
            buf = Marshal.AllocHGlobal(256);
            for (int i = 0; i < 256; i++) Marshal.WriteByte(buf, i, 0);
            Marshal.StructureToPtr(bufStruct, buf, false);
            fmtMem = Marshal.AllocHGlobal(24);
            for (int i = 0; i < 24; i++) Marshal.WriteByte(fmtMem, i, 0);
            fmtName = Utf8("generic");
            var fmt = new KV3ID_t { pName = (byte*)fmtName, Data1 = 0x7412167Cu, Data2 = 0x06E9, Data3 = 0x4698 };
            fmt.Data4[0] = 0xAF; fmt.Data4[1] = 0xF2; fmt.Data4[2] = 0xE6; fmt.Data4[3] = 0x3E;
            fmt.Data4[4] = 0xB5; fmt.Data4[5] = 0x90; fmt.Data4[6] = 0x37; fmt.Data4[7] = 0xE7;
            Marshal.StructureToPtr(fmt, fmtMem, false);
            pathId = Utf8("GAME");
            bool ok;
            try { ok = Functions.LoadKv3FromKv3OrKv1(kv, err, buf, fmtMem, pathId, 0); }
            catch (Exception ex) { return new NativeKv3Result { Ok = false, Detail = "LoadKV3FromKV3OrKV1 异常: " + ex.Message }; }
            if (!ok) return new NativeKv3Result { Ok = false, Detail = "引擎拒绝: " + ReadCUtlString(err) };

            outA = Marshal.AllocHGlobal(1 << 20);
            outB = Marshal.AllocHGlobal(1 << 20);
            for (int i = 0; i < (1 << 20); i++) { Marshal.WriteByte(outA, i, 0); Marshal.WriteByte(outB, i, 0); }
            bool sok;
            try { sok = Functions.SaveKv3AsJson(kv, outA, outB); }
            catch (Exception ex) { return new NativeKv3Result { Ok = false, Detail = "SaveKV3AsJSON 异常: " + ex.Message }; }
            if (!sok) return new NativeKv3Result { Ok = false, Detail = "JSON 产出失败" };
            // 两个输出参数都读，取有内容者（实测 JSON 落在第 3 参数）
            var ja = ReadCUtlString(outA);
            var jb = ReadCUtlString(outB);
            var json = ja.Length > 0 && ja.TrimStart().StartsWith('{') ? ja : jb;
            if (json.Length == 0) return new NativeKv3Result { Ok = false, Detail = "JSON 为空（引擎未产出）" };
            return new NativeKv3Result { Ok = true, Json = json, Detail = "kv1->json, len=" + json.Length };
        }
        finally
        {
            foreach (var h in new[] { kv, err, outA, outB, textPtr, fmtName, pathId, bufMem, fmtMem, buf })
                if (h != 0) Marshal.FreeHGlobal(h);
        }
    }
    /// <summary>
    /// JSON → KV1 文本（t24：VmatGenerator 写出 .vmat 用）。
    /// LoadKV3FromJSON 建树 → SaveKV3AsKV1Text 写 KV1（引擎规范格式）。
    /// </summary>
    public static unsafe NativeKv3Result Kv1FromJson(string json, int escapeBehavior = 0)
    {
        Functions.EnsureInitialized();
        nint kv = 0, err = 0, err2 = 0, jsonPtr = 0, pathId = 0, outBuf = 0;
        try
        {
            kv = Marshal.AllocHGlobal(256);
            for (int i = 0; i < 256; i++) Marshal.WriteByte(kv, i, 0);
            Marshal.WriteByte(kv, 0, 1);
            err = Marshal.AllocHGlobal(256);
            for (int i = 0; i < 256; i++) Marshal.WriteByte(err, i, 0);
            err2 = Marshal.AllocHGlobal(256);
            for (int i = 0; i < 256; i++) Marshal.WriteByte(err2, i, 0);
            jsonPtr = Utf8(json);
            pathId = Utf8("GAME");
            bool ok;
            try { ok = Functions.LoadKv3FromJson(kv, err, jsonPtr, pathId, 0); }
            catch (Exception ex) { return new NativeKv3Result { Ok = false, Detail = "LoadKV3FromJSON 异常: " + ex.Message }; }
            if (!ok) return new NativeKv3Result { Ok = false, Detail = "JSON 建树失败: " + ReadCUtlString(err) };

            outBuf = Marshal.AllocHGlobal(256);
            for (int i = 0; i < 256; i++) Marshal.WriteByte(outBuf, i, 0);
            try { Functions.CUtlBufferCtor(outBuf, 4096, 0, 0); } catch { }
            bool sok;
            try { sok = Functions.SaveKv3AsKv1Text(kv, err2, outBuf, escapeBehavior); }
            catch (Exception ex) { return new NativeKv3Result { Ok = false, Detail = "SaveKV3AsKV1Text 异常: " + ex.Message }; }
            if (!sok) return new NativeKv3Result { Ok = false, Detail = "写出失败: " + ReadCUtlString(err2) };
            long pBase = Marshal.ReadInt64(outBuf, 8);
            int nStart = Marshal.ReadInt32(outBuf, 16);
            int nEnd = Marshal.ReadInt32(outBuf, 32);
            int len = nEnd - nStart;
            if (pBase == 0 || len <= 0) return new NativeKv3Result { Ok = false, Detail = "输出缓冲为空" };
            var bytes = new byte[len];
            for (int i = 0; i < len; i++) bytes[i] = Marshal.ReadByte((nint)pBase, i);
            return new NativeKv3Result { Ok = true, Json = Encoding.UTF8.GetString(bytes), Detail = "json->kv1, len=" + len };
        }
        finally
        {
            foreach (var h in new[] { kv, err, err2, jsonPtr, pathId, outBuf })
                if (h != 0) Marshal.FreeHGlobal(h);
        }
    }
    /// <summary>
    /// KV3 文本 → KV3 **二进制**（t28i：进程内编译 KV3 类资源的正确路径）。
    /// <para>
    /// 原理（IDA 反编译 SaveKV3ToFile=0x124C70 实证）：该函数的序列化段
    /// （sub_1800C7360）是 **tier0 内部函数**，只有最后「写文件」走了引擎文件系统服务
    /// （qword_1803A9608 vtable+208）。因此序列化可进程内完成，写盘由调用方自己做。
    /// </para>
    /// <para>
    /// 适用：编译产物 = KV3 二进制的资源（.vnmgraph / .vdata 等）。
    /// 不适用：.vmdl 等需要 ModelDoc/mesh 处理的资源（那些仍须引擎服务图）。
    /// </para>
    /// <para>
    /// ★ 栈：二进制序列化器对深层树递归且栈帧大（~0x400 B/层），1 MB 级动画图在主线程
    /// 会爆栈（0xC0000005）。调用方应放在大栈线程（≥16 MB）——CompileService 已如此做。
    /// </para>
    /// </summary>
    /// <param name="kv3Text">KV3 文本源（须带 kv3 头）。</param>
    /// <param name="binaryFormatGuid">输出二进制格式 GUID；null = 当前版本 v5（6eb109e6-6b85-4583-a312-703a6e04068c）。</param>
    public static unsafe NativeKv3Result CompileKv3Binary(string kv3Text, byte[]? binaryFormatGuid = null)
    {
        Functions.EnsureInitialized();

        // 当前 KV3 二进制格式版本（sub_1800BE340 白名单第 5 项，最新）
        binaryFormatGuid ??= new byte[] { 0xE6, 0x09, 0xB1, 0x6E, 0x85, 0x6B, 0x83, 0x45, 0xA3, 0x12, 0x70, 0x3A, 0x6E, 0x04, 0x06, 0x8C };

        nint kv = 0, err = 0, textPtr = 0, fmtName = 0, pathId = 0, bufMem = 0, fmtMem = 0, buf = 0, outBuf = 0, serErr = 0;
        try
        {
            // ---- 1) 解析：KV3/KV1 文本 → KeyValues3 树（bit0=1 惰性对象）----
            kv = Marshal.AllocHGlobal(512);
            for (int i = 0; i < 512; i++) Marshal.WriteByte(kv, i, 0);
            Marshal.WriteByte(kv, 0, 1);
            err = Marshal.AllocHGlobal(256);
            for (int i = 0; i < 256; i++) Marshal.WriteByte(err, i, 0);
            bufMem = Marshal.AllocHGlobal(256);
            for (int i = 0; i < 256; i++) Marshal.WriteByte(bufMem, i, 0);
            try { Functions.CUtlBufferCtor(bufMem, 0, 0, 0); } catch { }
            var bufStruct = Marshal.PtrToStructure<CUtlBuffer>(bufMem);
            textPtr = Utf8(kv3Text);
            bufStruct.Flags = (uint)(Encoding.UTF8.GetByteCount(kv3Text) + 1) | 0x80000000u;
            bufStruct.pBase = (byte*)textPtr;
            bufStruct.nStart = 0;
            bufStruct.nEnd = Encoding.UTF8.GetByteCount(kv3Text);
            buf = Marshal.AllocHGlobal(256);
            for (int i = 0; i < 256; i++) Marshal.WriteByte(buf, i, 0);
            Marshal.StructureToPtr(bufStruct, buf, false);

            fmtMem = Marshal.AllocHGlobal(24);
            for (int i = 0; i < 24; i++) Marshal.WriteByte(fmtMem, i, 0);
            fmtName = Utf8("generic");
            var srcFmt = new KV3ID_t { pName = (byte*)fmtName, Data1 = 0x7412167Cu, Data2 = 0x06E9, Data3 = 0x4698 };
            srcFmt.Data4[0] = 0xAF; srcFmt.Data4[1] = 0xF2; srcFmt.Data4[2] = 0xE6; srcFmt.Data4[3] = 0x3E;
            srcFmt.Data4[4] = 0xB5; srcFmt.Data4[5] = 0x90; srcFmt.Data4[6] = 0x37; srcFmt.Data4[7] = 0xE7;
            Marshal.StructureToPtr(srcFmt, fmtMem, false);
            pathId = Utf8("GAME");

            if (!Functions.LoadKv3FromKv3OrKv1(kv, err, buf, fmtMem, pathId, 0))
                return new NativeKv3Result { Ok = false, Detail = "KV3 解析失败: " + ReadCUtlString(err) };

            // ---- 2) 二进制格式 fmt（输出 GUID）----
            var outFmtMem = Marshal.AllocHGlobal(24);
            for (int i = 0; i < 24; i++) Marshal.WriteByte(outFmtMem, i, 0);
            var outFmt = new KV3ID_t { pName = (byte*)fmtName };
            outFmt.Data1 = BitConverter.ToUInt32(binaryFormatGuid, 0);
            outFmt.Data2 = BitConverter.ToUInt16(binaryFormatGuid, 4);
            outFmt.Data3 = BitConverter.ToUInt16(binaryFormatGuid, 6);
            for (int i = 0; i < 8; i++) outFmt.Data4[i] = binaryFormatGuid[8 + i];
            Marshal.StructureToPtr(outFmt, outFmtMem, false);

            // ---- 3) 序列化（tier0 内部二进制写入器；经 Module 按 RVA 取）----
            using var m = Module.Load(Path.Combine(Functions.GameBinDir, Signatures.Tier0Dll));
            var ser = m.ResolveDelegateAtRva<SaveKv3BinaryFn>(RvaSaveKv3Binary);
            outBuf = Marshal.AllocHGlobal(256);
            for (int i = 0; i < 256; i++) Marshal.WriteByte(outBuf, i, 0);
            try { Functions.CUtlBufferCtor(outBuf, 1 << 20, 0, 0); } catch { }
            serErr = Marshal.AllocHGlobal(256);
            for (int i = 0; i < 256; i++) Marshal.WriteByte(serErr, i, 0);

            if (ser(outFmtMem, outFmtMem, kv, serErr, outBuf, 0) == 0)
                return new NativeKv3Result { Ok = false, Detail = "二进制序列化失败: " + ReadCUtlString(serErr) };

            // ---- 4) 取输出（pBase + nPut）----
            long pBase = Marshal.ReadInt64(outBuf, 8);
            int len = Marshal.ReadInt32(outBuf, 32) - Marshal.ReadInt32(outBuf, 16);
            if (pBase == 0 || len <= 0) return new NativeKv3Result { Ok = false, Detail = "序列化输出为空" };
            var bytes = new byte[len];
            for (int i = 0; i < len; i++) bytes[i] = Marshal.ReadByte((nint)pBase, i);
            return new NativeKv3Result { Ok = true, Json = Convert.ToBase64String(bytes), Detail = "kv3 binary, " + len + " B" };
        }
        finally
        {
            foreach (var h in new[] { kv, err, textPtr, fmtName, pathId, bufMem, fmtMem, buf, outBuf, serErr })
                if (h != 0) Marshal.FreeHGlobal(h);
        }
    }

    // SaveKV3ToFile 的二进制序列化段（sub_1800C7360）：(fmt, fmt2, root, err, buf, flags)
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate byte SaveKv3BinaryFn(nint fmt, nint fmt2, nint root, nint serr, nint buf, int flags);

    /// <summary>二进制序列化器 RVA（tier0 sub_1800C7360；随游戏更新需重定位）。</summary>
    private const int RvaSaveKv3Binary = 0xC7360;
    private static nint Utf8(string s)
    {
        var b = Encoding.UTF8.GetBytes(s + "\0");
        var p = Marshal.AllocHGlobal(b.Length);
        for (int i = 0; i < b.Length; i++) Marshal.WriteByte(p, i, b[i]);
        return p;
    }

    private static string ReadCUtlString(nint strBuf)
    {
        long p = Marshal.ReadInt64(strBuf, 0);
        if (p == 0) return "(null-ptr)";
        var ptr = (nint)p;
        int i = 0;
        while (Marshal.ReadByte(ptr, i) != 0) i++;
        var buf = new byte[i];
        for (int j = 0; j < i; j++) buf[j] = Marshal.ReadByte(ptr, j);
        return Encoding.UTF8.GetString(buf);
    }
}
