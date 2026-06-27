// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Diagnostics.Ports;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Features.Reviewing.Intake.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Services;
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

        // Per-purpose model resolution for the offline harness. The accessor carries the active run's tiered
        // model map; the resolver maps each AiPurpose to a configured model (or throws like the persisted
        // resolver when no tiered selection is active, preserving single-model behavior). This override must
        // win over the persisted AiRuntimeResolver registered in AddInfrastructureSupport.
        services.TryAddScoped<IOfflineTierModelAccessor, OfflineTierModelAccessor>();
        services.RemoveAll<IAiRuntimeResolver>();
        services.AddScoped<IAiRuntimeResolver, OfflineConfigAiRuntimeResolver>();

        services.TryAddSingleton<InMemoryReviewJobRepository>();
        services.TryAddSingleton<IJobRepository>(sp => sp.GetRequiredService<InMemoryReviewJobRepository>());
        services.TryAddSingleton<IProtocolRecorder, InMemoryProtocolRecorder>();
        services.TryAddSingleton<IReviewDiagnosticsReader, InMemoryReviewDiagnosticsReader>();
        services.TryAddSingleton<IAiConnectionRepository, NoOpAiConnectionRepository>();
        services.TryAddSingleton<IProCursorGateway, NoOpProCursorGateway>();
        services.TryAddSingleton<IThreadMemoryEmbedder, NoOpThreadMemoryEmbedder>();
        services.TryAddScoped<IThreadMemoryRepository, FixtureThreadMemoryRepository>();
        services.TryAddSingleton<IMemoryActivityLog, NoOpMemoryActivityLog>();
        services.TryAddScoped<IThreadMemoryService, ThreadMemoryService>();
        services.TryAddSingleton<IReviewJobIntakeStore, OfflineReviewJobIntakeStore>();
        services.TryAddScoped<IReviewEvaluationFixtureValidator, ReviewEvaluationFixtureValidator>();
        services.TryAddScoped<IReviewPromptExperimentValidator, ReviewPromptExperimentValidator>();
        services.TryAddScoped<IProtectedValueResolver, ConfigurationProtectedValueResolver>();
        services.TryAddScoped<IEvaluationArtifactWriter, JsonEvaluationArtifactWriter>();

        return services;
    }
}
