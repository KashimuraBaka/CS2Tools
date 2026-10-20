
// ============================================================================
// CompileService —— 编译执行（t28c 起全程进程内，不起任何子进程）
//
// 历史（t12 实证）：AssetConfig_Init / GenerateResourceFile 在裸进程 0xC0000005——
//   编译路径依赖引擎服务图，而 .NET 对 AccessViolationException fail-fast 不可 catch，
//   故曾用 CompileHost 子进程隔离（崩了只是退出码；t28d 该子工程已移除）。
//
// 现状（t28c，用户禁止额外进程方式）：编译全程进程内，靠**前置守卫**避免进崩代码：
//   EngineServiceGuard.Check 先读引擎服务全局槽（qword_183B60158 等，由引擎主程序注册），
//   缺任一槽位就不调用编译路径，把「引擎服务图未注册」作为可预期错误上报——
//   应用不崩、原因明确。CreateInterface + 注册表只读验证这两项能力进程内照常完成。
//
// 策略（t28q 起只有一级）：进程内——签名解析 -> CreateInterface+注册表只读 -> 服务图预检
//      -> 齐备才编译。Tier2（官方 exe 子进程）已删除：用户禁止额外进程方式，
//      且「默认就是通过签名进行编译，无需选择 dll 和 exe」。
// ============================================================================
using SDK.ResourceCompiler;
using System.IO;

namespace AssetCompiler.Services;

public sealed class CompileOutcome
{
    public bool Ok { get; init; }
    public string Stage { get; init; } = "";
    public string Detail { get; init; } = "";
    /// <summary>编译产物路径（t28l：SDK 门面直出）。</summary>
    public string OutputPath { get; init; } = "-";
    public List<string> Logs { get; init; } = [];
}

public static class CompileService
{
    /// <summary>编译输出目录（空 = 与源文件同目录）。t28k 新增：可控输出路径，直写不移动。</summary>
    public static string OutputDirectory { get; set; } = "";
    /// <summary>t12 重定位的新 RVA（游戏 10-17 更新后；旧锚点库 RVA 已禁用）。</summary>
    public const int RvaGenerateResourceFile = 0x188B90;
    public const int RvaAssetConfigInit = 0x187EA0;
    public const int RvaResolver = 0x1884C0;
    public const int RvaConfigLoader = 0x1F1B3E0;

    /// <summary>
    /// 编译（t28q：唯一入口。逻辑已沉入 SDK.ResourceCompiler.ResourceCompilerService.Compile，
    /// 此处只做 UI 适配；不含任何 DLL/exe 选择，也不起子进程）。
    /// </summary>
    public static CompileOutcome CompileSource(string gameBinDir, string sourceFile, string? outputDir = null)
    {
        var r = ResourceCompilerService.Compile(new CompileRequest(sourceFile, outputDir, gameBinDir));
        return new CompileOutcome
        {
            Ok = r.Ok,
            Stage = r.Stage,
            Detail = r.Detail,
            OutputPath = r.OutputPath ?? "-",
            Logs = r.Logs?.ToList() ?? [],
        };
    }
}
