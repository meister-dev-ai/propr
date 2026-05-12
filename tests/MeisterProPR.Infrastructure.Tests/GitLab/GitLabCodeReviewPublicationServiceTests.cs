// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Reviewing;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Security;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.GitLab;

public sealed class GitLabPublicationContextContractTests
{
    [Fact]
    public void ProviderAdapters_RegisterGitLabPublicationUnderNeutralInterface()
    {
        var services = new ServiceCollection();

        services.AddSingleton(Substitute.For<IClientScmConnectionRepository>());
        services.AddGitLabProviderAdapters();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var publicationService = scope.ServiceProvider
            .GetServices<ICodeReviewPublicationService>()
            .Single(service => service.Provider == ScmProvider.GitLab);

        Assert.IsType<GitLabCodeReviewPublicationService>(publicationService);
    }

    [Fact]
    public async Task PublishReviewAsync_AcceptsPublicationContextWithoutChangingResultAccounting()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var repository = new RepositoryRef(host, "101", "acme/platform", "acme/platform/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", "start-sha", "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true);
        var result = new ReviewResult("Looks solid overall.", []);
        var publicationContext = new ReviewPublicationContext(
            review,
            revision,
            reviewer,
            [new PrCommentThread(1, null, null, [new PrThreadComment("Bot", "Summary")])]);
        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://gitlab.example.com/api/v4/user" => GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" }),
                "https://gitlab.example.com/api/v4/projects/101/merge_requests/42/discussions" =>
                    GitLabTestHelpers.CreateJsonResponse(new { id = "discussion-1" }, System.Net.HttpStatusCode.Created),
                _ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound),
            });

        var sut = new GitLabCodeReviewPublicationService(
            new GitLabConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var diagnostics = await sut.PublishReviewAsync(clientId, review, revision, result, reviewer, publicationContext: publicationContext);

        Assert.Equal(0, diagnostics.PostedCount);
        Assert.Equal(0, diagnostics.SuppressedCount);
        Assert.Equal(0, diagnostics.CandidateCount);
    }
}
