// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using MeisterDev.ProPR.TestSupport;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing;

public sealed class ReviewingExposureCompositionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RegisteredFileReviewer_CompletesReviewAndRecordsExposureWhenCollectorIsAvailable(bool collectExposure)
    {
        var core = Substitute.For<IAiReviewCore>();
        core.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("No findings.", []));
        var repository = Substitute.For<IJobRepository>();
        ReviewFileResult? completed = null;
        repository.When(value => value.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>()))
            .Do(call => completed = call.ArgAt<ReviewFileResult>(0));
        var collector = Substitute.For<ICodeInsightReviewExposureCollector>();

        var services = CreateIsolatedServices(core, repository);
        if (collectExposure)
        {
            services.AddSingleton(collector);
            services.AddSingleton(LocalScmPolicies.Registry);
        }

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var registry = scope.ServiceProvider.GetService<IScmProviderRegistry>();
        if (collectExposure)
        {
            var registeredRegistry = Assert.IsAssignableFrom<IScmProviderRegistry>(registry);
            Assert.Same(LocalScmPolicies.Registry, registeredRegistry);
            Assert.False(registeredRegistry.IsRegistered(ScmProvider.AzureDevOps));
        }
        else
        {
            Assert.Null(registry);
        }

        var reviewer = scope.ServiceProvider.GetRequiredService<FileReviewer>();
        var file = new ChangedFile("src/Example.cs", ChangeType.Edit, "class Example {}", "@@ -1 +1 @@\n+class Example {}", false);
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "project", "repository", 32, 1);
        var pr = new PullRequest(
            "https://dev.azure.com/org", "project", "repository", "repository", 32, 1,
            "Review", null, "feature/example", "main", [file]);
        var context = new ReviewSystemContext(null, [], null) { ModelId = "review-model" };

        await reviewer.ReviewAsync(job, pr, file, 1, 1, context, null, Substitute.For<IChatClient>(), CancellationToken.None);

        Assert.NotNull(completed);
        Assert.True(completed.IsComplete);
        Assert.Empty(completed.Comments!);
        await core.Received(1).ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());
        await collector.Received(collectExposure ? 1 : 0).RecordAsync(
            Arg.Is<CodeInsightPullRequestKey>(key =>
                key.ClientId == job.ClientId && key.RepositoryId == job.RepositoryId && key.PullRequestId == job.PullRequestId),
            job.Id, file.Path, Arg.Any<string>(), "review-model", Arg.Is<string?>(value => value == null), "AzureDevOps:https://dev.azure.com",
            "completed-file-baseline", Arg.Any<DateTimeOffset>(), CancellationToken.None);
    }

    [Fact]
    public void RegisteredFileReviewer_WithCollectorWithoutSourcePolicies_RejectsComposition()
    {
        var services = CreateIsolatedServices(Substitute.For<IAiReviewCore>(), Substitute.For<IJobRepository>());
        services.AddSingleton(Substitute.For<ICodeInsightReviewExposureCollector>());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Null(scope.ServiceProvider.GetService<IScmProviderRegistry>());
        var error = Assert.Throws<ArgumentNullException>(() => scope.ServiceProvider.GetRequiredService<FileReviewer>());
        Assert.Equal("providerRegistry", error.ParamName);
    }

    private static IServiceCollection CreateIsolatedServices(IAiReviewCore core, IJobRepository repository)
    {
        var moduleServices = new ServiceCollection();
        moduleServices.AddReviewingModule(new ConfigurationBuilder().Build());
        var registration = Assert.Single(moduleServices, descriptor => descriptor.ServiceType == typeof(FileReviewer));
        IServiceCollection services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(core);
        services.AddSingleton(repository);
        services.AddSingleton(Substitute.For<IProtocolRecorder>());
        services.AddSingleton<IOptions<AiReviewOptions>>(Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()));
        services.Add(registration);
        return services;
    }
}
