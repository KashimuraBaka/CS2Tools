// ============================================================================
// SDK.Native.EngineLog — 通过 tier0 的 LoggingSystem 发消息（t28j）。
//
// 用途：让 SDK 的进程内编译能像引擎那样产出日志消息——
//   assetsystem.dll 的 CResourceAsset::RecompileAsset 正是用
//   LoggingSystem_IsChannelEnabled + LoggingSystem_Log 打出
//   "Recompiling resource asset: '%s'" / "Reloading Resource [%d/%d]: %s" 的。
// 我们进程内没有资源系统实例，但日志通道可用（实测 channel 0 各级 enabled，
// sev=LS_ASSERT/LS_MESSAGE/LS_WARNING 可见、VERBOSE 以上被抑制），
// 因此可用同一套通道把编译进度/结果以引擎口吻发出。
//
// 实测（2026-10-20，tier0 0x129E80 / 0x12A780）：
//   IsChannelEnabled(ch, 1..3) = true；LogDirect(ch, sev<=3, 0, text) -> 消息落 stdout；
//   sev>=4 -> resp=1（LoggingResponse 抑制）。
// ============================================================================
using System.Runtime.InteropServices;

namespace SDK.Native;

/// <summary>tier0 LoggingSeverity_t 子集。</summary>
public enum LogSeverity
{
    Assert = 1,
    Message = 2,
    Warning = 3,
}

/// <summary>引擎日志通道（tier0 LoggingSystem）。</summary>
public static class EngineLog
{
    private const int RvaIsChannelEnabled = 0x129E80;
    private const int RvaLogDirect = 0x12A780;

    private static bool _ready;
    private static IsChannelEnabledFn? _isEnabled;
    private static LogDirectFn? _log;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool IsChannelEnabledFn(nint channel, int verbosity);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int LogDirectFn(nint channel, int severity, nint rareOptions, nint text);

    /// <summary>绑定 tier0 日志函数（Functions.Initialize 之后调用一次）。</summary>
    public static void Bind(Module tier0)
    {
        _isEnabled = tier0.ResolveDelegateAtRva<IsChannelEnabledFn>(RvaIsChannelEnabled);
        _log = tier0.ResolveDelegateAtRva<LogDirectFn>(RvaLogDirect);
        _ready = true;
    }

    /// <summary>通道是否启用。</summary>
    public static bool IsEnabled(nint channel = 0, LogSeverity severity = LogSeverity.Message)
        => _isEnabled is not null && _isEnabled(channel, (int)severity);

    /// <summary>
    /// 通过引擎日志通道发一条消息（等价引擎内 LoggingSystem_Log 通道 0）。
    /// </summary>
    /// <returns>引擎的 LoggingResponse_t（0 = 已记录）。</returns>
    public static int Write(string text, LogSeverity severity = LogSeverity.Message, nint channel = 0)
    {
        if (!_ready || _log is null) return -1;
        var p = Marshal.StringToCoTaskMemUTF8(text);
        try { return _log(channel, (int)severity, 0, p); }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>便捷：以引擎资源编译口吻发消息。</summary>
    public static int WriteRecompiling(string resourcePath)
    {
        var text = "Recompiling resource asset: '" + resourcePath + "'";
        return Write(text, LogSeverity.Message);
    }
}
