// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Infrastructure.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.ProCursor.Remote;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Offline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing;

/// <summary>
///     Composition root helpers for the dedicated review-evaluation harness.
/// </summary>
public static class ReviewEvalHarnessServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the Reviewing stack needed by the offline review-evaluation harness.
    /// </summary>
    public static IServiceCollection AddReviewEvalHarness(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddInfrastructureSupport(configuration);
        services.AddProCursorRemoteMode(configuration);
        services.AddSingleton<IClientRegistry, NoOpClientRegistry>();
        services.AddSingleton<IClientScmConnectionRepository, NoOpClientScmConnectionRepository>();
        services.AddSingleton<IClientScmScopeRepository, NoOpClientScmScopeRepository>();
        services.AddSingleton<IScmProviderRegistry, NoOpScmProviderRegistry>();
        services.AddReviewingModule(configuration);

        services.RemoveAll<ReviewOrchestrationService>();
        services.RemoveAll<IReviewJobProcessor>();
        services.AddScoped<IPromptExperimentBatchRunner, PromptExperimentBatchRunner>();
        services.AddScoped<IPullRequestFetcher, FixturePullRequestFetcher>();
        services.AddScoped<IReviewContextToolsFactory, FixtureReviewContextToolsFactory>();
        services.AddScoped<IRepositoryInstructionFetcher, FixtureRepositoryInstructionFetcher>();
        services.AddScoped<IRepositoryExclusionFetcher, FixtureRepositoryExclusionFetcher>();
        services.TryAddScoped<NoOpProCursorGateway>();
        services.RemoveAll<IProCursorGateway>();
        services.AddScoped<IProCursorGateway>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ProCursorRemoteOptions>>().Value;
            return options.IsRemoteEnabled
                ? sp.GetRequiredService<HttpProCursorGateway>()
                : sp.GetRequiredService<NoOpProCursorGateway>();
        });

        return services;
    }
}
