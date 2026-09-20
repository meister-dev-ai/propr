// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Immutable;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Contracts;

/// <summary>
///     Outcome of probing a provider for reachability and auth. Deliberately carries a specific cause and an
///     action hint rather than a bare boolean, so a misconfiguration can be reported at configuration time
///     instead of surfacing later as a failed workload.
/// </summary>
/// <param name="Status">Normalized verification status.</param>
/// <param name="FailureCategory">Category of failure when verification did not succeed.</param>
/// <param name="Summary">Short human-readable outcome.</param>
/// <param name="ActionHint">What an operator should change, when the cause suggests one.</param>
/// <param name="CheckedAt">When the probe ran.</param>
/// <param name="Warnings">Non-fatal notes about the attempt.</param>
/// <param name="DriverMetadata">Driver-specific detail worth surfacing, with no secret material.</param>
public sealed record ProviderVerificationResult(
    AiVerificationStatus Status,
    AiVerificationFailureCategory? FailureCategory = null,
    string? Summary = null,
    string? ActionHint = null,
    DateTimeOffset? CheckedAt = null,
    IReadOnlyList<string>? Warnings = null,
    IReadOnlyDictionary<string, string>? DriverMetadata = null)
{
    // Held in fields so the copy is made wherever either collection is set: a positional argument, an object
    // initializer and a `with` expression all reach the property, and a property with an accessor body cannot
    // carry a field initializer of its own.
    private readonly IReadOnlyList<string>? _warnings = Held(Warnings);
    private readonly IReadOnlyDictionary<string, string>? _driverMetadata = Held(DriverMetadata);
    private readonly AiVerificationStatus _status = Agreeing(Status, FailureCategory);
    private readonly AiVerificationFailureCategory? _failureCategory = FailureCategory;

    /// <summary>Normalized verification status.</summary>
    public AiVerificationStatus Status
    {
        get => this._status;
        init => this._status = Agreeing(value, this.FailureCategory);
    }

    /// <summary>
    ///     Category of failure when verification did not succeed, and null otherwise.
    /// </summary>
    /// <remarks>
    ///     The two are one fact stated twice, so they are checked against each other wherever either is set. A
    ///     failure with no category reaches an operator as "it did not work" with nothing to act on, and a
    ///     success carrying one is read by the console's own branch on the category and shown as a problem.
    ///     Both directions are checked, so neither a <c>with</c> that changes only the status nor one that
    ///     changes only the category can leave the pair disagreeing. A <c>with</c> that changes both is read in
    ///     source order, so the status has to be assigned after the category it belongs with, which is the same
    ///     constraint the status accessor already imposed.
    /// </remarks>
    public AiVerificationFailureCategory? FailureCategory
    {
        get => this._failureCategory;
        init
        {
            Agreeing(this.Status, value);
            this._failureCategory = value;
        }
    }

    /// <summary>A never-verified snapshot, used before any probe has run.</summary>
    public static ProviderVerificationResult NeverVerified { get; } =
        new(AiVerificationStatus.NeverVerified, null, null, null, null, []);

    /// <summary>Non-fatal notes about the attempt.</summary>
    /// <remarks>
    ///     Copied wherever it is set. A result is handed to the host and kept — the never-verified snapshot is a
    ///     static every connection starts from — and a read-only list over an array or a List is cast back to it
    ///     in one line.
    /// </remarks>
    public IReadOnlyList<string>? Warnings
    {
        get => this._warnings;
        init => this._warnings = Held(value);
    }

    /// <summary>Driver-specific detail worth surfacing, with no secret material.</summary>
    public IReadOnlyDictionary<string, string>? DriverMetadata
    {
        get => this._driverMetadata;
        init => this._driverMetadata = Held(value);
    }

    private static AiVerificationStatus Agreeing(
        AiVerificationStatus status,
        AiVerificationFailureCategory? category)
    {
        if (status == AiVerificationStatus.Failed && category is null)
        {
            throw new ArgumentException(
                "A failed verification states why it failed. Without a category an operator is told it did not "
                + "work and nothing they can act on.",
                nameof(ProviderVerificationResult.FailureCategory));
        }

        return status != AiVerificationStatus.Failed && category is not null
            ? throw new ArgumentException(
                $"A verification that reads as '{status}' carries no failure category, and this one states "
                + $"'{category}'.",
                nameof(ProviderVerificationResult.FailureCategory))
            : status;
    }

    private static IReadOnlyList<string>? Held(IReadOnlyList<string>? value)
    {
        return value?.ToImmutableArray();
    }

    private static IReadOnlyDictionary<string, string>? Held(IReadOnlyDictionary<string, string>? value)
    {
        return value?.ToImmutableDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
    }
}
