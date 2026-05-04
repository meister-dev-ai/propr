// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     Creates fixture-backed review tools scoped to one offline evaluation run.
/// </summary>
public sealed class FixtureReviewContextToolsFactory(
    IReviewEvaluationFixtureAccessor fixtureAccessor,
    IOptions<AiReviewOptions> options,
    IProCursorGateway proCursorGateway) : IReviewContextToolsFactory
{
    public IReviewContextTools Create(ReviewContextToolsRequest request)
    {
        var fixture = fixtureAccessor.Fixture ?? throw new InvalidOperationException("No review evaluation fixture is active for this scope.");
        return new FixtureReviewContextTools(fixture, options, proCursorGateway, request.ClientId, request.KnowledgeSourceIds);
    }
}
