// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Application.DTOs;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>Resolves and validates provider reviewer identities.</summary>
public interface IReviewerIdentityService
{
    /// <summary>The provider family implemented by this adapter.</summary>
    ScmProvider Provider { get; }

    /// <summary>Derives a native automatic reviewer identity from an already selected connection.</summary>
    Task<ReviewerIdentity?> GetAutomaticReviewerIdentityAsync(
        ProviderHostRef host,
        ClientScmConnectionCredentialDto connection,
        CancellationToken ct = default) => Task.FromResult<ReviewerIdentity?>(null);

    /// <summary>Resolves candidate reviewer identities for a provider connection and search term.</summary>
    Task<IReadOnlyList<ReviewerIdentity>> ResolveCandidatesAsync(
        Guid clientId,
        ProviderHostRef host,
        string searchText,
        Guid? connectionId = null,
        CancellationToken ct = default);
}
