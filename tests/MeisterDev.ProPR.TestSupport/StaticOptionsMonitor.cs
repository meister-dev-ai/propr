// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.TestSupport;

/// <summary>
///     An options monitor over a fixed value, for tests that care what a component does with its settings rather
///     than how those settings change.
/// </summary>
public sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable OnChange(Action<T, string?> listener) => new NoSubscription();

    private sealed class NoSubscription : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
