// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.ThreadMemory.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Features.Reviewing.ThreadMemory.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Infrastructure.Features.Reviewing.ThreadMemory.DependencyInjection;

/// <summary>
///     Registers Reviewing thread-memory boundaries.
/// </summary>
public static class ReviewingThreadMemoryServiceCollectionExtensions
{
    /// <summary>
    ///     Registers Reviewing thread-memory adapters.
    /// </summary>
    public static IServiceCollection AddReviewingThreadMemory(this IServiceCollection services)
    {
        services.AddScoped<IReviewThreadMemoryStore>(sp =>
            new ReviewThreadMemoryStoreAdapter(sp.GetRequiredService<IThreadMemoryRepository>()));
        services.AddScoped<IReviewThreadMemoryService>(sp =>
            new ReviewThreadMemoryServiceAdapter(sp.GetRequiredService<IThreadMemoryService>()));

        return services;
    }
}
