// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.ReviewArchive;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.Services;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.Events;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Services;

public sealed class ReviewOrchestrationServiceDiffRetentionTests
{
    private const string OrganizationUrl = "https://dev.azure.com/org";
    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ConnectionId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task ProcessAsync_StoreDiffsOn_SavesIncrementFileDiffsWithRevisionKeyAndMapping()
    {
        var harness = new Harness(true);

        await harness.RunAsync();

        // The increment's changed-file diffs are captured under the same stored revision key the pipeline
        // uses (the provider revision id "1"), with the canonical unified diffs and provider-neutral
        // change types mapped onto the retained snapshot.
        await harness.IngestionService.Received(1).HandleReviewIncrementDiffsAsync(
            Arg.Is<ReviewIncrementCompletedEvent>(evt =>
                evt.ClientId == ClientId
                && evt.ConnectionId == ConnectionId
                && evt.RepositoryId == "repo"
                && evt.PullRequestId == 1
                && evt.RevisionKey == "1"
                && evt.FileDiffs.Count == 2
                && evt.FileDiffs[0].FilePath == "src/Added.cs"
                && evt.FileDiffs[0].ChangeType == "Added"
                && !evt.FileDiffs[0].IsBinary
                && evt.FileDiffs[0].UnifiedDiff == "@@ -0,0 +1 @@\n+added"
                && evt.FileDiffs[1].FilePath == "assets/logo.png"
                && evt.FileDiffs[1].ChangeType == "Modified"
                && evt.FileDiffs[1].IsBinary
                // Binary files carry no renderable diff text.
                && evt.FileDiffs[1].UnifiedDiff == string.Empty),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_StoreDiffsOff_DoesNotSaveAnyDiffs()
    {
        var harness = new Harness(false);

        await harness.RunAsync();

        await harness.IngestionService.DidNotReceive()
            .HandleReviewIncrementDiffsAsync(Arg.Any<ReviewIncrementCompletedEvent>(), Arg.Any<CancellationToken>());
    }

    private sealed class Harness
    {
        private readonly ReviewJob _job;
        private readonly ReviewOrchestrationService _sut;

        public Harness(bool storeDiffs)
        {
            this.IngestionService = Substitute.For<IReviewArchiveIngestionService>();
            var scmConnectionRepository = Substitute.For<IClientScmConnectionRepository>();
            scmConnectionRepository.GetByClientIdAsync(ClientId, Arg.Any<CancellationToken>())
                .Returns([CreateConnection(storeDiffs)]);

            this._job = new ReviewJob(Guid.NewGuid(), ClientId, OrganizationUrl, "proj", "repo", 1, 1);
            this._job.SetReviewRevision(new ReviewRevision("head-sha", "base-sha", null, "1", null));

            var jobs = Substitute.For<IReviewJobExecutionStore>();

            var pr = CreatePullRequest();
            var prFetcher = Substitute.For<IPullRequestFetcher>();
            prFetcher.FetchRefAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
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
                .Returns(pr);

            var clientRegistry = Substitute.For<IClientRegistry>();
            clientRegistry.GetScmCommentPostingEnabledAsync(ClientId, Arg.Any<CancellationToken>())
                .Returns(true);
            clientRegistry.GetCustomSystemMessageAsync(ClientId, Arg.Any<CancellationToken>())
                .Returns((string?)null);

            var prScanRepository = Substitute.For<IReviewPrScanRepository>();
            prScanRepository.GetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns((ReviewPrScan?)null);

            var fileByFileReviewOrchestrator = Substitute.For<IFileByFileReviewOrchestrator>();
            fileByFileReviewOrchestrator.ReviewAsync(
                    Arg.Any<ReviewJob>(),
                    Arg.Any<PullRequest>(),
                    Arg.Any<ReviewSystemContext>(),
                    Arg.Any<CancellationToken>(),
                    Arg.Any<IChatClient?>())
                .Returns(new ReviewResult("Summary", new List<ReviewComment>().AsReadOnly()));

            var providerRegistry = CreateProviderRegistry();
            var (aiRepo, chatFactory) = CreateAiSubstitutes();

            this._sut = new ReviewOrchestrationService(
                jobs,
                prFetcher,
                providerRegistry,
                clientRegistry,
                prScanRepository,
                Substitute.For<IAiCommentResolutionCore>(),
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
                workspaceManager: CreateWorkspaceManager(),
                scmConnectionRepository: scmConnectionRepository,
                reviewArchiveIngestionService: this.IngestionService);
        }

        public IReviewArchiveIngestionService IngestionService { get; }

        public Task RunAsync()
        {
            return this._sut.ProcessAsync(this._job, CancellationToken.None);
        }

        private static ClientScmConnectionDto CreateConnection(bool storeDiffs)
        {
            var now = DateTimeOffset.UtcNow;
            return new ClientScmConnectionDto(
                ConnectionId,
                ClientId,
                ScmProvider.AzureDevOps,
                OrganizationUrl,
                ScmAuthenticationKind.PersonalAccessToken,
                "Azure DevOps",
                true,
                "verified",
                now,
                null,
                null,
                now,
                now)
            {
                StoreDiffs = storeDiffs,
            };
        }

        private static PullRequest CreatePullRequest()
        {
            var changedFiles = new List<ChangedFile>
            {
                new("src/Added.cs", ChangeType.Add, "added\n", "@@ -0,0 +1 @@\n+added"),
                new("assets/logo.png", ChangeType.Edit, string.Empty, "@@ binary @@", true),
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
                changedFiles);
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

        private static (IAiConnectionRepository aiRepo, IAiChatClientFactory chatFactory) CreateAiSubstitutes()
        {
            var aiRepo = Substitute.For<IAiConnectionRepository>();
            aiRepo.GetActiveForClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<AiConnectionDto?>(AiConnectionTestFactory.CreateChatConnection(Guid.NewGuid())));

            var chatFactory = Substitute.For<IAiChatClientFactory>();
            chatFactory.CreateClient(Arg.Any<string>(), Arg.Any<string?>())
                .Returns(Substitute.For<IChatClient>());

            return (aiRepo, chatFactory);
        }
    }
}
