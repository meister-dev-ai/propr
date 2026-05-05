// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Azure.Core;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.Services;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Services;

public sealed class ReviewOrchestrationServiceProCursorIntegrationTests
{
    [Fact]
    public async Task ProcessAsync_WhenReviewUsesProCursorKnowledgeTool_DelegatesThroughGatewayBackedReviewTools()
    {
        var job = CreateJob();
        var selectedSourceId = Guid.NewGuid();
        job.SetProCursorSourceScope(ProCursorSourceScopeMode.SelectedSources, [selectedSourceId]);
        var proCursorGateway = Substitute.For<IProCursorGateway>();
        proCursorGateway.AskKnowledgeAsync(Arg.Any<ProCursorKnowledgeQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new ProCursorKnowledgeAnswerDto(
                    "complete",
                    [
                        new ProCursorKnowledgeAnswerMatchDto(
                            Guid.NewGuid(),
                            ProCursorSourceKind.Repository,
                            Guid.NewGuid(),
                            "feature/procursor",
                            "abc123",
                            "docs/token-caching.md",
                            "Token caching",
                            "Token caching avoids redundant Azure DevOps calls.",
                            "hybrid",
                            0.91d,
                            "fresh"),
                    ]));

        var orchestrator = Substitute.For<IFileByFileReviewOrchestrator>();
        orchestrator.ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(async call =>
            {
                var systemContext = call.ArgAt<ReviewSystemContext>(2);
                var knowledge = await systemContext.ReviewTools!.AskProCursorKnowledgeAsync(
                    "How is token caching handled?",
                    call.ArgAt<CancellationToken>(3));

                Assert.Equal("complete", knowledge.Status);
                Assert.Single(knowledge.Results);
                return new ReviewResult("Knowledge-assisted review", []);
            });

        var service = CreateService(job, orchestrator, proCursorGateway);

        await service.ProcessAsync(job, CancellationToken.None);

        await proCursorGateway.Received(1)
            .AskKnowledgeAsync(
                Arg.Is<ProCursorKnowledgeQueryRequest>(request =>
                    request.ClientId == job.ClientId &&
                    request.Question == "How is token caching handled?" &&
                    request.KnowledgeSourceIds != null &&
                    request.KnowledgeSourceIds.SequenceEqual(new[] { selectedSourceId }) &&
                    request.RepositoryContext != null &&
                    request.RepositoryContext.ProviderScopePath == job.OrganizationUrl &&
                    request.RepositoryContext.ProviderProjectKey == job.ProjectId &&
                    request.RepositoryContext.RepositoryId == job.RepositoryId &&
                    request.RepositoryContext.Branch == "feature/procursor"),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_WhenReviewUsesProCursorSymbolTool_DelegatesThroughGatewayBackedReviewTools()
    {
        var job = CreateJob();
        var proCursorGateway = Substitute.For<IProCursorGateway>();
        proCursorGateway.GetSymbolInsightAsync(Arg.Any<ProCursorSymbolQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new ProCursorSymbolInsightDto(
                    "complete",
                    Guid.NewGuid(),
                    true,
                    true,
                    new ProCursorSymbolMatchDto(
                        "T:Demo.Greeter",
                        "Greeter",
                        "type",
                        "csharp",
                        "Demo.Greeter",
                        new ProCursorSourceLocationDto("src/Greeter.cs", 3, 12)),
                    [],
                    "fresh"));

        var orchestrator = Substitute.For<IFileByFileReviewOrchestrator>();
        orchestrator.ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(async call =>
            {
                var systemContext = call.ArgAt<ReviewSystemContext>(2);
                var symbol = await systemContext.ReviewTools!.GetProCursorSymbolInfoAsync(
                    "Greeter",
                    "qualifiedName",
                    10,
                    call.ArgAt<CancellationToken>(3));

                Assert.Equal("complete", symbol.Status);
                Assert.NotNull(symbol.Symbol);
                return new ReviewResult("Symbol-assisted review", []);
            });

        var service = CreateService(job, orchestrator, proCursorGateway);

        await service.ProcessAsync(job, CancellationToken.None);

        await proCursorGateway.Received(1)
            .GetSymbolInsightAsync(
                Arg.Is<ProCursorSymbolQueryRequest>(request =>
                    request.ClientId == job.ClientId &&
                    request.Symbol == "Greeter" &&
                    request.QueryMode == "qualifiedName" &&
                    request.StateMode == "reviewTarget" &&
                    request.MaxRelations == 10 &&
                    request.ReviewContext != null &&
                    request.ReviewContext.RepositoryId == job.RepositoryId &&
                    request.ReviewContext.SourceBranch == "feature/procursor" &&
                    request.ReviewContext.PullRequestId == job.PullRequestId &&
                    request.ReviewContext.IterationId == job.IterationId),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_WhenScmCommentPostingDisabled_PreservesDiagnosticsWithoutPublishing()
    {
        var job = CreateJob();
        var proCursorGateway = Substitute.For<IProCursorGateway>();

        var orchestrator = Substitute.For<IFileByFileReviewOrchestrator>();
        orchestrator.ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(new ReviewResult("Knowledge-assisted review", [new ReviewComment("src/Greeter.cs", 3, CommentSeverity.Warning, "Issue")]));

        var jobs = Substitute.For<IReviewJobExecutionStore>();
        jobs.GetById(job.Id).Returns(job);

        var prFetcher = Substitute.For<IPullRequestFetcher>();
        prFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.IterationId,
                Arg.Any<int?>(),
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(CreatePullRequest(job));

        var reviewerManager = Substitute.For<IReviewAssignmentService>();
        reviewerManager.Provider.Returns(ScmProvider.AzureDevOps);
        reviewerManager.AddOptionalReviewerAsync(
                Arg.Any<Guid>(),
                Arg.Any<CodeReviewRef>(),
                Arg.Any<ReviewerIdentity>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var clientRegistry = Substitute.For<IClientRegistry>();
        var reviewerId = Guid.NewGuid();
        clientRegistry.GetReviewerIdentityAsync(job.ClientId, job.ProviderHost, Arg.Any<CancellationToken>())
            .Returns(new ReviewerIdentity(job.ProviderHost, reviewerId.ToString("D"), reviewerId.ToString("D"), reviewerId.ToString("D"), false));
        clientRegistry.GetCommentResolutionBehaviorAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(CommentResolutionBehavior.Silent);
        clientRegistry.GetCustomSystemMessageAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns((string?)null);
        clientRegistry.GetScmCommentPostingEnabledAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(false);

        var prScanRepository = Substitute.For<IReviewPrScanRepository>();
        prScanRepository.GetAsync(job.ClientId, job.RepositoryId, job.PullRequestId, Arg.Any<CancellationToken>())
            .Returns((ReviewPrScan?)null);

        var instructionFetcher = Substitute.For<IRepositoryInstructionFetcher>();
        instructionFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RepositoryInstruction>>([]));

        var exclusionFetcher = Substitute.For<IRepositoryExclusionFetcher>();
        exclusionFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ReviewExclusionRules.Empty));

        var instructionEvaluator = Substitute.For<IRepositoryInstructionEvaluator>();
        instructionEvaluator.EvaluateRelevanceAsync(
                Arg.Any<IReadOnlyList<RepositoryInstruction>>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RepositoryInstruction>>([]));

        var aiConnectionRepository = Substitute.For<IAiConnectionRepository>();
        aiConnectionRepository.GetActiveForClientAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(AiConnectionTestFactory.CreateChatConnection(job.ClientId, "gpt-4o", baseUrl: "https://ai.test.local"));

        var chatClientFactory = Substitute.For<IAiChatClientFactory>();
        chatClientFactory.CreateClient(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(Substitute.For<IChatClient>());

        var publicationService = Substitute.For<ICodeReviewPublicationService>();
        publicationService.Provider.Returns(ScmProvider.AzureDevOps);

        var threadStatusWriter = Substitute.For<IReviewThreadStatusWriter>();
        threadStatusWriter.Provider.Returns(ScmProvider.AzureDevOps);
        threadStatusWriter.UpdateThreadStatusAsync(
                Arg.Any<Guid>(),
                Arg.Any<ReviewThreadRef>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var threadReplyPublisher = Substitute.For<IReviewThreadReplyPublisher>();
        threadReplyPublisher.Provider.Returns(ScmProvider.AzureDevOps);
        threadReplyPublisher.ReplyAsync(
                Arg.Any<Guid>(),
                Arg.Any<ReviewThreadRef>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var providerRegistry = Substitute.For<IScmProviderRegistry>();
        providerRegistry.GetCodeReviewPublicationService(Arg.Any<ScmProvider>()).Returns(publicationService);
        providerRegistry.GetReviewAssignmentService(Arg.Any<ScmProvider>()).Returns(reviewerManager);
        providerRegistry.GetReviewThreadStatusWriter(Arg.Any<ScmProvider>()).Returns(threadStatusWriter);
        providerRegistry.GetReviewThreadReplyPublisher(Arg.Any<ScmProvider>()).Returns(threadReplyPublisher);
        providerRegistry.GetRegisteredCapabilities(Arg.Any<ScmProvider>())
            .Returns(["reviewAssignment", "reviewThreadStatus", "reviewThreadReply"]);

        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetOperationalConnectionAsync(
                Arg.Any<Guid>(),
                Arg.Any<ProviderHostRef>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ClientScmConnectionCredentialDto?>(null));

        var reviewContextToolsFactory = new AdoReviewContextToolsFactory(
            new VssConnectionFactory(Substitute.For<TokenCredential>()),
            connectionRepository,
            proCursorGateway,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileSizeBytes = 1024 * 1024, ModelId = "gpt-4o" }),
            NullLoggerFactory.Instance);

        var service = new ReviewOrchestrationService(
            jobs,
            prFetcher,
            orchestrator,
            providerRegistry,
            clientRegistry,
            prScanRepository,
            Substitute.For<IAiCommentResolutionCore>(),
            Substitute.For<IProtocolRecorder>(),
            reviewContextToolsFactory,
            instructionFetcher,
            exclusionFetcher,
            instructionEvaluator,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewRetries = 3, ModelId = "gpt-4o" }),
            NullLogger<ReviewOrchestrationService>.Instance,
            aiConnectionRepository,
            chatClientFactory);

        await service.ProcessAsync(job, CancellationToken.None);

        await jobs.Received(1).SetResultAsync(job.Id, Arg.Any<ReviewResult>(), Arg.Any<CancellationToken>());
        await publicationService.DidNotReceiveWithAnyArgs()
            .PublishReviewAsync(default, default!, default!, default!, default!, default);
    }

    private static ReviewOrchestrationService CreateService(
        ReviewJob job,
        IFileByFileReviewOrchestrator orchestrator,
        IProCursorGateway proCursorGateway)
    {
        var jobs = Substitute.For<IReviewJobExecutionStore>();
        jobs.GetById(job.Id).Returns(job);

        var prFetcher = Substitute.For<IPullRequestFetcher>();
        prFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.IterationId,
                Arg.Any<int?>(),
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(CreatePullRequest(job));

        var reviewerManager = Substitute.For<IReviewAssignmentService>();
        reviewerManager.Provider.Returns(ScmProvider.AzureDevOps);
        reviewerManager.AddOptionalReviewerAsync(
                Arg.Any<Guid>(),
                Arg.Any<CodeReviewRef>(),
                Arg.Any<ReviewerIdentity>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var clientRegistry = Substitute.For<IClientRegistry>();
        var reviewerId = Guid.NewGuid();
        clientRegistry.GetReviewerIdentityAsync(job.ClientId, job.ProviderHost, Arg.Any<CancellationToken>())
            .Returns(new ReviewerIdentity(job.ProviderHost, reviewerId.ToString("D"), reviewerId.ToString("D"), reviewerId.ToString("D"), false));
        clientRegistry.GetCommentResolutionBehaviorAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(CommentResolutionBehavior.Silent);
        clientRegistry.GetCustomSystemMessageAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns((string?)null);
        clientRegistry.GetScmCommentPostingEnabledAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(true);

        var prScanRepository = Substitute.For<IReviewPrScanRepository>();
        prScanRepository.GetAsync(job.ClientId, job.RepositoryId, job.PullRequestId, Arg.Any<CancellationToken>())
            .Returns((ReviewPrScan?)null);

        var instructionFetcher = Substitute.For<IRepositoryInstructionFetcher>();
        instructionFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RepositoryInstruction>>([]));

        var exclusionFetcher = Substitute.For<IRepositoryExclusionFetcher>();
        exclusionFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ReviewExclusionRules.Empty));

