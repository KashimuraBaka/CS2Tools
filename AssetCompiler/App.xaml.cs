using AssetCompiler.Services;
using SDK.ResourceCompiler;
using System.Windows;

namespace AssetCompiler;

public partial class App : Application
{
    /// <summary>
    /// 无头自检：AssetCompiler.exe --selftest [configPath]
    /// 加载真实配置 + 读注册表 + 逐个解析 29 个标识符 + 
    /// 全部打印到 stdout 后退出。用于 CI / 复核，无需点 GUI。
    /// 退出码：0 = 全部通过；1 = 有不通过项。
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length > 0 && e.Args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
        {
            Environment.ExitCode = RunSelfTest(e.Args.Length > 1 ? e.Args[1] : null);
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    private static int RunSelfTest(string? configPath)
    {
        try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); } catch { /* 控制台不可重定向时忽略 */ }
        int fails = 0;
        void Check(string name, bool ok, string detail)
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + "  ::  " + detail);
            if (!ok) fails++;
        }

        var game = @"F:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game";
        configPath ??= Path.Combine(game, Kv3ConfigService.DefaultConfigRelative);

        Console.WriteLine("=== 1) config load: " + configPath + " ===");
        ConfigLoadResult cfg;
        try { cfg = Kv3ConfigService.Load(configPath); }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL  config load  ::  " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
        Check("KV3 header", cfg.EncodingName.Length > 0 && cfg.FormatName.Length > 0,
            "encoding=" + cfg.EncodingName + "(" + cfg.EncodingId + ") format=" + cfg.FormatName + "(" + cfg.FormatId + ")");
        Console.WriteLine("  KV3 path: " + Kv3ConfigService.NativeNote);
        // t22：把 Functions.Initialize 的解析日志逐条打出来供复核（CS2Fixes 式 Found … at 0x…）
        if (SDK.Native.Functions.ResolveLog.Count > 0)
        {
            Console.WriteLine("  --- Functions.Initialize 解析日志 ---");
            foreach (var l in SDK.Native.Functions.ResolveLog) Console.WriteLine("    " + l);
        }
        Check("file size", cfg.FileBytes == 32212, $"{cfg.FileBytes} B / {cfg.FileLines} lines");
        Check("assettypes rows", cfg.Types.Count == 71, $"actual {cfg.Types.Count} (expect 71, incl. quoted key 3dsmax)");
        Check("subassettypes rows", cfg.SubTypes.Count == 8, $"actual {cfg.SubTypes.Count} (expect 8, separate section)");
        Check("asset_warnings", cfg.WarningsCount == 1, $"{cfg.WarningsCount} group(s), title [{cfg.WarningTitle}]");
        int withComp = cfg.Types.Count(t => t.HasCompiler);
        Check("entries with compiler", withComp == 29, $"actual {withComp} (expect 29 = 28 resolvable + 1 miss)");
        Check("quoted key present", cfg.Types.Any(t => t.Name == "3dsmax"), "3dsmax is in the table (regex over text would miss it)");
        Console.WriteLine("  _class distribution: " + string.Join(" | ",
            cfg.Types
                .GroupBy(t => t.Class)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key} {g.Count()}{(g.Any(x => x.HasCompiler) ? $"({g.Count(x => x.HasCompiler)} w/ compiler)" : "")}"))
        );

        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine("=== 2) compiler registry (in-process read-only) ===");
        RegistrySnapshot reg;
        try
        {
            reg = RegistryService.Read(Path.Combine(game, @"bin\win64\resourcecompiler.dll"));
            RegistryService.ValidateTable(reg);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL  registry read  ::  {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        Check("registry entries", reg.Entries.Count == 43, $"actual {reg.Entries.Count} (table RVA {reg.TableRvaText}, index0 self-check passed)");
        Check("CreateInterface + RTTI", reg.SystemObject != IntPtr.Zero && reg.RttiOk, $"sys=0x{reg.SystemObject:X} RTTI={reg.RttiName}");
        Check("index0 identifier", reg.Entries.Count > 0 && reg.Entries[0].Identifier == "CompileWorldNode",
            reg.Entries.Count > 0 ? $"idx {reg.Entries[0].Index} = {reg.Entries[0].Identifier}" : "(empty)");

        Console.WriteLine();
        Console.WriteLine("=== 3) resolve every m_CompilerIdentifier ===");
        int hit = 0, miss = 0;
        var missed = new List<string>();
        foreach (var t in cfg.Types.Where(t => t.HasCompiler))
        {
            var r = RegistryService.Resolve(reg, t.CompilerIdentifier);
            if (r.Found && r.Entry is not null) { hit++; }
            else { miss++; missed.Add(t.Name + " -> " + t.CompilerIdentifier); }
        }
        Check("resolvable entries", hit == 28, $"hit {hit} / 29 (expect 28)");
        Check("missed entries", miss == 1, $"miss {miss} / 29 (expect 1)");
        foreach (var m in missed) Console.WriteLine($"    missed: {m}");
        int distinctHit = cfg.Types.Where(t => t.HasCompiler)
            .Select(t => t.CompilerIdentifier).Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(id => reg.Entries.Any(x => string.Equals(x.Identifier, id, StringComparison.OrdinalIgnoreCase)));
        Console.WriteLine($"  distinct level: {distinctHit}/27 hit (CompilePanorama 3 entries -> 1 distinct)");

        Console.WriteLine();
        Console.WriteLine(fails == 0 ? "===== SELFTEST: ALL PASS =====" : ($"===== SELFTEST: {fails} FAIL ====="));
        return fails == 0 ? 0 : 1;
    }
}
