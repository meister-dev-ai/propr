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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Services;

/// <summary>
///     Covers how a job picks up its tenant's reasoning-capture policy. The installation options are a process
///     singleton and a job carries a client and no tenant, so the policy is resolved once while the review context
///     is assembled and travels on the context from there.
/// </summary>
public class ReviewOrchestrationServiceReasoningCaptureTests
{
    [Theory]
    [InlineData(ReasoningCapturePolicy.Enabled, true)]
    [InlineData(ReasoningCapturePolicy.Disabled, false)]
    public async Task ProcessAsync_ATenantThatStatesAPolicy_PutsItOnTheContext(
        ReasoningCapturePolicy policy,
        bool expected)
    {
        var (job, prFetcher, clientRegistry, prScanRepository) = BuildDefaults();
        var policies = Substitute.For<ITenantReasoningCapturePolicyProvider>();
        policies.GetForClientAsync(job.ClientId, Arg.Any<CancellationToken>()).Returns(policy);

        var capturedContext = await RunAsync(job, prFetcher, clientRegistry, prScanRepository, policies);

        Assert.NotNull(capturedContext);
        Assert.Equal(expected, capturedContext!.CaptureReasoning);
    }

    // A tenant that has stated nothing leaves the value unset, so the installation switch keeps deciding and the
    // behaviour is what it was before the policy existed.
    [Fact]
    public async Task ProcessAsync_ATenantThatStatesNoPolicy_LeavesTheContextValueUnset()
    {
        var (job, prFetcher, clientRegistry, prScanRepository) = BuildDefaults();
        var policies = Substitute.For<ITenantReasoningCapturePolicyProvider>();
        policies.GetForClientAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(ReasoningCapturePolicy.InstallationDefault);

        var capturedContext = await RunAsync(job, prFetcher, clientRegistry, prScanRepository, policies);

        Assert.NotNull(capturedContext);
        Assert.Null(capturedContext!.CaptureReasoning);
    }

    // The offline and minimal wirings compose no policy provider at all.
    [Fact]
    public async Task ProcessAsync_WithoutAPolicyProvider_LeavesTheContextValueUnset()
    {
        var (job, prFetcher, clientRegistry, prScanRepository) = BuildDefaults();

        var capturedContext = await RunAsync(job, prFetcher, clientRegistry, prScanRepository, policies: null);

        Assert.NotNull(capturedContext);
        Assert.Null(capturedContext!.CaptureReasoning);
    }

    // Two tenants' jobs run through the same process and the same installation options, so the decision cannot
    // live on those options. Each job has to carry its own.
    [Fact]
    public async Task ProcessAsync_TwoTenantsWithOpposingPolicies_EachJobCarriesItsOwn()
    {
        var policies = Substitute.For<ITenantReasoningCapturePolicyProvider>();

        var capturing = BuildDefaults();
        policies.GetForClientAsync(capturing.job.ClientId, Arg.Any<CancellationToken>())
            .Returns(ReasoningCapturePolicy.Enabled);
        var withholding = BuildDefaults();
        policies.GetForClientAsync(withholding.job.ClientId, Arg.Any<CancellationToken>())
            .Returns(ReasoningCapturePolicy.Disabled);

        var capturingContext = await RunAsync(capturing.job, capturing.prFetcher, capturing.clientRegistry, capturing.prScanRepository, policies);
        var withholdingContext = await RunAsync(withholding.job, withholding.prFetcher, withholding.clientRegistry, withholding.prScanRepository, policies);

        Assert.NotNull(capturingContext);
        Assert.NotNull(withholdingContext);
        Assert.True(capturingContext!.CaptureReasoning);
        Assert.False(withholdingContext!.CaptureReasoning);
    }

