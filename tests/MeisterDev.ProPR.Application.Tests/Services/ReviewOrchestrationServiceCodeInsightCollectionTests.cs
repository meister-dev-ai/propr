// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Application.Tests.Services;

public sealed class ReviewOrchestrationServiceCodeInsightCollectionTests
{
    private const string OrganizationUrl = "https://dev.azure.com/org";
    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // A push supersedes the job while its comments are going out. The publishing mark is the control plane's
    // answer to whether this review is still wanted, and publishing over a refusal puts a review on the pull
    // request for work that was already decided against.
    [Fact]
    public async Task ProcessAsync_WhenTheJobIsNoLongerProcessing_PostsNothing()
    {
        var leases = Substitute.For<IReviewJobLeaseStore>();
        leases.TryMarkPublishingAsync(Arg.Any<Guid>(), Arg.Any<ReviewJobLease?>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var harness = new Harness(leaseStore: leases);

        await harness.RunAsync();

        await harness.Publisher.DidNotReceiveWithAnyArgs().PublishReviewAsync(
            default,
            default!,
            default!,
            default!,
            default!,
            default,
            default);
    }

    // The ordinary path still publishes: the guard must refuse a superseded job, not every job.
    [Fact]
    public async Task ProcessAsync_WhenTheJobIsStillProcessing_PublishesAsUsual()
    {
        var leases = Substitute.For<IReviewJobLeaseStore>();
        leases.TryMarkPublishingAsync(Arg.Any<Guid>(), Arg.Any<ReviewJobLease?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var harness = new Harness(leaseStore: leases);

        await harness.RunAsync();

        await harness.Publisher.ReceivedWithAnyArgs(1).PublishReviewAsync(
            default,
            default!,
            default!,
            default!,
            default!,
            default,
            default);
    }

    [Fact]
    public async Task ProcessAsync_CollectsEveryProducedFindingWithItsAnchorAndProvenance()
    {
        var harness = new Harness();

        await harness.RunAsync();

        await harness.IngestionService.Received(1).HandleReviewFindingsProducedAsync(
            Arg.Is<ReviewFindingsProducedEvent>(evt =>
                evt.ClientId == ClientId
                && evt.RepositoryId == "repo"
                && evt.PullRequestId == 1
                && evt.JobId == harness.JobId
                && evt.RevisionKey == "1"
                && evt.Findings.Count == 3
                && evt.Findings[0].Ordinal == 0
                && evt.Findings[0].FilePath == "src/Service.cs"
                && evt.Findings[0].LineNumber == 42
                && evt.Findings[0].Severity == CommentSeverity.Error
                && evt.Findings[0].Message == "Null dereference"
                && evt.Findings[0].OriginPassKind == "Baseline"
                && evt.Findings[0].ScopeRelation == ReviewCommentScopeRelation.OnChangedLine

                // Which model produced it travels with the finding, so reviewer quality stays readable per model
                // without the collection path asking the review pipeline anything.
                && evt.Findings[0].OriginModelId == "gpt-5.4-mini"
                && evt.Findings[0].OriginLogicalModelName == "thrifty-reviewer"

                // And a finding whose model was never recorded stays unattributed rather than inheriting one.
                && evt.Findings[1].OriginModelId == null
                && evt.Findings[1].OriginLogicalModelName == null

                // The definition the finding sits inside travels with it, so a hotspot can be a symbol and not
                // only a file.
                && evt.Findings[0].OriginSymbolName == "Process"
                && evt.Findings[0].OriginSymbolKind == "Method"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_CollectsFindingsSuppressedByTheMinimumSeverityFilter()
    {
        // The minimum-severity filter governs SCM publication only. A finding that was produced but not
        // posted is still a finding the reviewer produced, and excluding it would silently understate every
        // downstream quality metric.
        var harness = new Harness(minimumSeverityToPost: CommentSeverity.Error);

        await harness.RunAsync();

        await harness.IngestionService.Received(1).HandleReviewFindingsProducedAsync(
            Arg.Is<ReviewFindingsProducedEvent>(evt =>
                evt.Findings.Count == 3
                && evt.Findings.Any(finding => finding.Severity == CommentSeverity.Info)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_PairsEachFindingWithTheProviderCommentPostedForItsAnchor()
    {
        var harness = new Harness(
            postedComments:
            [
                new PostedReviewCommentRef("comment-1", "thread-1", "src/Service.cs", 42),
                new PostedReviewCommentRef("comment-2", "thread-2", "src/Other.cs", 7),
            ]);

        await harness.RunAsync();

        await harness.IngestionService.Received(1).HandleReviewFindingsProducedAsync(
            Arg.Is<ReviewFindingsProducedEvent>(evt =>
                evt.Findings[0].ProviderThreadId == "thread-1"
                && evt.Findings[0].ProviderCommentId == "comment-1"
                && evt.Findings[1].ProviderThreadId == "thread-2"
                && evt.Findings[1].ProviderCommentId == "comment-2"
                // The third finding shares no anchor with any posted comment, so it carries no provider ids.
                && evt.Findings[2].ProviderThreadId == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_LeavesAmbiguousAnchorPublicationIdentityUnknown()
    {
        var harness = new Harness(
            comments:
            [
                new ReviewComment("src/Service.cs", 42, CommentSeverity.Error, "First"),
                new ReviewComment("src/Service.cs", 42, CommentSeverity.Warning, "Second"),
            ],
            postedComments: [new PostedReviewCommentRef("comment-1", "thread-1", "src/Service.cs", 42)]);

        await harness.RunAsync();

        await harness.IngestionService.Received(1).HandleReviewFindingsProducedAsync(
            Arg.Is<ReviewFindingsProducedEvent>(evt =>
                evt.Findings.All(finding => finding.ProviderThreadId == null
                                            && finding.PublicationState == CodeInsightPublicationState.Unknown)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_UsesAnActualReceiptOrdinalForFindingsSharingAnAnchor()
    {
        var harness = new Harness(
            comments:
            [
                new ReviewComment("src/Service.cs", 42, CommentSeverity.Error, "First"),
                new ReviewComment("src/Service.cs", 42, CommentSeverity.Warning, "Second")
            ],
            postedComments: [new PostedReviewCommentRef("comment-1", "thread-1", "src/Service.cs", 42, FindingOrdinal: 1)]);
        await harness.RunAsync();
        await harness.IngestionService.Received(1).HandleReviewFindingsProducedAsync(
            Arg.Is<ReviewFindingsProducedEvent>(evt => evt.Findings[0].ProviderThreadId == null
                                                       && evt.Findings[1].ProviderThreadId == "thread-1" &&
                                                       evt.Findings[1].PublicationState == CodeInsightPublicationState.Published),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_CollectionFailureDoesNotFailTheReview()
    {
        var harness = new Harness(collectionThrows: true);

        // A best-effort observer must never surface its failure to the review, which has already been
        // published and persisted by this point.
        await harness.RunAsync();

        await harness.Jobs.Received(1).SetResultAsync(
            harness.JobId,
            Arg.Any<ReviewResult>(),
            Arg.Any<CancellationToken>());
        await harness.Jobs.DidNotReceive().SetFailedAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_WithoutACollectionConsumer_IsANoOp()
    {
        var harness = new Harness(withConsumer: false);

        await harness.RunAsync();

        await harness.Jobs.Received(1).SetResultAsync(
            harness.JobId,
            Arg.Any<ReviewResult>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("https://dev.azure.com/org", "AzureDevOps:https://dev.azure.com")]
    [InlineData("https://example.visualstudio.com/DefaultCollection", "AzureDevOps:https://example.visualstudio.com")]
    public async Task ProcessAsync_HostedOrganizationRetainsTheProducingJobAuthorityNamespace(string sourcePath, string expectedScope)
    {
        var harness = new Harness(organizationUrl: sourcePath);
        await harness.RunAsync();
        await harness.IngestionService.Received(1).HandleReviewFindingsProducedAsync(
            Arg.Is<ReviewFindingsProducedEvent>(evt => evt.Findings.Count > 0 && evt.Findings.All(finding => finding.ProviderScope == expectedScope)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_PreservesTheSelfHostedCollectionNamespaceFromTheJob()
    {
        var harness = new Harness(organizationUrl: "https://ado.example.test/collection");
        await harness.RunAsync();
        await harness.IngestionService.Received(1).HandleReviewFindingsProducedAsync(
            Arg.Is<ReviewFindingsProducedEvent>(evt => evt.Findings.All(finding => finding.ProviderScope == "AzureDevOps:https://ado.example.test/collection")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_LeavesSameAnchorFailureIdentityUnknownWithoutAnOrdinal()
    {
        var harness = new Harness(
            comments: [new("src/Service.cs", 42, CommentSeverity.Error, "First"), new("src/Service.cs", 42, CommentSeverity.Warning, "Second")],
            postedComments: [new("comment", "thread", "src/Service.cs", 42)],
            postingFailures: [new("inline", "src/Service.cs", 42, "Provider rejection")]);
        await harness.RunAsync();
        await harness.IngestionService.Received(1).HandleReviewFindingsProducedAsync(
            Arg.Is<ReviewFindingsProducedEvent>(evt => evt.Findings.All(finding => finding.PublicationState == CodeInsightPublicationState.Unknown)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_MapsSameAnchorReceiptsAndFailuresThroughThePublicationSubset()
    {
        var harness = new Harness(
            minimumSeverityToPost: CommentSeverity.Warning,
            comments:
            [
                new("src/Service.cs", 42, CommentSeverity.Info, "Withheld"), new("src/Service.cs", 42, CommentSeverity.Error, "Published"),
                new("src/Service.cs", 42, CommentSeverity.Warning, "Failed")
            ],
            postedComments: [new("comment", "thread", "src/Service.cs", 42, FindingOrdinal: 0)],
            postingFailures: [new("inline", "src/Service.cs", 42, "Provider rejection", FindingOrdinal: 1)]);
        await harness.RunAsync();
        await harness.IngestionService.Received(1).HandleReviewFindingsProducedAsync(
            Arg.Is<ReviewFindingsProducedEvent>(evt => evt.Findings[0].PublicationState == CodeInsightPublicationState.PolicyWithheld
                                                       && evt.Findings[1].PublicationState == CodeInsightPublicationState.Published &&
                                                       evt.Findings[1].ProviderThreadId == "thread"
                                                       && evt.Findings[2].PublicationState == CodeInsightPublicationState.Failed &&
                                                       evt.Findings[2].ProviderThreadId == null),
            Arg.Any<CancellationToken>());
    }

    private sealed class Harness
    {
        private readonly ReviewJob _job;
        private readonly ReviewOrchestrationService _sut;

        public Harness(
            CommentSeverity minimumSeverityToPost = CommentSeverity.Info,
            IReadOnlyList<ReviewComment>? comments = null,
            IReadOnlyList<PostedReviewCommentRef>? postedComments = null,
            bool collectionThrows = false,
            bool withConsumer = true,
            IReviewJobLeaseStore? leaseStore = null,
            string organizationUrl = OrganizationUrl,
            IReadOnlyList<ReviewCommentPostingFailure>? postingFailures = null)
        {
            this.IngestionService = Substitute.For<ICodeInsightFindingIngestionService>();
            if (collectionThrows)
            {
                this.IngestionService
                    .HandleReviewFindingsProducedAsync(
                        Arg.Any<ReviewFindingsProducedEvent>(),
                        Arg.Any<CancellationToken>())
                    .ThrowsAsync(new InvalidOperationException("collection is broken"));
            }

            this._job = new ReviewJob(Guid.NewGuid(), ClientId, organizationUrl, "proj", "repo", 1, 1);
            this._job.SetReviewRevision(new ReviewRevision("head-sha", "base-sha", null, "1", null));
            this.JobId = this._job.Id;

            this.Jobs = Substitute.For<IReviewJobExecutionStore>();

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
            clientRegistry.GetMinimumSeverityToPostAsync(ClientId, Arg.Any<CancellationToken>())
                .Returns(minimumSeverityToPost);

            var prScanRepository = Substitute.For<IReviewPrScanRepository>();
            prScanRepository.GetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns((ReviewPrScan?)null);

            var fileByFileReviewOrchestrator = Substitute.For<IFileByFileReviewOrchestrator>();
            fileByFileReviewOrchestrator.ReviewAsync(
                    Arg.Any<ReviewJob>(),
                    Arg.Any<PullRequest>(),
                    Arg.Any<ReviewSystemContext>(),
                    Arg.Any<CancellationToken>(),
                    Arg.Any<IChatClient?>())
                .Returns(new ReviewResult("Summary", comments ?? CreateComments()));

            var providerRegistry = CreateProviderRegistry(postedComments ?? [], postingFailures ?? []);
            this.Publisher = providerRegistry.GetCodeReviewPublicationService(ScmProvider.AzureDevOps);

            this._sut = new ReviewOrchestrationService(
                this.Jobs,
                prFetcher,
                providerRegistry,
                clientRegistry,
                prScanRepository,
                Substitute.For<IProtocolRecorder>(),
                CreateReviewContextToolsFactory(),
                CreateInstructionFetcher(),
                CreateExclusionFetcher(),
                CreateInstructionEvaluator(),
                Substitute.For<IOptions<AiReviewOptions>>(),
                NullLogger<ReviewOrchestrationService>.Instance,
                AiConnectionTestFactory.CreateChatRuntimeResolver(),
                fileByFileReviewOrchestrator,
                workspaceManager: CreateWorkspaceManager(),
                codeInsightFindingIngestionService: withConsumer ? this.IngestionService : null,
                leaseStore: leaseStore);
        }

        public ICodeInsightFindingIngestionService IngestionService { get; }

        public ICodeReviewPublicationService Publisher { get; } = null!;

        public IReviewJobExecutionStore Jobs { get; }

        public Guid JobId { get; }

        public Task RunAsync()
        {
            return this._sut.ProcessAsync(this._job, CancellationToken.None);
        }

        private static IReadOnlyList<ReviewComment> CreateComments()
        {
            return new List<ReviewComment>
            {
                new("src/Service.cs", 42, CommentSeverity.Error, "Null dereference")
                {
                    OriginPassKind = "Baseline",
                    ScopeRelation = ReviewCommentScopeRelation.OnChangedLine,
                    OriginModelId = "gpt-5.4-mini",
                    OriginLogicalModelName = "thrifty-reviewer",
                    OriginSymbolName = "Process",
                    OriginSymbolKind = "Method",
                },
                new("src/Other.cs", 7, CommentSeverity.Warning, "Missing bounds check"),
                new("src/Nit.cs", 3, CommentSeverity.Info, "Rename this local"),
            }.AsReadOnly();
        }

        private static PullRequest CreatePullRequest()
        {
            var changedFiles = new List<ChangedFile>
            {
                new("src/Service.cs", ChangeType.Edit, "changed\n", "@@ -1 +1 @@\n-old\n+changed"),
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

        private static IScmProviderRegistry CreateProviderRegistry(
            IReadOnlyList<PostedReviewCommentRef> postedComments, IReadOnlyList<ReviewCommentPostingFailure> failures)
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
                .Returns(ReviewCommentPostingDiagnosticsDto.Empty() with { PostedComments = postedComments, PostingFailures = failures });

            var registry = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
            registry.GetSourceIdentityPolicy(Arg.Any<ScmProvider>()).Returns(call =>
                MeisterDev.ProPR.TestSupport.LocalScmPolicies.Registry.GetSourceIdentityPolicy(call.Arg<ScmProvider>()));
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
    }
}
