// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.Security;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Forgejo;

public sealed class ForgejoPublicationContextContractTests
{
    [Fact]
    public void ProviderAdapters_RegisterForgejoPublicationUnderNeutralInterface()
    {
        var services = new ServiceCollection();

        services.AddSingleton(Substitute.For<IClientScmConnectionRepository>());
        services.AddForgejoProviderAdapters();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var publicationService = scope.ServiceProvider
            .GetServices<ICodeReviewPublicationService>()
            .Single(service => service.Provider == ScmProvider.Forgejo);

        Assert.IsType<ForgejoCodeReviewPublicationService>(publicationService);
    }

    [Fact]
    public async Task PublishReviewAsync_AcceptsPublicationContextWithoutChangingResultAccounting()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var revision = new ReviewRevision(
            "aabbccddeeff00112233445566778899aabbccdd",
            "00112233445566778899aabbccddeeff00112233",
            "00112233445566778899aabbccddeeff00112233",
            "aabbccddeeff00112233445566778899aabbccdd",
            "00112233445566778899aabbccddeeff00112233...aabbccddeeff00112233445566778899aabbccdd");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true);
        var result = new ReviewResult("Looks solid overall.", []);
        var publicationContext = new ReviewPublicationContext(
            review,
            revision,
            reviewer,
            [new PrCommentThread(1, null, null, [new PrThreadComment("Bot", "Summary")])]);
        var connectionRepository = ForgejoTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = ForgejoTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://codeberg.example.com/api/v1/user" => ForgejoTestHelpers.CreateJsonResponse(new { login = "meister-dev" }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews?limit=100" =>
                    ForgejoTestHelpers.CreateJsonResponse(Array.Empty<object>()),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews" =>
                    ForgejoTestHelpers.CreateJsonResponse(new { id = 9001 }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var sut = new ForgejoCodeReviewPublicationService(
            new ForgejoConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var diagnostics = await sut.PublishReviewAsync(clientId, review, revision, result, reviewer, publicationContext: publicationContext);

        Assert.Equal(0, diagnostics.PostedCount);
        Assert.Equal(0, diagnostics.SuppressedCount);
        Assert.Equal(0, diagnostics.CandidateCount);
    }

    [Fact]
    public async Task PublishReviewAsync_CapturesCreatedReviewCommentIds()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var revision = new ReviewRevision(
            "aabbccddeeff00112233445566778899aabbccdd",
            "00112233445566778899aabbccddeeff00112233",
            "00112233445566778899aabbccddeeff00112233",
            "aabbccddeeff00112233445566778899aabbccdd",
            "00112233445566778899aabbccddeeff00112233...aabbccddeeff00112233445566778899aabbccdd");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true);
        var result = new ReviewResult(
            "Looks solid overall.",
            [new ReviewComment("src/file.ts", 18, CommentSeverity.Warning, "Guard this null case.")]);

        var connectionRepository = ForgejoTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = ForgejoTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://codeberg.example.com/api/v1/user" => ForgejoTestHelpers.CreateJsonResponse(new { login = "meister-dev" }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews?limit=100" =>
                    ForgejoTestHelpers.CreateJsonResponse(Array.Empty<object>()),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews" =>
                    ForgejoTestHelpers.CreateJsonResponse(new { id = 9001L }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews/9001/comments" =>
                    ForgejoTestHelpers.CreateJsonResponse(
                        new[]
                        {
                            new { id = 4242, path = "src/file.ts", position = 18, original_position = (int?)null },
                        }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var sut = new ForgejoCodeReviewPublicationService(
            new ForgejoConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var diagnostics = await sut.PublishReviewAsync(clientId, review, revision, result, reviewer);

        var reference = Assert.Single(diagnostics.PostedComments);
        Assert.Equal("4242", reference.ProviderCommentId);
        Assert.Equal("9001", reference.ProviderThreadId);
        Assert.Equal("src/file.ts", reference.FilePath);
        Assert.Equal(18, reference.Line);
        Assert.Equal(1, diagnostics.PostedCount);
    }

    [Fact]
    public async Task PublishReviewAsync_WhenReviewCommentsEndpointUnavailable_PublishesWithEmptyPostedComments()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var revision = new ReviewRevision(
            "aabbccddeeff00112233445566778899aabbccdd",
            "00112233445566778899aabbccddeeff00112233",
            "00112233445566778899aabbccddeeff00112233",
            "aabbccddeeff00112233445566778899aabbccdd",
            "00112233445566778899aabbccddeeff00112233...aabbccddeeff00112233445566778899aabbccdd");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true);
        var result = new ReviewResult(
            "Looks solid overall.",
            [new ReviewComment("src/file.ts", 18, CommentSeverity.Warning, "Guard this null case.")]);

        var connectionRepository = ForgejoTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = ForgejoTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://codeberg.example.com/api/v1/user" => ForgejoTestHelpers.CreateJsonResponse(new { login = "meister-dev" }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews?limit=100" =>
                    ForgejoTestHelpers.CreateJsonResponse(Array.Empty<object>()),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews" =>
                    ForgejoTestHelpers.CreateJsonResponse(new { id = 9001L }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews/9001/comments" =>
                    ForgejoTestHelpers.CreateJsonResponse(new { message = "Not Found" }, HttpStatusCode.NotFound),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var sut = new ForgejoCodeReviewPublicationService(
            new ForgejoConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var diagnostics = await sut.PublishReviewAsync(clientId, review, revision, result, reviewer);

        Assert.Empty(diagnostics.PostedComments);
        Assert.Equal(1, diagnostics.PostedCount);
    }
}
