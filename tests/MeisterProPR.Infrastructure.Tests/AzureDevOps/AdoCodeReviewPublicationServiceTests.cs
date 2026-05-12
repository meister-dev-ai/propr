// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Azure.Core;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;
using MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Support;
using MeisterProPR.Infrastructure.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AzureDevOps;

public sealed class AdoCodeReviewPublicationServiceTests
{
    [Fact]
    public void ProviderAdapters_RegisterAzureDevOpsPublicationUnderNeutralInterface()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        services.AddSingleton(Substitute.For<IClientScmConnectionRepository>());
        services.AddSingleton(Substitute.For<IClientScmScopeRepository>());
        services.AddSingleton(Substitute.For<IAdoCommentPoster>());

        services.AddAzureDevOpsProviderAdapters();
        services.AddAzureDevOpsInfrastructureServices(configuration, Substitute.For<TokenCredential>());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var publicationService = scope.ServiceProvider
            .GetServices<ICodeReviewPublicationService>()
            .Single(service => service.Provider == ScmProvider.AzureDevOps);

        Assert.IsType<AdoCodeReviewPublicationService>(publicationService);
    }

    [Fact]
    public async Task PublishReviewAsync_PassesCompareIterationContextToCommentPoster()
    {
        var clientId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid().ToString("D");
        var connectionId = Guid.NewGuid();
        var scopeId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.AzureDevOps, "https://dev.azure.com/org-one");
        var repository = new RepositoryRef(host, repositoryId, "project-1", "project-1");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", "base-sha", "7", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "reviewer-guid", "meister-bot", "Meister Bot", true);
        var result = new ReviewResult("Looks solid.", []);
        IReadOnlyList<PrCommentThread> existingThreads =
        [
            new PrCommentThread(12, "/src/Foo.cs", 14, [new PrThreadComment("Bot", "Existing thread")]),
        ];

        var publicationContext = new ReviewPublicationContext(
            review,
            revision,
            reviewer,
            existingThreads,
            new AzureDevOpsPublicationContext(3));

        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetByClientIdAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(
            [
                new ClientScmConnectionDto(
                    connectionId,
                    clientId,
                    ScmProvider.AzureDevOps,
                    host.HostBaseUrl,
                    ScmAuthenticationKind.OAuthClientCredentials,
                    "Azure DevOps",
                    true,
                    "verified",
                    null,
                    null,
                    null,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow),
            ]);

        var scopeRepository = Substitute.For<IClientScmScopeRepository>();
        scopeRepository.GetByConnectionIdAsync(clientId, connectionId, Arg.Any<CancellationToken>())
            .Returns(
            [
                new ClientScmScopeDto(
                    scopeId,
                    clientId,
                    connectionId,
                    "organization",
                    "org-one",
                    host.HostBaseUrl,
                    "Org One",
                    "verified",
                    true,
                    null,
                    null,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow),
            ]);

        var commentPoster = Substitute.For<IAdoCommentPoster>();
        commentPoster.PostAsync(
                host.HostBaseUrl,
                "project-1",
                repositoryId,
                review.Number,
                7,
                result,
                clientId,
                Arg.Is<IReadOnlyList<PrCommentThread>?>(threads => threads == existingThreads),
                Arg.Is<AzureDevOpsPublicationContext?>(ctx => ctx!.CompareToIterationId == 3),
                Arg.Is<ReviewerIdentity?>(identity => identity == reviewer),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ReviewCommentPostingDiagnosticsDto.Empty()));

        var sut = new AdoCodeReviewPublicationService(
            connectionRepository,
            scopeRepository,
            new VssConnectionFactory(Substitute.For<TokenCredential>()),
            commentPoster);

        await sut.PublishReviewAsync(clientId, review, revision, result, reviewer, CancellationToken.None, publicationContext);

        await commentPoster.Received(1)
            .PostAsync(
                host.HostBaseUrl,
                "project-1",
                repositoryId,
                review.Number,
                7,
                result,
                clientId,
                Arg.Is<IReadOnlyList<PrCommentThread>?>(threads => threads == existingThreads),
                Arg.Is<AzureDevOpsPublicationContext?>(ctx => ctx!.CompareToIterationId == 3),
                Arg.Is<ReviewerIdentity?>(identity => identity == reviewer),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishReviewAsync_PassesPublicationIdentityToCommentPosterForSummarySuppressionFallback()
    {
        var clientId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid().ToString("D");
        var connectionId = Guid.NewGuid();
        var scopeId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.AzureDevOps, "https://dev.azure.com/org-one");
        var repository = new RepositoryRef(host, repositoryId, "project-1", "project-1");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", "base-sha", "7", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "configured-reviewer", "configured-reviewer", "Configured Reviewer", false);
        var publicationIdentity = new ReviewerIdentity(host, "meister-bot", "meister-bot", "Meister Bot", false);
        var result = new ReviewResult("Looks solid.", []);
        IReadOnlyList<PrCommentThread> existingThreads =
        [
            new PrCommentThread(12, null, null, [new PrThreadComment("Meister Bot", "**AI Review Summary**")]),
        ];

        var publicationContext = new ReviewPublicationContext(
            review,
            revision,
            publicationIdentity,
            existingThreads,
            new AzureDevOpsPublicationContext(3));

        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetByClientIdAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(
            [
                new ClientScmConnectionDto(
                    connectionId,
                    clientId,
                    ScmProvider.AzureDevOps,
                    host.HostBaseUrl,
                    ScmAuthenticationKind.OAuthClientCredentials,
                    "Azure DevOps",
                    true,
                    "verified",
                    null,
                    null,
                    null,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow),
            ]);

        var scopeRepository = Substitute.For<IClientScmScopeRepository>();
        scopeRepository.GetByConnectionIdAsync(clientId, connectionId, Arg.Any<CancellationToken>())
            .Returns(
            [
                new ClientScmScopeDto(
                    scopeId,
                    clientId,
                    connectionId,
                    "organization",
                    "org-one",
                    host.HostBaseUrl,
                    "Org One",
                    "verified",
                    true,
                    null,
                    null,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow),
            ]);

        var commentPoster = Substitute.For<IAdoCommentPoster>();
        commentPoster.PostAsync(
                host.HostBaseUrl,
                "project-1",
                repositoryId,
                review.Number,
                7,
                result,
                clientId,
                Arg.Is<IReadOnlyList<PrCommentThread>?>(threads => threads == existingThreads),
                Arg.Is<AzureDevOpsPublicationContext?>(ctx => ctx!.CompareToIterationId == 3),
                Arg.Is<ReviewerIdentity?>(identity => identity != null && identity.DisplayName == "Meister Bot"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ReviewCommentPostingDiagnosticsDto.Empty()));

        var sut = new AdoCodeReviewPublicationService(
            connectionRepository,
            scopeRepository,
            new VssConnectionFactory(Substitute.For<TokenCredential>()),
            commentPoster);

        await sut.PublishReviewAsync(clientId, review, revision, result, reviewer, CancellationToken.None, publicationContext);

        await commentPoster.Received(1)
            .PostAsync(
                host.HostBaseUrl,
                "project-1",
                repositoryId,
                review.Number,
                7,
                result,
                clientId,
                Arg.Is<IReadOnlyList<PrCommentThread>?>(threads => threads == existingThreads),
                Arg.Is<AzureDevOpsPublicationContext?>(ctx => ctx!.CompareToIterationId == 3),
                Arg.Is<ReviewerIdentity?>(identity => identity != null && identity.DisplayName == "Meister Bot"),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public void PublishReviewAsync_RenderingUsesSafeReadableFormattingContract()
    {
        var rendered = HtmlSanitizer.RenderForDisplay(
            "Run dotnet \"$ProCursorDll\" and remove <script>alert('xss')</script>.",
            ReviewBodyRenderingMode.ThreadReply);

        Assert.Contains("\"$ProCursorDll\"", rendered.RenderedText);
        Assert.DoesNotContain("&quot;", rendered.RenderedText);
        Assert.Equal(-1, rendered.RenderedText.IndexOf("<script>", StringComparison.Ordinal));
        Assert.True(rendered.ContainsUnsafeMarkup);
    }
}
