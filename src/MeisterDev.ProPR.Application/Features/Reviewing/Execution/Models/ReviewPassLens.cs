// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

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

    /// <summary>
    ///     Knowledge-guided review lens. A pass with this lens screens the file against the embedded ProRV catalog
    ///     (CodeQL-derived per-language checks + GitHub-Actions attack classes), injects the applicable checks as
    ///     focused guidance, and runs on any tier when at least one check applies — a file the catalog finds nothing
    ///     for is skipped for that pass.
    /// </summary>
    public const string ProRV = "prorv";

    /// <summary>
    ///     Defect-inventory lens. A pass with this lens reviews the file's diff text alone — repository tools are
    ///     withheld and the pass completes in a single turn — and lists every plausible defect instead of only
    ///     high-confidence findings. The downstream union, dedup, verification, and ranking stages filter the
    ///     inventory, so the pass trades per-comment precision for candidate coverage. Runs on any tier for a text
    ///     file with a diff.
    /// </summary>
    public const string Inventory = "inventory";

    /// <summary>All recognized lens values.</summary>
    public static IReadOnlyCollection<string> Known { get; } = [Security, ProRV, Inventory];

    /// <summary>
    ///     True when <paramref name="lens" /> is <see langword="null" /> (an ordinary pass) or a recognized lens value.
    /// </summary>
    public static bool IsValid(string? lens)
    {
        return lens is null || Known.Contains(lens);
    }
}
