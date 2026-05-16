// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>Adds derived data to an existing Reviewing execution context without changing pipeline ownership.</summary>
/// <typeparam name="TContext">Execution context type.</typeparam>
public interface IReviewContextEnricher<TContext>
{
    /// <summary>Returns an enriched form of the provided context.</summary>
    Task<TContext> EnrichAsync(TContext context, CancellationToken cancellationToken);
}
