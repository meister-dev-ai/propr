// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Declaration;

/// <summary>
///     A stored vocabulary value split into the key that qualifies it and the mode name it carries.
/// </summary>
/// <param name="Key">
///     The identity key of the family that declared the mode, or <see langword="null" /> for a value carrying no
///     qualifier.
/// </param>
/// <param name="ModeName">The mode name.</param>
public readonly record struct ProviderQualifiedValue(string? Key, string ModeName)
{
    /// <summary>Whether the value carried a qualifier.</summary>
    public bool IsQualified => this.Key is not null;
}
