// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Services;

/// <summary>
///     The in-process review path records the author of the pull request it reviews. The fetch is the only
///     point that holds the author, so a path that read it from anything else would find nothing there.
/// </summary>
public sealed class ReviewOrchestrationServiceAuthorRecordingTests
{
    private const string OrganizationUrl = "https://dev.azure.com/org";
    private static readonly Guid ClientId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task ProcessAsync_WithAnAuthorOnTheFetch_RecordsThatAuthorOnTheJob()
    {
        var author = new PullRequestAuthor(
            new ProviderHostRef(ScmProvider.AzureDevOps, OrganizationUrl),
            "6f0c1a2b-3d4e-5f60-7182-93a4b5c6d7e8",
            "octo.dev@example.com",
            "Octo Dev",
            false);
        var harness = new Harness(author);

        await harness.RunAsync();

        await harness.Jobs.Received(1).UpdatePullRequestAuthorAsync(
            harness.JobId,
            Arg.Is<PullRequestAuthor>(recorded =>
                recorded.ExternalUserId == author.ExternalUserId
                && recorded.Login == author.Login
                && recorded.DisplayName == author.DisplayName
                && recorded.IsBot == false
                && recorded.AuthorKey == author.AuthorKey),
            Arg.Any<CancellationToken>());
    }

    // A payload that named nobody records nothing. The connection identity that performed the fetch is the
    // other account in scope here, and substituting it would attribute the review to ProPR itself.
    [Fact]
    public async Task ProcessAsync_WithNoAuthorOnTheFetch_RecordsNothing()
    {
        var harness = new Harness(null);

        await harness.RunAsync();

        await harness.Jobs.DidNotReceiveWithAnyArgs()
            .UpdatePullRequestAuthorAsync(default, default!, default);
    }

    // The author column feeds metering, and the review is worth more than the row that counts it. A store that
    // cannot take the write leaves the review to run, post and finish; the job is not failed over it.
    [Fact]
    public async Task ProcessAsync_WhenTheAuthorWriteFails_StillCompletesTheReview()
    {
        var author = new PullRequestAuthor(
            new ProviderHostRef(ScmProvider.AzureDevOps, OrganizationUrl),
            "6f0c1a2b-3d4e-5f60-7182-93a4b5c6d7e8");
        var harness = new Harness(author, authorWriteFails: true);

        await harness.RunAsync();

        await harness.Jobs.Received(1).SetResultAsync(
            harness.JobId,
            Arg.Any<ReviewResult>(),
            Arg.Any<CancellationToken>());
        await harness.Jobs.DidNotReceiveWithAnyArgs().SetFailedAsync(default, default!, default);
    }

    private sealed class Harness
    {
        private readonly ReviewJob _job;
        private readonly ReviewOrchestrationService _sut;

        public Harness(PullRequestAuthor? author, bool authorWriteFails = false)
        {
            this._job = new ReviewJob(Guid.NewGuid(), ClientId, OrganizationUrl, "proj", "repo", 1, 1);
            this._job.SetReviewRevision(new ReviewRevision("head-sha", "base-sha", null, "1", null));

            this.Jobs = Substitute.For<IReviewJobExecutionStore>();
            if (authorWriteFails)
            {
                this.Jobs.UpdatePullRequestAuthorAsync(
                        Arg.Any<Guid>(),
                        Arg.Any<PullRequestAuthor>(),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromException(new InvalidOperationException("the author column is unavailable")));
            }

            var prFetcher = Substitute.For<IPullRequestFetcher>();
            prFetcher.FetchRefAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<int>(),
                    Arg.Any<Guid?>(),
                    Arg.Any<CancellationToken>())
                .Returns(new PullRequestRef("feature/x", "main", PrStatus.Active));
            prFetcher.FetchAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<int>(),
                    Arg.Any<int>(),
                    Arg.Any<int?>(),
                    Arg.Any<Guid?>(),
                    Arg.Any<CancellationToken>(),
                    Arg.Any<ReviewRevision?>(),
                    Arg.Any<IReviewRepositoryWorkspace?>())
                .Returns(CreatePullRequest(author));

            var clientRegistry = Substitute.For<IClientRegistry>();
            clientRegistry.GetScmCommentPostingEnabledAsync(ClientId, Arg.Any<CancellationToken>()).Returns(true);
            clientRegistry.GetCustomSystemMessageAsync(ClientId, Arg.Any<CancellationToken>()).Returns((string?)null);

