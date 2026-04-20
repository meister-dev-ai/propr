// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Features.Providers.Common;

internal sealed class ProviderReviewContextToolsFactory(IEnumerable<IProviderReviewContextToolsFactory> factories)
    : IReviewContextToolsFactory
{
    private readonly IReadOnlyDictionary<ScmProvider, IProviderReviewContextToolsFactory> _factoriesByProvider =
        factories.ToDictionary(factory => factory.Provider);

    public IReviewContextTools Create(ReviewContextToolsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var provider = request.CodeReview.Repository.Host.Provider;
        if (!this._factoriesByProvider.TryGetValue(provider, out var factory))
        {
            throw new InvalidOperationException($"No review-context tools factory is registered for provider {provider}.");
        }

        return factory.Create(request);
    }
}
