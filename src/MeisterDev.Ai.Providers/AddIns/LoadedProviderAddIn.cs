// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.AddIns;

/// <summary>What the loader recorded about an add-in it took.</summary>
/// <param name="Key">The family's declared identity key.</param>
/// <param name="Label">The family's human-readable name.</param>
/// <param name="Version">The family's own version.</param>
/// <param name="ContractVersion">The contract version the family declared it was built against.</param>
/// <param name="ReachedHostPatterns">The hosts the family declared it contacts.</param>
/// <param name="RequiredCapabilityKey">
///     The premium capability the family declared it needs, or null when it needs none.
/// </param>
/// <param name="FilePath">The assembly the family came from.</param>
/// <param name="ContentHash">
///     The SHA-256 of that file as it was when the host started, lowercase hexadecimal, or null when the file
///     could not be read.
/// </param>
/// <param name="Origin">Which directory the assembly was found in.</param>
/// <remarks>
///     The first six values are strings the family states about itself and the host has nothing to check them
///     against. The file path and the content hash are what an operator can check: they identify the file that
///     produced those six, so a running installation can be compared against what was shipped. Neither is a
///     security boundary.
/// </remarks>
public sealed record LoadedProviderAddIn(
    string Key,
    string Label,
    string Version,
    string ContractVersion,
    IReadOnlyList<string> ReachedHostPatterns,
    string? RequiredCapabilityKey,
    string FilePath,
    string? ContentHash,
    ProviderAddInOrigin Origin);
