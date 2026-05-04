// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Offline execution request for one fixture-backed review run.
/// </summary>
public sealed record ReviewWorkflowRequest(
    ReviewJob Job,
    IChatClient ChatClient,
    string ModelId,
    ReviewEvaluationFixture? Fixture = null,
    EvaluationConfiguration? Configuration = null);
