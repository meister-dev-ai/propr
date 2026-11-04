// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.DTOs;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Application.AI;

/// <summary>
///     Calls a configured model once, with the smallest request it serves, to find out whether the model answers as
///     the profile configures it.
/// </summary>
/// <remarks>
///     A verification lists the models of an endpoint, which proves the credential and the address but not one model:
///     a model identifier with a typo, or one the key may not use, passes verification and fails every call of a
///     review. A chat model is asked for at most <see cref="MaximumOutputTokens" /> tokens, and an embedding
///     model embeds one short text, whose vector length is compared with the configured dimensions and reported in
///     the outcome. The calls go to the family directly and not through the review runtime, so they are neither
///     retried nor metered against a review. They use the credential of the profile, so a caller asks its owner
///     before it checks.
/// </remarks>
public static class AiModelChecks
{
    /// <summary>The most tokens a chat model is asked to answer with.</summary>
    public const int MaximumOutputTokens = 16;

    /// <summary>How long a model may take to answer one check.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(25);

    internal const string Prompt = "Reply with OK.";
    internal const string EmbeddingInput = "ProPR model check";

    /// <summary>Checks one model of a profile.</summary>
    /// <param name="driver">The family of the profile.</param>
    /// <param name="endpoint">Where the profile reaches the provider, with its credential.</param>
    /// <param name="model">The configured model.</param>
    /// <param name="operation">Whether the model is checked for chat or embeddings.</param>
    /// <param name="ct">The cancellation of the caller, which propagates.</param>
    /// <param name="timeout">How long the model may take; <see cref="Timeout" /> without one.</param>
    public static async Task<AiModelCheckOutcomeDto> CheckAsync(
        IAiProviderDriver driver,
        ProviderEndpoint endpoint,
        AiConfiguredModelDto model,
        AiOperationKind operation,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(model);
        var limit = timeout ?? Timeout;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limit);
        try
        {
            if (operation == AiOperationKind.Embedding)
            {
                if (model.EmbeddingDimensions is not { } dimensions)
                {
                    return Failed(
                        model, operation, AiModelCheckReasons.Dimensions, null,
                        "The profile states no dimensions for this embedding model.",
                        "Enter the dimensions of the vectors that the model returns.");
                }

                using var generator = driver.CreateEmbeddingGenerator(endpoint, model.ToProviderModel(), ProviderDeclaredProtocolModes.Embeddings, dimensions);
                var embeddings = await generator
                    .GenerateAsync([EmbeddingInput], cancellationToken: deadline.Token)
                    .ConfigureAwait(false);
                int? returned = embeddings.Count == 0 ? null : embeddings[0].Vector.Length;
                var checkedWidth = returned == dimensions
                    ? Passed(model, operation)
                    : Failed(
                        model, operation, AiModelCheckReasons.Dimensions, null,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"The model returned a vector of {returned ?? 0} dimensions; the profile configures {dimensions}."),
                        "Enter the dimensions of the vectors that the model returns.");
                return checkedWidth with { ReturnedDimensions = returned };
            }

            using var client = driver.CreateChatClient(endpoint, model.ToProviderModel(), ProviderDeclaredProtocolModes.Auto);
            await client.GetResponseAsync(
                    [new ChatMessage(ChatRole.User, Prompt)],
                    new ChatOptions { MaxOutputTokens = MaximumOutputTokens },
                    deadline.Token)
                .ConfigureAwait(false);
            return Passed(model, operation);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failed(
                model, operation, AiModelCheckReasons.Unreachable, null,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The model did not answer within {limit.TotalSeconds:0} seconds."),
                "Retry later, or check the network path to the provider.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Describe(driver, model, operation, exception);
        }
    }

    /// <summary>
    ///     Describes a call that threw. The family reads the status off its own client's exception, so a refusal of
    ///     the provider is told apart from a call that got no answer.
    /// </summary>
    private static AiModelCheckOutcomeDto Describe(
        IAiProviderDriver driver,
        AiConfiguredModelDto model,
        AiOperationKind operation,
        Exception exception)
    {
        var verdict = driver.ClassifyRuntimeFailure(exception);
        var status = verdict.HttpStatus;
        var reason = status switch
        {
            401 => AiModelCheckReasons.Credentials,
            403 or 404 => AiModelCheckReasons.ModelUnavailable,
            429 => AiModelCheckReasons.RateLimited,
            >= 500 => AiModelCheckReasons.ProviderError,
            >= 400 => AiModelCheckReasons.Rejected,
            null when DriverCalls.IsUnanswered(exception, CancellationToken.None) => AiModelCheckReasons.Unreachable,
            _ => AiModelCheckReasons.Unknown,
        };
        var hint = reason switch
        {
            AiModelCheckReasons.ModelUnavailable =>
                "Check the model identifier, and that the credential may use the model.",
            AiModelCheckReasons.Unreachable => "Confirm the base URL, any required path prefix, and outbound connectivity.",
            _ => DriverFailureMapper.ActionHintFor(status) ??
                 "Review the provider-specific details and check the model again.",
        };
        return Failed(model, operation, reason, status, exception.Message, hint);
    }

    private static AiModelCheckOutcomeDto Passed(AiConfiguredModelDto model, AiOperationKind operation) =>
        new(model.Id, model.RemoteModelId, operation, true);

    private static AiModelCheckOutcomeDto Failed(
        AiConfiguredModelDto model,
        AiOperationKind operation,
        string reason,
        int? status,
        string summary,
        string hint) =>
        new(model.Id, model.RemoteModelId, operation, false, reason, status, summary, hint);
}
