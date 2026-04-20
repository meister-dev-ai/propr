// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Creates <see cref="IReviewContextTools" /> instances scoped to a single pull request review.
/// </summary>
public interface IReviewContextToolsFactory
{
    /// <summary>Creates a new <see cref="IReviewContextTools" /> instance for the specified normalized review context.</summary>
    IReviewContextTools Create(ReviewContextToolsRequest request);
}
