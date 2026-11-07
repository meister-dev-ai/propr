// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Features.ReviewArchive;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Application.Tests.Services;

/// <summary>
///     The identity a review is published under. A review whose file reviews failed on the last attempt publishes its
///     partial result under the same identity as a completed review: the account the pull request fetch authenticated
///     as.
/// </summary>
public sealed class ReviewOrchestrationServicePublicationIdentityTests
{
    private const string AppBotLogin = "propr-review-meister-dev[bot]";

    [Fact]
    public async Task ThePartialResultOfFailedFileReviews_IsPublishedUnderTheBotLoginTheFetchAuthenticatedAs()
    {
        var job = NewGitHubJob();
        var harness = Harness.Create(PullRequestFor(job, AppBotLogin), FailOneOfTwoFileReviews);

        await harness.Service.ProcessAsync(job, CancellationToken.None);

        var (identity, _) = harness.SinglePublication();
        Assert.Equal(AppBotLogin, identity.DisplayName);
        Assert.Equal(AppBotLogin, identity.Login);
        Assert.True(identity.IsBot);
    }

    // The provider adapter looks for the threads ProPR already raised among the threads in the publication context.
    // Without them, the Azure DevOps adapter posts a second summary thread on the pull request.
    [Fact]
    public async Task ThePartialResultOfFailedFileReviews_IsPublishedAgainstTheThreadsTheFetchRead()
    {
        var job = NewGitHubJob();
        var pullRequest = PullRequestFor(job, AppBotLogin);
        var harness = Harness.Create(pullRequest, FailOneOfTwoFileReviews);

        await harness.Service.ProcessAsync(job, CancellationToken.None);

        var (_, context) = harness.SinglePublication();
        Assert.NotNull(context);
        Assert.Equal(pullRequest.ExistingThreads, context.ExistingThreads);
        Assert.Equal(AppBotLogin, context.AuthorizedPublicationIdentity.Login);
    }

    // A later review and the thread pass recognise ProPR's own threads by the recorded origin of each posted
    // comment, so the comments of a failed review have to be recorded as a completed review's are.
    [Fact]
    public async Task TheCommentsOfFailedFileReviews_AreRecordedAsPostedByTheJob()
    {
        var job = NewGitHubJob();
        var harness = Harness.Create(PullRequestFor(job, AppBotLogin), FailOneOfTwoFileReviews);
        harness.PostComment(new PostedReviewCommentRef("4498327645", "PRRT_kwDOVk4Z4c60uCFh", "src/durations.ts", 21));

        await harness.Service.ProcessAsync(job, CancellationToken.None);

        await harness.Origins.Received(1).RecordAsync(
            Arg.Is<IReadOnlyList<PostedCommentOriginEntry>>(entries =>
                entries.Count == 1 && entries[0].ProviderCommentId == "4498327645" && entries[0].JobId == job.Id),
            Arg.Any<CancellationToken>());
    }

    // Without an account from the fetch, the review is published under the fallback identity. Its id, its login and its
    // display name are the key of the connection, which matches no account, so no comment of a person's account is taken
    // for ProPR's own. The warning names the key, so that an operator can find the connection.
    [Fact]
    public async Task AFetchWithoutAnAuthenticatedAccount_PublishesUnderTheConnectionKeyAndLogsAWarning()
    {
        var job = NewGitHubJob();
        var harness = Harness.Create(PullRequestFor(job, authorizedIdentityName: null), CompleteTheFileReviews);
        var connectionKey = $"connection:{job.ClientId:D}:GitHub:1447959009:1";

        await harness.Service.ProcessAsync(job, CancellationToken.None);

        var (identity, _) = harness.SinglePublication();
        Assert.Equal(connectionKey, identity.ExternalUserId);
        Assert.Equal(connectionKey, identity.Login);
        Assert.Equal(connectionKey, identity.DisplayName);
        Assert.False(identity.IsBot);
        Assert.Contains(
            harness.Log.Entries,
            entry => entry.Level == LogLevel.Warning
                     && entry.Message.Contains(job.Id.ToString(), StringComparison.Ordinal)
                     && entry.Message.Contains(connectionKey, StringComparison.Ordinal));
    }

