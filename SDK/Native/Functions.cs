// ============================================================================
// SDK.Native.Functions — addresses.h/cpp 等价物（CS2Fixes 范式）
//
// 每个被调函数：
//   ① 一条**强类型 delegate 声明**（真实 C++ 签名，不用 void*/裸 nint 冒充）
//   ② 一个静态解析结果字段
// 全部绑定集中在单一 Initialize(gameBinDir)：
//   * 导出函数走 GetProcAddress
//   * 内部函数走 Signatures.cs 的命名签名（FindUniqueSignature，要求唯一命中）
//   * 多副本函数走「定位调用方 + 提取 E8 rel32」
//   * 任一未命中/多命中 ⇒ 抛异常（fail-fast），不静默用错地址
//   * 逐条记录解析地址到 ResolveLog，供自检/日志面板复核
// ============================================================================
namespace SDK.Native;

/// <summary>未找到签名/导出时抛出（CS2Fixes 的 fail-fast 语义）。</summary>
public sealed class FunctionResolveException(string name, string detail) : Exception($"函数 [{name}] 解析失败：{detail}")
{
    public string FunctionName { get; } = name;
}

public static class Functions
{
    // ==================== 强类型委托声明（真实签名） ====================

    // ?LoadKV3@@YA_NPEAVCKV3Arena@@PEAVCUtlString@@PEAVCUtlBuffer@@AEBUKV3ID_t@@PEBDI@Z
    // bool LoadKV3(CKV3Arena* arena, CUtlString* pError, CUtlBuffer* pBuffer,
    //             const KV3ID_t& fmt, const char* pPathId, unsigned int nFlags)
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate bool LoadKv3ArenaFn(nint arena, nint pError, nint pBuffer, nint fmt, nint pPathId, uint nFlags);

    // ?SaveKV3AsJSON@@YA_NPEBVKeyValues3@@PEAVCUtlString@@1@Z
    // bool SaveKV3AsJSON(const KeyValues3* pRoot, CUtlString* pOutJson, CUtlString* pError)
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate bool SaveKv3AsJsonFn(nint pRoot, nint pOutJson, nint pError);

    // ?LoadKV3Text_NoHeader@@YA_NPEAVKeyValues3@@PEAVCUtlString@@PEBDAEBUKV3ID_t@@2I@Z
    // bool LoadKV3Text_NoHeader(KeyValues3* pRoot, CUtlString* pError,
    //                            const char* pText, const KV3ID_t& fmt, const char* pPathId, unsigned int nFlags)
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate bool LoadKv3TextNoHeaderFn(nint pRoot, nint pError, nint pText, nint fmt, nint pPathId, uint nFlags);

    // ??0CUtlBuffer@@QEAA@HHW4BufferFlags_t@0@@Z
    // CUtlBuffer::CUtlBuffer(int nGrowSize, int nPutFlags, BufferFlags_t flags)
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate nint CUtlBufferCtorFn(nint self, int nGrowSize, int nPutFlags, int flags);

    // CMemAllocSystemInitialize()  —— 建立块分配器 size-class 表
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void MemAllocSystemInitializeFn();

    // resourcecompiler.dll sub_1800C84F0(void* pSymTabBuckets, int nInitialCapacity,
    //                                       int nGrowSize, char bSomething)
    // —— arena 内嵌符号表的 bucket 数组初始化（t21 关键缺失项）
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate nint SymTabPoolInitFn(nint self, int nCapacity, int nGrowSize, byte bFlag);

    // ?LoadKV3FromKV3OrKV1@@YA_NPEAVKeyValues3@@PEAVCUtlString@@PEAVCUtlBuffer@@AEBUKV3ID_t@@PEBDI@Z
    // bool LoadKV3FromKV3OrKV1(KeyValues3* kv, CUtlString* pError, CUtlBuffer* pBuffer,
    //                           const KV3ID_t& fmt, const char* pPathId, uint nFlags)
    // —— 按内容自动判别 KV3 / KV1 读取（VmatGenerator 读 .vmat 用，替代三方 KV 库）
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate bool LoadKv3FromKv3OrKv1Fn(nint kv, nint pError, nint pBuffer, nint fmt, nint pPathId, uint nFlags);

