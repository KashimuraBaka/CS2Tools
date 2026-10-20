// ============================================================================
// SDK.ResourceCompiler.ResourceCompilerService — 签名解析后的**编译接口**（t28l）。
//
// 设计动机：签名解析（Functions.Initialize）完成后，SDK 就应直接开放编译能力，
// 而不是让应用层抱着 "Tier1/Tier2" 这种策略概念围着 SDK 转。本类是 SDK 对外的
// 编译门面：给定源文件路径与（可选）输出目录，返回编译结果。
//
// 覆盖范围与边界（详见 docs\dlls\win64_render_scene_physics_animation\
// inprocess_resource_compile_report.md）：
//   * .vnmgraph / .vdata（产物 = KV3 二进制）→ 进程内完整编译（tier0 内部序列化器）；
//   * 其它资源类型 → 需要引擎服务图（CAssetSystem 实例 + mod 上下文），
//     进程内不可行；此时返回带明确原因的失败，**不调用、不崩溃**。
// ============================================================================
using SDK.Native;
using SDK.Tier0;

namespace SDK.ResourceCompiler;

/// <summary>编译请求。</summary>
/// <param name="SourcePath">源文件路径（.vXXX 文本）。</param>
/// <param name="OutputDirectory">输出目录；null/空 = 与源文件同目录。</param>
/// <param name="GameBinDir">游戏 bin 目录（含 bin\win64）。null = 从源路径推断。</param>
public sealed record CompileRequest(string SourcePath, string? OutputDirectory = null, string? GameBinDir = null);

/// <summary>编译结果。</summary>
public sealed record CompileResult(
    bool Ok,
    string Stage,
    string Detail,
    string? OutputPath = null,
    IReadOnlyList<string>? Logs = null);

/// <summary>
/// 资源编译门面。流程：签名解析（幂等）→ 资源系统只读验证 → 按资源类型分流。
/// </summary>
public static class ResourceCompilerService
{
    /// <summary>产物即 KV3 二进制的资源扩展名（进程内可完整编译）。</summary>
    public static readonly string[] Kv3BinaryExtensions = [".vnmgraph", ".vdata"];

    /// <summary>编译单个资源。</summary>
    public static CompileResult Compile(CompileRequest request)
    {
        var logs = new List<string>();
        try
        {
            var src = request.SourcePath;
            if (!File.Exists(src))
                return new CompileResult(false, "input", "源文件不存在: " + src, Logs: logs);

            // 游戏 bin 目录：显式指定 > 从源路径推断（<game>\content\... -> <game>\bin\win64）
            var gameBinDir = request.GameBinDir;
            if (string.IsNullOrWhiteSpace(gameBinDir))
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(src));
                while (dir is not null && !Directory.Exists(Path.Combine(dir, "bin", "win64")))
                    dir = Path.GetDirectoryName(dir);
                gameBinDir = dir is null ? "." : Path.Combine(dir, "bin", "win64");
            }
            if (!Directory.Exists(gameBinDir))
                return new CompileResult(false, "input", "游戏 bin 目录不存在: " + gameBinDir, Logs: logs);

            // ① 签名解析（幂等；已初始化则复用）
            if (!Functions.IsInitialized) Functions.Initialize(gameBinDir);
            logs.AddRange(Functions.ResolveLog.Select(l => "resolve: " + l));

            // ② CreateInterface + 注册表只读验证（进程内可完成的能力）
            var dll = Path.Combine(gameBinDir, @"bin\win64\resourcecompiler.dll");
            var reg = RegistryService.Read(dll);
            RegistryService.ValidateTable(reg);
            logs.Add($"CreateInterface + registry OK: {reg.Entries.Count} entries (index0 = {reg.Entries[0].Identifier}, table RVA {reg.TableRvaText})");

            // ③ KV3 二进制类资源：进程内完整编译
            var ext = Path.GetExtension(src).ToLowerInvariant();
            if (Kv3BinaryExtensions.Contains(ext))
                return CompileKv3Binary(src, gameBinDir, request.OutputDirectory, logs);

            // ④ 其它资源：需要引擎服务图——预检后明确失败，不调用、不崩溃
            var svc = EngineServiceGuard.Check(gameBinDir);
            logs.Add("service slots missing: " + string.Join(", ", svc.Missing));
            return new CompileResult(false, "engine-services", svc.Summary, Logs: logs);
        }
        catch (Exception ex)
        {
            return new CompileResult(false, "exception", ex.GetType().Name + ": " + ex.Message, Logs: logs);
        }
    }

    private static CompileResult CompileKv3Binary(string src, string gameBinDir, string? outputDir, List<string> logs)
    {
        var srcText = File.ReadAllText(src);
        if (!srcText.TrimStart().StartsWith("<!--", StringComparison.Ordinal))
            return new CompileResult(false, "input", "不是 KV3 文本源（缺 kv3 头）: " + src, Logs: logs);

        // 引擎口吻消息（assetsystem 的 RecompileAsset 同款通道）
        var rel = Path.GetRelativePath(gameBinDir, src).Replace(Path.DirectorySeparatorChar, '/');
        EngineLog.WriteRecompiling(rel);
        logs.Add("engine-log: Recompiling resource asset: '" + rel + "'");

        // 二进制序列化器递归深、栈帧大（~0x400 B/层），1 MB 级图形必须大栈线程
        byte[]? outBytes = null; string? compileErr = null;
        var th = new System.Threading.Thread(() =>
        {
            try { var r = Kv3NativeArena.CompileKv3Binary(srcText); if (r.Ok) outBytes = Convert.FromBase64String(r.Json); else compileErr = r.Detail; }   // 内部经 NativeApi.KeyValues3_LoadKV3FromKV3OrKV1 / KeyValues3_SaveKV3AsKV1Text
            catch (Exception ex) { compileErr = ex.GetType().Name + ": " + ex.Message; }
        }, 32 * 1024 * 1024);
        th.Start(); th.Join();

        if (outBytes is null)
            return new CompileResult(false, "serialize", "KV3 二进制编译失败: " + compileErr, Logs: logs);

        // 输出路径：outputDir > 源目录；直接 WriteAllBytes，零 File.Move
        var outName = Path.GetFileName(src) + "_c";
        var outPath = string.IsNullOrWhiteSpace(outputDir) ? src + "_c" : Path.Combine(outputDir, outName);
        try
        {
            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(outPath, outBytes);
        }
        catch (Exception ex)
        {
            var fallback = Path.Combine(AppContext.BaseDirectory, outName);
            File.WriteAllBytes(fallback, outBytes);
            logs.Add($"输出目录不可写（{ex.GetType().Name}），回退: {fallback}");
            outPath = fallback;
        }
        logs.Add($"KV3 binary compiled in-process: {outBytes.Length} B -> {outPath}");
        EngineLog.Write("Reloading Resource [1/1]: " + rel, LogSeverity.Message);
        logs.Add("engine-log: Reloading Resource [1/1]: " + rel);
        return new CompileResult(true, "kv3-binary", $"KV3 二进制编译成功：{outBytes.Length} B（VK3 魔数 + 格式 GUID）", outPath, logs);
    }
}
