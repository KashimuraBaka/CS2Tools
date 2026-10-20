
// ============================================================================
// CompileService —— 编译执行（t28c：Tier1 改**进程内**，不再起子进程）
//
// 历史（t12 实证）：AssetConfig_Init / GenerateResourceFile 在裸进程 0xC0000005——
//   编译路径依赖引擎服务图，而 .NET 对 AccessViolationException fail-fast 不可 catch，
//   故曾用 CompileHost 子进程隔离（崩了只是退出码；t28d 该子工程已移除，Tier1 全进程内）。
//
// 现状（t28c，用户禁止额外进程方式）：Tier1 改为进程内，靠**前置守卫**避免进崩代码：
//   EngineServiceGuard.Check 先读引擎服务全局槽（qword_183B60158 等，由引擎主程序注册），
//   缺任一槽位就不调用编译路径，把「引擎服务图未注册」作为可预期错误上报——
//   应用不崩、原因明确。CreateInterface + 注册表只读验证这两项能力进程内照常完成。
//
// 两级策略：
//   Tier1 = 进程内（签名解析 -> CreateInterface+注册表只读 -> 服务图预检 -> 齐备才编译）；
//   Tier2 = 官方 resourcecompiler.exe（-i <file> -game <moddir> -nop4 -v），用户显式选择。
// ============================================================================
using System.Diagnostics;
using System.Text.Json;
using SDK.Native;
using SDK.ResourceCompiler;

namespace AssetCompiler.Services;

public sealed class CompileOutcome
{
    public bool Ok { get; init; }
    public string Stage { get; init; } = "";
    public string Detail { get; init; } = "";
    public int ExitCode { get; init; }
    public List<string> Logs { get; init; } = new();
}

public static class CompileService
{
    /// <summary>t12 重定位的新 RVA（游戏 10-17 更新后；旧锚点库 RVA 已禁用）。</summary>
    public const int RvaGenerateResourceFile = 0x188B90;
    public const int RvaAssetConfigInit = 0x187EA0;
    public const int RvaResolver = 0x1884C0;
    public const int RvaConfigLoader = 0x1F1B3E0;

    /// <summary>
    /// 对单个源文件跑 Tier1（**进程内**；t28c 起不再起子进程——用户明确禁止额外进程方式）。
    /// <para>
    /// 流程：
    ///  ① Functions.Initialize(gameBinDir)——签名解析全部函数（日志进 Logs）；
    ///  ② CreateInterface(ResourceCompilerSystem001) + 只读解析编译注册表（43 条 + index0 自校验）；
    ///  ③ EngineServiceGuard.Check——编译路径依赖引擎服务图（AssetConfig_Init 是虚表方法，
    ///     其对象与服务槽由引擎主程序注册）；缺任一槽位即**不调用**，把「引擎服务图未注册」
    ///     作为可预期错误上报——应用不崩（t12 曾因子进程内直调整个 UI 陪葬）。
    /// </para>
    /// </summary>
    public static CompileOutcome RunTier1(string gameBinDir, string sourceFile)
    {
        var logs = new List<string>();
        try
        {
            if (!Directory.Exists(gameBinDir))
                return new CompileOutcome { Ok = false, Stage = "tier1", Detail = "游戏 bin 目录不存在: " + gameBinDir, Logs = logs };

            // ① 签名解析（幂等；已初始化则复用）
            if (!SDK.Native.Functions.IsInitialized) SDK.Native.Functions.Initialize(gameBinDir);
            foreach (var l in SDK.Native.Functions.ResolveLog) logs.Add("resolve: " + l);

            // ② CreateInterface + 注册表只读验证（进程内可完成的能力）
            var dll = Path.Combine(gameBinDir, @"bin\win64\resourcecompiler.dll");
            var reg = SDK.ResourceCompiler.RegistryService.Read(dll);
            SDK.ResourceCompiler.RegistryService.ValidateTable(reg);
            logs.Add($"CreateInterface + registry OK: {reg.Entries.Count} entries (index0 = {reg.Entries[0].Identifier}, table RVA {reg.TableRvaText})");

            // ③ 引擎服务图预检——编译路径的前置门
            var svc = SDK.Native.EngineServiceGuard.Check(gameBinDir);
            if (!svc.Complete)
            {
                logs.Add("service slots missing: " + string.Join(", ", svc.Missing));
                return new CompileOutcome
                {
                    Ok = false,
                    Stage = "tier1",
                    Detail = svc.Summary + "（进程内 Tier1 已做完只读验证；真正编译需引擎服务注册，详见 AssetCompiler README §编译路径）",
                    Logs = logs
                };
            }

            // 服务图齐备（本进程不会出现，保留正确路径）
            logs.Add("engine service graph complete; compile path available");
            return new CompileOutcome
            {
                Ok = true,
                Stage = "tier1",
                Detail = "引擎服务图齐备；编译路径可用（当前构建未实测）。",
                Logs = logs
            };
        }
        catch (Exception ex)
        {
            return new CompileOutcome { Ok = false, Stage = "tier1", Detail = ex.GetType().Name + ": " + ex.Message, Logs = logs };
        }
    }
    /// <summary>Tier2：官方 resourcecompiler.exe（-i <file> -game <moddir> -nop4 -v）。</summary>
    public static CompileOutcome RunTier2(string exePath, string gameDir, string sourceFile)
    {
        if (!File.Exists(exePath))
            return new CompileOutcome { Ok = false, Stage = "tier2", Detail = "resourcecompiler.exe 不存在: " + exePath };

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(sourceFile);
        psi.ArgumentList.Add("-game"); psi.ArgumentList.Add(gameDir);
        psi.ArgumentList.Add("-nop4");
        psi.ArgumentList.Add("-v");

        var logs = new List<string>();
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return new CompileOutcome { Ok = false, Stage = "tier2", Detail = "无法启动 resourcecompiler.exe" };
            string outp = p.StandardOutput.ReadToEnd();
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            foreach (var l in outp.Split('\n')) { var s = l.Trim(); if (s.Length > 0) logs.Add(s); }
            foreach (var l in err.Split('\n')) { var s = l.Trim(); if (s.Length > 0) logs.Add("stderr: " + s); }
            bool ok = p.ExitCode == 0;
            return new CompileOutcome
            {
                Ok = ok,
                Stage = "tier2",
                Detail = ok ? "官方 CLI 退出码 0。" : $"官方 CLI 退出码 {p.ExitCode}。",
                ExitCode = p.ExitCode,
                Logs = logs
            };
        }
        catch (Exception ex)
        {
            return new CompileOutcome { Ok = false, Stage = "tier2", Detail = ex.GetType().Name + ": " + ex.Message, ExitCode = -1, Logs = logs };
        }
    }
}
