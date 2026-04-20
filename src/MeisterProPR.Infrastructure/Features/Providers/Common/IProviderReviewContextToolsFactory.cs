// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Features.Providers.Common;

internal interface IProviderReviewContextToolsFactory
{
    ScmProvider Provider { get; }

    IReviewContextTools Create(ReviewContextToolsRequest request);
}