    // ?SaveKV3AsKV1Text@@YA_NPEBVKeyValues3@@PEAVCUtlString@@PEAVCUtlBuffer@@W4KV1TextEscapeBehavior_t@@@Z
    // bool SaveKV3AsKV1Text(const KeyValues3* pRoot, CUtlString* pOut, CUtlBuffer* pBuf,
    //                       KV1TextEscapeBehavior_t eEscape)
    // —— 写 KeyValues1 文本（VmatGenerator 写 .vmat 用，替代三方 KV 库）
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate bool SaveKv3AsKv1TextFn(nint pRoot, nint pOut, nint pBuf, int eEscape);

    // ?LoadKV3FromJSON@@YA_NPEAVKeyValues3@@PEAVCUtlString@@PEBD2I@Z
    // bool LoadKV3FromJSON(KeyValues3* kv, CUtlString* pError, const char* pJson, const char* pPathId, uint nFlags)
    // —— 由 JSON 建 KeyValues3 树（VmatGenerator 写 .vmat 的上半段）
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate bool LoadKv3FromJsonFn(nint kv, nint pError, nint pJson, nint pPathId, uint nFlags);

    // ?LoadKV3FromKV1File@@YA_NPEAVKeyValues3@@PEAVCUtlString@@PEBD2W4KV1TextEscapeBehavior_t@@I@Z
    // bool LoadKV3FromKV1File(CKV3Arena* arena, CUtlString* pError, const char* pFileName,
    //                         const char* pPathId, KV1TextEscapeBehavior_t eEscape, uint nFlags)
    // —— arena 版 KV1 文件加载（VmatGenerator 读 .vmat 用，与 LoadKV3 同构的稳健路径）
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate bool LoadKv3FromKv1FileFn(nint kv, nint pError, nint pFileName, nint pPathId, int eEscape, uint nFlags);

    // EarlyInit_Tier0() —— tier0 运行时早期初始化（t21 探针与 CMemAllocSystemInitialize 成对调用；
    //                        t22 重构一度漏掉 ⇒ 编译链间歇堆损坏，t24 补回）
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void EarlyInitTier0Fn();

    // CreateInterface(const char* pName, void* pReturnCode)  —— 接口一律走这个
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate nint CreateInterfaceFn(nint pName, nint pReturnCode);

    // ==================== 解析结果字段 ====================

    public static LoadKv3ArenaFn LoadKv3Arena { get; private set; } = null!;
    public static SaveKv3AsJsonFn SaveKv3AsJson { get; private set; } = null!;
    public static LoadKv3TextNoHeaderFn LoadKv3TextNoHeader { get; private set; } = null!;
    public static CUtlBufferCtorFn CUtlBufferCtor { get; private set; } = null!;
    public static MemAllocSystemInitializeFn MemAllocSystemInitialize { get; private set; } = null!;
    public static EarlyInitTier0Fn EarlyInitTier0 { get; private set; } = null!;
    public static SymTabPoolInitFn SymTabPoolInit { get; private set; } = null!;
    public static CreateInterfaceFn CreateInterface { get; private set; } = null!;
    public static LoadKv3FromKv3OrKv1Fn LoadKv3FromKv3OrKv1 { get; private set; } = null!;
    public static SaveKv3AsKv1TextFn SaveKv3AsKv1Text { get; private set; } = null!;
    public static LoadKv3FromJsonFn LoadKv3FromJson { get; private set; } = null!;
    public static LoadKv3FromKv1FileFn LoadKv3FromKv1File { get; private set; } = null!;

    /// <summary>各函数的解析地址（RVA），逐条记录供日志/自检复核。</summary>
    public static List<string> ResolveLog { get; } = [];

    public static bool IsInitialized { get; private set; }

    /// <summary>Initialize 时的游戏 bin 目录（供 SDK 内部按需再加载模块）。</summary>
    public static string GameBinDir { get; private set; } = "";

    // 解析出的关键 RVA（签名解析的统一产物）
    public static int RvaLoadKv3Arena { get; private set; }
    public static int RvaSaveKv3AsJson { get; private set; }
    public static int RvaLoadKv3FromKv3OrKv1 { get; private set; }
    public static int RvaSaveKv3AsKv1Text { get; private set; }
    public static int RvaLoadKv3FromJson { get; private set; }
    public static int RvaCUtlBufferCtor { get; private set; }
    public static int RvaLoadKv3TextNoHeader { get; private set; }
    public static int RvaGenerateResourceFile { get; private set; }
    public static int RvaAssetConfigInit { get; private set; }
    public static int RvaArenaCtor { get; private set; }
    public static int RvaSymTabPoolInit { get; private set; }