        var instructionEvaluator = Substitute.For<IRepositoryInstructionEvaluator>();
        instructionEvaluator.EvaluateRelevanceAsync(
                Arg.Any<IReadOnlyList<RepositoryInstruction>>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RepositoryInstruction>>([]));

        var aiConnectionRepository = Substitute.For<IAiConnectionRepository>();
        aiConnectionRepository.GetActiveForClientAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(AiConnectionTestFactory.CreateChatConnection(job.ClientId, "gpt-4o", baseUrl: "https://ai.test.local"));

        var chatClientFactory = Substitute.For<IAiChatClientFactory>();
        chatClientFactory.CreateClient(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(Substitute.For<IChatClient>());

        var publicationService = Substitute.For<ICodeReviewPublicationService>();
        publicationService.Provider.Returns(ScmProvider.AzureDevOps);
        publicationService.PublishReviewAsync(
                Arg.Any<Guid>(),
                Arg.Any<CodeReviewRef>(),
                Arg.Any<ReviewRevision>(),
                Arg.Any<ReviewResult>(),
                Arg.Any<ReviewerIdentity>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ReviewCommentPostingDiagnosticsDto.Empty()));

        var threadStatusWriter = Substitute.For<IReviewThreadStatusWriter>();
        threadStatusWriter.Provider.Returns(ScmProvider.AzureDevOps);
        threadStatusWriter.UpdateThreadStatusAsync(
                Arg.Any<Guid>(),
                Arg.Any<ReviewThreadRef>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var threadReplyPublisher = Substitute.For<IReviewThreadReplyPublisher>();
        threadReplyPublisher.Provider.Returns(ScmProvider.AzureDevOps);
        threadReplyPublisher.ReplyAsync(
                Arg.Any<Guid>(),
                Arg.Any<ReviewThreadRef>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var providerRegistry = Substitute.For<IScmProviderRegistry>();
        providerRegistry.GetCodeReviewPublicationService(Arg.Any<ScmProvider>()).Returns(publicationService);
        providerRegistry.GetReviewAssignmentService(Arg.Any<ScmProvider>()).Returns(reviewerManager);
        providerRegistry.GetReviewThreadStatusWriter(Arg.Any<ScmProvider>()).Returns(threadStatusWriter);
        providerRegistry.GetReviewThreadReplyPublisher(Arg.Any<ScmProvider>()).Returns(threadReplyPublisher);
        providerRegistry.GetRegisteredCapabilities(Arg.Any<ScmProvider>())
            .Returns(
            [
                "reviewAssignment",
                "reviewThreadStatus",
                "reviewThreadReply",
            ]);

        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetOperationalConnectionAsync(
                Arg.Any<Guid>(),
                Arg.Any<ProviderHostRef>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ClientScmConnectionCredentialDto?>(null));

        var reviewContextToolsFactory = new AdoReviewContextToolsFactory(
            new VssConnectionFactory(Substitute.For<TokenCredential>()),
            connectionRepository,
            proCursorGateway,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileSizeBytes = 1024 * 1024, ModelId = "gpt-4o" }),
            NullLoggerFactory.Instance);

