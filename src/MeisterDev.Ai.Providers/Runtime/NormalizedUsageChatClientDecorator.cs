// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Runtime.CompilerServices;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Usage;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Runtime;

/// <summary>
///     Replaces the usage a provider reported with the counters its driver mapped it onto, so every later reader
///     of the response sees the host's normalized shape.
/// </summary>
/// <remarks>
///     <para>
///         The driver's mapping is the only place a vendor's counter names are read, and a response travels a long
///         way past it: the budget stage prices it, the telemetry stage records it, the review loop accumulates it
///         onto a protocol, and the relay serialises it to a runner that resolves no driver at all. Applying the
///         mapping once, here, is what makes all of those read the same counters.
///     </para>
///     <para>
///         It fills <see cref="ProviderRuntimeStage.Normalization" />, the innermost stage, so the stages that
///         price and record the call are outside it and see the mapped counters. A response that reported no usage
///         is left alone: a payload invented for it would turn a missing measurement into a measured zero.
///     </para>
/// </remarks>
/// <param name="driver">The family whose mapping produces the counters.</param>
public sealed class NormalizedUsageChatClientDecorator(IAiProviderDriver driver) : IProviderChatClientDecorator
{
    /// <inheritdoc />
    public ProviderRuntimeStage Stage => ProviderRuntimeStage.Normalization;

    /// <inheritdoc />
    public IChatClient Decorate(IChatClient inner, ProviderEndpoint endpoint, ProviderModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(inner);

        return new NormalizedUsageChatClient(inner, driver);
    }

    private sealed class NormalizedUsageChatClient(IChatClient inner, IAiProviderDriver driver)
        : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

            if (response.Usage is not null)
            {
                response.Usage = driver.ReadUsage(response.Usage).ToUsageDetails();
            }

            return response;
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var updates = base.GetStreamingResponseAsync(messages, options, cancellationToken);

            await foreach (var update in updates.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                // Usage arrives on a streamed response as a content item on one of the updates, so the mapping is
                // applied there rather than to a response object the caller assembles itself. The contents are
                // replaced rather than written into, because the list an inner client hands over may be one it
                // does not allow a caller to edit.
                if (update.Contents.Any(content => content is UsageContent))
                {
                    update.Contents = [.. update.Contents.Select(Mapped)];
                }

                yield return update;
            }

            AIContent Mapped(AIContent content) =>
                content is UsageContent reported
                    ? new UsageContent(driver.ReadUsage(reported.Details).ToUsageDetails())
                    : content;
        }
    }
}
