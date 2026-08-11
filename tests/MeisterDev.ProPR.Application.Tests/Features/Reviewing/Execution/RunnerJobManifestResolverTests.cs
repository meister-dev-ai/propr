// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Features.Budgeting.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Runner.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Execution;

public sealed class RunnerJobManifestResolverTests
{
    private readonly IClientRegistry _clients = Substitute.For<IClientRegistry>();
    private readonly IRepositoryExclusionFetcher _exclusions = Substitute.For<IRepositoryExclusionFetcher>();
    private readonly IRepositoryInstructionFetcher _instructions = Substitute.For<IRepositoryInstructionFetcher>();
    private readonly IRepositoryInstructionEvaluator _relevance = Substitute.For<IRepositoryInstructionEvaluator>();
    private readonly IAiRuntimeResolver _runtimes = Substitute.For<IAiRuntimeResolver>();
    private readonly ILogicalModelResolver _logicalModels = Substitute.For<ILogicalModelResolver>();

    public RunnerJobManifestResolverTests()
    {
        this._runtimes.ResolveChatRuntimeAsync(Arg.Any<Guid>(), Arg.Any<AiPurpose>(), Arg.Any<CancellationToken>())
            .Returns(_ => Runtime("reviewer-default"));
        this._logicalModels.ResolveChatRuntimeAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IProtocolRecorder?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(ci => new ResolvedLogicalModelChatRuntime(
                Runtime(ci.ArgAt<string>(1)),
                ci.ArgAt<string>(1),
                LogicalModelLayer.TenantCatalog,
                ReviewReasoningEffort.Medium));

        this._clients.GetReviewPassesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ReviewPassSpec>>(_ =>
                [new ReviewPassSpec(Guid.NewGuid(), LogicalModelName: "reviewer-medium")]);
        this._clients.GetBaselineReasoningEffortAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ReviewReasoningEffort.None);
        this._clients.GetOutputLanguageAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("en");
        this._clients.GetCustomSystemMessageAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        this._exclusions.FetchAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(ReviewExclusionRules.Empty);
        this._instructions.FetchAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<RepositoryInstruction>>(_ => []);
    }

    // The control plane resolves a name to a connection; the manifest carries only what is safe to send.
    private static IResolvedAiChatRuntime Runtime(string logicalModelName)
    {
        var model = new AiConfiguredModelDto(
            Guid.NewGuid(),
            "gpt-5-mini",
            "Reviewer",
            [AiOperationKind.Chat],
            [AiProtocolMode.Auto],
            TokenizerName: "o200k_base",
            MaxInputTokens: 200_000,
            MaxContextTokens: 400_000,
            SupportsToolUse: true,
            SupportsStructuredOutput: true);

        var connection = Substitute.For<IResolvedAiChatRuntime>();
        connection.Connection.Returns(
            new AiConnectionDto(
                Guid.NewGuid(),
                null,
                "Primary",
                AiProviderKind.OpenAi,
                "https://api.invalid",
                AiAuthMode.ApiKey,
                AiDiscoveryMode.ManualOnly,
                true,
                [model],
                [],
                new AiVerificationResultDto(AiVerificationStatus.NeverVerified),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));
        connection.Model.Returns(model);
        connection.LogicalModelName.Returns(logicalModelName);
        return connection;
    }

    private static ReviewJob JobWithRevision()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 42, 1);
        job.SetReviewRevision(new ReviewRevision("head-sha", "base-sha", null, "rev", "base...head"));
        return job;
    }

    private static RunnerJobManifestRequest RequestFor(ReviewJob job)
    {
        return new RunnerJobManifestRequest(
            job,
            new ReviewJobLease(job.Id, "host-a", 3, DateTimeOffset.UtcNow.AddMinutes(2)),
            "main",
            ["src/a.cs"],
            $"/runner/workspace/{job.Id:D}",
            1_073_741_824);
    }

    private RunnerJobManifestResolver CreateResolver(
        IBudgetCapsProvider? caps = null,
        IReviewSpendAccumulator? spend = null,
        IScmProviderRegistry? providerRegistry = null,
        AiReviewOptions? reviewOptions = null,
        ILicensingCapabilityService? licensing = null,
        IReviewJobExecutionStore? executionStore = null)
    {
        return new RunnerJobManifestResolver(
            this._clients,
            this._exclusions,
            this._instructions,
            this._relevance,
            NullLogger<RunnerJobManifestResolver>.Instance,
            this._runtimes,
            this._logicalModels,
            budgetCapsProvider: caps,
            spendAccumulator: spend,
            providerRegistry: providerRegistry,
            reviewOptions: reviewOptions,
            licensing: licensing,
            executionStore: executionStore);
    }

    // The same stamp the in-process path writes at review start. Ingested spend is priced through the job's
    // connection and the overview reads the model off the job. A dispatched job that never passed through
    // review start had neither, so its cost stayed null however much it spent.
    [Fact]
    public async Task ResolvingAManifest_StampsTheJobsAiConfigLikeReviewStartWould()
    {
        var job = JobWithRevision();
        var executionStore = Substitute.For<IReviewJobExecutionStore>();

        var resolution = await this.CreateResolver(executionStore: executionStore).ResolveAsync(RequestFor(job));

        Assert.True(resolution.Succeeded);
        await executionStore.Received(1).UpdateAiConfigAsync(
            job.Id,
            Arg.Is<Guid?>(connectionId => connectionId != Guid.Empty),
            "gpt-5-mini",
            Arg.Any<CancellationToken>(),
            Arg.Any<float?>());
    }

    // The executor composes no PR-wide generator. A job whose pass list publishes from a pr_wide entry
    // must be refused at dispatch, not leased to a host that would review less than was asked without
    // reporting it.
    [Fact]
    public async Task APublishingPrWidePass_RefusesDispatchByName()
    {
        var job = JobWithRevision();
        this._clients.GetReviewPassesAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ReviewPassSpec>>(_ =>
            [
                new ReviewPassSpec(Guid.NewGuid(), LogicalModelName: "reviewer-medium"),
                new ReviewPassSpec(Guid.NewGuid(), Scope: ReviewPassScope.PrWide, LogicalModelName: "reviewer-high"),
            ]);

        var resolution = await this.CreateResolver().ResolveAsync(RequestFor(job));

        Assert.False(resolution.Succeeded);
        Assert.Contains("pr_wide", resolution.Refusal!, StringComparison.Ordinal);
    }

    // A shadow entry publishes nothing, so skipping it remotely changes telemetry, not the review.
    // Refusing dispatch for it would strand jobs over a pass the client is still evaluating.
    [Fact]
    public async Task AShadowPrWidePass_StillDispatches()
    {
        var job = JobWithRevision();
        this._clients.GetReviewPassesAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ReviewPassSpec>>(_ =>
                [new ReviewPassSpec(Guid.NewGuid(), Scope: ReviewPassScope.PrWide, Shadow: true, LogicalModelName: "reviewer-high")]);

        var resolution = await this.CreateResolver().ResolveAsync(RequestFor(job));

        Assert.True(resolution.Succeeded);
    }

    // The license lives in this database; a runner has no way to read it. Carried resolved, so the
    // executor's planner applies the same clamp the in-process planner would.
    [Fact]
    public async Task TheManifest_CarriesWhetherParallelExecutionIsLicensed()
    {
        var job = JobWithRevision();
        var licensing = Substitute.For<ILicensingCapabilityService>();
        licensing.IsEnabledAsync(PremiumCapabilityKey.ParallelReviewExecution, Arg.Any<CancellationToken>()).Returns(false);

        var resolution = await this.CreateResolver(licensing: licensing).ResolveAsync(RequestFor(job));

        Assert.False(resolution.Manifest!.ParallelReviewExecutionLicensed);
    }

    [Fact]
    public async Task WithoutALicensingService_TheManifestReadsLicensed()
    {
        var resolution = await this.CreateResolver().ResolveAsync(RequestFor(JobWithRevision()));

        Assert.True(resolution.Manifest!.ParallelReviewExecutionLicensed);
    }

    // Linked items are discovered at dispatch because discovery is a credentialed provider call. The
    // manifest carries the same bounded, provider-neutral summaries the in-process prompt is built from.
    [Fact]
    public async Task TheManifest_CarriesTheLinkedItemsTheInProcessPathWouldAttach()
    {
        var job = JobWithRevision();
        this._clients.GetIncludeLinkedItemsInContextEnabledAsync(job.ClientId, Arg.Any<CancellationToken>()).Returns(true);
        var registry = RegistryDiscovering(
            new LinkedItem(
                "AB#12", "User Story", "Add the widget", "So the widget exists.", "https://items.invalid/12",
                [new LinkedItemRef("Parent", "AB#4", null, "The epic")]),
            new LinkedItem("AB#13", "Bug", "Widget crashes", null, null, []));

        var resolution = await this.CreateResolver(providerRegistry: registry, reviewOptions: new AiReviewOptions())
            .ResolveAsync(this.RequestWithConversation(job));

        Assert.True(resolution.Succeeded);
        var items = resolution.Manifest!.LinkedItems;
        Assert.NotNull(items);
        Assert.Equal(["AB#12", "AB#13"], items!.Select(item => item.ProviderKey));
        var first = items[0];
        Assert.Equal("User Story", first.ItemType);
        Assert.Equal("So the widget exists.", first.Description);
        var link = Assert.Single(first.RelatedLinks);
        Assert.Equal("Parent", link.Kind);
        Assert.Equal("AB#4", link.TargetKey);
    }

    // The same ceiling the in-process path applies before the prompt is built, applied at the same kind of
    // moment: nothing oversized leaves the control plane just because the review runs elsewhere.
    [Fact]
    public async Task LinkedItems_AreBoundedBeforeTheyRideTheManifest()
    {
        var job = JobWithRevision();
        this._clients.GetIncludeLinkedItemsInContextEnabledAsync(job.ClientId, Arg.Any<CancellationToken>()).Returns(true);
        var registry = RegistryDiscovering(
            new LinkedItem("AB#1", "Bug", "One", null, null, []),
            new LinkedItem("AB#2", "Bug", "Two", null, null, []));

        var resolution = await this.CreateResolver(
                providerRegistry: registry,
                reviewOptions: new AiReviewOptions { MaxLinkedItemsInContext = 1 })
            .ResolveAsync(this.RequestWithConversation(job));

        Assert.Equal(["AB#1"], resolution.Manifest!.LinkedItems!.Select(item => item.ProviderKey));
    }

    // A client that keeps linked items out of context must not even pay for discovery, and the manifest
    // carries the same nothing the in-process prompt would see.
    [Fact]
    public async Task LinkedItems_AreNotDiscoveredWhenTheClientKeepsThemOut()
    {
        var job = JobWithRevision();
        this._clients.GetIncludeLinkedItemsInContextEnabledAsync(job.ClientId, Arg.Any<CancellationToken>()).Returns(false);
        var registry = Substitute.For<IScmProviderRegistry>();

        var resolution = await this.CreateResolver(providerRegistry: registry, reviewOptions: new AiReviewOptions())
            .ResolveAsync(this.RequestWithConversation(job));

        Assert.True(resolution.Succeeded);
        Assert.Null(resolution.Manifest!.LinkedItems);
        Assert.Empty(registry.ReceivedCalls());
    }

    // The in-process path reviews without linked items when discovery fails; refusing the dispatch here
    // would make the remote path stricter than the local one for the same failure.
    [Fact]
    public async Task AFailedDiscovery_CostsTheLinkedItemsAndNotTheDispatch()
    {
        var job = JobWithRevision();
        this._clients.GetIncludeLinkedItemsInContextEnabledAsync(job.ClientId, Arg.Any<CancellationToken>()).Returns(true);
        var registry = Substitute.For<IScmProviderRegistry>();
        registry.GetLinkedItemProvider(Arg.Any<ScmProvider>()).Throws(new InvalidOperationException("provider down"));

        var resolution = await this.CreateResolver(providerRegistry: registry, reviewOptions: new AiReviewOptions())
            .ResolveAsync(this.RequestWithConversation(job));

        Assert.True(resolution.Succeeded);
        Assert.Null(resolution.Manifest!.LinkedItems);
    }

    private RunnerJobManifestRequest RequestWithConversation(ReviewJob job)
    {
        var conversation = new PullRequest(
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            "repo",
            job.PullRequestId,
            job.IterationId,
            "Add the widget",
            "AB#12",
            "feature/widget",
            "main",
            []);

        return RequestFor(job) with { Conversation = conversation };
    }

    private static IScmProviderRegistry RegistryDiscovering(params LinkedItem[] items)
    {
        var provider = Substitute.For<ILinkedItemProvider>();
        provider.DiscoverLinkedItemsAsync(Arg.Any<Guid>(), Arg.Any<PullRequest>(), Arg.Any<CancellationToken>())
            .Returns(items);
        var registry = Substitute.For<IScmProviderRegistry>();
        registry.GetLinkedItemProvider(Arg.Any<ScmProvider>()).Returns(provider);
        return registry;
    }

    // The parity that matters: what the manifest says a review is configured with has to be what the
    // in-process path would read for the same job, or a review means one thing locally and another
    // remotely, and the difference only shows up where it is hardest to notice.
    [Fact]
    public async Task TheManifest_CarriesWhatTheInProcessPathWouldRead()
    {
        var job = JobWithRevision();
        this._clients.GetOutputLanguageAsync(job.ClientId, Arg.Any<CancellationToken>()).Returns("de");
        this._clients.GetReviewPassesAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ReviewPassSpec>>(_ =>
            [
                new ReviewPassSpec(Guid.NewGuid(), LogicalModelName: "reviewer-medium"),
                new ReviewPassSpec(Guid.NewGuid(), Lens: "security", LogicalModelName: "reviewer-high"),
            ]);
        this._exclusions.FetchAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(ReviewExclusionRules.FromPatterns(["**/*.min.js"]));

        var resolution = await this.CreateResolver().ResolveAsync(RequestFor(job));

        Assert.True(resolution.Succeeded);
        var manifest = resolution.Manifest!;

        // Each of these is read by the in-process path from the same accessor.
        Assert.Equal(await this._clients.GetOutputLanguageAsync(job.ClientId), manifest.Prompts.Language);
        var configuredPasses = await this._clients.GetReviewPassesAsync(job.ClientId);
        Assert.Equal(configuredPasses.Count, manifest.Passes.Count);
        Assert.Equal(
            configuredPasses.Select(pass => pass.LogicalModelName),
            manifest.Passes.Select(pass => pass.Model.LogicalModelName));
        Assert.Equal(configuredPasses.Select(pass => pass.Lens), manifest.Passes.Select(pass => pass.Lens));
        Assert.Equal(["**/*.min.js"], manifest.Exclusions);
        Assert.Equal(["src/a.cs"], manifest.Target.ChangedPaths);
        Assert.Equal("head-sha", manifest.Target.HeadSha);
        Assert.Equal("base-sha", manifest.Target.BaseSha);
        Assert.Equal(3, manifest.LeaseGeneration);
        Assert.Equal(RunnerContractVersion.Current, manifest.ContractVersion);
    }

    // The pass ordinal is how a pass's trace is identified, so the order the client configured has to
    // survive into the manifest rather than being reconstructed on the far side.
    // The per-client decisions that change what a review does. A runner has no client record to read them
    // from, so a manifest that omits them lets every one fall to its default, and the review becomes a
    // different review with nothing to show it. The clearest case is the pass list, which does nothing
    // unless the union is on.
    [Fact]
    public async Task TheManifest_CarriesThePerClientDecisionsThatChangeWhatAReviewDoes()
    {
        var job = JobWithRevision();
        job.SetAiConfig(null, "gpt-5.6-luna", 0.25f);
        this._clients.GetMultiPassUnionEnabledAsync(job.ClientId, Arg.Any<CancellationToken>()).Returns(true);
        this._clients.GetLanguageRobustScreeningEnabledAsync(job.ClientId, Arg.Any<CancellationToken>()).Returns(true);
        this._clients.GetEvidenceBackedVerificationEnabledAsync(job.ClientId, Arg.Any<CancellationToken>()).Returns(true);
        this._clients.GetIncludeLinkedItemsInContextEnabledAsync(job.ClientId, Arg.Any<CancellationToken>()).Returns(false);

        var resolution = await this.CreateResolver().ResolveAsync(RequestFor(job));

        var behaviour = resolution.Manifest!.Behaviour;
        Assert.NotNull(behaviour);
        Assert.True(behaviour!.EnableMultiPassUnion);
        Assert.True(behaviour.EnableLanguageRobustScreening);
        Assert.True(behaviour.EnableEvidenceBackedVerification);
        Assert.False(behaviour.IncludeLinkedItemsInContext);
        Assert.Equal(0.25f, behaviour.Temperature);
    }

    // Read from the client, not assumed: a client with everything off must produce a manifest that states it
    // rather than one that omits the section and lets the executor guess.
    [Fact]
    public async Task TheManifest_SaysSoWhenEveryDecisionIsOff()
    {
        var resolution = await this.CreateResolver().ResolveAsync(RequestFor(JobWithRevision()));

        var behaviour = resolution.Manifest!.Behaviour;
        Assert.NotNull(behaviour);
        Assert.False(behaviour!.EnableMultiPassUnion);
        Assert.False(behaviour.EnableLanguageRobustScreening);
        Assert.False(behaviour.EnableEvidenceBackedVerification);
        Assert.Null(behaviour.Temperature);
    }

    [Fact]
    public async Task ThePassOrdinals_FollowTheConfiguredOrder()
    {
        var job = JobWithRevision();
        this._clients.GetReviewPassesAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ReviewPassSpec>>(_ =>
            [
                new ReviewPassSpec(Guid.NewGuid(), LogicalModelName: "first"),
                new ReviewPassSpec(Guid.NewGuid(), LogicalModelName: "second"),
                new ReviewPassSpec(Guid.NewGuid(), LogicalModelName: "third"),
            ]);

        var manifest = (await this.CreateResolver().ResolveAsync(RequestFor(job))).Manifest!;

        Assert.Equal([1, 2, 3], manifest.Passes.Select(pass => pass.Ordinal));
        Assert.Equal(["first", "second", "third"], manifest.Passes.Select(pass => pass.Model.LogicalModelName));
    }

    // Resolving once is the point. A configuration change made while the review runs must not reach into a
    // review already under way, which is as true in-process as it is for a runner.
    [Fact]
    public async Task AConfigurationChangeAfterDispatch_DoesNotReachTheRunningJob()
    {
        var job = JobWithRevision();
        var manifest = (await this.CreateResolver().ResolveAsync(RequestFor(job))).Manifest!;

        this._clients.GetOutputLanguageAsync(job.ClientId, Arg.Any<CancellationToken>()).Returns("fr");
        this._clients.GetReviewPassesAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ReviewPassSpec>>(_ => []);

        Assert.Equal("en", manifest.Prompts.Language);
        Assert.Single(manifest.Passes);
    }

    [Fact]
    public async Task AJobWithNoPinnedRevision_IsRefusedRatherThanDispatched()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 42, 1);

        var resolution = await this.CreateResolver().ResolveAsync(RequestFor(job));

        Assert.False(resolution.Succeeded);
        Assert.Null(resolution.Manifest);
        Assert.Contains("head and base commit", resolution.Refusal!, StringComparison.Ordinal);
    }

    // An executor cannot tell a value that failed to resolve from one deliberately left empty, so a partial
    // manifest would have it review under configuration that was never chosen.
    [Fact]
    public async Task AFailureAnywhereInResolution_RefusesTheLeaseInsteadOfSendingAPartialManifest()
    {
        var job = JobWithRevision();
        this._exclusions.FetchAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns<ReviewExclusionRules>(_ => throw new InvalidOperationException("provider unreachable"));

        var resolution = await this.CreateResolver().ResolveAsync(RequestFor(job));

        Assert.False(resolution.Succeeded);
        Assert.Null(resolution.Manifest);
        Assert.Contains("provider unreachable", resolution.Refusal!, StringComparison.Ordinal);
    }

    // A pass that still binds a concrete model cannot run remotely: resolving that binding is what keeps
    // the provider key on the control plane, and a name is the only thing safe to send.
    [Fact]
    public async Task APassBindingAConcreteModel_RefusesTheLease()
    {
        var job = JobWithRevision();
        this._clients.GetReviewPassesAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ReviewPassSpec>>(_ => [new ReviewPassSpec(Guid.NewGuid())]);

        var resolution = await this.CreateResolver().ResolveAsync(RequestFor(job));

        Assert.False(resolution.Succeeded);
        Assert.Contains("logical model", resolution.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BudgetHeadroom_IsTheTightestHardCapLessWhatIsAlreadySpent()
    {
        var job = JobWithRevision();
        var caps = Substitute.For<IBudgetCapsProvider>();
        caps.GetCapsAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(new BudgetCaps(null, 100m, null, 20m, null, null));
        var spend = Substitute.For<IReviewSpendAccumulator>();
        spend.GetBaselineAsync(Arg.Any<ReviewSpendSubject>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewSpendBaseline(
                    new ReviewScopeSpend(30m, false),
                    new ReviewScopeSpend(5m, false),
                    ReviewScopeSpend.None));

        var manifest = (await this.CreateResolver(caps, spend).ResolveAsync(RequestFor(job))).Manifest!;

        // Monthly leaves 70; the pull request leaves 15. The tighter one is what the executor may spend.
        Assert.Equal(15m, manifest.BudgetHeadroomUsd);
    }

    [Fact]
    public async Task NoConfiguredHardCap_MeansNoHeadroomToReport()
    {
        var job = JobWithRevision();
        var caps = Substitute.For<IBudgetCapsProvider>();
        caps.GetCapsAsync(job.ClientId, Arg.Any<CancellationToken>()).Returns(BudgetCaps.None);

        var manifest = (await this.CreateResolver(caps, Substitute.For<IReviewSpendAccumulator>())
            .ResolveAsync(RequestFor(job))).Manifest!;

        Assert.Null(manifest.BudgetHeadroomUsd);
    }

    [Fact]
    public async Task RepositoryInstructions_AreNarrowedToTheOnesRelevantToTheChangedPaths()
    {
        var job = JobWithRevision();
        var all = new List<RepositoryInstruction>
        {
            new("a.md", "A", "always", "body a"),
            new("b.md", "B", "sometimes", "body b"),
        };
        this._instructions.FetchAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<RepositoryInstruction>>(_ => all);
        this._relevance.EvaluateRelevanceAsync(
                Arg.Any<IReadOnlyList<RepositoryInstruction>>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<RepositoryInstruction>>(_ => [all[1]]);

        var manifest = (await this.CreateResolver().ResolveAsync(RequestFor(job))).Manifest!;

        Assert.Single(manifest.RepositoryInstructions);
        Assert.Equal("b.md", manifest.RepositoryInstructions[0].FileName);
        Assert.Equal("body b", manifest.RepositoryInstructions[0].Body);
    }

    // The relay routes by name and nothing else. A client whose review purpose points straight at a
    // connection has no name to send, and dispatching anyway would hand the runner a manifest whose first
    // model call cannot be served.
    [Fact]
    public async Task AClientWithoutANamedDefaultModel_IsRefusedRatherThanDispatched()
    {
        var job = JobWithRevision();
        var unnamed = Runtime("ignored");
        unnamed.LogicalModelName.Returns((string?)null);
        this._runtimes.ResolveChatRuntimeAsync(job.ClientId, Arg.Any<AiPurpose>(), Arg.Any<CancellationToken>())
            .Returns(unnamed);

        var resolution = await this.CreateResolver().ResolveAsync(RequestFor(job));

        Assert.False(resolution.Succeeded);
        Assert.Contains("logical model", resolution.Refusal, StringComparison.OrdinalIgnoreCase);
    }

    // A shadow pass runs for comparison and never publishes. Dropping the flag on the way out would make a
    // runner post findings from a pass the client is still evaluating.
    [Fact]
    public async Task AShadowPass_TravelsAsAShadowPass()
    {
        var job = JobWithRevision();
        this._clients.GetReviewPassesAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ReviewPassSpec>>(_ =>
            [
                new ReviewPassSpec(Guid.NewGuid(), LogicalModelName: "live"),
                new ReviewPassSpec(Guid.NewGuid(), Shadow: true, LogicalModelName: "candidate"),
            ]);

        var manifest = (await this.CreateResolver().ResolveAsync(RequestFor(job))).Manifest!;

        Assert.Equal([false, true], manifest.Passes.Select(pass => pass.Shadow));
    }

    // The executor budgets its context against these before it makes a call, and cannot ask the control
    // plane what it is about to call: the manifest is the only description it gets.
    [Fact]
    public async Task EachModel_TravelsWithWhatTheExecutorNeedsToCountTokensAgainstIt()
    {
        var manifest = (await this.CreateResolver().ResolveAsync(RequestFor(JobWithRevision()))).Manifest!;

        Assert.Equal("reviewer-default", manifest.DefaultModel.LogicalModelName);
        Assert.Equal("gpt-5-mini", manifest.DefaultModel.RemoteModelId);
        Assert.Equal("o200k_base", manifest.DefaultModel.TokenizerName);
        Assert.Equal(400_000, manifest.DefaultModel.MaxContextTokens);
        Assert.Equal("OpenAi", manifest.DefaultModel.ProviderKind);

        var pass = Assert.Single(manifest.Passes);
        Assert.Equal("reviewer-medium", pass.Model.LogicalModelName);
        Assert.Equal("Medium", pass.Model.ReasoningEffort);
    }

    // The reviewer reads the conversation to avoid raising again what has already been answered, and it
    // cannot fetch it: reading a review's threads needs a credential the executor does not hold.
    [Fact]
    public async Task TheConversationAlreadyOnTheReview_TravelsWithTheManifest()
    {
        var job = JobWithRevision();
        var request = RequestFor(job) with
        {
            Description = "Adds the widget.",
            ExistingThreads =
            [
                new PrCommentThread("t1", "src/a.cs", 12, [new PrThreadComment("Reviewer", "Is this bounded?")], "active"),
            ],
        };

        var manifest = (await this.CreateResolver().ResolveAsync(request)).Manifest!;

        Assert.Equal("Adds the widget.", manifest.Target.Description);
        var thread = Assert.Single(manifest.Target.ExistingThreads);
        Assert.Equal("src/a.cs", thread.FilePath);
        Assert.Equal(12, thread.LineNumber);
        Assert.Equal("active", thread.Status);
        Assert.Equal("Is this bounded?", Assert.Single(thread.Comments).Content);
    }
}
