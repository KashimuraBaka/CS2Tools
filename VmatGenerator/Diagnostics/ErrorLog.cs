using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text;

namespace VmatGenerator.Diagnostics;

/// <summary>严重级别。</summary>
public enum ErrorSeverity
{
    /// <summary>普通信息（记录但不代表故障）。</summary>
    Info,
    /// <summary>警告：功能降级但未中断。</summary>
    Warning,
    /// <summary>错误：某个操作失败，已被拦截，应用继续运行。</summary>
    Error,
    /// <summary>致命错误：无法恢复，应用即将退出。</summary>
    Fatal,
}

/// <summary>一条错误记录。刻意做成不可变 record，便于跨线程安全传递。</summary>
/// <param name="Timestamp">发生时间。</param>
/// <param name="Severity">严重级别。</param>
/// <param name="Operation">触发错误的操作名（菜单 / 命令 / 事件）。</param>
/// <param name="Control">出错控件的描述（类型 / Name / 文本 / 绑定路径）。</param>
/// <param name="ExceptionType">异常类型全名。</param>
/// <param name="Message">异常消息。</param>
/// <param name="Detail">完整堆栈 + 内部异常链。</param>
/// <param name="ThreadId">发生线程。</param>
public sealed record ErrorEntry(
    DateTimeOffset Timestamp,
    ErrorSeverity Severity,
    string Operation,
    string Control,
    string ExceptionType,
    string Message,
    string Detail,
    int ThreadId)
{
    /// <summary>供 DataGrid 绑定的一行式摘要。</summary>
    public string TimeText => Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>级别中文显示。</summary>
    public string SeverityText => Severity switch
    {
        ErrorSeverity.Info => "信息",
        ErrorSeverity.Warning => "警告",
        ErrorSeverity.Error => "错误",
        _ => "致命",
    };
}

/// <summary>
/// 专用错误记录组件。负责把「哪个控件、在哪个操作、抛了什么异常」持久化到磁盘，
/// 同时保留一份内存环形缓冲供 UI 显示。
///
/// 设计约束：
/// <list type="bullet">
/// <item><b>绝不抛出异常</b> —— 记录器自身故障不能反过来弄崩应用，因此所有写盘
/// 路径都包在 try/catch 里。</item>
/// <item><b>线程安全</b> —— UI 线程、后台任务、异常处理器可能并发写入。</item>
/// <item><b>写用户可写目录</b> —— 写到 <c>%LOCALAPPDATA%\VmatGenerator\</c>，
/// 而不是 exe 旁边，避免「装到 Program Files 就写不进去」。</item>
/// <item><b>自动轮转</b> —— 超过 <see cref="MaxLogBytes"/> 时改名归档，防止日志无限增长。</item>
/// </list>
/// </summary>
public static class ErrorLog
{
    /// <summary>日志文件名。</summary>
    public const string FileName = "error.log";

    /// <summary>归档文件名（轮转后的上一份）。</summary>
    public const string ArchiveFileName = "error.previous.log";

    /// <summary>单个日志文件大小上限（2 MB），超过后轮转。</summary>
    public const long MaxLogBytes = 2 * 1024 * 1024;

    /// <summary>内存中保留的记录条数上限。</summary>
    public const int MaxRecentEntries = 500;

    private static readonly Lock Gate = new();
    private static readonly Queue<ErrorEntry> RecentQueue = new();
    private static bool _directoryResolved;

    /// <summary>提升事件：新增一条记录时触发（用于 UI 实时刷新）。</summary>
    public static event Action<ErrorEntry>? EntryLogged;

    /// <summary>本次运行中累计的错误条数（不含 Info/Warning）。</summary>
    public static int ErrorCount { get; private set; }

    /// <summary>
    /// 最近一次写盘失败的原因。正常情况下为 <c>null</c>；
    /// 若所有候选目录都不可写，这里会说明原因（界面可据此提示用户）。
    /// 注意：即使写盘失败，内存中的 <see cref="Recent"/> 仍然可用。
    /// </summary>
    public static string? LastWriteError { get; private set; }