    /// <summary>
    /// 集中绑定全部函数。任一失败即抛 FunctionResolveException（fail-fast）。
    /// </summary>
    public static void Initialize(string gameBinDir)
    {
        ResolveLog.Clear();
        IsInitialized = false;

        string tier0Path = Path.Combine(gameBinDir, Signatures.Tier0Dll);
        string rcPath = Path.Combine(gameBinDir, Signatures.ResourceCompilerDll);
        if (!File.Exists(tier0Path)) throw new FunctionResolveException("Initialize", "tier0.dll 不存在: " + tier0Path);
        if (!File.Exists(rcPath)) throw new FunctionResolveException("Initialize", "resourcecompiler.dll 不存在: " + rcPath);

        GameBinDir = gameBinDir;
        using var tier0 = Module.Load(tier0Path);
        using var rc = Module.Load(rcPath);

        // ---- 1) 块分配器 size-class 表（t21：不初始化则第一个键名 interning 即 FATAL）----
        MemAllocSystemInitialize = BindExport<MemAllocSystemInitializeFn>(tier0, "CMemAllocSystemInitialize");
        try { MemAllocSystemInitialize(); Log("CMemAllocSystemInitialize", "tier0", (int)(tier0.GetExport("CMemAllocSystemInitialize").ToInt64() - tier0.Base.ToInt64())); }
        catch (Exception ex) { throw new FunctionResolveException("CMemAllocSystemInitialize", "调用抛出: " + ex.Message); }

        // ---- 1b) tier0 运行时早期初始化（与 CMemAllocSystemInitialize 成对，缺它编译链间歇堆损坏）----
        EarlyInitTier0 = BindExport<EarlyInitTier0Fn>(tier0, "EarlyInit_Tier0");
        try { EarlyInitTier0(); Log("EarlyInit_Tier0", "tier0", (int)(tier0.GetExport("EarlyInit_Tier0").ToInt64() - tier0.Base.ToInt64())); }
        catch (Exception ex) { throw new FunctionResolveException("EarlyInit_Tier0", "调用抛出: " + ex.Message); }

        // ---- 2) 导出函数：GetProcAddress ----
        RvaLoadKv3Arena = tier0.FindUniqueSignature(Signatures.Get("LoadKv3Arena").Name, Signatures.Get("LoadKv3Arena").WindowsPattern);
        LoadKv3Arena = tier0.ResolveDelegateAtRva<LoadKv3ArenaFn>(RvaLoadKv3Arena);
        Log("LoadKv3Arena", "tier0", RvaLoadKv3Arena);
        RvaSaveKv3AsJson = tier0.FindUniqueSignature(Signatures.Get("SaveKv3AsJson").Name, Signatures.Get("SaveKv3AsJson").WindowsPattern);
        SaveKv3AsJson = tier0.ResolveDelegateAtRva<SaveKv3AsJsonFn>(RvaSaveKv3AsJson);
        Log("SaveKv3AsJson", "tier0", RvaSaveKv3AsJson);
        RvaLoadKv3TextNoHeader = tier0.FindUniqueSignature(Signatures.Get("LoadKv3TextNoHeader").Name, Signatures.Get("LoadKv3TextNoHeader").WindowsPattern);
        LoadKv3TextNoHeader = tier0.ResolveDelegateAtRva<LoadKv3TextNoHeaderFn>(RvaLoadKv3TextNoHeader);
        Log("LoadKv3TextNoHeader", "tier0", RvaLoadKv3TextNoHeader);
        RvaCUtlBufferCtor = tier0.FindUniqueSignature(Signatures.Get("CUtlBufferCtor").Name, Signatures.Get("CUtlBufferCtor").WindowsPattern);
        CUtlBufferCtor = tier0.ResolveDelegateAtRva<CUtlBufferCtorFn>(RvaCUtlBufferCtor);
        Log("CUtlBufferCtor", "tier0", RvaCUtlBufferCtor);
        CreateInterface = BindExport<CreateInterfaceFn>(rc, "CreateInterface");
        RvaLoadKv3FromKv3OrKv1 = tier0.FindUniqueSignature(Signatures.Get("LoadKv3FromKv3OrKv1").Name, Signatures.Get("LoadKv3FromKv3OrKv1").WindowsPattern);
        LoadKv3FromKv3OrKv1 = tier0.ResolveDelegateAtRva<LoadKv3FromKv3OrKv1Fn>(RvaLoadKv3FromKv3OrKv1);
        Log("LoadKv3FromKv3OrKv1", "tier0", RvaLoadKv3FromKv3OrKv1);
        RvaSaveKv3AsKv1Text = tier0.FindUniqueSignature(Signatures.Get("SaveKv3AsKv1Text").Name, Signatures.Get("SaveKv3AsKv1Text").WindowsPattern);
        SaveKv3AsKv1Text = tier0.ResolveDelegateAtRva<SaveKv3AsKv1TextFn>(RvaSaveKv3AsKv1Text);
        Log("SaveKv3AsKv1Text", "tier0", RvaSaveKv3AsKv1Text);
        RvaLoadKv3FromJson = tier0.FindUniqueSignature(Signatures.Get("LoadKv3FromJson").Name, Signatures.Get("LoadKv3FromJson").WindowsPattern);
        LoadKv3FromJson = tier0.ResolveDelegateAtRva<LoadKv3FromJsonFn>(RvaLoadKv3FromJson);
        Log("LoadKv3FromJson", "tier0", RvaLoadKv3FromJson);
        LoadKv3FromKv1File = BindExport<LoadKv3FromKv1FileFn>(tier0, "?LoadKV3FromKV1File@@YA_NPEAVKeyValues3@@PEAVCUtlString@@PEBD2W4KV1TextEscapeBehavior_t@@I@Z");

        // ---- 3) 内部函数：命名签名（要求唯一命中）----
        RvaArenaCtor = rc.FindUniqueSignature(Signatures.Get("ArenaCtor").Name, Signatures.Get("ArenaCtor").WindowsPattern);
        Log("ArenaCtor", "resourcecompiler", RvaArenaCtor);

        RvaGenerateResourceFile = rc.FindUniqueSignature(Signatures.Get("GenerateResourceFile").Name, Signatures.Get("GenerateResourceFile").WindowsPattern);
        Log("GenerateResourceFile", "resourcecompiler", RvaGenerateResourceFile);

        RvaAssetConfigInit = rc.FindUniqueSignature(Signatures.Get("AssetConfig_Init").Name, Signatures.Get("AssetConfig_Init").WindowsPattern);
        Log("AssetConfig_Init", "resourcecompiler", RvaAssetConfigInit);

        // ---- 4) 多副本函数：定位调用方 + 提取 E8 rel32 ----
        // PoolInit 函数体在 rc 里被多处内联展开：28 字节序言模式实测命中 10 处，
        // 只共享 29 字节序言，随后在 E8 rel32 与多处 RIP 相对位移上分叉
        // （hit0 vs hit1 在 512 字节内 111 字节不同）⇒ 纯模式无法区分；
        // 改用 ArenaCtor（唯一）的 +0xF8 处调用点提取被调地址。
        RvaSymTabPoolInit = rc.ResolveCallTargetRva(RvaArenaCtor, Signatures.ArenaCtor_PoolInitCallOffset);
        SymTabPoolInit = rc.ResolveDelegateAtRva<SymTabPoolInitFn>(RvaSymTabPoolInit);
        Log("SymTabPoolInit(via ArenaCtor+" + Signatures.ArenaCtor_PoolInitCallOffset.ToString("X") + ")", "resourcecompiler", RvaSymTabPoolInit);

        IsInitialized = true;
    }

    private static T BindExport<T>(Module m, string export) where T : Delegate
    {
        try { return m.ResolveDelegateByName<T>(export); }
        catch (Exception ex) { throw new FunctionResolveException(export, System.IO.Path.GetFileName(m.Path) + ": " + ex.Message); }
    }

    private static void Log(string name, string module, int rva)
        => ResolveLog.Add($"Found {name} ({module}) at 0x{rva:X}");

    /// <summary>确保已初始化；未初始化则抛错。</summary>
    public static void EnsureInitialized()
    {
        if (!IsInitialized) throw new FunctionResolveException("Initialize", "尚未调用 Functions.Initialize(gameBinDir)");
    }
}
