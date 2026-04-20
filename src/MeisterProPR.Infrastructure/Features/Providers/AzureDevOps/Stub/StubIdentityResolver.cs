// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Stub;

/// <summary>No-op identity resolver used when <c>ADO_STUB_PR=true</c>.</summary>
public sealed class StubIdentityResolver : IIdentityResolver
{
    /// <inheritdoc />
    public Task<IReadOnlyList<ResolvedIdentity>> ResolveAsync(
        string organizationUrl,
        string displayName,
        Guid clientId,
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ResolvedIdentity>>([]);
    }
}
