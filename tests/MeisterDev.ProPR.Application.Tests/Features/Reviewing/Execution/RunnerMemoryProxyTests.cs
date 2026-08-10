// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Execution;

/// <summary>
///     The control-plane half of proxied thread memory. Authorization first, always; then the same service
///     an in-process review calls, on the job's own client — so what memory says about a finding does not
///     depend on which side asked.
/// </summary>
public sealed class RunnerMemoryProxyTests
{
    private static readonly Guid JobId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid ClientId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly RunnerCallContext Call = new(JobId, 4, "runner-a");

    private readonly IRunnerCallAuthorizer _authorizer = Substitute.For<IRunnerCallAuthorizer>();
    private readonly IReviewJobExecutionStore _jobs = Substitute.For<IReviewJobExecutionStore>();
    private readonly IThreadMemoryService _memory = Substitute.For<IThreadMemoryService>();

    [Fact]
    public async Task AServedCall_ReconsidersOnTheJobsOwnClient()
    {
        var job = Job();
        var reconsidered = new ReviewResult("reconsidered", []);
        this.Arrange(job, reconsidered);

        var result = await this.CreateProxy().ReconsiderAsync(Call, "src/a.cs", "@@", Draft(), 0.2f);

        Assert.True(result.IsServed);
        Assert.Same(reconsidered, result.Value);
        await this._memory.Received(1).RetrieveAndReconsiderAsync(
            ClientId,
            job,
            "src/a.cs",
            "@@",
            Arg.Any<ReviewResult>(),
            null,
            Arg.Any<CancellationToken>(),
            0.2f,
            null);
    }

    // Authorization comes first, always. A superseded executor must not read another attempt's memory.
    [Fact]
    public async Task AnUnauthorizedCall_NeverReachesTheStore()
    {
        this.Arrange(Job(), new ReviewResult("reconsidered", []), authorized: false);

        var result = await this.CreateProxy().ReconsiderAsync(Call, "src/a.cs", null, Draft(), null);

        Assert.Equal(RunnerCallRefusal.SupersededGeneration, result.Refusal);
        Assert.Empty(this._memory.ReceivedCalls());
    }

    // The offline harness has no memory store. Not-offered lets the executor record the degradation;
    // an empty success would read as "memory ran and found nothing".
    [Fact]
    public async Task AnInstallationWithoutAMemoryStore_AnswersNotOffered()
    {
        this.Arrange(Job(), new ReviewResult("reconsidered", []));

        var result = await this.CreateProxy(withStore: false).ReconsiderAsync(Call, "src/a.cs", null, Draft(), null);

        Assert.True(result.Unavailable);
        Assert.False(result.IsServed);
    }

    [Fact]
    public async Task AJobTheStoreDoesNotKnow_IsRefused()
    {
        this.Arrange(job: null, new ReviewResult("reconsidered", []));

        var result = await this.CreateProxy().ReconsiderAsync(Call, "src/a.cs", null, Draft(), null);

        Assert.Equal(RunnerCallRefusal.JobNotExecuting, result.Refusal);
        Assert.Empty(this._memory.ReceivedCalls());
    }

    // The reconsideration prompt reads the output language, the custom system message, and the stage
    // override off the context. Passed as null, a German client's remote review came back with
    // English reconsiderations, and an admin's override for this stage was silently ignored.
    [Fact]
    public async Task WithTheClientConfigurationAtHand_TheContextCarriesLanguageAndOverrides()
    {
        this.Arrange(Job(), new ReviewResult("reconsidered", []));
        var clients = Substitute.For<IClientRegistry>();
        clients.GetOutputLanguageAsync(ClientId, Arg.Any<CancellationToken>()).Returns("German");
        clients.GetCustomSystemMessageAsync(ClientId, Arg.Any<CancellationToken>()).Returns("Be terse.");
        var overrides = Substitute.For<IPromptOverrideService>();
        overrides.GetOverrideAsync(ClientId, null, "MemoryReconsiderationSystemPrompt", Arg.Any<CancellationToken>())
            .Returns("Reconsider like so.");
        ReviewSystemContext? captured = null;
        this._memory.RetrieveAndReconsiderAsync(
                Arg.Any<Guid>(), Arg.Any<ReviewJob>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<ReviewResult>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<float?>(),
                Arg.Do<ReviewSystemContext?>(context => captured = context))
            .Returns(new ReviewResult("reconsidered", []));

        var proxy = new RunnerMemoryProxy(this._authorizer, this._jobs, this._memory, clients, overrides);
        await proxy.ReconsiderAsync(Call, "src/a.cs", null, Draft(), null);

        Assert.NotNull(captured);
        Assert.Equal("German", captured!.OutputLanguage);
        Assert.Equal("Be terse.", captured.ClientSystemMessage);
        Assert.Equal("Reconsider like so.", captured.PromptOverrides["MemoryReconsiderationSystemPrompt"]);
    }

    private void Arrange(ReviewJob? job, ReviewResult reconsidered, bool authorized = true)
    {
        this._authorizer.AuthorizeAsync(Arg.Any<RunnerCallContext>(), Arg.Any<CancellationToken>())
            .Returns(
                authorized
                    ? RunnerCallAuthorization.Allow(Guid.NewGuid())
                    : RunnerCallAuthorization.Refuse(RunnerCallRefusal.SupersededGeneration));
        this._jobs.GetById(JobId).Returns(job);
        this._memory.RetrieveAndReconsiderAsync(
                Arg.Any<Guid>(),
                Arg.Any<ReviewJob>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<ReviewResult>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<float?>(),
                Arg.Any<ReviewSystemContext?>())
            .Returns(reconsidered);
    }

    private RunnerMemoryProxy CreateProxy(bool withStore = true)
    {
        return new RunnerMemoryProxy(this._authorizer, this._jobs, withStore ? this._memory : null);
    }

    private static ReviewResult Draft()
    {
        return new ReviewResult("draft", [new ReviewComment("src/a.cs", 3, CommentSeverity.Warning, "Maybe real")]);
    }

    private static ReviewJob Job()
    {
        return new ReviewJob(JobId, ClientId, "https://forge.invalid", "team", "repo-id", 12, 2);
    }
}
