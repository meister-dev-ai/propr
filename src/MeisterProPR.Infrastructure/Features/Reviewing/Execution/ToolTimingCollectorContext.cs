// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution;

internal static class ToolTimingCollectorContext
{
    private static readonly AsyncLocal<ToolTimingCollector?> CurrentCollector = new();

    public static IDisposable BeginCollection()
    {
        var previous = CurrentCollector.Value;
        CurrentCollector.Value = new ToolTimingCollector();
        return new Scope(previous);
    }

    public static IReadOnlyList<ProtocolEventPhaseTiming>? CaptureSnapshot()
    {
        var phases = CurrentCollector.Value?.GetSnapshot();
        return phases is { Count: > 0 } ? phases : null;
    }

    public static async Task<T> RecordAsync<T>(
        string name,
        string displayName,
        Func<Task<T>> action,
        Func<T, string?>? summaryFactory = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        var collector = CurrentCollector.Value;
        if (collector is null)
        {
            return await action();
        }

        return await collector.RecordValueAsync(name, displayName, action, summaryFactory);
    }

    public static async Task RecordAsync(string name, string displayName, Func<Task> action, Func<string?>? summaryFactory = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        await RecordAsync<object?>(
            name,
            displayName,
            async () =>
            {
                await action();
                return null;
            },
            _ => summaryFactory?.Invoke());
    }

    public static T Record<T>(
        string name,
        string displayName,
        Func<T> action,
        Func<T, string?>? summaryFactory = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        var collector = CurrentCollector.Value;
        if (collector is null)
        {
            return action();
        }

        return collector.RecordValue(name, displayName, action, summaryFactory);
    }

    public static void Record(string name, string displayName, Action action, Func<string?>? summaryFactory = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        Record<object?>(
            name,
            displayName,
            () =>
            {
                action();
                return null;
            },
            _ => summaryFactory?.Invoke());
    }

    private sealed class Scope(ToolTimingCollector? previous) : IDisposable
    {
        public void Dispose()
        {
            CurrentCollector.Value = previous;
        }
    }

    private sealed class ToolTimingCollector
    {
        private readonly Dictionary<string, int> _occurrenceByName = new(StringComparer.Ordinal);
        private readonly List<ProtocolEventPhaseTiming> _phases = [];
        private int _nextSequence = 1;

        public async Task<T> RecordValueAsync<T>(
            string name,
            string displayName,
            Func<Task<T>> action,
            Func<T, string?>? summaryFactory)
        {
            var startedAt = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            var occurrence = this.NextOccurrence(name);

            try
            {
                var result = await action();
                this.AddPhase(
                    name, displayName, occurrence, startedAt, DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds, ProtocolEventTimingAvailabilities.Captured,
                    ProtocolEventToolOutcomes.Succeeded, summaryFactory?.Invoke(result));
                return result;
            }
            catch (OperationCanceledException ex)
            {
                this.AddPhase(
                    name, displayName, occurrence, startedAt, DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds, ProtocolEventTimingAvailabilities.Captured,
                    ProtocolEventToolOutcomes.Cancelled, ex.Message);
                throw;
            }
            catch (Exception ex)
            {
                this.AddPhase(
                    name, displayName, occurrence, startedAt, DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds, ProtocolEventTimingAvailabilities.Captured,
                    ProtocolEventToolOutcomes.Failed, ex.Message);
                throw;
            }
        }

        public T RecordValue<T>(
            string name,
            string displayName,
            Func<T> action,
            Func<T, string?>? summaryFactory)
        {
            var startedAt = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            var occurrence = this.NextOccurrence(name);

            try
            {
                var result = action();
                this.AddPhase(
                    name, displayName, occurrence, startedAt, DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds, ProtocolEventTimingAvailabilities.Captured,
                    ProtocolEventToolOutcomes.Succeeded, summaryFactory?.Invoke(result));
                return result;
            }
            catch (OperationCanceledException ex)
            {
                this.AddPhase(
                    name, displayName, occurrence, startedAt, DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds, ProtocolEventTimingAvailabilities.Captured,
                    ProtocolEventToolOutcomes.Cancelled, ex.Message);
                throw;
            }
            catch (Exception ex)
            {
                this.AddPhase(
                    name, displayName, occurrence, startedAt, DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds, ProtocolEventTimingAvailabilities.Captured,
                    ProtocolEventToolOutcomes.Failed, ex.Message);
                throw;
            }
        }

        public IReadOnlyList<ProtocolEventPhaseTiming> GetSnapshot()
        {
            return this._phases.Count == 0 ? [] : this._phases.ToList().AsReadOnly();
        }

        private int NextOccurrence(string name)
        {
            if (this._occurrenceByName.TryGetValue(name, out var occurrence))
            {
                occurrence++;
                this._occurrenceByName[name] = occurrence;
                return occurrence;
            }

            this._occurrenceByName[name] = 1;
            return 1;
        }

        private void AddPhase(
            string name,
            string displayName,
            int occurrence,
            DateTimeOffset startedAt,
            DateTimeOffset completedAt,
            long durationMs,
            string availability,
            string outcome,
            string? summary)
        {
            this._phases.Add(
                new ProtocolEventPhaseTiming(
                    name,
                    displayName,
                    this._nextSequence++,
                    occurrence > 1 ? occurrence : null,
                    startedAt,
                    completedAt,
                    durationMs,
                    availability,
                    outcome,
                    summary));
        }
    }
}
