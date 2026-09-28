// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Usage;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Services;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution;

/// <summary>
///     What the in-process relay client asks the provider for. The pass shapes its options when the review
///     starts; the relay answers the tenant's reasoning-capture question again for every completion, and the
///     options that reach the provider have to follow the later answer.
/// </summary>
public sealed class RelayChatClientReasoningTests
{
    private static readonly RunnerCallContext Call =
        new(Guid.Parse("77777777-7777-4777-8777-777777777777"), 3, "runner-a");

    // The case a tenant policy saved mid-review produces: options built for a review that captured reasoning,
    // and a job that no longer does.
    [Fact]
    public async Task AJobThatWithholdsReasoning_AsksTheProviderForNoSummary()
    {
        var relay = new RecordingRelay(capturesReasoning: false);
        var client = new RelayChatClient(Call, "reviewer-medium", relay, ReviewReasoningEffort.None, () => "k");

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "review this")],
            new ChatOptions().ApplyReasoning(captureReasoning: true, ReviewReasoningEffort.None));

        var request = Assert.IsType<ProviderReasoningRequest>(RawRequest(relay.Relayed));
        Assert.False(request.CaptureReasoning);
    }

    // The effort is configuration, not consent: a tenant that keeps no reasoning still gets the model it
    // paid for. Dropping the effort with the summary would quietly downgrade every review of that tenant.
    [Fact]
    public async Task AJobThatWithholdsReasoning_KeepsTheEffortTheJobRunsAt()
    {
        var relay = new RecordingRelay(capturesReasoning: false);
        var client = new RelayChatClient(Call, "reviewer-medium", relay, ReviewReasoningEffort.High, () => "k");

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "review this")],
            new ChatOptions().ApplyReasoning(captureReasoning: true, ReviewReasoningEffort.High));

        var request = Assert.IsType<ProviderReasoningRequest>(RawRequest(relay.Relayed));
        Assert.False(request.CaptureReasoning);
        Assert.Equal(ProviderReasoningEffort.High, request.Effort);
    }

    // The pass shaped its own reasoning request when the review started, under the decision in force then. A
    // relay that added to those options instead of stating the current answer would leave the earlier one.
    [Fact]
    public async Task AJobThatCapturesReasoning_OverridesTheRequestThePassShaped()
    {
        var relay = new RecordingRelay(capturesReasoning: true);
        var client = new RelayChatClient(Call, "reviewer-medium", relay, ReviewReasoningEffort.High, () => "k");

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "review this")],
            new ChatOptions().ApplyReasoning(captureReasoning: false, ReviewReasoningEffort.High));

        var request = Assert.IsType<ProviderReasoningRequest>(RawRequest(relay.Relayed));
        Assert.True(request.CaptureReasoning);
        Assert.Equal(ProviderReasoningEffort.High, request.Effort);
    }

    // Tool calling is what a review pass is made of, and the output ceiling bounds what it may spend on the
    // answer. A rebuild that dropped either would turn a relayed pass into a different pass.
    [Fact]
    public async Task TheRebuiltOptions_KeepEveryToolAndTheCeilingThePassSet()
    {
        var relay = new RecordingRelay(capturesReasoning: true);
        var client = new RelayChatClient(Call, "reviewer-medium", relay, ReviewReasoningEffort.None, () => "k");

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "review this")],
            new ChatOptions
            {
                Tools =
                [
                    AIFunctionFactory.Create(() => "x", "probe"),
                    AIFunctionFactory.Create(() => "y", "read_file"),
                ],
                MaxOutputTokens = 4096,
            });

        Assert.Equal(["probe", "read_file"], relay.Relayed!.Tools!.Select(tool => tool.Name));
        Assert.Equal(4096, relay.Relayed.MaxOutputTokens);
        Assert.True(AsksForASummary(relay.Relayed));
    }

    // The options argument is nullable by contract and a pass may send none. The tenant's answer governs those
    // calls too, so a job that captures reasoning has to get the request built for it.
    [Fact]
    public async Task AJobThatCapturesReasoning_ShapesOptionsThePassDidNotSend()
    {
        var relay = new RecordingRelay(capturesReasoning: true);
        var client = new RelayChatClient(Call, "reviewer-medium", relay, ReviewReasoningEffort.None, () => "k");

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "review this")]);

        Assert.True(AsksForASummary(relay.Relayed));
    }

    // The effort is configuration and reaches the provider on its own account, so a call that sent no options
    // still runs at the level the job is configured for.
    [Fact]
    public async Task AConfiguredEffort_ShapesOptionsThePassDidNotSend()
    {
        var relay = new RecordingRelay(capturesReasoning: false);
        var client = new RelayChatClient(Call, "reviewer-medium", relay, ReviewReasoningEffort.Low, () => "k");

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "review this")]);

        var request = Assert.IsType<ProviderReasoningRequest>(RawRequest(relay.Relayed));
        Assert.Equal(ProviderReasoningEffort.Low, request.Effort);
        Assert.False(request.CaptureReasoning);
    }

    // Nothing to ask for and nothing to withdraw: the call reaches the provider as it would have without any
    // reasoning options, which is what an ordinary sampling model expects.
    [Fact]
    public async Task AJobWithNothingToAskFor_SendsNoOptions()
    {
        var relay = new RecordingRelay(capturesReasoning: false);
        var client = new RelayChatClient(Call, "reviewer-medium", relay, ReviewReasoningEffort.None, () => "k");

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "review this")]);

        Assert.Null(relay.Relayed);
    }

    // A provider request the pipeline did not build cannot be shown to ask for no reasoning, and the tenant
    // keeps none, so it goes. The rest of what the pass shaped is not this shaping's to take away.
    [Fact]
    public async Task AProviderRequestThisPipelineDidNotBuild_IsDroppedWhenTheTenantKeepsNoReasoning()
    {
        var relay = new RecordingRelay(capturesReasoning: false);
        var client = new RelayChatClient(Call, "reviewer-medium", relay, ReviewReasoningEffort.None, () => "k");

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "review this")],
            new ChatOptions
            {
                MaxOutputTokens = 4096,
                RawRepresentationFactory = _ => "the options the pass shaped itself",
            });

        Assert.Null(relay.Relayed!.RawRepresentationFactory);
        Assert.Equal(4096, relay.Relayed.MaxOutputTokens);
    }

    // The factory is the caller's code and is written for the provider client it will be handed: one that
    // casts to a concrete provider type throws for anything else. The relay decides what to do with it
    // without calling it, so a pass shaping its own provider options still gets its completion.
    [Fact]
    public async Task AProviderRequestThisPipelineDidNotBuild_IsNeverCalledToFindOutWhatItAsksFor()
    {
        var relay = new RecordingRelay(capturesReasoning: false);
        var client = new RelayChatClient(Call, "reviewer-medium", relay, ReviewReasoningEffort.None, () => "k");

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "review this")],
            new ChatOptions
            {
                RawRepresentationFactory = _ => throw new InvalidOperationException("This factory only accepts the provider client it was written for."),
            });

        Assert.Equal("ok", response.Text);
    }

    // With a summary to ask for, the resolved answer is what reaches the provider, whatever the options
    // already carried.
    [Fact]
    public async Task AJobThatCapturesReasoning_ReplacesAProviderRequestThisPipelineDidNotBuild()
    {
        var relay = new RecordingRelay(capturesReasoning: true);
        var client = new RelayChatClient(Call, "reviewer-medium", relay, ReviewReasoningEffort.None, () => "k");

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "review this")],
            new ChatOptions { RawRepresentationFactory = _ => "the options the pass shaped itself" });

        Assert.True(AsksForASummary(relay.Relayed));
    }

    // The caller owns the options it passed and may send the same instance again, to this client or to
    // another, so the shaping is applied to a copy. Clearing the factory on the caller's instance would take
    // the provider request away from every later call that instance is used for.
    [Fact]
    public async Task DroppingAnUnreadableProviderRequest_LeavesTheCallersOptionsUntouched()
    {
        var relay = new RecordingRelay(capturesReasoning: false);
        var client = new RelayChatClient(Call, "reviewer-medium", relay, ReviewReasoningEffort.None, () => "k");
        var callersFactory = (Func<IChatClient, object?>)(_ => "the options the pass shaped itself");
        var callersOptions = new ChatOptions { MaxOutputTokens = 4096, RawRepresentationFactory = callersFactory };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "review this")], callersOptions);

        Assert.Null(relay.Relayed!.RawRepresentationFactory);
        Assert.Same(callersFactory, callersOptions.RawRepresentationFactory);
    }

    // The same for the path that states a reasoning request: it is written onto a copy, so the instance the
    // pass holds keeps the request it shaped and the temperature it set.
    [Fact]
    public async Task StatingTheResolvedReasoningRequest_LeavesTheCallersOptionsUntouched()
    {
        var relay = new RecordingRelay(capturesReasoning: true);
        var client = new RelayChatClient(Call, "reviewer-medium", relay, ReviewReasoningEffort.High, () => "k");
        var callersOptions = new ChatOptions { Temperature = 0.2f };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "review this")], callersOptions);

        Assert.True(AsksForASummary(relay.Relayed));
        Assert.Null(callersOptions.RawRepresentationFactory);
        Assert.Equal(0.2f, callersOptions.Temperature);
    }

    /// <summary>Whether these options ask a reasoning-capable provider to return its reasoning.</summary>
    private static bool AsksForASummary(ChatOptions? options)
    {
        return RawRequest(options) is ProviderReasoningRequest { CaptureReasoning: true };
    }

    // The control plane holds a job open while an executor works on it. A completion for a job it is no longer
    // holding carries no authorization reason, so the refusal the pass is told about has to be stated here.
    [Fact]
    public async Task AJobTheControlPlaneNoLongerHolds_IsReportedAsAJobThatIsNotExecuting()
    {
        var client = new RelayChatClient(Call, "reviewer-medium", new RefusingRelay(RunnerRelayResult.JobNotHeld()), ReviewReasoningEffort.None, () => "k");

        var refused = await Assert.ThrowsAsync<RunnerCallRefusedException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "review this")]));

        Assert.Equal(RunnerCallRefusal.JobNotExecuting, refused.Refusal);
        Assert.DoesNotContain(nameof(RunnerCallRefusal.None), refused.Message, StringComparison.Ordinal);
    }

    // An authorization refusal already names its own reason, which travels unchanged.
    [Fact]
    public async Task ARefusedCaller_IsReportedWithTheAuthorizationReason()
    {
        var client = new RelayChatClient(
            Call,
            "reviewer-medium",
            new RefusingRelay(RunnerRelayResult.NotAuthorized(RunnerCallRefusal.SupersededGeneration)),
            ReviewReasoningEffort.None,
            () => "k");

        var refused = await Assert.ThrowsAsync<RunnerCallRefusedException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "review this")]));

        Assert.Equal(RunnerCallRefusal.SupersededGeneration, refused.Refusal);
    }

    /// <summary>What a provider speaking its own protocol is handed for these options, if anything.</summary>
    private static object? RawRequest(ChatOptions? options)
    {
        return options?.RawRepresentationFactory?.Invoke(new NativeProtocolClient());
    }

    /// <summary>A relay that performs nothing and answers with one prepared refusal.</summary>
    private sealed class RefusingRelay(RunnerRelayResult refusal) : IRunnerAiRelay
    {
        public Task<RunnerRelayResult> CompleteAsync(
            RunnerCallContext call,
            RunnerRelayRequest request,
            CancellationToken ct = default)
        {
            return Task.FromResult(refusal);
        }
    }

    /// <summary>A relay that answers one reasoning-capture decision and keeps the options it was given.</summary>
    private sealed class RecordingRelay(bool capturesReasoning) : IRunnerAiRelay
    {
        public ChatOptions? Relayed { get; private set; }

        public Task<RunnerRelayResult> CompleteAsync(
            RunnerCallContext call,
            RunnerRelayRequest request,
            CancellationToken ct = default)
        {
            this.Relayed = request.OptionsFor(capturesReasoning);

            return Task.FromResult(
                RunnerRelayResult.Completed(
                    new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")),
                    false,
                    ProviderTokenUsage.Missing));
        }
    }

    private sealed class NativeProtocolClient : INativeProtocolChatClient
    {
        public string NativeProtocol => ProviderDeclaredProtocolModes.Auto;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