        return new ReviewOrchestrationService(
            jobs,
            prFetcher,
            orchestrator,
            providerRegistry,
            clientRegistry,
            prScanRepository,
            Substitute.For<IAiCommentResolutionCore>(),
            Substitute.For<IProtocolRecorder>(),
            reviewContextToolsFactory,
            instructionFetcher,
            exclusionFetcher,
            instructionEvaluator,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewRetries = 3, ModelId = "gpt-4o" }),
            NullLogger<ReviewOrchestrationService>.Instance,
            aiConnectionRepository,
            chatClientFactory);
    }

    private static ReviewJob CreateJob()
    {
        return new ReviewJob(
            Guid.NewGuid(),
            Guid.Parse("90000000-0000-0000-0000-000000000001"),
            "https://dev.azure.com/test-org",
            "project-a",
            "repo-a",
            42,
            7);
    }

    private static PullRequest CreatePullRequest(ReviewJob job)
    {
        return new PullRequest(
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            job.RepositoryId,
            job.PullRequestId,
            job.IterationId,
            "Test PR",
            null,
            "refs/heads/feature/procursor",
            "main",
            new List<ChangedFile> { new("/src/Greeter.cs", ChangeType.Edit, "class Greeter {}", "@@ -1 +1 @@") }
                .AsReadOnly());
    }
}
