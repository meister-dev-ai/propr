// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Strategies.Ports;

/// <summary>Resolves Reviewing pipeline profiles for an existing persisted strategy.</summary>
public interface IReviewPipelineProfileProvider
{
    /// <summary>Returns the registered profiles for one persisted strategy.</summary>
    IReadOnlyList<ReviewPipelineProfile> GetProfiles(ReviewStrategy strategy);
}
