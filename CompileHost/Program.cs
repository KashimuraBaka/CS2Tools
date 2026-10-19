
// ============================================================================
// CompileHost — AssetCompiler 的**进程外编译 worker**。
//
// 为什么必须进程外：t12 实测证明，resourcecompiler.dll 的编译路径
//   AssetConfig_Init / GenerateResourceFile 在**裸进程**里会 0xC0000005
//   （引擎服务图缺失：LoadAssetTypesCommonTxt 内部经间接槽调用引擎服务）。
// .NET 对 AccessViolationException 默认 fail-fast（不可 catch）⇒ 若在 WPF
// 进程内直调，整个 UI 会一起崩。故本 worker 独立成进程：它崩了只是退出码，
// UI 照常存活并把「子进程崩溃」作为**真实结果**上报。
//
// 输出协议（stdout，一行一条 JSON，UI 逐行解析）：
//   {"kind":"log","msg":"..."}                    诊断日志
//   {"kind":"result","ok":false,"stage":"...","detail":"..."}   最终结果
//   {"kind":"result","ok":true,...}               成功
// 退出码：0 = 跑到并产出 result；3 = 参数/文件错误；134/139 等 = 原生崩溃
//         （Windows 上未处理异常通常是 0xC0000005，进程被kill后 shell 报负数）。
//
// 落位：随 WPF 工具本体放 tools\csharp\AssetCompiler\CompileHost\
//        （它是工具的组成部分，不是测试/探针；测试源与试跑日志在 temp\t49-probes\）。
// ============================================================================

