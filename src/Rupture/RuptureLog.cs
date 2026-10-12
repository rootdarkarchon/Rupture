using BepInEx.Logging;
using System.Diagnostics;

namespace Rupture;

// Verbose diagnostics are opt-in; failures remain visible with a bounded rate.
internal sealed class RuptureLog
{
    private readonly ManualLogSource _source;
    private readonly object _gate = new object();
    private long _windowStarted;
    private int _failureMessages;
    internal bool IsDebugEnabled => RuptureConfiguration.EnableDebugLogging?.Value == true;
    internal RuptureLog(ManualLogSource source) { _source = source; }
    internal void Initialization(object message) => _source.LogInfo(message);
    internal void InitializationWarning(object message) => _source.LogWarning(message);
    internal void Log(LogLevel level, object message)
    {
        if (IsDebugEnabled) { _source.Log(level, message); return; }
        if ((level & (LogLevel.Warning | LogLevel.Error | LogLevel.Fatal)) == 0) return;
        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            if (now - _windowStarted >= Stopwatch.Frequency * 60L)
            {
                _windowStarted = now;
                _failureMessages = 0;
            }
            if (_failureMessages >= 60) return;
            _failureMessages++;
            _source.Log(level, message);
            if (_failureMessages == 60) _source.LogWarning("Further Rupture warnings/errors are suppressed until the next minute. Enable debug logging for full diagnostics.");
        }
    }
    internal void LogInfo(object message) => Log(LogLevel.Info, message);
    internal void LogWarning(object message) => Log(LogLevel.Warning, message);
    internal void LogError(object message) => Log(LogLevel.Error, message);
    internal void LogDebug(object message) => Log(LogLevel.Debug, message);
}
