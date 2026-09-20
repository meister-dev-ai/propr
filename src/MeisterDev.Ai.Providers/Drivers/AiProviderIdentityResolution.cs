// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Drivers;

/// <summary>
///     The outcome of reading a stored or transported provider identity against the loaded families, through
///     <see cref="AiProviderLegacyNames.ResolveIdentity" />.
/// </summary>
/// <remarks>
///     An identity no loaded family claims produces an unresolved outcome. The resolution neither throws nor
///     returns a null for one, so the caller decides what happens to it: report it as it stands, refuse the read,
///     or substitute a family. The identity is carried either way, because a caller that refuses has to name what
///     it refused, and a caller that keeps the identity for later needs the value it read.
/// </remarks>
public readonly record struct AiProviderIdentityResolution
{
    private readonly string? _name;
    private readonly string? _key;

    private AiProviderIdentityResolution(string? name, bool isResolved, string? key)
    {
        // Trimmed here rather than at each factory, so the identity Name reports is trimmed however the outcome
        // was built.
        this._name = name?.Trim();
        this.IsResolved = isResolved;
        this._key = key;
    }

    /// <summary>The identity as it was read, trimmed of surrounding whitespace.</summary>
    /// <remarks>
    ///     A resolved identity keeps the spelling it was read under rather than the claiming family's key, so a
    ///     caller writing a value back can leave a row holding a superseded spelling as it is until that row's own
    ///     rewrite. <see cref="TryGetKey" /> is what answers with the key.
    /// </remarks>
    public string Name => this._name ?? string.Empty;

    /// <summary>Whether a loaded family claims this identity.</summary>
    public bool IsResolved { get; }

    /// <summary>Gets the identity key of the family that claims this identity.</summary>
    /// <param name="key">The claiming family's declared key, when the identity resolved; otherwise empty.</param>
    /// <returns><see langword="true" /> when the identity resolved.</returns>
    public bool TryGetKey(out string key)
    {
        key = this._key ?? string.Empty;
        return this.IsResolved;
    }

    /// <summary>
    ///     The identity key of the family that claims this identity, or <paramref name="fallback" /> when no
    ///     loaded family claims it.
    /// </summary>
    /// <param name="fallback">The identity to stand in for one no loaded family claims.</param>
    public string KeyOr(string fallback)
    {
        return this.IsResolved ? this._key ?? string.Empty : fallback;
    }

    /// <summary>Reports an identity claimed by the family that declared <paramref name="key" />.</summary>
    /// <param name="name">The identity as it was read.</param>
    /// <param name="key">The claiming family's declared key.</param>
    internal static AiProviderIdentityResolution Resolved(string name, string key)
    {
        return new AiProviderIdentityResolution(name, true, key);
    }

    /// <summary>Reports an identity no loaded family claims.</summary>
    /// <param name="name">The identity as it was read.</param>
    internal static AiProviderIdentityResolution Unresolved(string name)
    {
        return new AiProviderIdentityResolution(name, false, null);
    }
}
