// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     Provider-aware persisted identity for a discovered source selection.
/// </summary>
public sealed record CanonicalSourceReference
{
    public CanonicalSourceReference(string provider, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        this.Provider = provider.Trim();
        this.Value = value.Trim();
    }

    public string Provider { get; }

    public string Value { get; }
}
