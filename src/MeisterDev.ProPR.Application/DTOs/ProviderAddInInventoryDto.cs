// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>Every assembly the add-in loader saw when the host started.</summary>
/// <param name="Loaded">The families that were taken.</param>
/// <param name="Rejected">The assemblies that were skipped, each with why.</param>
/// <remarks>
///     Both lists reflect load time. Discovery is one pass and the directories are never rescanned, so this is
///     not a listing of what the directories hold now: an assembly deleted from a mounted volume under a running
///     host still appears as loaded and still serves.
/// </remarks>
public sealed record ProviderAddInInventoryDto(
    IReadOnlyList<LoadedProviderAddInDto> Loaded,
    IReadOnlyList<RejectedProviderAddInDto> Rejected,
    IReadOnlyList<AwaitingProviderAddInDto> Awaiting);

/// <summary>
///     One add-in found in the external directory that nobody has activated, so none of it has run.
/// </summary>
/// <remarks>
///     Every value here was read out of the file with none of it executed: the hash from the bytes, the rest
///     from the assembly's manifest and the attribute it states. What the driver would say about itself is not
///     here, because asking it means running it, and that is the decision being asked for.
/// </remarks>
/// <param name="FilePath">The assembly that was found.</param>
/// <param name="ContentHash">The SHA-256 of that file, which an activation is bound to.</param>
/// <param name="Key">The identity key it states, or null when it states no manifest.</param>
/// <param name="Label">What it calls itself, or null when it states no manifest.</param>
/// <param name="Version">The version it states, or null when it states no manifest.</param>
/// <param name="ContractVersion">The contract version it was built against, or null when it states no manifest.</param>
/// <param name="ReachedHosts">The hosts it says it will contact.</param>
/// <param name="RequiredCapability">The premium capability it needs, or null for none.</param>
/// <param name="AssemblyName">The assembly's own name, readable whether or not it states a manifest.</param>
/// <param name="AssemblyVersion">The assembly's own version.</param>
/// <param name="Refusal">Why it cannot be activated as it stands, or null when it can.</param>
/// <param name="CanBeActivated">Whether an administrator can activate it as it stands.</param>
public sealed record AwaitingProviderAddInDto(
    string FilePath,
    string? ContentHash,
    string? Key,
    string? Label,
    string? Version,
    string? ContractVersion,
    IReadOnlyList<string> ReachedHosts,
    string? RequiredCapability,
    string AssemblyName,
    string AssemblyVersion,
    string? Refusal,
    bool CanBeActivated);

/// <summary>One decision an administrator made to let this host run an add-in binary.</summary>
/// <param name="ContentHash">The bytes that were activated.</param>
/// <param name="Key">The identity the add-in stated at the time.</param>
/// <param name="Label">What it called itself at the time.</param>
/// <param name="Version">The version it stated at the time.</param>
/// <param name="FilePath">Where the file sat at the time.</param>
/// <param name="ActivatedByDisplayName">The administrator who decided.</param>
/// <param name="ActivatedAt">When they decided.</param>
/// <param name="IsServing">
///     Whether that add-in is one this host is running now. False for an activation whose file has since been
///     replaced or removed, and for one revoked since the host started.
/// </param>
public sealed record ProviderAddInActivationDto(
    string ContentHash,
    string Key,
    string Label,
    string Version,
    string FilePath,
    string ActivatedByDisplayName,
    DateTimeOffset ActivatedAt,
    bool IsServing);

/// <summary>One provider family the host loaded from an add-in directory.</summary>
/// <param name="Key">The family's identity key, which a connection is stored against.</param>
/// <param name="Label">The family's human-readable name. Two families may share one, so the key is shown beside it.</param>
/// <param name="Version">The family's own version.</param>
/// <param name="ContractVersion">The contract version the family declared it was built against.</param>
/// <param name="ReachedHostPatterns">The hosts the family declared it contacts.</param>
/// <param name="RequiredCapabilityKey">The premium capability the family declared it needs, or null for none.</param>
/// <param name="FilePath">The assembly the family came from.</param>
/// <param name="ContentHash">The SHA-256 of that file when the host read it, or null when it could not be read.</param>
/// <param name="Origin">Which directory it came from: <c>built-in</c> or <c>external</c>.</param>
/// <remarks>
///     The first six are values the family states about itself and the host has nothing to check them against.
///     The file path and the content hash identify the file that produced them, which an operator can
///     check a running installation against.
/// </remarks>
public sealed record LoadedProviderAddInDto(
    string Key,
    string Label,
    string Version,
    string ContractVersion,
    IReadOnlyList<string> ReachedHostPatterns,
    string? RequiredCapabilityKey,
    string FilePath,
    string? ContentHash,
    string Origin);

/// <summary>One assembly the host found in an add-in directory and did not load.</summary>
/// <param name="Category">
///     Which kind of failure this was: <c>failed</c>, <c>duplicate</c>, <c>mis-packaged</c>,
///     <c>version-mismatch</c> or <c>non-conforming</c>.
/// </param>
/// <param name="Reason">What went wrong, in terms an operator can act on.</param>
/// <param name="FilePath">The assembly that was skipped.</param>
/// <param name="ContentHash">The SHA-256 of that file, or null when it could not be read.</param>
/// <param name="Key">The family's identity key, where the host got far enough to read one.</param>
/// <param name="Origin">Which directory it came from: <c>built-in</c> or <c>external</c>.</param>
public sealed record RejectedProviderAddInDto(
    string Category,
    string Reason,
    string FilePath,
    string? ContentHash,
    string? Key,
    string Origin);
