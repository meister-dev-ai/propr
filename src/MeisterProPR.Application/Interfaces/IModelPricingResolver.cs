// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Resolves the configured USD pricing for the model a review pass used, so the protocol recorder can
///     price token usage. Implementations are best-effort and return <see langword="null" /> when the
///     connection, model, or pricing cannot be resolved, leaving the caller to record an unpriced cost.
/// </summary>
public interface IModelPricingResolver
{
    /// <summary>
    ///     Resolves pricing for the model identified by <paramref name="modelId" /> under the AI connection
    ///     <paramref name="connectionId" />, falling back to the model bound to <paramref name="category" />'s
    ///     purpose when the model id does not match a configured model.
    /// </summary>
    /// <param name="connectionId">The AI connection the review pass used.</param>
    /// <param name="category">The effort-tier category of the pass, used to resolve a purpose binding when the model id does not match.</param>
    /// <param name="modelId">The effective model deployment name the pass reported.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The resolved pricing, or <see langword="null" /> when it cannot be determined.</returns>
    Task<ModelPricing?> ResolveAsync(
        Guid connectionId,
        AiConnectionModelCategory category,
        string modelId,
        CancellationToken ct);
}
