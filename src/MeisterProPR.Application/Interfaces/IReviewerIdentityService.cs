// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Resolves and validates provider reviewer identities.</summary>
public interface IReviewerIdentityService
{
    /// <summary>The provider family implemented by this adapter.</summary>
    ScmProvider Provider { get; }

    /// <summary>Resolves candidate reviewer identities for a provider connection and search term.</summary>
    Task<IReadOnlyList<ReviewerIdentity>> ResolveCandidatesAsync(
        Guid clientId,
        ProviderHostRef host,
        string searchText,
        CancellationToken ct = default);
}
