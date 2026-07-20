// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Runtime.CompilerServices;
using MeisterProPR.Application.AI;
using MeisterProPR.Application.Features.Budgeting;
using MeisterProPR.Domain.Services;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>
///     A chat-client decorator that enforces the ambient review job's USD hard cap. Before each call it stops the
///     review (by throwing <see cref="BudgetHardCapReachedException" />) when the accumulated spend has reached a
///     hard cap; after each call it prices the response's token usage and records it against the running total.
///     When no budget scope is active (the common case — the client has no caps, or the call is outside a review),
///     it is a transparent pass-through.
/// </summary>
public sealed class BudgetEnforcingChatClient(
    IChatClient innerClient,
    IBudgetScopeAccessor budgetScopeAccessor,
    ModelPricing pricing) : DelegatingChatClient(innerClient)
{
    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var scope = budgetScopeAccessor.Current;
        scope?.ThrowIfHardCapReached();

        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

        scope?.RecordCall(this.Price(AiTokenUsageExtractor.FromResponse(response)));
        return response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var scope = budgetScopeAccessor.Current;
        scope?.ThrowIfHardCapReached();

        UsageDetails? usage = null;
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            foreach (var content in update.Contents)
            {
                if (content is UsageContent usageContent)
                {
                    usage = usageContent.Details;
                }
            }

            yield return update;
        }

        if (scope is not null && usage is not null)
        {
            scope.RecordCall(this.Price(AiTokenUsageExtractor.FromUsage(usage)));
        }
    }

    private decimal? Price(AiTokenUsage usage)
    {
        return AiCostCalculator.Calculate(usage, pricing).Usd;
    }
}
