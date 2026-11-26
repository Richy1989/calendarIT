using Microsoft.Extensions.Logging;

namespace CalendarIT.Tests;

/// <summary>Keeps every line logged — rendered message and raw property values — for assertions.</summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Lines { get; } = [];

    public string All => string.Join("\n", Lines);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
            ? string.Join(" ", pairs.Select(p => $"{p.Key}={p.Value}"))
            : "";
        Lines.Add($"{logLevel}: {formatter(state, exception)} | {values} | {exception}");
    }
}