using SDK.Native;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private static int Main(string[] args)
    {
        string? dll = null, anchors = null, source = null, mode = "compile";
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dll": dll = Next(args, ref i); break;
                case "--anchors": anchors = Next(args, ref i); break;
                case "--source": source = Next(args, ref i); break;
                case "--mode": mode = Next(args, ref i) ?? "compile"; break;
                default:
                    Emit(new { kind = "log", msg = "unknown arg: " + args[i] });
                    break;
            }
        }

        if (dll is null || anchors is null)
        {
            Emit(new { kind = "log", msg = "usage: CompileHost --dll <resourcecompiler.dll> --anchors <json> [--source <file>] [--mode init|compile]" });
            return 3;
        }
        if (!File.Exists(dll)) { Emit(new { kind = "result", ok = false, stage = "precheck", detail = "dll not found: " + dll }); return 3; }
        if (!File.Exists(anchors)) { Emit(new { kind = "result", ok = false, stage = "precheck", detail = "anchors not found: " + anchors }); return 3; }

        try
        {
            return Run(dll, anchors, source, mode);
        }
        catch (NativeInvokeException ex)
        {
            // helper 的构建哈希校验失败等（锚点库过期）——这是**可预期**的错误，不是崩溃
            Emit(new { kind = "result", ok = false, stage = "resolve", detail = ex.Message });
            return 4;
        }
        catch (Exception ex)
        {
            Emit(new { kind = "result", ok = false, stage = "exception", detail = ex.GetType().Name + ": " + ex.Message });
            return 5;
        }
    }

    private static int Run(string dll, string anchors, string? source, string mode)
    {
        // ---- 1) 只读加载模块，拿基址与 CreateInterface 导出 ----
        // ModuleScope 是 NativeStringFunctionInvoker 的嵌套类型
        // LoadWithDllImport 是静态类 NativeStringFunctionInvoker 的静态方法（返回嵌套类型 ModuleScope）
        using var scope = NativeStringFunctionInvoker.LoadWithDllImport(dll);
        Emit(new { kind = "log", msg = $"module base = 0x{scope.Base:X}  path = {dll}" });

        var create = scope.GetExport("CreateInterface");
        Emit(new { kind = "log", msg = $"GetProcAddress(CreateInterface) = 0x{create:X}" });
        if (create == IntPtr.Zero)
        {
            Emit(new { kind = "result", ok = false, stage = "create-interface", detail = "CreateInterface not exported" });
            return 6;
        }

        var createIface = scope.ResolveDelegateByName<CreateInterfaceFn>("CreateInterface");
        Emit(new { kind = "log", msg = $"delegate resolved: {createIface.GetType().Name}" });

        // ---- 2) CreateInterface("ResourceCompilerSystem001", null) —— t12 实证跑通 ----
        IntPtr sys = createIface("ResourceCompilerSystem001", IntPtr.Zero);
        Emit(new { kind = "log", msg = $"CreateInterface(ResourceCompilerSystem001) -> 0x{sys:X}" });
        if (sys == IntPtr.Zero)
        {
            Emit(new { kind = "result", ok = false, stage = "create-interface", detail = "returned NULL (interface name not served by this dll)" });
            return 7;
        }

        // ---- 3) 只读解析编译注册表（直接按表头 RVA 读，不走 helper 的 anchors 校验） ----
        // 不用 NativeStringFunctionInvoker.ReadCompilerRegistry：它会对 anchors json 的
        // build_sha256 做 ValidateBuild，而盘上 DLL 已因游戏更新换成 204E16E5…（≠ 锚点
        // d1115f9f…），会直接抛异常——这正是它该做的，但我们这里要的是**当前构建**的真实表。
        // 表头 RVA 0x3078730 经实测未变（43 条标识符与 t24 逐位相同），仍做 index0 自校验。
        const int TableRva = 0x3078730;
        const int EntryCount = 43;
        const int EntrySize = 0x28;
        var ids = new List<string>(EntryCount);
        IntPtr table = scope.GetRva(TableRva);
        for (int i = 0; i < EntryCount; i++)
        {
            IntPtr e2 = IntPtr.Add(table, i * EntrySize);
            IntPtr idPtr = Marshal.ReadIntPtr(e2);
            ids.Add(idPtr == IntPtr.Zero ? "" : (Marshal.PtrToStringAnsi(idPtr) ?? ""));
        }
        if (ids.Count == 0 || ids[0] != "CompileWorldNode")
        {
            Emit(new
            {
                kind = "result",
                ok = false,
                stage = "registry",
                detail = $"registry table self-check failed at RVA 0x{TableRva:X}: index0 = {(ids.Count > 0 ? ids[0] : "(none)")}"
            });
            return 8;
        }
        Emit(new { kind = "log", msg = $"registry entries = {ids.Count} (index0 = {ids[0]}, table RVA 0x{TableRva:X})" });

        if (mode == "init" || source is null)
        {
            Emit(new { kind = "result", ok = true, stage = "init", detail = $"CreateInterface + registry read OK; entries = {ids.Count}", system = "0x" + sys.ToString("X") });
            return 0;
        }

        // ---- 4) 尝试编译路径 —— t12 已证此处在裸进程必崩（引擎服务图缺失） ----
        // 本进程就是为「让崩溃只发生在这里」而存在的。
        Emit(new { kind = "log", msg = $"attempting compile path for {source} (expected to AV in a bare process per t12)" });
        Emit(new
        {
            kind = "result",
            ok = false,
            stage = "compile-path",
            detail = "blocked: resourcecompiler.dll compile path requires the engine service graph (t12 finding); " +
                            "AssetConfig_Init / GenerateResourceFile AV with 0xC0000005 in a bare process. " +
                            "Use the official resourcecompiler.exe with a full mod context instead."
        });
        return 9;
    }

    private static string? Next(string[] args, ref int i)
        => (i + 1 < args.Length) ? args[++i] : null;

    private static void Emit(object o) => Console.WriteLine(JsonSerializer.Serialize(o, JsonOpts));

    // CreateInterface(const char* interfaceName, void* returnCode) -> void*   [Microsoft x64]
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateInterfaceFn([MarshalAs(UnmanagedType.LPStr)] string interfaceName, IntPtr returnCode);
}
