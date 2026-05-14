// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.AI.FileByFileReview;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.DependencyInjection;

public sealed class ReviewingExecutionServiceCollectionExtensionsTests
{
    [Fact]
    public void AddReviewingExecution_RegistersScopedReviewHelpersForScopedDependencies()
    {
        var services = new ServiceCollection();

        services.AddReviewingExecution();

        Assert.Equal(ServiceLifetime.Scoped, GetLifetime<FileReviewDispatchPlanner>(services));
        Assert.Equal(ServiceLifetime.Scoped, GetLifetime<ReviewSynthesisExecutor>(services));
        Assert.Equal(ServiceLifetime.Singleton, GetLifetime<QualityFilterExecutor>(services));
    }

    private static ServiceLifetime GetLifetime<TService>(IServiceCollection services)
    {
        return services.Single(descriptor => descriptor.ServiceType == typeof(TService)).Lifetime;
    }
}
