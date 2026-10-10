using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace SystemD.Crystal.Tests.Support;

/// <summary>A logger factory that keeps every entry with its level and event id (spec 006 R5.3, 007 R6.2).</summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly ConcurrentQueue<Entry> _entries = new();

    public sealed record Entry(LogLevel Level, EventId EventId, string Message, Exception? Exception);

    public IReadOnlyList<Entry> Entries => [.. _entries];

    public IEnumerable<string> At(LogLevel level) => _entries.Where(e => e.Level == level).Select(e => e.Message);

    public IReadOnlyList<Entry> Of(EventId id) => [.. _entries.Where(e => e.EventId.Id == id.Id)];

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLoggerFactory owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner._entries.Enqueue(new Entry(logLevel, eventId, formatter(state, exception), exception));
    }
}
