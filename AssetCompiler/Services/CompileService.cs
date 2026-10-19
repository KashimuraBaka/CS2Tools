
// ============================================================================
// CompileService —— 编译执行（**隔离子进程**，保 GUI 不被原生 AV 打死）
//
// 为什么必须子进程（t12 实证，队长已复验）：
//   AssetConfig_Init 与 GenerateResourceFile 在**裸进程**均 0xC0000005 ——
//   编译器缺引擎服务图（文件系统/KV3/资产配置；CreateInterface 对其余 22 个
//   引擎接口名全返回 NULL）。.NET 对 AccessViolationException 默认 fail-fast
//   （不可 catch）⇒ 在 UI 进程内直调会连 GUI 一起崩。
//
// 两级策略（队长方向）：
//   Tier1 = DLL 路径（CreateInterface -> AssetConfig_Init -> GenerateResourceFile，
//            用 t12 重定位的新 RVA，带运行时签名自检），如实捕获失败；
//   Tier2 = 官方 resourcecompiler.exe（-i <file> -game <moddir> -nop4 -v）。
//   UI 逐文件显示两级结果与所用路径——Tier1 的失败本身就是有价值的实证结论。
// ============================================================================
using System.Diagnostics;
using System.Text.Json;

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

    public static string HostExe { get; set; } = "";

    /// <summary>
    /// 解析 CompileHost.exe 的真实路径（t16 修复）。
    /// 候选顺序：
    ///   1) 显式指定的 HostExe（若非空且存在）；
    ///   2) 主程序自己的输出目录（csproj 的 CopyCompileHostOutputs 目标会把它复制过来）；
    ///   3) CompileHost 子工程的输出目录，按当前配置 / Release / Debug 顺序探。
    /// 这样即便没跑构建后复制（例如手工 dotnet run、或只建了主工程），Tier1 仍可达。
    /// </summary>
    public static string ResolveHostExe(string? explicitPath = null)
    {
        var cands = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath)) cands.Add(explicitPath);
        if (!string.IsNullOrWhiteSpace(HostExe)) cands.Add(HostExe);

        string? baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDir))
        {
            cands.Add(Path.Combine(baseDir, "CompileHost.exe"));
            // 主程序输出目录：<repo>/projects/CS2-Tools/AssetCompiler/bin/<cfg|/x64/<cfg>/net10.0-windows
            // CompileHost 输出：<repo>/projects/CS2-Tools/CompileHost/bin/<cfg|/x64/<cfg>/net10.0-windows
            // 注意 Platforms=x64 会在 bin 下多插一段 x64，两种布局都要探。
            var dir = new DirectoryInfo(baseDir);
            for (int up = 0; up < 6 && dir is not null; up++, dir = dir.Parent)
            {
                if (dir is null) break;
                var projDir = Path.Combine(dir.FullName, "CompileHost");
                if (!Directory.Exists(projDir)) continue;
                foreach (var cfg in new[] { "Release", "Debug" })
                {
                    cands.Add(Path.Combine(projDir, "bin", cfg, "net10.0-windows", "CompileHost.exe"));
                    cands.Add(Path.Combine(projDir, "bin", "x64", cfg, "net10.0-windows", "CompileHost.exe"));
                }
                break;
            }
        }
        foreach (var c in cands)
        {
            if (!string.IsNullOrEmpty(c) && File.Exists(c)) return c;
        }
        return HostExe ?? "";
    }

    /// <summary>对单个源文件跑 Tier1（DLL 路径，子进程）。</summary>
    public static CompileOutcome RunTier1(string dllPath, string anchorsPath, string sourceFile)
    {
        var host = ResolveHostExe();
        if (string.IsNullOrEmpty(host) || !File.Exists(host))
            return new CompileOutcome { Ok = false, Stage = "tier1", Detail = "CompileHost.exe 不存在（已尝试候选路径）；请先构建 CompileHost 子工程。最后候选: " + host };

        // 记住解析到的真实路径，供日志面板显示
        HostExe = host;

        var psi = new ProcessStartInfo
        {
            FileName = host,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(host) ?? "."
        };
        psi.ArgumentList.Add("--dll"); psi.ArgumentList.Add(dllPath);
        psi.ArgumentList.Add("--anchors"); psi.ArgumentList.Add(anchorsPath);
        psi.ArgumentList.Add("--source"); psi.ArgumentList.Add(sourceFile);
        psi.ArgumentList.Add("--mode"); psi.ArgumentList.Add("compile");

        var logs = new List<string>();
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return new CompileOutcome { Ok = false, Stage = "tier1", Detail = "无法启动 CompileHost" };
            string outp = p.StandardOutput.ReadToEnd();
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            int code = p.ExitCode;

            foreach (var line in outp.Split('\n'))
            {
                var s = line.Trim();
                if (s.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(s);
                    var kind = doc.RootElement.TryGetProperty("kind", out var k) ? k.GetString() : null;
                    if (kind == "log")
                    {
                        var msg = doc.RootElement.TryGetProperty("msg", out var m) ? m.GetString() : "";
                        logs.Add(msg ?? "");
                    }
                    else if (kind == "result")
                    {
                        bool ok = doc.RootElement.TryGetProperty("ok", out var o) && o.GetBoolean();
                        var stage = doc.RootElement.TryGetProperty("stage", out var st) ? st.GetString() : "";
                        var detail = doc.RootElement.TryGetProperty("detail", out var d) ? d.GetString() : "";
                        return new CompileOutcome { Ok = ok, Stage = stage ?? "", Detail = detail ?? "", ExitCode = code, Logs = logs };
                    }
                }
                catch { /* 非 JSON 行原样入日志 */ logs.Add(s); }
            }
            if (!string.IsNullOrWhiteSpace(err)) logs.Add("stderr: " + err.Trim());

            // 没有 result 行 ⇒ 子进程多半被原生异常杀了（t12 实证的 AV）
            string crash = code < 0 || code == 134 || code == 139
                ? $"子进程被原生异常终止（exit {code}）——与 t12 实证一致：裸进程缺引擎服务图。"
                : $"子进程未产出 result（exit {code}）。";
            return new CompileOutcome { Ok = false, Stage = "tier1", Detail = crash, ExitCode = code, Logs = logs };
        }
        catch (Exception ex)
        {
            return new CompileOutcome { Ok = false, Stage = "tier1", Detail = ex.GetType().Name + ": " + ex.Message, ExitCode = -1, Logs = logs };
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