    // A pass context is cloned from the job context, so an additional pass must not fall back to the
    // installation switch halfway through a review.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CloneForPass_CarriesTheJobsReasoningCapture(bool captureReasoning)
    {
        var context = new ReviewSystemContext(null, [], null) { CaptureReasoning = captureReasoning };

        Assert.Equal(captureReasoning, context.CloneForPass().CaptureReasoning);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void CapturesReasoning_AStatedPolicyOverridesTheInstallationSwitch(bool stated, bool expected)
    {
        var context = new ReviewSystemContext(null, [], null) { CaptureReasoning = stated };

        Assert.Equal(expected, context.CapturesReasoning(new AiReviewOptions { CaptureReasoningInProtocol = !stated }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CapturesReasoning_NoStatedPolicyFollowsTheInstallationSwitch(bool installationSwitch)
    {
        var context = new ReviewSystemContext(null, [], null);

        Assert.Equal(
            installationSwitch,
            context.CapturesReasoning(new AiReviewOptions { CaptureReasoningInProtocol = installationSwitch }));
    }

    private static async Task<ReviewSystemContext?> RunAsync(
        ReviewJob job,
        IPullRequestFetcher prFetcher,
        IClientRegistry clientRegistry,
        IReviewPrScanRepository prScanRepository,
        ITenantReasoningCapturePolicyProvider? policies)
    {
        var orchestrator = Substitute.For<IFileByFileReviewOrchestrator>();
        ReviewSystemContext? capturedContext = null;
        orchestrator
            .ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Do<ReviewSystemContext>(context => capturedContext = context),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(new ReviewResult("Summary", new List<ReviewComment>().AsReadOnly()));

        var service = CreateService(prFetcher, orchestrator, clientRegistry, prScanRepository, policies);
        await service.ProcessAsync(job, CancellationToken.None);

        return capturedContext;
    }

    private static ReviewOrchestrationService CreateService(
        IPullRequestFetcher prFetcher,
        IFileByFileReviewOrchestrator orchestrator,
        IClientRegistry clientRegistry,
        IReviewPrScanRepository prScanRepository,
        ITenantReasoningCapturePolicyProvider? policies)
    {
        var reviewContextToolsFactory = Substitute.For<IReviewContextToolsFactory>();
        reviewContextToolsFactory
            .Create(Arg.Any<ReviewContextToolsRequest>())
            .Returns(Substitute.For<IReviewContextTools>());

        var instructionFetcher = Substitute.For<IRepositoryInstructionFetcher>();
        instructionFetcher
            .FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RepositoryInstruction>>([]));

        var instructionEvaluator = Substitute.For<IRepositoryInstructionEvaluator>();
        instructionEvaluator
            .EvaluateRelevanceAsync(
                Arg.Any<IReadOnlyList<RepositoryInstruction>>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RepositoryInstruction>>([]));

        var exclusionFetcher = Substitute.For<IRepositoryExclusionFetcher>();
        exclusionFetcher
            .FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ReviewExclusionRules.Empty));

        return new ReviewOrchestrationService(
            Substitute.For<IReviewJobExecutionStore>(),
            prFetcher,
            CreateProviderRegistry(),
            clientRegistry,
            prScanRepository,
            Substitute.For<IProtocolRecorder>(),
            reviewContextToolsFactory,
            instructionFetcher,
            exclusionFetcher,
            instructionEvaluator,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()),
            Substitute.For<ILogger<ReviewOrchestrationService>>(),
            AiConnectionTestFactory.CreateChatRuntimeResolver(),
            orchestrator,
            workspaceManager: CreateWorkspaceManager(),
            reasoningCapturePolicies: policies);
    }

    private static IScmProviderRegistry CreateProviderRegistry()
    {
        var publicationService = Substitute.For<ICodeReviewPublicationService>();
        publicationService.Provider.Returns(ScmProvider.AzureDevOps);
        publicationService.PublishReviewAsync(
                Arg.Any<Guid>(),
                Arg.Any<CodeReviewRef>(),
                Arg.Any<ReviewRevision>(),
                Arg.Any<ReviewResult>(),
                Arg.Any<ReviewerIdentity>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<ReviewPublicationContext?>())
            .Returns(Task.FromResult(ReviewCommentPostingDiagnosticsDto.Empty()));

        var reviewerManager = Substitute.For<IReviewAssignmentService>();
        reviewerManager.Provider.Returns(ScmProvider.AzureDevOps);
        reviewerManager.AddOptionalReviewerAsync(
                Arg.Any<Guid>(),
                Arg.Any<CodeReviewRef>(),
                Arg.Any<ReviewerIdentity>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var registry = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
        registry.GetCodeReviewPublicationService(Arg.Any<ScmProvider>()).Returns(publicationService);
        registry.GetReviewAssignmentService(Arg.Any<ScmProvider>()).Returns(reviewerManager);
        registry.GetRegisteredCapabilities(Arg.Any<ScmProvider>()).Returns(["reviewAssignment"]);
        return registry;
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

    private static (ReviewJob job, IPullRequestFetcher prFetcher, IClientRegistry clientRegistry,
        IReviewPrScanRepository prScanRepository) BuildDefaults()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
        job.SetReviewRevision(new ReviewRevision("head-sha", "base-sha", null, null, null));

        var prFetcher = Substitute.For<IPullRequestFetcher>();
        prFetcher.FetchRefAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PullRequestRef("feature/test", "main", PrStatus.Active)));

        var clientRegistry = Substitute.For<IClientRegistry>();
        var prScanRepository = Substitute.For<IReviewPrScanRepository>();
        prScanRepository.GetAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewPrScan?>(null));

        var reviewerId = Guid.NewGuid();
        var reviewerIdentity = new ReviewerIdentity(
            job.ProviderHost,
            reviewerId.ToString("D"),
            reviewerId.ToString("D"),
            reviewerId.ToString("D"),
            false);
        clientRegistry.GetReviewerIdentityAsync(job.ClientId, job.ProviderHost, Arg.Any<CancellationToken>())
            .Returns(reviewerIdentity);
        clientRegistry.GetEffectiveReviewerIdentityAsync(job.ClientId, job.ProviderHost, Arg.Any<CancellationToken>())
            .Returns(reviewerIdentity);
        clientRegistry.GetCommentResolutionBehaviorAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CommentResolutionBehavior.Silent));
        clientRegistry.GetCustomSystemMessageAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var pr = new PullRequest(
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            job.RepositoryId,
            job.PullRequestId,
            job.IterationId,
            "Test PR",
            null,
            "feature/x",
            "main",
            new List<ChangedFile>().AsReadOnly());

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

        return (job, prFetcher, clientRegistry, prScanRepository);
    }
}