    /// <summary>
    /// 日志目录。按优先级探测第一个<b>真正可写</b>的位置：
    /// <list type="number">
    /// <item><c>%LOCALAPPDATA%\VmatGenerator</c>（标准用户数据目录，推荐）</item>
    /// <item>可执行文件所在目录下的 <c>logs\</c>（便携模式）</item>
    /// <item><c>%TEMP%\VmatGenerator</c>（最后兜底）</item>
    /// </list>
    /// 每个候选目录都会真正试写一次，避免「配好了但不可写却在静默丢日志」。
    /// </summary>
    [AllowNull]
    public static string LogDirectory
    {
        get
        {
            if (_directoryResolved) return field ?? Path.GetTempPath();

            var failures = new StringBuilder();
            foreach (var candidate in CandidateDirectories())
            {
                try
                {
                    Directory.CreateDirectory(candidate);
                    // 真正写一个探针文件，确认可写。
                    var probe = Path.Combine(candidate, ".probe");
                    File.WriteAllText(probe, "1");
                    File.Delete(probe);

                    field = candidate;
                    LogFilePath = Path.Combine(candidate, FileName);
                    LastWriteError = null;
                    _directoryResolved = true;
                    return field;
                }
                catch (Exception ex)
                {
                    failures.AppendLine($"  {candidate} -> {ex.GetType().Name}: {ex.Message}");
                }
            }

            // 一个都不可写：记下原因，内存记录继续工作。
            LastWriteError = "所有候选日志目录均不可写：" + Environment.NewLine + failures.ToString().TrimEnd();
            field = Path.GetTempPath();
            LogFilePath = Path.Combine(field, FileName);
            _directoryResolved = true;
            return field;
        }

        private set;
    }

    /// <summary>按优先级列出候选日志目录。</summary>
    private static IEnumerable<string> CandidateDirectories()
    {
        var localAppData = string.Empty;
        try { localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); }
        catch { /* 忽略 */ }

        if (!string.IsNullOrEmpty(localAppData))
            yield return Path.Combine(localAppData, "VmatGenerator");

        // 注意：yield return 不能出现在带 catch 的 try 块里，因此先取值再返回。
        var exeDir = string.Empty;
        try { exeDir = AppContext.BaseDirectory; } catch { /* 忽略 */ }
        if (!string.IsNullOrEmpty(exeDir))
            yield return Path.Combine(exeDir, "logs");

