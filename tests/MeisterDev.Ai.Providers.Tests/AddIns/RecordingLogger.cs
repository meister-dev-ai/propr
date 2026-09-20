// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Logging;

namespace MeisterDev.Ai.Providers.Tests.AddIns;

/// <summary>Keeps every entry written to it, so a test can assert what was reported and how often.</summary>
internal sealed class RecordingLogger : ILogger
{
    /// <summary>Every entry, in the order it was written.</summary>
    public List<RecordedEntry> Entries { get; } = [];

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return null;
    }

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        this.Entries.Add(new RecordedEntry(logLevel, formatter(state, exception), exception));
    }

    /// <summary>One written entry.</summary>
    /// <param name="Level">How severe the writer said it was.</param>
    /// <param name="Message">The rendered message.</param>
    /// <param name="Exception">The exception carried with it, if any.</param>
    internal sealed record RecordedEntry(LogLevel Level, string Message, Exception? Exception);
}
