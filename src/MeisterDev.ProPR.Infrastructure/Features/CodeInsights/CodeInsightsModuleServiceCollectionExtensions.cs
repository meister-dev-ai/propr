// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.CodeInsights;
using MeisterDev.ProPR.Application.Features.CodeInsights.Events;
using MeisterDev.ProPR.Application.Features.CodeInsights.History;
using MeisterDev.ProPR.Application.Features.CodeInsights.Metrics;
using MeisterDev.ProPR.Application.Features.CodeInsights.Ports;
using MeisterDev.ProPR.Application.Features.CodeInsights.Rollups;
using MeisterDev.ProPR.Application.Features.CodeInsights.Survival;
using MeisterDev.ProPR.Application.Features.CodeInsights.Taxonomy;
using MeisterDev.ProPR.Infrastructure.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Classification;
using MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Dispositions;
using MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Events;
using MeisterDev.ProPR.Infrastructure.Features.CodeInsights.History;
using MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Metrics;
using MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Misses;
using MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Rollups;
using MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Survival;
using MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MeisterDev.ProPR.Infrastructure.Features.CodeInsights;

/// <summary>Registers the Code Insights collection module.</summary>
public static class CodeInsightsModuleServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the code-insight store and its passive ingestion consumer when database-backed runtime
    ///     services are available. Registration alone does not start collecting: the licence and per-client
    ///     opt-in gate decides that at call time.
    /// </summary>
    public static IServiceCollection AddCodeInsightsModule(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null)
    {
        if (!configuration.HasDatabaseConnectionString())
        {
            return services;
        }

        // The store takes an optional IDbContextFactory<MeisterProPRDbContext> and runs its reads and writes
        // on a fresh factory context, so a best-effort collection failure can never leave tracked entities
        // behind that poison the shared request-scoped context. The factory is registered by the
        // infrastructure module under the same database-connection-string gate as this module.
        // Every collection path consults the gate before writing a row or spending a token. It fails closed,
        // so registering the module does not by itself start any collection.
        services.AddScoped<ICodeInsightsCollectionGate, CodeInsightsCollectionGate>();

        // One store serves the five collection boundaries, so it is registered once and each port resolves to
        // that instance. Registering the class per interface would give a request as many stores as it happens to
        // touch, and each would carry its own change tracker.
        services.AddScoped<CodeInsightFindingStore>();
        services.AddScoped<ICodeInsightFindingStore>(sp => sp.GetRequiredService<CodeInsightFindingStore>());
        services.AddScoped<ICodeInsightClassificationStore>(sp => sp.GetRequiredService<CodeInsightFindingStore>());
        services.AddScoped<ICodeInsightDispositionStore>(sp => sp.GetRequiredService<CodeInsightFindingStore>());
        services.AddScoped<ICodeInsightMissStore>(sp => sp.GetRequiredService<CodeInsightFindingStore>());
        services.AddScoped<ICodeInsightRetentionStore>(sp => sp.GetRequiredService<CodeInsightFindingStore>());
        services.AddScoped<ICodeInsightFindingIngestionService, CodeInsightFindingIngestionService>();

        // The taxonomy service serves an admin surface rather than a best-effort side-write, so it uses the
        // shared request-scoped context like every other configuration repository.
        services.AddScoped<ICodeInsightTaxonomyService, CodeInsightTaxonomyService>();

        // Post-hoc type classification. The classifier resolves its model through the shared AI runtime
        // resolver, so its token cost lands in the existing per-client accounting without a second path.
        services.AddScoped<IFindingTypeClassifier, AiFindingTypeClassifier>();
        services.AddScoped<ICodeInsightClassificationSweeper, CodeInsightClassificationSweeper>();

        // Disposition back-tracking: a sibling of the thread-memory consumer on the same resolved-thread event.
        services.AddScoped<IDisregardedFindingClassifier, AiDisregardedFindingClassifier>();
        services.AddScoped<ICodeInsightDispositionService, CodeInsightDispositionService>();

        // Miss harvesting: the false-negative side, without which only precision is measurable.
        services.AddScoped<IHumanMissClassifier, AiHumanMissClassifier>();
        services.AddScoped<ICodeInsightMissHarvester, CodeInsightMissHarvester>();

        // Memory keywords: search metadata on resolution memories, extracted from text already stored on them.
        services.AddScoped<IMemoryKeywordExtractor, AiMemoryKeywordExtractor>();

        // The keyword backlog on memories stored before extraction existed. Off unless an installation asks for
        // it: every row costs a model call.
        services.AddScoped<ICodeInsightMemoryKeywordSweeper, CodeInsightMemoryKeywordSweeper>();

        // Roll-ups: one stored grain, day-bucketed, with the five reporting grains and the wider buckets
        // derived on read. The projector recomputes rather than increments, so it is safe to call repeatedly.
        services.AddScoped<ICodeInsightRollupProjector, CodeInsightRollupProjector>();
        services.AddScoped<ICodeInsightRollupReader, CodeInsightRollupReader>();

        // The two headline lenses. The seal is taken once when a pull request finishes; the reader serves
        // correctness from those seals and acceptance from the live projection, so acceptance is answerable
        // before any pull request has closed.
        services.AddScoped<ICodeInsightMetricSealer, CodeInsightMetricSealer>();

        // The closure a crawl-only installation never sees: quiet, unmeasured pull requests are asked about
        // directly so correctness is not limited to the pull requests that happened to end mid-review.
        services.AddScoped<ICodeInsightSealSweeper, CodeInsightSealSweeper>();
        services.AddScoped<ICodeInsightMetricReader, CodeInsightMetricReader>();

        // Drill-through: the records behind a number. A metric nobody can open up is a number nobody can check.
        services.AddScoped<ICodeInsightBrowseReader, CodeInsightBrowseReader>();

        // Survival: of what a review raised, how much was still being raised when the pull request finished.
        services.AddScoped<ICodeInsightSurvivalReader, CodeInsightSurvivalReader>();

        // Quality-condition transitions. The table is the contract for a future alerting consumer; there is no
        // publisher, bus, or handler seam, because there is nothing to hand an event to yet.
        services.AddScoped<ICodeInsightEventStore, CodeInsightEventStore>();
        services.AddScoped<ICodeInsightConditionEvaluator, CodeInsightConditionEvaluator>();

        // How much of the review history that already exists the collection knows about. Collection starts the
        // day it is switched on, so this is what turns "the numbers look thin" into a count per repository.
        services.AddScoped<ICodeInsightHistoryReader, CodeInsightHistoryReader>();

        return services;
    }
}
