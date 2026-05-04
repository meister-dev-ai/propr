// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Diagnostics.Ports;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Features.Reviewing.Intake.Ports;
using MeisterProPR.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline.DependencyInjection;

/// <summary>
///     Registers offline Reviewing services backed by in-memory persistence.
/// </summary>
public static class OfflineReviewingServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the in-memory Reviewing seam used by the offline review-evaluation harness.
    /// </summary>
    public static IServiceCollection AddOfflineReviewing(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.TryAddScoped<IReviewEvaluationFixtureAccessor, ReviewEvaluationFixtureAccessor>();
        services.TryAddSingleton<InMemoryReviewJobRepository>();
        services.TryAddSingleton<IJobRepository>(sp => sp.GetRequiredService<InMemoryReviewJobRepository>());
        services.TryAddSingleton<IProtocolRecorder, InMemoryProtocolRecorder>();
        services.TryAddSingleton<IReviewDiagnosticsReader, InMemoryReviewDiagnosticsReader>();
        services.TryAddSingleton<IAiConnectionRepository, NoOpAiConnectionRepository>();
        services.TryAddSingleton<IProCursorGateway, NoOpProCursorGateway>();
        services.TryAddSingleton<IThreadMemoryRepository, NoOpThreadMemoryRepository>();
        services.TryAddSingleton<IMemoryActivityLog, NoOpMemoryActivityLog>();
        services.TryAddSingleton<IThreadMemoryService, NoOpThreadMemoryService>();
        services.TryAddSingleton<IReviewJobIntakeStore, OfflineReviewJobIntakeStore>();
        services.TryAddScoped<IReviewEvaluationFixtureValidator, ReviewEvaluationFixtureValidator>();
        services.TryAddScoped<IProtectedValueResolver, ConfigurationProtectedValueResolver>();
        services.TryAddScoped<IEvaluationArtifactWriter, JsonEvaluationArtifactWriter>();

        return services;
    }
}