        var temp = string.Empty;
        try { temp = Path.GetTempPath(); } catch { /* 忽略 */ }
        if (!string.IsNullOrEmpty(temp))
            yield return Path.Combine(temp, "VmatGenerator");
    }

    /// <summary>日志文件完整路径。</summary>
    [AllowNull]
    public static string LogFilePath
    {
        get
        {
            // LogDirectory 的解析会顺带设置 _logFilePath。
            _ = LogDirectory;
            return field ?? Path.Combine(Path.GetTempPath(), FileName);
        }

        private set;
    }

    /// <summary>清空缓存的日志目录解析结果（供测试或需要重新探测时使用）。</summary>
    public static void ResetDirectoryProbe()
    {
        lock (Gate)
        {
            LogDirectory = null;
            LogFilePath = null;
            _directoryResolved = false;
            LastWriteError = null;
        }
    }

    /// <summary>内存中的最近记录（只读快照，按时间正序）。</summary>
    public static IReadOnlyList<ErrorEntry> Recent
    {
        get { lock (Gate) return [.. RecentQueue]; }
    }

    /// <summary>
    /// 记录一条错误。这是组件的唯一公开入口，内部保证不抛异常。
    /// </summary>
    /// <param name="severity">严重级别。</param>
    /// <param name="operation">触发错误的操作名。</param>
    /// <param name="control">出错控件描述（见 <c>ControlErrorRecorder.Describe</c>）。</param>
    /// <param name="ex">异常；为 <c>null</c> 时视为一条普通信息记录。</param>
    public static void Write(ErrorSeverity severity, string operation, string control, Exception? ex)
    {
        ErrorEntry entry;
        try
        {
            entry = new ErrorEntry(
                Timestamp: DateTimeOffset.Now,
                Severity: severity,
                Operation: Safe(() => operation, "(未知操作)"),
                Control: Safe(() => control, "(未知控件)"),
                ExceptionType: ex?.GetType().FullName ?? "(无异常)",
                Message: ex?.Message ?? Safe(() => operation, string.Empty),
                Detail: BuildDetail(ex),
                ThreadId: Environment.CurrentManagedThreadId);

            if (severity is ErrorSeverity.Error or ErrorSeverity.Fatal) ErrorCount++;
        }
        catch
        {
            return; // 构造记录本身失败 —— 直接放弃，绝不影响调用方。
        }

        lock (Gate)
        {
            try
            {
                RecentQueue.Enqueue(entry);
                while (RecentQueue.Count > MaxRecentEntries) RecentQueue.Dequeue();
            }
            catch
            {
                // 忽略：内存记录失败不影响写盘。
            }
        }

        AppendToFile(entry);

        try { EntryLogged?.Invoke(entry); } catch { /* 订阅者异常不影响记录 */ }
    }

    /// <summary>便捷重载：普通信息记录。</summary>
    public static void Info(string operation, string control) => Write(ErrorSeverity.Info, operation, control, null);

    /// <summary>便捷重载：警告记录。</summary>
    public static void Warn(string operation, string control, Exception? ex = null) => Write(ErrorSeverity.Warning, operation, control, ex);

    /// <summary>便捷重载：错误记录。</summary>
    public static void Error(string operation, string control, Exception? ex) => Write(ErrorSeverity.Error, operation, control, ex);

    /// <summary>读取磁盘上的全部日志文本（用于「打开日志文件」失败时的兜底展示）。</summary>
    public static string ReadAll()
    {
        try
        {
            var path = LogFilePath;
            return File.Exists(path) ? File.ReadAllText(path) : "(日志文件尚不存在)";
        }
        catch (Exception ex)
        {
            return $"(读取日志失败：{ex.Message})";
        }
    }

    /// <summary>清空内存记录并截断磁盘日志。</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            RecentQueue.Clear();
            ErrorCount = 0;
        }
        try
        {
            var path = LogFilePath;
            if (File.Exists(path)) File.WriteAllText(path, string.Empty, Encoding.UTF8);
        }
        catch
        {
            // 忽略。
        }
    }

    /// <summary>构造异常的详细文本：完整堆栈 + 内部异常链。</summary>
    private static string BuildDetail(Exception? ex)
    {
        if (ex is null) return string.Empty;
        try
        {
            var sb = new StringBuilder();
            var e = ex;
            var depth = 0;
            while (e is not null && depth < 10)
            {
                if (depth > 0) sb.AppendLine("  ---- 内层异常 ----");
                sb.AppendLine(e.ToString());
                e = e.InnerException;
                depth++;
            }
            return sb.ToString().TrimEnd();
        }
        catch
        {
            return Safe(() => ex.Message, string.Empty);
        }
    }

    /// <summary>追加写入磁盘，必要时轮转。任何失败都被吞掉。</summary>
    private static void AppendToFile(ErrorEntry entry)
    {
        try
        {
            var path = LogFilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            RotateIfNeeded(path);

            var sb = new StringBuilder();
            sb.AppendLine("================================================================");
            sb.AppendLine($"时间   : {entry.TimeText}");
            sb.AppendLine($"级别   : {entry.SeverityText}");
            sb.AppendLine($"操作   : {entry.Operation}");
            sb.AppendLine($"控件   : {entry.Control}");
            sb.AppendLine($"异常   : {entry.ExceptionType}");
            sb.AppendLine($"消息   : {entry.Message}");
            sb.AppendLine($"线程   : {entry.ThreadId}");
            sb.AppendLine("----------------------------------------------------------------");
            sb.AppendLine(entry.Detail);
            sb.AppendLine();

            File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // 记录器绝不抛出。
        }
    }

    /// <summary>日志超限时把当前文件改名为归档文件。</summary>
    private static void RotateIfNeeded(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < MaxLogBytes) return;
            var archive = Path.Combine(Path.GetDirectoryName(path) ?? ".", ArchiveFileName);
            if (File.Exists(archive)) File.Delete(archive);
            File.Move(path, archive);
        }
        catch
        {
            // 轮转失败就继续追加。
        }
    }

    /// <summary>执行可能抛异常的委托，失败时返回默认值。</summary>
    private static string Safe(Func<string> get, string fallback)
    {
        try { return get() ?? fallback; } catch { return fallback; }
    }
}
