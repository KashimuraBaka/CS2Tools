// ============================================================================
// SDK.ResourceCompiler.CompilerInterop — 编译调用面（GenerateResourceFile）
//
// 状态（t12 实证，t16/t18 保持）：裸进程**无法驱动编译**。
//   * CreateInterface("ResourceCompilerSystem001") 可拿到系统对象；
//   * 但 AssetConfig_Init 在其配置加载器（LoadAssetTypesCommonTxt）内经间接槽
//     调用引擎服务时 0xC0000005 —— 编译器缺引擎服务图（文件系统/KV3/资产配置）；
//   * GenerateResourceFile 内部按扩展名取 assettype 需要 this+88 的配置对象，同样 AV。
//
// ⇒ 编译调用必须由 EngineServiceGuard 前置守卫保护（t28c）：原生 AV 在 .NET 是
//   fail-fast（不可 catch），在 UI 进程内直调会连 GUI 一起打死。本类只封装
//   「RVA 常量 + 委托签名 + 子进程边界说明」，不提供进程内直调入口。
// ============================================================================
namespace SDK.ResourceCompiler;

/// <summary>编译调用面的 RVA 常量（t12 重定位，游戏 10-17 更新后 t24 全部 +0x1C0）。</summary>
public static class CompilerRva
{
    /// <summary>CResourceCompilerSystem::GenerateResourceFile(this, pSrcAbsPath, pOptions)。</summary>
    public const int GenerateResourceFile = 0x188B90;

    /// <summary>AssetConfig_Init（幂等）。</summary>
    public const int AssetConfigInit = 0x187EA0;

    /// <summary>CResourceCompilerSystem::GetCompilerByIdentifier（未导出，线性 V_stricmp_fast 扫描）。</summary>
    public const int Resolver = 0x1884C0;

    /// <summary>LoadAssetTypesCommonTxt（配置加载器，AV 发生处）。</summary>
    public const int ConfigLoader = 0x1F1B3E0;

    /// <summary>编译注册表表头 VA = 0x183078730（RVA 0x3078730）。</summary>
    public const int RegistryTable = 0x3078730;

    /// <summary>注册表条目数。</summary>
    public const int RegistryEntryCount = 43;

    /// <summary>注册表条目大小（字节）。</summary>
    public const int RegistryEntrySize = 0x28;

    /// <summary>m_CompilerIdentifier 在 assettype 对象内的偏移（schema-bound，t24 实测）。</summary>
    public const int CompilerIdentifierOffset = 0x108;
}

/// <summary>
/// 编译调用边界说明（供 UI / 文档引用）。
/// </summary>
public static class CompilerInterop
{
    /// <summary>编译必须在隔离子进程中执行的原因（UI 提示可直接引用）。</summary>
    public const string IsolationRationale =
        "resourcecompiler.dll 的编译路径需要引擎宿主提供服务图（文件系统 / KV3 解析 / 资产配置）；" +
        "裸进程下调 AssetConfig_Init / GenerateResourceFile 会 0xC0000005，而 .NET 对 " +
        "AccessViolationException 是 fail-fast（不可 catch）⇒ 进程内调用前必须先过 EngineServiceGuard 预检（t28c）。";

    /// <summary>GenerateResourceFile 的委托签名（仅在子进程内、且引擎服务图就绪时才可安全调用）。</summary>
    /// <remarks>
    /// 原型（t24）：bool GenerateResourceFile(this, const char* pSrcAbsPath, const char* pOptions)。
    /// 当前**不**在 SDK 内暴露可调用实例——见 <see cref="IsolationRationale"/>。
    /// </remarks>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int GenerateResourceFileFn(IntPtr self, IntPtr srcAbsPath, IntPtr options);
}
