// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MeisterDev.ProPR.CodeInsights;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Events;
using MeisterDev.ProPR.CodeInsights.History;
using MeisterDev.ProPR.CodeInsights.Metrics;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.CodeInsights.Rollups;
using MeisterDev.ProPR.CodeInsights.Survival;
using MeisterDev.ProPR.CodeInsights.Taxonomy;
using MeisterDev.ProPR.CodeInsights.Classification;
using MeisterDev.ProPR.CodeInsights.Dispositions;
using MeisterDev.ProPR.CodeInsights.Misses;
using MeisterDev.ProPR.CodeInsights.Persistence;

namespace MeisterDev.ProPR.CodeInsights;

/// <summary>Registers the Code Insights collection module.</summary>
public static class CodeInsightsModuleServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the code-insight store and its passive ingestion consumer when database-backed runtime
    ///     services are available. Registration alone does not start collecting: the licence and per-client
    ///     opt-in gate decides that at call time.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This module does not register everything its own services need. The three classifiers take
    ///         <c>IAiRuntimeResolver</c> and <c>IModelUsageRecorder</c> as required dependencies, and neither is
    ///         registered here: the resolver comes from the AI module and the usage recorder from the
    ///         usage-reporting module, which owns the daily usage row it writes to. A host that composes this
    ///         module without those two gets no error at startup, because nothing validates the graph; the
    ///         failure appears when the first classification sweep tries to build a classifier, and that
    ///         sweep's handler swallows it, so classification silently never happens.
    ///     </para>
    ///     <para>
    ///         Call this after the AI and usage-reporting modules, as <c>Program.cs</c> does. Two tests pin the
    ///         seam: one resolves every classifier from this module, and one asserts that the usage-reporting
    ///         module is where the recorder comes from.
    ///     </para>
    /// </remarks>
    public static IServiceCollection AddCodeInsightsModule(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null)
    {
        // Bound before the database gate, so the workers resolve their settings even on a host where the rest
        // of the module stays inert. Read from the same flat environment keys the host already documents, so
        // nothing deployed has to change: the options type is how the code reads them, not a new contract.
        services.AddOptions<CodeInsightsOptions>().Configure(options => BindOptions(options, configuration));

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
        // resolver, so it is configured wherever every other purpose is.
        services.AddScoped<IFindingTypeClassifier, AiFindingTypeClassifier>();
        services.AddScoped<ICodeInsightClassificationSweeper, CodeInsightClassificationSweeper>();

        // Disposition back-tracking: a sibling of the thread-memory consumer on the same resolved-thread event.
        services.AddScoped<IDisregardedFindingClassifier, AiDisregardedFindingClassifier>();
        services.AddScoped<ICodeInsightDispositionService, CodeInsightDispositionService>();

        // Miss harvesting: the false-negative side, without which only precision is measurable.
        services.AddScoped<IHumanMissClassifier, AiHumanMissClassifier>();
        services.AddScoped<ICodeInsightMissHarvester, CodeInsightMissHarvester>();

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

        // The importer replays history through the same consumers the live path uses, so it takes them as
        // dependencies rather than reaching for the store. The two archive stores are optional: without them a run
        // still imports findings, and says how many could never be linked to a thread.
        services.AddScoped<ICodeInsightHistoryImporter, CodeInsightHistoryImporter>();

        return services;
    }

    /// <summary>
    ///     Maps the installation's environment keys onto the options record. Absent keys keep the property
    ///     defaults, and every floor lives on the options type rather than here. Public so the mapping itself can
    ///     be tested: it is the contract between what an operator sets and what the workers read.
    /// </summary>
    public static void BindOptions(CodeInsightsOptions options, IConfiguration configuration)
    {
        options.ClassificationIntervalSeconds = configuration.GetValue(
            "CODE_INSIGHTS_CLASSIFICATION_INTERVAL_SECONDS",
            options.ClassificationIntervalSeconds);
        options.CatchUpIntervalSeconds = configuration.GetValue(
            "CODE_INSIGHTS_CATCHUP_INTERVAL_SECONDS",
            options.CatchUpIntervalSeconds);
        options.ConditionIntervalSeconds = configuration.GetValue(
            "CODE_INSIGHTS_CONDITION_INTERVAL_SECONDS",
            options.ConditionIntervalSeconds);
        options.PurgeIntervalSeconds = configuration.GetValue(
            "CODE_INSIGHTS_PURGE_INTERVAL_SECONDS",
            options.PurgeIntervalSeconds);
        options.RetentionDays = configuration.GetValue("CODE_INSIGHTS_RETENTION_DAYS", options.RetentionDays);
        options.BackfillMaxJobs = configuration.GetValue(
            "CODE_INSIGHTS_BACKFILL_MAX_JOBS",
            options.BackfillMaxJobs);
        options.SealSweepMaxPullRequests = configuration.GetValue(
            "CODE_INSIGHTS_SEAL_SWEEP_MAX_PULL_REQUESTS",
            options.SealSweepMaxPullRequests);
        options.SealSweepIdleDays = configuration.GetValue(
            "CODE_INSIGHTS_SEAL_SWEEP_IDLE_DAYS",
            options.SealSweepIdleDays);
        options.ConditionWindowDays = configuration.GetValue(
            "CODE_INSIGHTS_CONDITION_WINDOW_DAYS",
            options.ConditionWindowDays);
        options.F1DeclineThreshold = configuration.GetValue(
            "CODE_INSIGHTS_F1_DECLINE_THRESHOLD",
            options.F1DeclineThreshold);
        options.FalsePositiveShareThreshold = configuration.GetValue(
            "CODE_INSIGHTS_FALSE_POSITIVE_SHARE_THRESHOLD",
            options.FalsePositiveShareThreshold);
        options.ConcentrationThreshold = configuration.GetValue(
            "CODE_INSIGHTS_CONCENTRATION_THRESHOLD",
            options.ConcentrationThreshold);
        options.MinimumSealedPullRequests = configuration.GetValue(
            "CODE_INSIGHTS_MIN_SEALED_PULL_REQUESTS",
            options.MinimumSealedPullRequests);
    }
}
