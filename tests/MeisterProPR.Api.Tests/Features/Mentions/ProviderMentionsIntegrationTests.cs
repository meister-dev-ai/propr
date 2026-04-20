// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Threading.Channels;
using MeisterProPR.Api.Workers;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Features.Mentions;

public sealed class ProviderMentionsIntegrationTests
{
    [Fact]
    public async Task StartAsync_WithProviderNeutralPendingJob_PassesNormalizedContextToReplyService()
    {
        var channel = Channel.CreateUnbounded<MentionReplyJob>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IAsyncDisposable, IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var repo = Substitute.For<IMentionReplyJobRepository>();
        var replyService = Substitute.For<IMentionReplyService>();
        var pendingJob = CreateGitHubMentionReplyJob();

        ((IServiceScope)scope).ServiceProvider.Returns(serviceProvider);
        scopeFactory.CreateAsyncScope().Returns(new AsyncServiceScope((IServiceScope)scope));
        repo.GetPendingAsync(Arg.Any<CancellationToken>()).Returns([pendingJob]);
        serviceProvider.GetService(typeof(IMentionReplyJobRepository)).Returns(repo);
        serviceProvider.GetService(typeof(IMentionReplyService)).Returns(replyService);

        var worker = new MentionReplyWorker(
            channel.Reader,
            channel.Writer,
            scopeFactory,
            NullLogger<MentionReplyWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(50, CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        await replyService.Received(1)
            .ProcessAsync(
                Arg.Is<MentionReplyJob>(job =>
                    job.Provider == ScmProvider.GitHub &&
                    job.CodeReviewReference.Repository.Host.Provider == ScmProvider.GitHub &&
                    job.CodeReviewReference.ExternalReviewId == "7" &&
                    job.ReviewCommentReference != null &&
                    job.ReviewCommentReference.Author.Login == "octocat" &&
                    job.ReviewThreadReference.FilePath == "src/feature.ts"),
                Arg.Any<CancellationToken>());
    }

    private static MentionReplyJob CreateGitHubMentionReplyJob()
    {
        var job = new MentionReplyJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://dev.azure.com/org",
            "proj",
            "repo-gh-1",
            7,
            3,
            11,
            "@meister-dev-bot please help");

        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "repo-gh-1", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "7", 7);
        var thread = new ReviewThreadRef(review, "3", "src/feature.ts", 17, false);
        var comment = new ReviewCommentRef(
            thread,
            "11",
            new ReviewerIdentity(host, "user-1", "octocat", "Octo Cat", false),
            DateTimeOffset.UtcNow);

        job.SetProviderReviewContext(review);
        job.SetReviewThreadContext(thread);
        job.SetReviewCommentContext(comment);
        return job;
    }
}
