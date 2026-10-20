
// ============================================================================
// RegistryService（UI 侧薄封装，t18）—— 真正的 native 调用在
//   SDK.ResourceCompiler.RegistryService（projects\SDK\ResourceCompiler\）。
// 这里只把 SDK 的解析结果适配成 UI 行模型 + 保留友好错误语义；
// UI 层不直接出现 P/Invoke。
// ============================================================================
using SDK.ResourceCompiler;

namespace AssetCompiler.Services;

public static class RegistryService
{
    /// <summary>只读读取注册表（委托 SDK）。</summary>
    public static RegistrySnapshot Read(string dllPath) => SDK.ResourceCompiler.RegistryService.Read(dllPath);

    /// <summary>表头 RVA 运行时自校验（委托 SDK；不符则抛异常，不静默用错数据）。</summary>
    public static void ValidateTable(RegistrySnapshot snap) => SDK.ResourceCompiler.RegistryService.ValidateTable(snap);

    /// <summary>按标识符解析条目（UI 口径：条目级 28/29）。</summary>
    public static ResolveResult Resolve(RegistrySnapshot snap, string identifier)
    {
        var hit = SDK.ResourceCompiler.RegistryService.Resolve(snap, identifier);
        if (!hit.Found)
        {
            return new ResolveResult
            {
                Found = false,
                Identifier = identifier,
                Message = $"未命中：注册表 {snap.Entries.Count} 条中没有 {identifier}。引擎侧对应错误串语义: Resource type compiler identifier not found"
            };
        }

        var e = hit.Entry!;
        return new ResolveResult
        {
            Found = true,
            Identifier = identifier,
            Message = $"命中 idx {e.Index}",
            Entry = new RegistryEntryRow
            {
                Index = e.Index,
                Identifier = e.Identifier,
                Factory = e.Factory,
                CachedInstance = e.CachedInstance,
                EntryAddress = e.EntryAddress,
                FactoryRva = e.FactoryRva,
                SingletonRva = e.SingletonRva
            }
        };
    }
}
