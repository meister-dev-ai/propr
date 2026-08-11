// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Net;
using System.Net.Http.Json;
using MeisterDev.ProPR.Runner.Contracts;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Runner.Execution;

/// <summary>
///     Every model call the runner makes, routed through the control plane.
///     <para>
///         This is what keeps the provider key off the host. The manifest names a logical model, never a
///         connection, and the control plane resolves that name against a stored credential the runner
///         cannot read. A runner that could call a provider directly would need that credential, and the
///         whole separation would be a naming convention rather than a boundary.
///     </para>
///     <para>
///         It is also where the hard budget cap is actually enforced. The manifest's headroom figure is an
///         optimisation for winding down gracefully and is stale the moment it is written; the refusal
///         that matters comes back from here.
///     </para>
/// </summary>
public sealed class RelayChatClient(
    HttpClient http,
    Guid jobId,
    int leaseGeneration,
    string logicalModelName,
    RunnerBudgetSignal? budgetSignal = null) : IChatClient
{
    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var request = new
        {
            jobId,
            leaseGeneration,
            contractVersion = RunnerContractVersion.Current,
            logicalModelName,
            messages = messages.ToArray(),

            // The tools, temperature, output ceiling, and reasoning settings the pipeline shaped this call
            // with. Dropped, a tool-using review becomes a single-turn review with nothing to show it.
            options = RelayedChatOptions.FromChatOptions(options),

            // Idempotent per call so a retry after a network failure is charged once. The relay keeps the
            // first answer and replays it rather than spending a second completion.
            idempotencyKey = Guid.NewGuid().ToString("N"),
        };

        using var response = await http.PostAsJsonAsync("ai/chat", request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.PaymentRequired
            || response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // The hard stop doubles as the wind-down signal: once one completion is refused for budget,
            // every file not yet started would meet the same refusal, so the planner should stop
            // starting them rather than fail them one 402 at a time.
            if (response.StatusCode == HttpStatusCode.PaymentRequired)
            {
                budgetSignal?.MarkExhausted();
            }

            var error = await ReadErrorAsync(response, cancellationToken);
            throw new RelayRefusedException(error ?? "The control plane refused the completion.");
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            // The reason travels with the refusal. "Superseded" and "not the owner" are different
            // operational problems, and collapsing them into one sentence cost an afternoon of guessing.
            var refusal = await ReadErrorAsync(response, cancellationToken);
            throw new RelayRefusedException($"This runner no longer holds the lease for the job it is reviewing ({refusal ?? "no reason given"}).");
        }

        response.EnsureSuccessStatusCode();

        var relayed = await response.Content.ReadFromJsonAsync<RelayEnvelope>(cancellationToken);

        // The wind-down half of the budget contract. The relay says the soft cap is reached on the very
        // completion that crossed it; a runner that dropped this reviewed every remaining file at full
        // cost until the hard cap refused it mid-pass.
        if (relayed?.SoftCapReached == true)
        {
            budgetSignal?.MarkExhausted();
        }

        return relayed?.Response
               ?? throw new RelayRefusedException("The control plane returned an unreadable completion.");
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Deliberately not supported. Streaming through the relay would mean streaming usage accounting
        // too, and the cap is enforced per completion; the review pipeline does not stream.
        throw new NotSupportedException("The runner relays whole completions; streaming is not offered.");
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // The HttpClient is owned by the factory that built this, and outlives one job.
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<RunnerContractError>(ct);
            return error?.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or NotSupportedException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private sealed record RelayEnvelope(ChatResponse? Response, bool SoftCapReached, bool Replayed);
}

/// <summary>
///     A completion the control plane would not serve: the budget cap is reached, or the lease is gone.
///     Distinct from a transport failure, because retrying it is pointless and the review should wind down.
/// </summary>
public sealed class RelayRefusedException(string message) : Exception(message);
