// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     The closed vocabulary of review-pass lenses. A <see langword="null" /> lens is an ordinary resample pass; a
///     known lens value selects a specialist prompt/scope for the pass. Persistence stores the raw string, so a new
///     lens is added by declaring a constant here and teaching the runtime to honor it — no schema change required.
/// </summary>
public static class ReviewPassLens
{
    /// <summary>
    ///     Security-specialist discovery lens. A pass with this lens runs a security-specialist prompt and is scoped
    ///     to the files flagged by the deterministic security floor, regardless of the file's complexity tier.
    /// </summary>
    public const string Security = "security";

    /// <summary>All recognized lens values.</summary>
    public static IReadOnlyCollection<string> Known { get; } = [Security];

    /// <summary>
    ///     True when <paramref name="lens" /> is <see langword="null" /> (an ordinary pass) or a recognized lens value.
    /// </summary>
    public static bool IsValid(string? lens)
    {
        return lens is null || Known.Contains(lens);
    }
}