    private static void FailOneOfTwoFileReviews(IFileByFileReviewOrchestrator orchestrator)
    {
        // One file review failed and one completed, so the partial result carries what the completed review found.
        var partialResult = new ReviewResult(
            "The durations module sorts numbers as strings.",
            [new ReviewComment("src/durations.ts", 21, CommentSeverity.Warning, "The default sort() compares numbers as strings.")]);
        orchestrator
            .ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<IChatClient?>())
            .ThrowsAsync(new PartialReviewFailureException(1, 2, [new InvalidOperationException("The model endpoint refused the request.")], partialResult));
    }

    private static void CompleteTheFileReviews(IFileByFileReviewOrchestrator orchestrator)
    {
        orchestrator
            .ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<IChatClient?>())
            .Returns(new ReviewResult("The durations module sorts numbers as strings.", []));
    }

    private static ReviewJob NewGitHubJob()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://github.com", "meister-dev-propr-demo", "1447959009", 1, 1);
        job.SetProviderReviewContext(
            new CodeReviewRef(
                new RepositoryRef(
                    new ProviderHostRef(ScmProvider.GitHub, "https://github.com"),
                    "1447959009",
                    "meister-dev-propr-demo",
                    "meister-dev-propr-demo/propr-lens-demo"),
                CodeReviewPlatformKind.PullRequest,
                "1",
                1));
        job.SetReviewRevision(new ReviewRevision("4f1c2a9e0b7d", "9a8b7c6d5e4f", null, "4f1c2a9e0b7d", "9a8b7c6d5e4f...4f1c2a9e0b7d"));
        return job;
    }

    private static PullRequest PullRequestFor(ReviewJob job, string? authorizedIdentityName)
    {
        IReadOnlyList<PrCommentThread> threads =
        [
            new(
                "PRRT_kwDOVk4Z4c60uCFt",
                "/src/durations.ts",
                12,
                [new PrThreadComment("propr-review-meister-dev", "The parser accepts a negative duration.", CommentId: 4498327665)]),
        ];

        return new PullRequest(
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            "propr-lens-demo",
            job.PullRequestId,
            job.IterationId,
            "Sort the durations",
            null,
            "feature/durations",
            "main",
            [
                new ChangedFile("src/durations.ts", ChangeType.Edit, "content", "diff"),
                new ChangedFile("src/format.ts", ChangeType.Edit, "content", "diff"),
            ],
            PrStatus.Active,
            threads,
            AuthorizedIdentityName: authorizedIdentityName);
    }

    private sealed class Harness
    {
        private readonly List<PostedReviewCommentRef> _postedComments;

        private Harness(
            ReviewOrchestrationService service,
            ICodeReviewPublicationService publication,
            IPostedCommentOriginStore origins,
            ListLogger log,
            List<PostedReviewCommentRef> postedComments)
        {
            this.Service = service;
            this.Publication = publication;
            this.Origins = origins;
            this.Log = log;
            this._postedComments = postedComments;
        }

        public ReviewOrchestrationService Service { get; }

        public ICodeReviewPublicationService Publication { get; }

        public IPostedCommentOriginStore Origins { get; }

        public ListLogger Log { get; }

        public void PostComment(PostedReviewCommentRef comment) => this._postedComments.Add(comment);

        public (ReviewerIdentity Identity, ReviewPublicationContext? Context) SinglePublication()
        {
            var call = Assert.Single(
                this.Publication.ReceivedCalls(),
                received => received.GetMethodInfo().Name == nameof(ICodeReviewPublicationService.PublishReviewAsync));
            var arguments = call.GetArguments();
            return ((ReviewerIdentity)arguments[4]!, (ReviewPublicationContext?)arguments[6]);
        }

        public static Harness Create(PullRequest pullRequest, Action<IFileByFileReviewOrchestrator> scan)
        {
            var orchestrator = Substitute.For<IFileByFileReviewOrchestrator>();
            scan(orchestrator);

            var reviewContextToolsFactory = Substitute.For<IReviewContextToolsFactory>();
            reviewContextToolsFactory.Create(Arg.Any<ReviewContextToolsRequest>()).Returns(Substitute.For<IReviewContextTools>());

            var instructionFetcher = Substitute.For<IRepositoryInstructionFetcher>();
            instructionFetcher
                .FetchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<RepositoryInstruction>>([]));

            var instructionEvaluator = Substitute.For<IRepositoryInstructionEvaluator>();
            instructionEvaluator
                .EvaluateRelevanceAsync(Arg.Any<IReadOnlyList<RepositoryInstruction>>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<RepositoryInstruction>>([]));

            var exclusionFetcher = Substitute.For<IRepositoryExclusionFetcher>();
            exclusionFetcher
                .FetchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(ReviewExclusionRules.Empty));

            var prFetcher = Substitute.For<IPullRequestFetcher>();
            prFetcher.FetchRefAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new PullRequestRef(pullRequest.SourceBranch, pullRequest.TargetBranch, PrStatus.Active)));
            prFetcher.FetchAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<Guid?>(),
                    Arg.Any<CancellationToken>(), Arg.Any<ReviewRevision?>(), Arg.Any<IReviewRepositoryWorkspace?>())
                .Returns(pullRequest);

            var clientRegistry = Substitute.For<IClientRegistry>();
            clientRegistry.GetScmCommentPostingEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
            clientRegistry.GetCommentResolutionBehaviorAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(CommentResolutionBehavior.Silent));

            var prScanRepository = Substitute.For<IReviewPrScanRepository>();
            prScanRepository.GetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<ReviewPrScan?>(null));

            var workspace = Substitute.For<IReviewRepositoryWorkspace>();
            workspace.DisposeAsync().Returns(ValueTask.CompletedTask);
            var workspaceManager = Substitute.For<IReviewRepositoryWorkspaceManager>();
            workspaceManager.PrepareAsync(Arg.Any<ReviewRepositoryWorkspaceRequest>(), Arg.Any<CancellationToken>())
                .Returns(new ReviewRepositoryWorkspacePreparationResult(workspace, null));

            // The comments the provider reports as posted, which a test adds before the review runs.
            var postedComments = new List<PostedReviewCommentRef>();
            var publication = Substitute.For<ICodeReviewPublicationService>();
            publication.Provider.Returns(ScmProvider.GitHub);
            publication.PublishReviewAsync(
                    Arg.Any<Guid>(), Arg.Any<CodeReviewRef>(), Arg.Any<ReviewRevision>(), Arg.Any<ReviewResult>(), Arg.Any<ReviewerIdentity>(),
                    Arg.Any<CancellationToken>(), Arg.Any<ReviewPublicationContext?>())
                .Returns(call => Task.FromResult(
                    ReviewCommentPostingDiagnosticsDto.Empty(call.Arg<ReviewResult>().Comments.Count) with
                    {
                        PostedCount = call.Arg<ReviewResult>().Comments.Count,
                        PostedComments = postedComments.ToList(),
                    }));

            var registry = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
            registry.GetCodeReviewPublicationService(Arg.Any<ScmProvider>()).Returns(publication);
            registry.GetRegisteredCapabilities(Arg.Any<ScmProvider>()).Returns([]);

            var origins = Substitute.For<IPostedCommentOriginStore>();
            var log = new ListLogger();

            // A single attempt is the last attempt, so the first partial failure publishes the partial result.
            var options = Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewRetries = 1 });

            var service = new ReviewOrchestrationService(
                Substitute.For<IReviewJobExecutionStore>(),
                prFetcher,
                registry,
                clientRegistry,
                prScanRepository,
                Substitute.For<IProtocolRecorder>(),
                reviewContextToolsFactory,
                instructionFetcher,
                exclusionFetcher,
                instructionEvaluator,
                options,
                log,
                AiConnectionTestFactory.CreateChatRuntimeResolver(),
                orchestrator,
                workspaceManager: workspaceManager,
                postedCommentOriginStore: origins);

            return new Harness(service, publication, origins, log, postedComments);
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class ListLogger : ILogger<ReviewOrchestrationService>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            this.Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }
}
