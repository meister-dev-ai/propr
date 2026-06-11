// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Features.Providers.Common;

internal sealed class ProviderReviewWorkspaceRemoteResolver(IEnumerable<IProviderReviewWorkspaceRemoteResolver> resolvers) : IReviewWorkspaceRemoteResolver
{
    private readonly IReadOnlyDictionary<ScmProvider, IProviderReviewWorkspaceRemoteResolver> _resolversByProvider =
        resolvers.ToDictionary(resolver => resolver.Provider);

    public ScmProvider Provider => throw new NotSupportedException("The provider-neutral review workspace resolver dispatches by request provider.");

    public Task<ReviewWorkspaceRemoteRef> ResolveAsync(ReviewRepositoryWorkspaceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!this._resolversByProvider.TryGetValue(request.Provider, out var resolver))
        {
            throw new InvalidOperationException($"No review workspace remote resolver is registered for provider {request.Provider}.");
        }

        return resolver.ResolveAsync(request, ct);
    }
}
