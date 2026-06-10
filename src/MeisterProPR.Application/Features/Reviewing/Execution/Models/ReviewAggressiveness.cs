// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Controls how aggressively the review pipeline emits and retains findings.
///     Assertive emits uncertain findings (with confidence) and uses an LLM ranker to decide survival.
///     Calm uses deterministic filters and discards uncertain findings at the prompt level.
///     Balanced is the default middle ground.
/// </summary>
public enum ReviewAggressiveness
{
    Calm,
    Balanced,
    Assertive,
}
