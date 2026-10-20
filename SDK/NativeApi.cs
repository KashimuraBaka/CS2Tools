// ============================================================================
// SDK.NativeApi — 每个原生函数一个静态调用方法（t28m）。
//
// 命名规则：C++ 限定名的 `::` 换成 `_`，如
//   CResourceCompilerSystem::GenerateResourceFile -> CResourceCompilerSystem_GenerateResourceFile
// 方法体只是强类型委托的一层薄封装（委托声明与签名解析在 SDK.Native.Functions）。
// 这样调用方看到的就是「引擎有哪些函数可调、签名是什么」，而不是一堆 nint。
//
// 已解析函数清单（Functions.Initialize 后可用；共 13 个 + 2 个日志函数）。
// ============================================================================
using SDK.Native;
using System.Runtime.InteropServices;

namespace SDK;

/// <summary>原生函数静态调用面（名 = C++ 限定名）。</summary>
public static partial class NativeApi
{
    // ---- tier0.dll ----

    /// <summary>CMemAllocSystemInitialize() —— 建立块分配器 size-class 表。</summary>
    public static void CMemAllocSystem_Initialize() => Functions.MemAllocSystemInitialize();

    /// <summary>EarlyInit_Tier0() —— tier0 运行时早期初始化。</summary>
    public static void EarlyInit_Tier0() => Functions.EarlyInitTier0();

    /// <summary>
    /// ?LoadKV3@@YA_NPEAVCKV3Arena@@PEAVCUtlString@@PEAVCUtlBuffer@@AEBUKV3ID_t@@PEBDI@Z
    /// bool LoadKV3(CKV3Arena* arena, CUtlString* pError, CUtlBuffer* pBuffer,
    ///             const KV3ID_t&amp; fmt, const char* pPathId, unsigned int nFlags)
    /// </summary>
    public static bool CKV3Arena_LoadKV3(nint arena, nint pError, nint pBuffer, nint fmt, nint pPathId, uint nFlags)
        => Functions.LoadKv3Arena(arena, pError, pBuffer, fmt, pPathId, nFlags);

    /// <summary>?SaveKV3AsJSON@@...：bool SaveKV3AsJSON(const KeyValues3*, CUtlString* pOut, CUtlString* pError)。</summary>
    public static bool KeyValues3_SaveKV3AsJSON(nint pRoot, nint pOutJson, nint pError)
        => Functions.SaveKv3AsJson(pRoot, pOutJson, pError);

    /// <summary>?LoadKV3Text_NoHeader@@...：无头 KV3 文本解析（探针用）。</summary>
    public static bool KeyValues3_LoadKV3Text_NoHeader(nint pRoot, nint pError, nint pText, nint fmt, nint pPathId, uint nFlags)
        => Functions.LoadKv3TextNoHeader(pRoot, pError, pText, fmt, pPathId, nFlags);

    /// <summary>?LoadKV3FromKV3OrKV1@@...：按内容自动判别 KV3/KV1。</summary>
    public static bool KeyValues3_LoadKV3FromKV3OrKV1(nint pRoot, nint pError, nint pBuffer, nint fmt, nint pPathId, uint nFlags)
        => Functions.LoadKv3FromKv3OrKv1(pRoot, pError, pBuffer, fmt, pPathId, nFlags);

    /// <summary>?LoadKV3FromJSON@@...：JSON 文本 → KeyValues3 树。</summary>
    public static bool KeyValues3_LoadKV3FromJSON(nint pRoot, nint pError, nint pJson, nint pPathId, uint nFlags)
        => Functions.LoadKv3FromJson(pRoot, pError, pJson, pPathId, nFlags);

    /// <summary>?SaveKV3AsKV1Text@@...：KeyValues3 树 → KV1 文本（写入第三参 CUtlBuffer）。</summary>
    public static bool KeyValues3_SaveKV3AsKV1Text(nint pRoot, nint pOut, nint pBuf, int eEscape)
        => Functions.SaveKv3AsKv1Text(pRoot, pOut, pBuf, eEscape);

    /// <summary>??0CUtlBuffer@@QEAA@HHW4BufferFlags_t@0@@Z：CUtlBuffer(int nGrowSize, int nPutFlags, BufferFlags_t)。</summary>
    public static nint CUtlBuffer_CUtlBuffer(nint self, int nGrowSize, int nPutFlags, int flags)
        => Functions.CUtlBufferCtor(self, nGrowSize, nPutFlags, flags);

    // ---- resourcecompiler.dll ----

    /// <summary>CreateInterface(const char* interfaceName, void* pReturnCode) → void*。</summary>
    public static nint CreateInterface(nint pName, nint pReturnCode) => Functions.CreateInterface(pName, pReturnCode);

    /// <summary>
    /// CResourceCompilerSystem::GenerateResourceFile(resourcecompiler.dll RVA 0x188B90)。
    /// 反编译判据：prologue 依次保存 rcx/rdx/r8 到 arg_0/arg_8/arg_10 后 push rbp..r14；
    /// 体内 cmp [r12+28h], sil（this 状态查询）、mov rax,[r12+48h]、mov rax,[r12+50h]，
    /// 第 3 参（r8）被当对象读 +38h/+20h，第 2 参（rdx）经 V_GetFileExtensionSafe 取扩展名
    /// （与 "vmap" 比较后走 sub_180160070）。
    /// 即签名形如：
    ///   bool GenerateResourceFile(CResourceCompilerSystem* this, const char* pszSourceFile,
    ///                            CResourceCompilerContext* pCtx, intptr_t pReserved)
    /// **未实测调用**——进程内缺引擎服务图（见 EngineServiceGuard），此处仅暴露签名面。
    /// </summary>
    public static bool CResourceCompilerSystem_GenerateResourceFile(nint system, nint pszSourceFile, nint pContext, nint pReserved)
        => Functions.GenerateResourceFile(system, pszSourceFile, pContext, pReserved);

    /// <summary>AssetConfig_Init(resourcecompiler.dll RVA 0x187EA0)——虚表方法，需引擎创建的对象。</summary>
    public static nint AssetConfig_Init(nint pAssetConfig) => Functions.AssetConfigInit(pAssetConfig);

    /// <summary>arena 内嵌符号表 bucket 初始化（resourcecompiler.dll RVA 0xC84F0，经 ArenaCtor+0xF8 定位）。</summary>
    public static nint SymTabPool_Init(nint self, int nCapacity, int nGrowSize, byte bFlag)
        => Functions.SymTabPoolInit(self, nCapacity, nGrowSize, bFlag);

    // ---- tier0 日志通道（EngineLog）----

    /// <summary>LoggingSystem_IsChannelEnabled(channel, verbosity) → bool。</summary>
    public static bool LoggingSystem_IsChannelEnabled(nint channel, int verbosity) => EngineLog.IsEnabled(channel, (LogSeverity)verbosity);

    /// <summary>LoggingSystem_LogDirect(channel, severity, rareOptions, text) → LoggingResponse_t。</summary>
    public static int LoggingSystem_LogDirect(nint channel, int severity, nint rareOptions, nint text)
        => EngineLog.Write(Marshal.PtrToStringUTF8(text) ?? "", (LogSeverity)severity, channel);
}
