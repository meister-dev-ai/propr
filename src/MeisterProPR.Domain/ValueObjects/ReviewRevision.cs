// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>Provider-neutral revision identity for a reviewable change state.</summary>
public sealed record ReviewRevision
{
    /// <summary>Initializes a new instance of the <see cref="ReviewRevision" /> class.</summary>
    /// <param name="headSha">The head SHA of the revision.</param>
    /// <param name="baseSha">The base SHA of the revision.</param>
    /// <param name="startSha">The optional start SHA of the revision.</param>
    /// <param name="providerRevisionId">The optional provider-specific revision identifier.</param>
    /// <param name="patchIdentity">The optional patch identity.</param>
    public ReviewRevision(
        string headSha,
        string baseSha,
        string? startSha,
        string? providerRevisionId,
        string? patchIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseSha);

        this.HeadSha = headSha.Trim();
        this.BaseSha = baseSha.Trim();
        this.StartSha = NormalizeOptional(startSha);
        this.ProviderRevisionId = NormalizeOptional(providerRevisionId);
        this.PatchIdentity = NormalizeOptional(patchIdentity);
    }

    /// <summary>Gets the head SHA of the revision.</summary>
    public string HeadSha { get; }

    /// <summary>Gets the base SHA of the revision.</summary>
    public string BaseSha { get; }

    /// <summary>Gets the optional start SHA of the revision.</summary>
    public string? StartSha { get; }

    /// <summary>Gets the optional provider-specific revision identifier.</summary>
    public string? ProviderRevisionId { get; }

    /// <summary>Gets the optional patch identity.</summary>
    public string? PatchIdentity { get; }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
