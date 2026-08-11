// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Execution;

public sealed class RunnerToolProxyTests
{
    private static readonly Guid JobId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly RunnerCallContext Call = new(JobId, 4, "runner-a");

    private readonly IRunnerCallAuthorizer _authorizer = Substitute.For<IRunnerCallAuthorizer>();
    private readonly IRunnerJobToolsRegistry _registry = new RunnerJobToolsRegistry();
    private readonly IReviewContextTools _tools = Substitute.For<IReviewContextTools>();

    private RunnerToolProxy CreateProxy(bool authorized = true, bool codeKnowledge = true, bool held = true)
    {
        this._authorizer.AuthorizeAsync(Arg.Any<RunnerCallContext>(), Arg.Any<CancellationToken>())
            .Returns(
                authorized
                    ? RunnerCallAuthorization.Allow(Guid.NewGuid())
                    : RunnerCallAuthorization.Refuse(RunnerCallRefusal.SupersededGeneration));

        if (held)
        {
            this._registry.Register(JobId, this._tools, codeKnowledge);
        }

        return new RunnerToolProxy(this._authorizer, this._registry);
    }

    // The result has to be the shape the in-process tool returns, or the pipeline behaves differently
    // depending on which side answered, which is the defect class this whole boundary is trying to avoid.
    [Fact]
    public async Task AServedCall_ReturnsTheSameShapeTheInProcessToolWouldHave()
    {
        IReadOnlyList<ChangedFileSummary> changed =
            [new ChangedFileSummary("src/a.cs", ChangeType.Edit), new ChangedFileSummary("src/b.cs", ChangeType.Add)];
        this._tools.GetChangedFilesAsync(Arg.Any<CancellationToken>()).Returns(changed);

        var result = await this.CreateProxy().GetChangedFilesAsync(Call);

        Assert.True(result.IsServed);
        Assert.Equal(changed, result.Value);
    }

    // Authorization comes first, always. A refused caller must not reach a credentialed provider call.
    [Fact]
    public async Task AnUnauthorizedCall_NeverReachesTheTools()
    {
        var result = await this.CreateProxy(authorized: false).GetChangedFilesAsync(Call);

        Assert.False(result.IsServed);
        Assert.Equal(RunnerCallRefusal.SupersededGeneration, result.Refusal);
        await this._tools.DidNotReceive().GetChangedFilesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("knowledge")]
    [InlineData("symbol")]
    [InlineData("linked-details")]
    [InlineData("linked-discussion")]
    [InlineData("linked-resolve")]
    public async Task EveryProxiedOperation_IsAuthorizedBeforeItRuns(string operation)
    {
        var proxy = this.CreateProxy(authorized: false);

        var refusal = operation switch
        {
            "knowledge" => (await proxy.AskKnowledgeAsync(Call, "why")).Refusal,
            "symbol" => (await proxy.GetSymbolInsightAsync(Call, "Sym", null, null)).Refusal,
            "linked-details" => (await proxy.GetLinkedItemDetailsAsync(Call, "AB#1")).Refusal,
            "linked-discussion" => (await proxy.GetLinkedItemDiscussionAsync(Call, "AB#1")).Refusal,
            _ => (await proxy.ResolveLinkedItemAsync(Call, "AB#2")).Refusal,
        };

        Assert.Equal(RunnerCallRefusal.SupersededGeneration, refusal);
        Assert.Empty(this._tools.ReceivedCalls());
    }

    // An installation without code knowledge does not offer these tools at all. Answering "nothing found"
    // instead would have an executor record an absence of knowledge as evidence about the code.
    [Fact]
    public async Task WithCodeKnowledgeSwitchedOff_TheKnowledgeToolsAreNotOfferedRatherThanEmpty()
    {
        var proxy = this.CreateProxy(codeKnowledge: false);

        var knowledge = await proxy.AskKnowledgeAsync(Call, "why is this here");
        var symbol = await proxy.GetSymbolInsightAsync(Call, "Sym", null, null);

        Assert.True(knowledge.Unavailable);
        Assert.True(symbol.Unavailable);
        Assert.False(knowledge.IsServed);
        await this._tools.DidNotReceive().AskProCursorKnowledgeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // Switching code knowledge off must not take the source-control tools with it.
    [Fact]
    public async Task WithCodeKnowledgeSwitchedOff_TheSourceControlToolsStillWork()
    {
        this._tools.GetChangedFilesAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ChangedFileSummary>>(_ => []);

        var result = await this.CreateProxy(codeKnowledge: false).GetChangedFilesAsync(Call);

        Assert.True(result.IsServed);
    }

    // The replica that granted the lease is the one holding the job's tools open. Another replica cannot
    // serve the call, and reports it in the way the caller already knows how to handle.
    [Fact]
    public async Task AReplicaNotHoldingTheJob_RefusesRatherThanImprovising()
    {
        var result = await this.CreateProxy(held: false).GetChangedFilesAsync(Call);

        Assert.False(result.IsServed);
        Assert.Equal(RunnerCallRefusal.JobNotExecuting, result.Refusal);
    }

    [Fact]
    public async Task ReleasingAJob_StopsItsToolsBeingServed()
    {
        var proxy = this.CreateProxy();
        Assert.True(
            (await proxy.GetChangedFilesAsync(Call)).IsServed
            || (await proxy.GetChangedFilesAsync(Call)).Refusal == RunnerCallRefusal.None);

        this._registry.Release(JobId);

        Assert.Equal(RunnerCallRefusal.JobNotExecuting, (await proxy.GetChangedFilesAsync(Call)).Refusal);
    }

    [Fact]
    public async Task LinkedItemOperations_DelegateTheirArgumentsUnchanged()
    {
        var proxy = this.CreateProxy();
        this._tools.GetLinkedItemDiscussionAsync("AB#7", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<LinkedItemComment>>(_ => []);

        var result = await proxy.GetLinkedItemDiscussionAsync(Call, "AB#7");

        Assert.True(result.IsServed);
        await this._tools.Received(1).GetLinkedItemDiscussionAsync("AB#7", Arg.Any<CancellationToken>());
    }
}
