// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     Provider-aware persisted identity for a discovered source selection.
/// </summary>
public sealed record CanonicalSourceReference
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="CanonicalSourceReference"/> class.
    /// </summary>
    /// <param name="provider">The provider name.</param>
    /// <param name="value">The reference value.</param>
    /// <exception cref="ArgumentException">Thrown when provider or value is null or whitespace.</exception>
    public CanonicalSourceReference(string provider, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        this.Provider = provider.Trim();
        this.Value = value.Trim();
    }

    /// <summary>
    ///     Gets the provider name.
    /// </summary>
    public string Provider { get; }

    /// <summary>
    ///     Gets the reference value.
    /// </summary>
    public string Value { get; }
}
