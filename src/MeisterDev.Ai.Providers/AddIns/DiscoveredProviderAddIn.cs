// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Immutable;

namespace MeisterDev.Ai.Providers.AddIns;

/// <summary>
///     An add-in the host found and did not load, because nobody has activated these bytes.
/// </summary>
/// <remarks>
///     Everything here was read out of the file with none of it executed: the hash from the bytes, the rest from
///     the assembly's manifest and its <see cref="Declaration.ProviderAddInAttribute" />. What the driver would
///     say about itself is not here, because asking it means running it.
/// </remarks>
/// <param name="FilePath">The assembly that was found.</param>
/// <param name="ContentHash">The SHA-256 of that file, which an activation is bound to.</param>
/// <param name="Origin">Which directory it came from.</param>
/// <param name="Key">The identity key it states, or null when it states no manifest.</param>
/// <param name="Label">What it calls itself, or null when it states no manifest.</param>
/// <param name="Version">The version it states, or null when it states no manifest.</param>
/// <param name="ContractVersion">The contract version it was built against, or null when it states no manifest.</param>
/// <param name="ReachedHosts">The hosts it says it contacts.</param>
/// <param name="RequiredCapability">The premium capability it needs, or null for none.</param>
/// <param name="AssemblyName">The assembly's own name, which is readable whether or not a manifest is stated.</param>
/// <param name="AssemblyVersion">The assembly's own version.</param>
/// <param name="Refusal">
///     Why this add-in cannot be activated as it stands, or null when it can. An add-in built against another
///     contract, or stating no manifest, is shown with the reason rather than hidden.
/// </param>
public sealed record DiscoveredProviderAddIn(
    string FilePath,
    string? ContentHash,
    ProviderAddInOrigin Origin,
    string? Key,
    string? Label,
    string? Version,
    string? ContractVersion,
    IReadOnlyList<string> ReachedHosts,
    string? RequiredCapability,
    string AssemblyName,
    string AssemblyVersion,
    string? Refusal)
{
    private readonly IReadOnlyList<string> _reachedHosts = Held(ReachedHosts);

    /// <summary>The hosts it says it contacts.</summary>
    public IReadOnlyList<string> ReachedHosts
    {
        get => this._reachedHosts;
        init => this._reachedHosts = Held(value);
    }

    /// <summary>Whether an administrator could activate this add-in as it stands.</summary>
    public bool CanBeActivated => this.Refusal is null && this.ContentHash is not null;

    private static IReadOnlyList<string> Held(IReadOnlyList<string>? value)
    {
        return value is null ? [] : value.ToImmutableArray();
    }
}
