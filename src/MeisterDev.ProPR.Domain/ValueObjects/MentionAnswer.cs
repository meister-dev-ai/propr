// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.ValueObjects;

/// <summary>
///     What one answered mention produced: the text to post, and what producing it spent.
/// </summary>
/// <remarks>
///     The usage travels with the text because the orchestrator, not the answer service, owns the recording.
///     Returning the text alone is what left mention spend unmetered: the provider reports the tokens on the
///     same response, and a signature that cannot carry them discards them at the return statement.
/// </remarks>
/// <param name="Text">The answer to post on the thread.</param>
/// <param name="Usage">Tokens the call reported, normalized. Flagged estimated when the provider reported none.</param>
/// <param name="ModelId">The model that produced the answer, for pricing what it spent.</param>
/// <param name="ConnectionId">The AI connection the tokens were bought through, or null when it is not known.</param>
/// <param name="LogicalModelName">The logical-model role that resolved the runtime, or null when none did.</param>
public sealed record MentionAnswer(
    string Text,
    AiTokenUsage Usage,
    string ModelId,
    Guid? ConnectionId = null,
    string? LogicalModelName = null);
