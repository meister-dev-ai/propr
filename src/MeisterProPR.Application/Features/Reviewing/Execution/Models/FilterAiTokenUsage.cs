// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Token-accounting metadata captured when a comment relevance filter performs AI work.
/// </summary>
public sealed record FilterAiTokenUsage(
    string ImplementationId,
    string FilePath,
    long InputTokens,
    long OutputTokens,
    AiConnectionModelCategory ModelCategory,
    string? ModelId);