            var prScanRepository = Substitute.For<IReviewPrScanRepository>();
            prScanRepository.GetAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<int>(),
                    Arg.Any<CancellationToken>())
                .Returns((ReviewPrScan?)null);

            var fileByFileReviewOrchestrator = Substitute.For<IFileByFileReviewOrchestrator>();
            fileByFileReviewOrchestrator.ReviewAsync(
                    Arg.Any<ReviewJob>(),
                    Arg.Any<PullRequest>(),
                    Arg.Any<ReviewSystemContext>(),
                    Arg.Any<CancellationToken>(),
                    Arg.Any<IChatClient?>())
                .Returns(new ReviewResult("Summary", new List<ReviewComment>().AsReadOnly()));

            var (aiRepo, chatFactory) = CreateAiSubstitutes();

            this._sut = new ReviewOrchestrationService(
                this.Jobs,
                prFetcher,
                CreateProviderRegistry(),
                clientRegistry,
                prScanRepository,
                Substitute.For<IProtocolRecorder>(),
                CreateReviewContextToolsFactory(),
                CreateInstructionFetcher(),
                CreateExclusionFetcher(),
                CreateInstructionEvaluator(),
                Substitute.For<IOptions<AiReviewOptions>>(),
                NullLogger<ReviewOrchestrationService>.Instance,
                aiRepo,
                chatFactory,
                fileByFileReviewOrchestrator,
                workspaceManager: CreateWorkspaceManager());
        }

        public IReviewJobExecutionStore Jobs { get; }

        public Guid JobId => this._job.Id;

        public Task RunAsync()
        {
            return this._sut.ProcessAsync(this._job, CancellationToken.None);
        }

        private static PullRequest CreatePullRequest(PullRequestAuthor? author)
        {
            var changedFiles = new List<ChangedFile>
            {
                new("src/Added.cs", ChangeType.Add, "added\n", "@@ -0,0 +1 @@\n+added"),
            }.AsReadOnly();

            return new PullRequest(
                OrganizationUrl,
                "proj",
                "repo",
                "repo",
                1,
                1,
                "Test PR",
                null,
                "feature/x",
                "main",
                changedFiles,
                Author: author);
        }

        private static IScmProviderRegistry CreateProviderRegistry()
        {
            var commentPoster = Substitute.For<ICodeReviewPublicationService>();
            commentPoster.Provider.Returns(ScmProvider.AzureDevOps);
            commentPoster.PublishReviewAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<CodeReviewRef>(),
                    Arg.Any<ReviewRevision>(),
                    Arg.Any<ReviewResult>(),
                    Arg.Any<ReviewerIdentity>(),
                    Arg.Any<CancellationToken>(),
                    Arg.Any<ReviewPublicationContext?>())
                .Returns(ReviewCommentPostingDiagnosticsDto.Empty());

            var registry = Substitute.For<IScmProviderRegistry>();
            registry.GetCodeReviewPublicationService(Arg.Any<ScmProvider>()).Returns(commentPoster);
            registry.GetRegisteredCapabilities(Arg.Any<ScmProvider>()).Returns([]);
            return registry;
        }

        private static IReviewContextToolsFactory CreateReviewContextToolsFactory()
        {
            var factory = Substitute.For<IReviewContextToolsFactory>();
            factory.Create(Arg.Any<ReviewContextToolsRequest>()).Returns(Substitute.For<IReviewContextTools>());
            return factory;
        }

        private static IRepositoryInstructionFetcher CreateInstructionFetcher()
        {
            var fetcher = Substitute.For<IRepositoryInstructionFetcher>();
            fetcher.FetchAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<Guid?>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<RepositoryInstruction>>([]));
            return fetcher;
        }

        private static IRepositoryInstructionEvaluator CreateInstructionEvaluator()
        {
            var evaluator = Substitute.For<IRepositoryInstructionEvaluator>();
            evaluator.EvaluateRelevanceAsync(
                    Arg.Any<IReadOnlyList<RepositoryInstruction>>(),
                    Arg.Any<IReadOnlyList<string>>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<RepositoryInstruction>>([]));
            return evaluator;
        }

        private static IRepositoryExclusionFetcher CreateExclusionFetcher()
        {
            var fetcher = Substitute.For<IRepositoryExclusionFetcher>();
            fetcher.FetchAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<Guid?>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(ReviewExclusionRules.Empty));
            return fetcher;
        }

        private static IReviewRepositoryWorkspaceManager CreateWorkspaceManager()
        {
            var workspace = Substitute.For<IReviewRepositoryWorkspace>();
            workspace.DisposeAsync().Returns(ValueTask.CompletedTask);
            var manager = Substitute.For<IReviewRepositoryWorkspaceManager>();
            manager.PrepareAsync(Arg.Any<ReviewRepositoryWorkspaceRequest>(), Arg.Any<CancellationToken>())
                .Returns(new ReviewRepositoryWorkspacePreparationResult(workspace, null));
            return manager;
        }

        private static (IAiConnectionRepository AiRepo, IAiChatClientFactory ChatFactory) CreateAiSubstitutes()
        {
            var aiRepo = Substitute.For<IAiConnectionRepository>();
            aiRepo.GetActiveForClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<AiConnectionDto?>(AiConnectionTestFactory.CreateChatConnection(Guid.NewGuid())));

            var chatFactory = Substitute.For<IAiChatClientFactory>();
            chatFactory.CreateClient(Arg.Any<string>(), Arg.Any<string?>()).Returns(Substitute.For<IChatClient>());

            return (aiRepo, chatFactory);
        }
    }
}
