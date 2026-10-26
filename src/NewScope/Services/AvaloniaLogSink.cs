using System.Collections.Concurrent;
using Avalonia.Logging;

namespace NewScope.Services;

public sealed class AvaloniaLogSink : ILogSink
{
    private readonly ConcurrentDictionary<string, byte> _seen = new();

    public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning;

    public void Log(
        LogEventLevel level,
        string area,
        object? source,
        string messageTemplate)
    {
        Log(level, area, source, messageTemplate, []);
    }

    public void Log(
        LogEventLevel level,
        string area,
        object? source,
        string messageTemplate,
        params object?[] propertyValues)
    {
        if (level < LogEventLevel.Warning)
        {
            return;
        }

        try
        {
            var message = propertyValues.Length == 0
                ? messageTemplate
                : $"{messageTemplate} | {string.Join(", ", propertyValues.Select(value => value ?? "(null)"))}";
            var sourceName = source?.GetType().Name ?? "unknown";
            var line = $"Avalonia {level} [{area}] {sourceName}: {message}";
            if (!_seen.TryAdd(line, 0))
            {
                return;
            }

            if (_seen.Count > 200)
            {
                _seen.Clear();
            }

            if (level >= LogEventLevel.Error)
            {
                AppLog.Error(line);
            }
            else
            {
                AppLog.Warn(line);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Avalonia log formatting failed for {area}: {ex.Message}");
        }
    }
}
